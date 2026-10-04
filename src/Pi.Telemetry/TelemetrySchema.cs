using System.Text.Json.Serialization;

namespace Pi.Telemetry;

/// <summary>Allowed attribute value kinds. Mirrors TS <c>TelemetryAttributeType</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TelemetryAttributeType>))]
public enum TelemetryAttributeType
{
    String,
    Number,
    Boolean,
    StringArray,
    NumberArray,
    BooleanArray,
}

/// <summary>Attribute cardinality. Mirrors TS <c>"low" | "high"</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TelemetryCardinality>))]
public enum TelemetryCardinality
{
    Low,
    High,
}

/// <summary>Shared attribute metadata. Mirrors TS <c>TelemetryAttributeMetadata</c>.</summary>
public abstract record TelemetryAttributeMetadata(
    string Description,
    bool? Sensitive = null,
    TelemetryCardinality? Cardinality = null);

/// <summary>
/// Definition of one telemetry attribute. Mirrors the TS union
/// <c>TelemetryAttributeDefinition</c> as a record hierarchy discriminated by
/// <see cref="Type"/>; serialized with the original <c>"type"</c> tag spelling so
/// schema JSON stays wire-compatible with the TypeScript definitions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScalarAttributeDefinition), "scalar")]
[JsonDerivedType(typeof(ArrayAttributeDefinition), "array")]
public abstract record TelemetryAttributeDefinition : TelemetryAttributeMetadata
{
    private protected TelemetryAttributeDefinition(
        string description,
        bool? sensitive,
        TelemetryCardinality? cardinality)
        : base(description, sensitive, cardinality) { }

    /// <summary>Runtime value kind of this attribute.</summary>
    [JsonIgnore]
    public abstract TelemetryAttributeType Type { get; }
}

/// <summary>Scalar attribute definition (string / number / boolean with optional allowed values).</summary>
public sealed record ScalarAttributeDefinition : TelemetryAttributeDefinition
{
    public ScalarAttributeDefinition(
        TelemetryAttributeType type,
        string description,
        bool? sensitive = null,
        TelemetryCardinality? cardinality = null,
        IReadOnlyList<object?>? values = null,
        IReadOnlyList<object?>? examples = null)
        : base(description, sensitive, cardinality)
    {
        Type = type;
        Values = values;
        Examples = examples;
    }

    public override TelemetryAttributeType Type { get; }

    public IReadOnlyList<object?>? Values { get; init; }

    public IReadOnlyList<object?>? Examples { get; init; }
}

/// <summary>Array attribute definition with optional element values and example rows.</summary>
public sealed record ArrayAttributeDefinition : TelemetryAttributeDefinition
{
    public ArrayAttributeDefinition(
        TelemetryAttributeType type,
        string description,
        bool? sensitive = null,
        TelemetryCardinality? cardinality = null,
        IReadOnlyList<object?>? elementValues = null,
        IReadOnlyList<IReadOnlyList<object?>>? examples = null)
        : base(description, sensitive, cardinality)
    {
        Type = type;
        ElementValues = elementValues;
        Examples = examples;
    }

    public override TelemetryAttributeType Type { get; }

    public IReadOnlyList<object?>? ElementValues { get; init; }

    public IReadOnlyList<IReadOnlyList<object?>>? Examples { get; init; }
}

/// <summary>
/// Attribute definition bound to a required flag. Mirrors the TS intersection type
/// <c>TelemetryAttributeDefinition &amp; { required: boolean }</c> as composition.
/// </summary>
public sealed record TelemetryRequiredAttributeDefinition(
    TelemetryAttributeDefinition Definition,
    bool Required);

/// <summary>Definition of one span event. Mirrors TS <c>TelemetryEventDefinition</c>.</summary>
public sealed record TelemetryEventDefinition(
    string Description,
    IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> Attributes);

/// <summary>Allowed parent relationships. Mirrors TS <c>TelemetryParentDefinition</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AnyParent), "any")]
[JsonDerivedType(typeof(RootOrExternalParent), "root_or_external")]
[JsonDerivedType(typeof(SpansParent), "spans")]
public abstract record TelemetryParentDefinition;

/// <summary>Any parent span is allowed (<c>{ kind: "any" }</c>).</summary>
public sealed record AnyParent : TelemetryParentDefinition;

/// <summary>Only root or external spans may parent this span (<c>{ kind: "root_or_external" }</c>).</summary>
public sealed record RootOrExternalParent : TelemetryParentDefinition;

/// <summary>Only the listed spans may parent this span (<c>{ kind: "spans", spans: [...] }</c>).</summary>
public sealed record SpansParent(IReadOnlyList<string> SpanNames) : TelemetryParentDefinition;

/// <summary>Definition of one span in a telemetry schema. Mirrors TS <c>TelemetrySpanDefinition</c>.</summary>
public sealed record TelemetrySpanDefinition(
    string Description,
    TelemetryParentDefinition Parents,
    IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> StartAttributes,
    IReadOnlyDictionary<string, TelemetryAttributeDefinition> EndAttributes,
    IReadOnlyDictionary<string, TelemetryEventDefinition>? Events,
    TelemetryStatusPolicy Status);

/// <summary>Status policy of a span. Mirrors TS <c>status: { default: "ok"; errorWhen: string }</c>.</summary>
public sealed record TelemetryStatusPolicy(string Default, string ErrorWhen);

/// <summary>Root schema document. Mirrors TS <c>TelemetrySchemaDefinition</c>.</summary>
public sealed record TelemetrySchemaDefinition(
    int Version,
    IReadOnlyDictionary<string, TelemetrySpanDefinition> Spans);

/// <summary>
/// Identity helper for serializable telemetry schema data. Mirrors TS <c>defineTelemetrySchema</c>.
/// No runtime validation is performed; the value is returned unchanged.
/// </summary>
public static class TelemetrySchema
{
    public static TelemetrySchemaDefinition Define(TelemetrySchemaDefinition schema) => schema;
}
