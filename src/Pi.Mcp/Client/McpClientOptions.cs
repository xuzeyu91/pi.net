using Pi.Mcp.Protocol;

namespace Pi.Mcp;

/// <summary>单次请求选项。对应 TS <c>McpRequestOptions</c>。</summary>
public sealed record McpRequestOptions
{
    /// <summary>共享的默认选项实例。</summary>
    public static readonly McpRequestOptions None = new();

    /// <summary>请求超时（毫秒；缺省 30s）。</summary>
    public long? TimeoutMs { get; init; }
}

/// <summary>客户端构造选项。对应 TS <c>McpClientOptions</c>（名称与版本必填）。</summary>
public sealed class McpClientOptions
{
    /// <summary>客户端实现名称（必填）。</summary>
    public required string Name { get; init; }

    /// <summary>客户端实现版本（必填）。</summary>
    public required string Version { get; init; }

    /// <summary>实现标题（可选展示名）。</summary>
    public string? Title { get; init; }

    /// <summary>客户端能力声明。</summary>
    public ClientCapabilities? Capabilities { get; init; }

    /// <summary>请求的协议版本（缺省为最新版本）。</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>请求超时（毫秒；缺省 30s）。</summary>
    public long? RequestTimeoutMs { get; init; }

    /// <summary>声明的根目录（roots/list 服务器请求的应答来源）。</summary>
    public IReadOnlyList<McpRoot>? Roots { get; init; }
}
