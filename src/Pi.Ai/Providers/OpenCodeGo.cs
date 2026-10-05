using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenCode Go provider。对应 TS <c>providers/opencode-go.ts</c> 的
/// <c>opencodeGoProvider()</c>。
/// </summary>
public static class OpenCodeGo
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "opencode-go",
        Name = "OpenCode Go",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("OpenCode API key", ["OPENCODE_API_KEY"]) },
        Models = BuiltinCatalog.All("opencode-go"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.AnthropicMessagesApi()),
            ["openai-completions"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.OpenAiCompletionsApi()),
            ["openai-responses"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.OpenAiResponsesApi()),
        },
    });
}
