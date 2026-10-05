using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// DeepSeek provider。对应 TS <c>providers/deepseek.ts</c> 的 <c>deepseekProvider()</c>。
/// </summary>
public static class Deepseek
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "deepseek",
        Name = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("DeepSeek API key", ["DEEPSEEK_API_KEY"]) },
        Models = BuiltinCatalog.All("deepseek"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
