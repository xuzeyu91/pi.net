using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

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

/// <summary>
/// Google Vertex AI provider。对应 TS <c>providers/google-vertex.ts</c> 的
/// <c>googleVertexProvider()</c>。
/// </summary>
public static class GoogleVertex
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "google-vertex",
        Name = "Google Vertex AI",
        Auth = new ProviderAuth { ApiKey = new VertexAuth() },
        Models = BuiltinCatalog.All("google-vertex"),
        Api = LazyApis.GoogleVertexApi(),
    });
}
