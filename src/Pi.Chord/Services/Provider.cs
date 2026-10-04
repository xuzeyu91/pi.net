using Pi.Chord.Context;
using Pi.Chord.Delta;

namespace Pi.Chord.Services;

/// <summary>成员分类结果。对应 TS <c>InstanceMember</c>（method 或 state）。</summary>
internal abstract record InstanceMember
{
    /// <summary>方法成员：参数为 [args..., context]，返回 JSON 值。</summary>
    public sealed record Method(Func<object?[], object?> Invoke) : InstanceMember;

    /// <summary>状态成员：复制状态内部契约。</summary>
    public sealed record State(IReplicatedStateInternals Internals) : InstanceMember;
}

/// <summary>
/// 服务提供者实例（单例或 keyed）。对应 TS <c>ProviderInstance</c>。
/// C# 服务实现为成员字典：方法 → <see cref="Func{T, TResult}"/>，状态 → <see cref="IReplicatedStateInternals"/>。
/// </summary>
internal sealed class ProviderInstance
{
    public required ServiceInstanceAddress? Address { get; init; }
    public required IReadOnlyDictionary<string, InstanceMember> Members { get; init; }
    public required List<IDisposable> RemoveMemberListeners { get; init; }
    public bool Active { get; set; } = true;
}

/// <summary>订阅者：缓冲、快照序列去重、激活/排空状态。对应 TS <c>ProviderSubscriber</c>。</summary>
internal sealed class ProviderSubscriber
{
    public required Action<ServiceProviderUpdate, Context.Context> Listener { get; init; }
    public Queue<(ServiceProviderUpdate Update, Context.Context Context)> Buffer { get; } = new();
    public Dictionary<string, long> SnapshotSequences { get; } = [];
    public bool Active { get; set; }
    public bool Draining { get; set; }
    public bool Terminated { get; set; }
    public bool Closed { get; set; }
}

/// <summary>服务注册表项。对应 TS <c>ServiceRegistration</c>。</summary>
internal sealed class ServiceRegistration
{
    public required string ServiceId { get; init; }
    public required ServiceMode Mode { get; init; }
    public ProviderInstance? Singleton { get; set; }
    public IReadOnlyDictionary<string, string>? SingletonShape { get; set; }
    public Dictionary<string, ProviderInstance> Instances { get; } = [];
    public Dictionary<string, long> Generations { get; } = [];
    public HashSet<ProviderSubscriber> Subscribers { get; } = [];
}

/// <summary>服务订阅句柄。对应 TS <c>ServiceSubscription</c>。</summary>
public sealed record ServiceSubscriptionSnapshotHandle(
    ServiceSubscriptionSnapshot Snapshot,
    Action Activate,
    Action Close);

/// <summary>更新发布委托（endpoint 把更新推给远程消费者）。对应 TS <c>ServiceUpdatePublisher</c>。</summary>
public delegate Task ServiceUpdatePublisher(
    string subscriptionId, ServiceProviderUpdate update, Context.Context context);

/// <summary>
/// 远程服务提供者：托管一个 provider，面向一个远程消费者。对应 TS <c>RemoteServiceProvider</c>。
/// </summary>
public sealed class RemoteServiceProvider
{
    private readonly IReadOnlyList<ServiceCatalogueEntry> _catalogue;
    private readonly Dictionary<string, ServiceRegistration> _registrations = [];
    private bool _disposed;

    /// <summary>定义项（服务 id + 模式；默认 singleton）。</summary>
    public sealed record ProviderDefinition(string ServiceId, ServiceMode Mode = ServiceMode.Singleton, bool Local = false);

    public RemoteServiceProvider(IEnumerable<ProviderDefinition> entries)
    {
        var definitions = entries.ToList();
        foreach (var definition in definitions)
        {
            if (definition.Local)
                throw new ArgumentException($"Local service {definition.ServiceId} cannot be published remotely");
        }
        var ids = definitions.Select(d => d.ServiceId).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Remote service catalogue contains duplicate IDs");
        _catalogue = definitions
            .Select(d => new ServiceCatalogueEntry(d.ServiceId, d.Mode))
            .ToList();
        foreach (var definition in definitions)
        {
            _registrations[definition.ServiceId] = new ServiceRegistration
            {
                ServiceId = definition.ServiceId,
                Mode = definition.Mode,
            };
        }
    }

    public IReadOnlyList<ServiceCatalogueEntry> Catalogue => _catalogue;

    /// <summary>提供单例实现。</summary>
    public void Provide(string serviceId, IReadOnlyDictionary<string, object> implementation)
    {
        AssertActive();
        AssertAllowed(serviceId);
        var registration = Registration(serviceId, ServiceMode.Singleton);
        if (registration.Singleton is not null)
            throw new RemoteServiceError("service_mode_mismatch", $"Remote service {serviceId} already has a provider");
        var classified = ClassifyImplementation(serviceId, implementation);
        var shape = ServiceMemberShape(classified);
        AssertSingletonShape(registration, shape);
        registration.Singleton = CreateInstance(registration, classified, null);
        registration.SingletonShape = shape;
        LocalImplementations[registration] = implementation;
    }

    /// <summary>断开单例但保留活动订阅与远程门面。对应 TS <c>withdraw</c>。</summary>
    public void Withdraw(string serviceId)
    {
        AssertActive();
        AssertAllowed(serviceId);
        var registration = Registration(serviceId, ServiceMode.Singleton);
        var previous = registration.Singleton;
        if (previous is null) return;
        previous.Active = false;
        foreach (var remove in previous.RemoveMemberListeners) remove.Dispose();
        registration.Singleton = null;
        Emit(registration, new ServiceProviderUpdate.Unavailable());
    }

    /// <summary>无活动变更地校验单例替换形状。对应 TS <c>validateReplacement</c>。</summary>
    public void ValidateReplacement(string serviceId, IReadOnlyDictionary<string, object> implementation)
    {
        AssertActive();
        AssertAllowed(serviceId);
        var registration = Registration(serviceId, ServiceMode.Singleton);
        var classified = ClassifyImplementation(serviceId, implementation);
        AssertSingletonShape(registration, ServiceMemberShape(classified));
    }

    /// <summary>替换单例但保持稳定远程门面可用。对应 TS <c>replace</c>。</summary>
    public void Replace(string serviceId, IReadOnlyDictionary<string, object> implementation)
    {
        AssertActive();
        AssertAllowed(serviceId);
        var registration = Registration(serviceId, ServiceMode.Singleton);
        var classified = ClassifyImplementation(serviceId, implementation);
        var shape = ServiceMemberShape(classified);
        AssertSingletonShape(registration, shape);
        var replacement = CreateInstance(registration, classified, null);
        var previous = registration.Singleton;
        if (previous is not null)
        {
            previous.Active = false;
            foreach (var remove in previous.RemoveMemberListeners) remove.Dispose();
        }
        registration.Singleton = replacement;
        registration.SingletonShape = shape;
        LocalImplementations[registration] = implementation;
        Emit(registration, new ServiceProviderUpdate.Replaced(SnapshotInstance(replacement)));
    }

    /// <summary>取本地单例实现。对应 TS <c>use</c>。</summary>
    public IReadOnlyDictionary<string, object> Use(string serviceId)
    {
        AssertActive();
        AssertAllowed(serviceId);
        if (!_registrations.TryGetValue(serviceId, out var registration)
            || registration.Mode != ServiceMode.Singleton
            || registration.Singleton is null)
        {
            throw new RemoteServiceError("service_not_found", $"Remote service {serviceId} has no local provider");
        }
        return LocalImplementations[registration];
    }

    /// <summary>启动一个 keyed 实例并返回关闭委托。对应 TS <c>spawn</c>。</summary>
    public Action Spawn(string serviceId, string key, IReadOnlyDictionary<string, object> implementation)
    {
        AssertActive();
        AssertAllowed(serviceId);
        if (key.Length == 0)
            throw new ArgumentException("Remote service instance key must not be empty");
        var registration = Registration(serviceId, ServiceMode.Keyed);
        if (registration.Instances.ContainsKey(key))
        {
            throw new RemoteServiceError("service_mode_mismatch",
                $"Remote service {serviceId} already has a live instance with key {key}");
        }
        var generation = (registration.Generations.GetValueOrDefault(key)) + 1;
        registration.Generations[key] = generation;
        var address = new ServiceInstanceAddress(key, generation);
        var classified = ClassifyImplementation(serviceId, implementation);
        var instance = CreateInstance(registration, classified, address);
        registration.Instances[key] = instance;
        Emit(registration, new ServiceProviderUpdate.Spawned(SnapshotInstance(instance)));
        var closed = false;
        return () =>
        {
            if (closed) return;
            closed = true;
            if (!ReferenceEquals(registration.Instances.GetValueOrDefault(key), instance)) return;
            instance.Active = false;
            foreach (var remove in instance.RemoveMemberListeners) remove.Dispose();
            registration.Instances.Remove(key);
            Emit(registration, new ServiceProviderUpdate.Closed(address));
        };
    }

    /// <summary>本地实现字典（Use 的返回）。</summary>
    internal Dictionary<ServiceRegistration, IReadOnlyDictionary<string, object>> LocalImplementations { get; } = [];

    /// <summary>调用远程方法成员。对应 TS <c>invoke</c>。</summary>
    public async Task<object?> Invoke(ServiceCall call, Context.Context context)
    {
        AssertActive();
        AssertAllowed(call.ServiceId);
        if (!_registrations.TryGetValue(call.ServiceId, out var registration))
            throw new RemoteServiceError("service_not_found", $"Unknown remote service {call.ServiceId}");
        var instance = ResolveInstance(registration, call.Instance);
        if (!instance.Members.TryGetValue(call.Member, out var member))
            throw new RemoteServiceError("service_member_not_found",
                $"Unknown remote service member {call.ServiceId}.{call.Member}");
        if (member is not InstanceMember.Method method)
            throw new RemoteServiceError("service_member_mismatch",
                $"Remote service member {call.ServiceId}.{call.Member} is not a method");
        var arguments = call.Args.Append<object?>(context).ToArray();
        return await Task.FromResult(method.Invoke(arguments)).ConfigureAwait(false);
    }

    /// <summary>订阅服务状态流。对应 TS <c>subscribe</c>。</summary>
    public ServiceSubscriptionSnapshotHandle Subscribe(
        string serviceId, ServiceMode mode,
        Action<ServiceProviderUpdate, Context.Context> listener)
    {
        AssertActive();
        AssertAllowed(serviceId);
        var registration = Registration(serviceId, mode);
        if (registration.Mode == ServiceMode.Singleton && registration.Singleton is null)
            throw new RemoteServiceError("service_not_found", $"Remote service {serviceId} has no provider");
        var subscriber = new ProviderSubscriber { Listener = listener };
        registration.Subscribers.Add(subscriber);
        var snapshot = Snapshot(registration);
        RecordSnapshotSequences(subscriber.SnapshotSequences, snapshot.Instances);
        return new ServiceSubscriptionSnapshotHandle(
            snapshot,
            Activate: () =>
            {
                if (subscriber.Closed || subscriber.Active) return;
                subscriber.Active = true;
                ThrowCollectedErrors(DrainSubscriber(subscriber),
                    "Failed to activate remote service subscription");
            },
            Close: () =>
            {
                if (subscriber.Closed) return;
                subscriber.Closed = true;
                subscriber.Buffer.Clear();
                registration.Subscribers.Remove(subscriber);
            });
    }

    /// <summary>释放全部实例与订阅。对应 TS <c>dispose</c>。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var errors = new List<Exception>();
        foreach (var registration in _registrations.Values)
        {
            var singleton = registration.Singleton;
            if (singleton is not null)
            {
                singleton.Active = false;
                foreach (var remove in singleton.RemoveMemberListeners) remove.Dispose();
                registration.Singleton = null;
                try
                {
                    Emit(registration, new ServiceProviderUpdate.Unavailable());
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            foreach (var (key, instance) in registration.Instances.ToList())
            {
                instance.Active = false;
                foreach (var remove in instance.RemoveMemberListeners) remove.Dispose();
                registration.Instances.Remove(key);
                try
                {
                    Emit(registration, new ServiceProviderUpdate.Closed(instance.Address!));
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            foreach (var subscriber in registration.Subscribers)
            {
                if (subscriber.Active && !subscriber.Draining)
                {
                    subscriber.Closed = true;
                    subscriber.Buffer.Clear();
                }
                else
                {
                    subscriber.Terminated = true;
                }
            }
            registration.Subscribers.Clear();
        }
        _registrations.Clear();
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Failed to dispose remote service provider", errors);
    }

    // ─── 内部 ───────────────────────────────────────────────────────────

    private ServiceRegistration Registration(string serviceId, ServiceMode mode)
    {
        if (!_registrations.TryGetValue(serviceId, out var registration))
            throw new RemoteServiceError("service_not_found", $"Unknown remote service {serviceId}");
        if (registration.Mode != mode)
            throw new RemoteServiceError("service_mode_mismatch",
                $"Remote service {serviceId} is {registration.Mode}, not {mode}");
        return registration;
    }

    private ProviderInstance CreateInstance(
        ServiceRegistration registration,
        IReadOnlyDictionary<string, InstanceMember> classified,
        ServiceInstanceAddress? address)
    {
        var removeMemberListeners = new List<IDisposable>();
        var instance = new ProviderInstance
        {
            Address = address,
            Members = classified,
            RemoveMemberListeners = removeMemberListeners,
        };
        foreach (var (name, member) in classified)
        {
            if (member is not InstanceMember.State stateMember) continue;
            var captured = stateMember.Internals;
            var memberName = name;
            removeMemberListeners.Add(captured.SubscribeSource((ops, sequence, context) =>
            {
                if (!instance.Active) return;
                Emit(registration, new ServiceProviderUpdate.StateUpdate(
                    address, memberName, sequence, ops), context);
            }));
        }
        return instance;
    }

    private ProviderInstance ResolveInstance(ServiceRegistration registration, ServiceInstanceAddress? instance)
    {
        if (instance is null)
        {
            if (registration.Mode == ServiceMode.Singleton && registration.Singleton is not null)
                return registration.Singleton;
            throw new RemoteServiceError("service_instance_not_found",
                $"Remote service {registration.ServiceId} requires an instance address");
        }
        if (!registration.Instances.TryGetValue(instance.Key, out var found)
            || found.Address!.Generation != instance.Generation)
        {
            throw new RemoteServiceError("service_stale_instance",
                $"Remote service {registration.ServiceId} has no instance {instance.Key}@{instance.Generation}");
        }
        return found;
    }

    private ServiceSubscriptionSnapshot Snapshot(ServiceRegistration registration)
        => new(registration.ServiceId, registration.Mode,
            registration.Singleton is not null
                ? [SnapshotInstance(registration.Singleton)]
                : registration.Instances.Values.Select(SnapshotInstance).ToList());

    private ServiceInstanceSnapshot SnapshotInstance(ProviderInstance instance)
    {
        var members = new List<ServiceMemberSnapshot>();
        foreach (var (name, member) in instance.Members)
        {
            if (member is InstanceMember.Method)
            {
                members.Add(new ServiceMemberSnapshot.Method(name));
            }
            else
            {
                var (value, sequence) = ((InstanceMember.State)member).Internals.Snapshot();
                // 快照成员是根替换 op（r 无路径，已解码与 wire 同构）。
                members.Add(new ServiceMemberSnapshot.State(name, sequence,
                    [new DeltaOp.Replace(value)]));
            }
        }
        return new ServiceInstanceSnapshot(instance.Address, members);
    }

    private void Emit(ServiceRegistration registration, ServiceProviderUpdate update, Context.Context? context = null)
    {
        if (registration.Subscribers.Count == 0) return;
        var deliveryContext = context ?? ReplicatedStates.ServiceDeliveryContext();
        var errors = new List<Exception>();
        var subscribers = registration.Subscribers.ToArray();
        // 用户代码（含重入发布）之前，为所有人入队。
        foreach (var subscriber in subscribers)
        {
            if (subscriber.Closed || UpdateCoveredBySnapshot(subscriber.SnapshotSequences, update)) continue;
            if (subscriber.Buffer.Count == 100)
            {
                var snapshot = Snapshot(registration);
                subscriber.Buffer.Clear();
                subscriber.SnapshotSequences.Clear();
                RecordSnapshotSequences(subscriber.SnapshotSequences, snapshot.Instances);
                subscriber.Buffer.Enqueue((new ServiceProviderUpdate.Reset(snapshot), deliveryContext));
            }
            else
            {
                subscriber.Buffer.Enqueue((update, deliveryContext));
            }
        }
        foreach (var subscriber in subscribers) errors.AddRange(DrainSubscriber(subscriber));
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1)
            throw new AggregateException($"Failed to publish remote service {registration.ServiceId} update", errors);
    }

    private static List<Exception> DrainSubscriber(ProviderSubscriber subscriber)
    {
        if (!subscriber.Active || subscriber.Closed || subscriber.Draining) return [];
        var errors = new List<Exception>();
        subscriber.Draining = true;
        try
        {
            while (!subscriber.Closed)
            {
                if (!subscriber.Buffer.TryDequeue(out var entry)) break;
                try
                {
                    subscriber.Listener(entry.Update, entry.Context);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
        finally
        {
            subscriber.Draining = false;
            if (subscriber.Terminated) subscriber.Closed = true;
        }
        return errors;
    }

    private static void RecordSnapshotSequences(
        Dictionary<string, long> sequences, IReadOnlyList<ServiceInstanceSnapshot> instances)
    {
        foreach (var instance in instances)
        {
            foreach (var member in instance.Members)
            {
                if (member is ServiceMemberSnapshot.State state)
                    sequences[StateMemberKey(instance.Instance, state.Name)] = state.Sequence;
            }
        }
    }

    private static bool UpdateCoveredBySnapshot(Dictionary<string, long> sequences, ServiceProviderUpdate update)
    {
        switch (update)
        {
            case ServiceProviderUpdate.StateUpdate state:
            {
                var key = StateMemberKey(state.Instance, state.Member);
                if (!sequences.TryGetValue(key, out var sequence)) return false;
                if (state.Sequence <= sequence) return true;
                sequences.Remove(key);
                return false;
            }
            case ServiceProviderUpdate.Reset reset:
                sequences.Clear();
                RecordSnapshotSequences(sequences, reset.Snapshot.Instances);
                return false;
            case ServiceProviderUpdate.Replaced replaced:
                sequences.Clear();
                RecordSnapshotSequences(sequences, [replaced.Snapshot]);
                return false;
            case ServiceProviderUpdate.Spawned spawned:
                RecordSnapshotSequences(sequences, [spawned.Instance]);
                return false;
            case ServiceProviderUpdate.Unavailable:
                sequences.Clear();
                return false;
            case ServiceProviderUpdate.Closed closed:
            {
                // 该实例（全部代）的状态键移除：键前缀 = key\0。
                var prefix = $"{closed.Instance.Key}\u0000";
                foreach (var key in sequences.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    sequences.Remove(key);
                return false;
            }
            default:
                return false;
        }
    }

    private static string StateMemberKey(ServiceInstanceAddress? instance, string member)
        => instance is null ? member : $"{instance.Key}\u0000{instance.Generation}\u0000{member}";

    private void AssertAllowed(string serviceId)
    {
        if (!_registrations.ContainsKey(serviceId))
            throw new RemoteServiceError("service_not_allowed", $"Remote service {serviceId} is not allowlisted");
    }

    private void AssertActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteServiceProvider));
    }

    private static void AssertSingletonShape(
        ServiceRegistration registration, IReadOnlyDictionary<string, string> shape)
    {
        if (registration.SingletonShape is null) return;
        if (registration.SingletonShape.Count != shape.Count
            || registration.SingletonShape.Any(kv => !shape.TryGetValue(kv.Key, out var kind) || kind != kv.Value))
        {
            throw new RemoteServiceError("service_member_mismatch",
                $"Remote service {registration.ServiceId} provider shape changed");
        }
    }

    private static IReadOnlyDictionary<string, string> ServiceMemberShape(
        IReadOnlyDictionary<string, InstanceMember> members)
        => members.ToDictionary(kv => kv.Key, kv => kv.Value is InstanceMember.Method ? "method" : "state");

    /// <summary>成员分类：方法（Func）或状态（IReplicatedStateInternals）。对应 TS <c>classifyRemoteServiceImplementation</c>。</summary>
    internal static IReadOnlyDictionary<string, InstanceMember> ClassifyImplementation(
        string serviceId, IReadOnlyDictionary<string, object> implementation)
    {
        var members = new Dictionary<string, InstanceMember>();
        foreach (var name in implementation.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var value = implementation[name];
            switch (value)
            {
                case Func<object?[], object?> method:
                    members[name] = new InstanceMember.Method(method);
                    continue;
                case IReplicatedStateInternals state:
                    members[name] = new InstanceMember.State(state);
                    continue;
                default:
                    throw new ArgumentException(
                        $"Remote service member {serviceId}.{name} is not remotely exposable");
            }
        }
        if (members.Count == 0)
            throw new ArgumentException($"Remote service {serviceId} has no members");
        return members;
    }

    private static void ThrowCollectedErrors(IReadOnlyList<Exception> errors, string message)
    {
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException(message, errors);
    }
}

/// <summary>
/// 面向一个远程消费者的服务端点：解码控制调用（catalogue/subscribe/unsubscribe）
/// 并转发普通调用。对应 TS <c>createRemoteServiceEndpoint</c>。
/// </summary>
public sealed class RemoteServiceEndpoint : IDisposable
{
    private readonly RemoteServiceProvider _provider;
    private readonly Dictionary<string, ServiceSubscriptionSnapshotHandle> _subscriptions = [];
    private bool _disposed;

    private RemoteServiceEndpoint(RemoteServiceProvider provider) => _provider = provider;

    /// <summary>创建端点。</summary>
    public static RemoteServiceEndpoint Create(RemoteServiceProvider provider) => new(provider);

    /// <summary>
    /// 处理一次调用：控制调用就地处理，普通调用转发 provider。
    /// 返回 (是控制调用, 结果或快照)。
    /// </summary>
    public async Task<(bool IsControl, object? Result)> InvokeAsync(
        ServiceCall call,
        ServiceUpdatePublisher publish,
        Context.Context context)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteServiceEndpoint));
        var control = ServiceWire.DecodeServiceControlCall(call);
        switch (control)
        {
            case ServiceControlCall.Catalogue:
                return (true, _provider.Catalogue);
            case ServiceControlCall.Subscribe subscribe:
            {
                if (_subscriptions.ContainsKey(subscribe.SubscriptionId))
                    throw new InvalidOperationException("Service subscription ID is already active");
                var handle = _provider.Subscribe(subscribe.ServiceId, subscribe.Mode, (update, updateContext) =>
                {
                    // 同步完成的发布就地处理（对齐 TS 微任务时序）；否则后台尽力而为。
                    try
                    {
                        var task = publish(subscribe.SubscriptionId, update, updateContext);
                        if (!task.IsCompleted)
                        {
                            _ = task.ContinueWith(
                                _ => { }, CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                    }
                    catch
                    {
                        // 发布失败不影响 provider。
                    }
                });
                _subscriptions[subscribe.SubscriptionId] = handle;
                handle.Activate();
                return (true, handle.Snapshot);
            }
            case ServiceControlCall.Unsubscribe unsubscribe:
            {
                if (!_subscriptions.TryGetValue(unsubscribe.SubscriptionId, out var subscription))
                    throw new InvalidOperationException("Service subscription was not found");
                subscription.Close();
                _subscriptions.Remove(unsubscribe.SubscriptionId);
                return (true, null);
            }
            default:
                return (false, await _provider.Invoke(call, context).ConfigureAwait(false));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions.Values) subscription.Close();
        _subscriptions.Clear();
    }
}
