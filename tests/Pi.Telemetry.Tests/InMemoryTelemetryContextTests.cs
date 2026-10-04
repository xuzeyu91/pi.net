using Pi.Telemetry.Testing;
using Xunit;

namespace Pi.Telemetry.Tests;

public class InMemoryTelemetryContextTests
{
    private static IReadOnlyList<TelemetryAdapterConformanceCase> Cases()
        => TelemetryAdapterConformance.Create(() =>
            Task.FromResult<ITelemetryAdapterFixture>(new InMemoryFixture()));

    public static IEnumerable<object[]> ConformanceCaseNames()
        => Cases().Select(@case => new object[] { $"{@case.Group} / {@case.Name}" });

    [Theory]
    [MemberData(nameof(ConformanceCaseNames))]
    public async Task PassesConformanceSuite(string caseKey)
    {
        var @case = Cases().Single(candidate => $"{candidate.Group} / {candidate.Name}" == caseKey);
        await @case.Run();
    }

    [Fact]
    public async Task SpanIdsAndEndSequencesAreMonotonic()
    {
        var context = new InMemoryTelemetryContext();
        await context.StartSpanAsync(new SpanOptions("outer"), span => span.StartSpanAsync(new SpanOptions("inner"), _ => Task.FromResult(1)));
        var spans = context.GetSpans();
        Assert.Equal(2, spans.Count);
        Assert.Equal([1, 2], spans.Select(span => span.Id));
        Assert.Equal([2, 1], spans.Select(span => span.EndSequence)); // inner settles first
        Assert.Equal(1, spans[1].ParentId);
    }

    [Fact]
    public async Task NoopContextRunsCallbacksAndRecordsNothing()
    {
        var result = await NoopTelemetryContext.Instance.StartSpanAsync(new SpanOptions("noop"), _ => Task.FromResult(42));
        Assert.Equal(42, result);
    }

    [Fact]
    public void AttributeValuesRejectForeignTypes()
    {
        Assert.Throws<InvalidOperationException>(() => SpanAttributes.CopyValue(new object()));
        Assert.True(SpanAttributes.IsValidValue("s"));
        Assert.True(SpanAttributes.IsValidValue(1L));
        Assert.True(SpanAttributes.IsValidValue(new object[] { "a", "b" }));
        Assert.False(SpanAttributes.IsValidValue(new { x = 1 }));
    }

    private sealed class InMemoryFixture : ITelemetryAdapterFixture
    {
        private readonly InMemoryTelemetryContext _context = new();

        public ITelemetryContext Context => _context;

        public Task<IReadOnlyList<RecordedTelemetrySpan>> GetSpansAsync()
            => Task.FromResult(_context.GetSpans());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
