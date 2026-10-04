namespace Pi.Telemetry;

/// <summary>A recorded telemetry event (detached snapshot).</summary>
public sealed record RecordedTelemetryEvent(string Name, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>A recorded telemetry span (detached snapshot).</summary>
public sealed record RecordedTelemetrySpan(
    int Id,
    int? ParentId,
    string Name,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<RecordedTelemetryEvent> Events,
    SpanStatus Status,
    bool Settled,
    int? EndSequence);

/// <summary>
/// Backend-neutral reference implementation that records spans in process memory.
/// Mirrors TS <c>InMemoryTelemetryContext</c>, including settlement semantics,
/// automatic error status, end ordering, and passivity toward malformed payloads.
/// </summary>
public sealed class InMemoryTelemetryContext : ITelemetryContext
{
    private readonly object _lock = new();
    private readonly List<MutableSpan> _spans = new();
    private int _nextSpanId = 1;
    private int _nextEndSequence = 1;

    private sealed class MutableSpan
    {
        public required int Id { get; init; }
        public required int? ParentId { get; init; }
        public required string Name { get; init; }
        public required SpanAttributes Attributes { get; init; }
        public List<RecordedTelemetryEvent> Events { get; } = new();
        public SpanStatus Status { get; set; } = SpanStatus.Ok.Instance;
        public bool ExplicitStatus { get; set; }
        public bool Settled { get; set; }
        public int? EndSequence { get; set; }
    }

    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        => StartInMemorySpan(options, null, callback);

    private async Task<T> StartInMemorySpan<T>(
        SpanOptions options,
        MutableSpan? parent,
        Func<ITelemetrySpan, Task<T>> callback)
    {
        // A settled parent no longer admits recorded children; fall through to the no-op context.
        if (parent is { Settled: true }) return await NoopTelemetryContext.Instance.StartSpanAsync(options, callback);

        MutableSpan recordedSpan;
        var attributes = new SpanAttributes();
        try
        {
            if (options.Attributes is not null)
            {
                foreach (var (name, value) in options.Attributes)
                {
                    if (value is null) continue;
                    attributes[name] = SpanAttributes.CopyValue(value);
                }
            }

            lock (_lock)
            {
                recordedSpan = new MutableSpan
                {
                    Id = _nextSpanId++,
                    ParentId = parent?.Id,
                    Name = options.Name,
                    Attributes = attributes,
                };
                _spans.Add(recordedSpan);
            }
        }
        catch
        {
            // Creating the recording slot is passive; unreadable options fall through to no-op.
            return await NoopTelemetryContext.Instance.StartSpanAsync(options, callback);
        }

        var span = new InMemorySpan(this, recordedSpan);

        Task<T> task;
        try
        {
            task = callback(span);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            throw;
        }

        return await SettleAfterCallback(task, recordedSpan);

        async Task<T> SettleAfterCallback(Task<T> pending, MutableSpan span)
        {
            try
            {
                var result = await pending.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Settle(span, failed: false, null);
                return result;
            }
            catch (Exception error)
            {
                Settle(span, failed: true, error);
                throw;
            }
        }
    }

    private void Settle(MutableSpan span, bool failed, Exception? error)
    {
        lock (_lock)
        {
            if (span.Settled) return;
            if (failed && !span.ExplicitStatus)
                span.Status = error is not null ? SpanStatus.FromError(error) : SpanStatus.ErrorStatus.Bare;
            span.Settled = true;
            span.EndSequence = _nextEndSequence++;
        }
    }

    /// <summary>Returns detached snapshots in span-start order.</summary>
    public IReadOnlyList<RecordedTelemetrySpan> GetSpans()
    {
        lock (_lock)
        {
            return _spans.Select(span => new RecordedTelemetrySpan(
                span.Id,
                span.ParentId,
                span.Name,
                span.Attributes.Copy().ToDictionary(kv => kv.Key, kv => kv.Value),
                span.Events.Select(e => e with { }).ToList(),
                span.Status,
                span.Settled,
                span.EndSequence)).ToList();
        }
    }

    private sealed class InMemorySpan(InMemoryTelemetryContext owner, MutableSpan recorded) : ITelemetrySpan
    {
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => owner.StartInMemorySpan(options, recorded, callback);

        public void AddEvent(string name, SpanAttributes? attributes = null)
        {
            if (recorded.Settled) return;
            try
            {
                var copy = new Dictionary<string, object?>();
                if (attributes is not null)
                {
                    foreach (var (key, value) in attributes)
                    {
                        if (value is null) continue;
                        copy[key] = SpanAttributes.CopyValue(value);
                    }
                }
                lock (owner._lock) recorded.Events.Add(new RecordedTelemetryEvent(name, copy));
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }

        public void SetAttributes(SpanAttributes attributes)
        {
            if (recorded.Settled) return;
            try
            {
                // Copy first, then swap: a failure anywhere in the payload must leave
                // existing attributes untouched (atomicity, mirrors TS copy-then-assign).
                var copy = attributes.Copy();
                lock (owner._lock) recorded.Attributes.Merge(copy);
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }

        public void SetStatus(SpanStatus status)
        {
            if (recorded.Settled) return;
            try
            {
                lock (owner._lock)
                {
                    recorded.Status = status switch
                    {
                        SpanStatus.Ok => SpanStatus.Ok.Instance,
                        SpanStatus.ErrorStatus error => new SpanStatus.ErrorStatus(error.Detail is null
                            ? null
                            : new TelemetryErrorDetail(error.Detail.Name, error.Detail.Message)),
                        _ => SpanStatus.ErrorStatus.Bare,
                    };
                    recorded.ExplicitStatus = true;
                }
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }
    }
}
