using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>外部远程服务源绑定测试。</summary>
public class RemoteSourceTests
{
    /// <summary>假源：可编程目录与门面。</summary>
    private sealed class FakeSource(
        IReadOnlyList<ServiceCatalogueEntry>? entries = null,
        bool acceptsUnavailable = false) : IRemoteServiceSource
    {
        public List<string> OpenedServices { get; } = [];

        public bool ReadyCalled { get; private set; }

        public Dictionary<string, object> Facades { get; } = [];

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(entries ?? (IReadOnlyList<ServiceCatalogueEntry>)[]);

        public IRemoteServices Open(RemoteServicesOptions options)
        {
            foreach (var service in options.Services) OpenedServices.Add(service);
            return new FakeServices(this);
        }

        public bool AcceptsUnavailableServices { get; } = acceptsUnavailable;

        private sealed class FakeServices(FakeSource owner) : IRemoteServices
        {
            public Task ReadyAsync(CancellationToken cancellationToken = default)
            {
                owner.ReadyCalled = true;
                return Task.CompletedTask;
            }

            public object? UseRaw(string serviceId)
                => owner.Facades.GetValueOrDefault(serviceId);
        }
    }

    [Fact]
    public async Task ExternalServiceBindsFacadeAndReadies()
    {
        var source = new FakeSource([new ServiceCatalogueEntry("remote-svc", ServiceMode.Singleton)]);
        source.Facades["remote-svc"] = new Dictionary<string, object?> { ["remote"] = true };
        Dictionary<string, object?>? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("consumer", env =>
                {
                    env.Require(new Service<Dictionary<string, object?>>("remote-svc"));
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<Dictionary<string, object?>>("remote-svc"));
                        return Task.CompletedTask;
                    });
                }),
            ],
            ServiceSources = [source],
        });
        await kernel.ActivateAsync();

        Assert.Equal(["remote-svc"], source.OpenedServices);
        Assert.True(source.ReadyCalled);
        Assert.Same(source.Facades["remote-svc"], resolved);
    }

    [Fact]
    public async Task DuplicateOfferAcrossSourcesRejected()
    {
        var sourceA = new FakeSource([new ServiceCatalogueEntry("svc", ServiceMode.Singleton)]);
        var sourceB = new FakeSource([new ServiceCatalogueEntry("svc", ServiceMode.Singleton)]);
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets = [new SlotTestFacet("a", _ => { })],
            ServiceSources = [sourceA, sourceB],
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ActivateAsync());
    }

    [Fact]
    public async Task DeferredSourceResolvesUnlistedRequirement()
    {
        var deferred = new FakeSource(acceptsUnavailable: true);
        deferred.Facades["late-svc"] = new Dictionary<string, object?> { ["late"] = true };
        Dictionary<string, object?>? resolved = null;
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("consumer", env =>
                {
                    env.Require(new Service<Dictionary<string, object?>>("late-svc"));
                    env.OnActivate(() =>
                    {
                        resolved = env.Use(new Service<Dictionary<string, object?>>("late-svc"));
                        return Task.CompletedTask;
                    });
                }),
            ],
            ServiceSources = [deferred],
        });
        await kernel.ActivateAsync();
        Assert.Equal(["late-svc"], deferred.OpenedServices);
        Assert.Same(deferred.Facades["late-svc"], resolved);
    }

    [Fact]
    public async Task LocalRequirementDoesNotHitSources()
    {
        var source = new FakeSource([new ServiceCatalogueEntry("local-svc", ServiceMode.Singleton)]);
        var kernel = new FacetKernel(new FacetOptions
        {
            Facets =
            [
                new SlotTestFacet("provider", env =>
                {
                    env.Provide(new Service<Dictionary<string, object?>>("local-svc"),
                        new Dictionary<string, object?>());
                    env.OnActivate(() => Task.CompletedTask);
                }),
            ],
            ServiceSources = [source],
        });
        await kernel.ActivateAsync();
        Assert.Empty(source.OpenedServices); // 本地供给不打开外部源
    }

    private sealed class SlotTestFacet(string id, Action<IFacetEnvironment> setup) : IFacet
    {
        public string Id => id;

        public void Setup(IFacetEnvironment environment) => setup(environment);
    }
}
