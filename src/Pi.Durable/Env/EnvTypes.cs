using Pi.Chord.Context;

namespace Pi.Durable.Env;

/// <summary>文件种类。对应 TS <c>FileKind</c>。</summary>
public enum FileKind
{
    File,
    Directory,
    Symlink,
}

/// <summary>文件操作错误码。对应 TS <c>FileErrorCode</c> 字符串联合。</summary>
public enum FileErrorCode
{
    Aborted,
    NotFound,
    PermissionDenied,
    NotDirectory,
    IsDirectory,
    Invalid,
    NotSupported,
    Unknown,
}

/// <summary>文件操作失败。对应 TS <c>FileError</c>。</summary>
public sealed class FileError : Exception
{
    public FileError(FileErrorCode code, string message, string? path = null, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
        Path = path;
    }

    /// <summary>TS 错误码字符串（"aborted" / "not_found" / …），供与 TS 语义对齐的判断使用。</summary>
    public string CodeText => Code switch
    {
        FileErrorCode.Aborted => "aborted",
        FileErrorCode.NotFound => "not_found",
        FileErrorCode.PermissionDenied => "permission_denied",
        FileErrorCode.NotDirectory => "not_directory",
        FileErrorCode.IsDirectory => "is_directory",
        FileErrorCode.Invalid => "invalid",
        FileErrorCode.NotSupported => "not_supported",
        _ => "unknown",
    };

    public FileErrorCode Code { get; }

    public string? Path { get; }
}

/// <summary>命令执行错误码。对应 TS <c>ExecutionErrorCode</c>。</summary>
public enum ExecutionErrorCode
{
    Aborted,
    Timeout,
    ShellUnavailable,
    SpawnError,
    CallbackError,
    Unknown,
}

/// <summary>命令执行失败。对应 TS <c>ExecutionError</c>。</summary>
public sealed class ExecutionError : Exception
{
    public ExecutionError(ExecutionErrorCode code, string message, Exception? cause = null)
        : base(message, cause)
    {
        Code = code;
    }

    /// <summary>超时或中止后已落盘溢出的完整输出文件。</summary>
    public string? SpillPath { get; internal set; }

    /// <summary>TS 错误码字符串。</summary>
    public string CodeText => Code switch
    {
        ExecutionErrorCode.Aborted => "aborted",
        ExecutionErrorCode.Timeout => "timeout",
        ExecutionErrorCode.ShellUnavailable => "shell_unavailable",
        ExecutionErrorCode.SpawnError => "spawn_error",
        ExecutionErrorCode.CallbackError => "callback_error",
        _ => "unknown",
    };

    public ExecutionErrorCode Code { get; }
}

/// <summary>文件或目录条目信息。对应 TS <c>FileInfo</c>。</summary>
public sealed record FileInfo(
    string Name,
    string Path,
    FileKind Kind,
    long Size,
    double MtimeMs);

/// <summary>一行文本及其换行终止状态。对应 TS <c>TextLine</c>。</summary>
public sealed record TextLine(string Text, bool Terminated);

/// <summary>严格 LF 行读取器。对应 TS <c>TextLineReader</c>。</summary>
public interface ITextLineReader
{
    Task<Result<TextLine?, FileError>> ReadLineAsync(Context context);

    Task CloseAsync(Context context);
}

/// <summary>
/// 对一个已打开常规文件的位置读；路径改名后所有调用看到的仍是同一文件。对应 TS <c>BinaryReader</c>。
/// </summary>
public interface IBinaryReader
{
    /// <summary>打开时的文件元数据（而非路径现在指向的东西）。</summary>
    Task<Result<FileInfo, FileError>> InfoAsync(Context context);

    /// <summary>最多 <paramref name="length"/> 字节；只有文件末尾才会更少。</summary>
    Task<Result<byte[], FileError>> ReadAsync(long offset, long length, Context context);

    /// <summary>
    /// 一趟扫描定位行 <c>[startLine, endLine)</c>（endLine 缺省：到文件末尾），0 基；行 k 始于第 k 个换行字节之后。
    /// 解码大小与整文件 <c>new TextDecoder().decode(file)</c> 一致，文件起始的字节序标记不计入。
    /// </summary>
    Task<Result<LineScan, FileError>> ScanLinesAsync(LineScanOptions options, Context context);

    Task CloseAsync(Context context);
}

/// <summary>行扫描选项。对应 TS <c>scanLines</c> 的 options。</summary>
public sealed record LineScanOptions(long StartLine, long? EndLine = null);

/// <summary>文件中各行的位置，由 <c>scanLines</c> 找到。对应 TS <c>LineScan</c>。</summary>
public sealed record LineScan(
    long Newlines,
    long Start,
    long End,
    long FirstLineEnd,
    long LastLineStart,
    long SelectedBytes,
    long FirstLineBytes);

/// <summary>要观察的文件或目录；可以不存在，创建它也是变更。对应 TS <c>WatchTarget</c>。</summary>
public sealed record WatchTarget
{
    public required string Path { get; init; }

    /// <summary>观察目录下的一切，而不只是它的直接条目。其下的符号链接不跟随。</summary>
    public bool Recursive { get; init; }

    /// <summary>既不观察也不上报的条目：以 <c>.</c> 开头的名字（hidden），或这些名字（names）。</summary>
    public WatchExclude? Exclude { get; init; }
}

public sealed record WatchExclude
{
    public bool Hidden { get; init; }
    public IReadOnlyCollection<string>? Names { get; init; }
}

/// <summary>
/// 变化内容：paths（这些路径及其子树可能变化；可能误报，观察者健康时不会漏报）、overflow（覆盖不确定过一段时间，
/// 需全量重扫）、error（观察者已停止，此后不再回调）。对应 TS <c>WatchChange</c>。
/// </summary>
public readonly record struct WatchChange
{
    public enum KindKind { Paths, Overflow, Error }

    public KindKind Kind { get; private init; }

    /// <summary>Paths 变体：可能变化的路径。</summary>
    public IReadOnlyList<string>? Paths { get; private init; }

    /// <summary>Error 变体：观察者因该错误停止。</summary>
    public FileError? Error { get; private init; }

    public static WatchChange PathsChanged(IReadOnlyList<string> paths) =>
        new() { Kind = KindKind.Paths, Paths = paths };

    public static WatchChange Overflow() => new() { Kind = KindKind.Overflow };

    public static WatchChange Errored(FileError error) => new() { Kind = KindKind.Error, Error = error };
}

/// <summary>文件观察者。对应 TS <c>FileWatcher</c>。</summary>
public interface IFileWatcher
{
    /// <summary>
    /// native：约两秒内上报变化。polling：环境比较快照（网络/FUSE 文件系统不可靠）；两次快照之间被撤销的变化可能漏掉。
    /// </summary>
    FileWatchMode Mode { get; }

    /// <summary>停止观察；此调用完成后不再发起 onChange。幂等。</summary>
    Task CloseAsync(Context context);
}

public enum FileWatchMode
{
    Native,
    Polling,
}

/// <summary>一个目录条目的分页读取器。对应 TS <c>DirReader</c>。</summary>
public interface IDirReader
{
    /// <summary>
    /// 按文件系统返回顺序最多取 <paramref name="maxEntries"/> 条，从上次停止处继续；done 标记结束。消失或不受支持的
    /// 条目被跳过。调用失败或中止后应关闭读取器。
    /// </summary>
    Task<Result<DirPage, FileError>> NextAsync(int maxEntries, Context context);

    Task CloseAsync(Context context);
}

public sealed record DirPage(IReadOnlyList<FileInfo> Entries, bool Done);

/// <summary>可移植文件系统能力；操作以失败值返回而不是抛出。对应 TS <c>FileSystem</c>。</summary>
public interface IFileSystem
{
    /// <summary>文件命名空间：相同 id 在相同路径看到相同文件，与 cwd 无关。</summary>
    string Id { get; }

    string Cwd { get; }

    Task<Result<string, FileError>> AbsolutePathAsync(string path, Context context);

    Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, Context context);

    Task<Result<string, FileError>> ReadTextFileAsync(string path, Context context);

    Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, Context context);

    Task<Result<IReadOnlyList<string>, FileError>> ReadTextLinesAsync(
        string path, ReadTextLinesOptions? options, Context context);

    Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, Context context);

    /// <summary>打开常规文件做有界位置读。目录以 is_directory 失败；noFollow 时末级符号链接以 invalid 失败。</summary>
    Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(
        string path, OpenBinaryOptions? options, Context context);

    Task<Result<Unit, FileError>> WriteFileAsync(string path, FileContent content, Context context);

    Task<Result<Unit, FileError>> AppendFileAsync(string path, FileContent content, Context context);

    /// <summary>把文件截断或扩展到正好 <paramref name="size"/> 字节。</summary>
    Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, Context context);

    /// <summary>冲刷文件内容与元数据，保证打开的句柄能取回它们。</summary>
    Task<Result<Unit, FileError>> FlushFileAsync(string path, Context context);

    Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, Context context);

    Task<Result<FileInfo, FileError>> FileInfoAsync(string path, Context context);

    Task<Result<IReadOnlyList<FileInfo>, FileError>> ListDirAsync(string path, Context context);

    Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, Context context);

    /// <summary>
    /// 上报文件与目录变化。返回的观察者存在即代表覆盖已建立：先观察再加载的宿主不会错过加载期间的变化。
    /// </summary>
    Task<Result<IFileWatcher, FileError>> WatchAsync(
        IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, Context context);

    Task<Result<string, FileError>> CanonicalPathAsync(string path, Context context);

    Task<Result<bool, FileError>> ExistsAsync(string path, Context context);

    Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, Context context);

    Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, Context context);

    Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, Context context);

    Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, Context context);

    Task CleanupAsync(Context context);
}

/// <summary>占位单元，对应 TS 的 <c>undefined</c> 成功值。</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}

/// <summary>文件内容：文本或字节。对应 TS <c>string | Uint8Array</c>。</summary>
public readonly struct FileContent
{
    private FileContent(string? text, byte[]? bytes)
    {
        Text = text;
        Bytes = bytes;
    }

    public string? Text { get; }
    public byte[]? Bytes { get; }

    public static FileContent FromText(string text) => new(text, null);

    public static FileContent FromBytes(byte[] bytes) => new(null, bytes);

    public static implicit operator FileContent(string text) => FromText(text);

    public static implicit operator FileContent(byte[] bytes) => FromBytes(bytes);

    public long ByteCount => Bytes is { } b ? b.Length : System.Text.Encoding.UTF8.GetByteCount(Text!);
}

public sealed record ReadTextLinesOptions
{
    public long? MaxLines { get; init; }
}

public sealed record OpenBinaryOptions
{
    /// <summary>末级路径组件为符号链接时以 invalid 失败而不跟随；更早的组件仍然解析。</summary>
    public bool NoFollow { get; init; }
}

public sealed record CreateDirOptions
{
    public bool Recursive { get; init; } = true;
}

public sealed record RemoveOptions
{
    public bool Recursive { get; init; }

    public bool Force { get; init; }
}

public sealed record TempFileOptions
{
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }
}

/// <summary>输出超过任一阈值后把完整输出溢出到临时文件。对应 TS <c>ShellSpillOptions</c>。</summary>
public sealed record ShellSpillOptions
{
    public required long AfterBytes { get; init; }

    /// <summary>完整或部分行数。</summary>
    public required long AfterLines { get; init; }
}

public sealed record ShellExecResult(long ExitCode, string? SpillPath = null);

public sealed record ShellExecOptions
{
    public string? Cwd { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public bool InheritEnv { get; init; } = true;

    /// <summary>超时秒数；TS 中以秒为单位的小数。</summary>
    public double? Timeout { get; init; }

    /// <summary>stdout 与 stderr 的每个解码块按到达顺序回调：原始、无界、不限速。两个流各自解码。</summary>
    public Action<string, Context, ShellOutputInfo>? OnOutput { get; init; }

    public ShellSpillOptions? Spill { get; init; }

    /// <summary>调用方只保留输出的这一尾部，环境因此可以省略尾部之外的部分并以 skipped 上报。</summary>
    public ShellOutputWindow? Window { get; init; }
}

/// <summary>调用方保留的合并输出尾部及采样节奏。对应 TS <c>ShellOutputWindow</c>。</summary>
public sealed record ShellOutputWindow
{
    /// <summary>输出末尾保留的解码文本 UTF-8 字节数。</summary>
    public required long MaxBytes { get; init; }

    /// <summary>输出末尾保留的行数。</summary>
    public required long MaxLines { get; init; }

    /// <summary>调用方两次采样之间的最小间隔。</summary>
    public required double MinIntervalMs { get; init; }

    /// <summary>每次采样还按此速率与采样大小成比例地暂停调用方。</summary>
    public required double BytesPerSecond { get; init; }
}

/// <summary>
/// 环境省略的输出，按 onOutput 本应收到的解码文本计量：每个 U+FFFD 计 3 字节，未净化。对应 TS <c>ShellOutputSkip</c>。
/// </summary>
public sealed record ShellOutputSkip(long Bytes, long Newlines, bool EndsWithNewline);

public sealed record ShellOutputInfo
{
    public required ShellOutputStream Stream { get; init; }

    /// <summary>此块之前被省略的输出，只在传了 window 时出现。省略文本加上该块必然超过窗口至少一字节或一行。</summary>
    public ShellOutputSkip? Skipped { get; init; }
}

public enum ShellOutputStream
{
    Stdout,
    Stderr,
}

/// <summary>命令执行能力。对应 TS <c>Shell</c>。</summary>
public interface IShell
{
    /// <summary>
    /// 运行命令。字符串走环境 shell；数组直接运行第一个元素，不经 shell 解析。中止或超时只杀死本命令的进程。
    /// </summary>
    Task<Result<ShellExecResult, ExecutionError>> ExecAsync(
        ShellCommand command, ShellExecOptions? options, Context context);

    /// <summary>杀死本环境还在运行的所有命令；用于宿主关闭，而非单个请求。</summary>
    Task CleanupAsync(Context context);
}

/// <summary>命令：单串走 shell，或多元素 argv 直接运行。对应 TS <c>string | readonly string[]</c>。</summary>
public sealed class ShellCommand
{
    private ShellCommand(string? line, IReadOnlyList<string>? argv)
    {
        Line = line;
        Argv = argv;
    }

    /// <summary>字符串形式（走 shell）；argv 形式时为 null。</summary>
    public string? Line { get; }

    /// <summary>argv 形式；字符串形式时为 null。</summary>
    public IReadOnlyList<string>? Argv { get; }

    public bool IsString => Line is not null;

    public static ShellCommand FromString(string line) => new(line, null);

    public static ShellCommand FromArgv(params string[] args) => new(null, args);

    public static ShellCommand FromArgv(IReadOnlyList<string> args) => new(null, args);

    public static implicit operator ShellCommand(string line) => FromString(line);

    public static implicit operator ShellCommand(string[] args) => FromArgv(args);
}

/// <summary>执行环境 = 文件系统 + shell。对应 TS <c>ExecutionEnv</c>。</summary>
public interface IExecutionEnv : IFileSystem, IShell
{
}
