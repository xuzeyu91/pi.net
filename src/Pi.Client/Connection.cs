using Pi.Protocol;

namespace Pi.Client;

/// <summary>连接选项。对应 TS <c>ConnectionOptions</c>（connection.ts）。</summary>
public sealed record ConnectionOptions
{
    public required ByteTransportFactory TransportFactory { get; init; }

    public required string ServerId { get; init; }

    public int? MaxFrameLength { get; init; }

    public required Action<ServerMessage.Hello> OnHandshake { get; init; }

    /// <summary>已过滤掉 hello / hello_error 的服务端消息。</summary>
    public required Action<ServerMessage> OnMessage { get; init; }

    public required Action<ConnectionStateChange> OnStateChange { get; init; }
}

/// <summary>
/// 单次连接的握手状态机。对应 TS <c>Connection</c>（connection.ts）：
/// 创建传输 → 发客户端 hello → 校验服务端 hello（serverId 必须匹配）→ 就绪；
/// 任何失败都进入 disconnected 并以异常完成握手 promise。
/// </summary>
public sealed class Connection
{
    private const long MaxUInt32 = 0xffff_ffffL;

    /// <summary>连接生命周期快照（每次转换替换为新对象，便于引用比较检测并发替换）。</summary>
    private sealed class Lifecycle
    {
        public ConnectionState State { get; init; } = ConnectionState.Disconnected;

        public long Id { get; init; }

        public ProtocolCodec.ServerMessageDecoder? Decoder { get; init; }

        public IByteTransport? Transport { get; init; }

        public TaskCompletionSource<ServerMessage.Hello>? Handshake { get; init; }
    }

    private readonly ConnectionOptions _options;
    private readonly object _gate = new();
    private Lifecycle _lifecycle = new();
    private long _sequence;

    public Connection(ConnectionOptions options)
    {
        _options = options;
        MaxFrameLength = options.MaxFrameLength ?? (int)Frame.DefaultMaxFrameLength;
        if (MaxFrameLength <= 0 || (long)MaxFrameLength > MaxUInt32)
        {
            throw new ArgumentException($"Client maxFrameLength must be an integer between 1 and {MaxUInt32}");
        }
    }

    public ConnectionState State
    {
        get
        {
            lock (_gate) return _lifecycle.State;
        }
    }

    public int MaxFrameLength { get; }

    /// <summary>发起连接；握手完成后以服务端 hello 完成。对应 TS <c>connect()</c>。</summary>
    public Task<ServerMessage.Hello> ConnectAsync()
    {
        Lifecycle lifecycle;
        lock (_gate)
        {
            if (_lifecycle.State != ConnectionState.Disconnected)
            {
                return Task.FromException<ServerMessage.Hello>(
                    new DisconnectedError($"Client is already {Describe(_lifecycle.State)}"));
            }
            var id = ++_sequence;
            lifecycle = new Lifecycle
            {
                State = ConnectionState.Connecting,
                Id = id,
                Decoder = new ProtocolCodec.ServerMessageDecoder(new FrameDecoderOptions(MaxFrameLength)),
                Handshake = PromiseResolvers.Create<ServerMessage.Hello>(),
            };
            _lifecycle = lifecycle;
        }
        _options.OnStateChange(new ConnectionStateChange(ConnectionState.Connecting));

        var id2 = lifecycle.Id;
        var handlers = new TransportHandlers(
            chunk => HandleData(id2, chunk),
            () =>
            {
                if (IsCurrent(id2)) HandleClose();
            },
            error =>
            {
                if (IsCurrent(id2)) FailAndClose(ClientErrors.ToDisconnectedError(error));
            });
        _ = OpenTransportAsync(id2, handlers);
        return lifecycle.Handshake!.Task;
    }

    /// <summary>主动断开。对应 TS <c>disconnect(reason)</c>。</summary>
    public void Disconnect(string reason = "Client disconnected") => FailAndClose(new DisconnectedError(reason));

    public void Disconnect(Exception reason) => FailAndClose(reason);

    /// <summary>以异常终止连接。对应 TS <c>fail(error)</c>。</summary>
    public void Fail(Exception error) => FailAndClose(error);

    /// <summary>发送一个已编码帧。对应 TS <c>send(frame)</c>。</summary>
    public void Send(byte[] frame)
    {
        Lifecycle lifecycle;
        IByteTransport transport;
        lock (_gate)
        {
            lifecycle = _lifecycle;
            if (lifecycle.State != ConnectionState.Connected || lifecycle.Transport is null)
            {
                throw new DisconnectedError();
            }
            transport = lifecycle.Transport;
        }

        Task sending;
        try
        {
            sending = transport.SendAsync(frame);
        }
        catch (Exception error)
        {
            FailAndClose(ClientErrors.ToDisconnectedError(error));
            return;
        }
        _ = sending.ContinueWith(task =>
        {
            if (!task.IsFaulted) return;
            Lifecycle current;
            lock (_gate) current = _lifecycle;
            if (current.State != ConnectionState.Disconnected && ReferenceEquals(current.Transport, transport))
            {
                FailAndClose(ClientErrors.ToDisconnectedError(task.Exception!.GetBaseException()));
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task OpenTransportAsync(long id, IByteTransportHandlers handlers)
    {
        IByteTransport transport;
        try
        {
            transport = await _options.TransportFactory(handlers).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsCurrent(id)) Fail(ClientErrors.ToDisconnectedError(error));
            return;
        }

        Lifecycle lifecycle;
        lock (_gate) lifecycle = _lifecycle;
        if (lifecycle.State != ConnectionState.Connecting || lifecycle.Id != id)
        {
            transport.Close();
            return;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_lifecycle, lifecycle))
            {
                _lifecycle = new Lifecycle
                {
                    State = lifecycle.State,
                    Id = lifecycle.Id,
                    Decoder = lifecycle.Decoder,
                    Transport = transport,
                    Handshake = lifecycle.Handshake,
                };
            }
        }

        try
        {
            await transport.SendAsync(ProtocolCodec.EncodeClientMessage(
                new ClientMessage.Hello(ProtocolVersion.Current),
                new FrameDecoderOptions(MaxFrameLength))).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (IsCurrent(id)) FailAndClose(ClientErrors.ToDisconnectedError(error));
        }
    }

    private void HandleData(long id, byte[] chunk)
    {
        Lifecycle lifecycle;
        lock (_gate) lifecycle = _lifecycle;
        if (lifecycle.State == ConnectionState.Disconnected || lifecycle.Id != id) return;
        if (lifecycle.State == ConnectionState.Connecting && lifecycle.Transport is null)
        {
            FailAndClose(new ProtocolValidationError("Received server data before the client hello was sent"));
            return;
        }

        IReadOnlyList<ServerMessage> messages;
        try
        {
            messages = lifecycle.Decoder!.Push(chunk);
        }
        catch (Exception error)
        {
            FailAndClose(error);
            return;
        }
        foreach (var message in messages)
        {
            lock (_gate)
            {
                if (_lifecycle.State == ConnectionState.Disconnected) return;
            }
            HandleMessage(message);
        }
    }

    private void HandleMessage(ServerMessage message)
    {
        Lifecycle lifecycle;
        lock (_gate) lifecycle = _lifecycle;

        if (lifecycle.State == ConnectionState.Connecting)
        {
            if (message is ServerMessage.HelloError helloError)
            {
                FailAndClose(new ServerError(helloError.Error));
                return;
            }
            if (message is not ServerMessage.Hello hello)
            {
                FailAndClose(new ProtocolValidationError("Expected server hello as first message"));
                return;
            }
            if (hello.ServerId != _options.ServerId)
            {
                FailAndClose(new ProtocolValidationError(
                    $"Connected server {Quote(hello.ServerId)} does not match {Quote(_options.ServerId)}"));
                return;
            }
            if (lifecycle.Transport is null)
            {
                FailAndClose(new ProtocolValidationError(
                    "Received server hello before the client hello was sent"));
                return;
            }

            var connected = new Lifecycle
            {
                State = ConnectionState.Connected,
                Id = lifecycle.Id,
                Decoder = lifecycle.Decoder,
                Transport = lifecycle.Transport,
                Handshake = lifecycle.Handshake,
            };
            lock (_gate) _lifecycle = connected;
            try
            {
                _options.OnHandshake(hello);
            }
            catch (Exception error)
            {
                if (IsSameLifecycle(connected)) FailAndClose(error);
                return;
            }
            if (!IsSameLifecycle(connected)) return;
            _options.OnStateChange(new ConnectionStateChange(ConnectionState.Connected));
            if (!IsSameLifecycle(connected)) return;
            lock (_gate)
            {
                if (ReferenceEquals(_lifecycle, connected))
                {
                    _lifecycle = new Lifecycle
                    {
                        State = connected.State,
                        Id = connected.Id,
                        Decoder = connected.Decoder,
                        Transport = connected.Transport,
                    };
                }
            }
            lifecycle.Handshake?.TrySetResult(hello);
            return;
        }

        if (lifecycle.State != ConnectionState.Connected) return;
        if (message is ServerMessage.Hello or ServerMessage.HelloError)
        {
            FailAndClose(new ProtocolValidationError("Unexpected handshake message"));
            return;
        }
        _options.OnMessage(message);
    }

    private void HandleClose()
    {
        Lifecycle lifecycle;
        lock (_gate) lifecycle = _lifecycle;
        if (lifecycle.State == ConnectionState.Disconnected) return;
        Exception error = new DisconnectedError("Byte transport closed");
        try
        {
            lifecycle.Decoder!.End();
        }
        catch (Exception decoderError)
        {
            error = decoderError;
        }
        FailInternal(error);
    }

    private void FailAndClose(Exception error)
    {
        IByteTransport? transport;
        lock (_gate) transport = _lifecycle.State == ConnectionState.Disconnected ? null : _lifecycle.Transport;
        FailInternal(error);
        transport?.Close();
    }

    private void FailInternal(Exception error)
    {
        Lifecycle lifecycle;
        lock (_gate)
        {
            lifecycle = _lifecycle;
            if (lifecycle.State == ConnectionState.Disconnected) return;
            _lifecycle = new Lifecycle();
        }
        lifecycle.Handshake?.TrySetException(error);
        _options.OnStateChange(new ConnectionStateChange(ConnectionState.Disconnected, error));
    }

    private bool IsCurrent(long id)
    {
        lock (_gate) return _lifecycle.State != ConnectionState.Disconnected && _lifecycle.Id == id;
    }

    private bool IsSameLifecycle(Lifecycle lifecycle)
    {
        lock (_gate) return ReferenceEquals(_lifecycle, lifecycle);
    }

    private static string Describe(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => "connecting",
        ConnectionState.Connected => "connected",
        _ => "disconnected",
    };

    private static string Quote(string value) => $"\"{value}\"";

    private sealed class TransportHandlers(
        Action<byte[]> onData, Action onClose, Action<Exception> onError) : IByteTransportHandlers
    {
        public void OnData(byte[] chunk) => onData(chunk);

        public void OnClose() => onClose();

        public void OnError(Exception error) => onError(error);
    }
}
