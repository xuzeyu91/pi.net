using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// NVIDIA provider。对应 TS <c>providers/nvidia.ts</c> 的 <c>nvidiaProvider()</c>。
/// </summary>
public static class Nvidia
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "nvidia",
        Name = "NVIDIA",
        BaseUrl = "https://integrate.api.nvidia.com/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("NVIDIA API key", ["NVIDIA_API_KEY"]) },
        Models = BuiltinCatalog.All("nvidia"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
