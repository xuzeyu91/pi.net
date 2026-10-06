using System.Security.Cryptography;
using Pi.Chord.Context;

namespace Pi.Durable.Env;

/// <summary>
/// 以快照方式观察：原生事件只触发去抖重扫，变化是快照差分加上事件路径。被替换的文件、被改名或重建的祖先、连同内容
/// 一起创建的目录因此从不依赖操作系统恰好发了哪些事件。新目录在再次扫描之前先装上观察者，观察者出现之前写进去的
/// 内容不会错过。对应 TS <c>env/node-watch.ts</c> 的 <c>NodeFileWatcher</c>。
/// </summary>
public sealed class SnapshotFileWatcher : IFileWatcher
{
    private const int DebounceMs = 50;
    /// <summary>
    /// macOS 上 fs.watch 在 libuv 的 FSEvents 流生效之前就返回，期间的变化永不报告；装好观察者后隔这么久重扫一次兜住。
    /// </summary>
    private const int FseventsSettleMs = 500;
    private const int DefaultPollMs = 2000;
    private const int DefaultMaxDirectories = 10_000;
    /// <summary>轮询模式下最近修改过的小文件还按内容比较：文件系统时间戳粒度内的第二次写入可能保住 size 与 mtime。</summary>
    private const int HashMaxBytes = 256 * 1024;
    private const int HashRecentMs = 5000;

    /// <summary>观察行为选项。对应 TS <c>NodeWatchOptions</c>。</summary>
    public sealed record Options
    {
        public FileWatchMode? Mode { get; init; }
        public int? PollIntervalMs { get; init; }
        public int? MaxDirectories { get; init; }
    }

    private sealed record ResolvedTarget(
        string Path, bool Recursive, bool Hidden, IReadOnlySet<string> Names);

    /// <summary>快照对一个路径记住的内容。目录按身份比较（本移植以 kind + mtime 近似 TS 的 dev/ino，见差异记录）。</summary>
    private sealed record Entry(FileStat.EntryKind Kind, long Size, double MtimeMs, string? Hash);

    private sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException(string message) : base(message) { }
    }

    private readonly IReadOnlyList<ResolvedTarget> _targets;
    private readonly Action<WatchChange> _onChange;
    private readonly int _pollIntervalMs;
    private readonly int _maxDirectories;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private FileWatchMode _mode;
    private Dictionary<string, Entry> _snapshot = new(StringComparer.Ordinal);
    private Timer? _timer;
    private Timer? _settleTimer;
    private Task? _running;
    private bool _dirty;
    private bool _closed;

    private SnapshotFileWatcher(
        IReadOnlyList<ResolvedTarget> targets, Action<WatchChange> onChange, FileWatchMode mode, Options options)
    {
        _targets = targets;
        _onChange = onChange;
        _mode = mode;
        _pollIntervalMs = options.PollIntervalMs ?? DefaultPollMs;
        _maxDirectories = options.MaxDirectories ?? DefaultMaxDirectories;
    }

    /// <summary>建立覆盖：先装观察者，再取之后用来比较的快照。</summary>
    public static async Task<SnapshotFileWatcher> OpenAsync(
        IReadOnlyList<WatchTarget> targets,
        Func<string, string> resolvePath,
        Action<WatchChange> onChange,
        Options options)
    {
        var resolved = targets.Select(target => new ResolvedTarget(
            resolvePath(target.Path),
            target.Recursive,
            target.Exclude?.Hidden == true,
            new HashSet<string>(target.Exclude?.Names ?? Array.Empty<string>(), StringComparer.Ordinal))).ToList();
        // Windows 拒绝在下面有任何打开目录时改名，而原生观察者占住每个被观察目录：观察会破坏父目录改名，所以轮询。
        var mode = options.Mode
            ?? (OperatingSystem.IsWindows() || await AnyUnreliableAsync(resolved.Select(t => t.Path)).ConfigureAwait(false)
                ? FileWatchMode.Polling
                : FileWatchMode.Native);
        var watcher = new SnapshotFileWatcher(resolved, onChange, mode, options);
        try
        {
            await watcher.SyncAsync(report: false).ConfigureAwait(false);
        }
        catch (BudgetExceededException error)
        {
            watcher.Stop();
            throw new FileError(FileErrorCode.Invalid, error.Message);
        }
        catch (FileError)
        {
            watcher.Stop();
            throw;
        }
        catch (Exception error)
        {
            watcher.Stop();
            throw new FileError(FileErrorCode.Invalid, error.Message);
        }
        watcher.SchedulePoll();
        return watcher;
    }

    public FileWatchMode Mode => _mode;

    public Task CloseAsync(Context context)
    {
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            Stop();
        }
        return WaitRunningAsync();
    }

    private async Task WaitRunningAsync()
    {
        Task? running;
        lock (_gate) running = _running;
        if (running is not null) await running.ConfigureAwait(false);
    }

    private void Stop()
    {
        lock (_gate)
        {
            _closed = true;
            _timer?.Dispose();
            _timer = null;
            _settleTimer?.Dispose();
            _settleTimer = null;
        }
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }

    private void Deliver(WatchChange change)
    {
        lock (_gate)
        {
            if (_closed) return;
        }
        try
        {
            _onChange(change);
        }
        catch
        {
            // 抛异常的回调不能让观察停止。
        }
    }

    private void SchedulePoll()
    {
        lock (_gate)
        {
            if (_closed || _mode != FileWatchMode.Polling) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = Task.Run(async () =>
            {
                await FlushAsync().ConfigureAwait(false);
                SchedulePoll();
            }), null, _pollIntervalMs, Timeout.Infinite);
        }
    }

    private void ScheduleFlush()
    {
        lock (_gate)
        {
            if (_closed || _timer is not null) return;
            _timer = new Timer(_ =>
            {
                lock (_gate) _timer = null;
                _ = FlushAsync();
            }, null, DebounceMs, Timeout.Infinite);
        }
    }

    private void ScheduleSettle()
    {
        lock (_gate)
        {
            if (_closed) return;
            _settleTimer?.Dispose();
            _settleTimer = new Timer(_ =>
            {
                lock (_gate) _settleTimer = null;
                _ = FlushAsync();
            }, null, FseventsSettleMs, Timeout.Infinite);
        }
    }

    private async Task FlushAsync()
    {
        Task? existing;
        lock (_gate)
        {
            if (_running is not null)
            {
                _dirty = true;
                existing = _running;
            }
            else
            {
                existing = null;
                _running = RunFlushAsync();
            }
        }
        if (existing is not null)
        {
            await existing.ConfigureAwait(false);
            return;
        }
        Task? running;
        lock (_gate) running = _running;
        if (running is not null) await running.ConfigureAwait(false);
    }

    private async Task RunFlushAsync()
    {
        try
        {
            while (true)
            {
                bool closed;
                HashSet<string> events;
                lock (_gate)
                {
                    _dirty = false;
                    closed = _closed;
                    events = new HashSet<string>(_events, StringComparer.Ordinal);
                    _events.Clear();
                }
                if (closed) break;
                var changed = await SyncAsync(report: true).ConfigureAwait(false);
                changed.UnionWith(events);
                if (changed.Count > 0)
                {
                    var sorted = changed.OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    Deliver(WatchChange.PathsChanged(sorted));
                }
                bool dirty;
                lock (_gate) dirty = _dirty && !_closed;
                if (!dirty) break;
            }
        }
        catch (Exception error)
        {
            var fileError = error as FileError
                ?? new FileError(FileErrorCode.Invalid, error.Message);
            Deliver(WatchChange.Errored(fileError));
            Stop();
        }
        finally
        {
            lock (_gate) _running = null;
        }
    }

    /// <summary>重扫、报告差异、为新目录装观察者，直到没有新的为止。</summary>
    private async Task<HashSet<string>> SyncAsync(bool report)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < 10; round++)
        {
            bool closed;
            lock (_gate) closed = _closed;
            if (closed) break;
            var next = await ScanAsync().ConfigureAwait(false);
            if (report)
                foreach (var path in Diff(_snapshot, next))
                    changed.Add(path);
            lock (_gate) _snapshot = next;
            if (_mode == FileWatchMode.Polling || !ReconcileWatchers(next)) break;
            if (OperatingSystem.IsMacOS()) ScheduleSettle();
            // 在新目录的观察者出现之前写入它的内容出现在下一轮。
            report = true;
        }
        return changed;
    }

    private string[] Diff(Dictionary<string, Entry> previous, Dictionary<string, Entry> next)
    {
        var changed = new List<string>();
        foreach (var (path, entry) in next)
        {
            if (!previous.TryGetValue(path, out var before) || !SameEntry(before, entry)) changed.Add(Reported(path));
        }
        foreach (var path in previous.Keys)
            if (!next.ContainsKey(path)) changed.Add(Reported(path));
        return changed.ToArray();
    }

    private static bool SameEntry(Entry a, Entry b) =>
        a.Kind == b.Kind && a.Size == b.Size && a.MtimeMs == b.MtimeMs && a.Hash == b.Hash;

    /// <summary>改变身份的祖先把它下面的每个目标都移走了；报告那些目标。</summary>
    private string Reported(string path)
    {
        if (_targets.Any(target => IsWithin(path, target.Path))) return path;
        return _targets.FirstOrDefault(target => IsWithin(target.Path, path))?.Path ?? path;
    }

    private async Task<Dictionary<string, Entry>> ScanAsync()
    {
        var snapshot = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var directories = 0;
        void CountDirectory()
        {
            if (++directories > _maxDirectories)
            {
                throw new BudgetExceededException($"Watched paths exceed {_maxDirectories} directories");
            }
        }
        async Task RecordAsync(string path, FileStat.Entry stats)
        {
            string? hash = null;
            if (_mode == FileWatchMode.Polling
                && stats.Kind == FileStat.EntryKind.File
                && stats.Size <= HashMaxBytes
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)stats.MtimeMs < HashRecentMs)
            {
                try
                {
                    var content = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                }
                catch
                {
                    hash = null;
                }
            }
            snapshot[path] = new Entry(stats.Kind, stats.Size, stats.MtimeMs, hash);
        }
        async Task ScanDirectoryAsync(ResolvedTarget target, string directory)
        {
            string[] names;
            try
            {
                names = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (
                error is DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var path in names)
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                if (Excluded(target, name)) continue;
                if (snapshot.ContainsKey(path)) continue;
                var stats = FileStat.Lstat(path);
                if (stats is null) continue;
                await RecordAsync(path, stats.Value).ConfigureAwait(false);
                if (target.Recursive && stats.Value.Kind == FileStat.EntryKind.Directory)
                {
                    CountDirectory();
                    await ScanDirectoryAsync(target, path).ConfigureAwait(false);
                }
            }
        }
        foreach (var target in _targets)
        {
            foreach (var ancestor in AncestorsOf(target.Path))
            {
                if (snapshot.ContainsKey(ancestor)) continue;
                var stats = FileStat.Lstat(ancestor);
                // 只按身份：祖先自身的时间戳随每个无关兄弟变化。
                if (stats is not null)
                {
                    snapshot[ancestor] = new Entry(stats.Value.Kind, 0, 0, null);
                }
            }
            // 目标本身可能是符号链接，指向被观察的东西；跟随它。
            var targetStats = FileStat.Follow(target.Path);
            if (targetStats is null) continue;
            await RecordAsync(target.Path, targetStats.Value).ConfigureAwait(false);
            if (targetStats.Value.Kind == FileStat.EntryKind.Directory)
            {
                CountDirectory();
                await ScanDirectoryAsync(target, target.Path).ConfigureAwait(false);
            }
        }
        return snapshot;
    }

    /// <summary>
    /// 观察每个目标的现有祖先、每个目标目录；Linux 上递归目标下的每个目录单独观察（其他平台每个目标一个递归观察者）。
    /// 返回是否新装了观察者。
    /// </summary>
    private bool ReconcileWatchers(Dictionary<string, Entry> snapshot)
    {
        var wanted = new Dictionary<string, bool>(StringComparer.Ordinal);
        var perDirectory = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
        foreach (var target in _targets)
        {
            foreach (var ancestor in AncestorsOf(target.Path))
            {
                if (snapshot.TryGetValue(ancestor, out var entry) && entry.Kind == FileStat.EntryKind.Directory
                    && !wanted.ContainsKey(ancestor))
                {
                    wanted[ancestor] = false;
                }
            }
            if (!snapshot.TryGetValue(target.Path, out var targetEntry)
                || targetEntry.Kind != FileStat.EntryKind.Directory) continue;
            var recursiveRoot = target.Recursive && !perDirectory;
            wanted[target.Path] = recursiveRoot;
            if (target.Recursive && perDirectory)
            {
                foreach (var (path, entry) in snapshot)
                {
                    if (entry.Kind == FileStat.EntryKind.Directory && path != target.Path
                        && IsWithin(path, target.Path))
                    {
                        wanted[path] = false;
                    }
                }
            }
        }
        foreach (var (path, watcher) in _watchers)
        {
            if (!wanted.ContainsKey(path))
            {
                watcher.Dispose();
                _watchers.Remove(path);
            }
        }
        var added = false;
        foreach (var (path, recursive) in wanted)
        {
            if (_watchers.ContainsKey(path)) continue;
            try
            {
                var fsw = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = recursive,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                        | NotifyFilters.Size | NotifyFilters.Attributes,
                };
                fsw.Created += (_, e) => OnEvent(path, e.Name);
                fsw.Changed += (_, e) => OnEvent(path, e.Name);
                fsw.Deleted += (_, e) => OnEvent(path, e.Name);
                fsw.Renamed += (_, e) =>
                {
                    OnEvent(path, e.OldName);
                    OnEvent(path, e.Name);
                };
                // 消失或出错的被观察目录：重扫（同时替换观察者）。
                fsw.Error += (_, _) =>
                {
                    fsw.Dispose();
                    if (_watchers.TryGetValue(path, out var current) && ReferenceEquals(current, fsw))
                        _watchers.Remove(path);
                    ScheduleFlush();
                };
                fsw.EnableRaisingEvents = true;
                _watchers[path] = fsw;
                added = true;
            }
            catch (Exception error) when (
                error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
                    or ArgumentException)
            {
                // ENOENT/EACCES/EPERM 等价：跳过该目录。
            }
            catch (Exception)
            {
                // 观察数超限或不支持：改用快照比较，并声明覆盖不确定。
                SwitchToPolling();
                return false;
            }
        }
        return added;
    }

    private void SwitchToPolling()
    {
        if (_mode == FileWatchMode.Polling) return;
        _mode = FileWatchMode.Polling;
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        Deliver(WatchChange.Overflow());
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        SchedulePoll();
    }

    private void OnEvent(string directory, string? filename)
    {
        lock (_gate)
        {
            if (_closed) return;
            var path = filename is null
                ? directory
                : $"{(directory.EndsWith(System.IO.Path.DirectorySeparatorChar) ? directory : directory + System.IO.Path.DirectorySeparatorChar)}{filename}";
            // 关于祖先无关兄弟或被排除条目的事件被忽略。
            var relevant = InScope(path);
            if (relevant) _events.Add(Reported(path));
            if (relevant || filename is null) ScheduleFlush();
        }
    }

    private bool InScope(string path)
    {
        foreach (var target in _targets)
        {
            if (IsWithin(target.Path, path)) return true;
            if (!IsWithin(path, target.Path) || path == target.Path) continue;
            var relative = System.IO.Path.GetRelativePath(target.Path, path);
            var components = relative.Split(System.IO.Path.DirectorySeparatorChar);
            if (!target.Recursive && components.Length > 1) continue;
            if (components.Any(name => Excluded(target, name))) continue;
            return true;
        }
        return false;
    }

    // ---------- 路径助手 ----------

    /// <summary>path 的祖先，从最近的开始，直到根。</summary>
    private static IEnumerable<string> AncestorsOf(string path)
    {
        var current = System.IO.Path.GetDirectoryName(path);
        var root = System.IO.Path.GetPathRoot(path) ?? "";
        while (current is not null)
        {
            yield return current;
            if (current == root || System.IO.Path.GetDirectoryName(current) is not { } next || next == current) break;
            current = next;
        }
    }

    /// <summary>path 是否是 ancestor 或在它之下。</summary>
    private static bool IsWithin(string path, string ancestor)
    {
        if (path == ancestor) return true;
        var prefix = ancestor.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? ancestor
            : ancestor + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static bool Excluded(ResolvedTarget target, string name) =>
        (target.Hidden && name.StartsWith(".")) || target.Names.Contains(name);

    /// <summary>是否有路径（或其最近存在的祖先）位于不报告远端变化的文件系统上。</summary>
    private static async Task<bool> AnyUnreliableAsync(IEnumerable<string> paths)
    {
        // TS 用 Linux statfs 的 magic number 判定；.NET 无可移植 statfs，这里读取 /proc/self/mounts 的
        // 文件系统类型名做等价判定（仅 Linux；Windows/macOS 直接返回 false，与 TS 一致）。
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return false;
        var mounts = await ReadMountsAsync().ConfigureAwait(false);
        if (mounts.Count == 0) return false;
        foreach (var path in paths)
        {
            foreach (var candidate in new[] { path }.Concat(AncestorsOf(path)))
            {
                var match = mounts
                    .Where(kv => IsWithin(candidate, kv.Key) || kv.Key == "/")
                    .OrderByDescending(kv => kv.Key.Length)
                    .FirstOrDefault();
                if (match.Key is null) continue;
                if (UnreliableTypes.Contains(match.Value)) return true;
                break;
            }
        }
        return false;
    }

    private static readonly HashSet<string> UnreliableTypes = new(StringComparer.Ordinal)
    {
        // 与 TS UNRELIABLE_FILE_SYSTEMS 的 magic number 一一对应：NFS / SMB / CIFS / SMB2 / FUSE / 9P /
        // Lustre / GPFS / Ceph / OpenAFS / kAFS / sdcardfs。
        "nfs", "nfs4", "smbfs", "cifs", "fuse", "fuse.sshfs", "fuse.rclone", "9p", "lustre", "gpfs",
        "ceph", "ceph-fuse", "afs", "kafs", "sdcardfs",
    };

    private static async Task<Dictionary<string, string>> ReadMountsAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/self/mounts").ConfigureAwait(false);
            foreach (var line in lines)
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;
                var mountPoint = Uri.UnescapeDataString(parts[1].Replace("\\040", " "));
                result[mountPoint] = parts[2];
            }
        }
        catch
        {
            // 没有 /proc（非 Linux）或读取失败：视为不可判定。
        }
        return result;
    }
}
