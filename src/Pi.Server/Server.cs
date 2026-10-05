using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Protocol;

namespace Pi.Server;

/// <summary>
/// RPC 服务端主循环。对应 TS <c>Server&lt;TMetadata&gt;</c>（server.ts）：
/// 监听器装配 → 每连接握手（版本校验 + 服务端服务端点附加 + hello 回执）→
/// 目标路由（服务端域 / 会话域）→ 订阅编码器 → 排空关闭。
/// </summary>
public sealed class Server<TMetadata> where TMetadata : ISessionMetadata
{
    private const int DefaultHandshakeTimeoutMs = 5_000;
    private const long MaxUInt32 = 0xffff_ffffL;
    private const int MaxTimerDelayMs = 2_147_483_647;

    private readonly IServerHost<TMetadata> _host;
    private readonly IReadOnlyList<IServerListener> _listeners;
    private readonly int _maxFrameLength;
    private readonly int _handshakeTimeoutMs;
    private readonly Action<int>? _onConnectionCountChanged;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private readonly HashSet<ConnectionState> _connections = new(ReferenceEqualityComparer.Instance);
    private readonly SessionRouter<TMetadata> _sessions;
    private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    private Task? _closePromise;
    private bool _closedSettled;
    private Task<Server<TMetadata>>? _startPromise;
    private bool _started;

    public Server(IServerHost<TMetadata> host, ServerOptions options)
    {
        var (maxFrameLength, handshakeTimeoutMs) = ResolveOptions(options);
        _host = host;
        _listeners = options.Listeners;
        ServerId = options.ServerId;
        _maxFrameLength = maxFrameLength;
        _handshakeTimeoutMs = handshakeTimeoutMs;
        _onConnectionCountChanged = options.OnConnectionCountChanged;
        _onError = options.OnError;
        _sessions = new SessionRouter<TMetadata>(new SessionRouterOptions<TMetadata>
        {
            Host = host,
            ServerId = ServerId,
            IsClosing = () => _closing,
            PublishAttachment = (client, route, _) => SendMessageAsync((ConnectionState)client,
                new ServerMessage.Attachment(route)),
            ReportError = ReportError,
        });
        _ = _closedSignal.Task.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public string ServerId { get; }

    /// <summary>关闭完成时完成；监听器或路由会话清理失败时以异常完成。对应 TS <c>closed</c>。</summary>
    public Task Closed => _closedSignal.Task;

    /// <summary>启动全部监听器。对应 TS <c>start()</c>。</summary>
    public Task<Server<TMetadata>> StartAsync()
    {
        if (_started) return Task.FromException<Server<TMetadata>>(new InvalidOperationException("Server is already started"));
        if (_startPromise is not null)
        {
            return Task.FromException<Server<TMetadata>>(new InvalidOperationException("Server is already starting"));
        }
        if (_closing) return Task.FromException<Server<TMetadata>>(new InvalidOperationException("Server is closing or closed"));
        _startPromise = StartInternalAsync();
        return _startPromise;
    }

    private async Task<Server<TMetadata>> StartInternalAsync()
    {
        var started = new List<IServerListener>();
        try
        {
            foreach (var listener in _listeners)
            {
                await listener.StartAsync(connection => Accept(connection)).ConfigureAwait(false);
                started.Add(listener);
            }
            _started = true;
            return this;
        }
        catch (Exception error)
        {
            _closing = true;
            var cleanupErrors = new List<Exception>();
            cleanupErrors.AddRange(await AllSettledAsync(started.Select(listener => listener.CloseAsync()))
                .ConfigureAwait(false));
            try
            {
                await CloseServerStateAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            if (cleanupErrors.Count > 0)
            {
                var failure = new AggregateException("Server startup and cleanup failed",
                    [error, .. cleanupErrors]);
                SettleClosed(failure);
                throw failure;
            }
            SettleClosed();
            throw;
        }
        finally
        {
            _startPromise = null;
        }
    }

    /// <summary>接受一条已授权连接并返回其处理器。对应 TS <c>accept(connection)</c>。</summary>
    public IByteConnectionHandler Accept(IByteConnection connection)
    {
        if (_closing)
        {
            _ = CloseConnectionAsync(connection);
            return new NoopConnectionHandler(ReportError);
        }

        var state = new ConnectionState
        {
            Connection = connection,
            Decoder = new ProtocolCodec.ClientMessageDecoder(new FrameDecoderOptions(_maxFrameLength)),
            ServiceStateEncoders = [],
        };
        var timeout = new CancellationTokenSource();
        state.HandshakeTimeout = timeout;
        _ = Task.Delay(_handshakeTimeoutMs, timeout.Token).ContinueWith(
            _ => FailProtocolAsync(state, new ProtocolError("invalid_request", "Handshake timeout")),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        lock (_gate) _connections.Add(state);
        NotifyConnectionCountChanged();

        return new ConnectionHandler(
            chunk => Receive(state, chunk),
            () => TransportClosed(state),
            error =>
            {
                ReportError(error);
                _ = CloseConnectionAsync(connection).ContinueWith(_ => Disconnect(state), TaskScheduler.Default);
            });
    }

    /// <summary>排空并关闭服务端（幂等）。对应 TS <c>close()</c>。</summary>
    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closePromise is not null) return _closePromise;
            _closing = true;
            _closePromise = CloseInternalAsync();
            return _closePromise;
        }
    }

    private async Task CloseInternalAsync()
    {
        var starting = _startPromise;
        if (starting is not null)
        {
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch
            {
                // 对齐 TS 的 starting.catch(() => {})。
            }
        }

        var errors = new List<Exception>();
        errors.AddRange(await AllSettledAsync(_listeners.Select(listener => listener.CloseAsync()))
            .ConfigureAwait(false));
        try
        {
            await CloseServerStateAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        _started = false;

        if (errors.Count > 0)
        {
            Exception failure = errors.Count == 1 ? errors[0] : new AggregateException("Server shutdown failed", errors);
            SettleClosed(failure);
            throw failure;
        }
        SettleClosed();
    }

    private void Receive(ConnectionState state, byte[] chunk)
    {
        if (state.IsTerminal) return;
        IReadOnlyList<ClientMessage> messages;
        try
        {
            messages = state.Decoder.Push(chunk);
        }
        catch (Exception error)
        {
            _ = FailProtocolAsync(state, ToProtocolError(error));
            return;
        }
        foreach (var message in messages)
        {
            if (state.IsTerminal) return;
            DispatchMessage(state, message);
        }
    }

    private void DispatchMessage(ConnectionState state, ClientMessage message)
    {
        if (state.Stage == ConnectionStage.AwaitingHello)
        {
            if (message is not ClientMessage.Hello hello)
            {
                _ = FailProtocolAsync(state, new ProtocolError("invalid_request",
                    "The first client message must be hello"));
                return;
            }
            state.Stage = ConnectionStage.Handshaking;
            state.Handshake = HandleHandshakeOutcomeAsync(state, FinishHandshakeAsync(state, hello));
            return;
        }

        if (message is ClientMessage.Hello)
        {
            _ = FailProtocolAsync(state, new ProtocolError("invalid_request",
                "hello may only be sent as the first message"));
            return;
        }

        if (state.Stage == ConnectionStage.Ready)
        {
            DispatchReady(state, message);
            return;
        }
        if (state.Stage != ConnectionStage.Handshaking) return;
        var handshake = state.Handshake;
        if (handshake is null) return;
        _ = handshake.ContinueWith(_ =>
        {
            if (state.Stage != ConnectionStage.Ready || state.Disconnected) return;
            DispatchReady(state, message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DispatchReady(ConnectionState state, ClientMessage message)
    {
        switch (message)
        {
            case ClientMessage.Cancel cancel:
                HandleCancel(state, cancel);
                break;
            case ClientMessage.Request request:
                _ = HandleRequestAsync(state, request);
                break;
        }
    }

    private async Task HandleHandshakeOutcomeAsync(ConnectionState state, Task handshake)
    {
        try
        {
            await handshake.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await FailProtocolAsync(state, ToProtocolError(error)).ConfigureAwait(false);
        }
    }

    private async Task FinishHandshakeAsync(ConnectionState state, ClientMessage.Hello hello)
    {
        if (!ProtocolCodec.IsSupportedProtocolVersion(hello.Version))
        {
            await FailProtocolAsync(state, new ProtocolError("version",
                $"Unsupported protocol version {hello.Version}; expected {ProtocolVersion.Current}"))
                .ConfigureAwait(false);
            return;
        }

        if (_closing || state.Disconnected || state.Stage != ConnectionStage.Handshaking || state.Connection.Closed)
        {
            return;
        }
        var services = await _host.ServerServices.AttachClientAsync(
            new ServerPresentation(state, _sessions), Context.Todo).ConfigureAwait(false);
        if (_closing || state.Disconnected || state.Stage != ConnectionStage.Handshaking || state.Connection.Closed)
        {
            await services.ReleaseAsync(Context.Todo).ConfigureAwait(false);
            return;
        }
        state.ServerServices = services;
        var sent = await SendMessageAsync(state,
            new ServerMessage.Hello(ProtocolVersion.Current, ServerId)).ConfigureAwait(false);
        if (sent && !state.Disconnected && state.Stage == ConnectionStage.Handshaking)
        {
            state.Stage = ConnectionStage.Ready;
            ClearHandshakeTimeout(state);
        }
    }

    private void HandleCancel(ConnectionState state, ClientMessage.Cancel envelope)
    {
        if (envelope.Target is not RpcTarget target || target is not RpcTarget.SessionTarget and not RpcTarget.ServerTarget)
        {
            return;
        }
        if (TargetServerId(target) != ServerId) return;
        if (state.ActiveRequests.TryGetValue(envelope.Id, out var active) && SameTarget(active.Target, target))
        {
            active.Controller.Cancel();
        }
    }

    private async Task HandleRequestAsync(ConnectionState state, ClientMessage.Request envelope)
    {
        if (state.ActiveRequests.ContainsKey(envelope.Id))
        {
            await SendMessageAsync(state, new ServerMessage.ResponseError(envelope.Id,
                new ProtocolError("invalid_request", "Request ID is already active"))).ConfigureAwait(false);
            return;
        }

        ServiceCall call;
        try
        {
            call = ServiceWire.ParseServiceCall(envelope.Call);
        }
        catch
        {
            await SendMessageAsync(state, new ServerMessage.ResponseError(envelope.Id,
                new ProtocolError("invalid_request", "Invalid service call"))).ConfigureAwait(false);
            return;
        }

        var controller = new CancellationTokenSource();
        var active = new ActiveRequest(controller, envelope.Target);
        state.ActiveRequests[envelope.Id] = active;
        var context = Context.Todo.WithValue(Context.AbortSignalKey, (CancellationToken?)controller.Token);
        var control = ServiceWire.DecodeServiceControlCall(call);
        var subscribing = control as ServiceControlCall.Subscribe;
        var pendingUpdates = new List<ServiceProviderUpdate>();
        var subscriptionReady = subscribing is null;
        var installedSubscriptionEncoder = false;
        var responded = false;

        ServiceUpdatePublisher publish = async (subscriptionId, update, _) =>
        {
            if (subscribing is not null && subscriptionId == subscribing.SubscriptionId && !subscriptionReady)
            {
                lock (pendingUpdates) pendingUpdates.Add(update);
                return;
            }
            await SendServiceUpdateAsync(state, subscriptionId, update).ConfigureAwait(false);
        };

        try
        {
            if (TargetServerId(envelope.Target) != ServerId) throw new WrongServerError();
            if (subscribing is not null && state.ServiceStateEncoders.ContainsKey(subscribing.SubscriptionId))
            {
                throw new ProtocolValidationError($"Duplicate service subscription {subscribing.SubscriptionId}");
            }

            object? result;
            if (envelope.Target is RpcTarget.SessionTarget)
            {
                result = await _sessions.ExecuteServiceCallAsync(call, envelope.Target, state, publish, context)
                    .ConfigureAwait(false);
            }
            else if (state.ServerServices is not null)
            {
                result = await state.ServerServices.InvokeServiceAsync(call, publish, context).ConfigureAwait(false);
            }
            else
            {
                throw new ProtocolValidationError($"Unknown service member {call.ServiceId}.{call.Member}");
            }

            if (subscribing is not null)
            {
                if (result is null)
                {
                    throw new ProtocolValidationError("Service subscription did not return a snapshot");
                }
                var wire = ServiceWire.ParseServiceSubscriptionSnapshot(result);
                var stateEncoder = new ServiceStateEncoder();
                result = stateEncoder.EncodeSnapshot(new ServiceStateDecoder().DecodeSnapshot(wire));
                state.ServiceStateEncoders[subscribing.SubscriptionId] = stateEncoder;
                installedSubscriptionEncoder = true;
            }
            else if (control is ServiceControlCall.Unsubscribe unsubscribe)
            {
                state.ServiceStateEncoders.Remove(unsubscribe.SubscriptionId);
            }

            await SendMessageAsync(state, result is null
                ? new ServerMessage.ResponseOk(envelope.Id, null)
                : new ServerMessage.ResponseOk(envelope.Id, result)).ConfigureAwait(false);
            responded = true;

            if (subscribing is not null)
            {
                while (true)
                {
                    ServiceProviderUpdate? pending = null;
                    lock (pendingUpdates)
                    {
                        if (pendingUpdates.Count > 0)
                        {
                            pending = pendingUpdates[0];
                            pendingUpdates.RemoveAt(0);
                        }
                    }
                    if (pending is null) break;
                    await SendServiceUpdateAsync(state, subscribing.SubscriptionId, pending).ConfigureAwait(false);
                }
                subscriptionReady = true;
            }
        }
        catch (Exception error)
        {
            if (subscribing is not null && installedSubscriptionEncoder && !responded)
            {
                state.ServiceStateEncoders.Remove(subscribing.SubscriptionId);
            }
            if (responded)
            {
                ReportError(error);
                await CloseConnectionAsync(state.Connection).ConfigureAwait(false);
                Disconnect(state);
            }
            else
            {
                var protocolError = controller.IsCancellationRequested
                    ? new ProtocolError("cancelled", "RPC request cancelled")
                    : ToProtocolError(error);
                await SendMessageAsync(state, new ServerMessage.ResponseError(envelope.Id, protocolError))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (state.ActiveRequests.TryGetValue(envelope.Id, out var current)
                && ReferenceEquals(current, active))
            {
                state.ActiveRequests.Remove(envelope.Id);
            }
            controller.Dispose();
        }
    }

    private void TransportClosed(ConnectionState connection)
    {
        if (!connection.Disconnected && connection.Stage != ConnectionStage.Closing)
        {
            try
            {
                connection.Decoder.End();
            }
            catch (Exception error)
            {
                ReportError(error);
            }
        }
        Disconnect(connection);
    }

    private void Disconnect(ConnectionState connection)
    {
        if (connection.Disconnected) return;
        connection.Disconnected = true;
        connection.Stage = ConnectionStage.Closed;
        ClearHandshakeTimeout(connection);
        foreach (var active in connection.ActiveRequests.Values) active.Controller.Cancel();
        connection.ActiveRequests.Clear();
        connection.ServiceStateEncoders.Clear();
        bool removed;
        lock (_gate) removed = _connections.Remove(connection);
        if (removed) NotifyConnectionCountChanged();

        var serverServices = connection.ServerServices;
        connection.ServerServices = null;
        _ = ReportDisconnectErrorsAsync(connection, serverServices);
    }

    private async Task ReportDisconnectErrorsAsync(ConnectionState connection,
        IRoutedServerServiceAttachment? serverServices)
    {
        var tasks = new List<Task> { _sessions.DisconnectAsync(connection, Context.Todo) };
        if (serverServices is not null) tasks.Add(serverServices.ReleaseAsync(Context.Todo));
        foreach (var error in await AllSettledAsync(tasks).ConfigureAwait(false)) ReportError(error);
    }

    private async Task SendServiceUpdateAsync(ConnectionState connection, string subscriptionId,
        ServiceProviderUpdate update)
    {
        if (!connection.ServiceStateEncoders.TryGetValue(subscriptionId, out var stateEncoder)) return;
        await SendMessageAsync(connection, new ServerMessage.ServiceEvent(subscriptionId,
            stateEncoder.EncodeUpdate(update))).ConfigureAwait(false);
    }

    private async Task<bool> SendMessageAsync(ConnectionState connection, ServerMessage message)
    {
        if (connection.Disconnected || connection.Connection.Closed) return false;
        byte[] frame;
        try
        {
            frame = ProtocolCodec.EncodeServerMessage(message, new FrameDecoderOptions(_maxFrameLength));
        }
        catch (Exception error)
        {
            ReportError(error);
            await CloseConnectionAsync(connection.Connection).ConfigureAwait(false);
            Disconnect(connection);
            return false;
        }
        try
        {
            await connection.Connection.SendAsync(frame).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            ReportError(error);
            await CloseConnectionAsync(connection.Connection).ConfigureAwait(false);
            Disconnect(connection);
            return false;
        }
    }

    private async Task FailProtocolAsync(ConnectionState connection, ProtocolError error)
    {
        if (connection.Disconnected || connection.Stage is ConnectionStage.Closing or ConnectionStage.Closed) return;
        connection.Stage = ConnectionStage.Closing;
        ClearHandshakeTimeout(connection);
        byte[]? finalFrame = null;
        try
        {
            finalFrame = ProtocolCodec.EncodeServerMessage(new ServerMessage.HelloError(error),
                new FrameDecoderOptions(_maxFrameLength));
        }
        catch (Exception encodeError)
        {
            ReportError(encodeError);
        }
        await CloseConnectionAsync(connection.Connection, finalFrame).ConfigureAwait(false);
        Disconnect(connection);
    }

    private async Task CloseServerStateAsync()
    {
        List<ConnectionState> connections;
        lock (_gate) connections = _connections.ToList();
        foreach (var connection in connections)
        {
            connection.Stage = ConnectionStage.Closing;
            ClearHandshakeTimeout(connection);
        }
        await Task.WhenAll(connections.Select(connection => CloseConnectionAsync(connection.Connection)))
            .ConfigureAwait(false);
        foreach (var connection in connections) Disconnect(connection);

        var errors = await AllSettledAsync([_sessions.CloseAsync(Context.Background)]).ConfigureAwait(false);
        lock (_gate) _connections.Clear();
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Failed to close server Sessions", errors);
    }

    private async Task CloseConnectionAsync(IByteConnection connection, byte[]? finalChunk = null)
    {
        try
        {
            await connection.CloseAsync(finalChunk).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private ProtocolError ToProtocolError(Exception error)
    {
        switch (error)
        {
            case ServerError server:
                return new ProtocolError(server.Code, server.Message);
            case RemoteServiceError remote:
                return new ProtocolError(remote.Code, remote.Message);
            case ProtocolValidationError validation:
                return new ProtocolError("invalid_request", validation.Message);
            default:
                ReportError(error);
                return new ProtocolError("internal_error", ServerErrorText.InternalServerErrorMessage);
        }
    }

    private void NotifyConnectionCountChanged()
    {
        try
        {
            int count;
            lock (_gate) count = _connections.Count;
            _onConnectionCountChanged?.Invoke(count);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            _onError?.Invoke(error);
        }
        catch
        {
            // 错误观察者不得影响服务端状态。
        }
    }

    private void SettleClosed(Exception? error = null)
    {
        if (_closedSettled) return;
        _closedSettled = true;
        if (error is null) _closedSignal.TrySetResult();
        else _closedSignal.TrySetException(error);
    }

    private static void ClearHandshakeTimeout(ConnectionState state)
    {
        var timeout = state.HandshakeTimeout;
        if (timeout is null) return;
        state.HandshakeTimeout = null;
        try
        {
            timeout.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        timeout.Dispose();
    }

    private static string TargetServerId(RpcTarget target) => target switch
    {
        RpcTarget.ServerTarget server => server.ServerId,
        RpcTarget.SessionTarget session => session.ServerId,
        _ => "",
    };

    /// <summary>目标等价判定（serverId + sessionId + attachmentId）。对应 TS <c>sameTarget</c>。</summary>
    private static bool SameTarget(RpcTarget left, RpcTarget right)
    {
        if (TargetServerId(left) != TargetServerId(right)) return false;
        if (left is not RpcTarget.SessionTarget leftSession || right is not RpcTarget.SessionTarget rightSession)
        {
            return left is not RpcTarget.SessionTarget && right is not RpcTarget.SessionTarget;
        }
        return leftSession.SessionId == rightSession.SessionId
            && leftSession.AttachmentId == rightSession.AttachmentId;
    }

    private static (int MaxFrameLength, int HandshakeTimeoutMs) ResolveOptions(ServerOptions options)
    {
        if (!ServerIds.IsServerId(options.ServerId))
        {
            throw new ArgumentException("serverId must be a canonical lowercase UUIDv4");
        }
        var maxFrameLength = options.MaxFrameLength ?? (int)Frame.DefaultMaxFrameLength;
        if (maxFrameLength <= 0 || (long)maxFrameLength > MaxUInt32)
        {
            throw new ArgumentException($"Server maxFrameLength must be an integer between 1 and {MaxUInt32}");
        }
        var handshakeTimeoutMs = options.HandshakeTimeoutMs ?? DefaultHandshakeTimeoutMs;
        if (handshakeTimeoutMs <= 0 || handshakeTimeoutMs > MaxTimerDelayMs)
        {
            throw new ArgumentException(
                $"Server handshakeTimeoutMs must be an integer between 1 and {MaxTimerDelayMs}");
        }
        return (maxFrameLength, handshakeTimeoutMs);
    }

    private static async Task<List<Exception>> AllSettledAsync(IEnumerable<Task> tasks)
    {
        var list = tasks.ToList();
        if (list.Count == 0) return [];
        await Task.WhenAll(list.Select(SettleAsync)).ConfigureAwait(false);
        return list.Where(task => task.IsFaulted)
            .Select(task => task.Exception!.InnerExceptions.Count == 1
                ? task.Exception.InnerExceptions[0]
                : task.Exception)
            .ToList();
    }

    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // allSettled 语义：失败由调用方按 IsFaulted 收集。
        }
    }

    /// <summary>服务端域展示能力：把会话操作转给路由器。对应 TS <c>RoutedServerPresentation</c> 的内联实现。</summary>
    private sealed class ServerPresentation(ConnectionState state, SessionRouter<TMetadata> sessions)
        : IRoutedServerPresentation
    {
        public Task AttachSessionAsync(string sessionId, Context context)
            => sessions.AttachClientAsync(state, sessionId, context);

        public Task DetachSessionAsync(Context context) => sessions.DetachClientAsync(state, context);

        public Task PrepareSessionRemovalAsync(string sessionId, Context context)
            => sessions.RemoveSessionAsync(sessionId, context);
    }

    private sealed class ConnectionHandler(
        Action<byte[]> onData, Action onClose, Action<Exception> onError) : IByteConnectionHandler
    {
        public void OnData(byte[] chunk) => onData(chunk);

        public void OnClose() => onClose();

        public void OnError(Exception error) => onError(error);
    }

    private sealed class NoopConnectionHandler(Action<Exception> onError) : IByteConnectionHandler
    {
        public void OnData(byte[] chunk)
        {
        }

        public void OnClose()
        {
        }

        public void OnError(Exception error) => onError(error);
    }
}
