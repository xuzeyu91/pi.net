using System.Collections.Concurrent;
using Pi.Chord.Context;
using Pi.Durable.Env;

namespace Pi.Durable.Tools;

/// <summary>
/// 把同一文件的 <c>edit</c> / <c>write</c> 变更在本进程内串行化。对应 TS <c>tools/file-mutation-queue.ts</c>。
/// </summary>
/// <remarks>
/// 键 = 文件系统 id + 规范路径，因此同一个文件无论调用拿到的是哪个环境对象都排在一条链上；不同文件、不同文件系统
/// 从不互相等待。同一文件上的并发调用按各自的键解析顺序执行。这不是针对 <c>bash</c> 或其他进程的锁。
/// </remarks>
public static class FileMutationQueue
{
    /// <summary>每个文件变更链的尾巴，按文件系统 id 与规范路径键控。</summary>
    private static readonly ConcurrentDictionary<string, Task> Queues = new(StringComparer.Ordinal);

    private static async Task<string> MutationKeyAsync(IExecutionEnv env, string path, Context context)
    {
        var absolutePath = (await env.AbsolutePathAsync(path, context).ConfigureAwait(false)).GetOrThrow();
        return $"{env.Id}\0{await CanonicalAsync(env, absolutePath, context).ConfigureAwait(false)}";
    }

    /// <summary>
    /// 规范路径。对尚不存在的文件，用其规范父目录拼上文件名——这样「<c>write</c> 新建文件」与「之后对它的变更」
    /// 即使在符号链接目录下也共用同一个键。对应 TS <c>canonical</c>。
    /// </summary>
    private static async Task<string> CanonicalAsync(IExecutionEnv env, string absolutePath, Context context)
    {
        var result = await env.CanonicalPathAsync(absolutePath, context).ConfigureAwait(false);
        if (result.IsOk) return result.Value;
        if (result.Error.Code == FileErrorCode.NotSupported) return absolutePath;
        if (result.Error.Code != FileErrorCode.NotFound) throw result.Error;

        // 文件系统在分隔符处切分路径，因此名字里可能含在别处是分隔符的字符。
        var parent = (await env.JoinPathAsync([absolutePath, ".."], context).ConfigureAwait(false)).GetOrThrow();
        if (parent == absolutePath || !absolutePath.StartsWith(parent, StringComparison.Ordinal)) return absolutePath;
        var separatorPresent = parent.EndsWith('/') || parent.EndsWith('\\');
        var name = absolutePath[(parent.Length + (separatorPresent ? 0 : 1))..];
        var canonicalParent = await CanonicalAsync(env, parent, context).ConfigureAwait(false);
        return (await env.JoinPathAsync([canonicalParent, name], context).ConfigureAwait(false)).GetOrThrow();
    }

    /// <summary>
    /// 串行执行对 <paramref name="path"/> 的变更。对应 TS <c>withFileMutationQueue</c>。
    /// </summary>
    public static async Task<T> WithAsync<T>(
        IExecutionEnv env, string path, Func<Task<T>> fn, Context context)
    {
        var key = await MutationKeyAsync(env, path, context).ConfigureAwait(false);

        // 不 await 就占住槽位，别的调用无法在中间插入。
        var previous = Queues.TryGetValue(key, out var tail) ? tail : Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var myTail = previous.ContinueWith(_ => done.Task, TaskScheduler.Default).Unwrap();
        Queues[key] = myTail;

        await previous.ConfigureAwait(false);
        try
        {
            return await fn().ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
            // 只有仍是链尾时才移除，避免删掉后来者的槽位。
            if (Queues.TryGetValue(key, out var current) && ReferenceEquals(current, myTail))
                Queues.TryRemove(key, out _);
        }
    }
}
