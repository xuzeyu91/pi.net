using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Hugging Face provider。对应 TS <c>providers/huggingface.ts</c> 的 <c>huggingfaceProvider()</c>。
/// </summary>
public static class HuggingFace
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "huggingface",
        Name = "Hugging Face",
        BaseUrl = "https://router.huggingface.co/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Hugging Face token", ["HF_TOKEN"]) },
        Models = BuiltinCatalog.All("huggingface"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
