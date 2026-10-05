using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// xAI provider。对应 TS <c>providers/xai.ts</c> 的 <c>xaiProvider()</c>。
/// </summary>
public static class Xai
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
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
        Api = LazyApis.OpenAiResponsesApi(),
    });
}
