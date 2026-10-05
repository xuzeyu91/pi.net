using Pi.Chord.Facets;
using Pi.Chord.Services;

namespace Pi.Chord;

/// <summary>
/// chord 的应用组合 API 入口。对应 TS <c>src/api.ts</c>：
/// <c>createFacetHost</c> / <c>createStaticFacetLoader</c> / <c>combineFacetLoaders</c> /
/// <c>defineFacet</c> / <c>defineService</c> / <c>replicatedState</c>。
/// <para>TS 另有 <c>createRemoteServiceBinding</c>（依赖 services/consumer.ts 的 Proxy 门面）；
/// C# 侧待 <c>Services/Consumer.cs</c> 落地后补上。</para>
/// </summary>
public static class Api
{
    /// <summary>为一整组 facets 创建已激活的 host。对应 TS <c>createFacetHost</c>。</summary>
    public static async Task<FacetHost> CreateFacetHostAsync(FacetOptions options,
        CancellationToken cancellationToken = default)
    {
        var kernel = new FacetKernel(options);
        await kernel.ActivateAsync().ConfigureAwait(false);
        return new FacetHost
        {
            Services = kernel.Provider,
            Reload = kernel.ReloadAsync,
            Dispose = kernel.DisposeAsync,
        };
    }

    /// <summary>把固定 facets 列表包成加载器。对应 TS <c>createStaticFacetLoader</c>。</summary>
    public static IFacetLoader CreateStaticFacetLoader(IReadOnlyList<IFacet> facets)
        => new StaticFacetLoader([.. facets]);

    /// <summary>
    /// 串接多个加载器：任一失败则反序拆掉已加载的（并把清理错误一并抛出），
    /// 拆卸幂等且错误聚合。对应 TS <c>combineFacetLoaders</c>。
    /// </summary>
    public static IFacetLoader CombineFacetLoaders(IReadOnlyList<IFacetLoader> loaders)
        => new CombinedFacetLoader([.. loaders]);

    /// <summary>facet 定义（恒等）。对应 TS <c>defineFacet</c>。</summary>
    public static IFacet DefineFacet(IFacet facet) => facet;

    /// <summary>
    /// 定义服务契约。对应 TS <c>defineService</c>：空 id 与 <c>$chord.</c> 保留命名空间被拒绝。
    /// </summary>
    public static Service<T> DefineService<T>(string id, bool local = false)
    {
        if (id.Length == 0) throw new ArgumentException("Service ID must not be empty");
        // TODO: 确认保留命名空间是否应属于 chord 本身。
        if (id.StartsWith("$chord.", StringComparison.Ordinal))
        {
            throw new ArgumentException("Service IDs beginning with $chord. are reserved");
        }
        return new Service<T>(id, local);
    }

    /// <summary>
    /// 附加到授权源，创建「同步水合 + 仅发布」的复制态。对应 TS <c>replicatedState(source, options)</c>。
    /// </summary>
    public static AttachedReplicatedState<T> ReplicatedState<T>(IReplicatedStateSource<T> source,
        ReplicatedStateSourceOptions? options = null)
        => ReplicatedStateAttachments.AttachReplicatedStateSource(source, options);

    /// <summary>
    /// 以「不可变地接管一个无别名的严格 JSON 根」的方式创建权威复制态；
    /// 调用方此后不得再改动 <paramref name="initial"/>。对应 TS <c>replicatedState(initial)</c>。
    /// </summary>
    public static MutableReplicatedState<T> ReplicatedState<T>(T initial) where T : class
        => new(initial);

    /// <summary>固定 facets 列表的加载器。对应 TS <c>createStaticFacetLoader</c> 的返回对象。</summary>
    private sealed class StaticFacetLoader(IReadOnlyList<IFacet> facets) : IFacetLoader
    {
        public Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LoadedFacets
            {
                Facets = facets,
                Dispose = () => Task.CompletedTask,
            });
    }

    /// <summary>多加载器串接。对应 TS <c>combineFacetLoaders</c> 的返回对象。</summary>
    private sealed class CombinedFacetLoader(IReadOnlyList<IFacetLoader> loaders) : IFacetLoader
    {
        public async Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default)
        {
            var loaded = new List<LoadedFacets>();
            try
            {
                foreach (var loader in loaders)
                {
                    loaded.Add(await loader.LoadAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            catch (Exception error)
            {
                var reversed = loaded.AsEnumerable().Reverse().ToList();
                var cleanupErrors = await FacetLoader.DisposeLoadedFacetsAsync(reversed, cancellationToken)
                    .ConfigureAwait(false);
                if (cleanupErrors.Count > 0)
                {
                    throw new AggregateException("Facet loading and cleanup failed",
                        [error, .. cleanupErrors]);
                }
                throw;
            }

            var disposed = false;
            return new LoadedFacets
            {
                Facets = [.. loaded.SelectMany(entry => entry.Facets)],
                Dispose = async () =>
                {
                    if (disposed) return;
                    disposed = true;
                    var reversed = loaded.AsEnumerable().Reverse().ToList();
                    var errors = await FacetLoader.DisposeLoadedFacetsAsync(reversed, cancellationToken)
                        .ConfigureAwait(false);
                    if (errors.Count == 1) throw errors[0];
                    if (errors.Count > 1)
                    {
                        throw new AggregateException("Failed to dispose loaded facets", errors);
                    }
                },
            };
        }
    }
}
