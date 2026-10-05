using Pi.Ai.Utils;
using Pi.Ai.Types;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;

namespace Pi.Ai.Api;

/// <summary>语法约束采样解析结果。对应 TS <c>GrammarConstrainedSampling</c>。</summary>
public sealed record GrammarConstrainedSampling(string Format, string Definition, string InputProperty);

/// <summary>grammar 工具输入的流式 JSON 缓冲。对应 TS <c>GrammarToolInputJsonBuffer</c>。</summary>
public sealed record GrammarToolInputJsonBuffer
{
    public string Input { get; set; } = "";
    public bool Started { get; set; }
    public bool Closed { get; set; }
}

/// <summary>strict 模式不支持的 JSON Schema 关键字错误。对应 TS <c>UnsupportedStrictJsonSchemaError</c>。</summary>
public class UnsupportedStrictJsonSchemaError(string message) : InvalidOperationException(message);

/// <summary>
/// 约束采样辅助。对应 TS <c>api/constrained-sampling.ts</c>：把工具 schema 转换为
/// provider strict 模式要求的子集（required 补全 + 可空包装 anyOf + additionalProperties=false），
/// 并解析 grammar（lark/regex）自定义工具。
/// </summary>
public static class ConstrainedSampling
{
    /// <summary>strict 模式拒绝的 schema 关键字。对应 TS <c>UNSUPPORTED_STRICT_SCHEMA_KEYS</c>。</summary>
    private static readonly string[] UnsupportedStrictSchemaKeys =
    [
        "$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties", "dependentSchemas",
        "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems",
        "not", "if", "then", "else",
    ];

    /// <summary>provider 的 strict 模式是否拒绝该关键字取值。对应 TS <c>UnsupportedStrictSchemaKeywordCheck</c>。</summary>
    public delegate bool UnsupportedStrictSchemaKeywordCheck(string key, JsonNode? value);

    private static bool IsJsonSchemaObject(JsonNode? value) => value is JsonObject;

    private static bool IsStructuredSchema(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return false;
        var types = obj.Str("type") is { } single
            ? new List<string> { single }
            : obj["type"] is JsonArray array
                ? array.OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList()
                : [];
        return types.Contains("object") || types.Contains("array")
            || obj.Has("properties") || obj.Has("items");
    }

    private static bool SchemaAllowsNull(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return false;
        if (obj.Str("type") == "null") return true;
        if (obj["type"] is JsonArray typeArray
            && typeArray.OfType<JsonValue>().Any(v => v.GetValue<string>() == "null"))
        {
            return true;
        }
        if (obj["const"] is JsonValue { } constValue
            && constValue.TryGetValue<JsonElement>(out var constElement)
            && constElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (obj["enum"] is JsonArray enumArray
            && enumArray.Any(v => v is JsonValue { } ev
                && ev.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Null))
        {
            return true;
        }
        return obj["anyOf"] is JsonArray anyOf && anyOf.Any(variant => SchemaAllowsNull(variant));
    }

    private static void MakeJsonSchemaNodeStrict(
        JsonObject schema, UnsupportedStrictSchemaKeywordCheck? isUnsupportedKeyword)
    {
        foreach (var key in UnsupportedStrictSchemaKeys)
        {
            if (schema.Has(key))
            {
                throw new UnsupportedStrictJsonSchemaError($"{key} schemas are unsupported");
            }
        }
        if (isUnsupportedKeyword is not null)
        {
            foreach (var (key, value) in schema)
            {
                if (isUnsupportedKeyword(key, value))
                {
                    throw new UnsupportedStrictJsonSchemaError(
                        $"{key}: {value?.ToJsonString() ?? "null"} is unsupported");
                }
            }
        }

        if (schema["anyOf"] is { } anyOfNode)
        {
            if (anyOfNode is not JsonArray anyOf || anyOf.Count == 0)
            {
                throw new UnsupportedStrictJsonSchemaError("anyOf must contain at least one schema");
            }
            foreach (var variant in anyOf)
            {
                if (IsStructuredSchema(variant))
                {
                    throw new UnsupportedStrictJsonSchemaError("object and array unions are unsupported");
                }
                if (variant is JsonObject variantObject) MakeJsonSchemaNodeStrict(variantObject, isUnsupportedKeyword);
            }
        }

        if (schema["items"] is JsonArray)
        {
            throw new UnsupportedStrictJsonSchemaError("tuple schemas are unsupported");
        }
        if (schema.Obj("items") is { } items)
        {
            MakeJsonSchemaNodeStrict(items, isUnsupportedKeyword);
        }

        var isObjectSchema = schema.Str("type") == "object";
        if (schema.Has("properties") && !isObjectSchema)
        {
            throw new UnsupportedStrictJsonSchemaError("properties require type object");
        }
        if (!isObjectSchema) return;
        if (schema.Has("additionalProperties")
            && (schema["additionalProperties"] is not JsonValue { } ap
                || !ap.TryGetValue<bool>(out var apBool)
                || apBool))
        {
            throw new UnsupportedStrictJsonSchemaError("schema-valued or true additionalProperties is unsupported");
        }
        if (schema.Has("properties") && schema["properties"] is not JsonObject)
        {
            throw new UnsupportedStrictJsonSchemaError("object properties must be a schema map");
        }
        if (schema.Has("required")
            && (schema["required"] is not JsonArray required
                || required.Any(key => key is not JsonValue { } rv || !rv.TryGetValue<string>(out _))))
        {
            throw new UnsupportedStrictJsonSchemaError("object required must be a string array");
        }

        var properties = schema.Obj("properties") ?? [];
        var propertyNames = properties.Select(entry => entry.Key).ToList();
        var requiredSet = (schema["required"] as JsonArray ?? [])
            .OfType<JsonValue>()
            .Select(v => v.GetValue<string>())
            .ToHashSet();
        if (requiredSet.Any(key => !propertyNames.Contains(key)))
        {
            throw new UnsupportedStrictJsonSchemaError("required contains an unknown property");
        }
        foreach (var (key, property) in properties.ToList())
        {
            if (property is JsonObject propertyObject)
            {
                MakeJsonSchemaNodeStrict(propertyObject, isUnsupportedKeyword);
            }
            if (!requiredSet.Contains(key) && !SchemaAllowsNull(property))
            {
                properties[key] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(property?.DeepClone() ?? new JsonObject(), new JsonObject { ["type"] = "null" }),
                };
            }
        }
        schema["required"] = new JsonArray(propertyNames.Select(name => (JsonNode)name).ToArray());
        schema["additionalProperties"] = false;
    }

    /// <summary>把工具 schema 转换为 provider 约束采样要求的 strict 子集。对应 TS <c>makeStrictJsonSchema</c>。</summary>
    public static JsonObject MakeStrictJsonSchema(
        ToolSchema schema, UnsupportedStrictSchemaKeywordCheck? isUnsupportedKeyword = null)
    {
        var cloned = JsonNode.Parse(JsonSerializer.Serialize(schema.JsonSchema)) as JsonObject
            ?? throw new UnsupportedStrictJsonSchemaError("root schema must have type object");
        MakeJsonSchemaNodeStrict(cloned, isUnsupportedKeyword);
        if (cloned.Str("type") != "object")
        {
            throw new UnsupportedStrictJsonSchemaError("root schema must have type object");
        }
        return cloned;
    }

    public static JsonObject? GetJsonSchemaToolParameters(ToolDefinition tool, bool? strict)
        => strict == true
            ? MakeStrictJsonSchema(tool.Parameters)
            : JsonNode.Parse(JsonSerializer.Serialize(tool.Parameters.JsonSchema)) as JsonObject;

    public static string? GetGrammarToolInput(string toolName, object? arguments, string inputProperty)
    {
        string? input = arguments switch
        {
            JsonObject obj => obj.Str(inputProperty),
            IReadOnlyDictionary<string, object?> dict => dict.TryGetValue(inputProperty, out var value)
                ? value as string
                : null,
            _ => null,
        };
        if (input is null)
        {
            throw new InvalidOperationException(
                $"Grammar tool call \"{toolName}\" requires argument \"{inputProperty}\" to be a string.");
        }
        return input;
    }

    /// <summary>
    /// 追加 grammar 工具输入的流式 JSON 增量；闭包后输入变化即抛错。
    /// 对应 TS <c>appendGrammarToolInputJsonDelta</c>。
    /// </summary>
    public static string? AppendGrammarToolInputJsonDelta(
        GrammarToolInputJsonBuffer buffer, string inputProperty, string nextInput, bool close)
    {
        if (buffer.Closed)
        {
            if (close && nextInput == buffer.Input) return null;
            throw new InvalidOperationException(
                $"grammar tool input for property \"{inputProperty}\" changed after it was closed");
        }
        if (!nextInput.StartsWith(buffer.Input, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"grammar tool input for property \"{inputProperty}\" changed non-monotonically");
        }

        var inputDelta = nextInput[buffer.Input.Length..];
        if (!close && inputDelta.Length == 0) return null;

        var delta = "";
        if (!buffer.Started)
        {
            delta += "{" + JsonSerializer.Serialize(inputProperty) + ":\"";
            buffer.Started = true;
        }
        delta += JsonSerializer.Serialize(inputDelta)[1..^1];
        buffer.Input = nextInput;

        if (close)
        {
            delta += "\"}";
            buffer.Closed = true;
        }
        return delta;
    }

    private static string InferGrammarInputProperty(ToolDefinition tool)
    {
        var schema = JsonNode.Parse(JsonSerializer.Serialize(tool.Parameters.JsonSchema)) as JsonObject
            ?? throw new InvalidOperationException("grammar constrained sampling requires an object parameter schema");
        if (schema.Str("type") != "object")
        {
            throw new InvalidOperationException("grammar constrained sampling requires an object parameter schema");
        }
        if (schema["required"] is not JsonArray required || required.Count != 1
            || required[0] is not JsonValue { } requiredValue || !requiredValue.TryGetValue<string>(out var inputProperty))
        {
            throw new InvalidOperationException("grammar constrained sampling requires exactly one required string property");
        }

        var properties = schema.Obj("properties");
        if (properties?.Obj(inputProperty) is null)
        {
            throw new InvalidOperationException(
                $"grammar constrained sampling requires a properties entry for {inputProperty}");
        }
        if (properties.Obj(inputProperty)!.Str("type") != "string")
        {
            throw new InvalidOperationException(
                $"grammar constrained sampling property {inputProperty} must have type string");
        }
        return inputProperty;
    }

    /// <summary>解析工具声明的 constrainedSampling 配置（wire 形状：{type:"grammar"|"json_schema", ...}）。</summary>
    private static JsonObject? GetConstrainedSamplingConfig(ToolDefinition tool)
        => tool.ConstrainedSampling;

    /// <summary>
    /// 决定 JSON-schema 工具是否以 strict 模式发送。对应 TS
    /// <c>resolveJsonSchemaStrictSampling</c>。
    /// </summary>
    public static bool? ResolveJsonSchemaStrictSampling(
        ToolDefinition tool, bool supportsStrictMode,
        UnsupportedStrictSchemaKeywordCheck? isUnsupportedKeyword = null)
    {
        var config = GetConstrainedSamplingConfig(tool);
        if (config is null || config.Str("type") != "json_schema") return null;

        if (supportsStrictMode)
        {
            try
            {
                MakeStrictJsonSchema(tool.Parameters, isUnsupportedKeyword);
                return true;
            }
            catch (UnsupportedStrictJsonSchemaError error)
            {
                if (config.Str("strict") != "require") return null;
                throw new InvalidOperationException(
                    $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but {error.Message}.");
            }
        }
        if (config.Str("strict") == "require")
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" requires JSON-schema constrained sampling, but strict tools are unsupported.");
        }
        return null;
    }

    public static GrammarConstrainedSampling? ResolveGrammarConstrainedSampling(
        ToolDefinition tool, bool supportsOpenAiGrammarTools)
    {
        var config = GetConstrainedSamplingConfig(tool);
        if (config is null || config.Str("type") != "grammar") return null;
        if (!supportsOpenAiGrammarTools) return null;

        var variants = config.Obj("variants");
        var larkDefinition = variants?.Str("openai_lark");
        var regexDefinition = variants?.Str("openai_regex");
        var hasLark = larkDefinition is { } lark && lark.Trim().Length > 0;
        var hasRegex = regexDefinition is { } regex && regex.Trim().Length > 0;
        if (!hasLark && !hasRegex)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" cannot use grammar constrained sampling: no supported grammar variant was provided.");
        }

        try
        {
            return new GrammarConstrainedSampling(
                Format: hasLark ? "lark" : "regex",
                Definition: hasLark ? larkDefinition! : regexDefinition!,
                InputProperty: InferGrammarInputProperty(tool));
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" cannot use grammar constrained sampling: {error.Message}.");
        }
    }

    public static IReadOnlyDictionary<string, string> CreateGrammarToolInputProperties(
        IReadOnlyList<ToolDefinition>? tools, bool supportsOpenAiGrammarTools)
    {
        var properties = new Dictionary<string, string>();
        foreach (var tool in tools ?? [])
        {
            var grammar = ResolveGrammarConstrainedSampling(tool, supportsOpenAiGrammarTools);
            if (grammar is not null) properties[tool.Name] = grammar.InputProperty;
        }
        return properties;
    }
}
