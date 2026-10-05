using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Vercel AI Gateway provider。对应 TS <c>providers/vercel-ai-gateway.ts</c> 的
/// <c>vercelAIGatewayProvider()</c>。
/// </summary>
public static class VercelAiGateway
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "vercel-ai-gateway",
        Name = "Vercel AI Gateway",
        BaseUrl = "https://ai-gateway.vercel.sh",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Vercel AI Gateway API key", ["AI_GATEWAY_API_KEY"]) },
        Models = BuiltinCatalog.All("vercel-ai-gateway"),
        Api = LazyApis.AnthropicMessagesApi(),
        // AI Gateway 在 /typesafe/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = LazyApis.TypesafeSystemOneApi(),
        },
    });
}
