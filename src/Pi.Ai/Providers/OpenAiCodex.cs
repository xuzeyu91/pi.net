using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenAI Codex（legacy）provider。对应 TS <c>providers/openai-codex.ts</c> 的
/// <c>openaiCodexProvider()</c>：仅 OAuth 认证。
/// </summary>
public static class OpenAiCodex
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openai-codex",
        Name = "OpenAI Codex (legacy)",
        BaseUrl = "https://chatgpt.com/backend-api",
        Auth = new ProviderAuth
        {
            OAuth = new LazyOAuthAuth("OpenAI (ChatGPT Plus/Pro)", OAuthFlows.LoadOpenAICodex, isSubscription: true),
        },
        Models = BuiltinCatalog.All("openai-codex"),
        Api = LazyApis.OpenAiCodexResponsesApi(),
    });
}
