using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Moonshot AI CN provider。对应 TS <c>providers/moonshotai-cn.ts</c> 的 <c>moonshotaiCnProvider()</c>。
/// </summary>
public static class MoonshotAiCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "moonshotai-cn",
        Name = "Moonshot AI CN",
        BaseUrl = "https://api.moonshot.cn/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Moonshot AI API key", ["MOONSHOT_API_KEY"]) },
        Models = BuiltinCatalog.All("moonshotai-cn"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
