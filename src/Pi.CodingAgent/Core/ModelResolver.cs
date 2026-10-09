using System.Globalization;
using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>
/// A model plus the thinking level its pattern explicitly asked for.
/// Port of the TS <c>ScopedModel</c> (core/model-resolver.ts).
/// </summary>
public sealed record ScopedModel
{
    public required ModelSpec Model { get; init; }

    /// <summary>
    /// Set when the pattern carried an explicit level (e.g. <c>"model:high"</c>); null otherwise, so the
    /// caller applies its own default.
    /// </summary>
    public ThinkingLevel? ThinkingLevel { get; init; }
}

/// <summary>Port of the TS <c>ParsedModelResult</c>.</summary>
public sealed record ParsedModelResult
{
    public ModelSpec? Model { get; init; }

    /// <summary>Set only when the pattern carried an explicit level, like <see cref="ScopedModel.ThinkingLevel"/>.</summary>
    public ThinkingLevel? ThinkingLevel { get; init; }

    public string? Warning { get; init; }
}

/// <summary>
/// One scoping warning. Port of the TS <c>ModelScopeDiagnostic</c>, whose <c>type</c> / <c>code</c> are
/// string-literal unions; the port keeps them as strings with the literal values named below (the
/// convention already used by <see cref="VirtualModels.RouteReasons"/>).
/// </summary>
public sealed record ModelScopeDiagnostic
{
    /// <summary>The only value TS's <c>type</c> union admits.</summary>
    public static class Types
    {
        public const string Warning = "warning";
    }

    /// <summary>The values TS's <c>code</c> union admits.</summary>
    public static class Codes
    {
        /// <summary>No model matched the pattern.</summary>
        public const string NoMatch = "no-match";

        /// <summary>A model matched, but the pattern's <c>:level</c> suffix was not a thinking level.</summary>
        public const string InvalidThinkingLevel = "invalid-thinking-level";
    }

    /// <summary>Always <see cref="Types.Warning"/>.</summary>
    public required string Type { get; init; }

    /// <summary>One of <see cref="Codes"/>.</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>The pattern as the user wrote it, including any glob characters.</summary>
    public required string Pattern { get; init; }
}

/// <summary>Port of the TS <c>ResolveModelScopeResult</c>.</summary>
public sealed record ResolveModelScopeResult
{
    public required IReadOnlyList<ScopedModel> ScopedModels { get; init; }

    public required IReadOnlyList<ModelScopeDiagnostic> Diagnostics { get; init; }
}

/// <summary>Port of the TS inline options object for <c>resolveCliModel</c>.</summary>
public sealed record ResolveCliModelOptions
{
    public string? CliProvider { get; init; }

    public string? CliModel { get; init; }

    public ThinkingLevel? CliThinking { get; init; }

    public required IModelResolverRuntime Runtime { get; init; }
}

/// <summary>Port of the TS <c>ResolveCliModelResult</c>.</summary>
public sealed record ResolveCliModelResult
{
    public ModelSpec? Model { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public string? Warning { get; init; }

    /// <summary>
    /// Message suitable for CLI display. When set, <see cref="Model"/> is null — except in
    /// <see cref="ModelResolver.ResolveCliModel"/>, which never sets both.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>Port of the TS inline options object for <c>findInitialModel</c>.</summary>
public sealed record InitialModelOptions
{
    public string? CliProvider { get; init; }

    public string? CliModel { get; init; }

    public required IReadOnlyList<ScopedModel> ScopedModels { get; init; }

    public bool IsContinuing { get; init; }

    public string? DefaultProvider { get; init; }

    public string? DefaultModelId { get; init; }

    public ThinkingLevel? DefaultThinkingLevel { get; init; }

    /// <summary>Per-model overrides keyed <c>"provider/modelId"</c>. TS <c>Record&lt;string, ThinkingLevel&gt;</c>.</summary>
    public IReadOnlyDictionary<string, ThinkingLevel>? ModelThinkingLevels { get; init; }

    public required IModelResolverRuntime Runtime { get; init; }
}

/// <summary>Port of the TS <c>InitialModelResult</c>.</summary>
public sealed record InitialModelResult
{
    public ModelSpec? Model { get; init; }

    /// <summary>Never null — TS initializes it to <c>DEFAULT_THINKING_LEVEL</c> on every path.</summary>
    public required ThinkingLevel ThinkingLevel { get; init; }

    public string? FallbackMessage { get; init; }
}

/// <summary>Port of the TS <c>restoreModelFromSession</c> return type.</summary>
public sealed record RestoreModelResult
{
    public ModelSpec? Model { get; init; }

    public string? FallbackMessage { get; init; }
}

/// <summary>
/// Raised where TS <c>findInitialModel</c> prints a red error and then calls <c>process.exit(1)</c>.
/// </summary>
/// <remarks>
/// The line is written to <see cref="ModelResolver.ErrorWriter"/> first so the user sees exactly what TS
/// printed, and the throw replaces the exit so the process boundary (4g) owns the exit code instead of a
/// library call tearing the process down (difference C78).
/// </remarks>
public sealed class ModelResolutionException(string message) : Exception(message);

/// <summary>
/// The slice of <see cref="ModelRuntime"/> this module reads. TS takes the concrete class; C# names the
/// slice so the differential corpus can supply a fixture catalog instead of a real runtime, whose catalog
/// always also holds the builtin providers (difference C81).
/// </summary>
public interface IModelResolverRuntime
{
    /// <summary>Every known model, not only the authenticated ones (TS <c>getModels()</c>).</summary>
    IReadOnlyList<ModelSpec> GetModels();

    /// <summary>One model by provider and id, or null (TS <c>getModel()</c>).</summary>
    ModelSpec? GetModel(string providerId, string modelId);

    /// <summary>Whether the provider has usable credentials (TS <c>hasConfiguredAuth()</c>).</summary>
    bool HasConfiguredAuth(string providerId);

    /// <summary>The authenticated models known so far (TS <c>getAvailableSnapshot()</c>).</summary>
    IReadOnlyList<ModelSpec> GetAvailableSnapshot();

    /// <summary>Refreshes and returns the authenticated models (TS <c>getAvailable()</c>).</summary>
    Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(string? providerId, CancellationToken signal = default);
}

/// <summary>
/// Model resolution, scoping, and initial selection. Port of <c>core/model-resolver.ts</c> (783 lines).
/// </summary>
/// <remarks>
/// <para>
/// Differences recorded against the TS original:
/// <list type="bullet">
/// <item>C74：<c>defaultModelPerProvider</c> 用有序 <see cref="KeyValuePair{TKey,TValue}"/> 列表承载
/// （TS 是对象字面量，<c>Object.keys</c> 的顺序就是插入顺序，而两处优先序遍历依赖它）。查找另走
/// <see cref="DefaultModelIds"/>，等价于 TS 的索引访问。</item>
/// <item>C75：<c>Model&lt;Api&gt;</c> → <see cref="ModelSpec"/>；<c>Model&lt;Api&gt; | undefined</c> →
/// <c>ModelSpec?</c>。TS 的 <c>{...baseModel, id, name}</c> 展开变成 <c>with</c> 表达式。</item>
/// <item>C76：<c>AuthOperationOptions</c> 展开为末位 <see cref="CancellationToken"/>（同 C65）。</item>
/// <item>C77：<c>isValidThinkingLevel</c> 就地复用 <see cref="ThinkingLevels.Parse"/>
/// （其字符串集与 <c>cli/args.ts</c> 的 <c>VALID_THINKING_LEVELS</c> 一致，含 <c>off</c>）。</item>
/// <item>C78：<c>findInitialModel</c> 的 <c>console.error</c> + <c>process.exit(1)</c> 改为「先写
/// <see cref="ErrorWriter"/> 再返回一个以 <see cref="ModelResolutionException"/> 结束的 task」。
/// TS 的 <c>async</c> 函数把同步抛出变成被拒绝的 promise，故这里用 <c>Task.FromException</c> 复刻该形状，
/// 而不是同步抛出——调用方需要 <c>await</c> 才能观察到它。</item>
/// <item>C79：<c>toLowerCase()</c> 走 <see cref="JsString.ToLowerCase"/>（JS 的大小写映射，与
/// <c>ToLowerInvariant</c> 在个别码点上不同）；<c>localeCompare()</c> 走
/// <see cref="Collate"/>（Node 与 .NET 都用 ICU，与 <c>Pi.Tui.Autocomplete</c> 的既有结论一致）。</item>
/// <item>C80：诊断的 <c>type</c> / <c>code</c> 字符串字面量联合保留为 <c>string</c> 加常量类，见
/// <see cref="ModelScopeDiagnostic"/>。</item>
/// <item>C81：运行时入参的类型由 <see cref="ModelRuntime"/> 收窄为 <see cref="IModelResolverRuntime"/>
/// （本模块实际读到的五个成员）。TS 靠结构类型接受任何鸭子对象，C# 需要具名；<see cref="ModelRuntime"/>
/// 隐式实现它，故生产调用点不变。</item>
/// </list>
/// </para>
/// </remarks>
public static class ModelResolver
{
    /// <summary>
    /// Default chat model IDs for providers with built-in chat models. Port of the TS
    /// <c>defaultModelPerProvider</c>.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing: <see cref="FindInitialModelAsync"/> and
    /// <see cref="RestoreModelFromSessionAsync"/> walk it in this order and take the first provider that
    /// has the model available, so reordering changes which model a fresh install picks.
    /// </remarks>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> DefaultModelPerProvider =
    [
        new("amazon-bedrock", "us.anthropic.claude-opus-4-6-v1"),
        new("ant-ling", "Ring-2.6-1T"),
        new("anthropic", "claude-opus-4-8"),
        new("openai", "gpt-5.5"),
        new("azure", "gpt-5.4"),
        new("openai-codex", "gpt-6.1-sol"),
        new("radius", "balanced"),
        new("nvidia", "nvidia/nemotron-3-ultra-550b-a55b"),
        new("deepseek", "deepseek-v4-pro"),
        new("google", "gemini-3.1-pro-preview"),
        new("google-vertex", "gemini-3.1-pro-preview"),
        new("github-copilot", "gpt-5.4"),
        new("openrouter", "moonshotai/kimi-k2.6"),
        new("vercel-ai-gateway", "zai/glm-5.1"),
        new("xai", "grok-4.7"),
        new("groq", "openai/gpt-oss-120b"),
        new("cerebras", "gpt-oss-120b"),
        new("zai", "glm-5.3"),
        new("zai-coding-cn", "glm-5.3"),
        new("mistral", "devstral-medium-latest"),
        new("minimax", "MiniMax-M2.7"),
        new("minimax-cn", "MiniMax-M2.7"),
        new("moonshotai", "kimi-k2.6"),
        new("moonshotai-cn", "kimi-k2.6"),
        new("huggingface", "moonshotai/Kimi-K2.6"),
        new("fireworks", "accounts/fireworks/models/kimi-k3"),
        new("together", "moonshotai/Kimi-K3"),
        new("baseten", "zai-org/GLM-5.2"),
        new("opencode", "kimi-k2.6"),
        new("opencode-go", "kimi-k3"),
        new("kimi-coding", "kimi-for-coding"),
        new("meta", "muse-spark-1.3"),
        new("cloudflare-workers-ai", "@cf/moonshotai/kimi-k2.6"),
        new("cloudflare-ai-gateway", "workers-ai/@cf/moonshotai/kimi-k2.6"),
        new("qwen-token-plan", "qwen3.7-max"),
        new("qwen-token-plan-cn", "qwen3.7-max"),
        new("qwen-token-plan-individual", "qwen3.8-max"),
        new("xiaomi", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-cn", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-ams", "mimo-v2.5-pro"),
        new("xiaomi-token-plan-sgp", "mimo-v2.5-pro"),
    ];

    private static readonly Dictionary<string, string> DefaultModelIds =
        DefaultModelPerProvider.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Trailing date suffix <c>-YYYYMMDD</c>. TS writes <c>\d</c>, which in .NET would also match
    /// non-ASCII digits, so the port spells out <c>[0-9]</c>.</summary>
    private static readonly Regex DatedSuffix = new(@"-[0-9]{8}$", RegexOptions.CultureInvariant);

    /// <summary>The default chat model id for a provider, or null when the provider has none.</summary>
    public static string? GetDefaultModelId(string providerId) => DefaultModelIds.GetValueOrDefault(providerId);

    /// <summary>Whether the provider appears in <see cref="DefaultModelPerProvider"/> (TS <c>id in table</c>).</summary>
    public static bool HasDefaultModelProvider(string providerId) => DefaultModelIds.ContainsKey(providerId);

    /// <summary>
    /// Where <c>console.log</c> / <c>console.warn</c> lines go. Defaults to <see cref="Console.Out"/>;
    /// tests substitute a writer to observe the line without touching the process (convention C3).
    /// </summary>
    internal static TextWriter? OutWriterOverride { get; set; }

    /// <summary>Where <c>console.error</c> lines go. Defaults to <see cref="Console.Error"/>.</summary>
    internal static TextWriter? ErrorWriterOverride { get; set; }

    internal static TextWriter OutWriter => OutWriterOverride ?? Console.Out;

    internal static TextWriter ErrorWriter => ErrorWriterOverride ?? Console.Error;

    // ================================================================= exact / fuzzy matching

    /// <summary>
    /// Helper to check if a model ID looks like an alias (no date suffix). Dates are typically in the
    /// format <c>-20241022</c> or <c>-20250929</c>. Corresponds to the TS <c>isAlias</c>.
    /// </summary>
    private static bool IsAlias(string id)
    {
        if (id.EndsWith("-latest", StringComparison.Ordinal)) return true;

        // Check if ID ends with a date pattern (-YYYYMMDD)
        return !DatedSuffix.IsMatch(id);
    }

    /// <summary>
    /// Find an exact model reference match. Supports either a bare model id or a canonical
    /// <c>provider/modelId</c> reference. When matching by bare id, ambiguous matches across providers
    /// are rejected.
    /// </summary>
    public static ModelSpec? FindExactModelReferenceMatch(string modelReference, IReadOnlyList<ModelSpec> availableModels)
    {
        var trimmedReference = JsString.Trim(modelReference);
        if (trimmedReference.Length == 0) return null;

        var normalizedReference = JsString.ToLowerCase(trimmedReference);

        var canonicalMatches = availableModels
            .Where(model => JsString.ToLowerCase($"{model.Provider}/{model.Id}") == normalizedReference)
            .ToList();
        if (canonicalMatches.Count == 1) return canonicalMatches[0];
        if (canonicalMatches.Count > 1) return null;

        var slashIndex = trimmedReference.IndexOf('/');
        if (slashIndex != -1)
        {
            var provider = JsString.Trim(trimmedReference[..slashIndex]);
            var modelId = JsString.Trim(trimmedReference[(slashIndex + 1)..]);
            if (provider.Length > 0 && modelId.Length > 0)
            {
                var providerLower = JsString.ToLowerCase(provider);
                var modelIdLower = JsString.ToLowerCase(modelId);
                var providerMatches = availableModels
                    .Where(model => JsString.ToLowerCase(model.Provider) == providerLower
                        && JsString.ToLowerCase(model.Id) == modelIdLower)
                    .ToList();
                if (providerMatches.Count == 1) return providerMatches[0];
                if (providerMatches.Count > 1) return null;
            }
        }

        var idMatches = availableModels
            .Where(model => JsString.ToLowerCase(model.Id) == normalizedReference)
            .ToList();
        return idMatches.Count == 1 ? idMatches[0] : null;
    }

    /// <summary>
    /// Try to match a pattern to a model from the available models list, or null when nothing matches.
    /// Corresponds to the TS <c>tryMatchModel</c>.
    /// </summary>
    private static ModelSpec? TryMatchModel(string modelPattern, IReadOnlyList<ModelSpec> availableModels)
    {
        var exactMatch = FindExactModelReferenceMatch(modelPattern, availableModels);
        if (exactMatch is not null) return exactMatch;

        // No exact match - fall back to partial matching
        var patternLower = JsString.ToLowerCase(modelPattern);
        var matches = availableModels
            .Where(m => JsString.ToLowerCase(m.Id).Contains(patternLower, StringComparison.Ordinal)
                || JsString.ToLowerCase(m.Name).Contains(patternLower, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;

        // Separate into aliases and dated versions
        var aliases = matches.Where(m => IsAlias(m.Id)).ToList();
        var datedVersions = matches.Where(m => !IsAlias(m.Id)).ToList();

        if (aliases.Count > 0)
        {
            // Prefer alias - if multiple aliases, pick the one that sorts highest.
            // LINQ's OrderBy is stable, matching JS's Array.prototype.sort.
            return aliases.OrderBy(m => m, Comparer<ModelSpec>.Create((a, b) => Collate(b.Id, a.Id))).First();
        }

        // No alias found, pick latest dated version
        return datedVersions.OrderBy(m => m, Comparer<ModelSpec>.Create((a, b) => Collate(b.Id, a.Id))).First();
    }

    /// <summary>
    /// Build a synthetic model by re-labelling a real model of the provider. Corresponds to the TS
    /// <c>buildFallbackModel</c>.
    /// </summary>
    private static ModelSpec? BuildFallbackModel(string provider, string modelId, IReadOnlyList<ModelSpec> availableModels)
    {
        var providerModels = availableModels.Where(m => m.Provider == provider).ToList();
        if (providerModels.Count == 0) return null;

        var defaultId = GetDefaultModelId(provider);
        var baseModel = defaultId is not null
            ? providerModels.FirstOrDefault(m => m.Id == defaultId) ?? providerModels[0]
            : providerModels[0];

        return baseModel with { Id = modelId, Name = modelId };
    }

    // ================================================================= pattern parsing

    /// <summary>
    /// Parse a pattern to extract model and thinking level. Handles models with colons in their IDs
    /// (e.g. OpenRouter's <c>:exacto</c> suffix).
    /// </summary>
    /// <param name="allowInvalidThinkingLevelFallback">
    /// TS <c>options.allowInvalidThinkingLevelFallback</c>, which itself defaults to true. In strict mode
    /// (<c>false</c>, used by CLI <c>--model</c> parsing) an invalid suffix is treated as part of the model
    /// id and the whole pattern fails, so a typo cannot silently resolve to a different model.
    /// </param>
    public static ParsedModelResult ParseModelPattern(
        string pattern,
        IReadOnlyList<ModelSpec> availableModels,
        bool allowInvalidThinkingLevelFallback = true)
    {
        // Try exact match first
        var exactMatch = TryMatchModel(pattern, availableModels);
        if (exactMatch is not null)
        {
            return new ParsedModelResult { Model = exactMatch, ThinkingLevel = null, Warning = null };
        }

        // No match - try splitting on last colon if present
        var lastColonIndex = JsString.LastIndexOf(pattern, ":");
        if (lastColonIndex == -1)
        {
            // No colons, pattern simply doesn't match any model
            return new ParsedModelResult { Model = null, ThinkingLevel = null, Warning = null };
        }

        var prefix = pattern[..lastColonIndex];
        var suffix = pattern[(lastColonIndex + 1)..];

        if (ThinkingLevels.Parse(suffix) is { } level)
        {
            // Valid thinking level - recurse on prefix and use this level
            var result = ParseModelPattern(prefix, availableModels, allowInvalidThinkingLevelFallback);
            if (result.Model is not null)
            {
                // Only use this thinking level if no warning from inner recursion
                return new ParsedModelResult
                {
                    Model = result.Model,
                    ThinkingLevel = result.Warning is null ? level : null,
                    Warning = result.Warning,
                };
            }

            return result;
        }

        // Invalid suffix
        if (!allowInvalidThinkingLevelFallback)
        {
            return new ParsedModelResult { Model = null, ThinkingLevel = null, Warning = null };
        }

        // Scope mode: recurse on prefix and warn
        var fallback = ParseModelPattern(prefix, availableModels, allowInvalidThinkingLevelFallback);
        if (fallback.Model is not null)
        {
            return new ParsedModelResult
            {
                Model = fallback.Model,
                ThinkingLevel = null,
                Warning = $"Invalid thinking level \"{suffix}\" in pattern \"{pattern}\". Using default instead.",
            };
        }

        return fallback;
    }

    // ================================================================= scope resolution

    /// <summary>
    /// Resolve model patterns to actual <see cref="ModelSpec"/> objects with optional thinking levels.
    /// Format: <c>"pattern:level"</c> where <c>:level</c> is optional. For each pattern, finds all matching
    /// models and picks the best version: prefer an alias (e.g. <c>claude-sonnet-4-5</c>) over dated
    /// versions (<c>claude-sonnet-4-5-20250929</c>); if no alias, pick the latest dated version.
    /// </summary>
    /// <remarks>
    /// Supports models with colons in their IDs (e.g. OpenRouter's <c>model:exacto</c>). The algorithm
    /// tries to match the full pattern first, then progressively strips colon-suffixes to find a match.
    /// </remarks>
    public static ResolveModelScopeResult ResolveModelScopeFromModels(
        IReadOnlyList<string> patterns,
        IReadOnlyList<ModelSpec> models)
    {
        var availableModels = models.ToList();
        var scopedModels = new List<ScopedModel>();
        var diagnostics = new List<ModelScopeDiagnostic>();

        foreach (var pattern in patterns)
        {
            // Check if pattern contains glob characters
            if (pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('['))
            {
                // Extract optional thinking level suffix (e.g., "provider/*:high")
                var colonIdx = JsString.LastIndexOf(pattern, ":");
                var globPattern = pattern;
                ThinkingLevel? thinkingLevel = null;

                if (colonIdx != -1)
                {
                    var suffix = pattern[(colonIdx + 1)..];
                    if (ThinkingLevels.Parse(suffix) is { } level)
                    {
                        thinkingLevel = level;
                        globPattern = pattern[..colonIdx];
                    }
                }

                var exactMatch = FindExactModelReferenceMatch(globPattern, availableModels);
                if (exactMatch is not null)
                {
                    if (!scopedModels.Any(sm => ModelOperations.ModelsAreEqual(sm.Model, exactMatch)))
                    {
                        scopedModels.Add(new ScopedModel { Model = exactMatch, ThinkingLevel = thinkingLevel });
                    }

                    continue;
                }

                // Match against "provider/modelId" format OR just model ID. This allows "*sonnet*" to match
                // without requiring "anthropic/*sonnet*".
                var matchingModels = availableModels
                    .Where(m => Glob.Match($"{m.Provider}/{m.Id}", globPattern, new GlobOptions { NoCase = true })
                        || Glob.Match(m.Id, globPattern, new GlobOptions { NoCase = true }))
                    .ToList();

                if (matchingModels.Count == 0)
                {
                    diagnostics.Add(new ModelScopeDiagnostic
                    {
                        Type = ModelScopeDiagnostic.Types.Warning,
                        Code = ModelScopeDiagnostic.Codes.NoMatch,
                        Message = $"No models match pattern \"{pattern}\"",
                        Pattern = pattern,
                    });
                    continue;
                }

                foreach (var model in matchingModels)
                {
                    if (!scopedModels.Any(sm => ModelOperations.ModelsAreEqual(sm.Model, model)))
                    {
                        scopedModels.Add(new ScopedModel { Model = model, ThinkingLevel = thinkingLevel });
                    }
                }

                continue;
            }

            var parsed = ParseModelPattern(pattern, availableModels);

            if (parsed.Warning is { } warning)
            {
                diagnostics.Add(new ModelScopeDiagnostic
                {
                    Type = ModelScopeDiagnostic.Types.Warning,
                    Code = ModelScopeDiagnostic.Codes.InvalidThinkingLevel,
                    Message = warning,
                    Pattern = pattern,
                });
            }

            if (parsed.Model is null)
            {
                diagnostics.Add(new ModelScopeDiagnostic
                {
                    Type = ModelScopeDiagnostic.Types.Warning,
                    Code = ModelScopeDiagnostic.Codes.NoMatch,
                    Message = $"No models match pattern \"{pattern}\"",
                    Pattern = pattern,
                });
                continue;
            }

            // Avoid duplicates
            if (!scopedModels.Any(sm => ModelOperations.ModelsAreEqual(sm.Model, parsed.Model)))
            {
                scopedModels.Add(new ScopedModel { Model = parsed.Model, ThinkingLevel = parsed.ThinkingLevel });
            }
        }

        return new ResolveModelScopeResult { ScopedModels = scopedModels, Diagnostics = diagnostics };
    }

    /// <summary>Resolve patterns against the runtime's authenticated models, returning warnings instead
    /// of printing them. Corresponds to the TS <c>resolveModelScopeWithDiagnostics</c>.</summary>
    public static async Task<ResolveModelScopeResult> ResolveModelScopeWithDiagnosticsAsync(
        IReadOnlyList<string> patterns,
        IModelResolverRuntime runtime,
        CancellationToken signal = default)
        => ResolveModelScopeFromModels(patterns, await runtime.GetAvailableAsync(null, signal).ConfigureAwait(false));

    /// <summary>Resolve patterns against the runtime's authenticated models, printing every warning in
    /// yellow. Corresponds to the TS <c>resolveModelScope</c>.</summary>
    public static async Task<IReadOnlyList<ScopedModel>> ResolveModelScopeAsync(
        IReadOnlyList<string> patterns,
        IModelResolverRuntime runtime,
        CancellationToken signal = default)
    {
        var resolved =
            await ResolveModelScopeWithDiagnosticsAsync(patterns, runtime, signal).ConfigureAwait(false);
        foreach (var diagnostic in resolved.Diagnostics)
        {
            ErrorWriter.WriteLine(Chalk.Yellow($"Warning: {diagnostic.Message}"));
        }

        return resolved.ScopedModels;
    }

    // ================================================================= CLI model

    /// <summary>
    /// Resolve a single model from CLI flags. Supports <c>--provider &lt;provider&gt; --model &lt;pattern&gt;</c>,
    /// <c>--model &lt;provider&gt;/&lt;pattern&gt;</c>, and fuzzy matching (same rules as model scoping:
    /// exact id, then partial id/name).
    /// </summary>
    /// <remarks>
    /// This does not apply the thinking level by itself, but it may <em>parse</em> and return one from
    /// <c>"&lt;pattern&gt;:&lt;thinking&gt;"</c> so the caller can apply it.
    /// </remarks>
    public static ResolveCliModelResult ResolveCliModel(ResolveCliModelOptions options)
    {
        var cliProvider = options.CliProvider;
        var cliModel = options.CliModel;
        var cliThinking = options.CliThinking;
        var runtime = options.Runtime;

        if (string.IsNullOrEmpty(cliModel))
        {
            return new ResolveCliModelResult { Model = null, Warning = null, Error = null };
        }

        // Important: use *all* models here, not just models with pre-configured auth.
        // This allows "--api-key" to be used for first-time setup.
        var availableModels = runtime.GetModels().ToList();
        if (availableModels.Count == 0)
        {
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = "No models available. Check your installation or add models to models.json.",
            };
        }

        // Build canonical provider lookup (case-insensitive). A later duplicate wins, like Map.set.
        var providerMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in availableModels) providerMap[JsString.ToLowerCase(m.Provider)] = m.Provider;

        var hasCliProvider = !string.IsNullOrEmpty(cliProvider);
        string? provider = hasCliProvider ? providerMap.GetValueOrDefault(JsString.ToLowerCase(cliProvider!)) : null;
        if (hasCliProvider && provider is null)
        {
            return new ResolveCliModelResult
            {
                Model = null,
                Warning = null,
                Error = $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.",
            };
        }

        // If no explicit --provider, try to interpret "provider/model" format first. When the prefix before
        // the first slash matches a known provider, prefer that interpretation over matching models whose IDs
        // literally contain slashes (e.g. "zai/glm-5" should resolve to provider=zai, model=glm-5, not to a
        // vercel-ai-gateway model with id "zai/glm-5").
        var pattern = cliModel;
        var inferredProvider = false;

        if (provider is null)
        {
            var slashIndex = cliModel.IndexOf('/');
            if (slashIndex != -1)
            {
                var maybeProvider = cliModel[..slashIndex];
                if (providerMap.GetValueOrDefault(JsString.ToLowerCase(maybeProvider)) is { } canonical)
                {
                    provider = canonical;
                    pattern = cliModel[(slashIndex + 1)..];
                    inferredProvider = true;
                }
            }
        }

        // If no provider was inferred from the slash, try exact matches without provider inference. This
        // handles models whose IDs naturally contain slashes (e.g. OpenRouter-style IDs). Bare exact IDs can
        // exist in multiple providers, so do not choose by catalog order. Prefer the sole authenticated
        // provider when there is one; otherwise require an explicit provider to avoid silently selecting an
        // unusable provider.
        if (provider is null)
        {
            var lower = JsString.ToLowerCase(cliModel);
            var exactMatches = availableModels
                .Where(m => JsString.ToLowerCase(m.Id) == lower
                    || JsString.ToLowerCase($"{m.Provider}/{m.Id}") == lower)
                .ToList();
            if (exactMatches.Count == 1)
            {
                return new ResolveCliModelResult
                {
                    Model = exactMatches[0],
                    Warning = null,
                    ThinkingLevel = null,
                    Error = null,
                };
            }

            if (exactMatches.Count > 1)
            {
                var authenticatedExactMatches =
                    exactMatches.Where(m => runtime.HasConfiguredAuth(m.Provider)).ToList();
                if (authenticatedExactMatches.Count == 1)
                {
                    return new ResolveCliModelResult
                    {
                        Model = authenticatedExactMatches[0],
                        Warning = null,
                        ThinkingLevel = null,
                        Error = null,
                    };
                }

                var matches = string.Join(", ", exactMatches
                    .Select(m => $"{m.Provider}/{m.Id}")
                    .OrderBy(value => value, Comparer<string>.Create(Collate)));
                var authHint = authenticatedExactMatches.Count == 0
                    ? "No matching provider is authenticated."
                    : "More than one matching provider is authenticated.";
                return new ResolveCliModelResult
                {
                    Model = null,
                    Warning = null,
                    ThinkingLevel = null,
                    Error = $"Model \"{cliModel}\" is ambiguous across providers: {matches}. {authHint}"
                        + " Use --provider or provider/model.",
                };
            }
        }

        if (hasCliProvider && provider is not null)
        {
            // If both were provided, tolerate --model <provider>/<pattern> by stripping the provider prefix
            var prefix = $"{provider}/";
            if (JsString.StartsWith(JsString.ToLowerCase(cliModel), JsString.ToLowerCase(prefix)))
            {
                pattern = cliModel[prefix.Length..];
            }
        }

        var candidates = provider is not null
            ? availableModels.Where(m => m.Provider == provider).ToList()
            : availableModels;
        var parsed = ParseModelPattern(pattern, candidates, allowInvalidThinkingLevelFallback: false);

        if (parsed.Model is not null)
        {
            // If provider inference matched an unauthenticated provider/model pair, prefer one exact raw
            // model-id match that is authenticated. This keeps "provider/model" syntax preferred when usable,
            // but handles models whose literal id starts with a known provider name (for example commandcode
            // model id "xiaomi/mimo-v2.5-pro").
            if (inferredProvider)
            {
                var cliModelLower = JsString.ToLowerCase(cliModel);
                var rawExactMatches = availableModels
                    .Where(m => JsString.ToLowerCase(m.Id) == cliModelLower
                        && !ModelOperations.ModelsAreEqual(m, parsed.Model))
                    .ToList();
                if (rawExactMatches.Count > 0 && !runtime.HasConfiguredAuth(parsed.Model.Provider))
                {
                    var authenticatedRawMatches =
                        rawExactMatches.Where(m => runtime.HasConfiguredAuth(m.Provider)).ToList();
                    if (authenticatedRawMatches.Count == 1)
                    {
                        return new ResolveCliModelResult
                        {
                            Model = authenticatedRawMatches[0],
                            ThinkingLevel = null,
                            Warning = null,
                            Error = null,
                        };
                    }
                }
            }

            return new ResolveCliModelResult
            {
                Model = parsed.Model,
                ThinkingLevel = parsed.ThinkingLevel,
                Warning = parsed.Warning,
                Error = null,
            };
        }

        // If we inferred a provider from the slash but found no match within that provider, fall back to
        // matching the full input as a raw model id across all models. This handles OpenRouter-style IDs like
        // "openai/gpt-4o:extended" where "openai" looks like a provider but the full string is actually a
        // model id on openrouter.
        if (inferredProvider)
        {
            var lower = JsString.ToLowerCase(cliModel);
            var exact = availableModels.FirstOrDefault(m => JsString.ToLowerCase(m.Id) == lower
                || JsString.ToLowerCase($"{m.Provider}/{m.Id}") == lower);
            if (exact is not null)
            {
                return new ResolveCliModelResult
                {
                    Model = exact,
                    Warning = null,
                    ThinkingLevel = null,
                    Error = null,
                };
            }

            // Also try parseModelPattern on the full input against all models
            var fallback = ParseModelPattern(cliModel, availableModels, allowInvalidThinkingLevelFallback: false);
            if (fallback.Model is not null)
            {
                return new ResolveCliModelResult
                {
                    Model = fallback.Model,
                    ThinkingLevel = fallback.ThinkingLevel,
                    Warning = fallback.Warning,
                    Error = null,
                };
            }
        }

        if (provider is not null)
        {
            // Parse thinking level suffix from the pattern before building the fallback model, but only when
            // --thinking is not explicitly provided.
            // e.g. "zai-org/GLM-5.1-FP8:high" -> modelId="zai-org/GLM-5.1-FP8", fallbackThinking="high"
            var fallbackPattern = pattern;
            ThinkingLevel? fallbackThinking = null;
            if (cliThinking is null)
            {
                var lastColon = JsString.LastIndexOf(pattern, ":");
                if (lastColon != -1)
                {
                    var suffix = pattern[(lastColon + 1)..];
                    if (ThinkingLevels.Parse(suffix) is { } level)
                    {
                        fallbackPattern = pattern[..lastColon];
                        fallbackThinking = level;
                    }
                }
            }

            var fallbackModel = BuildFallbackModel(provider, fallbackPattern, availableModels);
            if (fallbackModel is not null)
            {
                var requestedThinking = cliThinking ?? fallbackThinking;
                var model = requestedThinking is { } requested && requested != ThinkingLevel.Off
                    ? fallbackModel with { Reasoning = true }
                    : fallbackModel;
                var fallbackWarning = parsed.Warning is { } innerWarning
                    ? $"{innerWarning} Model \"{fallbackPattern}\" not found for provider \"{provider}\"."
                        + " Using custom model id."
                    : $"Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id.";
                return new ResolveCliModelResult
                {
                    Model = model,
                    ThinkingLevel = fallbackThinking,
                    Warning = fallbackWarning,
                    Error = null,
                };
            }
        }

        var display = provider is not null ? $"{provider}/{pattern}" : cliModel;
        return new ResolveCliModelResult
        {
            Model = null,
            ThinkingLevel = null,
            Warning = parsed.Warning,
            Error = $"Model \"{display}\" not found. Use --list-models to see available models.",
        };
    }

    // ================================================================= initial model

    /// <summary>
    /// Find the initial model to use based on priority:
    /// <list type="number">
    /// <item>CLI args (provider + model)</item>
    /// <item>First model from scoped models (if not continuing/resuming)</item>
    /// <item>Saved default from settings, if auth is configured</item>
    /// <item>First available model with a valid API key, preferring <see cref="DefaultModelPerProvider"/></item>
    /// <item>Nothing available</item>
    /// </list>
    /// </summary>
    /// <exception cref="ModelResolutionException">
    /// CLI args were given but could not be resolved. TS prints the error and exits; see difference C78.
    /// </exception>
    public static Task<InitialModelResult> FindInitialModelAsync(InitialModelOptions options)
    {
        var cliProvider = options.CliProvider;
        var cliModel = options.CliModel;
        var scopedModels = options.ScopedModels;
        var isContinuing = options.IsContinuing;
        var defaultProvider = options.DefaultProvider;
        var defaultModelId = options.DefaultModelId;
        var defaultThinkingLevel = options.DefaultThinkingLevel;
        var modelThinkingLevels = options.ModelThinkingLevels;
        var runtime = options.Runtime;

        // 1. CLI args take priority
        if (!string.IsNullOrEmpty(cliProvider) && !string.IsNullOrEmpty(cliModel))
        {
            var resolved = ResolveCliModel(new ResolveCliModelOptions
            {
                CliProvider = cliProvider,
                CliModel = cliModel,
                Runtime = runtime,
            });
            if (resolved.Error is { } error)
            {
                ErrorWriter.WriteLine(Chalk.Red(error));

                // TS's `async` function turns this into a rejected promise rather than a synchronous
                // throw, and the port keeps that shape so callers that only observe the returned task
                // still see it (difference C78).
                return Task.FromException<InitialModelResult>(new ModelResolutionException(error));
            }

            if (resolved.Model is not null)
            {
                return Task.FromResult(new InitialModelResult
                {
                    Model = resolved.Model,
                    ThinkingLevel = Defaults.DefaultThinkingLevel,
                    FallbackMessage = null,
                });
            }
        }

        // 2. Use first model from scoped models (skip if continuing/resuming)
        if (scopedModels.Count > 0 && !isContinuing)
        {
            var scopedModel = scopedModels[0];
            var perModel = ReadThinkingLevel(
                modelThinkingLevels, $"{scopedModel.Model.Provider}/{scopedModel.Model.Id}");
            return Task.FromResult(new InitialModelResult
            {
                Model = scopedModel.Model,
                ThinkingLevel = scopedModel.ThinkingLevel
                    ?? perModel
                    ?? defaultThinkingLevel
                    ?? Defaults.DefaultThinkingLevel,
                FallbackMessage = null,
            });
        }

        // 3. Try saved default from settings if auth is configured.
        if (!string.IsNullOrEmpty(defaultProvider) && !string.IsNullOrEmpty(defaultModelId))
        {
            var found = runtime.GetModel(defaultProvider, defaultModelId);
            if (found is not null && runtime.HasConfiguredAuth(found.Provider))
            {
                var thinkingLevel = Defaults.DefaultThinkingLevel;
                var perModel = ReadThinkingLevel(modelThinkingLevels, $"{defaultProvider}/{defaultModelId}");
                if (perModel is { } perModelLevel) thinkingLevel = perModelLevel;
                else if (defaultThinkingLevel is { } defaultLevel) thinkingLevel = defaultLevel;
                return Task.FromResult(new InitialModelResult
                {
                    Model = found,
                    ThinkingLevel = thinkingLevel,
                    FallbackMessage = null,
                });
            }
        }

        // 4. Try first available model with valid API key
        var availableModels = runtime.GetAvailableSnapshot();

        if (availableModels.Count > 0)
        {
            // Try to find a default model from known providers
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    return Task.FromResult(new InitialModelResult
                    {
                        Model = match,
                        ThinkingLevel = Defaults.DefaultThinkingLevel,
                        FallbackMessage = null,
                    });
                }
            }

            // If no default found, use first available
            return Task.FromResult(new InitialModelResult
            {
                Model = availableModels[0],
                ThinkingLevel = Defaults.DefaultThinkingLevel,
                FallbackMessage = null,
            });
        }

        // 5. No model found
        return Task.FromResult(new InitialModelResult
        {
            Model = null,
            ThinkingLevel = Defaults.DefaultThinkingLevel,
            FallbackMessage = null,
        });
    }

    // ================================================================= session restore

    /// <summary>Restore model from session, with fallback to available models. Corresponds to the TS
    /// <c>restoreModelFromSession</c>.</summary>
    public static Task<RestoreModelResult> RestoreModelFromSessionAsync(
        string savedProvider,
        string savedModelId,
        ModelSpec? currentModel,
        bool shouldPrintMessages,
        IModelResolverRuntime runtime)
    {
        var restoredModel = runtime.GetModel(savedProvider, savedModelId);

        // Check if restored model exists and still has auth configured
        var hasConfiguredAuth = restoredModel is not null
            && runtime.HasConfiguredAuth(restoredModel.Provider);

        if (restoredModel is not null && hasConfiguredAuth)
        {
            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Restored model: {savedProvider}/{savedModelId}"));
            }

            return Task.FromResult(new RestoreModelResult { Model = restoredModel, FallbackMessage = null });
        }

        // Model not found or no API key - fall back
        var reason = restoredModel is null ? "model no longer exists" : "no auth configured";

        if (shouldPrintMessages)
        {
            ErrorWriter.WriteLine(
                Chalk.Yellow($"Warning: Could not restore model {savedProvider}/{savedModelId} ({reason})."));
        }

        // If we already have a model, use it as fallback
        if (currentModel is not null)
        {
            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Falling back to: {currentModel.Provider}/{currentModel.Id}"));
            }

            return Task.FromResult(new RestoreModelResult
            {
                Model = currentModel,
                FallbackMessage = $"Could not restore model {savedProvider}/{savedModelId} ({reason})."
                    + $" Using {currentModel.Provider}/{currentModel.Id}.",
            });
        }

        // Try to find any available model
        var availableModels = runtime.GetAvailableSnapshot();

        if (availableModels.Count > 0)
        {
            // Try to find a default model from known providers
            ModelSpec? fallbackModel = null;
            foreach (var (provider, defaultId) in DefaultModelPerProvider)
            {
                var match = availableModels.FirstOrDefault(m => m.Provider == provider && m.Id == defaultId);
                if (match is not null)
                {
                    fallbackModel = match;
                    break;
                }
            }

            // If no default found, use first available
            fallbackModel ??= availableModels[0];

            if (shouldPrintMessages)
            {
                OutWriter.WriteLine(Chalk.Dim($"Falling back to: {fallbackModel.Provider}/{fallbackModel.Id}"));
            }

            return Task.FromResult(new RestoreModelResult
            {
                Model = fallbackModel,
                FallbackMessage = $"Could not restore model {savedProvider}/{savedModelId} ({reason})."
                    + $" Using {fallbackModel.Provider}/{fallbackModel.Id}.",
            });
        }

        // No models available
        return Task.FromResult(new RestoreModelResult { Model = null, FallbackMessage = null });
    }

    // ================================================================= helpers

    private static ThinkingLevel? ReadThinkingLevel(
        IReadOnlyDictionary<string, ThinkingLevel>? levels, string key)
        => levels is not null && levels.TryGetValue(key, out var level) ? level : null;

    /// <summary>
    /// <c>String.prototype.localeCompare</c>. Both Node and .NET use ICU for the current culture, so this
    /// matches (the same conclusion <c>Pi.Tui.Autocomplete</c> reached for its labels). Only the model ids
    /// and <c>"provider/modelId"</c> strings of this module are ever collated.
    /// </summary>
    private static int Collate(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.None);
}
