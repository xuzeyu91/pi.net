using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Google provider。对应 TS <c>providers/google.ts</c> 的 <c>googleProvider()</c>。
/// </summary>
public static class Google
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "google",
        Name = "Google",
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Gemini API key", ["GEMINI_API_KEY"]) },
        Models = BuiltinCatalog.All("google"),
        Api = LazyApis.GoogleGenerativeAiApi(),
    });
}
