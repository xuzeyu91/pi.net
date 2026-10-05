using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// 内建模型目录读取。对应 TS <c>providers/&lt;name&gt;.models.ts</c> +
/// <c>models.generated.ts</c> 的按 provider 目录：从嵌入资源加载 groups JSON，
/// 缺失时退回空目录（pi 仓库不带生成产物，与 P18 约定一致）。
/// </summary>
internal static class BuiltinCatalog
{
    private static readonly ConcurrentDictionary<string, ModelCatalog> Cache = new(StringComparer.Ordinal);

    public static ModelCatalog Load(string provider)
        => Cache.GetOrAdd(provider, static id =>
        {
            try
            {
                return ModelCatalog.LoadFromResource(id);
            }
            catch (FileNotFoundException)
            {
                return ModelCatalog.Load(id, new JsonObject());
            }
        });

    public static IReadOnlyList<ModelSpec> Chat(string provider) => [.. Load(provider).ChatModels.Values];

    public static IReadOnlyList<ModelSpec> All(string provider)
        => [.. Load(provider).ChatModels.Values, .. Load(provider).ImageModels.Values, .. Load(provider).ClassifierModels.Values];

    public static ModelSpec? Find(string provider, string modelId) => Load(provider).Find(modelId);
}

/// <summary>
/// 内建 provider 目录：42 家（含 2 家动态/纯分类）的元数据与工厂。
/// 对应 TS <c>providers/all.ts</c> 的 <c>builtinProviders()</c> 所引用的各家工厂。
/// </summary>
public static class BuiltinProviders
{
    /// <summary>内建 provider id 列表（与 <c>models.generated.ts</c> 的 MODELS 键一致）。</summary>
    public static readonly IReadOnlyList<string> ProviderIds =
    [
        "amazon-bedrock", "ant-ling", "anthropic", "azure-openai-responses", "baseten",
        "cerebras", "cloudflare-ai-gateway", "cloudflare-workers-ai", "deepseek", "fireworks",
        "github-copilot", "google", "google-vertex", "groq", "huggingface",
        "kimi-coding", "meta", "minimax", "minimax-cn", "mistral",
        "moonshotai", "moonshotai-cn", "nvidia", "openai", "openai-codex",
        "opencode", "opencode-go", "openrouter", "qwen-token-plan", "qwen-token-plan-cn",
        "qwen-token-plan-individual", "radius", "together", "typesafe", "vercel-ai-gateway",
        "xai", "xiaomi", "xiaomi-token-plan-ams", "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp",
        "zai", "zai-coding-cn",
    ];

    /// <summary>按 id 构造 provider（每次新建）。对应 TS 各家 <c>xxxProvider()</c>。</summary>
    public static IProvider Create(string id) => id switch
    {
        "amazon-bedrock" => Bedrock(),
        "ant-ling" => SimpleOpenAi(id, "Ant Ling", "https://api.ant-ling.com/v1", "Ant Ling API key", "ANT_LING_API_KEY"),
        "anthropic" => Anthropic(),
        "azure-openai-responses" => SimpleSingle(id, "Azure OpenAI", null, "Azure OpenAI API key", "AZURE_OPENAI_API_KEY",
            BuiltinApis.AzureOpenAiResponses()),
        "baseten" => SimpleOpenAi(id, "Baseten", "https://inference.baseten.co/v1", "Baseten API key", "BASETEN_API_KEY"),
        "cerebras" => SimpleOpenAi(id, "Cerebras", "https://api.cerebras.ai/v1", "Cerebras API key", "CEREBRAS_API_KEY"),
        "cloudflare-ai-gateway" => CloudflareAiGateway(),
        "cloudflare-workers-ai" => CloudflareWorkersAi(),
        "deepseek" => SimpleOpenAi(id, "DeepSeek", "https://api.deepseek.com", "DeepSeek API key", "DEEPSEEK_API_KEY"),
        "fireworks" => Fireworks(),
        "github-copilot" => GitHubCopilot(),
        "google" => Google(),
        "google-vertex" => GoogleVertex(),
        "groq" => SimpleOpenAi(id, "Groq", "https://api.groq.com/openai/v1", "Groq API key", "GROQ_API_KEY"),
        "huggingface" => SimpleOpenAi(id, "Hugging Face", "https://router.huggingface.co/v1", "Hugging Face token", "HF_TOKEN"),
        "kimi-coding" => KimiCoding(),
        "meta" => Meta(),
        "minimax" => SimpleSingle(id, "MiniMax", "https://api.minimax.io/anthropic", "MiniMax API key", "MINIMAX_API_KEY",
            BuiltinApis.AnthropicMessages()),
        "minimax-cn" => SimpleSingle(id, "MiniMax CN", "https://api.minimaxi.com/anthropic", "MiniMax CN API key", "MINIMAX_CN_API_KEY",
            BuiltinApis.AnthropicMessages()),
        "mistral" => SimpleSingle(id, "Mistral", "https://api.mistral.ai", "Mistral API key", "MISTRAL_API_KEY",
            BuiltinApis.MistralConversations()),
        "moonshotai" => SimpleOpenAi(id, "Moonshot AI", "https://api.moonshot.ai/v1", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        "moonshotai-cn" => SimpleOpenAi(id, "Moonshot AI CN", "https://api.moonshot.cn/v1", "Moonshot AI API key", "MOONSHOT_API_KEY"),
        "nvidia" => SimpleOpenAi(id, "NVIDIA", "https://integrate.api.nvidia.com/v1", "NVIDIA API key", "NVIDIA_API_KEY"),
        "openai" => OpenAi(),
        "openai-codex" => OpenAiCodex(),
        "opencode" => OpenCode(),
        "opencode-go" => OpenCodeGo(),
        "openrouter" => OpenRouter(),
        "qwen-token-plan" => SimpleOpenAi(id, "Qwen Token Plan",
            "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
            "Qwen Token Plan API key", "QWEN_TOKEN_PLAN_API_KEY"),
        "qwen-token-plan-cn" => SimpleOpenAi(id, "Qwen Token Plan CN",
            "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1",
            "Qwen Token Plan CN API key", "QWEN_TOKEN_PLAN_CN_API_KEY"),
        "qwen-token-plan-individual" => SimpleOpenAi(id, "Qwen Token Plan Individual",
            "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
            "Qwen Token Plan Individual API key", "QWEN_TOKEN_PLAN_API_KEY"),
        "radius" => new RadiusProvider(),
        "together" => SimpleOpenAi(id, "Together", "https://api.together.ai/v1", "Together API key", "TOGETHER_API_KEY"),
        "typesafe" => TypeSafe(),
        "vercel-ai-gateway" => VercelAiGateway(),
        "xai" => Xai(),
        "xiaomi" => SimpleOpenAi(id, "Xiaomi", "https://api.xiaomimimo.com/v1", "Xiaomi API key", "XIAOMI_API_KEY"),
        "xiaomi-token-plan-ams" => SimpleOpenAi(id, "Xiaomi Token Plan AMS",
            "https://token-plan-ams.xiaomimimo.com/v1", "Xiaomi Token Plan AMS API key", "XIAOMI_TOKEN_PLAN_AMS_API_KEY"),
        "xiaomi-token-plan-cn" => SimpleOpenAi(id, "Xiaomi Token Plan CN",
            "https://token-plan-cn.xiaomimimo.com/v1", "Xiaomi Token Plan CN API key", "XIAOMI_TOKEN_PLAN_CN_API_KEY"),
        "xiaomi-token-plan-sgp" => SimpleOpenAi(id, "Xiaomi Token Plan SGP",
            "https://token-plan-sgp.xiaomimimo.com/v1", "Xiaomi Token Plan SGP API key", "XIAOMI_TOKEN_PLAN_SGP_API_KEY"),
        "zai" => SimpleOpenAi(id, "Z.AI", "https://api.z.ai/api/coding/paas/v4", "Z.AI API key", "ZAI_API_KEY"),
        "zai-coding-cn" => SimpleOpenAi(id, "Z.AI Coding CN", "https://open.bigmodel.cn/api/coding/paas/v4",
            "Z.AI Coding CN API key", "ZAI_CODING_CN_API_KEY"),
        _ => throw new ArgumentException($"Unknown builtin provider: {id}"),
    };

    /// <summary>全部内建 provider（每次新建）。对应 TS <c>builtinProviders()</c>。</summary>
    public static IReadOnlyList<IProvider> CreateAll() => ProviderIds.Select(Create).ToList();

    // ---------- 通用装配 ----------

    /// <summary>openai-completions 兼容家的通用装配。</summary>
    private static IProvider SimpleOpenAi(string id, string name, string baseUrl, string authName, string envVar)
        => SimpleSingle(id, name, baseUrl, authName, envVar, BuiltinApis.OpenAiCompletions());

    /// <summary>单一 API 实现的通用装配。</summary>
    private static IProvider SimpleSingle(string id, string name, string? baseUrl, string authName, string envVar,
        ProviderStreams api)
        => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = id,
            Name = name,
            BaseUrl = baseUrl,
            Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth(authName, [envVar]) },
            Models = BuiltinCatalog.All(id),
            Api = api,
        });

    // ---------- 非标准家 ----------

    private static IProvider Bedrock() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "amazon-bedrock",
        Name = "Amazon Bedrock",
        Auth = new ProviderAuth { ApiKey = new BedrockAuth() },
        Models = BuiltinCatalog.All("amazon-bedrock"),
        Api = BuiltinApis.BedrockConverseStream(),
    });

    private static IProvider Anthropic() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "anthropic",
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com",
        Auth = new ProviderAuth
        {
            ApiKey = new AnthropicApiKeyAuth(),
            OAuth = new LazyOAuthAuth("Anthropic (Claude Pro/Max)", OAuthFlows.LoadAnthropic, isSubscription: true),
        },
        Models = BuiltinCatalog.All("anthropic"),
        Api = BuiltinApis.AnthropicMessages(),
    });

    private static IProvider Google() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "google",
        Name = "Google",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Gemini API key", ["GEMINI_API_KEY"]) },
        Models = BuiltinCatalog.All("google"),
        Api = BuiltinApis.GoogleGenerativeAi(),
    });

    private static IProvider GoogleVertex() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "google-vertex",
        Name = "Google Vertex AI",
        Auth = new ProviderAuth { ApiKey = new VertexAuth() },
        Models = BuiltinCatalog.All("google-vertex"),
        Api = BuiltinApis.GoogleVertex(),
    });

    private static IProvider Fireworks() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "fireworks",
        Name = "Fireworks",
        BaseUrl = "https://api.fireworks.ai/inference",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Fireworks API key", ["FIREWORKS_API_KEY"]) },
        Models = BuiltinCatalog.All("fireworks"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.AnthropicMessages(),
            ["openai-completions"] = BuiltinApis.OpenAiCompletions(),
        },
    });

    private static IProvider GitHubCopilot() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "github-copilot",
        Name = "GitHub Copilot",
        BaseUrl = "https://api.individual.githubcopilot.com",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("GitHub Copilot token", ["COPILOT_GITHUB_TOKEN"]),
            OAuth = new LazyOAuthAuth("GitHub Copilot", OAuthFlows.LoadGitHubCopilot, isSubscription: true),
        },
        Models = BuiltinCatalog.All("github-copilot"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.AnthropicMessages(),
            ["openai-completions"] = BuiltinApis.OpenAiCompletions(),
            ["openai-responses"] = BuiltinApis.OpenAiResponses(),
        },
        // Copilot 登录后按 availableModelIds 收敛可用 chat 模型。
        FilterModels = static (models, credential) =>
        {
            if (credential is not Credential.OAuth oauth) return models;
            var availableModelIds = oauth.AvailableModelIds;
            if (availableModelIds is null) return models;
            var available = new HashSet<string>(availableModelIds, StringComparer.Ordinal);
            return models.Where(model => available.Contains(model.Id)).ToList();
        },
    });

    private static IProvider KimiCoding() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "kimi-coding",
        Name = "Kimi For Coding",
        BaseUrl = "https://api.kimi.com/coding",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("Kimi API key", ["KIMI_API_KEY"]),
            OAuth = new LazyOAuthAuth("Kimi Code (subscription)", OAuthFlows.LoadKimiCoding,
                isSubscription: true, loginLabel: "Sign in with Kimi Code"),
        },
        Models = BuiltinCatalog.All("kimi-coding"),
        Api = BuiltinApis.AnthropicMessages(),
    });

    private static IProvider Meta() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "meta",
        Name = "Meta",
        BaseUrl = "https://api.meta.ai/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("Meta Model API key", ["META_API_KEY"]),
            OAuth = new LazyOAuthAuth("Meta (Muse subscription)", OAuthFlows.LoadMeta,
                isSubscription: true, loginLabel: "Sign in with Meta"),
        },
        Models = BuiltinCatalog.All("meta"),
        Api = BuiltinApis.OpenAiResponses(),
    });

    private static IProvider OpenAi() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openai",
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("OpenAI API key", ["OPENAI_API_KEY"]),
            OAuth = new LazyOAuthAuth("OpenAI (ChatGPT subscription)", OAuthFlows.LoadOpenAIChatGPT,
                isSubscription: true, loginLabel: "Sign in with ChatGPT"),
        },
        Models = BuiltinCatalog.All("openai"),
        Api = BuiltinApis.OpenAiResponses(),
    });

    private static IProvider OpenAiCodex() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openai-codex",
        Name = "OpenAI Codex (legacy)",
        BaseUrl = "https://chatgpt.com/backend-api",
        Auth = new ProviderAuth
        {
            OAuth = new LazyOAuthAuth("OpenAI (ChatGPT Plus/Pro)", OAuthFlows.LoadOpenAICodex, isSubscription: true),
        },
        Models = BuiltinCatalog.All("openai-codex"),
        Api = BuiltinApis.OpenAiCodexResponses(),
    });

    private static IProvider OpenRouter() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openrouter",
        Name = "OpenRouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("OpenRouter API key", ["OPENROUTER_API_KEY"]),
            OAuth = new LazyOAuthAuth("OpenRouter OAuth", OAuthFlows.LoadOpenRouter,
                loginLabel: "Sign in with OpenRouter"),
        },
        Models = BuiltinCatalog.All("openrouter"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.AnthropicMessages(),
            ["openai-completions"] = BuiltinApis.OpenAiCompletions(),
        },
        Images = new Dictionary<string, ProviderImages> { ["openrouter-images"] = BuiltinApis.OpenRouterImages() },
        // OpenRouter 在 /api/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = BuiltinApis.TypesafeSystemOne(),
        },
    });

    private static IProvider OpenCode() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "opencode",
        Name = "OpenCode Zen",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("OpenCode API key", ["OPENCODE_API_KEY"]) },
        Models = BuiltinCatalog.All("opencode"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.AnthropicMessages()),
            ["google-generative-ai"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.GoogleGenerativeAi()),
            ["openai-completions"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.OpenAiCompletions()),
            ["openai-responses"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.OpenAiResponses()),
        },
        // OpenCode Zen 在 /zen/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = BuiltinApis.TypesafeSystemOne(),
        },
    });

    private static IProvider OpenCodeGo() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "opencode-go",
        Name = "OpenCode Go",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("OpenCode API key", ["OPENCODE_API_KEY"]) },
        Models = BuiltinCatalog.All("opencode-go"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.AnthropicMessages()),
            ["openai-completions"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.OpenAiCompletions()),
            ["openai-responses"] = BuiltinApis.WithOpenCodeSessionHeader(BuiltinApis.OpenAiResponses()),
        },
    });

    private static IProvider TypeSafe() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "typesafe",
        Name = "TypeSafe",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("TypeSafe API key", ["TYPESAFE_API_KEY"]) },
        Models = BuiltinCatalog.All("typesafe"),
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = BuiltinApis.TypesafeSystemOne(),
        },
    });

    private static IProvider VercelAiGateway() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "vercel-ai-gateway",
        Name = "Vercel AI Gateway",
        BaseUrl = "https://ai-gateway.vercel.sh",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Vercel AI Gateway API key", ["AI_GATEWAY_API_KEY"]) },
        Models = BuiltinCatalog.All("vercel-ai-gateway"),
        Api = BuiltinApis.AnthropicMessages(),
        // AI Gateway 在 /typesafe/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = BuiltinApis.TypesafeSystemOne(),
        },
    });

    private static IProvider Xai() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xai",
        Name = "xAI",
        BaseUrl = "https://api.x.ai/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("xAI API key", ["XAI_API_KEY"]),
            OAuth = new LazyOAuthAuth("xAI (Grok/X subscription)", OAuthFlows.LoadXai,
                isSubscription: true, loginLabel: "Sign in with SuperGrok or X Premium"),
        },
        Models = BuiltinCatalog.All("xai"),
        Api = BuiltinApis.OpenAiResponses(),
    });

    private static IProvider CloudflareAiGateway() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "cloudflare-ai-gateway",
        Name = "Cloudflare AI Gateway",
        Auth = new ProviderAuth { ApiKey = new CloudflareAuth(CloudflareAuthKind.AiGateway) },
        Models = BuiltinCatalog.All("cloudflare-ai-gateway"),
        // api 映射固定三家：models.dev 的网关目录会反复增删 workers-ai/* 条目，
        // 仅靠 models 推断会在目录恰好为空时拒绝 openai-completions 条目。
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = BuiltinApis.WithCloudflare(BuiltinApis.AnthropicMessages()),
            ["openai-completions"] = BuiltinApis.WithCloudflare(BuiltinApis.OpenAiCompletions()),
            ["openai-responses"] = BuiltinApis.WithCloudflare(BuiltinApis.OpenAiResponses()),
        },
    });

    private static IProvider CloudflareWorkersAi() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "cloudflare-workers-ai",
        Name = "Cloudflare Workers AI",
        Auth = new ProviderAuth { ApiKey = new CloudflareAuth(CloudflareAuthKind.WorkersAi) },
        Models = BuiltinCatalog.All("cloudflare-workers-ai"),
        Api = BuiltinApis.WithCloudflare(BuiltinApis.OpenAiCompletions()),
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["cloudflare-workers-ai-system-one"] = BuiltinApis.CloudflareWorkersAiSystemOne(),
        },
    });
}

/// <summary>
/// all.ts 的目录读取与集合装配。对应 TS <c>providers/all.ts</c>：
/// <c>getBuiltinModel(s)</c> / <c>getBuiltinImageModel(s)</c> / <c>getBuiltinClassifierModel(s)</c> /
/// <c>getBuiltinProviders()</c> / <c>getAllBuiltinModels()</c> / <c>builtinProviders()</c> / <c>builtinModels()</c>。
/// </summary>
public static class All
{
    /// <summary>内建 provider id 列表。对应 TS <c>getBuiltinProviders()</c>。</summary>
    public static IReadOnlyList<string> GetBuiltinProviders() => Pi.Ai.Providers.BuiltinProviders.ProviderIds;

    /// <summary>按 provider + id 取 chat 模型。对应 TS <c>getBuiltinModel()</c>。</summary>
    public static ModelSpec? GetBuiltinModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ChatModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部 chat 模型。对应 TS <c>getBuiltinModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinModels(string provider) => BuiltinCatalog.Chat(provider);

    /// <summary>按 provider + id 取图片模型。对应 TS <c>getBuiltinImageModel()</c>。</summary>
    public static ModelSpec? GetBuiltinImageModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ImageModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部图片模型。对应 TS <c>getBuiltinImageModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinImageModels(string provider)
        => [.. BuiltinCatalog.Load(provider).ImageModels.Values];

    /// <summary>按 provider + id 取分类模型。对应 TS <c>getBuiltinClassifierModel()</c>。</summary>
    public static ModelSpec? GetBuiltinClassifierModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ClassifierModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部分类模型。对应 TS <c>getBuiltinClassifierModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinClassifierModels(string provider)
        => [.. BuiltinCatalog.Load(provider).ClassifierModels.Values];

    /// <summary>按 provider 取全部类别模型。对应 TS <c>getAllBuiltinModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetAllBuiltinModels(string provider) => BuiltinCatalog.All(provider);

    /// <summary>全部内建 provider（每次新建）。对应 TS <c>builtinProviders()</c>。</summary>
    public static IReadOnlyList<IProvider> BuiltinProviders() => Pi.Ai.Providers.BuiltinProviders.CreateAll();

    /// <summary>注册全部内建 provider 的 <see cref="Models"/> 集合。对应 TS <c>builtinModels()</c>。</summary>
    public static Models.Models BuiltinModels()
    {
        var models = new Models.Models();
        foreach (var provider in Pi.Ai.Providers.BuiltinProviders.CreateAll()) models.SetProvider(provider);
        return models;
    }

    /// <summary>
    /// 目录生成时间（epoch 毫秒）。pi 仓库不带 <c>data/.manifest.json</c> 生成产物，
    /// C# 侧以嵌入资源是否存在为准，缺失返回 null。对应 TS <c>getBuiltinModelDataGeneratedAt()</c>。
    /// </summary>
    public static long? GetBuiltinModelDataGeneratedAt()
    {
        var assembly = typeof(All).Assembly;
        var resourceName = "Pi.Ai.ModelData..manifest.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        var generatedAt = manifest?.Str("generatedAt");
        return generatedAt is not null
            && DateTimeOffset.TryParse(generatedAt, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }
}
