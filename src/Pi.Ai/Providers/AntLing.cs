using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Ant Ling provider。对应 TS <c>providers/ant-ling.ts</c> 的 <c>antLingProvider()</c>。
/// </summary>
public static class AntLing
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "ant-ling",
        Name = "Ant Ling",
        BaseUrl = "https://api.ant-ling.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Ant Ling API key", ["ANT_LING_API_KEY"]) },
        Models = BuiltinCatalog.All("ant-ling"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
