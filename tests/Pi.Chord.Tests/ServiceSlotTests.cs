using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Xunit;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>服务槽接线测试：Use/Provide/Observe/Provider 装配。</summary>
public class ServiceSlotTests
{
    /// <summary>远程可发布的实现（成员字典）。</summary>
    private static IReadOnlyDictionary<string, object> RemoteImpl(
        MutableReplicatedState<Dictionary<string, object?>> doc)
        => new Dictionary<string, object>
        {
            ["echo"] = (Func<object?[], object?>)(args => args[0]),
            ["doc"] = doc,
        };

    [Fact]
    public async Task UseResolvesInOnActivateAfterAssembly()
    {
        Dictionary<string, object?>? provided = null;
        object? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new TestFacetHelper("provider", env =>
                {
                    provided = new Dictionary<string, object?> { ["v"] = 1L };
                    env.Provide(new Service<Dictionary<string, object?>>("config"), provided);
                    env.OnActivate(() => Task.CompletedTask);
                }),
                new TestFacetHelper("consumer", env =>
                {
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<Dictionary<string, object?>>("config"));
                        return Task.CompletedTask;
                    });
                }),
            ],
        });
        await kernel.ActivateAsync();
        Assert.Same(provided, resolved);
    }

    [Fact]
    public async Task UseBeforeActivationFails()
    {
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new TestFacetHelper("a", env =>
                {
                    // 装配前 Use 立即失败（C# 显式解析语义）。
                    Assert.Throws<InvalidOperationException>(() =>
                        env.Use(new Service<Dictionary<string, object?>>("config")));
                }),
            ],
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ActivateAsync());
    }

    [Fact]
    public async Task RemoteImplementationAssemblesIntoProvider()
    {
        var doc = new MutableReplicatedState<Dictionary<string, object?>>(
            new Dictionary<string, object?> { ["n"] = 1L });
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new TestFacetHelper("fs", env =>
                {
                    env.Provide(new Service<IReadOnlyDictionary<string, object>>("fs"),
                        RemoteImpl(doc));
                    env.OnActivate(() => Task.CompletedTask);
                }),
            ],
        });
        await kernel.ActivateAsync();

        // provider 装配了远程实现：方法调用走通。
        var result = await kernel.Provider.Invoke(
            new ServiceCall("fs", "echo", ["hi"]), Context.Context.Background);
        Assert.Equal("hi", result);

        // 状态变更经 provider 发布。
        var updates = new List<ServiceProviderUpdate>();
        var subscription = kernel.Provider.Subscribe("fs", ServiceMode.Singleton,
            (update, _) => updates.Add(update));
        subscription.Activate();
        doc.Change(Context.Context.Background, change =>
            change.Set(Path.Root.Append(Seg.Key("n")), 3L));
        Assert.Single(updates);
    }

    [Fact]
    public async Task KeyedObserveReceivesSpawnedInstances()
    {
        var observed = new List<object>();
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new TestFacetHelper("owner", env =>
                {
                    env.OnActivate(() => Task.CompletedTask);
                    // 本地 keyed 源在装配后由 SpawnDeferred 连接；本测试直接登记注册表。
                    env.Provide(new Service<object>("kv"), new object());
                    env.Own(() => Task.CompletedTask);
                }),
                new TestFacetHelper("watcher", env =>
                {
                    env.Observe(new Service<Dictionary<string, object?>>("kv"),
                        (instance, _) =>
                        {
                            lock (observed) observed.Add(instance!);
                            return Task.CompletedTask;
                        });
                }),
            ],
        });
        // watcher Observe 要求 keyed 源已连接——装配前注册会抛 disconnected，
        // 这里直接断言该语义（provider 集成阶段的完整 keyed 流由 LocalKeyedServiceRegistry 测试覆盖）。
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ActivateAsync());

        // 本地 keyed 注册表独立验证：孵化 → 观察。
        var registry = new LocalKeyedServiceRegistry(["kv"], _ => { });
        var seen = new List<string>();
        using var watch = registry.Observe(new Service<Dictionary<string, object?>>("kv"),
            (instance, _) =>
            {
                lock (seen) seen.Add((string)instance["k"]!);
                return Task.CompletedTask;
            });
        var close = registry.Spawn(new Service<Dictionary<string, object?>>("kv"), "a",
            new Dictionary<string, object?> { ["k"] = "a" });
        Assert.Equal(["a"], seen);
        close();
    }

    private sealed class TestFacetHelper(string id, Action<IFacetEnvironment> setup) : IFacet
    {
        public string Id => id;

        public void Setup(IFacetEnvironment environment) => setup(environment);
    }
}
