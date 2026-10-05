using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Protocol;

namespace Pi.Client;

/// <summary>
/// 客户端门面。对应 TS <c>Client</c>（client.ts）：连接生命周期、请求/响应、
/// 服务订阅（快照先就绪、更新按序投递）、附加路由变更与释放。
/// </summary>
public sealed class Client : IAsyncDisposable
{
    private sealed class PendingRequest
    {
        public required Action<object?> Resolve { get; init; }

        public required Action<Exception> Reject { get; init; }

        public required Action Cleanup { get; init; }
    }

    internal sealed class ActiveServiceListener
    {
        public required RpcTarget Target { get; init; }

        public required Func<ServiceProviderUpdate, Task> Listener { get; init; }

        public required ServiceStateDecoder Decoder { get; init; }

        public List<object?> QueuedWireUpdates { get; } = [];

        public List<ServiceProviderUpdate> Queued { get; } = [];

        public Task DeliveryTail { get; set; } = Task.CompletedTask;

        public bool Hydrated { get; set; }

        public bool Ready { get; set; }
    }

    private sealed class Subscription(
        string id, RpcTarget target, ServiceSubscriptionSnapshot snapshot,
        ActiveServiceListener active, Client client) : IServiceSubscription
    {
        private bool _disposed;

        public string Id => id;

        public RpcTarget Target => target;

        public ServiceSubscriptionSnapshot Snapshot => snapshot;

        public void Start()
        {
            if (_disposed || active.Ready) return;
            active.Ready = true;
            while (active.Queued.Count > 0)
            {
                var update = active.Queued[0];
                active.Queued.RemoveAt(0);
                client.DeliverServiceUpdate(active, update);
            }
        }

        public async Task DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(client.ServiceListenerOrNull(id), active)) client.RemoveServiceListener(id);
            try
            {
                if (client.Connected && client.TargetIsCurrent(target))
                {
                    await client.RequestAsync(target, ServiceWire.CreateServiceUnsubscribeCall(id))
                        .ConfigureAwait(false);
                }
                await active.DeliveryTail.ConfigureAwait(false);
            }
            finally
            {
                active.QueuedWireUpdates.Clear();
                active.Queued.Clear();
            }
        }
    }

    private readonly ClientOptions _options;
    private readonly Connection _connection;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly List<Action<ConnectionStateChange>> _connectionStateListeners = [];
    private readonly List<AttachmentChangeListener> _attachmentListeners = [];
    private readonly Dictionary<string, ActiveServiceListener> _serviceListeners = new(StringComparer.Ordinal);
    private long _requestSequence;
    private long _serviceSubscriptionSequence;
    private ServerMessage.Hello? _hello;
    private RpcTarget.SessionTarget? _attachment;
    private bool _disposed;
    private Task? _disposePromise;

    public Client(ClientOptions options)
    {
        if (!ServerIds.IsServerId(options.ServerId))
        {
            throw new ArgumentException("serverId must be a canonical lowercase UUIDv4");
        }
        _options = options;
        _connection = new Connection(new ConnectionOptions
        {
            TransportFactory = options.TransportFactory,
            ServerId = options.ServerId,
            MaxFrameLength = options.MaxFrameLength,
            OnHandshake = hello => _hello = hello,
            OnMessage = HandleMessage,
            OnStateChange = HandleConnectionStateChange,
        });
    }

    public bool Disposed => _disposed;

    public ConnectionState ConnectionState => _connection.State;

    public bool Connected => _connection.State == ConnectionState.Connected;

    public string ServerId => _options.ServerId;

    public ServerMessage.Hello? Hello => _hello;

    public RpcTarget.SessionTarget? Attachment => _attachment;

    /// <summary>连接并返回客户端；握手失败则释放后抛出。对应 TS <c>Client.connect(options)</c>。</summary>
    public static async Task<Client> ConnectAsync(ClientOptions options)
    {
        var client = new Client(options);
        try
        {
            await client.ConnectAsync().ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<ServerMessage.Hello> ConnectAsync()
    {
        if (_disposed) return Task.FromException<ServerMessage.Hello>(new ClientDisposedError());
        _hello = null;
        return _connection.ConnectAsync();
    }

    public Task<ServerMessage.Hello> ReconnectAsync() => ConnectAsync();

    public void Disconnect(string reason = "Client disconnected") => _connection.Disconnect(reason);

    public Unsubscribe OnConnectionStateChange(Action<ConnectionStateChange> listener)
    {
        AssertNotDisposed();
        lock (_gate) _connectionStateListeners.Add(listener);
        return () =>
        {
            lock (_gate) _connectionStateListeners.Remove(listener);
        };
    }

    public Unsubscribe OnAttachmentChange(AttachmentChangeListener listener)
    {
        AssertNotDisposed();
        lock (_gate) _attachmentListeners.Add(listener);
        return () =>
        {
            lock (_gate) _attachmentListeners.Remove(listener);
        };
    }

    /// <summary>对显式路由目标发起一次底层协议调用。对应 TS <c>request(target, call, signal)</c>。</summary>
    public Task<object?> RequestAsync(RpcTarget target, ServiceCall call, CancellationToken signal = default)
        => RequestAsync<object?>(target, call, signal, null);

    /// <summary>列举目标服务目录。对应 TS <c>serviceCatalogue</c>。</summary>
    public async Task<IReadOnlyList<ServiceCatalogueEntry>> ServiceCatalogueAsync(RpcTarget target,
        CancellationToken signal = default)
    {
        var result = await RequestAsync(target, ServiceWire.CreateServiceCatalogueCall(), signal)
            .ConfigureAwait(false);
        try
        {
            return ServiceWire.ParseServiceCatalogue(result);
        }
        catch (Exception error)
        {
            var validationError = new ProtocolValidationError(error.Message);
            _connection.Fail(validationError);
            throw validationError;
        }
    }

    /// <summary>订阅目标服务；快照就绪后由调用方 <c>Start</c> 触发投递。对应 TS <c>subscribeService</c>。</summary>
    public async Task<IServiceSubscription> SubscribeServiceAsync(RpcTarget target, string serviceId,
        ServiceMode mode, Func<ServiceProviderUpdate, Task> listener, CancellationToken signal = default)
    {
        var subscriptionId = $"service-{++_serviceSubscriptionSequence}";
        var active = new ActiveServiceListener
        {
            Target = target,
            Listener = listener,
            Decoder = new ServiceStateDecoder(),
        };
        lock (_gate) _serviceListeners[subscriptionId] = active;

        ServiceSubscriptionSnapshot snapshot;
        try
        {
            snapshot = await RequestAsync(target,
                ServiceWire.CreateServiceSubscribeCall(subscriptionId, serviceId, mode),
                signal,
                result =>
                {
                    var decoded = active.Decoder.DecodeSnapshot(
                        ServiceWire.ParseWireServiceSubscriptionSnapshot(result));
                    active.Hydrated = true;
                    while (active.QueuedWireUpdates.Count > 0)
                    {
                        var update = active.QueuedWireUpdates[0];
                        active.QueuedWireUpdates.RemoveAt(0);
                        active.Queued.Add(active.Decoder.DecodeUpdate(
                            ServiceWire.ParseWireServiceProviderUpdate(update)));
                    }
                    return decoded;
                }).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(ServiceListenerOrNull(subscriptionId), active))
                {
                    _serviceListeners.Remove(subscriptionId);
                }
            }
            throw;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(ServiceListenerOrNull(subscriptionId), active)) throw new DisconnectedError();
        }
        return new Subscription(subscriptionId, target, snapshot, active, this);
    }

    private async Task<T> RequestAsync<T>(RpcTarget target, ServiceCall call, CancellationToken signal,
        Func<object?, T>? transform)
    {
        if (_disposed) throw new ClientDisposedError();
        if (!Connected) throw new DisconnectedError();
        if (signal.IsCancellationRequested) throw new OperationCanceledException(signal);

        var id = $"request-{++_requestSequence}";
        var completion = PromiseResolvers.Create<T>();
        var sent = false;
        var aborted = false;
        CancellationTokenRegistration registration = default;

        void SendCancel()
        {
            if (!sent || !Connected) return;
            try
            {
                _connection.Send(ProtocolCodec.EncodeClientMessage(
                    new ClientMessage.Cancel(id, target),
                    new FrameDecoderOptions(_connection.MaxFrameLength)));
            }
            catch (Exception error)
            {
                _connection.Fail(error);
            }
        }

        if (signal.CanBeCanceled)
        {
            registration = signal.Register(() =>
            {
                if (aborted) return;
                aborted = true;
                completion.TrySetException(new OperationCanceledException(signal));
                SendCancel();
            });
        }

        lock (_gate)
        {
            _pendingRequests[id] = new PendingRequest
            {
                Resolve = result =>
                {
                    try
                    {
                        completion.TrySetResult(transform is null ? (T)result! : transform(result));
                    }
                    catch (Exception error)
                    {
                        var validationError = new ProtocolValidationError(error.Message);
                        _connection.Fail(validationError);
                        completion.TrySetException(validationError);
                    }
                },
                Reject = error => completion.TrySetException(error),
                Cleanup = () => registration.Dispose(),
            };
        }

        byte[] frame;
        try
        {
            frame = ProtocolCodec.EncodeClientMessage(
                new ClientMessage.Request(id, target, ToWireCall(call)),
                new FrameDecoderOptions(_connection.MaxFrameLength));
        }
        catch (Exception error)
        {
            TakePendingRequest(id)?.Reject(error);
            return await completion.Task.ConfigureAwait(false);
        }
        _connection.Send(frame);
        sent = true;
        if (aborted) SendCancel();
        return await completion.Task.ConfigureAwait(false);
    }

    private void HandleMessage(ServerMessage message)
    {
        switch (message)
        {
            case ServerMessage.Attachment attachment:
                if (attachment.Route is RpcTarget.SessionTarget session
                    && session.ServerId != _options.ServerId)
                {
                    _connection.Fail(new ProtocolValidationError("Attachment update belongs to another server"));
                    return;
                }
                SetAttachment(attachment.Route as RpcTarget.SessionTarget);
                return;

            case ServerMessage.ServiceEvent serviceEvent:
            {
                ActiveServiceListener? active;
                lock (_gate) active = ServiceListenerOrNull(serviceEvent.SubscriptionId);
                if (active is null) return;
                if (!active.Hydrated)
                {
                    active.QueuedWireUpdates.Add(serviceEvent.Update);
                    return;
                }
                ServiceProviderUpdate update;
                try
                {
                    update = active.Decoder.DecodeUpdate(
                        ServiceWire.ParseWireServiceProviderUpdate(serviceEvent.Update));
                }
                catch (Exception error)
                {
                    _connection.Fail(new ProtocolValidationError(error.Message));
                    return;
                }
                if (active.Ready) DeliverServiceUpdate(active, update);
                else active.Queued.Add(update);
                return;
            }

            case ServerMessage.ResponseOk ok:
            {
                var pending = TakePendingRequest(ok.Id);
                if (pending is null)
                {
                    _connection.Fail(new ProtocolValidationError("Response has no matching request"));
                    return;
                }
                pending.Resolve(ok.Result);
                return;
            }

            case ServerMessage.ResponseError error:
            {
                var pending = TakePendingRequest(error.Id);
                if (pending is null)
                {
                    _connection.Fail(new ProtocolValidationError("Response has no matching request"));
                    return;
                }
                pending.Reject(new ServerError(error.Error));
                return;
            }
        }
    }

    private void HandleConnectionStateChange(ConnectionStateChange change)
    {
        if (change.State == ConnectionState.Disconnected)
        {
            _hello = null;
            SetAttachment(null);
            RejectPendingRequests(change.Error ?? new DisconnectedError());
            lock (_gate) _serviceListeners.Clear();
        }

        List<Action<ConnectionStateChange>> listeners;
        lock (_gate) listeners = _connectionStateListeners.ToList();
        foreach (var listener in listeners)
        {
            try
            {
                listener(change);
            }
            catch (Exception error)
            {
                ReportListenerError(error);
            }
        }
    }

    private PendingRequest? TakePendingRequest(string id)
    {
        PendingRequest? request;
        lock (_gate)
        {
            request = _pendingRequests.GetValueOrDefault(id);
            if (request is not null) _pendingRequests.Remove(id);
        }
        request?.Cleanup();
        return request;
    }

    private void RejectPendingRequests(Exception error)
    {
        List<PendingRequest> requests;
        lock (_gate)
        {
            requests = _pendingRequests.Values.ToList();
            _pendingRequests.Clear();
        }
        foreach (var request in requests)
        {
            request.Cleanup();
            request.Reject(error);
        }
    }

    public Task DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposePromise is not null) return _disposePromise;
            _disposed = true;
            _disposePromise = Task.CompletedTask;
        }
        RejectPendingRequests(new ClientDisposedError());
        _connection.Disconnect(new ClientDisposedError());
        _hello = null;
        SetAttachment(null);
        lock (_gate)
        {
            _connectionStateListeners.Clear();
            _attachmentListeners.Clear();
            _serviceListeners.Clear();
        }
        return _disposePromise;
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    private void SetAttachment(RpcTarget.SessionTarget? attachment)
    {
        var previous = _attachment;
        if (previous?.ServerId == attachment?.ServerId
            && previous?.SessionId == attachment?.SessionId
            && previous?.AttachmentId == attachment?.AttachmentId)
        {
            return;
        }
        _attachment = attachment;
        List<AttachmentChangeListener> listeners;
        lock (_gate) listeners = _attachmentListeners.ToList();
        foreach (var listener in listeners)
        {
            try
            {
                listener(attachment);
            }
            catch (Exception error)
            {
                ReportListenerError(error);
            }
        }
    }

    private void DeliverServiceUpdate(ActiveServiceListener active, ServiceProviderUpdate update)
        => active.DeliveryTail = active.DeliveryTail
            .ContinueWith(_ => active.Listener(update), TaskScheduler.Default)
            .Unwrap()
            .ContinueWith(task =>
            {
                if (task.IsFaulted) ReportListenerError(task.Exception!.GetBaseException());
            }, TaskScheduler.Default);

    private bool TargetIsCurrent(RpcTarget target)
    {
        if (target is not RpcTarget.SessionTarget session) return _hello?.ServerId == ServerId;
        var attachment = _attachment;
        return attachment?.ServerId == session.ServerId
            && attachment.SessionId == session.SessionId
            && attachment.AttachmentId == session.AttachmentId;
    }

    private void AssertNotDisposed()
    {
        if (_disposed) throw new ClientDisposedError();
    }

    private void ReportListenerError(Exception error)
    {
        if (_options.OnListenerError is null) return;
        try
        {
            _options.OnListenerError(error);
        }
        catch
        {
            // 诊断不得影响协议或传输状态。
        }
    }

    internal ActiveServiceListener? ServiceListenerOrNull(string id)
        => _serviceListeners.GetValueOrDefault(id);

    internal void RemoveServiceListener(string id) => _serviceListeners.Remove(id);

    /// <summary>
    /// 把 <see cref="ServiceCall"/> 编码为 wire 形状（<c>{serviceId, member, args, instance?}</c>）。
    /// 对应 TS 的 <c>parseServiceCall(call) as JsonValue</c>（校验后原样作为 call 载荷）。
    /// </summary>
    internal static Dictionary<string, object?> ToWireCall(ServiceCall call)
    {
        var map = new Dictionary<string, object?>
        {
            ["serviceId"] = call.ServiceId,
            ["member"] = call.Member,
            ["args"] = call.Args,
        };
        if (call.Instance is not null)
        {
            map["instance"] = new Dictionary<string, object?>
            {
                ["key"] = call.Instance.Key,
                ["generation"] = call.Instance.Generation,
            };
        }
        return map;
    }
}

/// <summary>
/// 把「延迟解析的路由目标」适配成 chord 的服务传输。
/// 对应 TS <c>createClientServiceTransport</c>（client.ts）。
/// </summary>
public static class ClientServiceTransport
{
    public static IRemoteServiceTransport Create(Client client, Func<RpcTarget?> getTarget)
    {
        RpcTarget Target() => getTarget() ?? throw new InvalidOperationException(
            "Remote service target is unavailable");

        return new Adapter(client, Target);
    }

    private sealed class Adapter(Client client, Func<RpcTarget> target) : IRemoteServiceTransport
    {
        public Task<object?> InvokeAsync(ServiceCall call, Context context)
            => client.RequestAsync(target(), call, AbortSignalOf(context));

        public async Task<IRemoteServiceSubscription> SubscribeAsync(string serviceId, ServiceMode mode,
            Func<ServiceProviderUpdate, Context, Task?> listener, Context context)
        {
            var subscription = await client.SubscribeServiceAsync(target(), serviceId, mode,
                update => listener(update, Context.Background) ?? Task.CompletedTask,
                AbortSignalOf(context)).ConfigureAwait(false);
            return new SubscriptionAdapter(subscription);
        }

        private static CancellationToken AbortSignalOf(Context context)
            => context.AbortSignal ?? default;
    }

    private sealed class SubscriptionAdapter(IServiceSubscription subscription) : IRemoteServiceSubscription
    {
        public ServiceSubscriptionSnapshot Snapshot => subscription.Snapshot;

        public void Activate() => subscription.Start();

        public Task CloseAsync(Context context) => subscription.DisposeAsync();
    }
}
