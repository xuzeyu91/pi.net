using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Xiaomi Token Plan AMS provider。对应 TS <c>providers/xiaomi-token-plan-ams.ts</c> 的 <c>xiaomiTokenPlanAmsProvider()</c>。
/// </summary>
public static class XiaomiTokenPlanAms
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xiaomi-token-plan-ams",
        Name = "Xiaomi Token Plan AMS",
        BaseUrl = "https://token-plan-ams.xiaomimimo.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Xiaomi Token Plan AMS API key", ["XIAOMI_TOKEN_PLAN_AMS_API_KEY"]) },
        Models = BuiltinCatalog.All("xiaomi-token-plan-ams"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
