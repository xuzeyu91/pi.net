using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Facets;

namespace Pi.Chord.Node;

/// <summary>把宿主提供的 external import 解析成目标模块。对应 TS <c>FacetBundleExternalResolver</c>。</summary>
public delegate string? FacetBundleExternalResolver(string specifier);

/// <summary><c>CreateFacetBundleLoader</c> 的输入。对应 TS <c>FacetBundleLoaderOptions</c>。</summary>
public sealed record FacetBundleLoaderOptions
{
    public required string ManifestPath { get; init; }

    public required string Entry { get; init; }

    /// <summary>求值前校验条目的 SHA-256 完整性。缺省 true。</summary>
    public bool? VerifyIntegrity { get; init; }

    /// <summary>bundle 位于宿主包树之外时，解析宿主提供的 external import。</summary>
    public FacetBundleExternalResolver? ResolveExternal { get; init; }
}

/// <summary><c>CreateFacetBundleArtifactLoader</c> 的输入。对应 TS <c>FacetBundleArtifactLoaderOptions</c>。</summary>
public sealed record FacetBundleArtifactLoaderOptions
{
    /// <summary>待加载的 artifact（未校验的原始值）。</summary>
    public required JsonNode? Artifact { get; init; }

    /// <summary>在接收方应用里解析宿主提供的 external import。</summary>
    public FacetBundleExternalResolver? ResolveExternal { get; init; }

    /// <summary>物化模块代的父目录；缺省为操作系统临时目录。</summary>
    public string? TemporaryDirectory { get; init; }
}

/// <summary>
/// CommonJS 模块执行宿主。<para>TS 用 <c>node:vm</c> 的 <c>compileFunction</c> 执行 bundle；
/// C# 无 JS 引擎（扩展已改为 AssemblyLoadContext 插件），故把这一步抽成注入点。</para>
/// </summary>
public interface IFacetModuleHost
{
    /// <summary>执行 CommonJS 源码并返回 module.exports。</summary>
    object? ExecuteCommonJsModule(string source, string modulePath,
        IReadOnlyList<string> externalImports, FacetBundleExternalResolver? resolver);
}

/// <summary>默认模块宿主：明确拒绝 JS bundle（C# 插件路线下不使用该加载路径）。</summary>
public sealed class UnsupportedFacetModuleHost : IFacetModuleHost
{
    public const string Message =
        "Executing a JavaScript facet bundle requires a JavaScript host. " +
        "This .NET port loads facets as C# AssemblyLoadContext plugins instead; " +
        "inject an IFacetModuleHost to run JavaScript bundles.";

    public object? ExecuteCommonJsModule(string source, string modulePath,
        IReadOnlyList<string> externalImports, FacetBundleExternalResolver? resolver)
        => throw new NotSupportedException(Message);
}

/// <summary>
/// facet bundle 的清单/产物读取与加载。对应 TS <c>node/bundle-loader.ts</c>：
/// 版本化清单校验、条目完整性校验、相对文件名解析、包 specifier 校验、
/// artifact 物化与逐次加载的模块代。
/// </summary>
public static class FacetBundleLoader
{
    private const string IntegrityPrefix = "sha256-";

    /// <summary>读取并校验版本化的 facet bundle 清单。对应 TS <c>readFacetBundleManifest</c>。</summary>
    public static async Task<FacetBundleManifest> ReadFacetBundleManifestAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.GetFullPath(path);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not read facet bundle manifest {manifestPath}", error);
        }
        return ValidateManifest(parsed, manifestPath);
    }

    /// <summary>读取并校验磁盘上可传输的单个条目。对应 TS <c>readFacetBundleArtifact</c>。</summary>
    public static async Task<FacetBundleArtifact> ReadFacetBundleArtifactAsync(string manifestPath, string entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.Length == 0) throw new ArgumentException("Facet bundle entry name must not be empty");
        var resolvedManifestPath = Path.GetFullPath(manifestPath);
        var manifest = await ReadFacetBundleManifestAsync(resolvedManifestPath, cancellationToken)
            .ConfigureAwait(false);
        if (!manifest.Entries.TryGetValue(entry, out var bundleEntry))
        {
            throw new InvalidOperationException(
                $"Facet bundle {manifest.Plugin.Id} has no entry named {entry}");
        }
        var modulePath = ResolveBundleFile(resolvedManifestPath, bundleEntry.File, "entry");
        var source = await File.ReadAllTextAsync(modulePath, cancellationToken).ConfigureAwait(false);
        VerifySource(source, bundleEntry);
        var sourceMapContents = bundleEntry.SourceMap is null
            ? null
            : await File.ReadAllTextAsync(
                ResolveBundleFile(resolvedManifestPath, bundleEntry.SourceMap, "source map"), cancellationToken)
                .ConfigureAwait(false);

        return new FacetBundleArtifact
        {
            Format = FacetBundleFormats.ArtifactFormat,
            FormatVersion = FacetBundleFormats.ArtifactFormatVersion,
            Plugin = manifest.Plugin,
            EntryName = entry,
            Entry = bundleEntry,
            Source = source,
            SourceMapContents = sourceMapContents,
        };
    }

    /// <summary>
    /// 物化传输来的 artifact，并为每次加载创建新的模块代。
    /// 对应 TS <c>createFacetBundleArtifactLoader</c>。
    /// </summary>
    public static IFacetLoader CreateFacetBundleArtifactLoader(FacetBundleArtifactLoaderOptions options,
        IFacetModuleHost? moduleHost = null)
    {
        var artifact = ValidateArtifact(options.Artifact);
        var temporaryParent = Path.GetFullPath(options.TemporaryDirectory ?? Path.GetTempPath());
        var host = moduleHost ?? new UnsupportedFacetModuleHost();
        return new DelegateFacetLoader(async cancellationToken =>
        {
            Directory.CreateDirectory(temporaryParent);
            // TS 用 mkdtemp(join(temporaryParent, "chord-facet-"))：必须在调用方指定的父目录下建。
            var directory = Path.Combine(temporaryParent, $"chord-facet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                await MaterializeArtifactAsync(directory, artifact, cancellationToken).ConfigureAwait(false);
                var loaded = await CreateFacetBundleLoader(new FacetBundleLoaderOptions
                {
                    ManifestPath = Path.Combine(directory, FacetBundleFormats.ManifestFile),
                    Entry = artifact.EntryName,
                    ResolveExternal = options.ResolveExternal,
                }, host).LoadAsync(cancellationToken).ConfigureAwait(false);

                var disposed = false;
                return new LoadedFacets
                {
                    Facets = loaded.Facets,
                    Dispose = async () =>
                    {
                        if (disposed) return;
                        disposed = true;
                        var errors = new List<Exception>();
                        try
                        {
                            await loaded.Dispose().ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                        try
                        {
                            FacetBundler.TryDeleteDirectory(directory);
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                        if (errors.Count == 1) throw errors[0];
                        if (errors.Count > 1)
                        {
                            throw new AggregateException("Failed to dispose facet bundle artifact", errors);
                        }
                    },
                };
            }
            catch (Exception error)
            {
                try
                {
                    FacetBundler.TryDeleteDirectory(directory);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Facet bundle artifact loading and cleanup failed",
                        error, cleanupError);
                }
                throw;
            }
        });
    }

    /// <summary>为一个不透明 entry 创建可复用的加载器。对应 TS <c>createFacetBundleLoader</c>。</summary>
    public static IFacetLoader CreateFacetBundleLoader(FacetBundleLoaderOptions options,
        IFacetModuleHost? moduleHost = null)
    {
        if (options.Entry.Length == 0) throw new ArgumentException("Facet bundle entry name must not be empty");
        var manifestPath = Path.GetFullPath(options.ManifestPath);
        var host = moduleHost ?? new UnsupportedFacetModuleHost();
        return new DelegateFacetLoader(async cancellationToken =>
        {
            var manifest = await ReadFacetBundleManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (!manifest.Entries.TryGetValue(options.Entry, out var entry))
            {
                throw new InvalidOperationException(
                    $"Facet bundle {manifest.Plugin.Id} has no entry named {options.Entry}");
            }
            var modulePath = ResolveBundleFile(manifestPath, entry.File, "entry");
            try
            {
                var source = await File.ReadAllTextAsync(modulePath, cancellationToken).ConfigureAwait(false);
                if (options.VerifyIntegrity != false) VerifySource(source, entry);
                var exported = host.ExecuteCommonJsModule(source, modulePath, entry.ExternalImports,
                    options.ResolveExternal);
                var facets = FacetsFromModule(exported, manifest.Plugin.Id, options.Entry);
                var disposed = false;
                LoadedFacets? loaded = null;
                loaded = new LoadedFacets
                {
                    Facets = facets,
                    Dispose = () =>
                    {
                        if (disposed) return Task.CompletedTask;
                        disposed = true;
                        loaded!.ClearFacets();
                        return Task.CompletedTask;
                    },
                };
                return loaded;
            }
            catch (Exception error)
            {
                var detail = $": {error.Message}";
                throw new InvalidOperationException(
                    $"Could not load facet bundle entry {manifest.Plugin.Id}/{options.Entry}{detail}", error);
            }
        });
    }

    /// <summary>校验并规范化清单。对应 TS <c>validateManifest</c>。</summary>
    public static FacetBundleManifest ValidateManifest(JsonNode? value, string path)
    {
        if (value is not JsonObject manifest || manifest.Str("format") != FacetBundleFormats.Format)
        {
            throw new InvalidOperationException($"Invalid facet bundle manifest format in {path}");
        }
        if (manifest.Num("formatVersion") != FacetBundleFormats.FormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported facet bundle manifest version in {path}: {manifest["formatVersion"]}");
        }
        if (manifest["plugin"] is not JsonObject plugin || plugin.Str("id") is not { Length: > 0 } pluginId)
        {
            throw new InvalidOperationException($"Facet bundle manifest has an invalid plugin identity in {path}");
        }
        if (plugin.ContainsKey("version") && plugin.Str("version") is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Facet bundle manifest has an invalid plugin version in {path}");
        }
        if (manifest["entries"] is not JsonObject rawEntries || rawEntries.Count == 0)
        {
            throw new InvalidOperationException($"Facet bundle manifest has no entries in {path}");
        }

        var entries = new Dictionary<string, FacetBundleEntry>(StringComparer.Ordinal);
        foreach (var (name, candidate) in rawEntries)
        {
            if (name.Length == 0 || candidate is not JsonObject entry)
            {
                throw new InvalidOperationException($"Facet bundle manifest has an invalid entry in {path}");
            }
            var file = entry.Str("file")
                ?? throw new InvalidOperationException($"Facet bundle entry {name} has no file");
            ResolveBundleFile(path, file, $"entry {name}");
            var integrity = entry.Str("integrity")
                ?? throw new InvalidOperationException($"Facet bundle entry {name} has no integrity");
            ParseIntegrity(integrity);
            if (entry["externalImports"] is not JsonArray externalImports
                || externalImports.Any(item => item is not JsonValue element
                    || !element.TryGetValue<string>(out _)))
            {
                throw new InvalidOperationException($"Facet bundle entry {name} has invalid external imports");
            }
            var imports = externalImports
                .Select(item => item!.GetValue<string>())
                .ToList();
            if (imports.Distinct(StringComparer.Ordinal).Count() != imports.Count)
            {
                throw new InvalidOperationException($"Facet bundle entry {name} has duplicate external imports");
            }
            string? sourceMap = null;
            if (entry["sourceMap"] is not null)
            {
                sourceMap = entry.Str("sourceMap")
                    ?? throw new InvalidOperationException($"Facet bundle entry {name} has an invalid source map");
                ResolveBundleFile(path, sourceMap, $"entry {name} source map");
            }
            entries[name] = new FacetBundleEntry
            {
                File = file,
                Integrity = integrity,
                ExternalImports = imports,
                SourceMap = sourceMap,
            };
        }

        return new FacetBundleManifest
        {
            Format = FacetBundleFormats.Format,
            FormatVersion = FacetBundleFormats.FormatVersion,
            Plugin = new FacetBundlePlugin { Id = pluginId, Version = plugin.Str("version") },
            Entries = entries,
        };
    }

    /// <summary>校验并规范化 artifact。对应 TS <c>validateArtifact</c>。</summary>
    public static FacetBundleArtifact ValidateArtifact(JsonNode? value)
    {
        if (value is not JsonObject artifact
            || artifact.Str("format") != FacetBundleFormats.ArtifactFormat
            || artifact.Num("formatVersion") != FacetBundleFormats.ArtifactFormatVersion
            || artifact.Str("entryName") is not { Length: > 0 } entryName
            || artifact.Str("source") is not { } source)
        {
            throw new InvalidOperationException("Invalid facet bundle artifact");
        }

        var manifest = ValidateManifest(new JsonObject
        {
            ["format"] = FacetBundleFormats.Format,
            ["formatVersion"] = FacetBundleFormats.FormatVersion,
            ["plugin"] = artifact["plugin"]?.DeepClone(),
            ["entries"] = new JsonObject { [entryName] = artifact["entry"]?.DeepClone() },
        }, "facet bundle artifact");
        var entry = manifest.Entries[entryName];
        var sourceMapContents = artifact.Str("sourceMapContents");
        if (entry.SourceMap is null)
        {
            if (sourceMapContents is not null)
            {
                throw new InvalidOperationException("Facet bundle artifact has source map contents without a source map");
            }
        }
        else if (sourceMapContents is null)
        {
            throw new InvalidOperationException("Facet bundle artifact is missing its source map contents");
        }

        VerifySource(source, entry);
        return new FacetBundleArtifact
        {
            Format = FacetBundleFormats.ArtifactFormat,
            FormatVersion = FacetBundleFormats.ArtifactFormatVersion,
            Plugin = manifest.Plugin,
            EntryName = entryName,
            Entry = entry,
            Source = source,
            SourceMapContents = sourceMapContents,
        };
    }

    /// <summary>把 artifact 物化成「清单 + 条目文件（+ source map）」。对应 TS <c>materializeArtifact</c>。</summary>
    public static async Task MaterializeArtifactAsync(string directory, FacetBundleArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        var manifest = new FacetBundleManifest
        {
            Format = FacetBundleFormats.Format,
            FormatVersion = FacetBundleFormats.FormatVersion,
            Plugin = artifact.Plugin,
            Entries = new Dictionary<string, FacetBundleEntry>(StringComparer.Ordinal)
            {
                [artifact.EntryName] = artifact.Entry,
            },
        };

        var writes = new List<Task>
        {
            File.WriteAllTextAsync(Path.Combine(directory, artifact.Entry.File), artifact.Source, cancellationToken),
            File.WriteAllTextAsync(Path.Combine(directory, FacetBundleFormats.ManifestFile),
                FacetBundler.SerializeManifest(manifest), cancellationToken),
        };
        if (artifact.Entry.SourceMap is not null)
        {
            writes.Add(File.WriteAllTextAsync(Path.Combine(directory, artifact.Entry.SourceMap),
                artifact.SourceMapContents ?? "", cancellationToken));
        }
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    /// <summary>校验 SHA-256 完整性。对应 TS <c>verifySource</c>。</summary>
    public static void VerifySource(string source, FacetBundleEntry entry)
    {
        var expected = ParseIntegrity(entry.Integrity);
        var actual = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (actual != expected)
        {
            throw new InvalidOperationException($"Facet bundle integrity check failed for {entry.File}");
        }
    }

    /// <summary>解析 <c>sha256-&lt;base64&gt;</c>。对应 TS <c>parseIntegrity</c>。</summary>
    public static string ParseIntegrity(string integrity)
    {
        if (!integrity.StartsWith(IntegrityPrefix, StringComparison.Ordinal)
            || integrity.Length == IntegrityPrefix.Length)
        {
            throw new InvalidOperationException("Facet bundle entry has an invalid SHA-256 integrity value");
        }
        return integrity[IntegrityPrefix.Length..];
    }

    /// <summary>把相对文件名解析成清单同目录下的绝对路径。对应 TS <c>resolveBundleFile</c>。</summary>
    public static string ResolveBundleFile(string manifestPath, string file, string label)
    {
        if (file.Length == 0 || Path.IsPathRooted(file) || Path.GetFileName(file) != file
            || file is "." or "..")
        {
            throw new InvalidOperationException(
                $"Facet bundle {label} must be a filename relative to its manifest");
        }
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, file);
    }

    /// <summary>校验包 specifier 形状。对应 TS <c>validatePackageSpecifier</c>。</summary>
    public static void ValidatePackageSpecifier(string specifier)
    {
        var segments = specifier.Split('/');
        var packageParts = specifier.StartsWith('@') ? 2 : 1;
        if (specifier.StartsWith('.') || specifier.StartsWith('/') || specifier.StartsWith('#')
            || specifier.Contains(':') || specifier.Contains('\\')
            || segments.Length < packageParts
            || segments.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Facet bundle artifact has an unsupported external import: {specifier}");
        }
    }

    /// <summary>校验模块导出并取出 facets。对应 TS <c>facetsFromModule</c>。</summary>
    public static IReadOnlyList<IFacet> FacetsFromModule(object? imported, string pluginId, string entryName)
    {
        var exported = imported switch
        {
            IReadOnlyDictionary<string, object?> map when map.TryGetValue("default", out var @default) => @default,
            null => null,
            _ => imported,
        };
        var candidates = exported switch
        {
            IReadOnlyList<object?> list => list,
            null => [],
            _ => (IReadOnlyList<object?>)[exported],
        };
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Facet bundle entry {pluginId}/{entryName} exported no facets");
        }

        var facets = new List<IFacet>();
        foreach (var candidate in candidates)
        {
            if (candidate is not IFacet facet || facet.Id.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Facet bundle entry {pluginId}/{entryName} has a facet with an invalid ID");
            }
            facets.Add(facet);
        }
        var ids = facets.Select(facet => facet.Id).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new InvalidOperationException(
                $"Facet bundle entry {pluginId}/{entryName} exports duplicate facet IDs");
        }
        return facets;
    }
}

/// <summary>由委托实现的 facet 加载器（C# 无对象字面量，故用该薄包装承载 <c>load()</c>）。</summary>
internal sealed class DelegateFacetLoader(Func<CancellationToken, Task<LoadedFacets>> load) : IFacetLoader
{
    public Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default) => load(cancellationToken);
}
