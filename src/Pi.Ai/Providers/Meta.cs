using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Meta provider。对应 TS <c>providers/meta.ts</c> 的 <c>metaProvider()</c>。
/// </summary>
public static class Meta
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
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
        Api = LazyApis.OpenAiResponsesApi(),
    });
}
