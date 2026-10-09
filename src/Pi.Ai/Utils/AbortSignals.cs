using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 合并取消信号：多信号任一取消即触发。对应 TS <c>combineAbortSignals</c>
/// （C# 用 linked CancellationTokenSource 表达）。
/// </summary>
public static class AbortSignals
{
    public sealed class Combined : IDisposable
    {
        public CancellationToken Token { get; }

        private readonly CancellationTokenSource? _source;

        internal Combined(CancellationToken token, CancellationTokenSource? source)
        {
            Token = token;
            _source = source;
        }

        /// <summary>单信号直通时无需清理；多信号时释放 linked CTS。</summary>
        public void Dispose() => _source?.Dispose();
    }

    /// <summary>合并多个取消令牌。单个时直通（无额外清理），多个时建 linked 源。</summary>
    public static Combined Combine(params ReadOnlySpan<CancellationToken> signals)
    {
        var active = new List<CancellationToken>();
        foreach (var signal in signals)
        {
            if (signal.CanBeCanceled) active.Add(signal);
        }
        switch (active.Count)
        {
            case 0:
                return new Combined(CancellationToken.None, null);
            case 1:
                return new Combined(active[0], null);
            default:
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource([.. active]);
                return new Combined(source.Token, source);
            }
        }
    }

    /// <summary>
    /// 停止等待被取消的操作，但继续观察其结算（避免未观察异常）。
    /// 对应 TS <c>raceWithAbortSignal</c>：取消以 <see cref="OperationCanceledException"/> 呈现，
    /// 而非 TS 的 <c>signal.reason</c>。
    /// </summary>
    public static async Task<T> RaceWithAsync<T>(Task<T> operation, CancellationToken signal)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!signal.CanBeCanceled) return await operation.ConfigureAwait(false);
        if (signal.IsCancellationRequested)
        {
            Observe(operation);
            throw new OperationCanceledException(signal);
        }

        var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), aborted);

        var winner = await Task.WhenAny(operation, aborted.Task).ConfigureAwait(false);
        if (!ReferenceEquals(winner, operation))
        {
            Observe(operation);
            throw new OperationCanceledException(signal);
        }

        return await operation.ConfigureAwait(false);
    }

    /// <summary>非泛型重载：停止等待被取消的操作，但继续观察其结算。</summary>
    public static async Task RaceWithAsync(Task operation, CancellationToken signal)
        => await RaceWithAsync<object?>(AwaitAsync(operation), signal).ConfigureAwait(false);

    private static async Task<object?> AwaitAsync(Task operation)
    {
        await operation.ConfigureAwait(false);
        return null;
    }

    /// <summary>挂上观察者，避免被放弃的操作留下未观察异常。</summary>
    private static void Observe(Task operation)
        => _ = operation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
