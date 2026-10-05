using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>校验错误（含 instancePath 与文案）。对应 typebox <c>TLocalizedValidationError</c> 的用到的字段。</summary>
public sealed record SchemaValidationError(string Keyword, string InstancePath, string Message);

/// <summary>
/// 工具调用参数校验。对应 TS <c>utils/validation.ts</c>：typebox <c>Compile</c>/<c>Value.Convert</c>
/// 在 C# 侧以手写 JSON Schema 校验器复刻（与 Pi.Protocol 的 schema 校验同路线）——
/// 先做「可选 null 归一 + 按 schema 强制类型转换」，再校验；失败时按
/// <c>路径: 文案</c> 逐条列出并附上收到的参数。
/// </summary>
public static class Validation
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 按名字找工具并校验参数。对应 TS <c>validateToolCall</c>：找不到工具或校验失败抛错。
    /// </summary>
    public static JsonNode? ValidateToolCall(IReadOnlyList<ToolDefinition> tools, ToolCallContent toolCall)
    {
        var tool = tools.FirstOrDefault(candidate => candidate.Name == toolCall.Name)
            ?? throw new InvalidOperationException($"Tool \"{toolCall.Name}\" not found");
        return ValidateToolArguments(tool, toolCall);
    }

    /// <summary>
    /// 校验并（必要时）强制转换工具调用参数。对应 TS <c>validateToolArguments</c>。
    /// 校验失败抛 <see cref="InvalidOperationException"/>，消息含逐条错误与收到的参数。
    /// </summary>
    public static JsonNode? ValidateToolArguments(ToolDefinition tool, ToolCallContent toolCall)
    {
        var schema = ToSchemaObject(tool.Parameters);
        var args = Clone(ToNode(toolCall.Arguments));

        NormalizeOptionalNulls(args, schema);
        args = Convert(args, schema);

        if (Check(args, schema)) return args;

        var errors = CollectErrors(args, schema, "")
            .Select(error => $"  - {FormatValidationPath(error)}: {error.Message}")
            .ToList();
        var errorText = errors.Count > 0 ? string.Join("\n", errors) : "Unknown validation error";

        throw new InvalidOperationException(
            $"Validation failed for tool \"{toolCall.Name}\":\n{errorText}\n\nReceived arguments:\n" +
            $"{PrettyPrint(ToNode(toolCall.Arguments))}");
    }

    // ---------- schema / 值 归一 ----------

    /// <summary>把 <see cref="ToolSchema"/> 的字典形状转成 <see cref="JsonObject"/>（内部统一用 JSON 节点操作）。</summary>
    public static JsonObject ToSchemaObject(ToolSchema schema)
        => JsonSerializer.SerializeToNode(schema.JsonSchema) as JsonObject ?? new JsonObject();

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonSerializer.SerializeToNode(value),
    };

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    private static string PrettyPrint(JsonNode? node)
        => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    private static IReadOnlyList<string> SchemaTypes(JsonObject schema)
    {
        var types = new List<string>();
        switch (schema["type"])
        {
            case JsonValue value when value.TryGetValue<string>(out var single):
                types.Add(single);
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue element && element.TryGetValue<string>(out var text)) types.Add(text);
                }
                break;
        }
        return types;
    }

    private static bool MatchesJsonType(JsonNode? value, string type) => type switch
    {
        "number" => value is JsonValue number && number.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue integer
            && integer.GetValueKind() == JsonValueKind.Number
            && IsInteger(integer),
        "boolean" => value is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "string" => value is JsonValue text && text.GetValueKind() == JsonValueKind.String,
        "null" => value is null || value.GetValueKind() == JsonValueKind.Null,
        "array" => value is JsonArray,
        "object" => value is JsonObject,
        _ => false,
    };

    private static bool IsInteger(JsonValue value)
    {
        if (value.TryGetValue<long>(out _)) return true;
        if (value.TryGetValue<int>(out _)) return true;
        return value.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d;
    }

    // ---------- 可选 null 归一（TS normalizeOptionalNulls） ----------

    private static void NormalizeOptionalNulls(JsonNode? value, JsonObject schema)
    {
        if (value is JsonArray array)
        {
            if (schema["items"] is JsonArray tuple)
            {
                for (var index = 0; index < array.Count && index < tuple.Count; index++)
                {
                    if (tuple[index] is JsonObject itemSchema) NormalizeOptionalNulls(array[index], itemSchema);
                }
            }
            else if (schema["items"] is JsonObject items)
            {
                foreach (var item in array) NormalizeOptionalNulls(item, items);
            }
            return;
        }

        if (value is not JsonObject obj || schema["properties"] is not JsonObject properties) return;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray requiredArray)
        {
            foreach (var item in requiredArray)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var name)) required.Add(name);
            }
        }

        foreach (var (key, propertySchemaNode) in properties)
        {
            if (!obj.ContainsKey(key)) continue;
            if (propertySchemaNode is not JsonObject propertySchema) continue;

            var isNull = obj[key] is null || obj[key]!.GetValueKind() == JsonValueKind.Null;
            var hasStringRef = propertySchema["$ref"] is JsonValue refValue
                && refValue.GetValueKind() == JsonValueKind.String;
            // 可选、非 $ref、且 null 不合法 → 直接删除该键（对齐 TS normalizeOptionalNulls）。
            if (isNull && !required.Contains(key) && !hasStringRef && !Check(null, propertySchema))
            {
                obj.Remove(key);
                continue;
            }
            NormalizeOptionalNulls(obj[key], propertySchema);
        }
    }

    // ---------- 强制类型转换（TS Value.Convert + coerceWithJsonSchema） ----------

    private static JsonNode? Convert(JsonNode? value, JsonObject schema)
    {
        var next = value;

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var nested in allOf.OfType<JsonObject>()) next = Convert(next, nested);
        }
        if (schema["anyOf"] is JsonArray anyOf) next = ConvertWithUnion(next, anyOf.OfType<JsonObject>().ToList());
        if (schema["oneOf"] is JsonArray oneOf) next = ConvertWithUnion(next, oneOf.OfType<JsonObject>().ToList());

        var types = SchemaTypes(schema);
        var matchesUnionMember = types.Count > 1 && types.Any(type => MatchesJsonType(next, type));
        if (types.Count > 0 && !matchesUnionMember)
        {
            foreach (var type in types)
            {
                var candidate = CoercePrimitive(next, type);
                if (!NodeEquals(candidate, next))
                {
                    next = candidate;
                    break;
                }
            }
        }

        if (types.Contains("object") && next is JsonObject obj) ApplyObjectCoercion(obj, schema);
        if (types.Contains("array") && next is JsonArray array) ApplyArrayCoercion(array, schema);

        return next;
    }

    private static JsonNode? ConvertWithUnion(JsonNode? value, IReadOnlyList<JsonObject> schemas)
    {
        foreach (var schema in schemas)
        {
            if (Check(value, schema)) return value;
        }
        foreach (var schema in schemas)
        {
            var candidate = Convert(Clone(value), schema);
            if (Check(candidate, schema)) return candidate;
        }
        return value;
    }

    private static void ApplyObjectCoercion(JsonObject value, JsonObject schema)
    {
        var properties = schema["properties"] as JsonObject;
        if (properties is not null)
        {
            foreach (var (key, propertySchema) in properties)
            {
                if (!value.ContainsKey(key) || propertySchema is not JsonObject property) continue;
                value[key] = Convert(value[key], property);
            }
        }

        if (schema["additionalProperties"] is JsonObject additional)
        {
            var defined = properties is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(properties.Select(entry => entry.Key), StringComparer.Ordinal);
            foreach (var (key, propertyValue) in value.ToList())
            {
                if (defined.Contains(key)) continue;
                value[key] = Convert(propertyValue, additional);
            }
        }
    }

    private static void ApplyArrayCoercion(JsonArray value, JsonObject schema)
    {
        if (schema["items"] is JsonArray tuple)
        {
            for (var index = 0; index < value.Count && index < tuple.Count; index++)
            {
                if (tuple[index] is JsonObject itemSchema) value[index] = Convert(value[index], itemSchema);
            }
            return;
        }
        if (schema["items"] is JsonObject items)
        {
            for (var index = 0; index < value.Count; index++) value[index] = Convert(value[index], items);
        }
    }

    private static JsonNode? CoercePrimitive(JsonNode? value, string type)
    {
        var kind = value?.GetValueKind();
        switch (type)
        {
            case "number":
            {
                if (value is null || kind == JsonValueKind.Null) return JsonValue.Create(0d);
                if (kind == JsonValueKind.String && value!.GetValue<string>().Trim().Length > 0)
                {
                    if (double.TryParse(value.GetValue<string>(), out var parsed) && !double.IsNaN(parsed)
                        && !double.IsInfinity(parsed))
                    {
                        return JsonValue.Create(parsed);
                    }
                }
                if (kind is JsonValueKind.True or JsonValueKind.False) return JsonValue.Create(value!.GetValue<bool>() ? 1d : 0d);
                return value;
            }
            case "integer":
            {
                if (value is null || kind == JsonValueKind.Null) return JsonValue.Create(0L);
                if (kind == JsonValueKind.String && value!.GetValue<string>().Trim().Length > 0
                    && long.TryParse(value.GetValue<string>(), out var parsed))
                {
                    return JsonValue.Create(parsed);
                }
                if (kind is JsonValueKind.True or JsonValueKind.False) return JsonValue.Create(value!.GetValue<bool>() ? 1L : 0L);
                return value;
            }
            case "boolean":
            {
                if (value is null || kind == JsonValueKind.Null) return JsonValue.Create(false);
                if (kind == JsonValueKind.String)
                {
                    var text = value!.GetValue<string>();
                    if (text == "true") return JsonValue.Create(true);
                    if (text == "false") return JsonValue.Create(false);
                }
                if (kind == JsonValueKind.Number && value is JsonValue numberValue)
                {
                    if (numberValue.TryGetValue(out double number) && number == 1) return JsonValue.Create(true);
                    if (numberValue.TryGetValue(out double zero) && zero == 0) return JsonValue.Create(false);
                }
                return value;
            }
            case "string":
            {
                if (value is null || kind == JsonValueKind.Null) return JsonValue.Create(string.Empty);
                if (kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonValue.Create(value!.ToJsonString().Trim('"'));
                }
                return value;
            }
            case "null":
            {
                if (value is null) return null;
                if (kind == JsonValueKind.String && value.GetValue<string>().Length == 0) return null;
                if (kind == JsonValueKind.False) return null;
                if (kind == JsonValueKind.Number && value is JsonValue numberValue
                    && numberValue.TryGetValue(out double zeroNumber) && zeroNumber == 0)
                {
                    return null;
                }
                return value;
            }
            default:
                return value;
        }
    }

    private static bool NodeEquals(JsonNode? left, JsonNode? right)
        => string.Equals(left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal);

    // ---------- 校验（TS validator.Check / Errors） ----------

    /// <summary>校验值是否符合 schema。对应 typebox <c>validator.Check</c>。</summary>
    public static bool Check(JsonNode? value, JsonObject schema)
        => CollectErrors(value, schema, string.Empty).Count == 0;

    private static IReadOnlyList<SchemaValidationError> CollectErrors(JsonNode? value, JsonObject schema, string path)
    {
        var errors = new List<SchemaValidationError>();

        if (schema["allOf"] is JsonArray allOf)
        {
            foreach (var nested in allOf.OfType<JsonObject>()) errors.AddRange(CollectErrors(value, nested, path));
        }
        if (schema["anyOf"] is JsonArray anyOf && !anyOf.OfType<JsonObject>().Any(nested => Check(value, nested)))
        {
            errors.Add(new SchemaValidationError("anyOf", path, "Expected anyOf match"));
        }
        if (schema["oneOf"] is JsonArray oneOf
            && oneOf.OfType<JsonObject>().Count(nested => Check(value, nested)) != 1)
        {
            errors.Add(new SchemaValidationError("oneOf", path, "Expected exactly one oneOf match"));
        }

        var types = SchemaTypes(schema);
        if (types.Count > 0 && !types.Any(type => MatchesJsonType(value, type)))
        {
            errors.Add(new SchemaValidationError("type", path, $"Expected {string.Join(" | ", types)}"));
            return errors;
        }

        if (schema["enum"] is JsonArray enumValues
            && !enumValues.Any(candidate => NodeEquals(candidate, value)))
        {
            errors.Add(new SchemaValidationError("enum", path, "Expected enum value"));
        }
        if (schema.ContainsKey("const") && !NodeEquals(schema["const"], value))
        {
            errors.Add(new SchemaValidationError("const", path, "Expected const value"));
        }

        if (value is JsonValue scalar)
        {
            if (schema["minLength"] is JsonValue minLength && scalar.GetValueKind() == JsonValueKind.String
                && scalar.GetValue<string>().Length < minLength.GetValue<int>())
            {
                errors.Add(new SchemaValidationError("minLength", path, $"Expected string length >= {minLength.GetValue<int>()}"));
            }
            if (schema["pattern"] is JsonValue pattern && scalar.GetValueKind() == JsonValueKind.String)
            {
                var regex = new Regex(pattern.GetValue<string>(), RegexOptions.None, RegexTimeout);
                if (!regex.IsMatch(scalar.GetValue<string>()))
                {
                    errors.Add(new SchemaValidationError("pattern", path, $"Expected string matching {pattern.GetValue<string>()}"));
                }
            }
            if (scalar.GetValueKind() == JsonValueKind.Number && scalar.TryGetValue(out double number))
            {
                if (schema["minimum"] is JsonValue minimum && number < minimum.GetValue<double>())
                {
                    errors.Add(new SchemaValidationError("minimum", path, $"Expected number >= {minimum.GetValue<double>()}"));
                }
                if (schema["maximum"] is JsonValue maximum && number > maximum.GetValue<double>())
                {
                    errors.Add(new SchemaValidationError("maximum", path, $"Expected number <= {maximum.GetValue<double>()}"));
                }
            }
        }

        if (value is JsonArray array)
        {
            if (schema["minItems"] is JsonValue minItems && array.Count < minItems.GetValue<int>())
            {
                errors.Add(new SchemaValidationError("minItems", path, $"Expected array length >= {minItems.GetValue<int>()}"));
            }
            if (schema["items"] is JsonArray tuple)
            {
                for (var index = 0; index < array.Count && index < tuple.Count; index++)
                {
                    if (tuple[index] is JsonObject itemSchema)
                    {
                        errors.AddRange(CollectErrors(array[index], itemSchema, $"{path}/{index}"));
                    }
                }
            }
            else if (schema["items"] is JsonObject items)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    errors.AddRange(CollectErrors(array[index], items, $"{path}/{index}"));
                }
            }
        }

        if (value is JsonObject obj)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (schema["required"] is JsonArray requiredArray)
            {
                foreach (var item in requiredArray)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var name)) required.Add(name);
                }
            }
            foreach (var name in required)
            {
                if (!obj.ContainsKey(name))
                {
                    errors.Add(new SchemaValidationError("required", path, $"Expected required property {name}"));
                }
            }

            var properties = schema["properties"] as JsonObject;
            if (properties is not null)
            {
                foreach (var (key, propertySchema) in properties)
                {
                    if (!obj.ContainsKey(key) || propertySchema is not JsonObject property) continue;
                    errors.AddRange(CollectErrors(obj[key], property, $"{path}/{Escape(key)}"));
                }
            }

            if (schema["additionalProperties"] is JsonValue { } additionalFlag
                && additionalFlag.GetValueKind() == JsonValueKind.False)
            {
                var defined = properties is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(properties.Select(entry => entry.Key), StringComparer.Ordinal);
                foreach (var (key, _) in obj)
                {
                    if (!defined.Contains(key))
                    {
                        errors.Add(new SchemaValidationError("additionalProperties", $"{path}/{Escape(key)}",
                            "Expected no additional properties"));
                    }
                }
            }
        }

        return errors;
    }

    private static string Escape(string segment)
        => segment.Replace("~", "~0").Replace("/", "~1");

    /// <summary>
    /// 错误路径格式化：required 错误拼上缺失属性名，其余把 <c>/a/b</c> 转为 <c>a.b</c>。
    /// 对应 TS <c>formatValidationPath</c>。
    /// </summary>
    private static string FormatValidationPath(SchemaValidationError error)
    {
        if (error.Keyword == "required")
        {
            var requiredProperty = error.Message.StartsWith("Expected required property ", StringComparison.Ordinal)
                ? error.Message["Expected required property ".Length..]
                : null;
            if (!string.IsNullOrEmpty(requiredProperty))
            {
                var basePath = error.InstancePath.TrimStart('/').Replace('/', '.');
                return basePath.Length > 0 ? $"{basePath}.{requiredProperty}" : requiredProperty;
            }
        }
        var path = error.InstancePath.TrimStart('/').Replace('/', '.');
        return path.Length > 0 ? path : "root";
    }
}
