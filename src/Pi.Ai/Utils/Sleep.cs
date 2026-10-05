namespace Pi.Ai.Utils;

/// <summary>可中止休眠。对应 TS <c>sleep</c>（utils/sleep.ts）。</summary>
public static class Sleep
{
    /// <summary>休眠 ms 毫秒；token 取消时立即抛 OperationCanceledException。</summary>
    public static async Task DelayAsync(int ms, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ms <= 0) return;
        try
        {
            await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw; // 非 token 引起的取消不可能出现；保守重抛
        }
    }
}
