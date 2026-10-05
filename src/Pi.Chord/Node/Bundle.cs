using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pi.Chord.Node;

/// <summary>打包目标平台。对应 TS <c>FacetBundlePlatform</c>。</summary>
public enum FacetBundlePlatform
{
    Node,
    Browser,
    Neutral,
}

/// <summary><c>BundleFacetsAsync</c> 的输入。对应 TS <c>BundleFacetsOptions</c>。</summary>
public sealed record BundleFacetsOptions
{
    public required FacetBundlePlugin Plugin { get; init; }

    /// <summary>应用自选的 entry 名 → TypeScript/JavaScript 源文件路径。</summary>
    public required IReadOnlyDictionary<string, string> Entries { get; init; }

    public required string Outdir { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>额外留给加载方解析的包 import。</summary>
    public IReadOnlyList<string>? External { get; init; }

    public bool? SourceMap { get; init; }

    public bool? Minify { get; init; }

    public IReadOnlyDictionary<string, string>? Define { get; init; }

    public FacetBundlePlatform? Platform { get; init; }

    /// <summary>esbuild target；缺省按平台取 <c>node22.19</c> / <c>es2022</c>。</summary>
    public IReadOnlyList<string>? Target { get; init; }
}

/// <summary><c>BundleFacetsAsync</c> 的产出。对应 TS <c>BundleFacetsResult</c>。</summary>
public sealed record BundleFacetsResult
{
    public required FacetBundleManifest Manifest { get; init; }

    public required string ManifestPath { get; init; }
}

/// <summary>单条目打包请求（esbuild 调用的 C# 形状）。对应 TS <c>bundleEntry</c> 的 buildOptions。</summary>
public sealed record FacetEntryBundleRequest
{
    public required string EntryName { get; init; }

    /// <summary>入口源文件绝对路径。</summary>
    public required string Source { get; init; }

    public required string TemporaryDirectory { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>留给宿主解析的 external（已含 chord 自身的两条 + 调用方声明）。</summary>
    public required IReadOnlyList<string> External { get; init; }

    public required bool SourceMap { get; init; }

    public required bool Minify { get; init; }

    public IReadOnlyDictionary<string, string>? Define { get; init; }

    public required FacetBundlePlatform Platform { get; init; }

    /// <summary>已解析的 esbuild target 列表。</summary>
    public required IReadOnlyList<string> Target { get; init; }

    /// <summary>产物文件名模板。对应 TS <c>entryNames</c>。</summary>
    public required string EntryNames { get; init; }

    /// <summary>产物扩展名映射。对应 TS <c>outExtension: { ".js": ".cjs" }</c>。</summary>
    public required IReadOnlyDictionary<string, string> OutExtension { get; init; }

    /// <summary>banner（<c>"use strict";</c>）。对应 TS <c>banner.js</c>。</summary>
    public required string Banner { get; init; }
}

/// <summary>
/// 单条目打包产出（由 <see cref="IFacetEntryBundler"/> 提供）。
/// 对应 TS 从 esbuild <c>metafile</c> 读出的 outputs 集合。
/// </summary>
public sealed record FacetEntryBundleOutput
{
    /// <summary>入口产出的 JavaScript 绝对路径。</summary>
    public required string OutputPath { get; init; }

    /// <summary>esbuild 报告中 <c>external</c> 的 import specifier。</summary>
    public required IReadOnlyList<string> ExternalImports { get; init; }

    /// <summary>本次构建的全部产出绝对路径（用于「除 JS 与 source map 外不得有别的产出」校验）。</summary>
    public required IReadOnlyList<string> OutputPaths { get; init; }

    /// <summary>source map 绝对路径（请求 <c>SourceMap</c> 为 true 时必填）。</summary>
    public string? SourceMapPath { get; init; }
}

/// <summary>打包失败时携带 esbuild 诊断消息的异常。对应 TS 侧 esbuild 错误的 <c>errors</c> 字段。</summary>
public sealed class FacetBundleBuildException(string message, IReadOnlyList<string> messages, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>逐条诊断（形如 <c>file:line:col: text</c>）。</summary>
    public IReadOnlyList<string> Messages { get; } = messages;
}

/// <summary>
/// 单条目打包器。<para>TS 直接调用 esbuild；C# 无等价打包器，故把这一步抽成注入点——
/// 宿主可用 JS 侧适配器（node 子进程）实现，或在 C# 插件路线下换成 AssemblyLoadContext 产物。</para>
/// </summary>
public interface IFacetEntryBundler
{
    Task<FacetEntryBundleOutput> BundleEntryAsync(FacetEntryBundleRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// facet 打包器。对应 TS <c>node/bundle.ts</c>：把每个不透明 entry 打成一个独立的内容寻址
/// CommonJS 文件，写出清单，并以「临时目录 + 原子替换」更新输出目录。
/// </summary>
public static class FacetBundler
{
    /// <summary>始终视为 external 的包。对应 TS 的 external 前缀。</summary>
    public static readonly IReadOnlyList<string> BuiltinExternals = ["@earendil-works/chord", "@earendil-works/chord/*"];

    public static async Task<BundleFacetsResult> BundleFacetsAsync(BundleFacetsOptions options,
        IFacetEntryBundler bundler, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Directory.GetCurrentDirectory());
        var outputDirectory = Path.GetFullPath(Path.Combine(workingDirectory, options.Outdir));
        var outputParent = Path.GetDirectoryName(outputDirectory)
            ?? throw new InvalidOperationException($"Facet bundle output directory has no parent: {outputDirectory}");
        Directory.CreateDirectory(outputParent);
        var temporaryDirectory = Path.Combine(outputParent,
            $".{Path.GetFileName(outputDirectory)}.tmp-{Guid.NewGuid()}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var entries = new Dictionary<string, FacetBundleEntry>(StringComparer.Ordinal);
            foreach (var (entryName, source) in options.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                entries[entryName] = await BundleEntryAsync(new FacetEntryBundleRequest
                {
                    EntryName = entryName,
                    Source = Path.GetFullPath(Path.Combine(workingDirectory, source)),
                    TemporaryDirectory = temporaryDirectory,
                    WorkingDirectory = workingDirectory,
                    External = ResolveExternals(options),
                    SourceMap = options.SourceMap == true,
                    Minify = options.Minify == true,
                    Define = options.Define,
                    Platform = options.Platform ?? FacetBundlePlatform.Node,
                    Target = ResolveTarget(options),
                    EntryNames = $"facet-{ShortHash(entryName)}-[hash]",
                    OutExtension = new Dictionary<string, string> { [".js"] = ".cjs" },
                    Banner = "\"use strict\";",
                }, bundler, cancellationToken).ConfigureAwait(false);
            }

            var manifest = new FacetBundleManifest
            {
                Format = FacetBundleFormats.Format,
                FormatVersion = FacetBundleFormats.FormatVersion,
                Plugin = options.Plugin,
                Entries = entries,
            };
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, FacetBundleFormats.ManifestFile),
                SerializeManifest(manifest), cancellationToken).ConfigureAwait(false);
            await ReplaceDirectoryAsync(temporaryDirectory, outputDirectory, cancellationToken).ConfigureAwait(false);
            return new BundleFacetsResult
            {
                Manifest = manifest,
                ManifestPath = Path.Combine(outputDirectory, FacetBundleFormats.ManifestFile),
            };
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static async Task<FacetBundleEntry> BundleEntryAsync(FacetEntryBundleRequest request,
        IFacetEntryBundler bundler, CancellationToken cancellationToken)
    {
        FacetEntryBundleOutput output;
        try
        {
            output = await bundler.BundleEntryAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (FacetBundleBuildException error)
        {
            var detail = error.Messages.Count == 0 ? "" : $"\n{string.Join("\n", error.Messages)}";
            throw new InvalidOperationException(
                $"Could not bundle facet entry {request.EntryName}{detail}", error);
        }

        var absoluteOutputPath = Path.GetFullPath(output.OutputPath);
        var file = Path.GetRelativePath(request.TemporaryDirectory, absoluteOutputPath);
        if (file.Length == 0 || file.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.GetFileName(file) != file)
        {
            throw new InvalidOperationException(
                $"Facet entry {request.EntryName} produced an invalid output path");
        }

        var sourceMap = request.SourceMap ? $"{file}.map" : null;
        var allowedOutputs = new HashSet<string>(StringComparer.Ordinal) { absoluteOutputPath };
        if (sourceMap is not null)
        {
            allowedOutputs.Add(Path.Combine(request.TemporaryDirectory, sourceMap));
        }
        var unexpectedOutputs = output.OutputPaths
            .Select(Path.GetFullPath)
            .Where(path => !allowedOutputs.Contains(path))
            .ToList();
        if (unexpectedOutputs.Count > 0)
        {
            throw new InvalidOperationException(
                $"Facet entry {request.EntryName} produced files other than its JavaScript bundle and source map");
        }

        var contents = await File.ReadAllBytesAsync(absoluteOutputPath, cancellationToken).ConfigureAwait(false);
        if (sourceMap is not null)
        {
            var sourceMapPath = Path.Combine(request.TemporaryDirectory, sourceMap);
            if (!File.Exists(sourceMapPath))
            {
                throw new InvalidOperationException(
                    $"Facet entry {request.EntryName} did not produce its source map");
            }
        }

        return new FacetBundleEntry
        {
            File = file,
            Integrity = $"sha256-{Convert.ToBase64String(SHA256.HashData(contents))}",
            ExternalImports = output.ExternalImports
                .Distinct(StringComparer.Ordinal)
                .OrderBy(specifier => specifier, StringComparer.Ordinal)
                .ToList(),
            SourceMap = sourceMap,
        };
    }

    private static IReadOnlyList<string> ResolveExternals(BundleFacetsOptions options)
    {
        var externals = new List<string>(BuiltinExternals);
        externals.AddRange(options.External ?? []);
        return externals.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<string> ResolveTarget(BundleFacetsOptions options)
    {
        if (options.Target is { Count: > 0 }) return options.Target;
        return options.Platform == FacetBundlePlatform.Node || options.Platform is null
            ? ["node22.19"]
            : ["es2022"];
    }

    private static void ValidateOptions(BundleFacetsOptions options)
    {
        if (options.Plugin.Id.Length == 0)
        {
            throw new ArgumentException("Facet bundle plugin ID must not be empty");
        }
        if (options.Plugin.Version is { Length: 0 })
        {
            throw new ArgumentException("Facet bundle plugin version must not be empty");
        }
        if (options.Entries.Count == 0)
        {
            throw new ArgumentException("Facet bundle must contain at least one entry");
        }
        foreach (var (name, source) in options.Entries)
        {
            if (name.Length == 0) throw new ArgumentException("Facet bundle entry name must not be empty");
            if (source.Length == 0)
            {
                throw new ArgumentException($"Facet bundle entry {name} must have a source path");
            }
        }
        foreach (var external in options.External ?? [])
        {
            if (external.Length == 0) throw new ArgumentException("Facet bundle external import must not be empty");
        }
    }

    /// <summary>
    /// 以「旧目录改名备份 → 新目录改名就位 → 删除备份」原子替换输出目录。
    /// 对应 TS <c>replaceDirectory</c>；改名失败时回滚备份。
    /// </summary>
    public static Task ReplaceDirectoryAsync(string temporaryDirectory, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backupDirectory = $"{outputDirectory}.old-{Guid.NewGuid()}";
        var movedExisting = false;
        try
        {
            Directory.Move(outputDirectory, backupDirectory);
            movedExisting = true;
        }
        catch (DirectoryNotFoundException)
        {
            // 输出目录不存在 → 直接就位。
        }
        try
        {
            Directory.Move(temporaryDirectory, outputDirectory);
        }
        catch
        {
            if (movedExisting) Directory.Move(backupDirectory, outputDirectory);
            throw;
        }
        if (movedExisting) TryDeleteDirectory(backupDirectory);
        return Task.CompletedTask;
    }

    /// <summary>entry 名前 12 位 SHA-256 十六进制。对应 TS <c>shortHash</c>。</summary>
    public static string ShortHash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    /// <summary>清单 JSON（缩进 2 空格 + 结尾换行）。对应 TS <c>JSON.stringify(manifest, null, 2) + "\n"</c>。</summary>
    public static string SerializeManifest(FacetBundleManifest manifest)
        => JsonSerializer.Serialize(manifest, FacetBundleJson.Options) + "\n";

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 清理失败不掩盖原始错误（对齐 TS 的 rm force 语义）。
        }
    }
}

/// <summary>facet bundle 清单的 JSON 选项（wire 形状与 TS 一致）。</summary>
internal static class FacetBundleJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
