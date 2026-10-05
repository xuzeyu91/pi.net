namespace Pi.Chord;

/// <summary>
/// 打包器入口表面。对应 TS <c>src/bundler.ts</c>：重导出 <c>node/bundle.ts</c> 与
/// <c>node/package.ts</c> 的打包 API 与类型。C# 无重导出，此处以薄转发集中该入口。
/// </summary>
public static class Bundler
{
    /// <summary>对应 TS <c>bundleFacets</c>（node/bundle.ts）。</summary>
    public static Task<Node.BundleFacetsResult> BundleFacetsAsync(Node.BundleFacetsOptions options,
        Node.IFacetEntryBundler bundler, CancellationToken cancellationToken = default)
        => Node.FacetBundler.BundleFacetsAsync(options, bundler, cancellationToken);

    /// <summary>对应 TS <c>bundleFacetPackage</c>（node/package.ts）。</summary>
    public static Task<Node.BundleFacetPackageResult> BundleFacetPackageAsync(
        Node.BundleFacetPackageOptions options, Node.IFacetEntryBundler bundler,
        CancellationToken cancellationToken = default)
        => Node.FacetPackageBundler.BundleFacetPackageAsync(options, bundler, cancellationToken);
}
