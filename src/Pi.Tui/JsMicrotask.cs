namespace Pi.Tui;

/// <summary>
/// A source for exactly one <see cref="JsMicrotask"/>. The continuation registered by
/// <c>await</c> is stored here and only runs when <see cref="Resume"/> is called, which makes the
/// interleaving of an asynchronous chain completely deterministic (deviation T25).
///
/// This exists because <see cref="TaskCompletionSource"/> cannot be used for the purpose: a
/// continuation registered with <c>ConfigureAwait(false)</c> is still <em>posted</em> rather than
/// resumed inline inside <c>SetResult</c> on current runtimes, so the queue would transiently look
/// empty while a continuation is in flight. Driving the continuation directly removes that race.
/// </summary>
internal sealed class JsMicrotaskSource
{
    private readonly Lock _gate = new();
    private Action? _continuation;
    private bool _resumed;

    /// <summary>The awaitable that suspends the caller until <see cref="Resume"/> is called.</summary>
    public JsMicrotask Task => new(this);

    /// <summary>Stores the continuation the compiler hands over for this hop.</summary>
    public void SetContinuation(Action continuation)
    {
        lock (_gate)
        {
            if (_resumed)
            {
                // Resume() already happened (the producer and the pump are on different threads);
                // run the continuation straight away so the hop is never lost.
                continuation();
                return;
            }

            _continuation = continuation;
        }
    }

    /// <summary>Runs the registered continuation inline, on the calling thread.</summary>
    public void Resume()
    {
        Action? continuation;
        lock (_gate)
        {
            _resumed = true;
            continuation = _continuation;
            _continuation = null;
        }

        continuation?.Invoke();
    }
}

/// <summary>
/// An awaitable that never reports completion by itself: it is the C# stand-in for one JS
/// <c>await</c>, which always defers to the microtask queue even when the awaited value is already
/// settled. See <see cref="JsMicrotaskSource"/> and deviation T25.
/// </summary>
internal readonly struct JsMicrotask
{
    private readonly JsMicrotaskSource _source;

    public JsMicrotask(JsMicrotaskSource source) => _source = source;

    public JsMicrotaskAwaiter GetAwaiter() => new(_source);
}

/// <summary>The awaiter for <see cref="JsMicrotask"/>; always incomplete until resumed.</summary>
internal readonly struct JsMicrotaskAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion
{
    private readonly JsMicrotaskSource _source;

    public JsMicrotaskAwaiter(JsMicrotaskSource source) => _source = source;

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation) => _source.SetContinuation(continuation);

    public void UnsafeOnCompleted(Action continuation) => _source.SetContinuation(continuation);

    public void GetResult()
    {
    }
}
