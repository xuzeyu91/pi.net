using System.Text.Json.Nodes;

using Pi.Ai.Utils;
namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// Anthropic OAuth 流（Claude Pro/Max）。对应 TS auth/oauth/anthropic.ts：
/// browser（loopback 回调 + 手动粘贴竞速）与 copy_code（无头纯手动）两种方式。
/// </summary>
public sealed class AnthropicOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    // atob("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl")，保持与源码相同的编码形态。
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const int CallbackPort = 53692;
    private const string CallbackPath = "/callback";
    private const string RedirectUri = "http://localhost:53692/callback";
    private const string CopyCodeRedirectUri = "https://platform.claude.com/oauth/code/callback";
    private const string BrowserLoginMethod = "browser";
    private const string CopyCodeLoginMethod = "copy_code";
    private const string Scopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    private static string CallbackHost => ProviderEnv.GetValue("PI_OAUTH_CALLBACK_HOST") ?? "127.0.0.1";

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "Anthropic (Claude Pro/Max)";

    public bool IsSubscription => true;

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var method = await interaction.PromptAsync(
            new AuthPrompt.Select("Select Anthropic login method:",
            [
                new AuthSelectOption(BrowserLoginMethod, "Browser login (default)"),
                new AuthSelectOption(CopyCodeLoginMethod, "Copy code login (headless)"),
            ]), cancellationToken).ConfigureAwait(false);

        return method switch
        {
            CopyCodeLoginMethod => await LoginByCopyCodeAsync(interaction, cancellationToken).ConfigureAwait(false),
            BrowserLoginMethod => await LoginByBrowserAsync(interaction, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown Anthropic login method: {method}"),
        };
    }

    private async Task<Credential.OAuth> LoginByBrowserAsync(
        ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var pkce = Pkce.Generate();
        OAuthCallbackServer<string>? callback = null;
        try
        {
            try
            {
                callback = OAuthCallbackServer<string>.Start(new OAuthCallbackServerOptions<string>
                {
                    ProviderName = "Anthropic",
                    Host = CallbackHost,
                    Port = CallbackPort,
                    Path = CallbackPath,
                    State = pkce.Verifier,
                    Complete = code => Task.FromResult(code),
                    Signal = interaction.Signal,
                });
            }
            catch
            {
                // 端口不可用（对齐 TS .catch(() => undefined)）：退化为纯手动输入。
            }

            var authorizeUrl = await BuildAuthorizeUrlAsync(RedirectUri, pkce.Challenge, pkce.Verifier, cancellationToken).ConfigureAwait(false);
            interaction.Notify(new AuthEvent.AuthUrl(authorizeUrl,
                "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here."));

            var result = await OAuthCallbackFlow.WaitForCallbackOrManualInput(
                interaction, callback,
                new ManualPrompt(
                    "Complete login in your browser, or paste the authorization code / redirect URL here:",
                    RedirectUri)).ConfigureAwait(false);

            string? code;
            var state = pkce.Verifier;
            if (result is CallbackOrManualInput<string>.Callback browserCallback)
            {
                code = browserCallback.Value;
            }
            else
            {
                var input = ((CallbackOrManualInput<string>.Manual)result).Input;
                var parsed = OAuthInput.ParseAuthorizationInput(input);
                if (parsed.State is { } pastedState && pastedState != pkce.Verifier)
                    throw new InvalidOperationException("OAuth state mismatch");
                code = parsed.Code;
                state = parsed.State ?? pkce.Verifier;
            }

            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for tokens..."));
            return await ExchangeAuthorizationCodeAsync(
                code, state, pkce.Verifier, RedirectUri, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            callback?.Dispose();
        }
    }

    private async Task<Credential.OAuth> LoginByCopyCodeAsync(
        ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var pkce = Pkce.Generate();
        var authorizeUrl = await BuildAuthorizeUrlAsync(CopyCodeRedirectUri, pkce.Challenge, pkce.Verifier, cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.AuthUrl(authorizeUrl,
            "Complete login in your browser, then copy the code Anthropic shows and paste it here."));

        var input = await interaction.PromptAsync(
            new AuthPrompt.ManualCode(
                "Paste the code Anthropic shows after you sign in:",
                "code#state"), cancellationToken).ConfigureAwait(false);
        var parsed = OAuthInput.ParseAuthorizationInput(input);
        if (parsed.State is { } pastedState && pastedState != pkce.Verifier)
            throw new InvalidOperationException("OAuth state mismatch");
        if (string.IsNullOrEmpty(parsed.Code)) throw new InvalidOperationException("Missing authorization code");
        interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for tokens..."));
        return await ExchangeAuthorizationCodeAsync(
            parsed.Code!, parsed.State ?? pkce.Verifier, pkce.Verifier, CopyCodeRedirectUri,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>刷新 Anthropic OAuth 令牌。对应 TS <c>refreshAnthropicToken</c>。</summary>
    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        string responseBody;
        try
        {
            responseBody = await PostJsonAsync(TokenUrl, new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = credential.Refresh,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Anthropic token refresh request failed. url={TokenUrl}; details={FormatErrorDetails(error)}");
        }

        var json = ParseJsonObject(responseBody);
        if (json?.Str("access_token") is not { } access
            || json.Str("refresh_token") is not { } refresh
            || json.Num("expires_in") is not { } expiresIn)
        {
            throw new InvalidOperationException(
                $"Anthropic token refresh returned invalid JSON. url={TokenUrl}; body={responseBody}");
        }

        return new Credential.OAuth(refresh, access, Now() + (long)(expiresIn * 1000) - 5 * 60 * 1000);
    }

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private async Task<Credential.OAuth> ExchangeAuthorizationCodeAsync(
        string code, string state, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        string responseBody;
        try
        {
            responseBody = await PostJsonAsync(TokenUrl, new JsonObject
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["state"] = state,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Token exchange request failed. url={TokenUrl}; redirect_uri={redirectUri}; response_type=authorization_code; details={FormatErrorDetails(error)}");
        }

        var json = ParseJsonObject(responseBody);
        if (json?.Str("access_token") is not { } access
            || json.Str("refresh_token") is not { } refresh
            || json.Num("expires_in") is not { } expiresIn)
        {
            throw new InvalidOperationException(
                $"Token exchange returned invalid JSON. url={TokenUrl}; body={responseBody}");
        }

        return new Credential.OAuth(refresh, access, Now() + (long)(expiresIn * 1000) - 5 * 60 * 1000);
    }

    private static async Task<string> BuildAuthorizeUrlAsync(
        string redirectUri, string challenge, string state, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var queryString = await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return $"{AuthorizeUrl}?{queryString}";
    }

    private async Task<string> PostJsonAsync(string url, JsonObject body, CancellationToken cancellationToken)
    {
        var response = await OAuthHttp.PostJsonAsync(_client, url, body, cancellationToken).ConfigureAwait(false);
        var responseBody = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP request failed. status={(int)response.StatusCode}; url={url}; body={responseBody}");
        }
        return responseBody;
    }

    private static JsonObject? ParseJsonObject(string json)
        => json.Trim().Length == 0 ? null : TryParse(json);

    private static JsonObject? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>错误细节格式化（TS <c>formatErrorDetails</c> 的等价：类型名 + 消息 + 内层 cause）。</summary>
    private static string FormatErrorDetails(Exception error)
    {
        var details = new List<string> { $"{error.GetType().Name}: {error.Message}" };
        if (error.InnerException is { } inner) details.Add($"cause={FormatErrorDetails(inner)}");
        return string.Join("; ", details);
    }
}
