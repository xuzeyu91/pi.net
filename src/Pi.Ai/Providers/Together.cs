using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Together provider。对应 TS <c>providers/together.ts</c> 的 <c>togetherProvider()</c>。
/// </summary>
public static class Together
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "together",
        Name = "Together",
        BaseUrl = "https://api.together.ai/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Together API key", ["TOGETHER_API_KEY"]) },
        Models = BuiltinCatalog.All("together"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
