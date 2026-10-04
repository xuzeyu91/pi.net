namespace Pi.Telemetry;

/// <summary>
/// A single span in an active telemetry trace. Mirrors TS <c>TelemetrySpan</c>.
/// A span is itself a <see cref="ITelemetryContext"/>, so child spans are started
/// through <see cref="StartSpanAsync{T}"/>.
/// </summary>
public interface ITelemetrySpan
{
    /// <summary>Starts a child span whose parent is this span.</summary>
    Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback);

    /// <summary>Records an ordered, timestamped event on this span.</summary>
    void AddEvent(string name, SpanAttributes? attributes = null);

    /// <summary>Merges attributes onto this span; last write wins per key.</summary>
    void SetAttributes(SpanAttributes attributes);

    /// <summary>Sets the terminal status. Only the first explicit status is kept.</summary>
    void SetStatus(SpanStatus status);
}

/// <summary>
/// Telemetry context. Mirrors TS <c>TelemetryContext</c>.
/// <para>
/// The TS callback may return <c>T | Promise&lt;T&gt;</c>; in C# the single async
/// signature <c>Func&lt;ITelemetrySpan, Task&lt;T&gt;&gt;</c> plays that role, and
/// synchronous callbacks wrap their value with <see cref="Task.FromResult{T}(T)"/>.
/// (A sync-value overload would be ambiguous: a lambda returning <c>Task&lt;T&gt;</c>
/// would bind with <c>T = Task&lt;T&gt;</c> because .NET tasks, unlike JS promises,
/// do not auto-flatten.)
/// </para>
/// The callback owns the span lifecycle: the span is settled (with an automatic error
/// status if none was set) when the returned task completes, successfully or not.
/// </summary>
public interface ITelemetryContext
{
    /// <summary>Starts a span and runs <paramref name="callback"/> inside its lifetime.</summary>
    Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback);
}

/// <summary>Shared no-op telemetry used when the host application provides none.</summary>
public static class NoopTelemetryContext
{
    /// <summary>Singleton context in which every span is inert and callbacks run unchanged.</summary>
    public static readonly ITelemetryContext Instance = new NoopContext();

    private sealed class NoopSpan : ITelemetrySpan
    {
        public static readonly NoopSpan Instance = new();

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            try
            {
                return callback(Instance);
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
        }

        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private sealed class NoopContext : ITelemetryContext
    {
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => NoopSpan.Instance.StartSpanAsync(options, callback);
    }
}
