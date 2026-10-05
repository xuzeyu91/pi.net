using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Cloudflare AI Gateway provider。对应 TS <c>providers/cloudflare-ai-gateway.ts</c> 的
/// <c>cloudflareAIGatewayProvider()</c>。
/// </summary>
public static class CloudflareAiGateway
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "cloudflare-ai-gateway",
        Name = "Cloudflare AI Gateway",
        Auth = new ProviderAuth { ApiKey = new CloudflareAuth(CloudflareAuthKind.AiGateway) },
        Models = BuiltinCatalog.All("cloudflare-ai-gateway"),
        // api 映射固定三家：models.dev 的网关目录会反复增删 workers-ai/* 条目，
        // 仅靠 models 推断会在目录恰好为空时拒绝 openai-completions 条目。
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = CloudflareStreamWrappers.WithCloudflare(LazyApis.AnthropicMessagesApi()),
            ["openai-completions"] = CloudflareStreamWrappers.WithCloudflare(LazyApis.OpenAiCompletionsApi()),
            ["openai-responses"] = CloudflareStreamWrappers.WithCloudflare(LazyApis.OpenAiResponsesApi()),
        },
    });
}
