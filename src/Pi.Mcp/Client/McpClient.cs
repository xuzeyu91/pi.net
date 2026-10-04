using Pi.Mcp.Protocol;

namespace Pi.Mcp;

/// <summary>
/// MCP 客户端会话。对应 TS <c>client.ts</c>：initialize 握手、工具/资源列举与调用、
/// 通知处理、请求取消、超时与重连。传输层（stdio / streamable-http / in-memory）
/// 在 transports.ts，将于后续会话接上。
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>连接并完成 initialize 握手（能力协商）。</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("client.ts 完整实现：下一会话按 stdio 传输优先接通。");

    /// <summary>列举服务器提供的工具（tools/list）。</summary>
    public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("随 client.ts 一起移植。");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
