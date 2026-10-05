using System.Text.Json.Nodes;

namespace Pi.Chord.Node;

/// <summary>清单/artifact 解析用的 JSON 取值辅助（对齐 TS 的 typeof 检查语义）。</summary>
internal static class JsonObjectExtensions
{
    public static string? Str(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue primitive
            && primitive.TryGetValue<string>(out var text)
            ? text
            : null;

    public static double? Num(this JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is not JsonValue primitive) return null;
        if (primitive.TryGetValue<double>(out var d)) return d;
        if (primitive.TryGetValue<long>(out var l)) return l;
        if (primitive.TryGetValue<int>(out var i)) return i;
        return null;
    }
}
