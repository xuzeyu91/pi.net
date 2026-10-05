using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Mistral provider。对应 TS <c>providers/mistral.ts</c> 的 <c>mistralProvider()</c>。
/// </summary>
public static class Mistral
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "mistral",
        Name = "Mistral",
        BaseUrl = "https://api.mistral.ai",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Mistral API key", ["MISTRAL_API_KEY"]) },
        Models = BuiltinCatalog.All("mistral"),
        Api = LazyApis.MistralConversationsApi(),
    });
}
