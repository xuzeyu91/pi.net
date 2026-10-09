using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Core;

/// <summary>One schema validation failure, shaped like a TypeBox localized error.</summary>
public sealed record JsonSchemaError
{
    /// <summary>Failing keyword, e.g. <c>type</c>, <c>required</c>, <c>maxItems</c>.</summary>
    public required string Keyword { get; init; }

    /// <summary>JSON-pointer-like path of the failing instance (<c>""</c> at the root).</summary>
    public required string InstancePath { get; init; }

    public required string Message { get; init; }

    /// <summary>Populated for <c>required</c> (TypeBox's <c>params.requiredProperties</c>).</summary>
    public IReadOnlyList<string>? RequiredProperties { get; init; }
}

/// <summary>
/// A minimal JSON-schema node. The TS code compiles TypeBox schemas and reads
/// <c>Check()</c>/<c>Errors()</c>; this port keeps the same two entry points on a
/// hand-built schema tree so validation errors read the same.
/// </summary>
/// <remarks>
/// Differences from TypeBox (difference C38):
/// <list type="bullet">
/// <item>TypeBox reports every failing keyword of a node; this port stops at the first one per node,
/// so messages stay short.</item>
/// <item>Error wording mirrors TypeBox's (<c>Expected string</c>, <c>Expected required property</c>, …)
/// but is not guaranteed byte-identical for keywords the port does not use.</item>
/// </list>
/// </remarks>
public abstract record JsonSchema
{
    /// <summary>Whether <paramref name="value"/> satisfies this schema.</summary>
    public bool Check(JsonNode? value) => Check(value, out _);

    /// <summary>Whether <paramref name="value"/> satisfies this schema, collecting failures.</summary>
    public bool Check(JsonNode? value, out List<JsonSchemaError> errors)
    {
        errors = [];
        Validate(value, string.Empty, errors);
        return errors.Count == 0;
    }

    internal abstract void Validate(JsonNode? value, string path, List<JsonSchemaError> errors);

    /// <summary>TypeBox error for a bare type mismatch.</summary>
    private protected static void AddTypeError(List<JsonSchemaError> errors, string path, string expected)
        => errors.Add(new JsonSchemaError { Keyword = "type", InstancePath = path, Message = $"Expected {expected}" });

    private protected static void AddKeywordError(
        List<JsonSchemaError> errors, string keyword, string path, string message)
        => errors.Add(new JsonSchemaError { Keyword = keyword, InstancePath = path, Message = message });

    /// <summary>JSON <c>null</c> is a null <see cref="JsonNode"/> reference in this model.</summary>
    private protected static bool IsNull(JsonNode? value) => value is null;

    private protected static bool IsString(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<string>(out _);

    private protected static bool IsNumber(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<double>(out _);

    private protected static bool IsInteger(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<double>(out var number)
            && double.IsFinite(number) && Math.Floor(number) == number;

    private protected static bool IsBoolean(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<bool>(out _);

    /// <summary>Value of an <c>anyOf</c> branch: a failure of any branch is a <c>union</c> failure.</summary>
    internal static string ChildPath(string path, string segment)
        => $"{path}/{segment.Replace("~", "~0").Replace("/", "~1")}";
}

/// <summary>Accepts anything (TypeBox <c>Type.Unknown()</c>).</summary>
public sealed record AnyJsonSchema : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
    }
}

/// <summary>Accepts only JSON <c>null</c> (TypeBox <c>Type.Null()</c>).</summary>
public sealed record NullJsonSchema : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (!IsNull(value)) AddTypeError(errors, path, "null");
    }
}

/// <summary>TypeBox <c>Type.Boolean()</c>.</summary>
public sealed record BooleanJsonSchema : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (!IsBoolean(value)) AddTypeError(errors, path, "boolean");
    }
}

/// <summary>TypeBox <c>Type.String({ minLength })</c>.</summary>
public sealed record StringJsonSchema(int? MinLength = null) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (!IsString(value))
        {
            AddTypeError(errors, path, "string");
            return;
        }

        if (MinLength is { } minLength
            && value is JsonValue candidate
            && candidate.TryGetValue<string>(out var text)
            && text.Length < minLength)
        {
            AddKeywordError(errors, "minLength", path, $"Expected string length greater or equal to {minLength}");
        }
    }
}

/// <summary>TypeBox <c>Type.Number({...})</c> / <c>Type.Integer({...})</c>.</summary>
public sealed record NumberJsonSchema(
    bool Integer = false,
    double? Minimum = null,
    double? Maximum = null,
    double? ExclusiveMinimum = null) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        var matches = Integer ? IsInteger(value) : IsNumber(value);
        if (!matches)
        {
            AddTypeError(errors, path, Integer ? "integer" : "number");
            return;
        }

        var number = ((JsonValue)value!).GetValue<double>();
        if (Minimum is { } minimum && number < minimum)
        {
            AddKeywordError(errors, "minimum", path, $"Expected number greater or equal to {Format(minimum)}");
        }
        else if (Maximum is { } maximum && number > maximum)
        {
            AddKeywordError(errors, "maximum", path, $"Expected number less or equal to {Format(maximum)}");
        }
        else if (ExclusiveMinimum is { } exclusiveMinimum && number <= exclusiveMinimum)
        {
            AddKeywordError(errors, "exclusiveMinimum", path,
                $"Expected number greater than {Format(exclusiveMinimum)}");
        }
    }

    private static string Format(double value)
        => value == Math.Floor(value) && !double.IsInfinity(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>TypeBox <c>Type.Literal(value)</c>.</summary>
public sealed record LiteralJsonSchema(string Value) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (!IsString(value) || ((JsonValue)value!).GetValue<string>() != Value)
        {
            AddTypeError(errors, path, $"'{Value}'");
        }
    }
}

/// <summary>TypeBox <c>Type.Array(items, { minItems, maxItems })</c>.</summary>
public sealed record ArrayJsonSchema(JsonSchema Items, int? MinItems = null, int? MaxItems = null) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (value is not JsonArray array)
        {
            AddTypeError(errors, path, "array");
            return;
        }

        if (MinItems is { } minItems && array.Count < minItems)
        {
            AddKeywordError(errors, "minItems", path, $"Expected array length greater or equal to {minItems}");
            return;
        }
        if (MaxItems is { } maxItems && array.Count > maxItems)
        {
            AddKeywordError(errors, "maxItems", path, $"Expected array length less or equal to {maxItems}");
            return;
        }

        for (var index = 0; index < array.Count; index++)
        {
            Items.Validate(array[index], ChildPath(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), errors);
        }
    }
}

/// <summary>TypeBox <c>Type.Object({...})</c> with the default open <c>additionalProperties</c>.</summary>
public sealed record ObjectJsonSchema : JsonSchema
{
    private readonly List<(string Name, JsonSchema Type, bool Optional)> _properties = [];

    public ObjectJsonSchema(params (string Name, JsonSchema Type, bool Optional)[] properties)
        => _properties.AddRange(properties);

    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (value is not JsonObject instance)
        {
            AddTypeError(errors, path, "object");
            return;
        }

        // TypeBox checks `required` before the declared properties.
        var missing = _properties
            .Where(property => !property.Optional && !instance.ContainsKey(property.Name))
            .Select(property => property.Name)
            .ToList();
        if (missing.Count > 0)
        {
            errors.Add(new JsonSchemaError
            {
                Keyword = "required",
                InstancePath = path,
                Message = "Expected required property",
                RequiredProperties = missing,
            });
        }

        foreach (var (name, type, _) in _properties)
        {
            if (!instance.TryGetPropertyValue(name, out var property)) continue;
            // An explicit JSON null is a present-but-null value, never "absent" (TS `undefined`).
            type.Validate(property, ChildPath(path, name), errors);
        }
    }
}

/// <summary>TypeBox <c>Type.Record(Type.String(), values)</c>: every property is validated.</summary>
public sealed record RecordJsonSchema(JsonSchema Values) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        if (value is not JsonObject instance)
        {
            AddTypeError(errors, path, "object");
            return;
        }

        foreach (var (name, property) in instance)
        {
            Values.Validate(property, ChildPath(path, name), errors);
        }
    }
}

/// <summary>TypeBox <c>Type.Union([...])</c>: the value must satisfy at least one branch.</summary>
public sealed record UnionJsonSchema(params JsonSchema[] Variants) : JsonSchema
{
    internal override void Validate(JsonNode? value, string path, List<JsonSchemaError> errors)
    {
        foreach (var variant in Variants)
        {
            if (variant.Check(value)) return;
        }

        AddTypeError(errors, path, "union value");
    }
}

/// <summary>Compact constructors for the schema tree (mirrors the TypeBox call sites).</summary>
public static class JsonSchemaBuilder
{
    public static JsonSchema Any { get; } = new AnyJsonSchema();

    public static JsonSchema Null { get; } = new NullJsonSchema();

    public static JsonSchema Bool { get; } = new BooleanJsonSchema();

    public static JsonSchema Str(int? minLength = null) => new StringJsonSchema(minLength);

    public static JsonSchema Num(double? minimum = null, double? maximum = null, double? exclusiveMinimum = null)
        => new NumberJsonSchema(false, minimum, maximum, exclusiveMinimum);

    public static JsonSchema Int(double? minimum = null, double? maximum = null)
        => new NumberJsonSchema(true, minimum, maximum);

    public static JsonSchema Lit(string value) => new LiteralJsonSchema(value);

    public static JsonSchema Arr(JsonSchema items, int? minItems = null, int? maxItems = null)
        => new ArrayJsonSchema(items, minItems, maxItems);

    public static JsonSchema Obj(params (string Name, JsonSchema Type, bool Optional)[] properties)
        => new ObjectJsonSchema(properties);

    public static JsonSchema Rec(JsonSchema values) => new RecordJsonSchema(values);

    public static JsonSchema Union(params JsonSchema[] variants) => new UnionJsonSchema(variants);
}
