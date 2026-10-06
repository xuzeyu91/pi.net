using System.Diagnostics;
using System.Text;
using Pi.Chord.Context;

namespace Pi.Durable.Env;

/// <summary>
/// 本机执行环境（文件系统 + shell）。对应 TS <c>env/node.ts</c> 的 <c>NodeExecutionEnv</c>：同一台机器上的所有本地
/// 环境看到同样的文件；每个容器或远程主机有自己的 id。
/// </summary>
public sealed class LocalExecutionEnv : IExecutionEnv
{
    private const int MaxTimeoutMs = 2_147_483_647;
    private const double MaxTimeoutSeconds = MaxTimeoutMs / 1000.0;
    private const long SpillHighWaterMark = 1024 * 1024;
    /// <summary>BinaryReader 的单次最大读取量，让巨大的 length 只按文件实际产出分配。</summary>
    private const long BinaryReadChunk = 1024 * 1024;
    private const int LineReadChunkSize = 64 * 1024;

    /// <summary>TS 的写入不携带字节序标记，.NET 的 Encoding.UTF8 属性会；统一用无 BOM 实例。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _shellPath;
    private readonly IReadOnlyDictionary<string, string>? _shellEnv;
    private readonly WatchOptions _watchOptions;
    private readonly object _activePidsGate = new();
    private readonly HashSet<int> _activeChildPids = new();

    public LocalExecutionEnv(LocalEnvOptions options)
    {
        Cwd = options.Cwd;
        _shellPath = options.ShellPath;
        _shellEnv = options.ShellEnv;
        _watchOptions = options.Watch ?? new WatchOptions();
    }

    /// <summary>观察行为选项。对应 TS <c>NodeWatchOptions</c>。</summary>
    public sealed record WatchOptions
    {
        /// <summary>强制模式。缺省在 Windows（原生观察者会占住目录句柄）与不报告远端变化的文件系统上轮询。</summary>
        public FileWatchMode? Mode { get; init; }

        /// <summary>轮询模式的快照间隔；缺省 2000 ms。</summary>
        public int? PollIntervalMs { get; init; }

        /// <summary>单个观察者覆盖的目录上限；缺省 10,000。</summary>
        public int? MaxDirectories { get; init; }
    }

    public sealed record LocalEnvOptions
    {
        public required string Cwd { get; init; }
        public string? ShellPath { get; init; }
        public IReadOnlyDictionary<string, string>? ShellEnv { get; init; }
        public WatchOptions? Watch { get; init; }
    }

    /// <summary>每个本地环境看到同样的文件。</summary>
    public string Id => "dotnet:local";

    public string Cwd { get; }

    // ---------- 路径与元数据助手 ----------

    internal static string ResolvePath(string cwd, string path)
    {
        var normalized = path;
        if (normalized == "~")
        {
            normalized = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (normalized.StartsWith("~/") || (OperatingSystem.IsWindows() && normalized.StartsWith("~\\")))
        {
            normalized = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                normalized[2..]);
        }
        else if (normalized.StartsWith("file://"))
        {
            try
            {
                normalized = new Uri(normalized).LocalPath;
            }
            catch (UriFormatException)
            {
                // 畸形 URL 保留为普通路径，让文件系统方法保持不抛出的约定。
            }
        }
        return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Join(cwd, normalized));
    }

    private static FileKind? KindFrom(FileStat.Entry entry) => entry.Kind switch
    {
        FileStat.EntryKind.File => FileKind.File,
        FileStat.EntryKind.Directory => FileKind.Directory,
        FileStat.EntryKind.Symlink => FileKind.Symlink,
        _ => null,
    };

    private static Result<FileInfo, FileError> FileInfoFrom(string path, FileStat.Entry entry)
    {
        var kind = KindFrom(entry);
        if (kind is not { } k) return Result<FileInfo, FileError>.Err(new FileError(FileErrorCode.Invalid, "Unsupported file type", path));
        return Result<FileInfo, FileError>.Ok(new FileInfo(System.IO.Path.GetFileName(path), path, k, entry.Size, entry.MtimeMs));
    }

    private static FileError ToFileError(Exception error, string? fallbackPath = null)
    {
        if (error is FileError fe) return fe;
        var path = fallbackPath;
        switch (error)
        {
            case OperationCanceledException:
                return new FileError(FileErrorCode.Aborted, "aborted", path, error);
            case FileNotFoundException:
            case DirectoryNotFoundException:
                return new FileError(FileErrorCode.NotFound, error.Message, path, error);
            case UnauthorizedAccessException:
                return new FileError(FileErrorCode.PermissionDenied, error.Message, path, error);
            case IOException io:
                // Windows Win32 错误码经 IOException 的 HResult 传达。
                return io.HResult switch
                {
                    2 /* ERROR_FILE_NOT_FOUND */ or 3 /* ERROR_PATH_NOT_FOUND */ =>
                        new FileError(FileErrorCode.NotFound, io.Message, path, io),
                    5 /* ERROR_ACCESS_DENIED */ =>
                        new FileError(FileErrorCode.PermissionDenied, io.Message, path, io),
                    267 /* ERROR_DIRECTORY */ =>
                        new FileError(FileErrorCode.NotDirectory, io.Message, path, io),
                    _ => new FileError(FileErrorCode.Unknown, io.Message, path, io),
                };
            case ArgumentException:
                return new FileError(FileErrorCode.Invalid, error.Message, path, error);
            default:
                return new FileError(FileErrorCode.Unknown, error.Message, path, error);
        }
    }

    private static Result<T, FileError>? AbortResult<T>(Context context, string? path = null)
    {
        var signal = context.AbortSignal;
        return signal is { IsCancellationRequested: true }
            ? Result<T, FileError>.Err(new FileError(FileErrorCode.Aborted, "aborted", path))
            : null;
    }

    // ---------- 文本行读取 ----------

    /// <summary>严格 LF 读取器；报告最后一行是否以换行终止。</summary>
    private sealed class LocalTextLineReader : ITextLineReader
    {
        private readonly FileStream _file;
        private readonly string _path;
        private readonly StreamDecoder _decoder = new();
        private readonly byte[] _chunk = new byte[LineReadChunkSize];
        private long _byteOffset;
        private string _buffered = "";
        private bool _ended;
        private bool _closed;

        public LocalTextLineReader(FileStream file, string path)
        {
            _file = file;
            _path = path;
        }

        public async Task<Result<TextLine?, FileError>> ReadLineAsync(Context context)
        {
            if (AbortResult<TextLine?>(context, _path) is { } aborted) return aborted;
            if (_closed) return Result<TextLine?, FileError>.Err(
                new FileError(FileErrorCode.Invalid, "Text line reader is closed", _path));
            try
            {
                while (true)
                {
                    var newline = _buffered.IndexOf('\n');
                    if (newline != -1)
                    {
                        var text = _buffered[..newline];
                        _buffered = _buffered[(newline + 1)..];
                        return Result<TextLine?, FileError>.Ok(new TextLine(text, Terminated: true));
                    }
                    if (_ended)
                    {
                        if (_buffered.Length == 0) return Result<TextLine?, FileError>.Ok(null);
                        var tail = _buffered;
                        _buffered = "";
                        return Result<TextLine?, FileError>.Ok(new TextLine(tail, Terminated: false));
                    }

                    // 显式位置让被中止的读可以重试而不跳过字节。
                    _file.Seek(_byteOffset, SeekOrigin.Begin);
                    var bytesRead = await _file.ReadAsync(_chunk.AsMemory(0, _chunk.Length), context.AbortSignal ?? default)
                        .ConfigureAwait(false);
                    if (AbortResult<TextLine?>(context, _path) is { } afterReadAbort) return afterReadAbort;
                    _byteOffset += bytesRead;
                    if (bytesRead == 0)
                    {
                        _buffered += _decoder.Decode(null);
                        _ended = true;
                    }
                    else
                    {
                        _buffered += _decoder.Decode(_chunk.AsSpan(0, bytesRead).ToArray());
                    }
                }
            }
            catch (Exception error)
            {
                return Result<TextLine?, FileError>.Err(ToFileError(error, _path));
            }
        }

        public async Task CloseAsync(Context context)
        {
            if (_closed) return;
            _closed = true;
            _buffered = "";
            try
            {
                await _file.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 关闭是尽力而为，包括取消或早前 I/O 失败之后。
            }
        }
    }

    private static Result<T, FileError> ClosedResult<T>(string what, string path) =>
        Result<T, FileError>.Err(new FileError(FileErrorCode.Invalid, $"{what} is closed", path));

    // ---------- 二进制读取 ----------

    private sealed class LocalBinaryReader : IBinaryReader
    {
        private readonly FileStream _file;
        private readonly string _path;
        private bool _closed;

        public LocalBinaryReader(FileStream file, string path)
        {
            _file = file;
            _path = path;
        }

        public Task<Result<FileInfo, FileError>> InfoAsync(Context context)
        {
            if (AbortResult<FileInfo>(context, _path) is { } aborted) return Task.FromResult(aborted);
            if (_closed) return Task.FromResult(ClosedResult<FileInfo>("Binary reader", _path));
            try
            {
                var entry = FileStat.Lstat(_path);
                return Task.FromResult(entry is null
                    ? Result<FileInfo, FileError>.Err(new FileError(FileErrorCode.Unknown, "stat failed", _path))
                    : FileInfoFrom(_path, entry.Value));
            }
            catch (Exception error)
            {
                return Task.FromResult(Result<FileInfo, FileError>.Err(ToFileError(error, _path)));
            }
        }

        public async Task<Result<byte[], FileError>> ReadAsync(long offset, long length, Context context)
        {
            if (AbortResult<byte[]>(context, _path) is { } aborted) return aborted;
            if (_closed) return ClosedResult<byte[]>("Binary reader", _path);
            if (offset < 0 || length < 0)
            {
                return Result<byte[], FileError>.Err(
                    new FileError(FileErrorCode.Invalid, "Offset and length must be non-negative safe integers", _path));
            }
            var chunks = new List<byte[]>();
            long total = 0;
            try
            {
                while (total < length)
                {
                    var size = (int)Math.Min(length - total, BinaryReadChunk);
                    var chunk = new byte[size];
                    _file.Seek(offset + total, SeekOrigin.Begin);
                    var bytesRead = await _file.ReadAsync(chunk.AsMemory(0, size), context.AbortSignal ?? default)
                        .ConfigureAwait(false);
                    if (AbortResult<byte[]>(context, _path) is { } afterReadAbort) return afterReadAbort;
                    if (bytesRead == 0) break;
                    chunks.Add(bytesRead == size ? chunk : chunk[..bytesRead]);
                    total += bytesRead;
                }
            }
            catch (Exception error)
            {
                return Result<byte[], FileError>.Err(ToFileError(error, _path));
            }
            if (chunks.Count == 1) return Result<byte[], FileError>.Ok(chunks[0]);
            var bytes = new byte[total];
            var position = 0;
            foreach (var chunk in chunks)
            {
                Array.Copy(chunk, 0, bytes, position, chunk.Length);
                position += chunk.Length;
            }
            return Result<byte[], FileError>.Ok(bytes);
        }

        public async Task<Result<LineScan, FileError>> ScanLinesAsync(LineScanOptions options, Context context)
        {
            if (AbortResult<LineScan>(context, _path) is { } aborted) return aborted;
            if (_closed) return ClosedResult<LineScan>("Binary reader", _path);
            LineScanner scanner;
            try
            {
                scanner = new LineScanner(options.StartLine, options.EndLine);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Result<LineScan, FileError>.Err(
                    new FileError(FileErrorCode.Invalid, "Invalid line range", _path));
            }
            var chunk = new byte[LineReadChunkSize];
            try
            {
                for (long position = 0; ;)
                {
                    _file.Seek(position, SeekOrigin.Begin);
                    var bytesRead = await _file.ReadAsync(chunk.AsMemory(), context.AbortSignal ?? default)
                        .ConfigureAwait(false);
                    if (AbortResult<LineScan>(context, _path) is { } afterReadAbort) return afterReadAbort;
                    if (bytesRead == 0) return Result<LineScan, FileError>.Ok(scanner.Finish());
                    scanner.Push(chunk.AsSpan(0, bytesRead));
                    position += bytesRead;
                }
            }
            catch (Exception error)
            {
                return Result<LineScan, FileError>.Err(ToFileError(error, _path));
            }
        }

        public async Task CloseAsync(Context context)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                await _file.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 尽力而为。
            }
        }
    }

    // ---------- 目录读取 ----------

    private sealed class LocalDirReader : IDirReader
    {
        private readonly IEnumerator<string> _entries;
        private readonly string _path;
        private bool _done;
        private bool _closed;

        private LocalDirReader(IEnumerator<string> entries, string path)
        {
            _entries = entries;
            _path = path;
        }

        public static Result<IDirReader, FileError> Open(string path)
        {
            try
            {
                var enumerator = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                return Result<IDirReader, FileError>.Ok(new LocalDirReader(enumerator, path));
            }
            catch (Exception error)
            {
                return Result<IDirReader, FileError>.Err(ToFileError(error, path));
            }
        }

        public Task<Result<DirPage, FileError>> NextAsync(int maxEntries, Context context)
        {
            if (AbortResult<DirPage>(context, _path) is { } aborted) return Task.FromResult(aborted);
            if (_closed) return Task.FromResult(ClosedResult<DirPage>("Directory reader", _path));
            if (maxEntries <= 0)
            {
                return Task.FromResult(Result<DirPage, FileError>.Err(
                    new FileError(FileErrorCode.Invalid, "maxEntries must be a positive safe integer", _path)));
            }
            var entries = new List<FileInfo>();
            try
            {
                while (!_done && entries.Count < maxEntries)
                {
                    if (AbortResult<DirPage>(context, _path) is { } loopAbort) return Task.FromResult(loopAbort);
                    string name;
                    try
                    {
                        if (!_entries.MoveNext())
                        {
                            _done = true;
                            break;
                        }
                        name = _entries.Current;
                    }
                    catch (Exception error)
                    {
                        return Task.FromResult(Result<DirPage, FileError>.Err(ToFileError(error, _path)));
                    }
                    var entryPath = Path.GetFullPath(name); // 枚举返回完整路径
                    var entry = FileStat.Lstat(entryPath);
                    if (entry is null) continue; // 枚举与读取元数据之间被移除：不再属于列表。
                    var info = FileInfoFrom(entryPath, entry.Value);
                    if (info.IsOk) entries.Add(info.Value);
                }
                return Task.FromResult(Result<DirPage, FileError>.Ok(new DirPage(entries, _done)));
            }
            catch (Exception error)
            {
                return Task.FromResult(Result<DirPage, FileError>.Err(ToFileError(error, _path)));
            }
        }

        public Task CloseAsync(Context context)
        {
            if (_closed) return Task.CompletedTask;
            _closed = true;
            _entries.Dispose();
            return Task.CompletedTask;
        }
    }

    // ---------- Shell 发现与配置 ----------

    private sealed record ShellConfig(string Shell, IReadOnlyList<string> Args, bool CommandTransportStdin = false);

    private static bool IsLegacyWslBashPath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(
            normalized, @"^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe$");
    }

    private static ShellConfig GetBashShellConfig(string shell) => IsLegacyWslBashPath(shell)
        ? new ShellConfig(shell, new[] { "-s" }, CommandTransportStdin: true)
        : new ShellConfig(shell, new[] { "-c" });

    private static async Task<bool> PathExistsAsync(string path) =>
        await Task.Run(() => FileStat.Lstat(path) is not null).ConfigureAwait(false);

    private static async Task<(string Stdout, int? Status)> RunCommandAsync(string command, string[] args, int timeoutMs)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            var winner = await Task.WhenAny(exitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (winner != exitTask)
            {
                TryKillTree(process.Id);
                await exitTask.ConfigureAwait(false);
                return ("", null);
            }
            return (await stdoutTask.ConfigureAwait(false), process.ExitCode);
        }
        catch
        {
            return ("", null);
        }
    }

    private static async Task<string?> FindBashOnPathAsync()
    {
        var (stdout, status) = OperatingSystem.IsWindows()
            ? await RunCommandAsync("where.exe", new[] { "bash.exe" }, 5000).ConfigureAwait(false)
            : await RunCommandAsync("which", new[] { "bash" }, 5000).ConfigureAwait(false);
        if (status != 0 || stdout.Length == 0) return null;
        var firstMatch = stdout.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
        return firstMatch.Length > 0 && await PathExistsAsync(firstMatch).ConfigureAwait(false) ? firstMatch : null;
    }

    private static async Task<Result<ShellConfig, ExecutionError>> GetShellConfigAsync(string? customShellPath)
    {
        if (customShellPath is not null)
        {
            if (await PathExistsAsync(customShellPath).ConfigureAwait(false))
                return Result<ShellConfig, ExecutionError>.Ok(GetBashShellConfig(customShellPath));
            return Result<ShellConfig, ExecutionError>.Err(
                new ExecutionError(ExecutionErrorCode.ShellUnavailable,
                    $"Custom shell path not found: {customShellPath}"));
        }
        if (OperatingSystem.IsWindows())
        {
            var candidates = new List<string>();
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (programFiles is not null) candidates.Add(Path.Join(programFiles, "Git", "bin", "bash.exe"));
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (programFilesX86 is not null) candidates.Add(Path.Join(programFilesX86, "Git", "bin", "bash.exe"));
            foreach (var candidate in candidates)
            {
                if (await PathExistsAsync(candidate).ConfigureAwait(false))
                    return Result<ShellConfig, ExecutionError>.Ok(GetBashShellConfig(candidate));
            }
            var bashOnPath = await FindBashOnPathAsync().ConfigureAwait(false);
            if (bashOnPath is not null)
                return Result<ShellConfig, ExecutionError>.Ok(GetBashShellConfig(bashOnPath));
            return Result<ShellConfig, ExecutionError>.Err(new ExecutionError(
                ExecutionErrorCode.ShellUnavailable,
                "No bash shell found. Options:\n" +
                "  1. Install Git for Windows: https://git-scm.com/download/win\n" +
                "  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
                "  3. Configure an explicit ShellPath\n\n" +
                $"Searched Git Bash in:\n{string.Join("\n", candidates.Select(p => $"  {p}"))}"));
        }

        if (await PathExistsAsync("/bin/bash").ConfigureAwait(false))
            return Result<ShellConfig, ExecutionError>.Ok(GetBashShellConfig("/bin/bash"));
        var onPath = await FindBashOnPathAsync().ConfigureAwait(false);
        if (onPath is not null)
            return Result<ShellConfig, ExecutionError>.Ok(GetBashShellConfig(onPath));
        return Result<ShellConfig, ExecutionError>.Ok(new ShellConfig("sh", new[] { "-c" }));
    }

    // ---------- 观察者 ----------

    public async Task<Result<IFileWatcher, FileError>> WatchAsync(
        IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, Context context)
    {
        if (AbortResult<IFileWatcher>(context) is { } aborted) return aborted;
        try
        {
            var watcher = await SnapshotFileWatcher.OpenAsync(
                targets,
                path => ResolvePath(Cwd, path),
                onChange,
                new SnapshotFileWatcher.Options
                {
                    Mode = _watchOptions.Mode,
                    PollIntervalMs = _watchOptions.PollIntervalMs,
                    MaxDirectories = _watchOptions.MaxDirectories,
                }).ConfigureAwait(false);
            if (AbortResult<IFileWatcher>(context) is { } afterOpenAbort)
            {
                await watcher.CloseAsync(context).ConfigureAwait(false);
                return afterOpenAbort;
            }
            return Result<IFileWatcher, FileError>.Ok(watcher);
        }
        catch (Exception error)
        {
            return Result<IFileWatcher, FileError>.Err(ToFileError(error));
        }
    }

    // ---------- 文件系统 ----------

    public Task<Result<string, FileError>> AbsolutePathAsync(string path, Context context) =>
        Task.FromResult(Result<string, FileError>.Ok(ResolvePath(Cwd, path)));

    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, Context context)
    {
        var joined = parts.Aggregate("", Path.Join);
        return Task.FromResult(Result<string, FileError>.Ok(joined));
    }

    public async Task<Result<string, FileError>> ReadTextFileAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<string>(context, resolved) is { } aborted) return aborted;
        try
        {
            var bytes = await File.ReadAllBytesAsync(resolved, context.AbortSignal ?? default).ConfigureAwait(false);
            return Result<string, FileError>.Ok(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<ITextLineReader>(context, resolved) is { } aborted) return aborted;
        FileStream? file = null;
        try
        {
            file = new FileStream(resolved, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });
            if (AbortResult<ITextLineReader>(context, resolved) is { } afterOpenAbort)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                return afterOpenAbort;
            }
            return Result<ITextLineReader, FileError>.Ok(new LocalTextLineReader(file, resolved));
        }
        catch (Exception error)
        {
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            return Result<ITextLineReader, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path, ReadTextLinesOptions? options, Context context)
    {
        if (options?.MaxLines is { } max && max <= 0)
            return Result<IReadOnlyList<string>, FileError>.Ok(Array.Empty<string>());
        var opened = await OpenTextLineReaderAsync(path, context).ConfigureAwait(false);
        if (!opened.IsOk) return Result<IReadOnlyList<string>, FileError>.Err(opened.Error);
        var lines = new List<string>();
        try
        {
            while (options?.MaxLines is not { } limit || lines.Count < limit)
            {
                var line = await opened.Value.ReadLineAsync(context).ConfigureAwait(false);
                if (!line.IsOk) return Result<IReadOnlyList<string>, FileError>.Err(line.Error);
                if (line.Value is null) break;
                lines.Add(line.Value.Text);
            }
            return Result<IReadOnlyList<string>, FileError>.Ok(lines);
        }
        finally
        {
            await opened.Value.CloseAsync(context).ConfigureAwait(false);
        }
    }

    public async Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<byte[]>(context, resolved) is { } aborted) return aborted;
        try
        {
            var bytes = await File.ReadAllBytesAsync(resolved, context.AbortSignal ?? default).ConfigureAwait(false);
            return Result<byte[], FileError>.Ok(bytes);
        }
        catch (Exception error)
        {
            return Result<byte[], FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(
        string path, OpenBinaryOptions? options, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<IBinaryReader>(context, resolved) is { } aborted) return aborted;
        var noFollow = options?.NoFollow == true;
        FileStream? file = null;
        try
        {
            // Windows 没有 O_NOFOLLOW；打开前检查末级组件（不防竞态）。Unix 同样用打开前检查（见移植差异）。
            if (noFollow)
            {
                var entry = FileStat.Lstat(resolved);
                if (entry is { Kind: FileStat.EntryKind.Symlink }) return Result<IBinaryReader, FileError>.Err(
                    SymlinkRefused(resolved));
            }
            file = new FileStream(resolved, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });
            var statEntry = FileStat.Follow(resolved);
            if (statEntry is { Kind: not FileStat.EntryKind.File })
            {
                await file.DisposeAsync().ConfigureAwait(false);
                file = null;
                return Result<IBinaryReader, FileError>.Err(statEntry.Value.Kind == FileStat.EntryKind.Directory
                    ? new FileError(FileErrorCode.IsDirectory,
                        "EISDIR: illegal operation on a directory, read", resolved)
                    : new FileError(FileErrorCode.Invalid, "Not a regular file", resolved));
            }
            if (AbortResult<IBinaryReader>(context, resolved) is { } afterOpenAbort)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                return afterOpenAbort;
            }
            return Result<IBinaryReader, FileError>.Ok(new LocalBinaryReader(file, resolved));
        }
        catch (Exception error)
        {
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            return Result<IBinaryReader, FileError>.Err(ToFileError(error, resolved));
        }
    }

    private static FileError SymlinkRefused(string path, Exception? cause = null) =>
        new(FileErrorCode.Invalid, "Refusing to follow a symbolic link", path, cause);

    private static void EnsureParentDir(string resolved)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    public async Task<Result<Unit, FileError>> WriteFileAsync(string path, FileContent content, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        try
        {
            EnsureParentDir(resolved);
            if (AbortResult<Unit>(context, resolved) is { } afterMkdirAbort) return afterMkdirAbort;
            if (content.Bytes is { } bytes)
                await File.WriteAllBytesAsync(resolved, bytes, context.AbortSignal ?? default).ConfigureAwait(false);
            else
                await File.WriteAllTextAsync(resolved, content.Text ?? "", Utf8NoBom,
                    context.AbortSignal ?? default).ConfigureAwait(false);
            return Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<Unit, FileError>> AppendFileAsync(string path, FileContent content, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        try
        {
            EnsureParentDir(resolved);
            if (AbortResult<Unit>(context, resolved) is { } afterMkdirAbort) return afterMkdirAbort;
            await using (var stream = new FileStream(resolved, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
                var bytes = content.Bytes ?? Encoding.UTF8.GetBytes(content.Text ?? "");
                await stream.WriteAsync(bytes.AsMemory(), context.AbortSignal ?? default).ConfigureAwait(false);
            }
            return AbortResult<Unit>(context, resolved) ?? Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        if (size < 0)
        {
            return Result<Unit, FileError>.Err(
                new FileError(FileErrorCode.Invalid, "File size must be a non-negative safe integer", resolved));
        }
        try
        {
            await using (var stream = new FileStream(resolved, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(size);
            }
            return AbortResult<Unit>(context, resolved) ?? Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<Unit, FileError>> FlushFileAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        try
        {
            await using (var stream = new FileStream(resolved, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                await stream.FlushAsync(context.AbortSignal ?? default).ConfigureAwait(false);
            }
            return AbortResult<Unit>(context, resolved) ?? Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, Context context)
    {
        var source = ResolvePath(Cwd, sourcePath);
        var destination = ResolvePath(Cwd, destinationPath);
        if (AbortResult<Unit>(context, destination) is { } aborted) return aborted;
        try
        {
            File.Move(source, destination);
            return Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, source));
        }
    }

    public async Task<Result<FileInfo, FileError>> FileInfoAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<FileInfo>(context, resolved) is { } aborted) return aborted;
        try
        {
            var entry = FileStat.Lstat(resolved);
            if (entry is null)
            {
                return Result<FileInfo, FileError>.Err(
                    new FileError(FileErrorCode.NotFound, $"ENOENT: no such file or directory, lstat '{resolved}'",
                        resolved));
            }
            return FileInfoFrom(resolved, entry.Value);
        }
        catch (Exception error)
        {
            return Result<FileInfo, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<IReadOnlyList<FileInfo>, FileError>> ListDirAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<IReadOnlyList<FileInfo>>(context, resolved) is { } aborted) return aborted;
        try
        {
            var names = Directory.EnumerateFileSystemEntries(resolved);
            var infos = new List<FileInfo>();
            foreach (var name in names)
            {
                if (AbortResult<IReadOnlyList<FileInfo>>(context, resolved) is { } loopAbort) return loopAbort;
                // EnumerateFileSystemEntries 返回完整路径，直接使用。
                var entryPath = Path.GetFullPath(name);
                var entry = FileStat.Lstat(entryPath);
                if (entry is null) return Result<IReadOnlyList<FileInfo>, FileError>.Err(
                    new FileError(FileErrorCode.NotFound, $"ENOENT: no such file or directory, lstat '{entryPath}'",
                        entryPath));
                var info = FileInfoFrom(entryPath, entry.Value);
                if (info.IsOk) infos.Add(info.Value);
            }
            return Result<IReadOnlyList<FileInfo>, FileError>.Ok(infos);
        }
        catch (Exception error)
        {
            return Result<IReadOnlyList<FileInfo>, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<IDirReader>(context, resolved) is { } aborted) return Task.FromResult(aborted);
        return Task.FromResult(LocalDirReader.Open(resolved));
    }

    public async Task<Result<string, FileError>> CanonicalPathAsync(string path, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<string>(context, resolved) is { } aborted) return aborted;
        try
        {
            return Result<string, FileError>.Ok(
                await Task.Run(() => Path.GetFullPath(resolved)).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<bool, FileError>> ExistsAsync(string path, Context context)
    {
        var result = await FileInfoAsync(path, context).ConfigureAwait(false);
        if (result.IsOk) return Result<bool, FileError>.Ok(true);
        if (result.Error.Code == FileErrorCode.NotFound) return Result<bool, FileError>.Ok(false);
        return Result<bool, FileError>.Err(result.Error);
    }

    public async Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        try
        {
            if (options?.Recursive ?? true)
            {
                Directory.CreateDirectory(resolved);
            }
            else
            {
                Directory.CreateDirectory(resolved); // .NET 总是递归；非递归语义用存在性检查近似
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved)));
                if (parent is not null && !Directory.Exists(parent))
                {
                    throw new DirectoryNotFoundException($"ENOENT: no such file or directory, mkdir '{resolved}'");
                }
            }
            return Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, Context context)
    {
        var resolved = ResolvePath(Cwd, path);
        if (AbortResult<Unit>(context, resolved) is { } aborted) return aborted;
        try
        {
            var entry = FileStat.Lstat(resolved);
            if (entry is null && options?.Force != true)
            {
                return Result<Unit, FileError>.Err(
                    new FileError(FileErrorCode.NotFound, $"ENOENT: no such file or directory, rm '{resolved}'",
                        resolved));
            }
            if (entry is null) return Result<Unit, FileError>.Ok(Unit.Value);
            if (entry.Value.Kind == FileStat.EntryKind.Directory)
            {
                if (options?.Recursive == true) Directory.Delete(resolved, recursive: true);
                else Directory.Delete(resolved);
            }
            else
            {
                File.Delete(resolved);
            }
            return Result<Unit, FileError>.Ok(Unit.Value);
        }
        catch (Exception error)
        {
            return Result<Unit, FileError>.Err(ToFileError(error, resolved));
        }
    }

    public async Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, Context context)
    {
        if (AbortResult<string>(context) is { } aborted) return aborted;
        try
        {
            var dir = Path.Join(Path.GetTempPath(), (prefix ?? "tmp-") + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return Result<string, FileError>.Ok(dir);
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(ToFileError(error));
        }
    }

    public async Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, Context context)
    {
        var dir = await CreateTempDirAsync("tmp-", context).ConfigureAwait(false);
        if (!dir.IsOk) return dir;
        var filePath = Path.Join(dir.Value,
            $"{options?.Prefix ?? ""}{Guid.NewGuid():N}{options?.Suffix ?? ""}");
        try
        {
            await File.WriteAllTextAsync(filePath, "").ConfigureAwait(false);
            return Result<string, FileError>.Ok(filePath);
        }
        catch (Exception error)
        {
            return Result<string, FileError>.Err(ToFileError(error, filePath));
        }
    }

    public Task CleanupAsync(Context context)
    {
        int[] pids;
        lock (_activePidsGate)
        {
            pids = _activeChildPids.ToArray();
            _activeChildPids.Clear();
        }
        foreach (var pid in pids) TryKillTree(pid);
        return Task.CompletedTask;
    }

    // ---------- Shell 执行 ----------

    private static Result<long?, ExecutionError> ResolveTimeoutMs(double? timeout)
    {
        if (timeout is not { } seconds) return Result<long?, ExecutionError>.Ok(null);
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return Result<long?, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Timeout,
                "Invalid timeout: must be a finite number of seconds"));
        }
        var timeoutMs = seconds * 1000;
        if (timeoutMs > MaxTimeoutMs)
        {
            return Result<long?, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Timeout,
                $"Invalid timeout: maximum is {MaxTimeoutSeconds} seconds"));
        }
        return Result<long?, ExecutionError>.Ok((long)timeoutMs);
    }

    private static void TryKillTree(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Join(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows",
                        "System32", "taskkill.exe"),
                    ArgumentList = { "/F", "/T", "/PID", pid.ToString() },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                _ = killer;
            }
            else
            {
                var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        catch
        {
            // 进程已死或不可杀：忽略。
        }
    }

    public async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        ShellCommand command, ShellExecOptions? options, Context context)
    {
        var signal = context.AbortSignal;
        if (signal is { IsCancellationRequested: true })
            return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(ExecutionErrorCode.Aborted, "aborted"));
        var timeoutMsResult = ResolveTimeoutMs(options?.Timeout);
        if (!timeoutMsResult.IsOk) return Result<ShellExecResult, ExecutionError>.Err(timeoutMsResult.Error);
        var timeoutMs = timeoutMsResult.Value;

        var cwd = options?.Cwd is { } optionCwd ? ResolvePath(Cwd, optionCwd) : Cwd;
        // 字符串走 shell；argv 数组直接运行其程序，不经 shell 解析。
        string program;
        IReadOnlyList<string> args;
        string? stdinCommand;
        if (command.IsString)
        {
            var shellConfig = await GetShellConfigAsync(_shellPath).ConfigureAwait(false);
            if (!shellConfig.IsOk) return Result<ShellExecResult, ExecutionError>.Err(shellConfig.Error);
            var config = shellConfig.Value;
            program = config.Shell;
            args = config.CommandTransportStdin ? config.Args : config.Args.Append(command.Line!).ToArray();
            stdinCommand = config.CommandTransportStdin ? command.Line : null;
        }
        else
        {
            var argv = command.Argv!;
            if (argv.Count == 0)
            {
                return Result<ShellExecResult, ExecutionError>.Err(
                    new ExecutionError(ExecutionErrorCode.SpawnError, "Empty argv: no program to run"));
            }
            program = argv[0];
            args = argv.Skip(1).ToArray();
            stdinCommand = null;
        }
        if (!Directory.Exists(cwd))
        {
            return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(
                ExecutionErrorCode.SpawnError,
                $"Working directory does not exist: {cwd}\nCannot execute bash commands."));
        }

        var process = new Process();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdinCommand is not null,
            };
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            var extraEnv = options?.Env;
            if (options is { InheritEnv: false })
            {
                startInfo.EnvironmentVariables.Clear();
                if (extraEnv is not null)
                    foreach (var (key, value) in extraEnv) startInfo.EnvironmentVariables[key] = value;
            }
            else
            {
                // ProcessStartInfo.EnvironmentVariables 缺省继承当前进程环境；叠加 ShellEnv 与 extraEnv。
                if (_shellEnv is not null)
                    foreach (var (key, value) in _shellEnv) startInfo.EnvironmentVariables[key] = value;
                if (extraEnv is not null)
                    foreach (var (key, value) in extraEnv) startInfo.EnvironmentVariables[key] = value;
            }
            process.StartInfo = startInfo;
            process.Start();
        }
        catch (Exception error)
        {
            process.Dispose();
            return Result<ShellExecResult, ExecutionError>.Err(new ExecutionError(
                ExecutionErrorCode.SpawnError, error.Message, error));
        }

        lock (_activePidsGate) _activeChildPids.Add(process.Id);

        var completion = new TaskCompletionSource<Result<ShellExecResult, ExecutionError>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;
        var timedOut = false;
        ExecutionError? callbackError = null;
        ExecutionError? spillError = null;
        var gate = new object();
        string? spillPath = null;
        FileStream? spillStream = null;
        Task? spillStart = null;
        var spillQueue = new List<byte[]>();
        var spillPrefix = new List<byte[]>();
        long seenBytes = 0;
        long seenNewlines = 0;

        void Settle(Result<ShellExecResult, ExecutionError> result)
        {
            lock (gate)
            {
                if (settled) return;
                settled = true;
            }
            lock (_activePidsGate) _activeChildPids.Remove(process.Id);
            completion.TrySetResult(result);
        }

        void KillThisTree()
        {
            try
            {
                TryKillTree(process.Id);
            }
            catch
            {
                // 已退出。
            }
        }

        void FailCallback(Exception error)
        {
            lock (gate)
            {
                if (callbackError is not null) return;
                callbackError = new ExecutionError(ExecutionErrorCode.CallbackError, error.Message, error);
            }
            KillThisTree();
        }

        void FailSpill(Exception error)
        {
            lock (gate)
            {
                if (spillError is not null) return;
                spillError = new ExecutionError(ExecutionErrorCode.Unknown,
                    $"Failed to preserve complete shell output: {error.Message}", error);
            }
            KillThisTree();
        }

        void StartSpill(byte[] chunk)
        {
            lock (gate)
            {
                if (spillStream is not null)
                {
                    WriteSpillLocked(chunk);
                    return;
                }
                spillQueue.Add(chunk);
                if (spillStart is not null) return;
                spillStart = Task.Run(async () =>
                {
                    try
                    {
                        var created = await CreateTempFileAsync(
                            new TempFileOptions { Prefix = "pi-output-", Suffix = ".log" }, context).ConfigureAwait(false);
                        if (!created.IsOk) throw created.Error;
                        spillPath = created.Value;
                        var stream = new FileStream(spillPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                            64 * 1024, FileOptions.None);
                        lock (gate)
                        {
                            if (spillStream is null) spillStream = stream;
                            foreach (var queued in spillQueue) WriteSpillLocked(queued);
                            spillQueue.Clear();
                        }
                    }
                    catch (Exception error)
                    {
                        FailSpill(error);
                    }
                });
            }
        }

        void WriteSpillLocked(byte[] chunk)
        {
            if (spillStream is null || chunk.Length == 0) return;
            try
            {
                spillStream.Write(chunk, 0, chunk.Length);
            }
            catch (Exception error)
            {
                FailSpill(error);
            }
        }

        async Task FinishSpillAsync()
        {
            Task? start;
            lock (gate) start = spillStart;
            if (start is not null) await start.ConfigureAwait(false);
            FileStream? stream;
            lock (gate) stream = spillStream;
            if (stream is null) return;
            try
            {
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // 尽力而为。
            }
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        // 每个流一个解码器，让一个流内跨块的字符在交错中存活。
        var stdoutDecoder = new StreamDecoder();
        var stderrDecoder = new StreamDecoder();

        void Emit(string text, ShellOutputStream stream)
        {
            lock (gate)
            {
                if (settled || text.Length == 0 || options?.OnOutput is null || callbackError is not null) return;
            }
            try
            {
                options!.OnOutput!(text, context, new ShellOutputInfo { Stream = stream });
            }
            catch (Exception error)
            {
                FailCallback(error);
            }
        }

        void Feed(byte[] chunk, ShellOutputStream source, StreamDecoder decoder)
        {
            Emit(decoder.Decode(chunk), source);
            var spill = options?.Spill;
            if (spill is null || chunk.Length == 0) return;
            lock (gate)
            {
                if (spillStart is not null)
                {
                    StartSpill(chunk);
                    return;
                }
                seenBytes += chunk.Length;
                foreach (var b in chunk)
                    if (b == 0x0a) seenNewlines++;
                var lines = seenNewlines + (chunk[^1] == 0x0a ? 0 : 1);
                if (seenBytes <= spill.AfterBytes && lines <= spill.AfterLines)
                {
                    spillPrefix.Add(chunk);
                    return;
                }
                foreach (var prefix in spillPrefix) StartSpill(prefix);
                spillPrefix.Clear();
                StartSpill(chunk);
            }
        }

        async Task PumpAsync(Stream raw, ShellOutputStream source, StreamDecoder decoder)
        {
            var buffer = new byte[LineReadChunkSize];
            try
            {
                int read;
                while ((read = await raw.ReadAsync(buffer.AsMemory(), context.AbortSignal ?? default).ConfigureAwait(false)) > 0)
                {
                    Feed(buffer.AsSpan(0, read).ToArray(), source, decoder);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // 中止杀死进程后的管道破裂属预期；残余文本在收尾时冲刷。
            }
        }

        // 中止 / 超时只杀死本命令的进程。
        if (timeoutMs is { } timeoutValue)
        {
            var timer = new Timer(_ =>
            {
                timedOut = true;
                KillThisTree();
            }, null, timeoutValue, Timeout.Infinite);
            _ = completion.Task.ContinueWith(_ => timer.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        CancellationTokenRegistration? registration = null;
        if (signal is { } abortSignal)
        {
            if (abortSignal.IsCancellationRequested) KillThisTree();
            else registration = abortSignal.Register(KillThisTree);
        }

        var stdoutPump = PumpAsync(process.StandardOutput.BaseStream, ShellOutputStream.Stdout, stdoutDecoder);
        var stderrPump = PumpAsync(process.StandardError.BaseStream, ShellOutputStream.Stderr, stderrDecoder);

        _ = Task.Run(async () =>
        {
            try
            {
                // 不带 token：中止路径会杀死进程，WaitForExit 随之正常完成，避免 OCE 竞态误报 spawn_error。
                await process.WaitForExitAsync().ConfigureAwait(false);
                // 退出后给 stdio 一个宽限期（例如持住 stdio 的后代进程）；溢出未排空则继续等。
                var pumps = Task.WhenAll(stdoutPump, stderrPump);
                while (!pumps.IsCompleted)
                {
                    await Task.WhenAny(pumps, Task.Delay(100)).ConfigureAwait(false);
                    bool spillDraining;
                    lock (gate) spillDraining = spillError is null && spillStart is not null && spillStream is null;
                    if (pumps.IsCompleted || !spillDraining) break;
                }
                await FinishSpillAsync().ConfigureAwait(false);
                Emit(stdoutDecoder.Decode(null), ShellOutputStream.Stdout);
                Emit(stderrDecoder.Decode(null), ShellOutputStream.Stderr);
                ExecutionError? callbackFailure;
                lock (gate) callbackFailure = callbackError;
                if (callbackFailure is not null)
                {
                    Settle(Result<ShellExecResult, ExecutionError>.Err(callbackFailure));
                    return;
                }
                if (timedOut)
                {
                    var timeout = new ExecutionError(ExecutionErrorCode.Timeout, $"timeout:{options?.Timeout}");
                    lock (gate) timeout.SpillPath = spillPath;
                    Settle(Result<ShellExecResult, ExecutionError>.Err(timeout));
                    return;
                }
                if (signal is { IsCancellationRequested: true })
                {
                    var abortedError = new ExecutionError(ExecutionErrorCode.Aborted, "aborted");
                    lock (gate) abortedError.SpillPath = spillPath;
                    Settle(Result<ShellExecResult, ExecutionError>.Err(abortedError));
                    return;
                }
                ExecutionError? spillFailure;
                lock (gate) spillFailure = spillError;
                if (spillFailure is not null)
                {
                    Settle(Result<ShellExecResult, ExecutionError>.Err(spillFailure));
                    return;
                }
                var exitCode = process.ExitCode;
                Settle(Result<ShellExecResult, ExecutionError>.Ok(
                    new ShellExecResult(exitCode, spillPath)));
            }
            catch (Exception error)
            {
                Settle(Result<ShellExecResult, ExecutionError>.Err(
                    new ExecutionError(ExecutionErrorCode.SpawnError, error.Message, error)));
            }
            finally
            {
                registration?.Dispose();
                try { process.Dispose(); } catch { /* 忽略 */ }
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }
}
