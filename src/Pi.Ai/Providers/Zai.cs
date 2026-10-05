using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Z.AI provider。对应 TS <c>providers/zai.ts</c> 的 <c>zaiProvider()</c>。
/// </summary>
public static class Zai
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "zai",
        Name = "Z.AI",
        BaseUrl = "https://api.z.ai/api/coding/paas/v4",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Z.AI API key", ["ZAI_API_KEY"]) },
        Models = BuiltinCatalog.All("zai"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
