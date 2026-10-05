using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Qwen Token Plan CN provider。对应 TS <c>providers/qwen-token-plan-cn.ts</c> 的 <c>qwenTokenPlanCnProvider()</c>。
/// </summary>
public static class QwenTokenPlanCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan-cn",
        Name = "Qwen Token Plan CN",
        BaseUrl = "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Qwen Token Plan CN API key", ["QWEN_TOKEN_PLAN_CN_API_KEY"]) },
        Models = BuiltinCatalog.All("qwen-token-plan-cn"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
