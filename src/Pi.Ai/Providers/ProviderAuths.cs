using Pi.Ai.Auth;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// Amazon Bedrock 认证：接受 bearer token 或 AWS SDK 默认凭据链。登录流可存储
/// token/profile 选择；resolve 也会探测 ambient AWS 凭据（不复制进 pi 的凭据库）。
/// 对应 TS <c>providers/amazon-bedrock.ts</c> 的 <c>bedrockAuth</c>。
/// </summary>
public sealed class BedrockAuth : IApiKeyAuth
{
    public string Name => "AWS credentials or bearer token";

    public bool HasLogin => true;

    public async Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        interaction.ThrowIfAborted();
        var method = await interaction.PromptAsync(new AuthPrompt.Select(
            "Select Amazon Bedrock authentication method:",
            [
                new AuthSelectOption("bearer-token", "Bearer token"),
                new AuthSelectOption("aws-profile", "AWS profile"),
                new AuthSelectOption("credential-chain", "Existing AWS credential chain"),
            ]), cancellationToken).ConfigureAwait(false);
        interaction.ThrowIfAborted();

        if (method == "bearer-token")
        {
            return new Credential.ApiKey(await interaction.PromptAsync(
                new AuthPrompt.Secret("Enter Amazon Bedrock bearer token"), cancellationToken).ConfigureAwait(false));
        }

        interaction.Notify(new AuthEvent.Info(
            "Amazon Bedrock supports AWS profiles, IAM credentials, and role-based credentials.",
            [new AuthInfoLink(
                "https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html",
                "AWS credential provider chain")]));

        if (method == "aws-profile")
        {
            var profile = await interaction.PromptAsync(
                new AuthPrompt.Text("Enter AWS profile name"), cancellationToken).ConfigureAwait(false);
            return new Credential.ApiKey(null, new Dictionary<string, string> { ["AWS_PROFILE"] = profile });
        }

        if (method != "credential-chain")
        {
            throw new InvalidOperationException($"Unknown Amazon Bedrock auth method: {method}");
        }
        await interaction.PromptAsync(
            new AuthPrompt.Text("Configure AWS credentials, then press Enter to continue"), cancellationToken)
            .ConfigureAwait(false);
        return new Credential.ApiKey();
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

        if (!string.IsNullOrEmpty(credential?.Key))
        {
            return new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential.Key },
                Env = credential.Env,
                Source = "stored credential",
            };
        }
        if (await Env("AWS_BEARER_TOKEN_BEDROCK").ConfigureAwait(false) is not null)
        {
            return new AuthResult { Auth = new ModelAuth(), Source = "AWS_BEARER_TOKEN_BEDROCK" };
        }
        var storedProfile = credential?.Env?.GetValueOrDefault("AWS_PROFILE");
        if (storedProfile is not null || await Env("AWS_PROFILE").ConfigureAwait(false) is not null)
        {
            return new AuthResult
            {
                Auth = new ModelAuth(),
                Env = credential?.Env,
                Source = storedProfile is not null ? "stored credential" : "AWS_PROFILE",
            };
        }
        if (await Env("AWS_ACCESS_KEY_ID").ConfigureAwait(false) is not null
            && await Env("AWS_SECRET_ACCESS_KEY").ConfigureAwait(false) is not null)
        {
            return new AuthResult { Auth = new ModelAuth(), Source = "AWS access keys" };
        }
        if (await Env("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI").ConfigureAwait(false) is not null)
        {
            return new AuthResult { Auth = new ModelAuth(), Source = "ECS task role" };
        }
        if (await Env("AWS_CONTAINER_CREDENTIALS_FULL_URI").ConfigureAwait(false) is not null)
        {
            return new AuthResult { Auth = new ModelAuth(), Source = "ECS task role" };
        }
        if (await Env("AWS_WEB_IDENTITY_TOKEN_FILE").ConfigureAwait(false) is not null)
        {
            return new AuthResult { Auth = new ModelAuth(), Source = "web identity token" };
        }
        return null;
    }
}

/// <summary>
/// Google Vertex 认证：显式 API key 或 Application Default Credentials
/// （<c>gcloud auth application-default login</c>）。ADC 还需 project 与 location 环境变量，
/// 由实现自行读取。对应 TS <c>providers/google-vertex.ts</c> 的 <c>vertexAuth</c>。
/// </summary>
public sealed class VertexAuth : IApiKeyAuth
{
    /// <summary>ADC 默认凭据路径。</summary>
    public const string DefaultAdcPath = "~/.config/gcloud/application_default_credentials.json";

    public string Name => "Google Cloud credentials";

    public bool HasLogin => true;

    public async Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        interaction.ThrowIfAborted();
        var method = await interaction.PromptAsync(new AuthPrompt.Select(
            "Select Google Vertex AI authentication method:",
            [
                new AuthSelectOption("api-key", "Google Cloud API key"),
                new AuthSelectOption("adc", "Application Default Credentials"),
                new AuthSelectOption("service-account", "Service account credentials file"),
            ]), cancellationToken).ConfigureAwait(false);
        interaction.ThrowIfAborted();

        if (method == "api-key")
        {
            return new Credential.ApiKey(await interaction.PromptAsync(
                new AuthPrompt.Secret("Enter Google Cloud API key"), cancellationToken).ConfigureAwait(false));
        }
        if (method != "adc" && method != "service-account")
        {
            throw new InvalidOperationException($"Unknown Google Vertex AI auth method: {method}");
        }

        interaction.Notify(new AuthEvent.Info(
            method == "adc"
                ? "Run `gcloud auth application-default login`, then provide the project and location."
                : "Provide a service account credentials file, project, and location.",
            [new AuthInfoLink(
                "https://cloud.google.com/docs/authentication/provide-credentials-adc",
                "Application Default Credentials")]));

        var project = await interaction.PromptAsync(
            new AuthPrompt.Text("Enter Google Cloud project ID"), cancellationToken).ConfigureAwait(false);
        var location = await interaction.PromptAsync(
            new AuthPrompt.Text("Enter Google Cloud location"), cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>
        {
            ["GOOGLE_CLOUD_PROJECT"] = project,
            ["GOOGLE_CLOUD_LOCATION"] = location,
        };
        if (method == "service-account")
        {
            env["GOOGLE_APPLICATION_CREDENTIALS"] = await interaction.PromptAsync(
                new AuthPrompt.Text("Enter service account credentials file path"), cancellationToken)
                .ConfigureAwait(false);
        }
        return new Credential.ApiKey(null, env);
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

        var key = credential?.Key ?? await Env("GOOGLE_CLOUD_API_KEY").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(key))
        {
            return new AuthResult
            {
                Auth = new ModelAuth { ApiKey = key },
                Source = !string.IsNullOrEmpty(credential?.Key) ? "stored credential" : "GOOGLE_CLOUD_API_KEY",
            };
        }

        var adcPath = credential?.Env?.GetValueOrDefault("GOOGLE_APPLICATION_CREDENTIALS")
            ?? await Env("GOOGLE_APPLICATION_CREDENTIALS").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var hasCredentials = await ctx.FileExistsAsync(adcPath ?? DefaultAdcPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var project = credential?.Env?.GetValueOrDefault("GOOGLE_CLOUD_PROJECT")
            ?? await Env("GOOGLE_CLOUD_PROJECT").ConfigureAwait(false)
            ?? await Env("GCLOUD_PROJECT").ConfigureAwait(false);
        var location = credential?.Env?.GetValueOrDefault("GOOGLE_CLOUD_LOCATION")
            ?? await Env("GOOGLE_CLOUD_LOCATION").ConfigureAwait(false);

        if (hasCredentials && !string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(location))
        {
            return new AuthResult
            {
                Auth = new ModelAuth(),
                Env = credential?.Env,
                Source = credential is not null ? "stored credential" : "gcloud application default credentials",
            };
        }
        return null;
    }
}

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
