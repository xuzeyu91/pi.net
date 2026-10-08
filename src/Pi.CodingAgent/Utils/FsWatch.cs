namespace Pi.CodingAgent.Utils;

/// <summary>A filesystem watcher (port of the <c>FSWatcher</c> handle from <c>utils/fs-watch.ts</c>).</summary>
/// <remarks>
/// Node's <c>fs.watch</c> reports <c>(eventType, filename)</c> with <c>eventType</c> either
/// <c>"rename"</c> (create, delete, rename) or <c>"change"</c> (content modified). .NET splits those
/// across <see cref="FileSystemWatcher.Created"/>, <see cref="FileSystemWatcher.Deleted"/>,
/// <see cref="FileSystemWatcher.Renamed"/> and <see cref="FileSystemWatcher.Changed"/>, which this
/// wrapper folds back into the two Node event types.
/// </remarks>
public sealed class FsWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private int _disposed;

    internal FsWatcher(FileSystemWatcher watcher)
    {
        _watcher = watcher;
    }

    /// <summary>Stop watching. Errors are swallowed, matching <c>closeWatcher</c>.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // Ignore watcher close errors.
        }
    }
}

/// <summary>Port of <c>utils/fs-watch.ts</c>.</summary>
public static class FsWatch
{
    /// <summary>Delay before retrying a failed watch.</summary>
    public const int RetryDelayMs = 5000;

    /// <summary>Close a watcher, ignoring errors.</summary>
    public static void CloseWatcher(FsWatcher? watcher) => watcher?.Dispose();

    /// <summary>
    /// Watch <paramref name="path"/> (a file or a directory). A failure to start raises
    /// <paramref name="onError"/> and returns <see langword="null"/>; a later watcher error raises it too.
    /// </summary>
    public static FsWatcher? WatchWithErrorHandler(
        string path,
        Action<string, string?> listener,
        Action onError)
    {
        try
        {
            var isDirectory = Directory.Exists(path);
            var directory = isDirectory ? path : NodePath.Dirname(path);
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
                Filter = isDirectory ? string.Empty : NodePath.Basename(path),
            };

            watcher.Created += (_, e) => listener("rename", e.Name);
            watcher.Deleted += (_, e) => listener("rename", e.Name);
            watcher.Renamed += (_, e) => listener("rename", e.Name);
            watcher.Changed += (_, e) => listener("change", e.Name);
            watcher.Error += (_, _) => onError();

            watcher.EnableRaisingEvents = true;
            return new FsWatcher(watcher);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            onError();
            return null;
        }
    }
}
