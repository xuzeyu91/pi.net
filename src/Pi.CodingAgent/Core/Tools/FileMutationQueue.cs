namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// Serializes file mutation operations targeting the same file. Port of
/// <c>core/tools/file-mutation-queue.ts</c>. Operations for different files still run in parallel.
/// </summary>
/// <remarks>
/// <para>
/// The TS keeps a <c>Map&lt;string, Promise&lt;void&gt;&gt;</c> of per-file promise chains plus a
/// serial <c>registrationQueue</c> that makes each registration (key lookup + chain append) atomic
/// with respect to the others. The port keeps the same shape; the dictionary and the registration
/// pointer are guarded by a lock because .NET callers are multi-threaded where the TS event loop
/// was single-threaded.
/// </para>
/// <para>
/// The queue key is the TS <c>realpath(resolve(filePath))</c>: <see cref="Path.GetFullPath(string)"/>
/// covers the normalization half and <see cref="File.ResolveLinkTarget(string, bool)"/> resolves a
/// final symlink. Node reports a missing path as <c>ENOENT</c> and falls back to the resolved path;
/// the .NET probe answers null instead of throwing, which lands on the same fallback.
/// </para>
/// </remarks>
public static class FileMutationQueue
{
    private static readonly Dictionary<string, Task> FileMutationQueues = new();
    private static readonly object Lock = new();
    private static Task _registrationQueue = Task.CompletedTask;

    /// <summary>The TS <c>withFileMutationQueue(filePath, fn)</c>.</summary>
    public static async Task<T> WithFileMutationQueueAsync<T>(string filePath, Func<Task<T>> fn)
    {
        Task<(string Key, Task CurrentQueue, Task ChainedQueue, Action ReleaseNext)> registration;
        lock (Lock)
        {
            registration = _registrationQueue
                .ContinueWith(_ => RegisterAsync(filePath), TaskScheduler.Default)
                .Unwrap();
            // Swallow registration failures so one bad key cannot break the chain for later callers
            // (the TS does the same with `then(() => undefined, () => undefined)`).
            _registrationQueue = registration.ContinueWith(static _ => { }, TaskScheduler.Default);
        }

        var (key, currentQueue, chainedQueue, releaseNext) = await registration.ConfigureAwait(false);
        await currentQueue.ConfigureAwait(false);
        try
        {
            return await fn().ConfigureAwait(false);
        }
        finally
        {
            releaseNext();
            lock (Lock)
            {
                if (FileMutationQueues.TryGetValue(key, out var queued) && ReferenceEquals(queued, chainedQueue))
                {
                    FileMutationQueues.Remove(key);
                }
            }
        }
    }

    private static async Task<(string Key, Task CurrentQueue, Task ChainedQueue, Action ReleaseNext)> RegisterAsync(
        string filePath)
    {
        var key = await GetMutationQueueKeyAsync(filePath).ConfigureAwait(false);
        Task currentQueue;
        var nextTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task chainedQueue;
        lock (Lock)
        {
            currentQueue = FileMutationQueues.TryGetValue(key, out var queued) ? queued : Task.CompletedTask;
            chainedQueue = currentQueue
                .ContinueWith(_ => nextTcs.Task, TaskScheduler.Default)
                .Unwrap();
            FileMutationQueues[key] = chainedQueue;
        }

        return (key, currentQueue, chainedQueue, () => nextTcs.SetResult());
    }

    private static Task<string> GetMutationQueueKeyAsync(string filePath)
    {
        var resolvedPath = Path.GetFullPath(filePath);
        try
        {
            var target = File.ResolveLinkTarget(resolvedPath, returnFinalTarget: true);
            return Task.FromResult(target?.FullName ?? resolvedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Node rethrows anything but ENOENT/ENOTDIR from realpath; the .NET probe has no such
            // error surface, so every probe failure falls back to the resolved path like Node's
            // missing-path case. The key only groups mutations, so this cannot change behavior.
            return Task.FromResult(resolvedPath);
        }
    }
}
