using Pi.Chord.Delta;
using Pi.Chord.Facets;

namespace Pi.Chord.Services;

/// <summary>
/// 远程服务调用传输：一次调用 + 一次订阅。对应 TS <c>RemoteServiceTransport</c>（types.ts）。
/// </summary>
public interface IRemoteServiceTransport
{
    /// <summary>调用一个方法成员。对应 TS <c>invoke(call, context)</c>。</summary>
    Task<object?> InvokeAsync(ServiceCall call, Context.Context context);

    /// <summary>订阅一个服务（singleton 或 keyed）。对应 TS <c>subscribe(serviceId, mode, listener, context)</c>。</summary>
    Task<IRemoteServiceSubscription> SubscribeAsync(string serviceId, ServiceMode mode,
        Func<ServiceProviderUpdate, Context.Context, Task?> listener, Context.Context context);
}

/// <summary>一次服务订阅。对应 TS <c>ServiceSubscription</c>。</summary>
public interface IRemoteServiceSubscription
{
    ServiceSubscriptionSnapshot Snapshot { get; }

    /// <summary>开始接收更新（此前只缓冲）。对应 TS <c>activate()</c>。</summary>
    void Activate();

    /// <summary>关闭订阅并释放资源。对应 TS <c>close(context)</c>。</summary>
    Task CloseAsync(Context.Context context);
}

/// <summary>绑定选项。对应 TS <c>RemoteServiceBindingOptions</c>。</summary>
public sealed record RemoteServiceBindingOptions
{
    public required IReadOnlyList<string> Services { get; init; }

    public required IRemoteServiceTransport Transport { get; init; }

    /// <summary>是否已连接；缺省 true。对应 TS <c>bound</c>。</summary>
    public bool? Bound { get; init; }

    public Action<Exception>? OnError { get; init; }

    public Action? AssertAccess { get; init; }
}

/// <summary>远程服务成员种类。对应 TS <c>ServiceMemberSnapshot["kind"]</c>。</summary>
public enum RemoteServiceMemberKind
{
    Method,
    State,
}

/// <summary>
/// 远程服务成员句柄：可调用（方法）或可读/可订阅（状态）。
/// 对应 TS <c>MemberSlot</c>（services/consumer.ts）。
/// <para>TS 用 <c>Proxy</c> 让成员同时是函数与对象；C# 改为显式 API——
/// <see cref="CallAsync"/> / <see cref="AsCallable"/> 对应 apply，<see cref="Value"/> /
/// <see cref="Subscribe"/> 对应 <c>.value</c> / <c>.subscribe</c>。</para>
/// </summary>
public sealed class RemoteServiceMember
{
    private readonly string _serviceId;
    private readonly string _member;
    private readonly Func<object?[], Context.Context, Task<object?>> _invoke;
    private readonly Func<bool> _isActive;
    private readonly Action _assertAccess;
    private readonly ReplicatedStateReplica<object?> _state;
    private RemoteServiceMemberKind? _kind;
    private RemoteServiceMemberKind? _expectedKind;

    internal RemoteServiceMember(string serviceId, string member,
        Func<object?[], Context.Context, Task<object?>> invoke,
        Func<bool> isActive, Action assertAccess, Action<Exception> reportError)
    {
        _serviceId = serviceId;
        _member = member;
        _invoke = invoke;
        _isActive = isActive;
        _assertAccess = assertAccess;
        _state = new ReplicatedStateReplica<object?>(reportError);
    }

    public string Name => _member;

    /// <summary>状态成员当前值（访问即断言 state 种类）。对应 TS <c>.value</c>。</summary>
    public object? Value
    {
        get
        {
            _assertAccess();
            Expect(RemoteServiceMemberKind.State);
            return _state.Value;
        }
    }

    /// <summary>订阅状态成员。对应 TS <c>.subscribe(listener)</c>。</summary>
    public IDisposable Subscribe(StateListener<object?> listener)
    {
        _assertAccess();
        Expect(RemoteServiceMemberKind.State);
        return _state.Subscribe(listener);
    }

    /// <summary>设置成员种类（快照描述）。对应 TS <c>setDescription</c>。</summary>
    public void SetDescription(RemoteServiceMemberKind kind)
    {
        if (_kind is { } existing && existing != kind)
        {
            throw new InvalidOperationException($"Remote service member {_serviceId}.{_member} changed kind");
        }
        _kind = kind;
        if (_expectedKind is { } expected && expected != kind)
        {
            throw new RemoteServiceError("service_member_mismatch",
                $"Remote service member {_serviceId}.{_member} is {Describe(kind)}, not {Describe(expected)}");
        }
    }

    public void Hydrate(long sequence, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        SetDescription(RemoteServiceMemberKind.State);
        _state.Hydrate(sequence, ops, context);
    }

    public void Update(long sequence, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        SetDescription(RemoteServiceMemberKind.State);
        _state.Update(sequence, ops, context);
    }

    public void Clear() => _state.Clear();

    /// <summary>
    /// 显式调用方法成员（参数已去掉尾随 Context）。对应 TS <c>#call(args)</c> 的 invoke 段。
    /// </summary>
    public Task<object?> CallAsync(object?[] businessArgs, Context.Context context)
    {
        _assertAccess();
        Expect(RemoteServiceMemberKind.Method);
        if (!_isActive())
        {
            throw new RemoteServiceError("service_stale_instance",
                $"Remote service {_serviceId} binding is closed");
        }
        return _invoke(businessArgs, context);
    }

    /// <summary>
    /// 以「尾随 Context」约定暴露为可调用成员（供成员字典承载）。对应 TS 的 Proxy apply：
    /// 最后一个参数必须是 <c>Context</c>，它之前的才是业务参数。
    /// </summary>
    public Func<object?[], object?> AsCallable() => args =>
    {
        _assertAccess();
        Expect(RemoteServiceMemberKind.Method);
        if (!_isActive())
        {
            throw new RemoteServiceError("service_stale_instance",
                $"Remote service {_serviceId} binding is closed");
        }
        if (args.Length == 0 || args[^1] is not Context.Context context)
        {
            throw new RemoteServiceError("service_invalid_value",
                $"Remote service method {_serviceId}.{_member} requires a trailing Context");
        }
        return _invoke(args[..^1], context);
    };

    /// <summary>作为成员字典的值（方法 → 可调用；状态 → 可读可订阅）。</summary>
    public object? ToMemberValue()
        => _kind == RemoteServiceMemberKind.State ? _state : AsCallable();

    private void Expect(RemoteServiceMemberKind kind)
    {
        if (_expectedKind is { } expected && expected != kind)
        {
            throw new RemoteServiceError("service_member_mismatch",
                $"Remote service member {_serviceId}.{_member} was used as two different kinds");
        }
        _expectedKind = kind;
        if (_kind is { } actual && actual != kind)
        {
            throw new RemoteServiceError("service_member_mismatch",
                $"Remote service member {_serviceId}.{_member} is {Describe(actual)}, not {Describe(kind)}");
        }
    }

    internal static string Describe(RemoteServiceMemberKind kind)
        => kind == RemoteServiceMemberKind.State ? "state" : "method";
}

/// <summary>
/// 单个远程实例的门面：按成员名取 <see cref="RemoteServiceMember"/>，并接收快照/更新。
/// 对应 TS <c>ServiceFacade</c>（services/consumer.ts）的 Proxy。
/// <para>TS 的 <c>proxy</c> 对任意属性名即时造成员；C# 以
/// <see cref="Member"/>（显式取成员）与 <see cref="IReadOnlyDictionary{TKey,TValue}"/> 视图
/// （键 = 最近一次快照的成员名）两种方式呈现，后者可直接被 <c>(IReadOnlyDictionary&lt;string, object?&gt;)</c>
/// 类型的服务契约承接。</para>
/// </summary>
public sealed class RemoteServiceFacade : IReadOnlyDictionary<string, object?>
{
    private readonly string _serviceId;
    private readonly ServiceInstanceAddress? _address;
    private readonly IRemoteServiceTransport _transport;
    private readonly Func<bool> _isActive;
    private readonly Action _assertAccess;
    private readonly Action<Exception> _reportError;
    private readonly Dictionary<string, RemoteServiceMember> _members = [];
    private readonly Dictionary<string, RemoteServiceMemberKind> _descriptions = [];

    internal RemoteServiceFacade(string serviceId, ServiceInstanceAddress? address,
        IRemoteServiceTransport transport, Func<bool> isActive, Action assertAccess, Action<Exception> reportError)
    {
        _serviceId = serviceId;
        _address = address;
        _transport = transport;
        _isActive = isActive;
        _assertAccess = assertAccess;
        _reportError = reportError;
    }

    public string ServiceId => _serviceId;

    /// <summary>取（必要时新建）一个成员句柄。对应 TS <c>#slot(member)</c>。</summary>
    public RemoteServiceMember Member(string name)
    {
        if (_members.TryGetValue(name, out var existing)) return existing;
        var member = new RemoteServiceMember(_serviceId, name,
            (args, context) => _transport.InvokeAsync(
                new ServiceCall(_serviceId, name, args, _address), context),
            _isActive, _assertAccess, _reportError);
        if (_descriptions.TryGetValue(name, out var kind)) member.SetDescription(kind);
        _members[name] = member;
        return member;
    }

    /// <summary>安装一份实例快照（水合状态成员、校验已有成员仍在）。对应 TS <c>install</c>。</summary>
    public void Install(ServiceInstanceSnapshot snapshot, Context.Context context)
    {
        if (!SameAddress(snapshot.Instance, _address))
        {
            throw new InvalidOperationException("Remote service snapshot has the wrong address");
        }
        var members = ValidateMembers(snapshot.Members);
        foreach (var name in _members.Keys)
        {
            if (!members.ContainsKey(name))
            {
                throw new RemoteServiceError("service_member_not_found",
                    $"Unknown remote service member {_serviceId}.{name}");
            }
        }

        _descriptions.Clear();
        foreach (var (name, member) in members) _descriptions[name] = KindOf(member);

        foreach (var (name, member) in members)
        {
            var existing = _members.GetValueOrDefault(name);
            if (member is ServiceMemberSnapshot.State state)
            {
                (existing ?? Member(name)).Hydrate(state.Sequence, state.Ops, context);
            }
            else
            {
                existing?.SetDescription(RemoteServiceMemberKind.Method);
            }
        }
    }

    /// <summary>应用一次状态增量。对应 TS <c>update</c>。</summary>
    public void Update(string member, long sequence, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        if (!_descriptions.TryGetValue(member, out var kind) || kind != RemoteServiceMemberKind.State)
        {
            throw new InvalidOperationException(
                $"Remote service update targets non-state member {_serviceId}.{member}");
        }
        Member(member).Update(sequence, ops, context);
    }

    /// <summary>清空全部成员状态。对应 TS <c>clear</c>。</summary>
    public void Clear()
    {
        foreach (var member in _members.Values) member.Clear();
    }

    // ---------- IReadOnlyDictionary<string, object?> 视图（键 = 最近快照的成员名） ----------

    public IEnumerable<string> Keys => _descriptions.Keys;

    public IEnumerable<object?> Values => _descriptions.Keys.Select(key => Member(key).ToMemberValue());

    public int Count => _descriptions.Count;

    public object? this[string key] => Member(key).ToMemberValue();

    public bool ContainsKey(string key) => _descriptions.ContainsKey(key);

    public bool TryGetValue(string key, out object? value)
    {
        if (!_descriptions.ContainsKey(key))
        {
            value = null;
            return false;
        }
        value = Member(key).ToMemberValue();
        return true;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        => _descriptions.Keys.Select(key => new KeyValuePair<string, object?>(key, Member(key).ToMemberValue()))
            .GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal static RemoteServiceMemberKind KindOf(ServiceMemberSnapshot member)
        => member is ServiceMemberSnapshot.State ? RemoteServiceMemberKind.State : RemoteServiceMemberKind.Method;

    internal static bool SameAddress(ServiceInstanceAddress? left, ServiceInstanceAddress? right)
        => left is null || right is null ? left is null && right is null : left == right;

    internal static IReadOnlyDictionary<string, ServiceMemberSnapshot> ValidateMembers(
        IReadOnlyList<ServiceMemberSnapshot> members)
    {
        var result = new Dictionary<string, ServiceMemberSnapshot>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var name = member switch
            {
                ServiceMemberSnapshot.Method method => method.Name,
                ServiceMemberSnapshot.State state => state.Name,
                _ => "",
            };
            if (name.Length == 0 || result.ContainsKey(name))
            {
                throw new InvalidOperationException("Remote service has invalid member descriptions");
            }
            result[name] = member;
        }
        return result;
    }

    /// <summary>
    /// 校验 reset 快照的形状（服务/模式一致、实例地址形态、实例键唯一、状态成员必须是整根替换）。
    /// 对应 TS <c>validateResetSnapshot</c>。
    /// </summary>
    public static void ValidateResetSnapshot(ServiceSubscriptionSnapshot snapshot, string serviceId, ServiceMode mode)
    {
        if (snapshot.ServiceId != serviceId || snapshot.Mode != mode
            || (mode == ServiceMode.Singleton && snapshot.Instances.Count > 1))
        {
            throw new InvalidOperationException("Remote service reset has the wrong service or mode");
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in snapshot.Instances)
        {
            var needsAddress = mode == ServiceMode.Keyed;
            if (needsAddress ? instance.Instance is null : instance.Instance is not null)
            {
                throw new InvalidOperationException("Remote service reset has an invalid instance address");
            }
            if (instance.Instance is not null && !keys.Add(instance.Instance.Key))
            {
                throw new InvalidOperationException("Remote service reset repeats an instance key");
            }
            foreach (var member in instance.Members)
            {
                if (member is ServiceMemberSnapshot.State state
                    && (state.Ops.Count != 1 || state.Ops[0] is not DeltaOp.Replace))
                {
                    throw new InvalidOperationException("Remote service reset must contain full root replacements");
                }
            }
        }
    }
}

/// <summary>
/// keyed 远程服务的观察绑定。对应 TS <c>KeyedBinding&lt;T&gt;</c>（services/consumer.ts）。
/// </summary>
internal sealed class KeyedBinding<T> where T : class
{
    private sealed class KeyedInstance(string key, long generation, object service, RemoteServiceFacade facade)
        : IInstanceDirectoryEntry
    {
        public string Key => key;

        public long Generation => generation;

        public object Service { get; } = service;

        public RemoteServiceFacade Facade { get; } = facade;

        public void Deactivate() => Facade.Clear();
    }

    private readonly Service<T> _service;
    private readonly IRemoteServiceTransport _transport;
    private readonly Action<Exception> _reportError;
    private readonly Action _assertAccess;
    private readonly Action _onEmpty;
    private readonly InstanceDirectory<KeyedInstance> _instances;
    private IRemoteServiceSubscription? _subscription;
    private Task? _starting;
    private bool _closed;
    private bool _bound;
    private long _revision;

    public KeyedBinding(Service<T> service, IRemoteServiceTransport transport, Action<Exception> reportError,
        Action assertAccess, Action onEmpty, bool bound)
    {
        _service = service;
        _transport = transport;
        _reportError = reportError;
        _assertAccess = assertAccess;
        _onEmpty = onEmpty;
        _bound = bound;
        _instances = new InstanceDirectory<KeyedInstance>(ready: false, reportError);
    }

    /// <summary>观察 keyed 实例流（回调收到门面；C# 无 Proxy，故传门面而非 T）。</summary>
    public IDisposable Observe(Func<RemoteServiceFacade, Context.Context, Task?> handler)
    {
        if (_closed) throw new InvalidOperationException("Remote keyed service binding is closed");
        var stopped = false;
        var stop = _instances.Observe((service, context) =>
        {
            if (service is not KeyedInstance instance) return Task.CompletedTask;
            if (stopped || (context.AbortSignal?.IsCancellationRequested ?? false))
            {
                throw new RemoteServiceError("service_stale_instance",
                    $"Remote service {_service.Id} observation is closed");
            }
            _assertAccess();
            return handler(instance.Facade, context);
        });
        if (_bound && _starting is null)
        {
            var revision = _revision;
            var starting = StartAsync(revision);
            _starting = starting;
            _ = starting.ContinueWith(
                task =>
                {
                    if (task.IsFaulted && !_closed && _revision == revision && _bound)
                    {
                        _reportError(task.Exception!.GetBaseException());
                    }
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return new Disposal(() =>
        {
            if (stopped) return;
            stopped = true;
            stop.Dispose();
            if (_instances.ObserverCount == 0) _onEmpty();
        });
    }

    public async Task RebindAsync(bool bound, Context.Context context)
    {
        if (_closed) return;
        _bound = bound;
        var revision = ++_revision;
        await ResetAsync(context, waitForStarting: false).ConfigureAwait(false);
        if (_closed || _revision != revision || _bound != bound) return;
        if (bound && _instances.ObserverCount > 0)
        {
            var starting = StartAsync(revision);
            _starting = starting;
            await starting.ConfigureAwait(false);
        }
    }

    public Task ReadyAsync() => _starting ?? Task.CompletedTask;

    public async Task CloseAsync(Context.Context context)
    {
        if (_closed) return;
        _closed = true;
        _revision += 1;
        await ResetAsync(context, waitForStarting: true).ConfigureAwait(false);
        _instances.Dispose();
    }

    private async Task ResetAsync(Context.Context context, bool waitForStarting)
    {
        _instances.Reset();
        var starting = _starting;
        _starting = null;
        var subscription = _subscription;
        _subscription = null;
        if (waitForStarting && starting is not null)
        {
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch
            {
                // 对齐 TS 的 starting?.catch(() => {})：等待但不传播。
            }
        }
        if (subscription is not null) await subscription.CloseAsync(context).ConfigureAwait(false);
    }

    private async Task StartAsync(long revision)
    {
        var subscription = await _transport.SubscribeAsync(_service.Id, ServiceMode.Keyed, (update, context) =>
        {
            if (_revision == revision) Update(update, context);
            return Task.CompletedTask;
        }, Context.Context.Background).ConfigureAwait(false);
        if (_closed || !_bound || _revision != revision)
        {
            await subscription.CloseAsync(Context.Context.Background).ConfigureAwait(false);
            return;
        }
        _subscription = subscription;
        if (subscription.Snapshot.Mode != ServiceMode.Keyed || subscription.Snapshot.ServiceId != _service.Id)
        {
            throw new InvalidOperationException(
                $"Remote service {_service.Id} returned the wrong keyed snapshot");
        }
        foreach (var snapshot in subscription.Snapshot.Instances)
        {
            Spawn(snapshot, ReplicatedStates.ServiceDeliveryContext());
        }
        subscription.Activate();
        _instances.Ready();
    }

    private void Update(ServiceProviderUpdate update, Context.Context context)
    {
        if (_closed) return;
        try
        {
            switch (update)
            {
                case ServiceProviderUpdate.Reset reset:
                {
                    RemoteServiceFacade.ValidateResetSnapshot(reset.Snapshot, _service.Id, ServiceMode.Keyed);
                    var snapshots = reset.Snapshot.Instances
                        .ToDictionary(snapshot => snapshot.Instance!.Key, snapshot => snapshot);
                    foreach (var instance in _instances.Values.ToList())
                    {
                        if (snapshots.GetValueOrDefault(instance.Key)?.Instance?.Generation != instance.Generation)
                        {
                            _instances.Remove(instance);
                        }
                    }
                    foreach (var snapshot in snapshots.Values)
                    {
                        var instance = _instances.Get(snapshot.Instance!.Key);
                        if (instance is null) Spawn(snapshot, context);
                        else instance.Facade.Install(snapshot, context);
                    }
                    break;
                }
                case ServiceProviderUpdate.Unavailable:
                case ServiceProviderUpdate.Replaced:
                    throw new InvalidOperationException("Keyed service received a singleton lifecycle update");
                case ServiceProviderUpdate.Spawned spawned:
                    Spawn(spawned.Instance, context);
                    break;
                case ServiceProviderUpdate.Closed closed:
                {
                    var instance = _instances.Get(closed.Instance.Key);
                    if (instance?.Generation == closed.Instance.Generation) _instances.Remove(instance);
                    break;
                }
                case ServiceProviderUpdate.StateUpdate state:
                {
                    if (state.Instance is null)
                    {
                        throw new InvalidOperationException("Keyed state update has no instance address");
                    }
                    var instance = _instances.Get(state.Instance.Key);
                    if (instance?.Generation != state.Instance.Generation) return;
                    instance.Facade.Update(state.Member, state.Sequence, state.Ops, context);
                    break;
                }
            }
        }
        catch (Exception error)
        {
            _reportError(error);
        }
    }

    private void Spawn(ServiceInstanceSnapshot snapshot, Context.Context context)
    {
        var address = snapshot.Instance
            ?? throw new InvalidOperationException("Keyed service instance snapshot has no address");
        var active = true;
        var facade = new RemoteServiceFacade(_service.Id, address, _transport,
            () => active && !_closed, _assertAccess, _reportError);
        facade.Install(snapshot, context);
        _instances.Replace(new KeyedInstance(address.Key, address.Generation, facade, facade));
    }

    private sealed class Disposal(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }
}

/// <summary>
/// 远程服务绑定：按 allowlist 使用/观察远程服务，管理连接、就绪门与重绑。
/// 对应 TS <c>RemoteServiceBindingImpl</c>（services/consumer.ts）。
/// </summary>
public sealed class RemoteServiceBinding : IRemoteServices, IDisposable
{
    private sealed class SingletonBinding
    {
        public required RemoteServiceFacade Facade { get; init; }

        public IRemoteServiceSubscription? Subscription { get; set; }

        public Task? Starting { get; set; }

        public bool Active { get; set; } = true;

        public long Revision { get; set; }
    }

    private readonly IRemoteServiceTransport _transport;
    private readonly HashSet<string> _allowlist = new(StringComparer.Ordinal);
    private readonly Action<Exception> _reportError;
    private readonly Dictionary<string, ServiceMode> _modes = [];
    private readonly Action _assertAccess;
    private readonly Dictionary<string, SingletonBinding> _singletons = [];
    private readonly Dictionary<string, KeyedBinding<object>> _keyed = [];
    private bool _bound;
    private long _readinessRevision;
    private Task _bindingTransition = Task.CompletedTask;
    private bool _disposed;

    public RemoteServiceBinding(RemoteServiceBindingOptions options)
    {
        _transport = options.Transport;
        var ids = options.Services.ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new ArgumentException("Remote service binding has duplicate service IDs");
        }
        foreach (var id in ids) _allowlist.Add(id);
        _reportError = options.OnError ?? (_ => { });
        _assertAccess = options.AssertAccess ?? (() => { });
        _bound = options.Bound ?? true;
    }

    /// <summary>取单例服务的门面（显式成员访问）。对应 TS <c>use(service)</c> 的 Proxy。</summary>
    public RemoteServiceFacade Use<T>(Service<T> service)
    {
        AssertRemotable(service);
        AssertAvailable(service.Id, ServiceMode.Singleton);
        if (_singletons.TryGetValue(service.Id, out var existing)) return existing.Facade;

        SingletonBinding? binding = null;
        binding = new SingletonBinding
        {
            Facade = new RemoteServiceFacade(service.Id, null, _transport,
                () => _singletons.TryGetValue(service.Id, out var current)
                    && ReferenceEquals(current, binding) && current.Active && !_disposed && _bound,
                AssertHandleAccess, _reportError),
        };

        _singletons[service.Id] = binding;
        _readinessRevision += 1;
        if (_bound)
        {
            var revision = binding.Revision;
            var starting = StartSingletonAsync(service.Id, binding, revision);
            binding.Starting = starting;
            _ = starting.ContinueWith(
                task =>
                {
                    if (task.IsFaulted && binding.Active && binding.Revision == revision && !_disposed && _bound)
                    {
                        _reportError(task.Exception!.GetBaseException());
                    }
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return binding.Facade;
    }

    /// <summary>
    /// 取单例服务的 <typeparamref name="T"/> 句柄。C# 无 Proxy，故把门面按成员字典视图转型；
    /// <typeparamref name="T"/> 必须是 <c>IReadOnlyDictionary&lt;string, object?&gt;</c>（C# 的服务实现模型）或 <c>object</c>。
    /// </summary>
    public T UseAs<T>(Service<T> service) => (T)(object)Use(service);

    /// <summary>观察 keyed 服务的实例流（回调收到门面）。对应 TS <c>observe(service, handler)</c>。</summary>
    public IDisposable Observe<T>(Service<T> service, Func<RemoteServiceFacade, Context.Context, Task?> handler)
    {
        AssertRemotable(service);
        AssertAvailable(service.Id, ServiceMode.Keyed);
        if (!_keyed.TryGetValue(service.Id, out var binding))
        {
            KeyedBinding<object>? created = null;
            created = new KeyedBinding<object>(new Service<object>(service.Id, service.Local), _transport,
                _reportError, AssertHandleAccess, () =>
                {
                    if (!_keyed.TryGetValue(service.Id, out var current) || !ReferenceEquals(current, created)) return;
                    _keyed.Remove(service.Id);
                    _readinessRevision += 1;
                    _ = created.CloseAsync(Context.Context.Background)
                        .ContinueWith(task => _reportError(task.Exception!.GetBaseException()),
                            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }, _bound);
            binding = created;
            _keyed[service.Id] = binding;
            _readinessRevision += 1;
        }
        return binding.Observe(handler);
    }

    /// <summary>观察 keyed 服务并把门面转型为 <typeparamref name="T"/>（约束同 <see cref="UseAs{T}"/>）。</summary>
    public IDisposable ObserveAs<T>(Service<T> service, Func<T, Context.Context, Task?> handler)
        => Observe(service, (facade, context) => handler((T)(object)facade, context));

    /// <summary>就绪门：等全部起始与重绑完成；期间有新绑定则重来。对应 TS <c>ready(context)</c>。</summary>
    public async Task ReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
        while (true)
        {
            var revision = _readinessRevision;
            var starts = new List<Task> { _bindingTransition };
            foreach (var binding in _singletons.Values)
            {
                if (binding.Starting is not null) starts.Add(binding.Starting);
            }
            foreach (var binding in _keyed.Values) starts.Add(binding.ReadyAsync());
            await Task.WhenAll(starts).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
            if (revision == _readinessRevision) return;
        }
    }

    /// <summary>连接/断开并重建全部订阅。对应 TS <c>rebind(bound, context)</c>。</summary>
    public async Task RebindAsync(bool bound, Context.Context context)
    {
        if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
        _bound = bound;
        _readinessRevision += 1;
        var transitions = new List<Task>();
        foreach (var (serviceId, binding) in _singletons)
        {
            binding.Revision += 1;
            binding.Facade.Clear();
            var subscription = binding.Subscription;
            binding.Subscription = null;
            var revision = binding.Revision;
            var starting = StartSingletonAfterCloseAsync(serviceId, binding, revision, bound, subscription, context);
            binding.Starting = starting;
            transitions.Add(starting);
        }
        foreach (var binding in _keyed.Values) transitions.Add(binding.RebindAsync(bound, context));

        var completion = CompleteRebindAsync(transitions);
        _bindingTransition = completion;
        await completion.ConfigureAwait(false);
    }

    private async Task StartSingletonAfterCloseAsync(string serviceId, SingletonBinding binding,
        long revision, bool bound, IRemoteServiceSubscription? subscription, Context.Context context)
    {
        if (subscription is not null) await subscription.CloseAsync(context).ConfigureAwait(false);
        if (bound) await StartSingletonCoreAsync(serviceId, binding, revision).ConfigureAwait(false);
    }

    private static async Task CompleteRebindAsync(List<Task> transitions)
    {
        try
        {
            await Task.WhenAll(transitions).ConfigureAwait(false);
        }
        catch
        {
            var errors = transitions.Where(task => task.IsFaulted)
                .Select(task => task.Exception!.GetBaseException())
                .ToList();
            throw new AggregateException("Failed to rebind services", errors);
        }
    }

    /// <summary>释放全部绑定与订阅。对应 TS <c>dispose(context)</c>。</summary>
    public async Task DisposeAsync(Context.Context context)
    {
        if (_disposed) return;
        _disposed = true;
        var closes = new List<Task>();
        foreach (var binding in _singletons.Values)
        {
            binding.Active = false;
            binding.Facade.Clear();
            if (binding.Starting is not null) closes.Add(SwallowAsync(binding.Starting));
            if (binding.Subscription is not null) closes.Add(binding.Subscription.CloseAsync(context));
        }
        foreach (var binding in _keyed.Values) closes.Add(binding.CloseAsync(context));
        _singletons.Clear();
        _keyed.Clear();

        try
        {
            await Task.WhenAll(closes).ConfigureAwait(false);
        }
        catch
        {
            var errors = closes.Where(task => task.IsFaulted)
                .Select(task => task.Exception!.GetBaseException())
                .ToList();
            throw new AggregateException("Failed to dispose services", errors);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _singletons.Values)
        {
            binding.Active = false;
            binding.Facade.Clear();
        }
        _singletons.Clear();
        _keyed.Clear();
    }

    /// <summary>取服务的本地门面实例（<see cref="IRemoteServices"/> 契约）。对应 TS <c>useRaw</c>。</summary>
    public object? UseRaw(string serviceId)
    {
        if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
        if (!_allowlist.Contains(serviceId))
        {
            throw new RemoteServiceError("service_not_allowed",
                $"Remote service {serviceId} is not allowlisted");
        }
        var mode = _modes.TryGetValue(serviceId, out var existing) ? existing : ServiceMode.Singleton;
        _modes[serviceId] = mode;
        return _singletons.TryGetValue(serviceId, out var binding)
            ? binding.Facade
            : Use(new Service<object>(serviceId));
    }

    private async Task StartSingletonAsync(string serviceId, SingletonBinding binding, long revision)
        => await StartSingletonCoreAsync(serviceId, binding, revision).ConfigureAwait(false);

    private async Task StartSingletonCoreAsync(string serviceId, SingletonBinding binding, long revision)
    {
        var subscription = await _transport.SubscribeAsync(serviceId, ServiceMode.Singleton, (update, context) =>
        {
            if (!binding.Active || binding.Revision != revision) return Task.CompletedTask;
            try
            {
                switch (update)
                {
                    case ServiceProviderUpdate.Reset reset:
                    {
                        RemoteServiceFacade.ValidateResetSnapshot(reset.Snapshot, serviceId, ServiceMode.Singleton);
                        var snapshot = reset.Snapshot.Instances.FirstOrDefault();
                        if (snapshot is null) binding.Facade.Clear();
                        else binding.Facade.Install(snapshot, context);
                        break;
                    }
                    case ServiceProviderUpdate.Unavailable:
                        binding.Facade.Clear();
                        break;
                    case ServiceProviderUpdate.Replaced replaced:
                        if (replaced.Snapshot.Instance is not null)
                        {
                            throw new InvalidOperationException("Singleton replacement has an instance address");
                        }
                        binding.Facade.Install(replaced.Snapshot, context);
                        break;
                    case ServiceProviderUpdate.StateUpdate state when state.Instance is null:
                        binding.Facade.Update(state.Member, state.Sequence, state.Ops, context);
                        break;
                }
            }
            catch (Exception error)
            {
                _reportError(error);
            }
            return Task.CompletedTask;
        }, Context.Context.Background).ConfigureAwait(false);

        if (!binding.Active || _disposed || !_bound || binding.Revision != revision)
        {
            await subscription.CloseAsync(Context.Context.Background).ConfigureAwait(false);
            return;
        }
        binding.Subscription = subscription;
        var snapshot = subscription.Snapshot;
        if (snapshot.Mode != ServiceMode.Singleton || snapshot.ServiceId != serviceId
            || snapshot.Instances.Count != 1)
        {
            throw new InvalidOperationException(
                $"Remote service {serviceId} returned an invalid singleton snapshot");
        }
        binding.Facade.Install(snapshot.Instances[0], ReplicatedStates.ServiceDeliveryContext());
        subscription.Activate();
    }

    private void AssertHandleAccess()
    {
        if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
        _assertAccess();
    }

    private static void AssertRemotable<T>(Service<T> service)
    {
        if (service.Local)
        {
            throw new RemoteServiceError("service_not_allowed", $"Service {service.Id} is process-local");
        }
    }

    private void AssertAvailable(string serviceId, ServiceMode mode)
    {
        if (_disposed) throw new InvalidOperationException("Remote service binding is disposed");
        if (!_allowlist.Contains(serviceId))
        {
            throw new RemoteServiceError("service_not_allowed",
                $"Remote service {serviceId} is not allowlisted");
        }
        if (_modes.TryGetValue(serviceId, out var existing) && existing != mode)
        {
            throw new RemoteServiceError("service_mode_mismatch",
                $"Remote service {serviceId} is already used as {Describe(existing)}");
        }
        _modes[serviceId] = mode;
    }

    private static string Describe(ServiceMode mode) => mode == ServiceMode.Keyed ? "keyed" : "singleton";

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // 对齐 TS 的 starting?.catch(() => {})。
        }
    }
}
