using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Groq provider。对应 TS <c>providers/groq.ts</c> 的 <c>groqProvider()</c>。
/// </summary>
public static class Groq
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "groq",
        Name = "Groq",
        BaseUrl = "https://api.groq.com/openai/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Groq API key", ["GROQ_API_KEY"]) },
        Models = BuiltinCatalog.All("groq"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
