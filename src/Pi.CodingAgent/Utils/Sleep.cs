namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/sleep.ts</c>.</summary>
public static class Sleep
{
    /// <summary>
    /// Wait for <paramref name="ms"/> milliseconds. The TS version rejects with <c>Error("Aborted")</c>
    /// when the signal fires; here that becomes the standard <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task SleepAsync(int ms, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (ms <= 0)
        {
            return Task.CompletedTask;
        }

        return DelayCoreAsync(ms, cancellationToken);
    }

    private static async Task DelayCoreAsync(int ms, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
