using System.Text.Json.Nodes;
using Pi.Ai.Types;

namespace Pi.Durable.Tools;

/// <summary>
/// 工具参数的 JSON Schema 构造与校验辅助。
/// </summary>
/// <remarks>
/// TS 用 typebox 声明 <c>parameters</c> 并据此得到 <c>Static&lt;T&gt;</c> 参数类型；C# 无编译期 schema 到类型的桥，
/// 因此 schema 手写为 JSON Schema 字典（<see cref="ToolSchema"/>），运行期参数以 JSON 字典承载，经这里的
/// <see cref="Object"/> / <see cref="RequiredString"/> / <see cref="OptionalNumber"/> 取值。
/// </remarks>
public static class ToolSchemaBuilder
{
    /// <summary>构造对象 schema。对应 typebox <c>Type.Object</c>。</summary>
    public static ToolSchema Object(IReadOnlyDictionary<string, object?> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties) props[key] = value is JsonNode node ? node : JsonValue.Create(value);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode?)JsonValue.Create(r))]);
        return new ToolSchema(schema.ToDictionary(kv => kv.Key, kv => (object?)kv.Value?.DeepClone()));
    }

    /// <summary>构造字符串属性。对应 typebox <c>Type.String</c>。</summary>
    public static JsonObject String(string? description = null)
    {
        var node = new JsonObject { ["type"] = "string" };
        if (description is not null) node["description"] = description;
        return node;
    }

    /// <summary>构造数值属性。对应 typebox <c>Type.Number</c>。</summary>
    public static JsonObject Number(string? description = null)
    {
        var node = new JsonObject { ["type"] = "number" };
        if (description is not null) node["description"] = description;
        return node;
    }

    /// <summary>构造布尔属性。对应 typebox <c>Type.Boolean</c>。</summary>
    public static JsonObject Boolean(string? description = null)
    {
        var node = new JsonObject { ["type"] = "boolean" };
        if (description is not null) node["description"] = description;
        return node;
    }

    /// <summary>构造数组属性。对应 typebox <c>Type.Array</c>。</summary>
    public static JsonObject Array(JsonNode items, string? description = null)
    {
        var node = new JsonObject { ["type"] = "array", ["items"] = items };
        if (description is not null) node["description"] = description;
        return node;
    }

    /// <summary>把属性标记为可选（typebox 的 <c>Type.Optional</c> 不写进 <c>required</c>，故此处为恒等）。</summary>
    public static JsonObject Optional(JsonObject property) => property;
}

/// <summary>
/// 以 JSON 字典承载的工具参数读取。对应 TS 里 <c>args</c> 作为 <c>Static&lt;TSchema&gt;</c> 的直接属性访问
/// （如 <c>args.path</c>）：C# 侧是 <see cref="IReadOnlyDictionary{TKey,TValue}"/> 的键访问 + 类型检查。
/// </summary>
public static class ToolArgs
{
    /// <summary>空参数。</summary>
    public static IReadOnlyDictionary<string, object?> Empty { get; } = new Dictionary<string, object?>();

    /// <summary>把任意承载归一为参数字典（null / 非字典时返回空字典）。</summary>
    public static IReadOnlyDictionary<string, object?> Object(object? args)
        => args as IReadOnlyDictionary<string, object?> ?? Empty;

    /// <summary>取必填字符串；缺失或类型不符时抛出，消息形状与 TS 的 schema 校验失败一致。</summary>
    public static string RequiredString(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null)
            throw new InvalidOperationException($"Invalid arguments: {name} is required");
        if (value is not string text)
            throw new InvalidOperationException($"Invalid arguments: {name} must be a string");
        return text;
    }

    /// <summary>取可选数值（JSON 数字可能是 <see cref="long"/> 或 <see cref="double"/>）。</summary>
    public static double? OptionalNumber(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return null;
        return value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            decimal m => (double)m,
            _ => throw new InvalidOperationException($"Invalid arguments: {name} must be a number"),
        };
    }

    /// <summary>取可选字符串。</summary>
    public static string? OptionalString(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return null;
        if (value is string text) return text;
        throw new InvalidOperationException($"Invalid arguments: {name} must be a string");
    }

    /// <summary>取可选布尔。</summary>
    public static bool? OptionalBool(IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return null;
        if (value is bool flag) return flag;
        throw new InvalidOperationException($"Invalid arguments: {name} must be a boolean");
    }

    /// <summary>取可选对象数组（元素为字典）。</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>>? OptionalObjectArray(
        IReadOnlyDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return null;
        if (value is not System.Collections.IEnumerable items || value is string)
            throw new InvalidOperationException($"Invalid arguments: {name} must be an array");
        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in items)
        {
            if (item is IReadOnlyDictionary<string, object?> dict) result.Add(dict);
            else throw new InvalidOperationException($"Invalid arguments: {name} items must be objects");
        }

        return result;
    }
}

/// <summary>
/// 文本结果的内容块构造辅助。
/// </summary>
public static class ToolContent
{
    /// <summary>单个文本内容块。</summary>
    public static IReadOnlyList<ContentBlock> Text(string text) => [new TextContent(text)];
}
