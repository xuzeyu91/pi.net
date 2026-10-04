namespace Pi.Chord.Services;

/// <summary>成员快照（已解码）：方法或状态成员。对应 TS <c>ServiceInstanceSnapshot</c> 的成员联合。</summary>
public abstract record ServiceMemberSnapshot
{
    /// <summary>方法成员。</summary>
    public sealed record Method(string Name) : ServiceMemberSnapshot;

    /// <summary>状态成员：已解码的 Op 流。</summary>
    public sealed record State(string Name, long Sequence, IReadOnlyList<Delta.DeltaOp> Ops) : ServiceMemberSnapshot;
}

/// <summary>实例快照（已解码）。对应 TS <c>ServiceInstanceSnapshot</c>。</summary>
public sealed record ServiceInstanceSnapshot(ServiceInstanceAddress? Instance, IReadOnlyList<ServiceMemberSnapshot> Members);

/// <summary>订阅快照（已解码）。对应 TS <c>ServiceSubscriptionSnapshot</c>。</summary>
public sealed record ServiceSubscriptionSnapshot(string ServiceId, ServiceMode Mode, IReadOnlyList<ServiceInstanceSnapshot> Instances);

/// <summary>服务商推送更新（已解码）。对应 TS <c>ServiceProviderUpdate</c>。</summary>
public abstract record ServiceProviderUpdate
{
    /// <summary>状态增量。</summary>
    public sealed record StateUpdate(ServiceInstanceAddress? Instance, string Member, long Sequence, IReadOnlyList<Delta.DeltaOp> Ops) : ServiceProviderUpdate;

    /// <summary>整订阅重置。</summary>
    public sealed record Reset(ServiceSubscriptionSnapshot Snapshot) : ServiceProviderUpdate;

    /// <summary>服务暂不可用。</summary>
    public sealed record Unavailable : ServiceProviderUpdate;

    /// <summary>实例被替换。</summary>
    public sealed record Replaced(ServiceInstanceSnapshot Snapshot) : ServiceProviderUpdate;

    /// <summary>新实例启动。</summary>
    public sealed record Spawned(ServiceInstanceSnapshot Instance) : ServiceProviderUpdate;

    /// <summary>实例关闭。</summary>
    public sealed record Closed(ServiceInstanceAddress Instance) : ServiceProviderUpdate;
}

/// <summary>按（实例地址, 成员名）登记的状态编解码器注册表。对应 TS <c>StateCodecRegistry</c>。</summary>
internal sealed class StateCodecRegistry<T>
{
    private readonly Func<T> _create;
    private readonly Dictionary<string, (ServiceInstanceAddress? Instance, T Codec)> _entries = new();

    public StateCodecRegistry(Func<T> create) => _create = create;

    public void Reset() => _entries.Clear();

    public T Add(ServiceInstanceAddress? instance, string member)
    {
        var key = StateKey(instance, member);
        if (_entries.ContainsKey(key))
            throw new InvalidOperationException($"Duplicate service state {DescribeState(instance, member)}");
        var codec = _create();
        _entries[key] = (instance, codec);
        return codec;
    }

    public T Get(ServiceInstanceAddress? instance, string member)
    {
        if (_entries.TryGetValue(StateKey(instance, member), out var entry)) return entry.Codec;
        throw new InvalidOperationException($"Unknown service state {DescribeState(instance, member)}");
    }

    public void RemoveInstance(ServiceInstanceAddress instance)
    {
        foreach (var key in _entries.Keys.ToList())
        {
            if (SameAddress(_entries[key].Instance, instance)) _entries.Remove(key);
        }
    }

    private static string StateKey(ServiceInstanceAddress? instance, string member)
        => $"{instance?.Key ?? "null"}\u0000{instance?.Generation.ToString() ?? "null"}\u0000{member}";

    private static bool SameAddress(ServiceInstanceAddress? left, ServiceInstanceAddress right)
        => left is not null && left.Key == right.Key && left.Generation == right.Generation;

    private static string DescribeState(ServiceInstanceAddress? instance, string member)
        => instance is null ? member : $"{instance.Key}@{instance.Generation}.{member}";
}

/// <summary>
/// 服务状态编码器：把订阅内每个复制状态的 Op 流压缩为线上格式。
/// 对应 TS <c>createServiceStateEncoder</c>。生命周期规则：reset/unavailable 清空
/// 注册表，closed 移除对应实例（与 TS 一致）。
/// </summary>
public sealed class ServiceStateEncoder
{
    private readonly StateCodecRegistry<Delta.DeltaWire.Encoder> _codecs = new(() => new Delta.DeltaWire.Encoder());

    /// <summary>编码整份订阅快照（注册表重置后逐成员登记编码）。</summary>
    public WireServiceSubscriptionSnapshot EncodeSnapshot(ServiceSubscriptionSnapshot snapshot)
    {
        _codecs.Reset();
        return new WireServiceSubscriptionSnapshot(snapshot.ServiceId, snapshot.Mode,
            snapshot.Instances.Select(instance => EncodeInstance(instance, _codecs)).ToList());
    }

    /// <summary>编码一条推送更新。reset/unavailable 先清空注册表，closed 移除实例（与 TS 顺序一致）。</summary>
    public WireServiceProviderUpdate EncodeUpdate(ServiceProviderUpdate update)
    {
        switch (update)
        {
            case ServiceProviderUpdate.Reset reset:
            {
                _codecs.Reset();
                return new WireServiceProviderUpdate.Reset(new WireServiceSubscriptionSnapshot(
                    reset.Snapshot.ServiceId, reset.Snapshot.Mode,
                    reset.Snapshot.Instances.Select(instance => EncodeInstance(instance, _codecs)).ToList()));
            }
            case ServiceProviderUpdate.StateUpdate state:
                return new WireServiceProviderUpdate.StateUpdate(
                    state.Instance, state.Member, state.Sequence,
                    _codecs.Get(state.Instance, state.Member).Encode(state.Ops.ToList()));
            case ServiceProviderUpdate.Replaced replaced:
            {
                _codecs.Reset();
                return new WireServiceProviderUpdate.Replaced(EncodeInstance(replaced.Snapshot, _codecs));
            }
            case ServiceProviderUpdate.Spawned spawned:
                return new WireServiceProviderUpdate.Spawned(EncodeInstance(spawned.Instance, _codecs));
            case ServiceProviderUpdate.Unavailable:
                _codecs.Reset();
                return new WireServiceProviderUpdate.Unavailable();
            case ServiceProviderUpdate.Closed closed:
                _codecs.RemoveInstance(closed.Instance);
                return new WireServiceProviderUpdate.Closed(closed.Instance);
            default:
                throw new ArgumentException("unknown update type", nameof(update));
        }
    }

    private static WireServiceInstanceSnapshot EncodeInstance(
        ServiceInstanceSnapshot instance, StateCodecRegistry<Delta.DeltaWire.Encoder> codecs)
        => new(instance.Instance, instance.Members.Select(member => member switch
        {
            ServiceMemberSnapshot.State state => (WireServiceMemberSnapshot)new WireServiceMemberSnapshot.State(
                state.Name, state.Sequence, codecs.Add(instance.Instance, state.Name).Encode(state.Ops.ToList())),
            ServiceMemberSnapshot.Method method => new WireServiceMemberSnapshot.Method(method.Name),
            _ => throw new ArgumentException("unknown member kind"),
        }).ToList());
}

/// <summary>服务状态解码器：<see cref="ServiceStateEncoder"/> 的逆过程。对应 TS <c>createServiceStateDecoder</c>。</summary>
public sealed class ServiceStateDecoder
{
    private readonly StateCodecRegistry<Delta.DeltaWire.Decoder> _codecs = new(() => new Delta.DeltaWire.Decoder());

    /// <summary>解码整份订阅快照。</summary>
    public ServiceSubscriptionSnapshot DecodeSnapshot(WireServiceSubscriptionSnapshot snapshot)
    {
        _codecs.Reset();
        return new ServiceSubscriptionSnapshot(snapshot.ServiceId, snapshot.Mode,
            snapshot.Instances.Select(instance => DecodeInstance(instance, _codecs)).ToList());
    }

    /// <summary>解码一条推送更新。</summary>
    public ServiceProviderUpdate DecodeUpdate(WireServiceProviderUpdate update) => update switch
    {
        WireServiceProviderUpdate.Reset reset => new ServiceProviderUpdate.Reset(new ServiceSubscriptionSnapshot(
            reset.Snapshot.ServiceId, reset.Snapshot.Mode,
            reset.Snapshot.Instances.Select(instance => DecodeInstance(instance, _codecs)).ToList())),
        WireServiceProviderUpdate.StateUpdate state => new ServiceProviderUpdate.StateUpdate(
            state.Instance, state.Member, state.Sequence,
            _codecs.Get(state.Instance, state.Member).Decode(state.Ops.ToList())),
        WireServiceProviderUpdate.Replaced replaced => new ServiceProviderUpdate.Replaced(
            DecodeInstance(replaced.Snapshot, _codecs)),
        WireServiceProviderUpdate.Spawned spawned => new ServiceProviderUpdate.Spawned(
            DecodeInstance(spawned.Instance, _codecs)),
        WireServiceProviderUpdate.Unavailable => new ServiceProviderUpdate.Unavailable(),
        WireServiceProviderUpdate.Closed closed => new ServiceProviderUpdate.Closed(closed.Instance),
        _ => throw new ArgumentException("unknown update type", nameof(update)),
    };

    private static ServiceInstanceSnapshot DecodeInstance(
        WireServiceInstanceSnapshot instance, StateCodecRegistry<Delta.DeltaWire.Decoder> codecs)
        => new(instance.Instance, instance.Members.Select(member => member switch
        {
            WireServiceMemberSnapshot.State state => (ServiceMemberSnapshot)new ServiceMemberSnapshot.State(
                state.Name, state.Sequence, codecs.Add(instance.Instance, state.Name).Decode(state.Ops.ToList())),
            WireServiceMemberSnapshot.Method method => new ServiceMemberSnapshot.Method(method.Name),
            _ => throw new ArgumentException("unknown member kind"),
        }).ToList());
}
