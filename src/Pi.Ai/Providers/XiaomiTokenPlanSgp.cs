using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Xiaomi Token Plan SGP provider。对应 TS <c>providers/xiaomi-token-plan-sgp.ts</c> 的 <c>xiaomiTokenPlanSgpProvider()</c>。
/// </summary>
public static class XiaomiTokenPlanSgp
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xiaomi-token-plan-sgp",
        Name = "Xiaomi Token Plan SGP",
        BaseUrl = "https://token-plan-sgp.xiaomimimo.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Xiaomi Token Plan SGP API key", ["XIAOMI_TOKEN_PLAN_SGP_API_KEY"]) },
        Models = BuiltinCatalog.All("xiaomi-token-plan-sgp"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
