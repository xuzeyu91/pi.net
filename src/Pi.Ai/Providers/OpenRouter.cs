using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenRouter provider。对应 TS <c>providers/openrouter.ts</c> 的 <c>openrouterProvider()</c>：
/// 两家 chat API + 图片生成 + TypeSafe System One 分类。
/// </summary>
public static class OpenRouter
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
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
            ["anthropic-messages"] = LazyApis.AnthropicMessagesApi(),
            ["openai-completions"] = LazyApis.OpenAiCompletionsApi(),
        },
        Images = new Dictionary<string, ProviderImages>
        {
            ["openrouter-images"] = LazyApis.OpenRouterImagesApi(),
        },
        // OpenRouter 在 /api/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = LazyApis.TypesafeSystemOneApi(),
        },
    });
}
