using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>api.ts 测试（createFacetHost / defineService / 加载器组合 / replicatedState）。</summary>
public class ApiTests
{
    private sealed class FakeFacet(string id) : IFacet
    {
        public string Id { get; } = id;

        public void Setup(IFacetEnvironment environment)
        {
        }
    }

    private sealed class RecordingLoader(string name, IReadOnlyList<IFacet> facets, List<string> log,
        bool failOnLoad = false) : IFacetLoader
    {
        public Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (failOnLoad) throw new InvalidOperationException($"{name} failed");
            log.Add($"load:{name}");
            return Task.FromResult(new LoadedFacets
            {
                Facets = facets,
                Dispose = () =>
                {
                    log.Add($"dispose:{name}");
                    return Task.CompletedTask;
                },
            });
        }
    }

    [Fact]
    public async Task CreateFacetHostActivatesAndDisposes()
    {
        // C# 只有「成员字典（方法/复制状态）」实现可远程发布；FacetHost.Services 需要至少一个可发布供给。
        Dictionary<string, object>? provided = null;
        var facet = new SetupFacet("provider", environment =>
        {
            provided = new Dictionary<string, object>
            {
                ["echo"] = (Func<object?[], object?>)(args => args.Length > 0 ? args[0] : null),
            };
            environment.Provide(new Service<Dictionary<string, object>>("echoer"), provided);
        });

        var host = await Api.CreateFacetHostAsync(new FacetOptions { Facets = [facet] });
        Assert.NotNull(host.Services);
        Assert.NotNull(provided);

        // reload 保持形状（同 id 替换）。
        await host.ReloadAsync([facet]);
        await host.DisposeAsync();
    }

    private sealed class SetupFacet(string id, Action<IFacetEnvironment> setup) : IFacet
    {
        public string Id { get; } = id;

        public void Setup(IFacetEnvironment environment) => setup(environment);
    }

    [Fact]
    public void DefineServiceValidatesIdAndReservedNamespace()
    {
        var service = Api.DefineService<Dictionary<string, object?>>("config");
        Assert.Equal("config", service.Id);
        Assert.False(service.Local);
        Assert.True(Api.DefineService<Dictionary<string, object?>>("config", local: true).Local);

        Assert.Throws<ArgumentException>(() => Api.DefineService<object>(""));
        Assert.Throws<ArgumentException>(() => Api.DefineService<object>("$chord.internal"));
    }

    [Fact]
    public async Task StaticFacetLoaderReturnsFacetsAndNoOpDispose()
    {
        var loader = Api.CreateStaticFacetLoader([new FakeFacet("a"), new FakeFacet("b")]);
        var loaded = await loader.LoadAsync();
        Assert.Equal(["a", "b"], loaded.Facets.Select(facet => facet.Id));
        await loaded.DisposeAsync();
    }

    [Fact]
    public async Task CombineFacetLoadersFlattensAndDisposesOnceInReverseOrder()
    {
        var log = new List<string>();
        var combined = Api.CombineFacetLoaders(
        [
            new RecordingLoader("first", [new FakeFacet("a")], log),
            new RecordingLoader("second", [new FakeFacet("b")], log),
        ]);

        var loaded = await combined.LoadAsync();
        Assert.Equal(["a", "b"], loaded.Facets.Select(facet => facet.Id));
        Assert.Equal(["load:first", "load:second"], log);

        await loaded.DisposeAsync();
        Assert.Equal(["load:first", "load:second", "dispose:second", "dispose:first"], log);

        // 拆卸幂等。
        await loaded.DisposeAsync();
        Assert.Equal(4, log.Count);
    }

    [Fact]
    public async Task CombineFacetLoadersCleansUpOnLoadFailure()
    {
        var log = new List<string>();
        var combined = Api.CombineFacetLoaders(
        [
            new RecordingLoader("first", [new FakeFacet("a")], log),
            new RecordingLoader("second", [], log, failOnLoad: true),
        ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => combined.LoadAsync());
        Assert.Contains("second failed", error.Message);
        // 已加载的按反序清理。
        Assert.Contains("dispose:first", log);
    }

    [Fact]
    public async Task CombineFacetLoadersAggregatesLoadAndCleanupErrors()
    {
        var log = new List<string>();
        var combined = Api.CombineFacetLoaders(
        [
            new FailingDisposeLoader("first", log),
            new RecordingLoader("second", [], log, failOnLoad: true),
        ]);

        var error = await Assert.ThrowsAsync<AggregateException>(() => combined.LoadAsync());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("Facet loading and cleanup failed", error.Message);
    }

    private sealed class FailingDisposeLoader(string name, List<string> log) : IFacetLoader
    {
        public Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LoadedFacets
            {
                Facets = [new FakeFacet("a")],
                Dispose = () =>
                {
                    log.Add($"dispose:{name}");
                    throw new InvalidOperationException($"{name} cleanup failed");
                },
            });
    }

    [Fact]
    public async Task CombineFacetLoadersReportsSingleCleanupError()
    {
        var log = new List<string>();
        var combined = Api.CombineFacetLoaders([new FailingDisposeLoader("first", log)]);
        var loaded = await combined.LoadAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loaded.DisposeAsync());
        Assert.Contains("cleanup failed", error.Message);
    }

    [Fact]
    public void DefineFacetIsIdentity()
    {
        var facet = new FakeFacet("a");
        Assert.Same(facet, Api.DefineFacet(facet));
    }

    [Fact]
    public void ReplicatedStateCreatesMutableAndAttached()
    {
        var mutable = Api.ReplicatedState(new Dictionary<string, object?> { ["a"] = 1L });
        Assert.Equal(1L, mutable.Value["a"]);

        var attached = Api.ReplicatedState<Dictionary<string, object?>>(new FakeSource());
        Assert.Equal(42L, attached.Value!["seed"]);
    }

    private sealed class FakeSource : IReplicatedStateSource<Dictionary<string, object?>>
    {
        public ReplicatedStateSourceSnapshot<Dictionary<string, object?>> Snapshot { get; } =
            new(new Dictionary<string, object?> { ["seed"] = 42L }, 7);

        public IReplicatedStateSourceAttachment<Dictionary<string, object?>> Attach() => new FakeAttachment();
    }

    private sealed class FakeAttachment : IReplicatedStateSourceAttachment<Dictionary<string, object?>>
    {
        public void Activate(Action<ReplicatedStateSourceFrame<Dictionary<string, object?>>> receive)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>services/handle.ts 测试（ServiceSlot 解析/调用/守卫）。</summary>
public class ServiceSlotHandleTests
{
    private static ServiceSlot BoundSlot()
    {
        var slot = new ServiceSlot("svc", wrapObjects: true);
        slot.Bind(new Dictionary<string, object?>
        {
            ["echo"] = (Func<object?[], object?>)(args => args.Length > 0 ? args[0] : null),
            ["count"] = 7L,
        });
        return slot;
    }

    [Fact]
    public void ViewRequiresBindingAndAssertAccess()
    {
        var unbound = new ServiceSlot("svc");
        var error = Assert.Throws<InvalidOperationException>(() => unbound.View<object>(() => { }));
        Assert.Contains("is not bound yet", error.Message);

        var slot = BoundSlot();
        Assert.Throws<InvalidOperationException>(() => slot.View<object>(() => throw new InvalidOperationException("denied")));
        Assert.True(slot.IsBound);
        Assert.True(slot.WrapObjects);
    }

    [Fact]
    public void ResolveReadsDictionaryMembersAndGuardsAccess()
    {
        var slot = BoundSlot();
        Assert.Equal(7L, slot.Resolve("count", () => { }).Value);
        Assert.Null(slot.Resolve("missing", () => { }).Value);
        Assert.Throws<InvalidOperationException>(() => slot.Resolve("count",
            () => throw new InvalidOperationException("denied")));
    }

    [Fact]
    public void InvokeCallsCallableMembers()
    {
        var slot = BoundSlot();
        Assert.Equal("hi", slot.InvokeMember("echo", ["hi"], () => { }));
        Assert.Throws<InvalidOperationException>(() => slot.InvokeMember("count", [], () => { }));
    }

    [Fact]
    public void UnbindDisconnectsResolve()
    {
        var slot = BoundSlot();
        slot.Unbind();
        var error = Assert.Throws<InvalidOperationException>(() => slot.Resolve("count", () => { }));
        Assert.Contains("is disconnected", error.Message);
    }

    [Fact]
    public void ResolveFallsBackToPublicProperties()
    {
        var slot = new ServiceSlot("svc");
        slot.Bind(new SampleImplementation());
        Assert.Equal("ok", slot.Resolve("Name", () => { }).Value);
    }

    private sealed class SampleImplementation
    {
        public string Name => "ok";
    }
}
