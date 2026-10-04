using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>传输层默认消息字节上限（16 MiB）。</summary>
public static class TransportDefaults
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;
}

/// <summary>消息监听器。</summary>
public delegate void McpTransportMessageListener(JsonRpcMessage message);

/// <summary>错误监听器。</summary>
public delegate void McpTransportErrorListener(Exception error);

/// <summary>关闭监听器。</summary>
public delegate void McpTransportCloseListener();

/// <summary>
/// MCP 传输抽象。对应 TS <c>McpTransport</c>：start/send/close + 消息/错误/关闭三类监听。
/// </summary>
public interface IMcpTransport
{
    /// <summary>启动传输（建立子进程 / 连接等）。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>发送一条 JSON-RPC 消息。</summary>
    Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);

    /// <summary>关闭传输。</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>注册消息监听器，返回取消订阅委托。</summary>
    IDisposable OnMessage(McpTransportMessageListener listener);

    /// <summary>注册错误监听器，返回取消订阅委托。</summary>
    IDisposable OnError(McpTransportErrorListener listener);

    /// <summary>注册关闭监听器，返回取消订阅委托。</summary>
    IDisposable OnClose(McpTransportCloseListener listener);

    /// <summary>握手完成后由客户端回设协商出的协议版本（可空能力）。</summary>
    void SetProtocolVersion(string version) { }
}

/// <summary>
/// 监听器登记与分发（close 事件至多触发一次）。对应 TS <c>TransportEvents</c> 基类。
/// </summary>
public abstract class TransportEvents : IMcpTransport
{
    private readonly object _lock = new();
    private readonly HashSet<McpTransportMessageListener> _messageListeners = [];
    private readonly HashSet<McpTransportErrorListener> _errorListeners = [];
    private readonly HashSet<McpTransportCloseListener> _closeListeners = [];
    private bool _closeEmitted;

    public abstract Task StartAsync(CancellationToken cancellationToken = default);
    public abstract Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);
    public abstract Task CloseAsync(CancellationToken cancellationToken = default);

    public IDisposable OnMessage(McpTransportMessageListener listener)
    {
        lock (_lock) _messageListeners.Add(listener);
        return new Unsubscriber(() => { lock (_lock) _messageListeners.Remove(listener); });
    }

    public IDisposable OnError(McpTransportErrorListener listener)
    {
        lock (_lock) _errorListeners.Add(listener);
        return new Unsubscriber(() => { lock (_lock) _errorListeners.Remove(listener); });
    }

    public IDisposable OnClose(McpTransportCloseListener listener)
    {
        lock (_lock) _closeListeners.Add(listener);
        return new Unsubscriber(() => { lock (_lock) _closeListeners.Remove(listener); });
    }

    /// <summary>分发消息给全部监听器。</summary>
    protected void EmitMessage(JsonRpcMessage message)
    {
        McpTransportMessageListener[] snapshot;
        lock (_lock) snapshot = [.. _messageListeners];
        foreach (var listener in snapshot) listener(message);
    }

    /// <summary>分发错误给全部监听器。</summary>
    protected void EmitError(Exception error)
    {
        McpTransportErrorListener[] snapshot;
        lock (_lock) snapshot = [.. _errorListeners];
        foreach (var listener in snapshot) listener(error);
    }

    /// <summary>发出关闭事件（至多一次）。</summary>
    protected void EmitClose()
    {
        McpTransportCloseListener[] snapshot;
        lock (_lock)
        {
            if (_closeEmitted) return;
            _closeEmitted = true;
            snapshot = [.. _closeListeners];
        }
        foreach (var listener in snapshot) listener();
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
