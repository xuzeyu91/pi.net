namespace Pi.Chord.Services;

/// <summary>服务订阅模式。对应 TS <c>ServiceMode</c>。</summary>
public enum ServiceMode
{
    /// <summary>单例：整个服务器一个实例。</summary>
    Singleton,

    /// <summary>按键：每个实例键一个实例。</summary>
    Keyed,
}

/// <summary>服务实例地址。对应 TS <c>ServiceInstanceAddress</c>。</summary>
public sealed record ServiceInstanceAddress(string Key, long Generation);

/// <summary>服务目录条目。对应 TS <c>ServiceCatalogueEntry</c>。</summary>
public sealed record ServiceCatalogueEntry(string ServiceId, ServiceMode Mode);

/// <summary>服务调用：{ serviceId, member, args, instance? }。对应 TS <c>ServiceCall</c>。</summary>
public sealed record ServiceCall(string ServiceId, string Member, IReadOnlyList<object?> Args, ServiceInstanceAddress? Instance = null);

/// <summary>成员快照：方法（无状态）或状态成员（带序列号与 ops）。对应 TS <c>WireServiceMemberSnapshot</c>。</summary>
public abstract record WireServiceMemberSnapshot
{
    /// <summary>方法成员：服务器侧调用，无复制状态。</summary>
    public sealed record Method(string Name) : WireServiceMemberSnapshot;

    /// <summary>状态成员：按序列号递增的 delta 流。</summary>
    public sealed record State(string Name, long Sequence, IReadOnlyList<List<object?>> Ops) : WireServiceMemberSnapshot;
}

/// <summary>实例快照：可选地址 + 成员列表。对应 TS <c>WireServiceInstanceSnapshot</c>。</summary>
public sealed record WireServiceInstanceSnapshot(ServiceInstanceAddress? Instance, IReadOnlyList<WireServiceMemberSnapshot> Members);

/// <summary>订阅快照：服务 id + 模式 + 实例列表。对应 TS <c>WireServiceSubscriptionSnapshot</c>。</summary>
public sealed record WireServiceSubscriptionSnapshot(string ServiceId, ServiceMode Mode, IReadOnlyList<WireServiceInstanceSnapshot> Instances);

/// <summary>服务商推送更新。对应 TS <c>WireServiceProviderUpdate</c>。</summary>
public abstract record WireServiceProviderUpdate
{
    /// <summary>状态成员的增量推进。</summary>
    public sealed record StateUpdate(ServiceInstanceAddress? Instance, string Member, long Sequence, IReadOnlyList<List<object?>> Ops) : WireServiceProviderUpdate;

    /// <summary>整订阅重置（快照里必须只含根替换 ops）。</summary>
    public sealed record Reset(WireServiceSubscriptionSnapshot Snapshot) : WireServiceProviderUpdate;

    /// <summary>服务暂时不可用。</summary>
    public sealed record Unavailable : WireServiceProviderUpdate;

    /// <summary>实例被替换。</summary>
    public sealed record Replaced(WireServiceInstanceSnapshot Snapshot) : WireServiceProviderUpdate;

    /// <summary>新实例启动。</summary>
    public sealed record Spawned(WireServiceInstanceSnapshot Instance) : WireServiceProviderUpdate;

    /// <summary>实例关闭。</summary>
    public sealed record Closed(ServiceInstanceAddress Instance) : WireServiceProviderUpdate;
}

/// <summary>控制通道调用语义（<c>$chord.service</c>）。对应 TS <c>ServiceControlCall</c>。</summary>
public abstract record ServiceControlCall
{
    /// <summary>列举服务目录。</summary>
    public sealed record Catalogue : ServiceControlCall;

    /// <summary>订阅服务。</summary>
    public sealed record Subscribe(string SubscriptionId, string ServiceId, ServiceMode Mode) : ServiceControlCall;

    /// <summary>退订。</summary>
    public sealed record Unsubscribe(string SubscriptionId) : ServiceControlCall;
}

/// <summary>服务 wire 协议：控制调用构造/解析、快照与更新的校验。对应 TS <c>services/wire.ts</c>。</summary>
public static class ServiceWire
{
    private const string ServiceControlId = "$chord.service";
    private const string CatalogueMember = "catalogue";
    private const string SubscribeMember = "subscribe";
    private const string UnsubscribeMember = "unsubscribe";

    // ─── 控制调用构造 ──────────────────────────────────────────────────────

    /// <summary>构造服务目录列举调用。</summary>
    public static ServiceCall CreateServiceCatalogueCall() => new(ServiceControlId, CatalogueMember, []);

    /// <summary>构造订阅调用。</summary>
    public static ServiceCall CreateServiceSubscribeCall(string subscriptionId, string serviceId, ServiceMode mode)
        => new(ServiceControlId, SubscribeMember,
            [subscriptionId, serviceId, mode == ServiceMode.Singleton ? "singleton" : "keyed"]);

    /// <summary>构造退订调用。</summary>
    public static ServiceCall CreateServiceUnsubscribeCall(string subscriptionId)
        => new(ServiceControlId, UnsubscribeMember, [subscriptionId]);

    /// <summary>解析控制通道调用；非控制调用返回 null。对应 TS <c>decodeServiceControlCall</c>。</summary>
    public static ServiceControlCall? DecodeServiceControlCall(ServiceCall call)
    {
        if (call.ServiceId != ServiceControlId || call.Instance is not null) return null;
        if (call.Member == CatalogueMember && call.Args.Count == 0) return new ServiceControlCall.Catalogue();
        if (call.Member == SubscribeMember && call.Args.Count == 3
            && IsId(call.Args[0]) && IsId(call.Args[1])
            && call.Args[2] is "singleton" or "keyed")
        {
            return new ServiceControlCall.Subscribe((string)call.Args[0]!, (string)call.Args[1]!,
                (string)call.Args[2]! == "singleton" ? ServiceMode.Singleton : ServiceMode.Keyed);
        }
        if (call.Member == UnsubscribeMember && call.Args.Count == 1 && IsId(call.Args[0]))
            return new ServiceControlCall.Unsubscribe((string)call.Args[0]!);
        return null;
    }

    // ─── 解析与校验 ───────────────────────────────────────────────────────

    /// <summary>解析服务调用（CBOR 值模型 → <see cref="ServiceCall"/>）。对应 TS <c>parseServiceCall</c>。</summary>
    public static ServiceCall ParseServiceCall(object? value)
    {
        var map = Record(value, "service call");
        AssertKeys(map, ["serviceId", "member", "args"], ["instance"], "service call");
        if (!IsId(map.GetValueOrDefault("serviceId")) || !IsId(map.GetValueOrDefault("member"))
            || map.GetValueOrDefault("args") is not List<object?> args)
            throw new InvalidOperationException("Invalid service call");
        ServiceInstanceAddress? instance = map.TryGetValue("instance", out var raw) && raw is not null
            ? AssertAddress(raw) : null;
        return new ServiceCall((string)map["serviceId"]!, (string)map["member"]!, args, instance);
    }

    /// <summary>解析服务目录。对应 TS <c>parseServiceCatalogue</c>。</summary>
    public static IReadOnlyList<ServiceCatalogueEntry> ParseServiceCatalogue(object? value)
    {
        if (value is not List<object?> list) throw new InvalidOperationException("Invalid service catalogue");
        var seen = new HashSet<string>();
        var entries = new List<ServiceCatalogueEntry>(list.Count);
        foreach (var candidate in list)
        {
            var entry = Record(candidate, "service catalogue entry");
            AssertKeys(entry, ["serviceId", "mode"], [], "service catalogue entry");
            var serviceId = entry.GetValueOrDefault("serviceId") as string;
            if (!IsId(serviceId) || !IsMode(entry.GetValueOrDefault("mode"), out var mode) || !seen.Add(serviceId!))
                throw new InvalidOperationException("Invalid service catalogue");
            entries.Add(new ServiceCatalogueEntry(serviceId!, mode));
        }
        return entries;
    }

    /// <summary>解析订阅快照（已解码 ops）。对应 TS <c>parseServiceSubscriptionSnapshot</c>。</summary>
    public static WireServiceSubscriptionSnapshot ParseServiceSubscriptionSnapshot(object? value)
        => AssertSubscriptionSnapshot(value, requireOps: true);

    /// <summary>解析线上订阅快照（wire ops，可能含 id 引用）。对应 TS <c>parseWireServiceSubscriptionSnapshot</c>。</summary>
    public static WireServiceSubscriptionSnapshot ParseWireServiceSubscriptionSnapshot(object? value)
        => AssertSubscriptionSnapshot(value, requireOps: false);

    /// <summary>解析服务商更新（已解码 ops）。对应 TS <c>parseServiceProviderUpdate</c>。</summary>
    public static WireServiceProviderUpdate ParseServiceProviderUpdate(object? value)
        => AssertProviderUpdate(value, requireOps: true);

    /// <summary>解析线上服务商更新。对应 TS <c>parseWireServiceProviderUpdate</c>。</summary>
    public static WireServiceProviderUpdate ParseWireServiceProviderUpdate(object? value)
        => AssertProviderUpdate(value, requireOps: false);

    // ─── 内部校验 ────────────────────────────────────────────────────────

    private static WireServiceSubscriptionSnapshot AssertSubscriptionSnapshot(object? value, bool requireOps)
    {
        var snapshot = Record(value, "service subscription snapshot");
        AssertKeys(snapshot, ["serviceId", "mode", "instances"], [], "service subscription snapshot");
        var serviceId = snapshot.GetValueOrDefault("serviceId") as string;
        if (!IsId(serviceId) || !IsMode(snapshot.GetValueOrDefault("mode"), out var mode)
            || snapshot.GetValueOrDefault("instances") is not List<object?> instances)
            throw new InvalidOperationException("Invalid service subscription snapshot");
        var parsed = instances.Select(instance => AssertInstance(instance, requireOps)).ToList();
        return new WireServiceSubscriptionSnapshot(serviceId!, mode, parsed);
    }

    private static WireServiceProviderUpdate AssertProviderUpdate(object? value, bool requireOps)
    {
        var update = Record(value, "service provider update");
        var type = update.GetValueOrDefault("type") as string;
        switch (type)
        {
            case "state":
            {
                AssertKeys(update, ["type", "member", "sequence", "ops"], ["instance"], "state update");
                var member = update.GetValueOrDefault("member") as string;
                if (!IsId(member) || !IsInteger(update.GetValueOrDefault("sequence"), 1)
                    || update.GetValueOrDefault("ops") is not List<object?> rawOps)
                    throw new InvalidOperationException("Invalid service state update");
                ServiceInstanceAddress? instance = update.TryGetValue("instance", out var raw) && raw is not null
                    ? AssertAddress(raw) : null;
                var ops = AssertOps(rawOps, requireOps);
                return new WireServiceProviderUpdate.StateUpdate(instance, member!, (long)update["sequence"]!, ops);
            }
            case "reset":
            {
                AssertKeys(update, ["type", "snapshot"], [], "reset update");
                var snapshot = AssertSubscriptionSnapshot(update.GetValueOrDefault("snapshot"), requireOps);
                foreach (var instance in snapshot.Instances)
                {
                    foreach (var member in instance.Members)
                    {
                        if (member is WireServiceMemberSnapshot.State state
                            && (state.Ops.Count != 1 || !IsRootReplace(state.Ops[0])))
                        {
                            throw new InvalidOperationException("Service reset must contain full root replacements");
                        }
                    }
                }
                return new WireServiceProviderUpdate.Reset(snapshot);
            }
            case "unavailable":
                AssertKeys(update, ["type"], [], "unavailable update");
                return new WireServiceProviderUpdate.Unavailable();
            case "replaced":
                AssertKeys(update, ["type", "snapshot"], [], "replacement update");
                return new WireServiceProviderUpdate.Replaced(AssertInstance(update.GetValueOrDefault("snapshot"), requireOps));
            case "spawned":
                AssertKeys(update, ["type", "instance"], [], "spawn update");
                return new WireServiceProviderUpdate.Spawned(AssertInstance(update.GetValueOrDefault("instance"), requireOps));
            case "closed":
                AssertKeys(update, ["type", "instance"], [], "close update");
                return new WireServiceProviderUpdate.Closed(AssertAddress(update.GetValueOrDefault("instance")!));
            default:
                throw new InvalidOperationException("Invalid service provider update");
        }
    }

    /// <summary>根替换检查：唯一 op 必须是 <c>["r", …]</c>。</summary>
    private static bool IsRootReplace(List<object?> op) => op.Count > 0 && op[0] is "r";

    private static WireServiceInstanceSnapshot AssertInstance(object? value, bool requireOps)
    {
        var instance = Record(value, "service instance snapshot");
        AssertKeys(instance, ["members"], ["instance"], "service instance snapshot");
        ServiceInstanceAddress? address = instance.TryGetValue("instance", out var raw) && raw is not null
            ? AssertAddress(raw) : null;
        if (instance.GetValueOrDefault("members") is not List<object?> members)
            throw new InvalidOperationException("Invalid service instance snapshot");
        var parsed = members.Select(candidate => AssertMember(candidate, requireOps)).ToList();
        return new WireServiceInstanceSnapshot(address, parsed);
    }

    private static WireServiceMemberSnapshot AssertMember(object? value, bool requireOps)
    {
        var member = Record(value, "service member snapshot");
        var kind = member.GetValueOrDefault("kind") as string;
        if (kind == "method")
        {
            AssertKeys(member, ["name", "kind"], [], "service method snapshot");
            var name = member.GetValueOrDefault("name") as string;
            if (!IsId(name)) throw new InvalidOperationException("Invalid service method snapshot");
            return new WireServiceMemberSnapshot.Method(name!);
        }
        if (kind == "state")
        {
            AssertKeys(member, ["name", "kind", "sequence", "ops"], [], "service state snapshot");
            var name = member.GetValueOrDefault("name") as string;
            if (!IsId(name) || !IsInteger(member.GetValueOrDefault("sequence"), 0)
                || member.GetValueOrDefault("ops") is not List<object?> rawOps)
                throw new InvalidOperationException("Invalid service state snapshot");
            return new WireServiceMemberSnapshot.State(name!, (long)member["sequence"]!, AssertOps(rawOps, requireOps));
        }
        throw new InvalidOperationException("Invalid service member snapshot");
    }

    private static List<List<object?>> AssertOps(List<object?> rawOps, bool requireOps)
    {
        var ops = rawOps.OfType<List<object?>>().ToList();
        if (ops.Count != rawOps.Count) throw new InvalidOperationException("Invalid op tuple");
        if (requireOps)
        {
            foreach (var op in ops) Delta.PathSafety.AssertValidOp(Delta.DeltaWire.DecodeSingle(op));
        }
        else
        {
            foreach (var op in ops) Delta.DeltaWire.AssertValidWireOp(op);
        }
        return ops;
    }

    private static ServiceInstanceAddress AssertAddress(object? value)
    {
        var address = Record(value, "service instance address");
        AssertKeys(address, ["key", "generation"], [], "service instance address");
        var key = address.GetValueOrDefault("key") as string;
        if (!IsId(key) || !IsInteger(address.GetValueOrDefault("generation"), 1))
            throw new InvalidOperationException("Invalid service instance address");
        return new ServiceInstanceAddress(key!, (long)address["generation"]!);
    }

    private static Dictionary<string, object?> Record(object? value, string description)
    {
        if (value is not Dictionary<string, object?> map)
            throw new InvalidOperationException($"Invalid {description}");
        return map;
    }

    private static void AssertKeys(
        Dictionary<string, object?> value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional,
        string description)
    {
        var allowed = required.Concat(optional).ToHashSet();
        if (required.Any(key => !value.ContainsKey(key)) || value.Keys.Any(key => !allowed.Contains(key)))
            throw new InvalidOperationException($"Invalid {description}");
    }

    private static bool IsId(object? value) => value is string { Length: > 0 };

    private static bool IsMode(object? value, out ServiceMode mode)
    {
        switch (value)
        {
            case "singleton": mode = ServiceMode.Singleton; return true;
            case "keyed": mode = ServiceMode.Keyed; return true;
            default: mode = default; return false;
        }
    }

    private static bool IsInteger(object? value, long minimum)
        => value is long l ? l >= minimum : value is int i && i >= minimum;
}
