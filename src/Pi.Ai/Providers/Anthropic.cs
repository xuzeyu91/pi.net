using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// Anthropic 认证：存储 key → <c>ANTHROPIC_AUTH_TOKEN</c>（以 Authorization: Bearer 传递）
/// → <c>ANTHROPIC_OAUTH_TOKEN</c>/<c>ANTHROPIC_API_KEY</c> → 工作负载身份联合（最后兜底）。
/// 对应 TS <c>providers/anthropic.ts</c> 的 <c>anthropicApiKeyAuth</c>。
/// </summary>
public sealed class AnthropicApiKeyAuth : IApiKeyAuth
{
    public string Name => "Anthropic API key";

    public bool HasLogin => true;

    public async Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        interaction.ThrowIfAborted();
        var key = await interaction.PromptAsync(
            new AuthPrompt.Secret("Enter Anthropic API key"), cancellationToken).ConfigureAwait(false);
        interaction.ThrowIfAborted();
        return new Credential.ApiKey(key);
    }

    public async Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
        CancellationToken cancellationToken = default)
    {
        async Task<string?> Env(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await ctx.EnvAsync(name, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(credential?.Key))
        {
            return new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential.Key },
                Env = credential.Env,
                Source = "stored credential",
            };
        }

        var authToken = await Env(AnthropicEnv.AuthToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(authToken))
        {
            return new AuthResult
            {
                Auth = new ModelAuth
                {
                    Headers = new Dictionary<string, string?> { ["Authorization"] = $"Bearer {authToken}" },
                },
                Source = AnthropicEnv.AuthToken,
            };
        }

        foreach (var envVar in new[] { AnthropicEnv.OAuthToken, AnthropicEnv.ApiKey })
        {
            var apiKey = await Env(envVar).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(apiKey))
            {
                return new AuthResult { Auth = new ModelAuth { ApiKey = apiKey }, Source = envVar };
            }
        }

        // 工作负载身份联合：Anthropic SDK 自行用身份令牌换取短期访问令牌并刷新。
        // 放在最后，让 key 与 ANTHROPIC_AUTH_TOKEN 优先（与 SDK 一致）。这些 id 属于
        // provider 配置而非认证，因此走 env 传递。
        var federation = new Dictionary<string, string>();
        foreach (var envVar in new[]
                 {
                     AnthropicEnv.FederationRuleId, AnthropicEnv.OrganizationId,
                     AnthropicEnv.IdentityTokenFile,
                 })
        {
            var value = await Env(envVar).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value)) return null;
            federation[envVar] = value;
        }
        foreach (var envVar in new[] { AnthropicEnv.ServiceAccountId, AnthropicEnv.WorkspaceId })
        {
            var value = await Env(envVar).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(value)) federation[envVar] = value;
        }
        return new AuthResult
        {
            Auth = new ModelAuth(),
            Env = federation,
            Source = "workload identity federation",
        };
    }
}

/// <summary>
/// Anthropic provider。对应 TS <c>providers/anthropic.ts</c> 的 <c>anthropicProvider()</c>。
/// </summary>
public static class Anthropic
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "anthropic",
        Name = "Anthropic",
        BaseUrl = "https://api.anthropic.com",
        Auth = new ProviderAuth
        {
            ApiKey = new AnthropicApiKeyAuth(),
            OAuth = new LazyOAuthAuth("Anthropic (Claude Pro/Max)", OAuthFlows.LoadAnthropic, isSubscription: true),
        },
        Models = BuiltinCatalog.All("anthropic"),
        Api = LazyApis.AnthropicMessagesApi(),
    });
}
