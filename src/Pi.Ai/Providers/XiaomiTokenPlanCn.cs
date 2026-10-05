using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Xiaomi Token Plan CN provider。对应 TS <c>providers/xiaomi-token-plan-cn.ts</c> 的 <c>xiaomiTokenPlanCnProvider()</c>。
/// </summary>
public static class XiaomiTokenPlanCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "xiaomi-token-plan-cn",
        Name = "Xiaomi Token Plan CN",
        BaseUrl = "https://token-plan-cn.xiaomimimo.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Xiaomi Token Plan CN API key", ["XIAOMI_TOKEN_PLAN_CN_API_KEY"]) },
        Models = BuiltinCatalog.All("xiaomi-token-plan-cn"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
