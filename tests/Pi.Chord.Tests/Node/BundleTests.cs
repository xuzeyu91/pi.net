using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Facets;
using Pi.Chord.Node;
using Xunit;

namespace Pi.Chord.Tests.Node;

/// <summary>json.ts 测试（严格 JSON 契约）。</summary>
public class JsonContractTests
{
    [Fact]
    public void AcceptsOnlyStrictJsonValues()
    {
        Assert.True(Json.IsJsonValue(null));
        Assert.True(Json.IsJsonValue("text"));
        Assert.True(Json.IsJsonValue(1.5));
        Assert.True(Json.IsJsonValue(new List<object?> { 1, "a", null }));
        Assert.True(Json.IsJsonValue(new Dictionary<string, object?> { ["a"] = new List<object?> { true } }));

        // 非有限数字与非 JSON 类型被拒绝。
        Assert.False(Json.IsJsonValue(double.NaN));
        Assert.False(Json.IsJsonValue(double.PositiveInfinity));
        Assert.False(Json.IsJsonValue(new object()));
        Assert.False(Json.IsJsonValue(new Dictionary<string, object?> { ["a"] = new object() }));
    }

    [Fact]
    public void RejectsCycles()
    {
        var list = new List<object?>();
        list.Add(list);
        Assert.False(Json.IsJsonValue(list));
        Assert.Throws<InvalidOperationException>(() => Json.CopyJson(list));

        var map = new Dictionary<string, object?>();
        map["self"] = map;
        Assert.False(Json.IsJsonValue(map));
    }

    [Fact]
    public void CopyIsAliasFreeAndCanOmitNulls()
    {
        var inner = new List<object?> { 1, 2 };
        var source = new Dictionary<string, object?> { ["items"] = inner, ["note"] = null };
        var copied = (Dictionary<string, object?>)Json.CopyJson(source)!;

        Assert.NotSame(source, copied);
        Assert.NotSame(inner, copied["items"]);
        Assert.Equal(2, ((List<object?>)copied["items"]!).Count);
        Assert.True(copied.ContainsKey("note"));

        var omitted = (Dictionary<string, object?>)Json.CopyJson(source,
            new CopyJsonOptions { OmitUndefinedProperties = true })!;
        Assert.False(omitted.ContainsKey("note"));
    }

    [Fact]
    public void RejectsNonFiniteNumbersOnCopy()
    {
        Assert.Throws<InvalidOperationException>(() => Json.CopyJson(double.NaN));
        Assert.Throws<InvalidOperationException>(() => Json.CopyJson(new Dictionary<string, object?>
        {
            ["n"] = double.PositiveInfinity,
        }));
    }
}

/// <summary>node/manifest.ts + node/bundle.ts 测试（打包、清单、原子替换）。</summary>
public class FacetBundleTests
{
    /// <summary>脚本化假打包器：按请求产出确定的 cjs 内容。</summary>
    private sealed class FakeBundler(Func<FacetEntryBundleRequest, string> source,
        Func<FacetEntryBundleRequest, IReadOnlyList<string>>? imports = null,
        Func<FacetEntryBundleRequest, IReadOnlyList<string>>? extraOutputs = null) : IFacetEntryBundler
    {
        public int Calls { get; private set; }

        public async Task<FacetEntryBundleOutput> BundleEntryAsync(FacetEntryBundleRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var fileName = $"{request.EntryNames.Replace("[hash]", "abc123")}.cjs";
            var outputPath = Path.Combine(request.TemporaryDirectory, fileName);
            await File.WriteAllTextAsync(outputPath, source(request), cancellationToken);
            var outputs = new List<string> { outputPath };
            string? sourceMapPath = null;
            if (request.SourceMap)
            {
                sourceMapPath = $"{outputPath}.map";
                await File.WriteAllTextAsync(sourceMapPath, "{}", cancellationToken);
                outputs.Add(sourceMapPath);
            }
            if (extraOutputs is not null) outputs.AddRange(extraOutputs(request));
            return new FacetEntryBundleOutput
            {
                OutputPath = outputPath,
                ExternalImports = imports?.Invoke(request) ?? [],
                OutputPaths = outputs,
                SourceMapPath = sourceMapPath,
            };
        }
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chord-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task BundlesEntriesIntoContentAddressedManifest()
    {
        var working = TempDirectory();
        try
        {
            var result = await FacetBundler.BundleFacetsAsync(new BundleFacetsOptions
            {
                Plugin = new FacetBundlePlugin { Id = "acme.plugin", Version = "1.2.3" },
                Entries = new Dictionary<string, string> { ["alpha"] = "src/alpha.ts" },
                Outdir = "dist",
                WorkingDirectory = working,
                External = ["lodash"],
            }, new FakeBundler(_ => "module.exports = {};", _ => ["lodash", "@earendil-works/chord"]));

            Assert.Equal(FacetBundleFormats.Format, result.Manifest.Format);
            Assert.Equal(FacetBundleFormats.FormatVersion, result.Manifest.FormatVersion);
            Assert.Equal("acme.plugin", result.Manifest.Plugin.Id);
            Assert.Equal("1.2.3", result.Manifest.Plugin.Version);

            var entry = result.Manifest.Entries["alpha"];
            Assert.EndsWith(".cjs", entry.File);
            Assert.Null(entry.SourceMap);
            // external 去重后按字典序。
            Assert.Equal(["@earendil-works/chord", "lodash"], entry.ExternalImports);

            var expectedIntegrity = "sha256-" + Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes("module.exports = {};")));
            Assert.Equal(expectedIntegrity, entry.Integrity);

            // 清单已落盘，且内容可被重新读回。
            Assert.True(File.Exists(result.ManifestPath));
            var reloaded = await FacetBundleLoader.ReadFacetBundleManifestAsync(result.ManifestPath);
            Assert.Equal(entry.Integrity, reloaded.Entries["alpha"].Integrity);
            Assert.Equal(FacetBundleFormats.ManifestFile, Path.GetFileName(result.ManifestPath));
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(working);
        }
    }

    [Fact]
    public async Task EmitsSourceMapWhenRequestedAndReplacesExistingOutdir()
    {
        var working = TempDirectory();
        try
        {
            var outdir = Path.Combine(working, "dist");
            Directory.CreateDirectory(outdir);
            File.WriteAllText(Path.Combine(outdir, "stale.txt"), "old");

            var result = await FacetBundler.BundleFacetsAsync(new BundleFacetsOptions
            {
                Plugin = new FacetBundlePlugin { Id = "p" },
                Entries = new Dictionary<string, string> { ["beta"] = "src/beta.ts" },
                Outdir = "dist",
                WorkingDirectory = working,
                SourceMap = true,
            }, new FakeBundler(_ => "x"));

            Assert.Equal("beta", Assert.Single(result.Manifest.Entries).Key);
            var entry = result.Manifest.Entries["beta"];
            Assert.Equal($"{entry.File}.map", entry.SourceMap);
            // 旧目录被整体替换（陈旧文件消失）。
            Assert.False(File.Exists(Path.Combine(outdir, "stale.txt")));
            Assert.True(File.Exists(Path.Combine(outdir, entry.SourceMap!)));
            // 无临时目录残留。
            Assert.Empty(Directory.GetDirectories(working, ".*.tmp-*"));
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(working);
        }
    }

    [Fact]
    public async Task RejectsUnexpectedOutputsAndInvalidPaths()
    {
        var working = TempDirectory();
        try
        {
            var extra = Path.Combine(working, "unexpected.txt");
            File.WriteAllText(extra, "x");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => FacetBundler.BundleFacetsAsync(
                new BundleFacetsOptions
                {
                    Plugin = new FacetBundlePlugin { Id = "p" },
                    Entries = new Dictionary<string, string> { ["a"] = "src/a.ts" },
                    Outdir = "dist",
                    WorkingDirectory = working,
                },
                new FakeBundler(_ => "x", extraOutputs: _ => [extra])));
            Assert.Contains("produced files other than its JavaScript bundle and source map", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(working);
        }
    }

    [Fact]
    public async Task WrapsBundlerDiagnostics()
    {
        var working = TempDirectory();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => FacetBundler.BundleFacetsAsync(
                new BundleFacetsOptions
                {
                    Plugin = new FacetBundlePlugin { Id = "p" },
                    Entries = new Dictionary<string, string> { ["a"] = "src/a.ts" },
                    Outdir = "dist",
                    WorkingDirectory = working,
                },
                new ThrowingBundler()));
            Assert.Contains("Could not bundle facet entry a", error.Message);
            Assert.Contains("src/a.ts:3:1: Unexpected token", error.Message);
            // 失败后临时目录被清理。
            Assert.Empty(Directory.GetDirectories(working, ".*.tmp-*"));
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(working);
        }
    }

    private sealed class ThrowingBundler : IFacetEntryBundler
    {
        public Task<FacetEntryBundleOutput> BundleEntryAsync(FacetEntryBundleRequest request,
            CancellationToken cancellationToken = default)
            => throw new FacetBundleBuildException("build failed", ["src/a.ts:3:1: Unexpected token"]);
    }

    [Fact]
    public async Task ValidatesOptions()
    {
        var baseOptions = new BundleFacetsOptions
        {
            Plugin = new FacetBundlePlugin { Id = "p" },
            Entries = new Dictionary<string, string> { ["a"] = "a.ts" },
            Outdir = "dist",
        };
        var bundler = new FakeBundler(_ => "x");

        await Assert.ThrowsAsync<ArgumentException>(() => FacetBundler.BundleFacetsAsync(
            baseOptions with { Plugin = new FacetBundlePlugin { Id = "" } }, bundler));
        await Assert.ThrowsAsync<ArgumentException>(() => FacetBundler.BundleFacetsAsync(
            baseOptions with { Entries = new Dictionary<string, string>() }, bundler));
        await Assert.ThrowsAsync<ArgumentException>(() => FacetBundler.BundleFacetsAsync(
            baseOptions with { Entries = new Dictionary<string, string> { [""] = "a.ts" } }, bundler));
        await Assert.ThrowsAsync<ArgumentException>(() => FacetBundler.BundleFacetsAsync(
            baseOptions with { External = [""] }, bundler));
    }

    [Fact]
    public void ShortHashIsTwelveHexChars()
    {
        var hash = FacetBundler.ShortHash("alpha");
        Assert.Equal(12, hash.Length);
        Assert.All(hash, character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.Equal(hash, FacetBundler.ShortHash("alpha"));
    }
}

/// <summary>node/bundle-loader.ts 测试（清单/产物校验、完整性、加载器）。</summary>
public class FacetBundleLoaderTests
{
    private sealed class FakeFacet(string id) : IFacet
    {
        public string Id { get; } = id;

        public void Setup(IFacetEnvironment environment)
        {
        }
    }

    private sealed class FakeModuleHost(object? exports) : IFacetModuleHost
    {
        public object? ExecuteCommonJsModule(string source, string modulePath,
            IReadOnlyList<string> externalImports, FacetBundleExternalResolver? resolver) => exports;
    }

    private static (string Directory, string ManifestPath) WriteBundle(string source,
        IReadOnlyList<string>? externalImports = null, string? integrityOverride = null,
        string? sourceMapContents = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chord-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var integrity = integrityOverride
            ?? "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        var entry = new JsonObject
        {
            ["file"] = "facet-entry.cjs",
            ["integrity"] = integrity,
            ["externalImports"] = new JsonArray([.. (externalImports ?? []).Select(i => (JsonNode?)i)]),
        };
        if (sourceMapContents is not null) entry["sourceMap"] = "facet-entry.cjs.map";
        var manifest = new JsonObject
        {
            ["format"] = FacetBundleFormats.Format,
            ["formatVersion"] = FacetBundleFormats.FormatVersion,
            ["plugin"] = new JsonObject { ["id"] = "acme.plugin", ["version"] = "1.0.0" },
            ["entries"] = new JsonObject { ["main"] = entry },
        };
        var manifestPath = Path.Combine(directory, FacetBundleFormats.ManifestFile);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "facet-entry.cjs"), source);
        if (sourceMapContents is not null)
        {
            File.WriteAllText(Path.Combine(directory, "facet-entry.cjs.map"), sourceMapContents);
        }
        return (directory, manifestPath);
    }

    [Fact]
    public async Task ReadsArtifactAndVerifiesIntegrity()
    {
        var (directory, manifestPath) = WriteBundle("module.exports = {};", ["lodash"]);
        try
        {
            var artifact = await FacetBundleLoader.ReadFacetBundleArtifactAsync(manifestPath, "main");
            Assert.Equal(FacetBundleFormats.ArtifactFormat, artifact.Format);
            Assert.Equal("main", artifact.EntryName);
            Assert.Equal("acme.plugin", artifact.Plugin.Id);
            Assert.Equal("module.exports = {};", artifact.Source);
            Assert.Null(artifact.SourceMapContents);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoaderProducesFacetsFromModuleHost()
    {
        var (directory, manifestPath) = WriteBundle("module.exports = {};");
        try
        {
            var loader = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = manifestPath, Entry = "main" },
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("alpha") }));

            var loaded = await loader.LoadAsync();
            Assert.Equal("alpha", Assert.Single(loaded.Facets).Id);
            await loaded.DisposeAsync();
            // dispose 后 facets 清空（幂等）。
            Assert.Empty(loaded.Facets);
            await loaded.DisposeAsync();
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoaderRejectsIntegrityMismatchUnknownEntryAndBadExports()
    {
        var (directory, manifestPath) = WriteBundle("module.exports = {};", integrityOverride: "sha256-wrong");
        try
        {
            var loader = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = manifestPath, Entry = "main" },
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("a") }));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync());
            Assert.Contains("integrity check failed", error.Message);
            Assert.Contains("acme.plugin/main", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }

        var (okDirectory, okManifest) = WriteBundle("module.exports = {};");
        try
        {
            var missing = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = okManifest, Entry = "nope" },
                new FakeModuleHost(null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.LoadAsync());

            var noFacets = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = okManifest, Entry = "main" },
                new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new List<object?>() }));
            var noFacetsError = await Assert.ThrowsAsync<InvalidOperationException>(() => noFacets.LoadAsync());
            Assert.Contains("exported no facets", noFacetsError.Message);

            var duplicates = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = okManifest, Entry = "main" },
                new FakeModuleHost(new Dictionary<string, object?>
                {
                    ["default"] = new List<object?> { new FakeFacet("dup"), new FakeFacet("dup") },
                }));
            var duplicatesError = await Assert.ThrowsAsync<InvalidOperationException>(() => duplicates.LoadAsync());
            Assert.Contains("duplicate facet IDs", duplicatesError.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(okDirectory);
        }
    }

    [Fact]
    public async Task DefaultModuleHostRejectsJavaScriptBundles()
    {
        var (directory, manifestPath) = WriteBundle("module.exports = {};");
        try
        {
            var loader = FacetBundleLoader.CreateFacetBundleLoader(
                new FacetBundleLoaderOptions { ManifestPath = manifestPath, Entry = "main" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync());
            Assert.Contains("JavaScript facet bundle requires a JavaScript host", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ArtifactLoaderMaterializesAndCleansUp()
    {
        var (directory, manifestPath) = WriteBundle("module.exports = {};", sourceMapContents: "{}");
        try
        {
            var artifact = await FacetBundleLoader.ReadFacetBundleArtifactAsync(manifestPath, "main");
            Assert.Equal("{}", artifact.SourceMapContents);

            var temporary = Path.Combine(Path.GetTempPath(), $"chord-artifact-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporary);
            try
            {
                var loader = FacetBundleLoader.CreateFacetBundleArtifactLoader(
                    new FacetBundleArtifactLoaderOptions
                    {
                        Artifact = JsonSerializer.SerializeToNode(artifact, new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        }),
                        TemporaryDirectory = temporary,
                    },
                    new FakeModuleHost(new Dictionary<string, object?> { ["default"] = new FakeFacet("beta") }));

                var loaded = await loader.LoadAsync();
                Assert.Equal("beta", Assert.Single(loaded.Facets).Id);
                var generation = Assert.Single(Directory.GetDirectories(temporary));
                await loaded.DisposeAsync();
                Assert.False(Directory.Exists(generation));
            }
            finally
            {
                FacetBundler.TryDeleteDirectory(temporary);
            }
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void ValidateManifestRejectsMalformedShapes()
    {
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidateManifest(
            new JsonObject { ["format"] = "nope" }, "p"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidateManifest(
            new JsonObject { ["format"] = FacetBundleFormats.Format, ["formatVersion"] = 1 }, "p"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidateManifest(new JsonObject
        {
            ["format"] = FacetBundleFormats.Format,
            ["formatVersion"] = FacetBundleFormats.FormatVersion,
            ["plugin"] = new JsonObject { ["id"] = "" },
            ["entries"] = new JsonObject { ["a"] = new JsonObject() },
        }, "p"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidateManifest(new JsonObject
        {
            ["format"] = FacetBundleFormats.Format,
            ["formatVersion"] = FacetBundleFormats.FormatVersion,
            ["plugin"] = new JsonObject { ["id"] = "p" },
            ["entries"] = new JsonObject(),
        }, "p"));
    }

    [Fact]
    public void RejectsAbsoluteAndNestedBundleFileNames()
    {
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ResolveBundleFile("/m.json", "a/b.cjs", "entry"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ResolveBundleFile("/m.json", "..", "entry"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ResolveBundleFile("/m.json", "", "entry"));
    }

    [Fact]
    public void ValidatesPackageSpecifiers()
    {
        FacetBundleLoader.ValidatePackageSpecifier("lodash");
        FacetBundleLoader.ValidatePackageSpecifier("@scope/pkg/sub");
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidatePackageSpecifier("./local"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidatePackageSpecifier("node:fs"));
        Assert.Throws<InvalidOperationException>(() => FacetBundleLoader.ValidatePackageSpecifier("a//b"));
    }
}

/// <summary>node/package.ts 测试（package.json 元数据、约定 entry 解析、目录逃逸防护）。</summary>
public class FacetPackageBundlerTests
{
    private sealed class RecordingBundler : IFacetEntryBundler
    {
        public BundleFacetsOptions? Seen { get; private set; }

        public FacetEntryBundleRequest? LastRequest { get; private set; }

        public Task<FacetEntryBundleOutput> BundleEntryAsync(FacetEntryBundleRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var outputPath = Path.Combine(request.TemporaryDirectory,
                $"{request.EntryNames.Replace("[hash]", "h")}.cjs");
            File.WriteAllText(outputPath, "module.exports = {};");
            var outputs = new List<string> { outputPath };
            string? sourceMapPath = null;
            if (request.SourceMap)
            {
                sourceMapPath = $"{outputPath}.map";
                File.WriteAllText(sourceMapPath, "{}");
                outputs.Add(sourceMapPath);
            }
            return Task.FromResult(new FacetEntryBundleOutput
            {
                OutputPath = outputPath,
                ExternalImports = request.External,
                OutputPaths = outputs,
                SourceMapPath = sourceMapPath,
            });
        }
    }

    private static string CreatePackage(string packageJson, params string[] files)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chord-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), packageJson);
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "export default {};");
        }
        return directory;
    }

    [Fact]
    public async Task ResolvesConventionalAndConfiguredEntries()
    {
        var directory = CreatePackage("""
        {
          "name": "acme.facets",
          "version": "2.0.0",
          "peerDependencies": { "lodash": "*" },
          "chord": {
            "facets": { "configured": "src/configured.ts", "drop": false },
            "external": ["react"],
            "sourceMap": false
          }
        }
        """, "src/conventional.ts", "src/configured.ts", "src/drop.ts");

        try
        {
            var bundler = new RecordingBundler();
            var result = await FacetPackageBundler.BundleFacetPackageAsync(new BundleFacetPackageOptions
            {
                PackagePath = directory,
                Outdir = "dist",
                DefaultFacets = new Dictionary<string, string>
                {
                    ["conventional"] = "src/conventional.ts",
                    ["missing"] = "src/missing.ts",
                    ["drop"] = "src/drop.ts",
                },
            }, bundler);

            Assert.Equal("acme.facets", result.Bundle.Manifest.Plugin.Id);
            Assert.Equal("2.0.0", result.Bundle.Manifest.Plugin.Version);
            // 缺失的约定 entry 被跳过；chord.facets 的 false 删除 drop；configured 覆盖生效。
            Assert.Equal(["configured", "conventional"], result.Bundle.Manifest.Entries.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(Path.GetFullPath(directory), result.PackageDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(directory), "package.json"), result.PackageJsonPath);
            // chord 自身两条 external 恒在首位；peerDependencies + chord.external 各展开一条 "/*"。
            Assert.Equal(
                ["@earendil-works/chord", "@earendil-works/chord/*", "lodash", "lodash/*", "react", "react/*"],
                bundler.LastRequest!.External);
            // sourceMap=false → 无 map 产物。
            Assert.False(bundler.LastRequest.SourceMap);
            Assert.All(result.Bundle.Manifest.Entries.Values, entry => Assert.Null(entry.SourceMap));
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AcceptsPackageJsonPathDirectly()
    {
        var directory = CreatePackage("""
        { "name": "p", "version": "1.0.0", "chord": { "facets": { "a": "src/a.ts" } } }
        """, "src/a.ts");
        try
        {
            var result = await FacetPackageBundler.BundleFacetPackageAsync(new BundleFacetPackageOptions
            {
                PackagePath = Path.Combine(directory, "package.json"),
                Outdir = "dist",
            }, new RecordingBundler());
            Assert.Equal("a", Assert.Single(result.Bundle.Manifest.Entries).Key);
            // 默认 sourceMap=true。
            Assert.True(Assert.Single(result.Bundle.Manifest.Entries.Values).SourceMap is not null);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RejectsMissingMetadataEscapesAndEmptyEntrySet()
    {
        var empty = CreatePackage("""{ "name": "p", "version": "1.0.0" }""");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FacetPackageBundler.BundleFacetPackageAsync(
                    new BundleFacetPackageOptions { PackagePath = empty, Outdir = "dist" },
                    new RecordingBundler()));
            Assert.Contains("has no configured or conventional facet entries", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(empty);
        }

        var escaping = CreatePackage("""
        { "name": "p", "version": "1.0.0", "chord": { "facets": { "a": "../outside.ts" } } }
        """);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FacetPackageBundler.BundleFacetPackageAsync(
                    new BundleFacetPackageOptions { PackagePath = escaping, Outdir = "dist" },
                    new RecordingBundler()));
            Assert.Contains("escapes the package directory", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(escaping);
        }

        var badConfig = CreatePackage("""
        { "name": "p", "version": "1.0.0", "chord": { "unknown": true } }
        """);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FacetPackageBundler.BundleFacetPackageAsync(
                    new BundleFacetPackageOptions { PackagePath = badConfig, Outdir = "dist" },
                    new RecordingBundler()));
            Assert.Contains("unknown field", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(badConfig);
        }

        var missingName = CreatePackage("""{ "version": "1.0.0" }""");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FacetPackageBundler.BundleFacetPackageAsync(
                    new BundleFacetPackageOptions { PackagePath = missingName, Outdir = "dist" },
                    new RecordingBundler()));
            Assert.Contains("non-empty name", error.Message);
        }
        finally
        {
            FacetBundler.TryDeleteDirectory(missingName);
        }
    }
}
