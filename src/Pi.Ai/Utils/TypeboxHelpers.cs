using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>
/// 工具 schema 构造辅助。对应 TS <c>utils/typebox-helpers.ts</c>：TS 的 typebox
/// <c>Type.Unsafe</c> 在 C# 侧即直接产出 JSON Schema 节点（<c>ToolSchema</c> 的承载形状）。
/// </summary>
public static class TypeboxHelpers
{
    /// <summary>
    /// 构造字符串枚举 schema（兼容 Google API 等不支持 anyOf/const 的 provider）。
    /// 对应 TS <c>StringEnum</c>：<c>{ type: "string", enum: [...] }</c>，
    /// 可选 <c>description</c> / <c>default</c>。
    /// </summary>
    public static JsonObject StringEnum(IReadOnlyList<string> values,
        string? description = null, string? defaultValue = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]),
        };
        if (!string.IsNullOrEmpty(description)) schema["description"] = description;
        if (!string.IsNullOrEmpty(defaultValue)) schema["default"] = defaultValue;
        return schema;
    }
}
