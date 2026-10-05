using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Kimi For Coding provider。对应 TS <c>providers/kimi-coding.ts</c> 的
/// <c>kimiCodingProvider()</c>。
/// </summary>
public static class KimiCoding
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
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
        Api = LazyApis.AnthropicMessagesApi(),
    });
}
