using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using Pi.Protocol;

namespace Pi.Server;

/// <summary>服务端错误。对应 packages/server/src/errors.ts。</summary>
public sealed class ServerError(string message) : Exception(message);

/// <summary>
/// 服务器请求处理器：方法名 → 处理函数。参数为 CBOR 解码出的 call 字典
/// （plain JSON 值模型，对应 TS 里 opaque 的 call 对象）；返回结果值，
/// 抛异常则回错误响应。
/// </summary>
public delegate Task<object?> RpcRequestHandler(Dictionary<string, object?>? call);

/// <summary>
/// 连接级 RPC 服务器。对应 packages/server/src/server.ts 的核心语义：
/// 监听器接受字节流 → hello 握手（协议版本校验，失败回 hello_error）→
/// request 分发到处理器并回 response → cancel 触发取消令牌。
/// 服务更新（service_update）经 <see cref="SendServiceEventAsync"/> 主动推送。
/// </summary>
public sealed class RpcServer
{
    private readonly Dictionary<string, RpcRequestHandler> _handlers = [];
    private readonly List<(TcpClient Client, CancellationTokenSource Cts)> _connections = [];

    /// <summary>注册方法处理器，返回取消注册委托。</summary>
    public IDisposable RegisterHandler(string method, RpcRequestHandler handler)
    {
        _handlers[method] = handler;
        return new Unsubscriber(() => _handlers.Remove(method));
    }

    /// <summary>在 TCP 端点上接受连接并为每条连接跑会话循环（阻塞至 stop）。</summary>
    public async Task ListenAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_connections) _connections.Add((tcpClient, cts));
                _ = Task.Run(() => ServeConnectionAsync(tcpClient, cts.Token), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>向全部已握手连接推送 service_update（对应 TS 的 ServiceEventEnvelope 路由）。</summary>
    public async Task SendServiceEventAsync(string subscriptionId, object? update, CancellationToken cancellationToken = default)
    {
        var envelope = new ServerMessage.ServiceEvent(subscriptionId, update);
        var bytes = ProtocolCodec.EncodeServerMessage(envelope);
        List<NetworkStream> streams;
        lock (_connections)
            streams = _connections.Select(c => c.Client.GetStream()).ToList();
        foreach (var stream in streams)
        {
            try { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
            catch { /* 掉线的连接由会话循环自行清理 */ }
        }
    }

    /// <summary>单连接会话循环：帧解码 → 消息校验 → 握手/分发。</summary>
    private async Task ServeConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var stream = tcpClient.GetStream();
        // 注意：ProtocolCodec 的解码器内含帧解码，直接喂原始网络字节。
        var messageDecoder = new ProtocolCodec.ClientMessageDecoder();
        var helloDone = false;
        var cancellations = new Dictionary<string, CancellationTokenSource>();

        try
        {
            var buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                {
                    IReadOnlyList<ClientMessage> batch;
                    try
                    {
                        batch = messageDecoder.Push(buffer[..read]);
                    }
                    catch (ProtocolValidationError error)
                    {
                        await WriteServerMessageAsync(stream,
                            new ServerMessage.HelloError(new ProtocolError("invalid_message", error.Message)),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    foreach (var message in batch)
                    {
                        switch (message)
                        {
                        case ClientMessage.Hello hello when !helloDone:
                            if (ProtocolCodec.IsSupportedProtocolVersion(hello.Version))
                            {
                                helloDone = true;
                                await WriteServerMessageAsync(stream,
                                    new ServerMessage.Hello(ProtocolVersion.Current, NewServerId()),
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await WriteServerMessageAsync(stream,
                                    new ServerMessage.HelloError(new ProtocolError(
                                        "unsupported_version", $"Unsupported protocol version {hello.Version}")),
                                    cancellationToken).ConfigureAwait(false);
                                return;
                            }
                            break;

                        case ClientMessage.Hello:
                            // 重复 hello：直接断开。
                            return;

                        case ClientMessage.Request request when helloDone:
                        {
                            // call 载荷约定：{ method, params }（CBOR 解码的 plain JSON 值）。
                            var call = request.Call as Dictionary<string, object?>;
                            var method = call is not null
                                && call.TryGetValue("method", out var m)
                                && m is string methodText ? methodText : null;
                            var parameters = call is not null
                                && call.TryGetValue("params", out var p)
                                    ? p as Dictionary<string, object?> : null;
                            if (method is null)
                            {
                                await WriteResponseAsync(stream, request.Id, new ProtocolError(
                                    "invalid_call", "Request call must carry a method"), cancellationToken)
                                    .ConfigureAwait(false);
                                break;
                            }
                            var cts = new CancellationTokenSource();
                            cancellations[request.Id] = cts;
                            try
                            {
                                // 服务器当前注册的处理器表由实例闭包提供（见 RegisterHandler）。
                                var result = await DispatchAsync(method, parameters).ConfigureAwait(false);
                                await WriteResponseAsync(stream, request.Id, result, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                await WriteResponseAsync(stream, request.Id, new ProtocolError(
                                    "cancelled", "Request was cancelled"), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception error)
                            {
                                await WriteResponseAsync(stream, request.Id, new ProtocolError(
                                    "handler_error", error.Message), cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                cancellations.Remove(request.Id);
                            }
                            break;
                        }

                        case ClientMessage.Cancel cancel when helloDone:
                            if (cancellations.TryGetValue(cancel.Id, out var pendingCts))
                                await pendingCts.CancelAsync().ConfigureAwait(false);
                            break;
                        }
                    }
                }
            }
        }
        catch (IOException) { /* 连接断开 */ }
        catch (ObjectDisposedException) { }
        finally
        {
            foreach (var cts in cancellations.Values) await cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<object?> DispatchAsync(string method, Dictionary<string, object?>? parameters)
    {
        if (!_handlers.TryGetValue(method, out var handler))
            throw new InvalidOperationException($"No handler for method: {method}");
        return await handler(parameters).ConfigureAwait(false);
    }

    private static string NewServerId()
        => Guid.NewGuid().ToString("D");

    private static async Task WriteServerMessageAsync(NetworkStream stream, ServerMessage message, CancellationToken ct)
        => await stream.WriteAsync(ProtocolCodec.EncodeServerMessage(message), ct).ConfigureAwait(false);

    private static Task WriteResponseAsync(NetworkStream stream, string requestId, object? result, CancellationToken ct)
        => WriteServerMessageAsync(stream, new ServerMessage.ResponseOk(requestId, result), ct);

    private static Task WriteResponseAsync(NetworkStream stream, string requestId, ProtocolError error, CancellationToken ct)
        => WriteServerMessageAsync(stream, new ServerMessage.ResponseError(requestId, error), ct);

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
