using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Moonshot AI provider。对应 TS <c>providers/moonshotai.ts</c> 的 <c>moonshotaiProvider()</c>。
/// </summary>
public static class MoonshotAi
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "moonshotai",
        Name = "Moonshot AI",
        BaseUrl = "https://api.moonshot.ai/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Moonshot AI API key", ["MOONSHOT_API_KEY"]) },
        Models = BuiltinCatalog.All("moonshotai"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
