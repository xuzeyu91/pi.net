using System.Text.Json.Nodes;

namespace Pi.Chord.Node;

/// <summary><c>BundleFacetPackageAsync</c> 的输入。对应 TS <c>BundleFacetPackageOptions</c>。</summary>
public sealed record BundleFacetPackageOptions
{
    /// <summary>插件包目录或其 package.json 路径。</summary>
    public required string PackagePath { get; init; }

    public required string Outdir { get; init; }

    /// <summary>对应源文件存在时套用的应用约定（entry 名 → 相对路径）。</summary>
    public IReadOnlyDictionary<string, string>? DefaultFacets { get; init; }
}

/// <summary><c>BundleFacetPackageAsync</c> 的产出。对应 TS <c>BundleFacetPackageResult</c>。</summary>
public sealed record BundleFacetPackageResult
{
    public required BundleFacetsResult Bundle { get; init; }

    public required string PackageDirectory { get; init; }

    public required string PackageJsonPath { get; init; }
}

/// <summary>package.json 解析出的 facet 包元数据。对应 TS <c>FacetPackageMetadata</c>。</summary>
internal sealed record FacetPackageMetadata
{
    public required string PackageDirectory { get; init; }

    public required string PackageJsonPath { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyList<string> PeerDependencies { get; init; }

    public required IReadOnlyDictionary<string, string?> ConfiguredFacets { get; init; }

    public required IReadOnlyList<string> External { get; init; }

    public required bool SourceMap { get; init; }
}

/// <summary>
/// 插件包打包器。对应 TS <c>node/package.ts</c>：用 package.json 元数据 + 应用提供的
/// facet 约定确定 entry，再交给 <see cref="FacetBundler.BundleFacetsAsync"/>。
/// </summary>
public static class FacetPackageBundler
{
    public static async Task<BundleFacetPackageResult> BundleFacetPackageAsync(BundleFacetPackageOptions options,
        IFacetEntryBundler bundler, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadFacetPackageMetadataAsync(options.PackagePath, cancellationToken)
            .ConfigureAwait(false);
        var entries = await ResolveFacetEntriesAsync(metadata, options.DefaultFacets ?? new Dictionary<string, string>(),
            cancellationToken).ConfigureAwait(false);
        var external = metadata.PeerDependencies.Concat(metadata.External)
            .SelectMany(specifier => new[] { specifier, $"{specifier}/*" })
            .ToList();

        var result = await FacetBundler.BundleFacetsAsync(new BundleFacetsOptions
        {
            Plugin = new FacetBundlePlugin { Id = metadata.Name, Version = metadata.Version },
            Entries = entries,
            Outdir = options.Outdir,
            WorkingDirectory = metadata.PackageDirectory,
            External = external,
            SourceMap = metadata.SourceMap,
        }, bundler, cancellationToken).ConfigureAwait(false);

        return new BundleFacetPackageResult
        {
            Bundle = result,
            PackageDirectory = metadata.PackageDirectory,
            PackageJsonPath = metadata.PackageJsonPath,
        };
    }

    private static async Task<FacetPackageMetadata> ReadFacetPackageMetadataAsync(string packagePath,
        CancellationToken cancellationToken)
    {
        if (packagePath.Length == 0) throw new ArgumentException("Facet package path must not be empty");
        var candidate = Path.GetFullPath(packagePath);
        string packageDirectory;
        string packageJsonPath;
        if (Directory.Exists(candidate))
        {
            packageDirectory = RealPath(candidate);
            packageJsonPath = Path.Combine(packageDirectory, "package.json");
        }
        else if (File.Exists(candidate) && Path.GetFileName(candidate) == "package.json")
        {
            packageJsonPath = RealPath(candidate);
            packageDirectory = Path.GetDirectoryName(packageJsonPath)!;
        }
        else
        {
            throw new InvalidOperationException(
                $"Facet package path must name a directory or package.json: {candidate}");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(await File.ReadAllTextAsync(packageJsonPath, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not read facet package metadata {packageJsonPath}", error);
        }
        if (parsed is not JsonObject metadata)
        {
            throw new InvalidOperationException($"Facet package metadata must be an object: {packageJsonPath}");
        }
        var name = metadata.Str("name");
        if (name is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Facet package must have a non-empty name: {packageJsonPath}");
        }
        var version = metadata.Str("version");
        if (version is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Facet package must have a non-empty version: {packageJsonPath}");
        }

        var chord = ParseChordConfiguration(metadata["chord"], packageJsonPath);
        return new FacetPackageMetadata
        {
            PackageDirectory = packageDirectory,
            PackageJsonPath = packageJsonPath,
            Name = name,
            Version = version,
            PeerDependencies = ParsePeerDependencies(metadata["peerDependencies"], packageJsonPath),
            ConfiguredFacets = chord.Facets,
            External = chord.External,
            SourceMap = chord.SourceMap,
        };
    }

    private static IReadOnlyList<string> ParsePeerDependencies(JsonNode? value, string packageJsonPath)
    {
        if (value is null) return [];
        if (value is not JsonObject dependencies
            || dependencies.Any(entry => entry.Key.Length == 0 || entry.Value is not JsonValue peerVersion
                || !peerVersion.TryGetValue<string>(out _)))
        {
            throw new InvalidOperationException($"Facet package has invalid peerDependencies: {packageJsonPath}");
        }
        return dependencies.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    private static (IReadOnlyDictionary<string, string?> Facets, IReadOnlyList<string> External, bool SourceMap)
        ParseChordConfiguration(JsonNode? value, string packageJsonPath)
    {
        if (value is null)
        {
            return (new Dictionary<string, string?>(StringComparer.Ordinal), [], true);
        }
        if (value is not JsonObject chord)
        {
            throw new InvalidOperationException(
                $"Facet package chord configuration must be an object: {packageJsonPath}");
        }
        if (chord.Any(entry => entry.Key is not ("facets" or "external" or "sourceMap")))
        {
            throw new InvalidOperationException(
                $"Facet package chord configuration has an unknown field: {packageJsonPath}");
        }

        var facets = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (chord["facets"] is JsonObject configuredFacets)
        {
            foreach (var (name, sourceNode) in configuredFacets)
            {
                // false = 显式禁用该约定 entry；字符串 = 显式源路径。
                var disabled = sourceNode is JsonValue flag
                    && flag.GetValueKind() is System.Text.Json.JsonValueKind.False;
                var source = sourceNode is JsonValue text && text.TryGetValue<string>(out var path) ? path : null;
                if (name.Length == 0 || (!disabled && string.IsNullOrEmpty(source)))
                {
                    throw new InvalidOperationException(
                        $"Facet package has an invalid chord.facets entry: {packageJsonPath}");
                }
                facets[name] = disabled ? null : source;
            }
        }
        else if (chord.ContainsKey("facets") && chord["facets"] is not null)
        {
            throw new InvalidOperationException(
                $"Facet package chord.facets must be an object: {packageJsonPath}");
        }

        var external = new List<string>();
        if (chord["external"] is JsonArray externalArray)
        {
            foreach (var item in externalArray)
            {
                if (item is not JsonValue specifierValue
                    || !specifierValue.TryGetValue<string>(out var specifier)
                    || specifier.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Facet package chord.external must contain non-empty strings: {packageJsonPath}");
                }
                external.Add(specifier);
            }
            external = external.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
        else if (chord.ContainsKey("external") && chord["external"] is not null)
        {
            throw new InvalidOperationException(
                $"Facet package chord.external must contain non-empty strings: {packageJsonPath}");
        }

        var sourceMap = true;
        if (chord.ContainsKey("sourceMap") && chord["sourceMap"] is not null)
        {
            if (chord["sourceMap"] is not JsonValue flag
                || flag.GetValueKind() is not (System.Text.Json.JsonValueKind.True
                    or System.Text.Json.JsonValueKind.False))
            {
                throw new InvalidOperationException(
                    $"Facet package chord.sourceMap must be a boolean: {packageJsonPath}");
            }
            sourceMap = flag.GetValueKind() == System.Text.Json.JsonValueKind.True;
        }

        return (facets, external, sourceMap);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveFacetEntriesAsync(
        FacetPackageMetadata metadata, IReadOnlyDictionary<string, string> defaultFacets,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        // 先按应用约定：源文件存在才收录（缺失即跳过）。
        foreach (var (name, source) in defaultFacets)
        {
            ValidateFacetMapping(name, source, "default");
            var path = ResolvePackageEntry(metadata.PackageDirectory, source, name);
            if (!File.Exists(path)) continue;
            var canonicalPath = RealPath(path);
            ValidateCanonicalPackageEntry(metadata.PackageDirectory, canonicalPath, name);
            entries[name] = canonicalPath;
        }

        // 再按 package.json 的 chord.facets 覆盖：false 删除，字符串替换。
        foreach (var (name, source) in metadata.ConfiguredFacets)
        {
            if (source is null)
            {
                entries.Remove(name);
                continue;
            }
            ValidateFacetMapping(name, source, "configured");
            var path = ResolvePackageEntry(metadata.PackageDirectory, source, name);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Could not access configured facet entry {name}: {path}");
            }
            var canonicalPath = RealPath(path);
            ValidateCanonicalPackageEntry(metadata.PackageDirectory, canonicalPath, name);
            entries[name] = canonicalPath;
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Facet package {metadata.Name} has no configured or conventional facet entries");
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return entries;
    }

    private static void ValidateFacetMapping(string name, string source, string kind)
    {
        if (name.Length == 0)
        {
            throw new InvalidOperationException($"Facet package {kind} entry name must not be empty");
        }
        if (source.Length == 0)
        {
            throw new InvalidOperationException($"Facet package {kind} entry {name} must have a source path");
        }
    }

    private static string ResolvePackageEntry(string packageDirectory, string source, string name)
    {
        if (Path.IsPathRooted(source))
        {
            throw new InvalidOperationException(
                $"Facet package entry {name} must be relative to the package directory");
        }
        var path = Path.GetFullPath(Path.Combine(packageDirectory, source));
        var relativePath = Path.GetRelativePath(packageDirectory, path);
        if (relativePath.Length == 0 || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Facet package entry {name} escapes the package directory");
        }
        return path;
    }

    private static void ValidateCanonicalPackageEntry(string packageDirectory, string path, string name)
    {
        var relativePath = Path.GetRelativePath(packageDirectory, path);
        if (relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Facet package entry {name} resolves outside the package directory");
        }
    }

    /// <summary>
    /// 规范化路径：<c>Path.GetFullPath</c> + 解析符号链接（对齐 TS <c>realpath</c>）。
    /// </summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full;
        }
    }
}
