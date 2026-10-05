using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
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
/// Amazon Bedrock provider。对应 TS <c>providers/amazon-bedrock.ts</c> 的
/// <c>amazonBedrockProvider()</c>。
/// </summary>
public static class AmazonBedrock
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "amazon-bedrock",
        Name = "Amazon Bedrock",
        Auth = new ProviderAuth { ApiKey = new BedrockAuth() },
        Models = BuiltinCatalog.All("amazon-bedrock"),
        Api = LazyApis.BedrockConverseStreamApi(),
    });
}
