using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>services/consumer.ts 测试（远程服务绑定的显式 API 版）。</summary>
public class ConsumerTests
{
    private static Context.Context Ctx => Context.Context.Background;

    private sealed class FakeSubscription(
        ServiceSubscriptionSnapshot snapshot, List<string> closed, string label,
        Func<ServiceProviderUpdate, Context.Context, Task?> listener) : IRemoteServiceSubscription
    {
        public ServiceSubscriptionSnapshot Snapshot { get; } = snapshot;

        public bool Activated { get; private set; }

        public void Activate() => Activated = true;

        public Task CloseAsync(Context.Context context)
        {
            closed.Add(label);
            return Task.CompletedTask;
        }

        public Task PushAsync(ServiceProviderUpdate update) => listener(update, Ctx) ?? Task.CompletedTask;
    }

    private sealed class FakeTransport : IRemoteServiceTransport
    {
        public List<ServiceCall> Calls { get; } = [];

        public List<string> Subscribed { get; } = [];

        public List<string> Closed { get; } = [];

        public ServiceSubscriptionSnapshot? Snapshot { get; set; }

        public object? InvokeResult { get; set; }

        public List<FakeSubscription> Subscriptions { get; } = [];

        public Task<object?> InvokeAsync(ServiceCall call, Context.Context context)
        {
            Calls.Add(call);
            return Task.FromResult(InvokeResult);
        }

        public Task<IRemoteServiceSubscription> SubscribeAsync(string serviceId, ServiceMode mode,
            Func<ServiceProviderUpdate, Context.Context, Task?> listener, Context.Context context)
        {
            Subscribed.Add($"{serviceId}:{mode}");
            var subscription = new FakeSubscription(Snapshot!, Closed, serviceId, listener);
            Subscriptions.Add(subscription);
            return Task.FromResult<IRemoteServiceSubscription>(subscription);
        }
    }

    private static ServiceSubscriptionSnapshot SingletonSnapshot(params ServiceMemberSnapshot[] members)
        => new("echoer", ServiceMode.Singleton, [new ServiceInstanceSnapshot(null, members)]);

    private static RemoteServiceBinding Binding(FakeTransport transport,
        Action<Exception>? onError = null, bool bound = true)
        => new(new RemoteServiceBindingOptions
        {
            Services = ["echoer"],
            Transport = transport,
            Bound = bound,
            OnError = onError,
        });

    [Fact]
    public void RejectsLocalUnlistedAndModeMismatchedServices()
    {
        var binding = Binding(new FakeTransport());
        var local = Assert.Throws<RemoteServiceError>(() => binding.Use(new Service<object>("echoer", true)));
        Assert.Equal("service_not_allowed", local.Code);

        var unlisted = Assert.Throws<RemoteServiceError>(() => binding.Use(new Service<object>("other")));
        Assert.Equal("service_not_allowed", unlisted.Code);

        binding.Use(new Service<object>("echoer"));
        var mismatch = Assert.Throws<RemoteServiceError>(() =>
        {
            _ = binding.Observe(new Service<object>("echoer"), (_, _) => Task.CompletedTask);
        });
        Assert.Equal("service_mode_mismatch", mismatch.Code);
    }

    [Fact]
    public async Task InstallsSnapshotHydratesStateAndInvokesMethods()
    {
        var transport = new FakeTransport
        {
            Snapshot = SingletonSnapshot(
                new ServiceMemberSnapshot.Method("echo"),
                new ServiceMemberSnapshot.State("doc", 3,
                    [new DeltaOp.Replace(new Dictionary<string, object?> { ["a"] = 1L })])),
            InvokeResult = "pong",
        };
        var binding = Binding(transport);

        var facade = binding.Use(new Service<object>("echoer"));
        await binding.ReadyAsync();

        // 成员字典视图：键来自快照。
        Assert.Equal(["doc", "echo"], facade.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, facade.Count);
        Assert.True(facade.ContainsKey("echo"));
        Assert.False(facade.ContainsKey("missing"));

        // 方法成员：调用转发到 transport。
        var result = await facade.Member("echo").CallAsync(["hi"], Ctx);
        Assert.Equal("pong", result);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("echoer", call.ServiceId);
        Assert.Equal("echo", call.Member);
        Assert.Equal(["hi"], call.Args);

        // 状态成员：已水合。
        var value = Assert.IsType<Dictionary<string, object?>>(facade.Member("doc").Value);
        Assert.Equal(1L, value["a"]);

        // 可调用成员按「尾随 Context」约定暴露。
        var callable = Assert.IsAssignableFrom<Func<object?[], object?>>(facade["echo"]);
        await (Task<object?>)callable(["again", Ctx])!;
        Assert.Equal(2, transport.Calls.Count);
    }

    [Fact]
    public async Task MemberKindMismatchAndStaleBindingAreReported()
    {
        var transport = new FakeTransport { Snapshot = SingletonSnapshot(new ServiceMemberSnapshot.Method("echo")) };
        var binding = Binding(transport);
        var facade = binding.Use(new Service<object>("echoer"));
        await binding.ReadyAsync();

        // 尾随 Context 缺失（成员按方法使用）。
        var callable = Assert.IsAssignableFrom<Func<object?[], object?>>(facade["echo"]);
        var invalid = Assert.Throws<RemoteServiceError>(() => callable([]));
        Assert.Equal("service_invalid_value", invalid.Code);

        // 同一成员随后按 state 使用 → 种类冲突（#expect 状态机）。
        var mismatch = Assert.Throws<RemoteServiceError>(() => facade.Member("echo").Value);
        Assert.Equal("service_member_mismatch", mismatch.Code);

        // 断开后成员不可再调用（stale instance）。
        await binding.RebindAsync(false, Ctx);
        var stale = await Assert.ThrowsAsync<RemoteServiceError>(
            () => facade.Member("echo").CallAsync([], Ctx));
        Assert.Equal("service_stale_instance", stale.Code);
    }

    [Fact]
    public async Task AppliesResetAndStateUpdates()
    {
        var transport = new FakeTransport { Snapshot = SingletonSnapshot(new ServiceMemberSnapshot.Method("echo")) };
        var binding = Binding(transport);
        var facade = binding.Use(new Service<object>("echoer"));
        await binding.ReadyAsync();

        var subscription = Assert.Single(transport.Subscriptions);
        Assert.True(subscription.Activated);

        // reset：整体替换成员集合。
        await subscription.PushAsync(new ServiceProviderUpdate.Reset(SingletonSnapshot(
            new ServiceMemberSnapshot.State("doc", 1,
                [new DeltaOp.Replace(new Dictionary<string, object?> { ["n"] = 2L })]))));
        Assert.Equal(2L, ((Dictionary<string, object?>)facade.Member("doc").Value!)["n"]);

        // state：序列号连续才生效。
        await subscription.PushAsync(new ServiceProviderUpdate.StateUpdate(
            null, "doc", 2, [new DeltaOp.Set(Pi.Chord.Delta.Path.Root.Append(Pi.Chord.Delta.Seg.Key("n")), 5L)]));
        Assert.Equal(5L, ((Dictionary<string, object?>)facade.Member("doc").Value!)["n"]);

        // unavailable：清空。
        await subscription.PushAsync(new ServiceProviderUpdate.Unavailable());
        Assert.Null(facade.Member("doc").Value);
    }

    [Fact]
    public async Task RebindAndDisposeCloseSubscriptions()
    {
        var transport = new FakeTransport { Snapshot = SingletonSnapshot(new ServiceMemberSnapshot.Method("echo")) };
        var binding = Binding(transport);
        binding.Use(new Service<object>("echoer"));
        await binding.ReadyAsync();

        await binding.RebindAsync(false, Ctx);
        Assert.Contains("echoer", transport.Closed);
        Assert.Single(transport.Subscribed);

        // 重新连上会再订阅一次。
        await binding.RebindAsync(true, Ctx);
        Assert.Equal(2, transport.Subscribed.Count);

        await binding.DisposeAsync(Ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.ReadyAsync());
    }

    [Fact]
    public async Task DuplicateServiceIdsAreRejected()
    {
        var error = await Task.FromResult(Assert.Throws<ArgumentException>(() => new RemoteServiceBinding(
            new RemoteServiceBindingOptions
            {
                Services = ["a", "a"],
                Transport = new FakeTransport(),
            })));
        Assert.Contains("duplicate service IDs", error.Message);
    }

    [Fact]
    public void ValidateResetSnapshotRejectsBadShapes()
    {
        // 服务不符。
        Assert.Throws<InvalidOperationException>(() => RemoteServiceFacade.ValidateResetSnapshot(
            new ServiceSubscriptionSnapshot("other", ServiceMode.Singleton, []), "echoer", ServiceMode.Singleton));
        // singleton 带了实例地址。
        Assert.Throws<InvalidOperationException>(() => RemoteServiceFacade.ValidateResetSnapshot(
            new ServiceSubscriptionSnapshot("echoer", ServiceMode.Singleton,
                [new ServiceInstanceSnapshot(new ServiceInstanceAddress("k", 1), [])]),
            "echoer", ServiceMode.Singleton));
        // 状态成员不是整根替换。
        Assert.Throws<InvalidOperationException>(() => RemoteServiceFacade.ValidateResetSnapshot(
            new ServiceSubscriptionSnapshot("echoer", ServiceMode.Singleton,
                [new ServiceInstanceSnapshot(null, [new ServiceMemberSnapshot.State("doc", 1, [])])]),
            "echoer", ServiceMode.Singleton));
        // keyed 重复实例键。
        Assert.Throws<InvalidOperationException>(() => RemoteServiceFacade.ValidateResetSnapshot(
            new ServiceSubscriptionSnapshot("echoer", ServiceMode.Keyed,
            [
                new ServiceInstanceSnapshot(new ServiceInstanceAddress("k", 1), []),
                new ServiceInstanceSnapshot(new ServiceInstanceAddress("k", 2), []),
            ]),
            "echoer", ServiceMode.Keyed));
    }

    [Fact]
    public async Task LoopbackTransportRoundTripsThroughProvider()
    {
        var provider = new RemoteServiceProvider(
            [new RemoteServiceProvider.ProviderDefinition("echoer")]);
        provider.Provide("echoer", new Dictionary<string, object>
        {
            // provider 会把 context 追加到 args 末尾（对齐 TS invoke）。
            ["echo"] = (Func<object?[], object?>)(args => args[0]),
        });

        var binding = new RemoteServiceBinding(new RemoteServiceBindingOptions
        {
            Services = ["echoer"],
            Transport = LoopbackServiceTransport.Create(provider),
        });

        var facade = binding.Use(new Service<object>("echoer"));
        await binding.ReadyAsync();

        Assert.Contains("echo", facade.Keys);
        var result = await facade.Member("echo").CallAsync(["round-trip"], Ctx);
        Assert.Equal("round-trip", result);

        await binding.DisposeAsync(Ctx);
    }
}
