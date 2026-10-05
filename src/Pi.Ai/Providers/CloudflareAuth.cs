using Pi.Ai.Auth;

namespace Pi.Ai.Providers;

/// <summary>Cloudflare 认证种类。对应 TS <c>CloudflareAuthKind</c>。</summary>
public enum CloudflareAuthKind
{
    WorkersAi,
    AiGateway,
}

/// <summary>
/// Cloudflare Workers AI / AI Gateway 认证：按字段合并（凭据值优先，回退 ambient env——
/// 只带 API key 的凭据仍能从环境取 account/gateway id）。对应 TS <c>providers/cloudflare-auth.ts</c>。
/// </summary>
public sealed class CloudflareAuth(CloudflareAuthKind kind) : IApiKeyAuth
{
    public const string ApiKeyEnv = "CLOUDFLARE_API_KEY";
    public const string AccountIdEnv = "CLOUDFLARE_ACCOUNT_ID";
    public const string GatewayIdEnv = "CLOUDFLARE_GATEWAY_ID";

    public string Name => "Cloudflare API key";

    public bool HasLogin => true;

    public async Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var key = await interaction.PromptAsync(
            new AuthPrompt.Secret("Enter Cloudflare API key"), cancellationToken).ConfigureAwait(false);
        var accountId = await interaction.PromptAsync(
            new AuthPrompt.Text("Enter Cloudflare account ID"), cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string> { [AccountIdEnv] = accountId };
        if (kind == CloudflareAuthKind.AiGateway)
        {
            env[GatewayIdEnv] = await interaction.PromptAsync(
                new AuthPrompt.Text("Enter Cloudflare AI Gateway ID"), cancellationToken).ConfigureAwait(false);
        }
        return new Credential.ApiKey(key, env);
    }

    private static async Task<string?> ResolveValueAsync(string name, IAuthContext ctx,
        Credential.ApiKey? credential, CancellationToken cancellationToken)
    {
        var fromCredential = credential is null
            ? null
            : name == ApiKeyEnv ? credential.Key : credential.Env?.GetValueOrDefault(name);
        if (fromCredential is not null) return fromCredential;
        cancellationToken.ThrowIfCancellationRequested();
        var value = await ctx.EnvAsync(name, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    public async Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await ResolveValueAsync(ApiKeyEnv, ctx, credential, cancellationToken).ConfigureAwait(false);
        var accountId = await ResolveValueAsync(AccountIdEnv, ctx, credential, cancellationToken).ConfigureAwait(false);
        var gatewayId = kind == CloudflareAuthKind.AiGateway
            ? await ResolveValueAsync(GatewayIdEnv, ctx, credential, cancellationToken).ConfigureAwait(false)
            : null;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(accountId)
            || (kind == CloudflareAuthKind.AiGateway && string.IsNullOrEmpty(gatewayId)))
        {
            return null;
        }

        var env = new Dictionary<string, string> { [AccountIdEnv] = accountId! };
        if (!string.IsNullOrEmpty(gatewayId)) env[GatewayIdEnv] = gatewayId!;

        var source = credential is not null ? "stored credential" : ApiKeyEnv;
        if (kind == CloudflareAuthKind.AiGateway)
        {
            // AI Gateway 用 cf-aig-authorization 承载密钥，并显式抑制默认 Authorization/x-api-key。
            return new AuthResult
            {
                Auth = new ModelAuth
                {
                    Headers = new Dictionary<string, string?>
                    {
                        ["cf-aig-authorization"] = $"Bearer {apiKey}",
                        ["Authorization"] = null,
                        ["x-api-key"] = null,
                    },
                },
                Env = env,
                Source = source,
            };
        }
        return new AuthResult
        {
            Auth = new ModelAuth { ApiKey = apiKey },
            Env = env,
            Source = source,
        };
    }
}
