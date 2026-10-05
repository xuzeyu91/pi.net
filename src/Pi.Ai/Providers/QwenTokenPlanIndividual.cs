using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Qwen Token Plan Individual provider。对应 TS <c>providers/qwen-token-plan-individual.ts</c> 的 <c>qwenTokenPlanIndividualProvider()</c>。
/// </summary>
public static class QwenTokenPlanIndividual
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan-individual",
        Name = "Qwen Token Plan Individual",
        BaseUrl = "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Qwen Token Plan Individual API key", ["QWEN_TOKEN_PLAN_API_KEY"]) },
        Models = BuiltinCatalog.All("qwen-token-plan-individual"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
