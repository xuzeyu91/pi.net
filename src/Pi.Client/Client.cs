using System.Net.Sockets;
using System.Text.Json.Nodes;
using Pi.Protocol;

namespace Pi.Client;

/// <summary>客户端连接错误。对应 packages/client/src/errors.ts。</summary>
public sealed class DisconnectedError(string message) : Exception(message);

public sealed class ClientDisposedError(string message) : Exception(message);

/// <summary>服务器回传的业务错误（ResponseOk=false）。对应 TS <c>ServerError</c>。</summary>
public sealed class ClientServerError(string code, string message) : Exception($"[{code}] {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// RPC 客户端。对应 packages/client/src/client.ts 的核心语义：
/// hello 握手 → request/response 按_id_配对、超时与取消（cancel envelope）、
/// 服务器主动推送的 service_update 通知、连接状态跟踪。
/// </summary>
public sealed class RpcClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _requestTimeout;
    private readonly object _lock = new();

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ClientState _state = ClientState.Disconnected;
    private long _nextRequestId = 1;
    private string? _serverId;
    private readonly Dictionary<string, PendingRequest> _pending = [];
    private readonly List<Action<ServerMessage.ServiceEvent>> _serviceEventListeners = [];
    private CancellationTokenSource? _readCts;

    /// <param name="host">服务器主机。</param>
    /// <param name="port">服务器端口。</param>
    /// <param name="requestTimeout">单请求超时（缺省 30s）。</param>
    public RpcClient(string host, int port, TimeSpan? requestTimeout = null)
    {
        _host = host;
        _port = port;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>连接状态。</summary>
    public ClientState State => _state;

    /// <summary>握手拿到的服务器 id。</summary>
    public string? ServerId => _serverId;

    /// <summary>订阅 service_update 推送，返回取消订阅委托。</summary>
    public IDisposable OnServiceEvent(Action<ServerMessage.ServiceEvent> listener)
    {
        _serviceEventListeners.Add(listener);
        return new Unsubscriber(() => _serviceEventListeners.Remove(listener));
    }

    /// <summary>
    /// 连接并完成 hello 握手。对应 TS <c>client.connect()</c>；
    /// hello_error 时抛出 <see cref="ClientServerError"/>。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state != ClientState.Disconnected)
            throw new InvalidOperationException($"Cannot connect client in {_state} state");
        _state = ClientState.Connecting;
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();
            _readCts = new CancellationTokenSource();
            _ = PumpIncomingAsync(_readCts.Token);

            var helloId = "hello";
            var pendingHello = RegisterPending(helloId);
            await SendAsync(new ClientMessage.Hello(ProtocolVersion.Current)).ConfigureAwait(false);
            var completed = await Task.WhenAny(pendingHello.Completion.Task,
                Task.Delay(_requestTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != pendingHello.Completion.Task)
                throw new DisconnectedError("Handshake timed out");
            var hello = await pendingHello.Completion.Task.ConfigureAwait(false);
            _serverId = hello switch
            {
                ServerMessage.Hello ok => ok.ServerId,
                ServerMessage.HelloError error => throw new ClientServerError(
                    error.Error.Code, error.Error.Message),
                _ => throw new DisconnectedError("Unexpected handshake response"),
            };
            _state = ClientState.Connected;
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>发起一次调用：{ method, params } 载荷 + 请求 id，等待 Response。对应 TS <c>call()</c>。</summary>
    public async Task<object?> CallAsync(string method, JsonObject? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_state != ClientState.Connected)
            throw new DisconnectedError("RPC client is not connected");
        var id = $"req-{_nextRequestId++}";
        var pending = RegisterPending(id);
        var envelope = new ClientMessage.Request(id,
            new RpcTarget.ServerTarget(_serverId ?? ""),
            new JsonObject { ["method"] = method, ["params"] = parameters });
        await SendAsync(envelope).ConfigureAwait(false);

        var completed = await Task.WhenAny(pending.Completion.Task,
            Task.Delay(_requestTimeout, cancellationToken)).ConfigureAwait(false);
        if (completed == pending.Completion.Task)
            return await pending.Completion.Task.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            // 通知服务器取消。
            await SendAsync(new ClientMessage.Cancel(id, envelope.Target)).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        throw new DisconnectedError($"Request {method} timed out");
    }

    /// <summary>关闭连接并拒绝全部挂起请求。</summary>
    public async Task CloseAsync()
    {
        _readCts?.Cancel();
        List<PendingRequest> pending;
        lock (_lock)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var request in pending)
            request.Completion.TrySetException(new DisconnectedError("Connection closed"));
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
        _state = ClientState.Disconnected;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ---------- 内部 ----------

    private PendingRequest RegisterPending(string id)
    {
        var pending = new PendingRequest();
        lock (_lock) _pending[id] = pending;
        return pending;
    }

    private async Task SendAsync(ClientMessage message)
    {
        var stream = _stream ?? throw new DisconnectedError("RPC client is not connected");
        await stream.WriteAsync(ProtocolCodec.EncodeClientMessage(message)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>入站泵：帧解码 → 服务端消息分发（响应配对 / service_update 推送）。</summary>
    private async Task PumpIncomingAsync(CancellationToken cancellationToken)
    {
        // ProtocolCodec 解码器内含帧解码，直接喂原始网络字节。
        var messageDecoder = new ProtocolCodec.ServerMessageDecoder();
        var buffer = new byte[8192];
        try
        {
            var stream = _stream!;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                foreach (var message in messageDecoder.Push(buffer[..read]))
                    HandleMessage(message);
            }
        }
        catch { /* 连接断开：挂起请求由 CloseAsync 兜底拒绝 */ }
    }

    /// <summary>以结果 resolve 挂起请求（含握手）。</summary>
    private void ResolvePending(string id, object? result)
    {
        PendingRequest? pending;
        lock (_lock) _pending.TryGetValue(id, out pending);
        pending?.Completion.TrySetResult(result);
    }

    /// <summary>以错误 reject 挂起请求。</summary>
    private void ResolvePendingError(string id, Exception error)
    {
        PendingRequest? pending;
        lock (_lock) _pending.TryGetValue(id, out pending);
        pending?.Completion.TrySetException(error);
    }

    private void HandleMessage(ServerMessage message)
    {
        switch (message)
        {
            case ServerMessage.ResponseOk ok:
            {
                PendingRequest? pending;
                lock (_lock) _pending.TryGetValue(ok.Id, out pending);
                pending?.Completion.TrySetResult(ok.Result);
                break;
            }
            case ServerMessage.ResponseError error:
            {
                PendingRequest? pending;
                lock (_lock) _pending.TryGetValue(error.Id, out pending);
                pending?.Completion.TrySetException(
                    new ClientServerError(error.Error.Code, error.Error.Message));
                break;
            }
            case ServerMessage.ServiceEvent serviceEvent:
                foreach (var listener in _serviceEventListeners.ToList()) listener(serviceEvent);
                break;
            case ServerMessage.Hello hello:
                _serverId = hello.ServerId;
                // 握手响应：resolve 等待中的 hello 请求。
                ResolvePending("hello", hello);
                break;
            case ServerMessage.HelloError helloError:
                ResolvePendingError("hello",
                    new ClientServerError(helloError.Error.Code, helloError.Error.Message));
                break;
        }
    }

    private PendingRequest? FindPending(string id)
    {
        lock (_lock) return _pending.GetValueOrDefault(id);
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

/// <summary>客户端连接状态。对应 TS <c>ConnectionState</c>。</summary>
public enum ClientState
{
    Disconnected,
    Connecting,
    Connected,
    Closed,
}
