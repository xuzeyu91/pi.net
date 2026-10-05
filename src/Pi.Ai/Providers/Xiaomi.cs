using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Xiaomi provider。对应 TS <c>providers/xiaomi.ts</c> 的 <c>xiaomiProvider()</c>。
/// </summary>
public static class Xiaomi
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xiaomi",
        Name = "Xiaomi",
        BaseUrl = "https://api.xiaomimimo.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Xiaomi API key", ["XIAOMI_API_KEY"]) },
        Models = BuiltinCatalog.All("xiaomi"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
