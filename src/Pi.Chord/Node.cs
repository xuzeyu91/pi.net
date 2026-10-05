namespace Pi.Chord;

/// <summary>
/// Node 宿主侧入口表面。对应 TS <c>src/node.ts</c>：重导出 <c>node/bundle-loader.ts</c> 的
/// 加载器工厂与 <c>node/manifest.ts</c> 的格式常量/类型。C# 无重导出，此处以薄转发集中该入口。
/// </summary>
public static class NodeApi
{
    /// <summary>对应 TS <c>FACET_BUNDLE_FORMAT</c>。</summary>
    public const string BundleFormat = Node.FacetBundleFormats.Format;

    /// <summary>对应 TS <c>FACET_BUNDLE_FORMAT_VERSION</c>。</summary>
    public const int BundleFormatVersion = Node.FacetBundleFormats.FormatVersion;

    /// <summary>对应 TS <c>FACET_BUNDLE_MANIFEST_FILE</c>。</summary>
    public const string BundleManifestFile = Node.FacetBundleFormats.ManifestFile;

    /// <summary>对应 TS <c>FACET_BUNDLE_ARTIFACT_FORMAT</c>。</summary>
    public const string BundleArtifactFormat = Node.FacetBundleFormats.ArtifactFormat;

    /// <summary>对应 TS <c>FACET_BUNDLE_ARTIFACT_FORMAT_VERSION</c>。</summary>
    public const int BundleArtifactFormatVersion = Node.FacetBundleFormats.ArtifactFormatVersion;

    /// <summary>对应 TS <c>readFacetBundleManifest</c>。</summary>
    public static Task<Node.FacetBundleManifest> ReadFacetBundleManifestAsync(string path,
        CancellationToken cancellationToken = default)
        => Node.FacetBundleLoader.ReadFacetBundleManifestAsync(path, cancellationToken);

    /// <summary>对应 TS <c>readFacetBundleArtifact</c>。</summary>
    public static Task<Node.FacetBundleArtifact> ReadFacetBundleArtifactAsync(string manifestPath, string entry,
        CancellationToken cancellationToken = default)
        => Node.FacetBundleLoader.ReadFacetBundleArtifactAsync(manifestPath, entry, cancellationToken);

    /// <summary>对应 TS <c>createFacetBundleLoader</c>。</summary>
    public static Facets.IFacetLoader CreateFacetBundleLoader(Node.FacetBundleLoaderOptions options,
        Node.IFacetModuleHost? moduleHost = null)
        => Node.FacetBundleLoader.CreateFacetBundleLoader(options, moduleHost);

    /// <summary>对应 TS <c>createFacetBundleArtifactLoader</c>。</summary>
    public static Facets.IFacetLoader CreateFacetBundleArtifactLoader(
        Node.FacetBundleArtifactLoaderOptions options, Node.IFacetModuleHost? moduleHost = null)
        => Node.FacetBundleLoader.CreateFacetBundleArtifactLoader(options, moduleHost);
}
