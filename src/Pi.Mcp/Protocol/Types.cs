using System.Text.Json.Nodes;

namespace Pi.Mcp.Protocol;

/// <summary>
/// MCP 协议版本常量。对应 TS <c>protocol/types.ts</c> 头部：
/// 客户端接受的服务端版本列表（旧版 SDK 服务器以自己的最新版本应答，因此保持接受）。
/// </summary>
public static class McpProtocolVersions
{
    public const string Latest = "2025-11-25";

    public static readonly IReadOnlyList<string> Supported =
    [
        Latest, "2025-06-18", "2025-03-26", "2024-11-05",
    ];

    public static bool IsSupported(string version) => Supported.Contains(version);
}

/// <summary>客户端/服务器实现信息。对应 TS <c>Implementation</c>。</summary>
public sealed record McpImplementation(string Name, string Version, string? Title = null);

/// <summary>客户端能力声明（能力集合的具体形态由服务器协商，松散承载）。对应 TS <c>ClientCapabilities</c>。</summary>
public sealed record ClientCapabilities(JsonObject? Raw = null)
{
    public bool HasRoots => Raw?.ContainsKey("roots") == true;
}

/// <summary>服务器能力声明。对应 TS <c>ServerCapabilities</c>。</summary>
public sealed record ServerCapabilities(JsonObject Raw);

/// <summary>MCP 工具描述。对应 TS <c>Tool</c>。</summary>
public sealed record McpTool(
    string Name,
    JsonObject InputSchema,
    string? Description = null,
    string? Title = null);

/// <summary>MCP 资源描述。对应 TS <c>Resource</c>（规范要求 name，但省略时以 uri 代替）。</summary>
public sealed record McpResource(string Uri, string? Name = null);

/// <summary>MCP 资源模板。对应 TS <c>ResourceTemplate</c>。</summary>
public sealed record McpResourceTemplate(string UriTemplate, string? Name = null);

/// <summary>initialize 的完整应答。对应 TS <c>InitializeResult</c>。</summary>
public sealed record InitializeResult(
    string ProtocolVersion,
    ServerCapabilities Capabilities,
    McpImplementation ServerInfo,
    string? Instructions = null);

/// <summary>工具调用结果内容块（松散承载）。对应 TS <c>ContentBlock</c> 联合。</summary>
public sealed record McpContentBlock(JsonObject Raw);

/// <summary>tools/call 的结果。对应 TS <c>CallToolResult</c>（仅返回 structuredContent 的服务器按规范补空 content）。</summary>
public sealed record CallToolResult(IReadOnlyList<McpContentBlock> Content, JsonObject? StructuredContent = null)
{
    /// <summary>便捷判断：结果是否为错误（isError=true）。</summary>
    public bool IsError { get; init; }
}

/// <summary>资源读取结果条目：text 或 blob 二选一。对应 TS <c>contents</c> 元素。</summary>
public sealed record McpResourceContents(string Uri, string? Text = null, string? Blob = null);

/// <summary>resources/read 的结果。对应 TS <c>ReadResourceResult</c>。</summary>
public sealed record ReadResourceResult(IReadOnlyList<McpResourceContents> Contents);

/// <summary>MCP 根目录声明（roots/list 应答项）。对应 TS <c>Root</c>。</summary>
public sealed record McpRoot(string Uri, string? Name = null);

/// <summary>
/// 协议类型与结果校验。对应 TS <c>client.ts</c> 里的 validateInitializeResult /
/// validateCallToolResult / validateReadResourceResult 等函数。
/// </summary>
public static class McpProtocol
{
    /// <summary>校验 initialize 应答的结构与字段类型。</summary>
    public static InitializeResult ValidateInitializeResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("protocolVersion", out var versionNode) && versionNode is JsonValue v1
            && v1.TryGetValue<string>(out var protocolVersion)
            && value.TryGetPropertyValue("capabilities", out var caps) && caps is JsonObject capabilities
            && value.TryGetPropertyValue("serverInfo", out var info) && info is JsonObject serverInfo
            && serverInfo.TryGetPropertyValue("name", out var nameNode) && nameNode is JsonValue { } nv
            && nv.TryGetValue<string>(out var serverName)
            && serverInfo.TryGetPropertyValue("version", out var versionNode2) && versionNode2 is JsonValue { } vv
            && vv.TryGetValue<string>(out var serverVersion))
        {
            string? instructions = null;
            if (value.TryGetPropertyValue("instructions", out var instr) && instr is JsonValue iv)
                iv.TryGetValue(out instructions);
            return new InitializeResult(
                protocolVersion,
                new ServerCapabilities(capabilities),
                new McpImplementation(serverName, serverVersion),
                instructions);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP initialize result");
    }

    /// <summary>校验 tools/call 结果；仅返回 structuredContent 的服务器补空 content。</summary>
    public static CallToolResult ValidateCallToolResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("content", out var content) && content is not JsonArray)
            throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP tools/call result");
        if (value.TryGetPropertyValue("structuredContent", out var structured) && structured is not JsonObject)
            throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP tools/call structured content");

        var blocks = (content as JsonArray)?.OfType<JsonObject>()
            .Select(b => new McpContentBlock(b)).ToList() ?? [];
        var isError = value.TryGetPropertyValue("isError", out var isErr) && isErr is JsonValue { } isErrV
            && isErrV.TryGetValue<bool>(out var isErrBool) && isErrBool;
        var structuredContent = value["structuredContent"] as JsonObject;
        return new CallToolResult(blocks, structuredContent) { IsError = isError };
    }

    /// <summary>校验 resources/read 结果。</summary>
    public static ReadResourceResult ValidateReadResourceResult(JsonObject value)
    {
        if (value.TryGetPropertyValue("contents", out var contents) && contents is JsonArray array)
        {
            var items = new List<McpResourceContents>();
            foreach (var item in array.OfType<JsonObject>())
            {
                if (item.TryGetPropertyValue("uri", out var uriNode) && uriNode is JsonValue { } uv
                    && uv.TryGetValue<string>(out var uri))
                {
                    var text = item["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
                    var blob = item["blob"] is JsonValue bv && bv.TryGetValue<string>(out var b) ? b : null;
                    if (text is not null || blob is not null)
                    {
                        items.Add(new McpResourceContents(uri, text, blob));
                        continue;
                    }
                }
                throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid contents in MCP resources/read result");
            }
            return new ReadResourceResult(items);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP resources/read result");
    }

    /// <summary>校验并提取一页列表结果（items 数组 + 可选 nextCursor；null/"" 视为无游标）。</summary>
    public static (IReadOnlyList<JsonObject> Items, string? NextCursor) ValidateListPage(
        string method, string key, JsonObject value, Func<JsonObject, bool> isItem)
    {
        if (value.TryGetPropertyValue(key, out var itemsNode) && itemsNode is JsonArray array)
        {
            var items = new List<JsonObject>();
            foreach (var item in array.OfType<JsonObject>())
            {
                if (!isItem(item))
                    throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid entry in MCP {method} result");
                items.Add(item);
            }
            string? nextCursor = null;
            if (value.TryGetPropertyValue("nextCursor", out var cursorNode))
            {
                // 部分服务器以 null 或 "" 结束分页：视为无游标。
                if (cursorNode is JsonValue { } cv && cv.TryGetValue<string>(out var c) && c.Length > 0)
                    nextCursor = c;
            }
            return (items, nextCursor);
        }
        throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid MCP {method} result");
    }

    /// <summary>工具条目校验：name 必填 + inputSchema 为对象。</summary>
    public static bool IsTool(JsonObject tool)
        => tool.TryGetPropertyValue("name", out var name) && name is JsonValue
            && tool.TryGetPropertyValue("inputSchema", out var schema) && schema is JsonObject;

    /// <summary>资源条目校验：uri 必填，name 可省略。</summary>
    public static bool IsResource(JsonObject resource)
        => resource.TryGetPropertyValue("uri", out var uri) && uri is JsonValue;

    /// <summary>资源模板条目校验：uriTemplate 必填。</summary>
    public static bool IsResourceTemplate(JsonObject template)
        => template.TryGetPropertyValue("uriTemplate", out var uriTemplate) && uriTemplate is JsonValue;

    /// <summary>把JsonObject 转为 McpTool。</summary>
    public static McpTool ToTool(JsonObject tool)
    {
        var name = (tool["name"] as JsonValue)?.GetValue<string>() ?? "";
        var description = (tool["description"] as JsonValue)?.GetValue<string>();
        var title = (tool["title"] as JsonValue)?.GetValue<string>();
        return new McpTool(name, (tool["inputSchema"] as JsonObject)!, description, title);
    }

    /// <summary>把 JsonObject 转为 McpResource（name 缺省取 uri）。</summary>
    public static McpResource ToResource(JsonObject resource)
    {
        var uri = (resource["uri"] as JsonValue)?.GetValue<string>() ?? "";
        var name = (resource["name"] as JsonValue)?.GetValue<string>() ?? uri;
        return new McpResource(uri, name);
    }

    /// <summary>把 JsonObject 转为 McpResourceTemplate（name 缺省取 uriTemplate）。</summary>
    public static McpResourceTemplate ToResourceTemplate(JsonObject template)
    {
        var uriTemplate = (template["uriTemplate"] as JsonValue)?.GetValue<string>() ?? "";
        var name = (template["name"] as JsonValue)?.GetValue<string>() ?? uriTemplate;
        return new McpResourceTemplate(uriTemplate, name);
    }
}
