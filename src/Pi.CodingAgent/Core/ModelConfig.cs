using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>One cost tier of <c>models.json</c>. Corresponds to the TS <c>ModelCostTierSchema</c>.</summary>
public sealed record ModelsJsonCostTier
{
    public required double InputTokensAbove { get; init; }

    public required double Input { get; init; }

    public required double Output { get; init; }

    public required double CacheRead { get; init; }

    public required double CacheWrite { get; init; }
}

/// <summary>
/// Cost block of a model definition or override. The schema requires every rate in a definition and
/// allows any subset in an override, so all rates are nullable here.
/// </summary>
public sealed record ModelsJsonCost
{
    public double? Input { get; init; }

    public double? Output { get; init; }

    public double? CacheRead { get; init; }

    public double? CacheWrite { get; init; }

    public IReadOnlyList<ModelsJsonCostTier>? Tiers { get; init; }
}

/// <summary>A model listed by a provider in <c>models.json</c>. Corresponds to <c>ModelDefinitionSchema</c>.</summary>
public sealed record ModelsJsonModel
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Api { get; init; }

    public string? BaseUrl { get; init; }

    public bool? Reasoning { get; init; }

    /// <summary>Thinking level → provider value (<c>null</c> disables the level). Kept as JSON for the overlay step.</summary>
    public JsonObject? ThinkingLevelMap { get; init; }

    public IReadOnlyList<string>? Input { get; init; }

    public ModelInputLimits? InputLimits { get; init; }

    public ModelsJsonCost? Cost { get; init; }

    public JsonObject? PromptCache { get; init; }

    public double? ContextWindow { get; init; }

    public double? MaxTokens { get; init; }

    public JsonObject? SamplingParams { get; init; }

    public JsonObject? SamplingParamsByThinkingLevel { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public JsonObject? Compat { get; init; }
}

/// <summary>A per-model override. Corresponds to <c>ModelOverrideSchema</c> (no id/api/baseUrl).</summary>
public sealed record ModelsJsonModelOverride
{
    public string? Name { get; init; }

    public bool? Reasoning { get; init; }

    public JsonObject? ThinkingLevelMap { get; init; }

    public IReadOnlyList<string>? Input { get; init; }

    public ModelInputLimits? InputLimits { get; init; }

    public ModelsJsonCost? Cost { get; init; }

    public JsonObject? PromptCache { get; init; }

    public double? ContextWindow { get; init; }

    public double? MaxTokens { get; init; }

    public JsonObject? SamplingParams { get; init; }

    public JsonObject? SamplingParamsByThinkingLevel { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public JsonObject? Compat { get; init; }
}

/// <summary>A provider entry of <c>models.json</c>. Corresponds to <c>ProviderConfigSchema</c>.</summary>
public sealed record ModelsJsonProvider
{
    public string? Name { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? Api { get; init; }

    /// <summary>OAuth flavour; the schema only allows <c>"radius"</c>.</summary>
    public string? OAuth { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>API compatibility overrides (union of the OpenAI/Anthropic shapes), kept as JSON.</summary>
    public JsonObject? Compat { get; init; }

    public bool? AuthHeader { get; init; }

    public IReadOnlyList<ModelsJsonModel>? Models { get; init; }

    public IReadOnlyDictionary<string, ModelsJsonModelOverride>? ModelOverrides { get; init; }
}

/// <summary>
/// Immutable, credential-blind <c>models.json</c> snapshot. Port of the TS <c>ModelConfig</c>.
/// </summary>
/// <remarks>
/// The TS class deep-freezes each provider so callers cannot mutate the snapshot; C# has no freeze, so
/// <see cref="GetProvider"/> hands out the stored instance and callers must treat it as read-only
/// (difference C39). Error text embeds the platform's own I/O message instead of Node's
/// (<c>ENOENT: no such file or directory, open '…'</c>), which was already platform-specific.
/// </remarks>
public sealed class ModelConfig
{
    private readonly Dictionary<string, ModelsJsonProvider> _providers;

    private ModelConfig(Dictionary<string, ModelsJsonProvider> providers, string? error = null)
    {
        _providers = providers;
        Error = error;
    }

    /// <summary>Why the config could not be loaded, or <c>null</c> when it loaded (or is absent).</summary>
    public string? Error { get; }

    /// <summary>Load <c>models.json</c>. A missing file and an unset path both yield an empty config.</summary>
    public static async Task<ModelConfig> LoadAsync(string? modelsJsonPath)
    {
        if (string.IsNullOrEmpty(modelsJsonPath)) return new ModelConfig(new Dictionary<string, ModelsJsonProvider>());
        var path = Paths.NormalizePath(modelsJsonPath);

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new ModelConfig(new Dictionary<string, ModelsJsonProvider>());
        }
        catch (DirectoryNotFoundException)
        {
            return new ModelConfig(new Dictionary<string, ModelsJsonProvider>());
        }
        catch (Exception error)
        {
            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Failed to load models.json: {error.Message}\n\nFile: {path}");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Json.StripJsonComments(Text.StripBom(content)));
        }
        catch (Exception error)
        {
            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Failed to parse models.json: {error.Message}\n\nFile: {path}");
        }

        if (!ModelsConfigSchema.Check(parsed, out var validationErrors))
        {
            var formatted = validationErrors.Count == 0
                ? "Unknown schema error"
                : string.Join('\n', validationErrors.Select(error =>
                    $"  - {FormatValidationPath(error)}: {error.Message}"));
            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Invalid models.json schema:\n{formatted}\n\nFile: {path}");
        }

        var providers = new Dictionary<string, ModelsJsonProvider>(StringComparer.Ordinal);
        // TS: `Object.entries(config.providers)` — the provider map is nested under the root key.
        if (parsed is JsonObject root && root["providers"] is JsonObject providerMap)
        {
            foreach (var (providerId, provider) in providerMap)
            {
                if (provider is not JsonObject providerObject) continue;
                providers[providerId] = ParseProvider(providerObject);
            }
        }

        return new ModelConfig(providers);
    }

    public ModelsJsonProvider? GetProvider(string providerId) => _providers.GetValueOrDefault(providerId);

    public IReadOnlyList<string> GetProviderIds() => _providers.Keys.ToList();

    /// <summary>TypeBox-style path formatting for one validation error.</summary>
    internal static string FormatValidationPath(JsonSchemaError error)
    {
        if (error.Keyword == "required" && error.RequiredProperties is { Count: > 0 } required)
        {
            var requiredProperty = required[0];
            var basePath = ToDottedPath(error.InstancePath);
            return basePath.Length > 0 ? $"{basePath}.{requiredProperty}" : requiredProperty;
        }

        var path = ToDottedPath(error.InstancePath);
        return path.Length > 0 ? path : "root";
    }

    /// <summary>TS: <c>instancePath.replace(/^\//, "").replace(/\//g, ".")</c>.</summary>
    private static string ToDottedPath(string instancePath)
    {
        var trimmed = instancePath.StartsWith('/') ? instancePath[1..] : instancePath;
        return trimmed.Replace('/', '.');
    }

    // ---------------------------------------------------------------- schema

    private static readonly JsonSchema PercentileCutoffsSchema = JsonSchemaBuilder.Obj(
        ("p50", JsonSchemaBuilder.Num(), true),
        ("p75", JsonSchemaBuilder.Num(), true),
        ("p90", JsonSchemaBuilder.Num(), true),
        ("p99", JsonSchemaBuilder.Num(), true));

    private static readonly JsonSchema OpenRouterRoutingSchema = JsonSchemaBuilder.Obj(
        ("allow_fallbacks", JsonSchemaBuilder.Bool, true),
        ("require_parameters", JsonSchemaBuilder.Bool, true),
        ("data_collection", JsonSchemaBuilder.Union(JsonSchemaBuilder.Lit("deny"), JsonSchemaBuilder.Lit("allow")), true),
        ("zdr", JsonSchemaBuilder.Bool, true),
        ("enforce_distillable_text", JsonSchemaBuilder.Bool, true),
        ("order", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true),
        ("only", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true),
        ("ignore", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true),
        ("quantizations", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true),
        ("sort", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Str(),
            JsonSchemaBuilder.Obj(
                ("by", JsonSchemaBuilder.Str(), true),
                ("partition", JsonSchemaBuilder.Union(JsonSchemaBuilder.Str(), JsonSchemaBuilder.Null), true))), true),
        ("max_price", JsonSchemaBuilder.Obj(
            ("prompt", JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), JsonSchemaBuilder.Str()), true),
            ("completion", JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), JsonSchemaBuilder.Str()), true),
            ("image", JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), JsonSchemaBuilder.Str()), true),
            ("audio", JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), JsonSchemaBuilder.Str()), true),
            ("request", JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), JsonSchemaBuilder.Str()), true)), true),
        ("preferred_min_throughput",
            JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), PercentileCutoffsSchema), true),
        ("preferred_max_latency",
            JsonSchemaBuilder.Union(JsonSchemaBuilder.Num(), PercentileCutoffsSchema), true));

    private static readonly JsonSchema VercelGatewayRoutingSchema = JsonSchemaBuilder.Obj(
        ("only", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true),
        ("order", JsonSchemaBuilder.Arr(JsonSchemaBuilder.Str()), true));

    private static readonly JsonSchema ThinkingLevelMapValueSchema =
        JsonSchemaBuilder.Union(JsonSchemaBuilder.Str(), JsonSchemaBuilder.Null);

    private static readonly JsonSchema ThinkingLevelMapSchema = JsonSchemaBuilder.Obj(
        ("off", ThinkingLevelMapValueSchema, true),
        ("minimal", ThinkingLevelMapValueSchema, true),
        ("low", ThinkingLevelMapValueSchema, true),
        ("medium", ThinkingLevelMapValueSchema, true),
        ("high", ThinkingLevelMapValueSchema, true),
        ("xhigh", ThinkingLevelMapValueSchema, true),
        ("max", ThinkingLevelMapValueSchema, true));

    private static readonly JsonSchema SamplingParamsSchema = JsonSchemaBuilder.Rec(JsonSchemaBuilder.Any);

    private static readonly JsonSchema SamplingParamsByThinkingLevelSchema = JsonSchemaBuilder.Obj(
        ("off", SamplingParamsSchema, true),
        ("minimal", SamplingParamsSchema, true),
        ("low", SamplingParamsSchema, true),
        ("medium", SamplingParamsSchema, true),
        ("high", SamplingParamsSchema, true),
        ("xhigh", SamplingParamsSchema, true),
        ("max", SamplingParamsSchema, true));

    private static readonly JsonSchema ChatTemplateKwargScalarSchema = JsonSchemaBuilder.Union(
        JsonSchemaBuilder.Str(), JsonSchemaBuilder.Num(), JsonSchemaBuilder.Bool, JsonSchemaBuilder.Null);

    private static readonly JsonSchema ChatTemplateKwargVariableSchema = JsonSchemaBuilder.Obj(
        ("$var", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Lit("thinking.enabled"), JsonSchemaBuilder.Lit("thinking.effort")), false),
        ("omitWhenOff", JsonSchemaBuilder.Bool, true));

    private static readonly JsonSchema ChatTemplateKwargSchema = JsonSchemaBuilder.Union(
        ChatTemplateKwargScalarSchema, ChatTemplateKwargVariableSchema);

    private static readonly JsonSchema OpenAiCompletionsCompatSchema = JsonSchemaBuilder.Obj(
        ("supportsStore", JsonSchemaBuilder.Bool, true),
        ("supportsDeveloperRole", JsonSchemaBuilder.Bool, true),
        ("supportsReasoningEffort", JsonSchemaBuilder.Bool, true),
        ("supportsUsageInStreaming", JsonSchemaBuilder.Bool, true),
        ("supportsFinishReason", JsonSchemaBuilder.Bool, true),
        ("maxTokensField", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Lit("max_completion_tokens"), JsonSchemaBuilder.Lit("max_tokens")), true),
        ("requiresToolResultName", JsonSchemaBuilder.Bool, true),
        ("requiresAssistantAfterToolResult", JsonSchemaBuilder.Bool, true),
        ("requiresThinkingAsText", JsonSchemaBuilder.Bool, true),
        ("requiresReasoningContentOnAssistantMessages", JsonSchemaBuilder.Bool, true),
        ("thinkingFormat", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Lit("openai"),
            JsonSchemaBuilder.Lit("openrouter"),
            JsonSchemaBuilder.Lit("together"),
            JsonSchemaBuilder.Lit("baseten"),
            JsonSchemaBuilder.Lit("deepseek"),
            JsonSchemaBuilder.Lit("zai"),
            JsonSchemaBuilder.Lit("qwen"),
            JsonSchemaBuilder.Lit("chat-template"),
            JsonSchemaBuilder.Lit("qwen-chat-template"),
            JsonSchemaBuilder.Lit("string-thinking"),
            JsonSchemaBuilder.Lit("ant-ling")), true),
        ("chatTemplateKwargs", JsonSchemaBuilder.Rec(ChatTemplateKwargSchema), true),
        ("chatTemplateArgs", JsonSchemaBuilder.Rec(ChatTemplateKwargSchema), true),
        ("cacheControlFormat", JsonSchemaBuilder.Lit("anthropic"), true),
        ("openRouterRouting", OpenRouterRoutingSchema, true),
        ("vercelGatewayRouting", VercelGatewayRoutingSchema, true),
        ("supportsOpenAIGrammarTools", JsonSchemaBuilder.Bool, true),
        ("supportsStrictMode", JsonSchemaBuilder.Bool, true),
        ("sendSessionAffinityHeaders", JsonSchemaBuilder.Bool, true),
        ("sessionAffinityFormat", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Lit("openai"),
            JsonSchemaBuilder.Lit("openai-nosession"),
            JsonSchemaBuilder.Lit("openrouter")), true),
        ("supportsLongCacheRetention", JsonSchemaBuilder.Bool, true),
        ("vllmPriority", JsonSchemaBuilder.Num(), true));

    private static readonly JsonSchema OpenAiResponsesCompatSchema = JsonSchemaBuilder.Obj(
        ("supportsDeveloperRole", JsonSchemaBuilder.Bool, true),
        ("sessionAffinityFormat", JsonSchemaBuilder.Union(
            JsonSchemaBuilder.Lit("openai"),
            JsonSchemaBuilder.Lit("openai-nosession"),
            JsonSchemaBuilder.Lit("openrouter")), true),
        ("supportsLongCacheRetention", JsonSchemaBuilder.Bool, true),
        ("supportsStrictMode", JsonSchemaBuilder.Bool, true),
        ("supportsOpenAIGrammarTools", JsonSchemaBuilder.Bool, true),
        ("supportsMaxOutputTokens", JsonSchemaBuilder.Bool, true));

    private static readonly JsonSchema ModelCostTierSchema = JsonSchemaBuilder.Obj(
        ("inputTokensAbove", JsonSchemaBuilder.Num(), false),
        ("input", JsonSchemaBuilder.Num(), false),
        ("output", JsonSchemaBuilder.Num(), false),
        ("cacheRead", JsonSchemaBuilder.Num(), false),
        ("cacheWrite", JsonSchemaBuilder.Num(), false));

    private static readonly JsonSchema ModelCostSchema = JsonSchemaBuilder.Obj(
        ("input", JsonSchemaBuilder.Num(), false),
        ("output", JsonSchemaBuilder.Num(), false),
        ("cacheRead", JsonSchemaBuilder.Num(), false),
        ("cacheWrite", JsonSchemaBuilder.Num(), false),
        ("tiers", JsonSchemaBuilder.Arr(ModelCostTierSchema), true));

    private static readonly JsonSchema ModelCostOverrideSchema = JsonSchemaBuilder.Obj(
        ("input", JsonSchemaBuilder.Num(), true),
        ("output", JsonSchemaBuilder.Num(), true),
        ("cacheRead", JsonSchemaBuilder.Num(), true),
        ("cacheWrite", JsonSchemaBuilder.Num(), true),
        ("tiers", JsonSchemaBuilder.Arr(ModelCostTierSchema), true));

    private static readonly JsonSchema ModelPromptCacheSchema = JsonSchemaBuilder.Obj(
        ("short", JsonSchemaBuilder.Num(exclusiveMinimum: 0), true),
        ("long", JsonSchemaBuilder.Num(exclusiveMinimum: 0), true));

    private static readonly JsonSchema ImageResizeSchema = JsonSchemaBuilder.Obj(
        ("maxWidth", JsonSchemaBuilder.Int(minimum: 1), true),
        ("maxHeight", JsonSchemaBuilder.Int(minimum: 1), true),
        ("maxBytes", JsonSchemaBuilder.Int(minimum: 1), true),
        ("jpegQuality", JsonSchemaBuilder.Int(minimum: 1, maximum: 100), true));

    private static readonly JsonSchema ModelInputLimitsSchema = JsonSchemaBuilder.Obj(
        ("maxRequestBytes", JsonSchemaBuilder.Int(minimum: 1), true),
        ("images", JsonSchemaBuilder.Obj(
            ("resize", ImageResizeSchema, true),
            ("maxPerMessage", JsonSchemaBuilder.Int(minimum: 1), true),
            ("maxPerRequest", JsonSchemaBuilder.Int(minimum: 1), true)), true));

    private static readonly JsonSchema AnthropicMessagesCompatSchema = JsonSchemaBuilder.Obj(
        ("supportsEagerToolInputStreaming", JsonSchemaBuilder.Bool, true),
        ("supportsLongCacheRetention", JsonSchemaBuilder.Bool, true),
        ("sendSessionAffinityHeaders", JsonSchemaBuilder.Bool, true),
        ("supportsCacheControlOnTools", JsonSchemaBuilder.Bool, true),
        ("supportsTemperature", JsonSchemaBuilder.Bool, true),
        ("forceAdaptiveThinking", JsonSchemaBuilder.Bool, true),
        ("allowEmptySignature", JsonSchemaBuilder.Bool, true),
        ("supportsStrictTools", JsonSchemaBuilder.Bool, true),
        ("supportsMidConvoEffort", JsonSchemaBuilder.Bool, true),
        ("allowedFallbackModels", JsonSchemaBuilder.Arr(
            JsonSchemaBuilder.Obj(
                ("provider", JsonSchemaBuilder.Str(minLength: 1), false),
                ("model", JsonSchemaBuilder.Str(minLength: 1), false),
                ("cost", ModelCostSchema, false)),
            maxItems: 3), true));

    private static readonly JsonSchema ProviderCompatSchema = JsonSchemaBuilder.Union(
        OpenAiCompletionsCompatSchema, OpenAiResponsesCompatSchema, AnthropicMessagesCompatSchema);

    private static readonly JsonSchema ModelDefinitionSchema = JsonSchemaBuilder.Obj(
        ("id", JsonSchemaBuilder.Str(minLength: 1), false),
        ("name", JsonSchemaBuilder.Str(minLength: 1), true),
        ("api", JsonSchemaBuilder.Str(minLength: 1), true),
        ("baseUrl", JsonSchemaBuilder.Str(minLength: 1), true),
        ("reasoning", JsonSchemaBuilder.Bool, true),
        ("thinkingLevelMap", ThinkingLevelMapSchema, true),
        ("input", JsonSchemaBuilder.Arr(
            JsonSchemaBuilder.Union(JsonSchemaBuilder.Lit("text"), JsonSchemaBuilder.Lit("image"))), true),
        ("inputLimits", ModelInputLimitsSchema, true),
        ("cost", ModelCostSchema, true),
        ("promptCache", ModelPromptCacheSchema, true),
        ("contextWindow", JsonSchemaBuilder.Num(), true),
        ("maxTokens", JsonSchemaBuilder.Num(), true),
        ("samplingParams", SamplingParamsSchema, true),
        ("samplingParamsByThinkingLevel", SamplingParamsByThinkingLevelSchema, true),
        ("headers", JsonSchemaBuilder.Rec(JsonSchemaBuilder.Str()), true),
        ("compat", ProviderCompatSchema, true));

    private static readonly JsonSchema ModelOverrideSchema = JsonSchemaBuilder.Obj(
        ("name", JsonSchemaBuilder.Str(minLength: 1), true),
        ("reasoning", JsonSchemaBuilder.Bool, true),
        ("thinkingLevelMap", ThinkingLevelMapSchema, true),
        ("input", JsonSchemaBuilder.Arr(
            JsonSchemaBuilder.Union(JsonSchemaBuilder.Lit("text"), JsonSchemaBuilder.Lit("image"))), true),
        ("inputLimits", ModelInputLimitsSchema, true),
        ("cost", ModelCostOverrideSchema, true),
        ("promptCache", ModelPromptCacheSchema, true),
        ("contextWindow", JsonSchemaBuilder.Num(), true),
        ("maxTokens", JsonSchemaBuilder.Num(), true),
        ("samplingParams", SamplingParamsSchema, true),
        ("samplingParamsByThinkingLevel", SamplingParamsByThinkingLevelSchema, true),
        ("headers", JsonSchemaBuilder.Rec(JsonSchemaBuilder.Str()), true),
        ("compat", ProviderCompatSchema, true));

    private static readonly JsonSchema ProviderConfigSchema = JsonSchemaBuilder.Obj(
        ("name", JsonSchemaBuilder.Str(minLength: 1), true),
        ("baseUrl", JsonSchemaBuilder.Str(minLength: 1), true),
        ("apiKey", JsonSchemaBuilder.Str(minLength: 1), true),
        ("api", JsonSchemaBuilder.Str(minLength: 1), true),
        ("oauth", JsonSchemaBuilder.Lit("radius"), true),
        ("headers", JsonSchemaBuilder.Rec(JsonSchemaBuilder.Str()), true),
        ("compat", ProviderCompatSchema, true),
        ("authHeader", JsonSchemaBuilder.Bool, true),
        ("models", JsonSchemaBuilder.Arr(ModelDefinitionSchema), true),
        ("modelOverrides", JsonSchemaBuilder.Rec(ModelOverrideSchema), true));

    /// <summary>The compiled <c>ModelsConfigSchema</c> (TypeBox <c>Compile(ModelsConfigSchema)</c>).</summary>
    private static readonly JsonSchema ModelsConfigSchema = JsonSchemaBuilder.Obj(
        ("providers", JsonSchemaBuilder.Rec(ProviderConfigSchema), false));

    // ------------------------------------------------------------ conversion

    private static ModelsJsonProvider ParseProvider(JsonObject value) => new()
    {
        Name = ReadString(value, "name"),
        BaseUrl = ReadString(value, "baseUrl"),
        ApiKey = ReadString(value, "apiKey"),
        Api = ReadString(value, "api"),
        OAuth = ReadString(value, "oauth"),
        Headers = ReadStringMap(value, "headers"),
        Compat = value["compat"] as JsonObject,
        AuthHeader = ReadBool(value, "authHeader"),
        Models = ReadArray(value, "models", static model => ParseModel(model)),
        ModelOverrides = ReadObject(value, "modelOverrides", static (_, model) => ParseOverride(model)),
    };

    private static ModelsJsonModel ParseModel(JsonObject value) => new()
    {
        Id = ReadString(value, "id") ?? string.Empty,
        Name = ReadString(value, "name"),
        Api = ReadString(value, "api"),
        BaseUrl = ReadString(value, "baseUrl"),
        Reasoning = ReadBool(value, "reasoning"),
        ThinkingLevelMap = value["thinkingLevelMap"] as JsonObject,
        Input = ReadStringArray(value, "input"),
        InputLimits = value["inputLimits"] is JsonObject limits ? ModelCatalog.ParseInputLimits(limits) : null,
        Cost = ParseCost(value["cost"] as JsonObject),
        PromptCache = value["promptCache"] as JsonObject,
        ContextWindow = ReadDouble(value, "contextWindow"),
        MaxTokens = ReadDouble(value, "maxTokens"),
        SamplingParams = value["samplingParams"] as JsonObject,
        SamplingParamsByThinkingLevel = value["samplingParamsByThinkingLevel"] as JsonObject,
        Headers = ReadStringMap(value, "headers"),
        Compat = value["compat"] as JsonObject,
    };

    private static ModelsJsonModelOverride ParseOverride(JsonObject value) => new()
    {
        Name = ReadString(value, "name"),
        Reasoning = ReadBool(value, "reasoning"),
        ThinkingLevelMap = value["thinkingLevelMap"] as JsonObject,
        Input = ReadStringArray(value, "input"),
        InputLimits = value["inputLimits"] is JsonObject limits ? ModelCatalog.ParseInputLimits(limits) : null,
        Cost = ParseCost(value["cost"] as JsonObject),
        PromptCache = value["promptCache"] as JsonObject,
        ContextWindow = ReadDouble(value, "contextWindow"),
        MaxTokens = ReadDouble(value, "maxTokens"),
        SamplingParams = value["samplingParams"] as JsonObject,
        SamplingParamsByThinkingLevel = value["samplingParamsByThinkingLevel"] as JsonObject,
        Headers = ReadStringMap(value, "headers"),
        Compat = value["compat"] as JsonObject,
    };

    private static ModelsJsonCost? ParseCost(JsonObject? value)
    {
        if (value is null) return null;
        IReadOnlyList<ModelsJsonCostTier>? tiers = null;
        if (value["tiers"] is JsonArray tierArray)
        {
            tiers = tierArray.OfType<JsonObject>().Select(tier => new ModelsJsonCostTier
            {
                InputTokensAbove = ReadDouble(tier, "inputTokensAbove") ?? 0,
                Input = ReadDouble(tier, "input") ?? 0,
                Output = ReadDouble(tier, "output") ?? 0,
                CacheRead = ReadDouble(tier, "cacheRead") ?? 0,
                CacheWrite = ReadDouble(tier, "cacheWrite") ?? 0,
            }).ToList();
        }

        return new ModelsJsonCost
        {
            Input = ReadDouble(value, "input"),
            Output = ReadDouble(value, "output"),
            CacheRead = ReadDouble(value, "cacheRead"),
            CacheWrite = ReadDouble(value, "cacheWrite"),
            Tiers = tiers,
        };
    }

    private static string? ReadString(JsonObject owner, string key)
        => owner[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? ReadDouble(JsonObject owner, string key)
        => owner[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static bool? ReadBool(JsonObject owner, string key)
        => owner[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonObject owner, string key)
        => owner[key] is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(value => value.TryGetValue<string>(out var text) ? text : null)
                .Where(text => text is not null)
                .Select(text => text!)
                .ToList()
            : null;

    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonObject owner, string key)
    {
        if (owner[key] is not JsonObject map) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in map)
        {
            if (value is JsonValue entry && entry.TryGetValue<string>(out var text)) result[name] = text;
        }
        return result;
    }

    private static IReadOnlyList<T>? ReadArray<T>(JsonObject owner, string key, Func<JsonObject, T> convert)
        => owner[key] is JsonArray array ? array.OfType<JsonObject>().Select(convert).ToList() : null;

    private static IReadOnlyDictionary<string, T>? ReadObject<T>(
        JsonObject owner, string key, Func<string, JsonObject, T> convert)
    {
        if (owner[key] is not JsonObject map) return null;
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in map)
        {
            if (value is JsonObject entry) result[name] = convert(name, entry);
        }
        return result;
    }
}
