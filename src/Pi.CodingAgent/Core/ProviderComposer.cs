using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 组合内置 provider、<c>models.json</c> 与扩展三层，产出一个不读凭据的 <see cref="IProvider"/>。
/// 对应 TS <c>core/provider-composer.ts</c>（732 行）。
/// </summary>
/// <remarks>
/// <para><b>叠加顺序</b>（自下而上）：内置目录 → <c>models.json</c>（baseUrl/compat 覆盖 + 自定义模型
/// upsert）→ 扩展（模型整体替换或 baseUrl 覆盖）→ 扩展 OAuth 的 <c>modifyModels</c>（chat-only）→
/// <c>models.json</c> 的顶层 <c>modelOverrides</c>（chat-only，最后应用一次）。</para>
/// <para>TS 侧所有辅助函数都未导出，没有可复刻的单元测试；本移植的测试针对
/// <see cref="ComposeModelProvider"/> 写端到端行为断言。</para>
/// <para>已记录的差异：C50（扩展 OAuth 登录面直接复用 <see cref="ProviderAuthInteraction"/>）、
/// C51（<c>ApiKeyAuth.check</c> 未移植——C# <see cref="IApiKeyAuth"/> 无该成员，可用性一律经 resolve
/// 判定）、C52（api 注册表回退统一走 <c>StreamSimple</c>）、C53（<c>Provider.headers</c> 未移植）、
/// C54（能力接口恒实现，缺失时返回 TS <c>Models</c> 层会产出的同一 error 结果）。</para>
/// </remarks>
public static class ProviderComposer
{
    /// <summary>TS <c>clearApiKeyCache = clearConfigValueCache</c>。</summary>
    public static void ClearApiKeyCache() => ConfigValueResolver.ClearConfigValueCache();

    /// <summary><c>compat</c> 里需要按键做嵌套浅合并的四个字段。</summary>
    private static readonly IReadOnlyList<string> NestedCompatKeys =
        ["openRouterRouting", "vercelGatewayRouting", "chatTemplateKwargs", "chatTemplateArgs"];

    /// <summary>TS <c>compat.ts</c> 里 api 注册表的 API id。</summary>
    private const string OpenAiCompletionsApi = "openai-completions";

    // ================================================================= register / compose

    /// <summary>
    /// 校验一次扩展声明是否可组合（注册/重载时立即报结构性错误）。
    /// 对应 TS <c>validateExtensionProvider</c>。
    /// </summary>
    public static void ValidateExtensionProvider(
        string providerId,
        IProvider? baseProvider,
        ModelsJsonProvider? modelsConfig,
        ProviderConfigInput extension)
    {
        if (extension.StreamSimple is not null && string.IsNullOrEmpty(extension.Api))
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: \"api\" is required when registering streamSimple.");
        }

        ApplyExtension(
            providerId,
            ApplyModelsJson(providerId, GetAllProviderModels(baseProvider), modelsConfig),
            extension);
    }

    /// <summary>
    /// 组合三层并返回 provider。对应 TS <c>composeModelProvider</c>。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 结构性错误（缺 api/baseUrl、无效 contextWindow、无认证方式等）——与 TS 一致地在注册时立即抛出。
    /// </exception>
    public static IProvider ComposeModelProvider(
        string providerId,
        IProvider? baseProvider,
        ModelConfig modelConfig,
        ProviderConfigInput? extension)
    {
        var config = modelConfig.GetProvider(providerId);
        var apiKey = ComposeApiKeyAuth(providerId, baseProvider, config, extension);
        var oauth = ComposeOAuthAuth(providerId, baseProvider, config, extension);

        var name = extension?.Name
            ?? config?.Name
            ?? baseProvider?.Name
            ?? extension?.OAuth?.Name
            ?? providerId;
        var baseUrl = extension?.BaseUrl ?? config?.BaseUrl ?? baseProvider?.BaseUrl;

        // 构造器里先做一次 eager 校验（对齐 TS：getAllModels() 在认证检查之前跑），
        // 让结构性错误优先于「没有认证方式」报出。
        var provider = new ComposedProvider(
            providerId, name, baseUrl, apiKey, oauth, baseProvider, config, extension);

        if (apiKey is null && oauth is null)
        {
            // 与 TS 同形的兜底：composeApiKeyAuth 只在「OAuth-only」时返回 null，而那时
            // composeOAuthAuth 必然非 null，因此这条分支实际不可达（TS 亦然）。
            throw new InvalidOperationException($"Provider {providerId}: no authentication method configured.");
        }

        return provider;
    }

    // ============================================================== configured headers

    /// <summary>
    /// 解析某个模型最终生效的请求头。对应 TS <c>resolveConfiguredModelHeaders</c>。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ResolveConfiguredModelHeaders(
        ModelSpec model,
        ModelsJsonProvider? config,
        ProviderConfigInput? extension,
        IReadOnlyDictionary<string, string>? env = null)
        => ConfigValueResolver.ResolveHeadersOrThrow(
            RawModelHeaders(model, config, extension),
            $"model \"{model.Provider}/{model.Id}\"",
            env);

    /// <summary>
    /// 模型请求的兼容配置（头 + 是否写 Authorization）。对应 TS <c>CompatibilityRequestConfig</c>。
    /// </summary>
    public sealed record CompatibilityRequestConfig
    {
        /// <summary>合并后的请求头；调用方值覆盖模型自带值。</summary>
        public IReadOnlyDictionary<string, string?>? Headers { get; init; }

        public required bool AuthHeader { get; init; }
    }

    /// <summary>对应 TS <c>resolveCompatibilityRequestConfig</c>。</summary>
    public static CompatibilityRequestConfig ResolveCompatibilityRequestConfig(
        ModelSpec model,
        ModelsJsonProvider? config,
        ProviderConfigInput? extension)
    {
        var configured = ConfigValueResolver.ResolveHeadersOrThrow(
            MergeStringMaps(ConfiguredHeaders(config, extension), RawModelHeaders(model, config, extension)),
            $"model \"{model.Provider}/{model.Id}\"");

        // TS：`model.headers || configured`——空对象也算「有」（truthy），所以只看是否为 null。
        Dictionary<string, string?>? headers = null;
        if (model.Headers is not null || configured is not null)
        {
            headers = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (model.Headers is not null)
            {
                foreach (var (name, value) in model.Headers) headers[name] = value;
            }
            if (configured is not null)
            {
                foreach (var (name, value) in configured) headers[name] = value;
            }
        }

        return new CompatibilityRequestConfig
        {
            Headers = headers,
            AuthHeader = extension?.AuthHeader ?? config?.AuthHeader ?? false,
        };
    }

    /// <summary>
    /// 由配置推导的请求认证状态（不读凭据存储）。对应 TS <c>configuredRequestAuthStatus</c>。
    /// </summary>
    public static AuthStatus? ConfiguredRequestAuthStatus(
        ModelsJsonProvider? config,
        ProviderConfigInput? extension)
    {
        var value = ConfiguredApiKey(config, extension);
        if (value is null) return null;

        if (ConfigValueResolver.IsCommandConfigValue(value))
        {
            return new AuthStatus { Configured = true, Source = AuthStatusSource.ModelsJsonCommand };
        }

        var names = ConfigValueResolver.GetConfigValueEnvVarNames(value);
        if (names.Count > 0)
        {
            return ConfigValueResolver.IsConfigValueConfigured(value)
                ? new AuthStatus
                {
                    Configured = true,
                    Source = AuthStatusSource.Environment,
                    Label = string.Join(", ", names),
                }
                : new AuthStatus { Configured = false };
        }

        return new AuthStatus
        {
            Configured = true,
            Source = extension?.ApiKey is not null ? AuthStatusSource.Fallback : AuthStatusSource.ModelsJsonKey,
        };
    }

    // ================================================================ model layer stacking

    /// <summary>TS <c>getAllProviderModels</c>：优先 <c>getAllModels()</c>，缺失则回落 <c>getModels()</c>。</summary>
    private static IReadOnlyList<ModelSpec> GetAllProviderModels(IProvider? provider)
        => provider is null ? [] : provider.GetAllModels();

    /// <summary>
    /// 浅合并两份 <c>compat</c>：四个嵌套键按对象合并，其余键覆盖。
    /// 对应 TS <c>mergeCompat</c>。
    /// </summary>
    private static JsonObject? MergeCompat(JsonObject? baseCompat, JsonObject? overrideCompat)
    {
        if (overrideCompat is null) return baseCompat;

        var merged = new JsonObject();
        if (baseCompat is not null)
        {
            foreach (var (key, value) in baseCompat) merged[key] = value?.DeepClone();
        }
        foreach (var (key, value) in overrideCompat) merged[key] = value?.DeepClone();

        foreach (var key in NestedCompatKeys)
        {
            var baseNested = baseCompat?[key] as JsonObject;
            var overrideNested = overrideCompat[key] as JsonObject;
            if (baseNested is null && overrideNested is null) continue;
            merged[key] = MergeJsonObjects(baseNested, overrideNested);
        }

        return merged;
    }

    /// <summary>浅合并两份输入限制（<c>images.resize</c> 再深一层）。对应 TS <c>mergeInputLimits</c>。</summary>
    private static ModelInputLimits? MergeInputLimits(ModelInputLimits? baseLimits, ModelInputLimits? overrideLimits)
    {
        if (overrideLimits is null) return baseLimits;

        ModelImageInputLimits? images;
        if (overrideLimits.Images is { } overrideImages)
        {
            var baseImages = baseLimits?.Images;
            images = new ModelImageInputLimits
            {
                Resize = overrideImages.Resize is { } overrideResize
                    ? MergeImageResize(baseImages?.Resize, overrideResize)
                    : baseImages?.Resize,
                MaxPerMessage = overrideImages.MaxPerMessage ?? baseImages?.MaxPerMessage,
                MaxPerRequest = overrideImages.MaxPerRequest ?? baseImages?.MaxPerRequest,
            };
        }
        else
        {
            images = baseLimits?.Images;
        }

        return new ModelInputLimits
        {
            MaxRequestBytes = overrideLimits.MaxRequestBytes ?? baseLimits?.MaxRequestBytes,
            Images = images,
        };
    }

    private static ModelImageResizeOptions MergeImageResize(
        ModelImageResizeOptions? baseResize, ModelImageResizeOptions overrideResize)
        => new()
        {
            MaxWidth = overrideResize.MaxWidth ?? baseResize?.MaxWidth,
            MaxHeight = overrideResize.MaxHeight ?? baseResize?.MaxHeight,
            MaxBytes = overrideResize.MaxBytes ?? baseResize?.MaxBytes,
            JpegQuality = overrideResize.JpegQuality ?? baseResize?.JpegQuality,
        };

    /// <summary>
    /// 按思考档位合并采样参数：仅覆盖显式出现的档位，其余保留基值。
    /// 对应 TS <c>mergeSamplingParamsByThinkingLevel</c>。
    /// </summary>
    private static IReadOnlyDictionary<string, JsonObject>? MergeSamplingParamsByThinkingLevel(
        IReadOnlyDictionary<string, JsonObject>? baseParams,
        IReadOnlyDictionary<string, JsonObject>? overrideParams)
    {
        if (overrideParams is null) return baseParams;

        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (baseParams is not null)
        {
            foreach (var (level, parameters) in baseParams) merged[level] = (JsonObject)parameters.DeepClone();
        }

        foreach (var level in Pi.Ai.Models.ThinkingLevels.ExtendedThinkingLevels)
        {
            if (!overrideParams.TryGetValue(level, out var parameters) || parameters is null) continue;
            merged[level] = MergeJsonObjects(
                baseParams is not null && baseParams.TryGetValue(level, out var baseEntry) ? baseEntry : null,
                parameters);
        }

        return merged;
    }

    /// <summary>
    /// 应用 <c>models.json</c> 的顶层 <c>modelOverrides</c>。对应 TS <c>applyModelOverride</c>。
    /// </summary>
    /// <remarks>
    /// <c>promptCache</c> 在 <see cref="ModelSpec"/> 里没有具名字段，沿用本移植的约定经
    /// <see cref="ModelSpec.Extra"/> 承载（见 <c>ModelSpecJson</c> 的说明）。
    /// </remarks>
    private static ModelSpec ApplyModelOverride(ModelSpec model, ModelsJsonModelOverride overrides)
    {
        var result = model with
        {
            Name = overrides.Name ?? model.Name,
            Reasoning = overrides.Reasoning ?? model.Reasoning,
            ThinkingLevelMap = overrides.ThinkingLevelMap is { } levelMap
                ? MergeThinkingLevelMap(model.ThinkingLevelMap, levelMap)
                : model.ThinkingLevelMap,
            Input = overrides.Input ?? model.Input,
            InputLimits = MergeInputLimits(model.InputLimits, overrides.InputLimits),
            Cost = overrides.Cost is { } cost
                ? new ModelCostRates(
                    cost.Input ?? model.Cost?.Input ?? 0,
                    cost.Output ?? model.Cost?.Output ?? 0,
                    cost.CacheRead ?? model.Cost?.CacheRead,
                    cost.CacheWrite ?? model.Cost?.CacheWrite)
                : model.Cost,
            ContextWindow = overrides.ContextWindow is { } contextWindow ? (long)contextWindow : model.ContextWindow,
            MaxTokens = overrides.MaxTokens is { } maxTokens ? (long)maxTokens : model.MaxTokens,
            SamplingParams = overrides.SamplingParams is { } samplingParams
                ? MergeJsonObjects(model.SamplingParams, samplingParams)
                : model.SamplingParams,
            SamplingParamsByThinkingLevel = MergeSamplingParamsByThinkingLevel(
                model.SamplingParamsByThinkingLevel, ToSamplingParamsByThinkingLevel(overrides.SamplingParamsByThinkingLevel)),
            Compat = MergeCompat(model.Compat, overrides.Compat),
        };

        return overrides.PromptCache is { } promptCache
            ? SetExtraValue(result, "promptCache",
                MergeJsonObjects(ExtraObject(result, "promptCache"), promptCache))
            : result;
    }

    /// <summary>把 <c>models.json</c> 的一条模型定义转成 chat 模型。对应 TS <c>modelFromJson</c>。</summary>
    private static ModelSpec ModelFromJson(
        string providerId,
        ModelsJsonModel definition,
        ModelsJsonProvider providerConfig,
        ModelSpec? defaults)
    {
        var api = definition.Api ?? providerConfig.Api ?? defaults?.Api;
        if (string.IsNullOrEmpty(api))
        {
            throw new InvalidOperationException(
                $"Provider {providerId}, model {definition.Id}: no \"api\" specified. Set at provider or model level.");
        }

        var baseUrl = definition.BaseUrl ?? providerConfig.BaseUrl ?? defaults?.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
        }

        if (definition.ContextWindow is { } contextWindow && contextWindow <= 0)
        {
            throw new InvalidOperationException($"Provider {providerId}, model {definition.Id}: invalid contextWindow");
        }
        if (definition.MaxTokens is { } maxTokens && maxTokens <= 0)
        {
            throw new InvalidOperationException($"Provider {providerId}, model {definition.Id}: invalid maxTokens");
        }

        var model = new ModelSpec
        {
            Id = definition.Id,
            Name = definition.Name ?? definition.Id,
            Api = api,
            Provider = providerId,
            BaseUrl = baseUrl,
            Type = ModelType.Chat,
            Reasoning = definition.Reasoning ?? false,
            ThinkingLevelMap = definition.ThinkingLevelMap is { } levelMap
                ? ThinkingLevelMap.FromJsonObject(levelMap)
                : null,
            Input = definition.Input ?? [ModelInput.Text],
            InputLimits = definition.InputLimits,
            Cost = definition.Cost is { } cost
                ? new ModelCostRates(cost.Input ?? 0, cost.Output ?? 0, cost.CacheRead, cost.CacheWrite)
                : new ModelCostRates(0, 0, 0, 0),
            ContextWindow = (long)(definition.ContextWindow ?? 128000),
            MaxTokens = (long)(definition.MaxTokens ?? 16384),
            SamplingParams = definition.SamplingParams,
            SamplingParamsByThinkingLevel = ToSamplingParamsByThinkingLevel(definition.SamplingParamsByThinkingLevel),
            Headers = null,
            Compat = MergeCompat(providerConfig.Compat, definition.Compat),
        };

        return SetExtraValue(model, "promptCache", definition.PromptCache);
    }

    /// <summary>在候选模型里挑一个「默认值来源」。对应 TS <c>findModelDefaults</c>。</summary>
    private static ModelSpec? FindModelDefaults(IReadOnlyList<ModelSpec> models, string modelId, string? api)
    {
        var chatModels = models.Where(model => model.Type == ModelType.Chat).ToList();
        return chatModels.FirstOrDefault(model => model.Id == modelId)
            ?? (api is not null ? chatModels.FirstOrDefault(model => model.Api == api) : null)
            ?? chatModels.FirstOrDefault(model => model.Api == OpenAiCompletionsApi)
            ?? chatModels.FirstOrDefault();
    }

    /// <summary>按类别挑扩展声明的默认值来源。对应 TS <c>findExtensionModelDefaults</c>。</summary>
    private static ModelSpec? FindExtensionModelDefaults(IReadOnlyList<ModelSpec> models, ProviderModelConfig definition)
    {
        var type = definition.Type;
        var candidates = models.Where(model => model.Type == type).ToList();
        return candidates.FirstOrDefault(model => model.Id == definition.Id)
            ?? (definition.Api is not null ? candidates.FirstOrDefault(model => model.Api == definition.Api) : null)
            ?? (type == ModelType.Chat ? candidates.FirstOrDefault(model => model.Api == OpenAiCompletionsApi) : null)
            ?? candidates.FirstOrDefault();
    }

    /// <summary>把扩展声明的模型定义转成目录条目。对应 TS <c>extensionModelFromDefinition</c>。</summary>
    private static ModelSpec ExtensionModelFromDefinition(
        string providerId,
        IReadOnlyList<ModelSpec> models,
        ProviderConfigInput config,
        ProviderModelConfig definition)
    {
        var type = definition.Type;
        var defaults = FindExtensionModelDefaults(models, definition);
        var api = definition.Api ?? (type == ModelType.Chat ? config.Api : null) ?? defaults?.Api;
        if (string.IsNullOrEmpty(api))
        {
            var providerLevel = type == ModelType.Chat ? " or provider level" : string.Empty;
            throw new InvalidOperationException(
                $"Provider {providerId}, model {definition.Id}: no \"api\" specified. Set it at model level{providerLevel}.");
        }

        var baseUrl = definition.BaseUrl ?? config.BaseUrl ?? defaults?.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
        }

        var model = new ModelSpec
        {
            Id = definition.Id,
            Name = definition.Name,
            Api = api,
            Provider = providerId,
            BaseUrl = baseUrl,
            Type = type,
            Input = definition.Input,
            InputLimits = definition.InputLimits,
            Cost = definition.Cost,
            Headers = null,
        };

        if (type == ModelType.Image)
        {
            // TS 直接展开定义，因此图片模型的 output 也随模型带下去；C# 的 ModelSpec 没有该字段，
            // 沿用 ModelSpecJson 的约定经 Extra 保留。
            var output = new JsonArray();
            foreach (var modality in ((ProviderImageModelConfig)definition).Output)
            {
                output.Add(JsonValue.Create(modality));
            }
            return SetExtraValue(model, "output", output);
        }

        return type == ModelType.Classifier
            ? model with { ContextWindow = (long)((ProviderClassifierModelConfig)definition).ContextWindow }
            : ApplyChatDefinition(model, (ProviderChatModelConfig)definition);
    }

    private static ModelSpec ApplyChatDefinition(ModelSpec model, ProviderChatModelConfig definition)
    {
        var result = model with
        {
            Reasoning = definition.Reasoning,
            ThinkingLevelMap = definition.ThinkingLevelMap,
            ContextWindow = (long)definition.ContextWindow,
            MaxTokens = (long)definition.MaxTokens,
            SamplingParams = definition.SamplingParams,
            SamplingParamsByThinkingLevel = definition.SamplingParamsByThinkingLevel,
            Compat = definition.Compat,
        };
        return SetExtraValue(result, "promptCache", definition.PromptCache);
    }

    /// <summary>
    /// 应用 <c>models.json</c> 一层：baseUrl/compat 覆盖 + 自定义模型 upsert。
    /// 对应 TS <c>applyModelsJson</c>。
    /// </summary>
    private static List<ModelSpec> ApplyModelsJson(
        string providerId,
        IReadOnlyList<ModelSpec> baseModels,
        ModelsJsonProvider? config)
    {
        if (config is null) return [.. baseModels];

        if (!string.IsNullOrEmpty(config.OAuth) && string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: \"baseUrl\" is required when \"oauth\" is set.");
        }

        var hasOverrides = config.ModelOverrides is { Count: > 0 };
        // TS 的 truthiness：空数组的 length 为 0 视为「未指定」，但空对象 {} 视为「已指定」。
        if ((config.Models is null or { Count: 0 })
            && string.IsNullOrEmpty(config.BaseUrl)
            && config.Headers is null
            && config.Compat is null
            && !hasOverrides
            && string.IsNullOrEmpty(config.ApiKey)
            && string.IsNullOrEmpty(config.OAuth)
            && config.AuthHeader is null)
        {
            throw new InvalidOperationException(
                $"Provider {providerId}: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".");
        }

        var models = new List<ModelSpec>(baseModels.Count);
        foreach (var model in baseModels)
        {
            var baseUrl = config.OAuth == "radius" ? model.BaseUrl : config.BaseUrl ?? model.BaseUrl;
            models.Add(model.Type == ModelType.Chat
                ? model with { BaseUrl = baseUrl, Compat = MergeCompat(model.Compat, config.Compat) }
                : model with { BaseUrl = baseUrl });
        }

        foreach (var definition in config.Models ?? [])
        {
            var existingIndex = models.FindIndex(model => model.Type == ModelType.Chat && model.Id == definition.Id);
            var defaults = FindModelDefaults(models, definition.Id, definition.Api ?? config.Api);
            var model = ModelFromJson(providerId, definition, config, defaults);
            if (existingIndex >= 0) models[existingIndex] = model;
            else models.Add(model);
        }

        return models;
    }

    /// <summary>
    /// 应用扩展一层：有模型声明时整体替换目录，否则只覆盖 baseUrl。
    /// 对应 TS <c>applyExtension</c>。
    /// </summary>
    private static List<ModelSpec> ApplyExtension(
        string providerId,
        IReadOnlyList<ModelSpec> models,
        ProviderConfigInput? config)
    {
        if (config is null) return [.. models];

        if (config.Models is null or { Count: 0 })
        {
            return string.IsNullOrEmpty(config.BaseUrl)
                ? [.. models]
                : [.. models.Select(model => model with { BaseUrl = config.BaseUrl! })];
        }

        return [.. config.Models.Select(definition =>
            ExtensionModelFromDefinition(providerId, models, config, definition))];
    }

    // ====================================================================== auth composition

    /// <summary>把扩展 OAuth 声明包装成规范的 <see cref="IOAuthAuth"/>。对应 TS <c>adaptOAuth</c>。</summary>
    private sealed class ExtensionOAuthAuth(ExtensionOAuthConfig config) : IOAuthAuth
    {
        public string Name => config.Name;

        public bool IsSubscription => config.IsSubscription ?? false;

        /// <remarks>
        /// TS 在此把扩展的 notify/prompt 回调翻译成规范的 <c>AuthInteraction</c>。C# 的两个面本来就是
        /// 同一个类型，因此这里直接转发（差异 C50）。
        /// </remarks>
        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => config.Login(interaction, options, cancellationToken);

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => config.RefreshToken(credential, cancellationToken);

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = config.GetApiKey(credential) });
    }

    /// <summary>
    /// 把配置的请求头与 Authorization 叠加到已解析的认证上。
    /// 对应 TS <c>withConfiguredAuth</c>。
    /// </summary>
    private static ModelAuth WithConfiguredAuth(
        ModelAuth auth, IReadOnlyDictionary<string, string>? headers, bool authHeader)
    {
        Dictionary<string, string?>? merged = null;
        if (auth.Headers is not null || headers is not null)
        {
            merged = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (auth.Headers is not null)
            {
                foreach (var (name, value) in auth.Headers) merged[name] = value;
            }
            if (headers is not null)
            {
                foreach (var (name, value) in headers) merged[name] = value;
            }
        }

        if (authHeader)
        {
            if (string.IsNullOrEmpty(auth.ApiKey))
            {
                throw new InvalidOperationException("authHeader requires a resolved API key");
            }

            var withAuthorization = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (merged is not null)
            {
                foreach (var (key, value) in merged) withAuthorization[key] = value;
            }
            withAuthorization["Authorization"] = $"Bearer {auth.ApiKey}";
            merged = withAuthorization;
        }

        return auth with { Headers = merged };
    }

    /// <summary>TS <c>configuredApiKey</c>：扩展优先于 <c>models.json</c>。</summary>
    private static string? ConfiguredApiKey(ModelsJsonProvider? config, ProviderConfigInput? extension)
        => extension?.ApiKey ?? config?.ApiKey;

    /// <summary>TS <c>configuredHeaders</c>：扩展覆盖 <c>models.json</c>；两者皆无才是 null。</summary>
    private static IReadOnlyDictionary<string, string>? ConfiguredHeaders(
        ModelsJsonProvider? config, ProviderConfigInput? extension)
        => config?.Headers is null && extension?.Headers is null
            ? null
            : MergeStringMaps(config?.Headers, extension?.Headers);

    /// <summary>TS <c>configContextEnv</c>：补全模板里引用、但未显式给出的环境变量。</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> ConfigContextEnvAsync(
        IReadOnlyList<string?> values,
        IAuthContext ctx,
        IReadOnlyDictionary<string, string>? explicitEnv = null,
        CancellationToken cancellationToken = default)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (explicitEnv is not null)
        {
            foreach (var (name, value) in explicitEnv) env[name] = value;
        }

        var names = new List<string>();
        foreach (var value in values)
        {
            if (value is null) continue;
            foreach (var name in ConfigValueResolver.GetConfigValueEnvVarNames(value))
            {
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }

        foreach (var name in names)
        {
            if (env.ContainsKey(name)) continue;
            var value = await ctx.EnvAsync(name, cancellationToken).ConfigureAwait(false);
            if (value is not null) env[name] = value;
        }

        return env.Count > 0 ? env : null;
    }

    /// <summary>
    /// 组合 api-key 认证。对应 TS <c>composeApiKeyAuth</c>。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>check</c> 成员在 C# 没有对应物（<see cref="IApiKeyAuth"/> 只有
    /// <c>ResolveAsync</c>，可用性一律经 resolve 判定，见 <c>ModelsAuth.CheckProviderAuthAsync</c>），
    /// 因此只移植 <c>login</c> 与 <c>resolve</c>（差异 C51）。
    /// </remarks>
    private static IApiKeyAuth? ComposeApiKeyAuth(
        string providerId,
        IProvider? baseProvider,
        ModelsJsonProvider? config,
        ProviderConfigInput? extension)
    {
        var inherited = baseProvider?.Auth?.ApiKey;
        var rawKey = ConfiguredApiKey(config, extension);
        var hasOAuth = extension?.OAuth is not null || baseProvider?.Auth?.OAuth is not null;
        // 纯 OAuth provider 不伪造 api-key 登录法。
        if (inherited is null && rawKey is null && hasOAuth) return null;

        var rawHeaders = ConfiguredHeaders(config, extension);
        var authHeader = extension?.AuthHeader ?? config?.AuthHeader ?? false;
        return new ComposedApiKeyAuth(providerId, inherited, rawKey, rawHeaders, authHeader);
    }

    /// <summary>组合出的 api-key 认证。对应 TS <c>composeApiKeyAuth</c> 返回的对象字面量。</summary>
    private sealed class ComposedApiKeyAuth(
        string providerId,
        IApiKeyAuth? inherited,
        string? rawKey,
        IReadOnlyDictionary<string, string>? rawHeaders,
        bool authHeader) : IApiKeyAuth
    {
        public string Name => inherited?.Name ?? "API key";

        public bool HasLogin => true;

        /// <summary>继承的 login，否则提示输入密钥。对应 TS 的 login 三元。</summary>
        public Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
            CancellationToken cancellationToken = default)
            => inherited is { HasLogin: true }
                ? inherited.LoginAsync(interaction, cancellationToken)
                : PromptForApiKeyAsync(interaction, cancellationToken);

        private static async Task<Credential.ApiKey> PromptForApiKeyAsync(
            ProviderAuthInteraction interaction, CancellationToken cancellationToken)
        {
            var key = await interaction
                .PromptAsync(new AuthPrompt.Secret("Enter API key"), cancellationToken)
                .ConfigureAwait(false);
            return new Credential.ApiKey(key);
        }

        public async Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
        {
            AuthResult? result;
            if (credential is not null)
            {
                if (inherited is not null)
                {
                    result = await inherited.ResolveAsync(credential, ctx, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = string.IsNullOrEmpty(credential.Key)
                        ? null
                        : new AuthResult
                        {
                            Auth = new ModelAuth { ApiKey = credential.Key },
                            Env = credential.Env,
                            Source = "stored credential",
                        };
                }
            }
            else if (rawKey is not null)
            {
                var env = await ConfigContextEnvAsync([rawKey], ctx, null, cancellationToken).ConfigureAwait(false);
                var key = ConfigValueResolver.ResolveConfigValueOrThrow(
                    rawKey, $"API key for provider \"{providerId}\"", env);
                result = inherited is not null
                    ? await inherited.ResolveAsync(new Credential.ApiKey(key), ctx, cancellationToken)
                        .ConfigureAwait(false)
                    : new AuthResult { Auth = new ModelAuth { ApiKey = key }, Source = "configured API key" };
            }
            else
            {
                result = inherited is null
                    ? null
                    : await inherited.ResolveAsync(null, ctx, cancellationToken).ConfigureAwait(false);
            }

            if (result is null) return null;

            var explicitEnv = new Dictionary<string, string>(StringComparer.Ordinal);
            if (credential?.Env is not null)
            {
                foreach (var (name, value) in credential.Env) explicitEnv[name] = value;
            }
            if (result.Env is not null)
            {
                foreach (var (name, value) in result.Env) explicitEnv[name] = value;
            }

            var headerEnv = await ConfigContextEnvAsync(
                HeaderValues(rawHeaders), ctx, explicitEnv, cancellationToken).ConfigureAwait(false);
            var headers = ConfigValueResolver.ResolveHeadersOrThrow(
                rawHeaders, $"provider \"{providerId}\"", headerEnv);

            return result with { Auth = WithConfiguredAuth(result.Auth, headers, authHeader) };
        }
    }

    /// <summary>组合 OAuth 认证。对应 TS <c>composeOAuthAuth</c>。</summary>
    private static IOAuthAuth? ComposeOAuthAuth(
        string providerId,
        IProvider? baseProvider,
        ModelsJsonProvider? config,
        ProviderConfigInput? extension)
    {
        IOAuthAuth? oauth = extension?.OAuth is { } extensionOAuth
            ? new ExtensionOAuthAuth(extensionOAuth)
            : baseProvider?.Auth?.OAuth;
        if (oauth is null) return null;

        return new ComposedOAuthAuth(
            providerId, oauth, ConfiguredHeaders(config, extension),
            extension?.AuthHeader ?? config?.AuthHeader ?? false);
    }

    /// <summary>组合出的 OAuth 认证：<c>toAuth</c> 叠加配置头与 Authorization。</summary>
    private sealed class ComposedOAuthAuth(
        string providerId,
        IOAuthAuth inner,
        IReadOnlyDictionary<string, string>? rawHeaders,
        bool authHeader) : IOAuthAuth
    {
        public string Name => inner.Name;

        public bool IsSubscription => inner.IsSubscription;

        public string? LoginLabel => inner.LoginLabel;

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => inner.LoginAsync(interaction, options, cancellationToken);

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => inner.RefreshAsync(credential, cancellationToken);

        public async Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
        {
            var auth = await inner.ToAuthAsync(credential, cancellationToken).ConfigureAwait(false);
            // 差异 C55：TS 这里读 `credential.env`（OAuthCredentials 的开放索引签名），
            // C# Credential.OAuth 没有该环境包，故恒为 undefined。
            var headers = ConfigValueResolver.ResolveHeadersOrThrow(
                rawHeaders,
                $"provider \"{providerId}\"",
                null);
            return WithConfiguredAuth(auth, headers, authHeader);
        }
    }

    /// <summary>
    /// 某个模型最终生效的原始请求头（未解析模板）。对应 TS <c>rawModelHeaders</c>。
    /// </summary>
    /// <remarks>
    /// <c>models.json</c> 的定义与覆盖只作用于 chat；扩展定义按「类别 + id」匹配，
    /// 避免不同类别的同名模型共享请求头。
    /// </remarks>
    private static IReadOnlyDictionary<string, string>? RawModelHeaders(
        ModelSpec model,
        ModelsJsonProvider? config,
        ProviderConfigInput? extension)
    {
        var chatDefinition = model.Type == ModelType.Chat
            ? config?.Models?.FirstOrDefault(entry => entry.Id == model.Id)
            : null;
        var extensionModel = extension?.Models?.FirstOrDefault(
            entry => entry.Type == model.Type && entry.Id == model.Id);

        IReadOnlyDictionary<string, string>? overrideHeaders = null;
        if (model.Type == ModelType.Chat
            && config?.ModelOverrides is { } overrides
            && overrides.TryGetValue(model.Id, out var entry)
            && entry is not null)
        {
            overrideHeaders = entry.Headers;
        }

        var headers = MergeStringMaps(overrideHeaders, chatDefinition?.Headers, extensionModel?.Headers);
        return headers.Count > 0 ? headers : null;
    }

    // ================================================================== shared helpers

    /// <summary>
    /// JS 对象展开语义的字符串字典合并：先出现的键保持位置，后出现者覆盖值。
    /// </summary>
    private static Dictionary<string, string> MergeStringMaps(
        params IReadOnlyDictionary<string, string>?[] maps)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var map in maps)
        {
            if (map is null) continue;
            foreach (var (key, value) in map) merged[key] = value;
        }
        return merged;
    }

    /// <summary>取头部模板值（供 <c>configContextEnv</c> 收集环境变量名）。</summary>
    private static IReadOnlyList<string?> HeaderValues(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return [];
        return [.. headers.Values.Select(value => (string?)value)];
    }

    /// <summary>浅合并两个 JSON 对象（后者的键覆盖前者，键序保持首见）。</summary>
    private static JsonObject MergeJsonObjects(JsonObject? baseObject, JsonObject? overrideObject)
    {
        var merged = new JsonObject();
        if (baseObject is not null)
        {
            foreach (var (key, value) in baseObject) merged[key] = value?.DeepClone();
        }
        if (overrideObject is not null)
        {
            foreach (var (key, value) in overrideObject) merged[key] = value?.DeepClone();
        }
        return merged;
    }

    /// <summary>把 <c>thinkingLevelMap</c> 覆盖合并到基映射上（显式 null 表示禁用该档位）。</summary>
    private static ThinkingLevelMap MergeThinkingLevelMap(ThinkingLevelMap? baseMap, JsonObject overrideMap)
    {
        var combined = new JsonObject();
        if (baseMap is not null)
        {
            foreach (var (level, mapped) in baseMap.Entries)
            {
                combined[level] = mapped is null ? null : JsonValue.Create(mapped);
            }
        }
        foreach (var (level, value) in overrideMap) combined[level] = value?.DeepClone();
        return ThinkingLevelMap.FromJsonObject(combined);
    }

    /// <summary>把 <c>samplingParamsByThinkingLevel</c> 的 JSON 形状转成按档位索引的字典。</summary>
    private static IReadOnlyDictionary<string, JsonObject>? ToSamplingParamsByThinkingLevel(JsonObject? source)
    {
        if (source is null) return null;
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (level, value) in source)
        {
            if (value is JsonObject parameters) result[level] = parameters;
        }
        return result;
    }

    /// <summary>读取 <see cref="ModelSpec.Extra"/> 里的对象字段（<c>promptCache</c> 等）。</summary>
    private static JsonObject? ExtraObject(ModelSpec model, string key) => model.Extra?[key] as JsonObject;

    /// <summary>
    /// 写入 <see cref="ModelSpec.Extra"/> 的某个字段。TS 的模型对象可以随意带字段，
    /// C# 的 <see cref="ModelSpec"/> 没有具名字段的那些（<c>promptCache</c>/<c>output</c>）走这里。
    /// </summary>
    private static ModelSpec SetExtraValue(ModelSpec model, string key, JsonNode? value)
    {
        var extra = new JsonObject();
        if (model.Extra is not null)
        {
            foreach (var (name, existing) in model.Extra)
            {
                if (name == key) continue;
                extra[name] = existing?.DeepClone();
            }
        }
        if (value is not null) extra[key] = value.DeepClone();
        return model with { Extra = extra.Count > 0 ? extra : null };
    }

    // ================================================================ composed provider

    /// <summary>
    /// 三层组合出的 provider。对应 TS <c>composeModelProvider</c> 里的对象字面量。
    /// </summary>
    /// <remarks>
    /// 差异 C54：TS 按能力条件性安装 <c>fetchDeferred</c>/<c>cancelDeferred</c>/<c>generateImages</c>/
    /// <c>classify</c>；C# 的能力接口是静态实现的，因此这里恒实现它们，缺失时返回 TS
    /// <c>Models</c> 层会产出的同一 error 结果（与 <c>ProviderFactory</c> 的既有做法一致）。
    /// </remarks>
    private sealed class ComposedProvider : IProvider, IImagesProvider, IClassifierProvider
    {
        private readonly string _providerId;
        private readonly IProvider? _base;
        private readonly ModelsJsonProvider? _config;
        private readonly ProviderConfigInput? _extension;
        private readonly Func<RefreshModelsContext, Task>? _refreshModels;

        /// <summary>最近一次刷新发布的扩展模型列表（覆盖 <see cref="ProviderConfigInput.Models"/>）。</summary>
        private IReadOnlyList<ProviderModelConfig>? _refreshedExtensionModels;

        /// <summary>扩展 OAuth 的当前凭据，供 <c>modifyModels</c> 使用。</summary>
        private Credential.OAuth? _extensionOAuthCredential;

        public ComposedProvider(
            string providerId,
            string name,
            string? baseUrl,
            IApiKeyAuth? apiKey,
            IOAuthAuth? oauth,
            IProvider? baseProvider,
            ModelsJsonProvider? config,
            ProviderConfigInput? extension)
        {
            _providerId = providerId;
            _base = baseProvider;
            _config = config;
            _extension = extension;
            Name = name;
            BaseUrl = baseUrl;
            Auth = new ProviderAuth { ApiKey = apiKey, OAuth = oauth };

            if (baseProvider?.RefreshModels is not null
                || extension?.RefreshModels is not null
                || extension?.OAuth?.ModifyModels is not null)
            {
                _refreshModels = RefreshModelsAsync;
            }

            // 注册时立刻校验一次，让结构性错误马上暴露。
            GetAllModels();
        }

        public string Id => _providerId;

        public string Name { get; }

        public string? BaseUrl { get; }

        public ProviderAuth? Auth { get; }

        // ------------------------------------------------------------------ catalog

        /// <summary>当前生效的扩展配置（刷新后模型列表被替换）。</summary>
        private ProviderConfigInput? CurrentExtension()
            => _extension is not null && _refreshedExtensionModels is not null
                ? _extension with { Models = _refreshedExtensionModels }
                : _extension;

        /// <summary>
        /// 叠放五层并返回全部类别的模型。对应 TS <c>getAllModels</c>。
        /// </summary>
        public IReadOnlyList<ModelSpec> GetAllModels()
        {
            var models = ApplyExtension(
                _providerId,
                ApplyModelsJson(_providerId, GetAllProviderModels(_base), _config),
                CurrentExtension());

            if (_extensionOAuthCredential is not null && _extension?.OAuth?.ModifyModels is { } modifyModels)
            {
                // 扩展钩子是 chat-only；其余类别原样透传。
                var chat = models.Where(model => model.Type == ModelType.Chat).ToList();
                var rest = models.Where(model => model.Type != ModelType.Chat).ToList();
                models = [.. modifyModels(chat, _extensionOAuthCredential), .. rest];
            }

            if (_config?.ModelOverrides is not { Count: > 0 } overrides) return models;

            return [.. models.Select(model =>
                model.Type == ModelType.Chat
                    && overrides.TryGetValue(model.Id, out var entry)
                    && entry is not null
                    ? ApplyModelOverride(model, entry)
                    : model)];
        }

        public IReadOnlyList<ModelSpec> GetModels()
            => [.. GetAllModels().Where(model => model.Type == ModelType.Chat)];

        public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
            => _base?.FilterModels(models, credential) ?? models;

        public IReadOnlyList<ModelSpec>? FilterAllModels(IReadOnlyList<ModelSpec> models, Credential? credential)
            => _base?.FilterAllModels(models, credential);

        // ------------------------------------------------------------------ refresh

        /// <summary>
        /// 先刷新 base，再刷新扩展；「先校验新列表、再发布」由 <see cref="ModelsPublication.Update"/> 保证。
        /// 对应 TS 的 <c>refreshModels</c> 条件成员。
        /// </summary>
        private async Task RefreshModelsAsync(RefreshModelsContext context)
        {
            if (_base?.RefreshModels is { } baseRefresh)
            {
                await baseRefresh(context).ConfigureAwait(false);
            }

            IReadOnlyList<ProviderModelConfig>? refreshed = null;
            if (_extension?.RefreshModels is { } extensionRefresh)
            {
                refreshed = await extensionRefresh(context).ConfigureAwait(false);
            }

            if (context.Signal.IsCancellationRequested) return;

            var oauthCredential = context.Credential as Credential.OAuth;
            await context.Publish(new ModelsPublication
            {
                Update = () =>
                {
                    if (refreshed is not null)
                    {
                        // 发布新的同步列表之前先校验。
                        ApplyExtension(
                            _providerId,
                            ApplyModelsJson(_providerId, GetAllProviderModels(_base), _config),
                            _extension! with { Models = refreshed });
                        _refreshedExtensionModels = refreshed;
                    }
                    _extensionOAuthCredential = oauthCredential;
                },
            }).ConfigureAwait(false);
        }

        public Func<RefreshModelsContext, Task>? RefreshModels => _refreshModels;

        // ------------------------------------------------------------------ streaming

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => StreamWith(model, context, options, simple: false);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => StreamWith(model, context, options, simple: true);

        private IAssistantMessageEventStream StreamWith(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options, bool simple)
            => LazyStream.Run(model, () =>
            {
                if (_extension?.StreamSimple is { } extensionStream && model.Api == _extension.Api)
                {
                    return Task.FromResult(extensionStream(model, new TranscriptContext(context), options));
                }

                if (_base is not null && SupportsBaseApi(model))
                {
                    return Task.FromResult(simple
                        ? _base.StreamSimple(model, context, options)
                        : _base.Stream(model, context, options));
                }

                var api = ApiRegistry.GetApiProvider(model.Api)
                    ?? throw new ModelsError(ModelsErrorCode.Provider,
                        $"No API provider registered for api: {model.Api}");

                // 差异 C52：C# 的 api 注册表契约里 Stream 收 JsonObject、StreamSimple 收
                // SimpleStreamOptions，而 provider 边界是 IReadOnlyDictionary——后者只能无损地
                // 转成 SimpleStreamOptions（Signal/回调等无法进 JSON）。因此与 Compat.Stream 一样，
                // 两条路径都走 StreamSimple。
                return Task.FromResult(api.StreamSimple(
                    model,
                    new TranscriptContext(context),
                    ProviderStreamOptions.FromDictionary(options),
                    null,
                    CancellationToken.None));
            });

        /// <summary>TS <c>supportsBaseApi</c>：base 的 chat 目录里是否有同 api 的模型。</summary>
        private bool SupportsBaseApi(ModelSpec model)
            => _base is not null && _base.GetModels().Any(entry => entry.Api == model.Api);

        // ------------------------------------------------------------------ deferred

        public bool SupportsFetchDeferred => _base?.SupportsFetchDeferred == true;

        public bool SupportsCancelDeferred => _base?.SupportsCancelDeferred == true;

        public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null)
            => _base?.StreamDeferred(model, handle, options);

        public Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
            => _base is null
                ? throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_providerId} does not support deferred responses")
                : _base.CancelDeferredAsync(model, handle, options, cancellationToken);

        // ------------------------------------------------------------------ images / classify

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
        {
            var extensionImages = _extension?.Images;
            if (extensionImages is not null
                && extensionImages.TryGetValue(model.Api, out var implementation)
                && implementation is not null)
            {
                return implementation.GenerateImages(model, context, options, cancellationToken);
            }

            if (_base is IImagesProvider baseImages)
            {
                return baseImages.GenerateImagesAsync(model, context, options, cancellationToken);
            }

            var aborted = options?.Signal.IsCancellationRequested == true;
            // TS：base 没有 generateImages 且扩展未声明任何 images 时该成员不存在，
            // 于是 Models.generateImages 报「does not support image generation」。
            return Task.FromResult(extensionImages is null or { Count: 0 }
                ? ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_providerId} does not support image generation"), aborted)
                : ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_providerId} has no image implementation for \"{model.Api}\""), aborted));
        }

        public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
        {
            var extensionClassifiers = _extension?.Classifiers;
            if (extensionClassifiers is not null
                && extensionClassifiers.TryGetValue(model.Api, out var implementation)
                && implementation is not null)
            {
                return implementation.Classify(model, context, options, cancellationToken);
            }

            if (_base is IClassifierProvider baseClassifier)
            {
                return baseClassifier.ClassifyAsync(model, context, options, cancellationToken);
            }

            var aborted = options?.Signal.IsCancellationRequested == true;
            return Task.FromResult(extensionClassifiers is null or { Count: 0 }
                ? ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_providerId} does not support classification"), aborted)
                : ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_providerId} has no classifier implementation for \"{model.Api}\""), aborted));
        }
    }
}
