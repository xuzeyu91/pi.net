using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Xunit;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>RemoteServiceProvider 与 RemoteServiceEndpoint 测试。</summary>
public class ProviderTests
{
    private static Func<object?[], object?> Method(Func<object?[], object?> f) => f;

    private static IReadOnlyDictionary<string, object> Impl(
        Dictionary<string, MutableReplicatedState<Dictionary<string, object?>>> states,
        string stateName)
    {
        var implementation = new Dictionary<string, object>
        {
            ["echo"] = Method(args => args.Length > 0 ? args[0] : null),
            [stateName] = states[stateName],
        };
        return implementation;
    }

    [Fact]
    public async Task ProvideInvokeAndSubscribeRoundTrip()
    {
        var doc = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?> { ["n"] = 1L });
        var provider = new RemoteServiceProvider(
        [new RemoteServiceProvider.ProviderDefinition("fs")]);
        Assert.Single(provider.Catalogue);
        Assert.Equal("fs", provider.Catalogue[0].ServiceId);

        provider.Provide("fs", Impl(new() { ["doc"] = doc }, "doc"));

        // 方法调用。
        var result = await provider.Invoke(
            new ServiceCall("fs", "echo", ["hello"]), Context.Context.Background);
        Assert.Equal("hello", result);

        // 订阅：激活即收到快照（hydrate 序列）。
        var updates = new List<ServiceProviderUpdate>();
        var subscription = provider.Subscribe("fs", ServiceMode.Singleton, (update, _) =>
        {
            updates.Add(update);
        });
        subscription.Activate();
        Assert.Empty(updates); // 订阅时无新发布：快照由返回值携带，activate 只排空缓冲

        // 状态变更 → state 更新推送。
        doc.Change(Context.Context.Background, change =>
            change.Set(Path.Root.Append(Seg.Key("n")), 7L));
        Assert.Single(updates);
        var stateUpdate = Assert.IsType<ServiceProviderUpdate.StateUpdate>(updates[0]);
        Assert.Equal("doc", stateUpdate.Member);
        Assert.Equal(1L, stateUpdate.Sequence);
        Assert.Equal(7L, ((DeltaOp.Set)stateUpdate.Ops[0]).Value);
    }

    [Fact]
    public void DuplicateProvideAndLocalServiceRejected()
    {
        var doc = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?> { ["n"] = 1L });
        var provider = new RemoteServiceProvider(
        [new RemoteServiceProvider.ProviderDefinition("fs")]);
        provider.Provide("fs", Impl(new() { ["doc"] = doc }, "doc"));
        Assert.Throws<RemoteServiceError>(() =>
            provider.Provide("fs", Impl(new() { ["doc"] = doc }, "doc")));

        // 本地服务不能远程发布。
        Assert.Throws<ArgumentException>(() => new RemoteServiceProvider(
            [new RemoteServiceProvider.ProviderDefinition("local1", Local: true)]));

        // 未注册服务不可提供。
        var provider2 = new RemoteServiceProvider([new RemoteServiceProvider.ProviderDefinition("a")]);
        Assert.Throws<RemoteServiceError>(() =>
            provider2.Provide("b", Impl(new() { ["doc"] = doc }, "doc")));
    }

    [Fact]
    public async Task KeyedSpawnCloseAndStaleInstance()
    {
        var docA = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?> { ["n"] = 0L });
        var provider = new RemoteServiceProvider(
            [new RemoteServiceProvider.ProviderDefinition("kv", ServiceMode.Keyed)]);
        var close = provider.Spawn("kv", "alpha", Impl(new() { ["doc"] = docA }, "doc"));

        var result = await provider.Invoke(new ServiceCall("kv", "echo", ["x"],
            new ServiceInstanceAddress("alpha", 1)), Context.Context.Background);
        Assert.Equal("x", result);

        // 关闭后同代调用 → stale instance。
        close();
        await Assert.ThrowsAsync<RemoteServiceError>(() => provider.Invoke(
            new ServiceCall("kv", "echo", ["x"], new ServiceInstanceAddress("alpha", 1)),
            Context.Context.Background));

        // 重新 spawn 同 key：代数 +1。
        var close2 = provider.Spawn("kv", "alpha", Impl(new() { ["doc"] = docA }, "doc"));
        await Assert.ThrowsAsync<RemoteServiceError>(() => provider.Invoke(
            new ServiceCall("kv", "echo", ["x"], new ServiceInstanceAddress("alpha", 1)),
            Context.Context.Background)); // 代 1 已 stale
        var ok = await provider.Invoke(new ServiceCall("kv", "echo", ["y"],
            new ServiceInstanceAddress("alpha", 2)), Context.Context.Background);
        Assert.Equal("y", ok);
        close2();
    }

    [Fact]
    public async Task EndpointHandlesControlCallsAndPublishes()
    {
        var doc = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?> { ["n"] = 1L });
        var provider = new RemoteServiceProvider([new RemoteServiceProvider.ProviderDefinition("fs")]);
        provider.Provide("fs", Impl(new() { ["doc"] = doc }, "doc"));
        using var endpoint = RemoteServiceEndpoint.Create(provider);

        // catalogue。
        var (isControl, catalogue) = await endpoint.InvokeAsync(
            ServiceWire.CreateServiceCatalogueCall(), (_, _, _) => Task.CompletedTask,
            Context.Context.Background);
        Assert.True(isControl);
        var entry = Assert.Single((IReadOnlyList<ServiceCatalogueEntry>)catalogue!);
        Assert.Equal("fs", entry.ServiceId);

        // subscribe：返回快照（根替换 op）。
        var pushed = new List<ServiceProviderUpdate>();
        var (subscribed, snapshot) = await endpoint.InvokeAsync(
            ServiceWire.CreateServiceSubscribeCall("sub-1", "fs", ServiceMode.Singleton),
            (subId, update, _) =>
            {
                Assert.Equal("sub-1", subId);
                pushed.Add(update);
                return Task.CompletedTask;
            }, Context.Context.Background);
        Assert.True(subscribed);
        var wireSnapshot = Assert.IsType<ServiceSubscriptionSnapshot>(snapshot);
        var instance = Assert.Single(wireSnapshot.Instances);
        Assert.Equal(2, instance.Members.Count); // doc(state) + echo(method)
        var stateMember = Assert.IsType<ServiceMemberSnapshot.State>(
            instance.Members.OfType<ServiceMemberSnapshot.State>().Single());
        Assert.Equal(0L, stateMember.Sequence); // 初始未发布过

        // 状态变更 → 推送 state 更新。
        doc.Change(Context.Context.Background, change =>
            change.Set(Path.Root.Append(Seg.Key("n")), 4L));
        var update = Assert.IsType<ServiceProviderUpdate.StateUpdate>(Assert.Single(pushed));
        Assert.Equal(4L, ((DeltaOp.Set)update.Ops[0]).Value);

        // unsubscribe。
        var (unsubscribed, _) = await endpoint.InvokeAsync(
            ServiceWire.CreateServiceUnsubscribeCall("sub-1"), (_, _, _) => Task.CompletedTask,
            Context.Context.Background);
        Assert.True(unsubscribed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.InvokeAsync(
            ServiceWire.CreateServiceUnsubscribeCall("sub-1"), (_, _, _) => Task.CompletedTask,
            Context.Context.Background));
    }
}
