namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/abort.ts</c> (<c>AbortSignal</c> → <see cref="CancellationToken"/>).</summary>
public static class Abort
{
    /// <summary>Normalize an optional public signal without imposing a deadline.</summary>
    public static CancellationToken OperationSignal(CancellationToken? signal) => signal ?? CancellationToken.None;

    /// <summary>
    /// Stop waiting on abort while observing the abandoned operation through settlement. The TS version
    /// rejects with the signal's <c>reason</c> (or an <c>AbortError</c>); here the cancellation surfaces as
    /// <see cref="OperationCanceledException"/> carrying the token.
    /// </summary>
    public static async Task<T> RaceWithAbortSignalAsync<T>(Task<T> operation, CancellationToken? signal)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (signal is null)
        {
            return await operation.ConfigureAwait(false);
        }

        var token = signal.Value;
        if (token.IsCancellationRequested)
        {
            Observe(operation);
            throw new OperationCanceledException(token);
        }

        var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            aborted);

        // `Register` fires synchronously for an already-cancelled token, which mirrors the trailing
        // `if (signal.aborted) onAbort();` in the TS implementation.
        if (token.IsCancellationRequested)
        {
            Observe(operation);
            throw new OperationCanceledException(token);
        }

        var winner = await Task.WhenAny(operation, aborted.Task).ConfigureAwait(false);
        if (!ReferenceEquals(winner, operation))
        {
            Observe(operation);
            throw new OperationCanceledException(token);
        }

        return await operation.ConfigureAwait(false);
    }

    /// <summary>Attach an observer so an abandoned operation's exception is not left unobserved.</summary>
    private static void Observe(Task operation)
    {
        _ = operation.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
