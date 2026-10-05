namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// OAuth 流的懒加载注册表。对应 TS auth/oauth/load.ts——TS 需要变量 specifier
/// 动态 import 阻止打包器把 Node-only 代码打进浏览器包；C# 无此问题，等价
/// 形式是 <see cref="Lazy{T}"/> 单例（模块级单例对象语义保持）。
/// </summary>
public static class OAuthFlows
{
    private static readonly Lazy<AnthropicOAuth> Anthropic = new(() => new AnthropicOAuth());
    private static readonly Lazy<OpenAiCodexOAuth> OpenAiCodex = new(() => new OpenAiCodexOAuth());
    private static readonly Lazy<OpenAiChatGptOAuth> OpenAiChatGpt = new(() => new OpenAiChatGptOAuth());
    private static readonly Lazy<GitHubCopilotOAuth> GitHubCopilot = new(() => new GitHubCopilotOAuth());
    private static readonly Lazy<OpenRouterOAuth> OpenRouter = new(() => new OpenRouterOAuth());
    private static readonly Lazy<KimiCodingOAuth> KimiCoding = new(() => new KimiCodingOAuth());
    private static readonly Lazy<MetaOAuth> Meta = new(() => new MetaOAuth());
    private static readonly Lazy<XaiOAuth> Xai = new(() => new XaiOAuth());

    /// <summary>对应 TS <c>loadAnthropicOAuth</c>。</summary>
    public static IOAuthAuth LoadAnthropic() => Anthropic.Value;

    /// <summary>对应 TS <c>loadOpenAICodexOAuth</c>。</summary>
    public static IOAuthAuth LoadOpenAICodex() => OpenAiCodex.Value;

    /// <summary>对应 TS <c>loadOpenAIChatGPTOAuth</c>。</summary>
    public static IOAuthAuth LoadOpenAIChatGPT() => OpenAiChatGpt.Value;

    /// <summary>对应 TS <c>loadGitHubCopilotOAuth</c>。</summary>
    public static IOAuthAuth LoadGitHubCopilot() => GitHubCopilot.Value;

    /// <summary>对应 TS <c>loadOpenRouterOAuth</c>。</summary>
    public static IOAuthAuth LoadOpenRouter() => OpenRouter.Value;

    /// <summary>对应 TS <c>loadKimiCodingOAuth</c>。</summary>
    public static IOAuthAuth LoadKimiCoding() => KimiCoding.Value;

    /// <summary>对应 TS <c>loadMetaOAuth</c>。</summary>
    public static IOAuthAuth LoadMeta() => Meta.Value;

    /// <summary>对应 TS <c>loadXaiOAuth</c>。</summary>
    public static IOAuthAuth LoadXai() => Xai.Value;

    /// <summary>对应 TS <c>loadRadiusOAuth</c>（网关为配置值，每次新建）。</summary>
    public static IOAuthAuth LoadRadius(RadiusOAuth.Options options, HttpClient? httpClient = null)
        => new RadiusOAuth(options, httpClient);
}
