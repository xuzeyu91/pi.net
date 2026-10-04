namespace Pi.Telemetry;

/// <summary>
/// Span starter bound to one explicit parent context. Mirrors the runtime behavior of
/// TS <c>TypedSpanStarter</c>: the schema argument exists only for compile-time type
/// inference in TypeScript and has no runtime effect, so the C# port exposes the bound
/// delegate without the schema-derived generic plumbing.
/// </summary>
public delegate Task<TResult> TypedSpanStarter<TResult>(
    string name,
    SpanAttributes? attributes,
    Func<ITelemetrySpan, TypedSpanStarter<TResult>, Task<TResult>> callback);

public static class TypedSpanStarterFactory
{
    /// <summary>
    /// Binds an explicit parent context into a span starter.
    /// Mirrors TS <c>createTypedSpanStarter</c>; schemas are accepted for API parity but unused.
    /// </summary>
    public static TypedSpanStarter<TResult> Create<TResult>(
        ITelemetryContext telemetryContext,
        params IReadOnlyList<TelemetrySchemaDefinition> schemas)
        => CreateCore<TResult>((options, callback) => telemetryContext.StartSpanAsync(options, callback), schemas);

    private static TypedSpanStarter<TResult> CreateCore<TResult>(
        Func<SpanOptions, Func<ITelemetrySpan, Task<TResult>>, Task<TResult>> start,
        IReadOnlyList<TelemetrySchemaDefinition> schemas)
        => (name, attributes, callback) => start(
            new SpanOptions(name, attributes),
            span => callback(span, CreateCore<TResult>(
                (childOptions, childCallback) => span.StartSpanAsync(childOptions, childCallback),
                schemas)));
}
