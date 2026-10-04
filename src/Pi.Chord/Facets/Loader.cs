using Pi.Chord.Services;

namespace Pi.Chord.Facets;

/// <summary>加载完毕的 facets 拆卸。对应 TS <c>facets/loader.ts</c>。</summary>
public static class FacetLoader
{
    /// <summary>并行拆全部加载结果，聚合失败（全跑完，互不阻断）。</summary>
    public static async Task<List<Exception>> DisposeLoadedFacetsAsync(
        IReadOnlyList<FacetKernel> loaded, CancellationToken cancellationToken = default)
    {
        var errors = new List<Exception>();
        foreach (var kernel in loaded)
        {
            errors.AddRange(await kernel.DisposeAsync().ConfigureAwait(false));
        }
        return errors;
    }
}
