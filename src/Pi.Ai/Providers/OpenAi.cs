using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenAI provider。对应 TS <c>providers/openai.ts</c> 的 <c>openaiProvider()</c>。
/// </summary>
public static class OpenAi
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
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
        Api = LazyApis.OpenAiResponsesApi(),
    });
}
