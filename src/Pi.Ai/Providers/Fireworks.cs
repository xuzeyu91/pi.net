using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Fireworks provider。对应 TS <c>providers/fireworks.ts</c> 的 <c>fireworksProvider()</c>：
/// 同时暴露 anthropic-messages 与 openai-completions 两套 API。
/// </summary>
public static class Fireworks
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "fireworks",
        Name = "Fireworks",
        BaseUrl = "https://api.fireworks.ai/inference",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Fireworks API key", ["FIREWORKS_API_KEY"]) },
        Models = BuiltinCatalog.All("fireworks"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = LazyApis.AnthropicMessagesApi(),
            ["openai-completions"] = LazyApis.OpenAiCompletionsApi(),
        },
    });
}
