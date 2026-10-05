using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>JSON 对象取值辅助（对齐 TS 的 typeof 检查语义；OAuth 层与 API 层共用）。</summary>
public static class JsonNodeExtensions
{
    public static string? Str(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue { } primitive
            && primitive.TryGetValue<string>(out var text)
            ? text
            : null;

    public static double? Num(this JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is not JsonValue { } primitive)
        {
            return null;
        }
        // 程序内构造的 JsonValue 可能是装箱 int/long：TryGetValue<double> 只匹配
        // 精确类型，需逐级回落（解析出的 JSON 走 JsonElement 路径无此问题）。
        if (primitive.TryGetValue<double>(out var d)) return d;
        if (primitive.TryGetValue<long>(out var l)) return l;
        if (primitive.TryGetValue<int>(out var i)) return i;
        return null;
    }

    public static bool? Bool(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue { } primitive
            && primitive.TryGetValue<bool>(out var flag)
            ? flag
            : null;

    /// <summary>键存在且非 null（对齐 TS <c>value !== undefined</c>）。</summary>
    public static bool Has(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is not null
            && value.GetValueKind() != System.Text.Json.JsonValueKind.Null;

    public static JsonObject? Obj(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value) ? value as JsonObject : null;

    /// <summary>正有限数（对齐 TS positiveNumber）。</summary>
    public static int? PositiveInt(this JsonObject obj, string key)
        => obj.Num(key) is { } number && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0
            ? (int)number
            : null;
}
