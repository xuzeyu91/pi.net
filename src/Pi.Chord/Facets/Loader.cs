namespace Pi.Chord.Facets;

/// <summary>加载完毕的 facets 拆卸。对应 TS <c>facets/loader.ts</c>。</summary>
public static class FacetLoader
{
    /// <summary>
    /// 并行拆全部加载结果，按输入顺序聚合失败（全跑完，互不阻断）。
    /// 对应 TS <c>disposeLoadedFacets</c>（Promise.allSettled + flatMap rejected）。
    /// </summary>
    public static async Task<List<Exception>> DisposeLoadedFacetsAsync(
        IReadOnlyList<LoadedFacets> loaded, CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(loaded.Select(async entry =>
        {
            try
            {
                await entry.Dispose().ConfigureAwait(false);
                return (Exception?)null;
            }
            catch (Exception error)
            {
                return error;
            }
        })).ConfigureAwait(false);
        return results.Where(error => error is not null).Select(error => error!).ToList();
    }

    /// <summary>并行拆 FacetKernel 集合（host 内部用；TS 由 kernel 自身聚合）。</summary>
    public static async Task<List<Exception>> DisposeKernelsAsync(
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
