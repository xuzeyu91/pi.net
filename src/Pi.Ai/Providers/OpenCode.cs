using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenCode Zen provider。对应 TS <c>providers/opencode.ts</c> 的 <c>opencodeProvider()</c>：
/// 四家 chat API（均带会话路由头）+ TypeSafe System One 分类。
/// </summary>
public static class OpenCode
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "opencode",
        Name = "OpenCode Zen",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("OpenCode API key", ["OPENCODE_API_KEY"]) },
        Models = BuiltinCatalog.All("opencode"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.AnthropicMessagesApi()),
            ["google-generative-ai"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.GoogleGenerativeAiApi()),
            ["openai-completions"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.OpenAiCompletionsApi()),
            ["openai-responses"] = OpenCodeHeaders.WithOpenCodeSessionHeader(LazyApis.OpenAiResponsesApi()),
        },
        // OpenCode Zen 在 /zen/v1/systemone 提供 TypeSafe System One 协议。
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = LazyApis.TypesafeSystemOneApi(),
        },
    });
}
