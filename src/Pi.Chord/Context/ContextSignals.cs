namespace Pi.Chord.Context;

/// <summary>
/// 取消信号派生助手。对应 TS <c>chord/context/index.ts</c> 的
/// <c>withAbortSignal</c> / <c>withoutAbortSignal</c> / <c>withCancel</c> /
/// <c>awaitWithContext</c>（信号约定键见 <see cref="Context.AbortSignalKey"/>）。
/// </summary>
public static class ContextSignals
{
    /// <summary>
    /// 派生一个被父信号或新信号中任一取消的上下文；父上下文本身不变。
    /// 对应 TS <c>withAbortSignal(signal, context)</c>（<c>AbortSignal.any</c>）。
    /// </summary>
    public static Context WithAbortSignal(CancellationToken signal, Context context)
    {
        var parentSignal = context.AbortSignal;
        if (parentSignal is { CanBeCanceled: true } parent)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(parent, signal);
            return context.WithValue(Context.AbortSignalKey, (CancellationToken?)linked.Token);
        }
        return context.WithValue(Context.AbortSignalKey, (CancellationToken?)signal);
    }

    /// <summary>
    /// 派生一个保留全部上下文值但移除调用方取消的上下文。仅用于强制性清理。
    /// 对应 TS <c>withoutAbortSignal(context)</c>。
    /// </summary>
    public static Context WithoutAbortSignal(Context context)
        => context.WithValue(Context.AbortSignalKey, (CancellationToken?)null);

    /// <summary>派生一个可独立取消的子上下文。对应 TS <c>withCancel(context)</c>。</summary>
    public static (Context Context, Action Cancel) WithCancel(Context context)
    {
        var source = new CancellationTokenSource();
        return (WithAbortSignal(source.Token, context), source.Cancel);
    }

    /// <summary>
    /// 观察一个任务直至其落定或调用被取消；取消只拒绝本次等待，不取消底层任务。
    /// 对应 TS <c>awaitWithContext(promise, context)</c>。
    /// </summary>
    public static async Task<T> AwaitWithContext<T>(Task<T> task, Context context)
    {
        var signal = context.AbortSignal;
        if (signal is not { CanBeCanceled: true } token) return await task.ConfigureAwait(false);
        if (token.IsCancellationRequested) throw AbortError(token);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#if NET
        await using var registration = token.Register(() => completion.TrySetResult(true)).ConfigureAwait(false);
#else
        using var registration = token.Register(() => completion.TrySetResult(true));
#endif
        var finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
        if (finished == completion.Task) throw AbortError(token);
        return await task.ConfigureAwait(false);
    }

    /// <summary>无 Task&lt;T&gt; 泛型的等待重载。</summary>
    public static async Task AwaitWithContext(Task task, Context context)
    {
        var signal = context.AbortSignal;
        if (signal is not { CanBeCanceled: true } token)
        {
            await task.ConfigureAwait(false);
            return;
        }
        if (token.IsCancellationRequested) throw AbortError(token);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#if NET
        await using var registration = token.Register(() => completion.TrySetResult(true)).ConfigureAwait(false);
#else
        using var registration = token.Register(() => completion.TrySetResult(true));
#endif
        var finished = await Task.WhenAny(task, completion.Task).ConfigureAwait(false);
        if (finished == completion.Task) throw AbortError(token);
        await task.ConfigureAwait(false);
    }

    /// <summary>取消异常。对应 TS <c>abortError</c>（reason 为 Error 用 reason，否则 AbortError DOMException）。</summary>
    private static OperationCanceledException AbortError(CancellationToken token) => new(token);
}
