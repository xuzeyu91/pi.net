using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Baseten provider。对应 TS <c>providers/baseten.ts</c> 的 <c>basetenProvider()</c>。
/// </summary>
public static class Baseten
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "baseten",
        Name = "Baseten",
        BaseUrl = "https://inference.baseten.co/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Baseten API key", ["BASETEN_API_KEY"]) },
        Models = BuiltinCatalog.All("baseten"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
