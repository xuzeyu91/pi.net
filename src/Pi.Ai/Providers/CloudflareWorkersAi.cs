using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Cloudflare Workers AI provider。对应 TS <c>providers/cloudflare-workers-ai.ts</c> 的
/// <c>cloudflareWorkersAIProvider()</c>。
/// </summary>
public static class CloudflareWorkersAi
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "cloudflare-workers-ai",
        Name = "Cloudflare Workers AI",
        Auth = new ProviderAuth { ApiKey = new CloudflareAuth(CloudflareAuthKind.WorkersAi) },
        Models = BuiltinCatalog.All("cloudflare-workers-ai"),
        Api = CloudflareStreamWrappers.WithCloudflare(LazyApis.OpenAiCompletionsApi()),
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["cloudflare-workers-ai-system-one"] = LazyApis.CloudflareWorkersAiSystemOneApi(),
        },
    });
}
