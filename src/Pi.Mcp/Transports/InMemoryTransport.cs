using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>
/// 进程内成对传输（测试与同进程服务）。对应 TS <c>InMemoryTransport</c>：
/// send 以"结构化拷贝"语义投递给对端（C# 用 JSON 序列化往返实现隔离拷贝，
/// 与 JS structuredClone 的隔离效果等价），投递通过队列异步进行（对齐 queueMicrotask）。
/// </summary>
public sealed class InMemoryTransport : TransportEvents
{
    private InMemoryTransport? _peer;
    private bool _started;
    private bool _closed;
    private readonly Channel<JsonRpcMessage> _inbox = Channel.CreateUnbounded<JsonRpcMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>配对连接对端。每个传输只能配对一次。</summary>
    public void ConnectPeer(InMemoryTransport peer)
    {
        if (_peer is not null)
            throw new InvalidOperationException("In-memory MCP transport already has a peer");
        _peer = peer;
        _ = PumpInboxAsync();
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) throw new McpConnectionClosedError();
        _started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!_started || _closed) throw new McpConnectionClosedError();
        var peer = _peer;
        if (peer is not { _started: true } || peer._closed)
            throw new McpConnectionClosedError("In-memory MCP peer is not connected");

        // structuredClone 语义：序列化往返，防止两侧共享可变引用。
        var copy = JsonRpc.Parse(JsonNode.Parse(JsonRpc.Serialize(message).ToJsonString()));
        // 对齐 queueMicrotask：异步投递，发送方不阻塞在对端监听器上。
        peer._inbox.Writer.TryWrite(copy);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        _closed = true;
        _inbox.Writer.TryComplete();
        EmitClose();
        if (_peer is not null) await _peer.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>模拟传输层失败（测试用）。对应 TS 的公开 emitError 覆写。</summary>
    public void SimulateError(Exception error) => EmitError(error);

    private async Task PumpInboxAsync()
    {
        await foreach (var message in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_closed) return;
            EmitMessage(message);
        }
    }
}

/// <summary>创建一对已互相连接的进程内传输。对应 TS <c>createInMemoryTransportPair</c>。</summary>
public static class InMemoryTransportPair
{
    public static (InMemoryTransport Client, InMemoryTransport Server) Create()
    {
        var client = new InMemoryTransport();
        var server = new InMemoryTransport();
        client.ConnectPeer(server);
        server.ConnectPeer(client);
        return (client, server);
    }
}
