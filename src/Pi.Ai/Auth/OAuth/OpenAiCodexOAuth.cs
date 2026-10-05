using System.Buffers.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// OpenAI Codex（ChatGPT OAuth）流。对应 TS auth/oauth/openai-codex.ts：
/// browser（端口 1455 回调 + 手动竞速）与 device_code（无头）两种方式；
/// access token 的 JWT 里必须带 ChatGPT 账户 id。
/// </summary>
public sealed class OpenAiCodexOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthBaseUrl = "https://auth.openai.com";
    private const string AuthorizeUrl = $"{AuthBaseUrl}/oauth/authorize";
    private const string TokenUrl = $"{AuthBaseUrl}/oauth/token";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string DeviceUserCodeUrl = $"{AuthBaseUrl}/api/accounts/deviceauth/usercode";
    private const string DeviceTokenUrl = $"{AuthBaseUrl}/api/accounts/deviceauth/token";
    private const string DeviceVerificationUri = $"{AuthBaseUrl}/codex/device";
    private const string DeviceRedirectUri = $"{AuthBaseUrl}/deviceauth/callback";
    private const int DeviceCodeTimeoutSeconds = 15 * 60;
    private const string BrowserLoginMethod = "browser";
    private const string DeviceCodeLoginMethod = "device_code";
    private const string Scope = "openid profile email offline_access";
    private const string JwtClaimPath = "https://api.openai.com/auth";

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "OpenAI (ChatGPT Plus/Pro)";

    public bool IsSubscription => true;

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var method = await interaction.PromptAsync(
            new AuthPrompt.Select("Select OpenAI Codex login method:",
            [
                new AuthSelectOption(BrowserLoginMethod, "Browser login (default)"),
                new AuthSelectOption(DeviceCodeLoginMethod, "Device code login (headless)"),
            ]), cancellationToken).ConfigureAwait(false);

        return method switch
        {
            DeviceCodeLoginMethod => await LoginByDeviceCodeAsync(interaction, cancellationToken).ConfigureAwait(false),
            BrowserLoginMethod => await LoginByBrowserAsync(interaction, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown OpenAI Codex login method: {method}"),
        };
    }

    /// <summary>刷新 OpenAI Codex OAuth 令牌。对应 TS <c>refreshOpenAICodexToken</c>。</summary>
    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await OAuthHttp.PostFormAsync(_client, TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.Refresh,
                ["client_id"] = ClientId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"OpenAI Codex token refresh error: {error.Message}");
        }

        var token = await ReadTokenResponseAsync(response, "refresh", cancellationToken).ConfigureAwait(false);
        return CredentialsFromToken(token);
    }

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private async Task<Credential.OAuth> LoginByBrowserAsync(
        ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var flow = await CreateAuthorizationFlowAsync().ConfigureAwait(false);
        // 端口 1455 与 Codex CLI 共享；被占用时回退到手动粘贴重定向 URL。
        OAuthCallbackServer<string>? callback = null;
        try
        {
            try
            {
                callback = OAuthCallbackServer<string>.Start(new OAuthCallbackServerOptions<string>
                {
                    ProviderName = "OpenAI",
                    Host = CallbackHost,
                    Port = 1455,
                    Path = "/auth/callback",
                    State = flow.State,
                    Complete = code => Task.FromResult(code),
                    Signal = interaction.Signal,
                });
            }
            catch
            {
                // 对齐 TS .catch(() => undefined)。
            }

            interaction.Notify(new AuthEvent.AuthUrl(flow.Url,
                "A browser window should open. Complete login to finish."));

            var result = await OAuthCallbackFlow.WaitForCallbackOrManualInput(
                interaction, callback,
                new ManualPrompt(
                    "Complete login in your browser, or paste the authorization code / redirect URL here:",
                    RedirectUri)).ConfigureAwait(false);

            string? code;
            if (result is CallbackOrManualInput<string>.Callback browserCallback)
            {
                code = browserCallback.Value;
            }
            else
            {
                var parsed = OAuthInput.ParseAuthorizationInput(((CallbackOrManualInput<string>.Manual)result).Input);
                if (parsed.State is { } pastedState && pastedState != flow.State)
                    throw new InvalidOperationException("State mismatch");
                code = parsed.Code;
            }

            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
            return await ExchangeAuthorizationCodeForCredentialsAsync(
                code, flow.Verifier, RedirectUri, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            callback?.Dispose();
        }
    }

    private async Task<Credential.OAuth> LoginByDeviceCodeAsync(
        ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var device = await StartDeviceAuthAsync(cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.DeviceCode(
            device.UserCode, DeviceVerificationUri, device.IntervalSeconds, DeviceCodeTimeoutSeconds));
        var code = await PollDeviceAuthAsync(device, cancellationToken).ConfigureAwait(false);
        return await ExchangeAuthorizationCodeForCredentialsAsync(
            code.AuthorizationCode, code.CodeVerifier, DeviceRedirectUri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Verifier, string State, string Url)> CreateAuthorizationFlowAsync()
    {
        var pkce = Pkce.Generate();
        var state = OAuthInput.RandomHexState();

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "pi",
        };
        var queryString = await new FormUrlEncodedContent(query).ReadAsStringAsync().ConfigureAwait(false);
        return (pkce.Verifier, state, $"{AuthorizeUrl}?{queryString}");
    }

    private async Task<Credential.OAuth> ExchangeAuthorizationCodeForCredentialsAsync(
        string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var response = await OAuthHttp.PostFormAsync(_client, TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, cancellationToken).ConfigureAwait(false);
        var token = await ReadTokenResponseAsync(response, "exchange", cancellationToken).ConfigureAwait(false);
        return CredentialsFromToken(token);
    }

    private sealed record OAuthToken(string Access, string Refresh, long Expires);

    private async Task<OAuthToken> ReadTokenResponseAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenAI Codex token {operation} failed ({(int)response.StatusCode}): {(text.Length > 0 ? text : response.ReasonPhrase ?? "")}");
        }

        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        var access = json?.Str("access_token");
        var refresh = json?.Str("refresh_token");
        var expiresIn = json?.Num("expires_in");
        if (access is null || access.Length == 0 || refresh is null || refresh.Length == 0 || expiresIn is not { } expires)
        {
            throw new InvalidOperationException(
                $"OpenAI Codex token {operation} response missing fields: {json?.ToJsonString() ?? "null"}");
        }

        return new OAuthToken(access, refresh, Now() + (long)(expires * 1000));
    }

    private sealed record DeviceAuthInfo(string DeviceAuthId, string UserCode, int IntervalSeconds);

    private async Task<DeviceAuthInfo> StartDeviceAuthAsync(CancellationToken cancellationToken)
    {
        var response = await OAuthHttp.PostJsonAsync(_client, DeviceUserCodeUrl,
            new JsonObject { ["client_id"] = ClientId }, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 404)
            {
                throw new InvalidOperationException(
                    "OpenAI Codex device code login is not enabled for this server. Use browser login or verify the server URL.");
            }
            var responseBody = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenAI Codex device code request failed with status {(int)response.StatusCode}{(responseBody.Length > 0 ? $": {responseBody}" : "")}");
        }

        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        var interval = json?.Num("interval");
        double? intervalSeconds = json?.Str("interval") is { } intervalText
            ? double.TryParse(intervalText.Trim(), out var parsedText) ? parsedText : null
            : interval;
        if (json?.Str("device_auth_id") is not { } deviceAuthId || deviceAuthId.Length == 0
            || json.Str("user_code") is not { } userCode || userCode.Length == 0
            || intervalSeconds is not { } intervalValue || double.IsNaN(intervalValue) || intervalValue < 0)
        {
            throw new InvalidOperationException(
                $"Invalid OpenAI Codex device code response: {json?.ToJsonString() ?? "null"}");
        }

        return new DeviceAuthInfo(deviceAuthId, userCode, (int)intervalValue);
    }

    private sealed record DeviceTokenSuccess(string AuthorizationCode, string CodeVerifier);

    private async Task<DeviceTokenSuccess> PollDeviceAuthAsync(
        DeviceAuthInfo device, CancellationToken cancellationToken)
    {
        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<DeviceTokenSuccess>
        {
            IntervalSeconds = device.IntervalSeconds,
            ExpiresInSeconds = DeviceCodeTimeoutSeconds,
            Signal = cancellationToken,
            Poll = async () =>
            {
                var response = await OAuthHttp.PostJsonAsync(_client, DeviceTokenUrl, new JsonObject
                {
                    ["device_auth_id"] = device.DeviceAuthId,
                    ["user_code"] = device.UserCode,
                }, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
                    var authorizationCode = json?.Str("authorization_code");
                    var codeVerifier = json?.Str("code_verifier");
                    if (authorizationCode is null || authorizationCode.Length == 0
                        || codeVerifier is null || codeVerifier.Length == 0)
                    {
                        return new DeviceCodePollResult<DeviceTokenSuccess>.Failed(
                            $"Invalid OpenAI Codex device auth token response: {json?.ToJsonString() ?? "null"}");
                    }
                    return new DeviceCodePollResult<DeviceTokenSuccess>.Complete(
                        new DeviceTokenSuccess(authorizationCode, codeVerifier));
                }

                if ((int)response.StatusCode is 403 or 404)
                {
                    return new DeviceCodePollResult<DeviceTokenSuccess>.Pending();
                }

                var responseBody = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
                string? errorCode = null;
                try
                {
                    var errorJson = JsonNode.Parse(responseBody) as JsonObject;
                    var error = errorJson?["error"];
                    errorCode = error switch
                    {
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        JsonObject obj => obj.Str("code"),
                        _ => null,
                    };
                }
                catch
                {
                    // 非 JSON 响应体
                }

                if (errorCode == "deviceauth_authorization_pending")
                    return new DeviceCodePollResult<DeviceTokenSuccess>.Pending();
                if (errorCode == "slow_down")
                    return new DeviceCodePollResult<DeviceTokenSuccess>.SlowDown();

                return new DeviceCodePollResult<DeviceTokenSuccess>.Failed(
                    $"OpenAI Codex device auth failed with status {(int)response.StatusCode}{(responseBody.Length > 0 ? $": {responseBody}" : "")}");
            },
        }).ConfigureAwait(false);
    }

    private Credential.OAuth CredentialsFromToken(OAuthToken token)
    {
        var accountId = GetAccountId(token.Access);
        if (accountId is null)
        {
            throw new InvalidOperationException("Failed to extract accountId from token");
        }

        return new Credential.OAuth(token.Refresh, token.Access, token.Expires) { AccountId = accountId };
    }

    /// <summary>解码 JWT payload 并取 ChatGPT 账户 id。对应 TS <c>decodeJwt</c>+<c>getAccountId</c>。</summary>
    internal static string? GetAccountId(string accessToken)
    {
        var payload = DecodeJwtPayload(accessToken);
        if (payload is null) return null;
        var accountId = payload.Obj(JwtClaimPath)?.Str("chatgpt_account_id");
        return accountId is { Length: > 0 } ? accountId : null;
    }

    private static JsonObject? DecodeJwtPayload(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1];
            var bytes = Base64Url.DecodeFromChars(payload);
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string CallbackHost => ProviderEnv.GetValue("PI_OAUTH_CALLBACK_HOST") ?? "127.0.0.1";
}
