using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// GitHub Copilot provider。对应 TS <c>providers/github-copilot.ts</c> 的
/// <c>githubCopilotProvider()</c>：三家 API 混合 + 按登录后 availableModelIds 收敛模型。
/// </summary>
public static class GitHubCopilot
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "github-copilot",
        Name = "GitHub Copilot",
        BaseUrl = "https://api.individual.githubcopilot.com",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("GitHub Copilot token", ["COPILOT_GITHUB_TOKEN"]),
            OAuth = new LazyOAuthAuth("GitHub Copilot", OAuthFlows.LoadGitHubCopilot, isSubscription: true),
        },
        Models = BuiltinCatalog.All("github-copilot"),
        ApiByApi = new Dictionary<string, ProviderStreams>
        {
            ["anthropic-messages"] = LazyApis.AnthropicMessagesApi(),
            ["openai-completions"] = LazyApis.OpenAiCompletionsApi(),
            ["openai-responses"] = LazyApis.OpenAiResponsesApi(),
        },
        // Copilot 登录后按 availableModelIds 收敛可用 chat 模型。
        FilterModels = static (models, credential) =>
        {
            if (credential is not Credential.OAuth oauth) return models;
            var availableModelIds = oauth.AvailableModelIds;
            if (availableModelIds is null) return models;
            var available = new HashSet<string>(availableModelIds, StringComparer.Ordinal);
            return models.Where(model => available.Contains(model.Id)).ToList();
        },
    });
}
