namespace Pi.Telemetry.Testing;

/// <summary>
/// A fresh adapter instance and normalized snapshot reader owned by one conformance case.
/// Mirrors TS <c>TelemetryAdapterFixture</c>.
/// </summary>
public interface ITelemetryAdapterFixture : IAsyncDisposable
{
    ITelemetryContext Context { get; }

    Task<IReadOnlyList<RecordedTelemetrySpan>> GetSpansAsync();
}

/// <summary>Creates an isolated adapter fixture for one conformance case.</summary>
public delegate Task<ITelemetryAdapterFixture> TelemetryAdapterFixtureFactory();

/// <summary>A runner-independent conformance case that can be registered with any test framework.</summary>
public sealed record TelemetryAdapterConformanceCase(string Group, string Name, Func<Task> Run);

/// <summary>Failure raised by a conformance case.</summary>
public sealed class TelemetryConformanceException(string message) : Exception(message);

/// <summary>
/// Runner-independent cases for the callback telemetry adapter contract.
/// Mirrors TS <c>createTelemetryAdapterConformance</c>; cases that relied on JS Proxy
/// unreadable objects are reproduced with throwing stand-ins (a dictionary whose
/// enumeration fails, a value whose copy fails), preserving the same passivity and
/// atomicity guarantees.
/// </summary>
public static class TelemetryAdapterConformance
{
    public static IReadOnlyList<TelemetryAdapterConformanceCase> Create(
        TelemetryAdapterFixtureFactory factory)
    {
        List<TelemetryAdapterConformanceCase> cases = [];
        void AddCase(string group, string name, Func<ITelemetryAdapterFixture, Task> test)
            => cases.Add(new TelemetryAdapterConformanceCase(group, name, async () =>
            {
                await using var fixture = await factory();
                await test(fixture);
            }));

        AddCase("callback lifecycle", "admits once synchronously and preserves the result", async fixture =>
        {
            var admitted = false;
            var calls = 0;
            var expected = new object();
            var result = fixture.Context.StartSpanAsync(
                new SpanOptions("success"),
                _ =>
                {
                    admitted = true;
                    calls++;
                    return Task.FromResult(expected);
                });

            True(admitted);
            Equal(1, calls);
            Same(expected, await result);
            var spans = await fixture.GetSpansAsync();
            var span = FindSpan(spans, "success");
            True(span.Status is SpanStatus.Ok);
            True(span.Settled);
        });

        AddCase("callback lifecycle", "preserves synchronous and asynchronous rejections", async fixture =>
        {
            var syncError = new InvalidOperationException("sync");
            await ThrowsSameAsync(
                fixture.Context.StartSpanAsync<object>(new SpanOptions("sync-error"), _ => throw syncError),
                syncError);

            var asyncError = new InvalidOperationException("async");
            await ThrowsSameAsync(
                fixture.Context.StartSpanAsync<object>(new SpanOptions("async-error"), _ => Task.FromException<object>(asyncError)),
                asyncError);

            var canceledError = new OperationCanceledException();
            // .NET await raises a fresh TaskCanceledException wrapper for canceled tasks
            // (JS propagates the raw rejection value), so assert cancellation propagation.
            try
            {
                await fixture.Context.StartSpanAsync<object>(
                    new SpanOptions("canceled-error"),
                    _ => Task.FromCanceled<object>(new CancellationToken(true)));
                throw new TelemetryConformanceException("Expected the span callback to be canceled.");
            }
            catch (TaskCanceledException) when (canceledError is not null)
            {
            }

            var spans = await fixture.GetSpansAsync();
            foreach (var name in new[] { "sync-error", "async-error", "canceled-error" })
                True(FindSpan(spans, name).Status is SpanStatus.ErrorStatus, $"Expected error status for {name}");
        });

        AddCase("status", "uses last explicit status without automatic overwrite", async fixture =>
        {
            await fixture.Context.StartSpanAsync(new SpanOptions("last-status"), span =>
            {
                span.SetStatus(new SpanStatus.ErrorStatus(new TelemetryErrorDetail("Expected", "first")));
                span.SetStatus(SpanStatus.Ok.Instance);
                return Task.FromResult(new object());
            });

            var thrown = new InvalidOperationException("after explicit status");
            await ThrowsSameAsync(
                fixture.Context.StartSpanAsync<object>(new SpanOptions("explicit-before-throw"), span =>
                {
                    span.SetStatus(SpanStatus.Ok.Instance);
                    throw thrown;
                }),
                thrown);

            var rejected = new InvalidOperationException("after async explicit status");
            await ThrowsSameAsync(
                fixture.Context.StartSpanAsync<object>(new SpanOptions("explicit-before-rejection"), span =>
                {
                    span.SetStatus(new SpanStatus.ErrorStatus(new TelemetryErrorDetail("Expected", "async failure")));
                    return Task.FromException<object>(rejected);
                }),
                rejected);

            await fixture.Context.StartSpanAsync(new SpanOptions("expected-failure"), span =>
            {
                span.SetStatus(new SpanStatus.ErrorStatus(new TelemetryErrorDetail("Expected", "returned failure")));
                return Task.FromResult(new object());
            });

            var spans = await fixture.GetSpansAsync();
            True(FindSpan(spans, "last-status").Status is SpanStatus.Ok);
            True(FindSpan(spans, "explicit-before-throw").Status is SpanStatus.Ok);
            Equal(
                new TelemetryErrorDetail("Expected", "async failure"),
                ((SpanStatus.ErrorStatus)FindSpan(spans, "explicit-before-rejection").Status).Detail);
            Equal(
                new TelemetryErrorDetail("Expected", "returned failure"),
                ((SpanStatus.ErrorStatus)FindSpan(spans, "expected-failure").Status).Detail);
        });

        AddCase("recording", "merges attributes and records ordered events", async fixture =>
        {
            await fixture.Context.StartSpanAsync(
                new SpanOptions("recording", new SpanAttributes
                {
                    ["start"] = "value",
                    ["overwrite"] = "start",
                    ["ignored"] = null,
                }),
                span =>
                {
                    span.SetAttributes(new SpanAttributes { ["count"] = 1L, ["overwrite"] = "middle" });
                    span.SetAttributes(new SpanAttributes { ["count"] = null, ["overwrite"] = "end" });
                    span.AddEvent("first", new SpanAttributes { ["index"] = 1L, ["ignored"] = null });
                    span.AddEvent("second", new SpanAttributes { ["index"] = 2L });
                    return Task.FromResult(new object());
                });

            var span = FindSpan(await fixture.GetSpansAsync(), "recording");
            Equal(
                new Dictionary<string, object?> { ["start"] = "value", ["overwrite"] = "end", ["count"] = 1L },
                span.Attributes);
            Equal(
            [
                new RecordedTelemetryEvent("first", new Dictionary<string, object?> { ["index"] = 1L }),
                new RecordedTelemetryEvent("second", new Dictionary<string, object?> { ["index"] = 2L }),
            ], span.Events);
        });

        AddCase("recording", "ignores failed attribute calls atomically", async fixture =>
        {
            await fixture.Context.StartSpanAsync(
                new SpanOptions("atomic-attributes", new SpanAttributes { ["retained"] = "value" }),
                span =>
                {
                    var attributes = new SpanAttributes
                    {
                        ["partial"] = "must not survive",
                        ["unreadable"] = new ThrowingOnCopyValue(),
                    };
                    span.SetAttributes(attributes);
                    return Task.FromResult(new object());
                });

            var span = FindSpan(await fixture.GetSpansAsync(), "atomic-attributes");
            Equal(new Dictionary<string, object?> { ["retained"] = "value" }, span.Attributes);
        });

        AddCase("recording", "makes calls after settlement inert", async fixture =>
        {
            ITelemetrySpan? settledSpan = null;
            await fixture.Context.StartSpanAsync(
                new SpanOptions("settled", new SpanAttributes { ["value"] = "initial" }),
                span =>
                {
                    settledSpan = span;
                    return Task.FromResult(new object());
                });
            var capturedSpan = settledSpan ?? throw new TelemetryConformanceException("Expected callback span");

            capturedSpan.SetAttributes(new SpanAttributes { ["value"] = "late" });
            capturedSpan.AddEvent("late", new SpanAttributes { ["value"] = true });
            capturedSpan.SetStatus(SpanStatus.ErrorStatus.Bare);
            var childResult = capturedSpan.StartSpanAsync(new SpanOptions("late-child"), _ => Task.FromResult(7));
            Equal(7, await childResult);

            var spans = await fixture.GetSpansAsync();
            var recorded = Single(spans);
            Equal(new Dictionary<string, object?> { ["value"] = "initial" }, recorded.Attributes);
            Empty(recorded.Events);
            True(recorded.Status is SpanStatus.Ok);
        });

        AddCase("parentage", "records nested and concurrent child relationships", async fixture =>
        {
            var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await fixture.Context.StartSpanAsync<object>(new SpanOptions("parent"), async parent =>
            {
                var first = parent.StartSpanAsync(new SpanOptions("first-child"), async _ =>
                {
                    await firstGate.Task;
                    return "first";
                });
                var second = parent.StartSpanAsync(new SpanOptions("second-child"), _ => Task.FromResult("done"));
                Equal("done", await second);
                firstGate.SetResult();
                await first;
                return new object();
            });

            var spans = await fixture.GetSpansAsync();
            var parent = FindSpan(spans, "parent");
            var first = FindSpan(spans, "first-child");
            var second = FindSpan(spans, "second-child");
            Null(parent.ParentId);
            Equal(parent.Id, first.ParentId);
            Equal(parent.Id, second.ParentId);
            NotNull(second.EndSequence);
            NotNull(first.EndSequence);
            NotNull(parent.EndSequence);
            True(second.EndSequence < first.EndSequence);
            True(first.EndSequence < parent.EndSequence);
        });

        AddCase("passivity", "suppresses unreadable telemetry payload failures", async fixture =>
        {
            var calls = 0;
            var options = new SpanOptions("unreadable-options", new ThrowingSpanAttributes { ["secret"] = "value" });
            var result = fixture.Context.StartSpanAsync(options, _ =>
            {
                calls++;
                return Task.FromResult(9);
            });

            Equal(1, calls);
            Equal(9, await result);
            Empty(await fixture.GetSpansAsync());

            await fixture.Context.StartSpanAsync(new SpanOptions("unreadable-recording"), span =>
            {
                var attributes = new ThrowingSpanAttributes { ["secret"] = "value" };
                span.SetAttributes(attributes);
                span.AddEvent("unreadable-event", attributes);
                span.SetStatus(SpanStatus.Ok.Instance);
                return Task.FromResult(new object());
            });

            var recorded = await fixture.GetSpansAsync();
            var span = Single(recorded);
            Empty(span.Attributes);
            Empty(span.Events);
            True(span.Status is SpanStatus.Ok);
        });

        AddCase("passivity", "ignores failed status calls atomically", async fixture =>
        {
            var rejection = new InvalidOperationException("rejected after unreadable status");
            await ThrowsSameAsync(
                fixture.Context.StartSpanAsync<object>(new SpanOptions("unreadable-status"), span =>
                {
                    span.SetStatus(null!); // simulates a failing/unreadable status payload
                    throw rejection;
                }),
                rejection);

            var spans = await fixture.GetSpansAsync();
            True(FindSpan(spans, "unreadable-status").Status is SpanStatus.ErrorStatus);
        });

        return cases;
    }

    private static RecordedTelemetrySpan FindSpan(IReadOnlyList<RecordedTelemetrySpan> spans, string name)
        => spans.FirstOrDefault(candidate => candidate.Name == name)
            ?? throw new TelemetryConformanceException($"Expected recorded span {name}");

    private static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TelemetryConformanceException(message ?? "Expected condition to hold.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        // Deep comparison via canonical JSON: plain EqualityComparer would use reference
        // semantics for Dictionary/nested record members, while the TS original relies
        // on deepStrictEqual for object/attribute comparisons. All telemetry payloads
        // are JSON primitives, so canonical serialization is a faithful deep-equality
        // proxy.
        var expectedJson = System.Text.Json.JsonSerializer.Serialize(expected, SerializerOptions);
        var actualJson = System.Text.Json.JsonSerializer.Serialize(actual, SerializerOptions);
        if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
            throw new TelemetryConformanceException($"Expected {expectedJson}, got {actualJson}.");
    }

    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
            throw new TelemetryConformanceException("Expected the same instance.");
    }

    private static void Null(object? value)
    {
        if (value is not null) throw new TelemetryConformanceException("Expected null.");
    }

    private static void NotNull(object? value)
    {
        if (value is null) throw new TelemetryConformanceException("Expected non-null.");
    }

    private static void Empty(System.Collections.IEnumerable collection)
    {
        foreach (var _ in collection)
            throw new TelemetryConformanceException("Expected empty collection.");
    }

    private static T Single<T>(IReadOnlyList<T> collection)
    {
        if (collection.Count != 1)
            throw new TelemetryConformanceException($"Expected single element, got {collection.Count}.");
        return collection[0];
    }

    private static async Task ThrowsSameAsync(Task operation, Exception expected)
    {
        try
        {
            await operation;
            throw new TelemetryConformanceException("Expected operation to reject.");
        }
        catch (Exception error) when (ReferenceEquals(error, expected))
        {
        }
    }

    /// <summary>Attribute value whose defensive copy throws, simulating an unreadable payload.</summary>
    private sealed class ThrowingOnCopyValue
    {
        public override string ToString() => throw new InvalidOperationException("read");
    }

    /// <summary>Attributes whose public enumeration throws, simulating an unreadable payload object.</summary>
    private sealed class ThrowingSpanAttributes : SpanAttributes
    {
        public override IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => throw new InvalidOperationException("enumerate");
    }
}
