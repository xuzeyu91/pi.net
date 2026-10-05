using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Cerebras provider。对应 TS <c>providers/cerebras.ts</c> 的 <c>cerebrasProvider()</c>。
/// </summary>
public static class Cerebras
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "cerebras",
        Name = "Cerebras",
        BaseUrl = "https://api.cerebras.ai/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Cerebras API key", ["CEREBRAS_API_KEY"]) },
        Models = BuiltinCatalog.All("cerebras"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
