using System.Text.Json.Serialization;

namespace Pi.Chord.Node;

/// <summary>
/// facet bundle 清单常量。对应 TS <c>node/manifest.ts</c> 的常量组。
/// </summary>
public static class FacetBundleFormats
{
    public const string Format = "chord.facet-bundle";
    public const int FormatVersion = 2;
    public const string ManifestFile = "chord-facets.json";
    public const string ArtifactFormat = "chord.facet-bundle-artifact";
    public const int ArtifactFormatVersion = 2;
}

/// <summary>插件身份。对应 TS <c>FacetBundlePlugin</c>。</summary>
public sealed record FacetBundlePlugin
{
    public required string Id { get; init; }

    public string? Version { get; init; }
}

/// <summary>清单中的一个条目。对应 TS <c>FacetBundleEntry</c>。</summary>
public sealed record FacetBundleEntry
{
    /// <summary>内容寻址的 CommonJS 文件名（相对清单）。</summary>
    public required string File { get; init; }

    /// <summary>JavaScript 文件的 SHA-256 subresource-integrity 值（<c>sha256-&lt;base64&gt;</c>）。</summary>
    public required string Integrity { get; init; }

    /// <summary>刻意留给加载方解析的 import。</summary>
    public required IReadOnlyList<string> ExternalImports { get; init; }

    /// <summary>source map 文件名（相对清单），未产出时为 null。</summary>
    [JsonPropertyName("sourceMap")]
    public string? SourceMap { get; init; }
}

/// <summary>facet bundle 清单。对应 TS <c>FacetBundleManifest</c>。</summary>
public sealed record FacetBundleManifest
{
    public required string Format { get; init; }

    public required int FormatVersion { get; init; }

    public required FacetBundlePlugin Plugin { get; init; }

    public required IReadOnlyDictionary<string, FacetBundleEntry> Entries { get; init; }
}

/// <summary>
/// 可存储/可传输到另一 Node 宿主的自包含清单条目。对应 TS <c>FacetBundleArtifact</c>。
/// </summary>
public sealed record FacetBundleArtifact
{
    public required string Format { get; init; }

    public required int FormatVersion { get; init; }

    public required FacetBundlePlugin Plugin { get; init; }

    public required string EntryName { get; init; }

    public required FacetBundleEntry Entry { get; init; }

    public required string Source { get; init; }

    [JsonPropertyName("sourceMapContents")]
    public string? SourceMapContents { get; init; }
}
