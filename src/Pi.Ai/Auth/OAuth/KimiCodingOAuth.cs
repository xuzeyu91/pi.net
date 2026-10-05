using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// Kimi Code（订阅）OAuth 流。对应 TS auth/oauth/kimi-coding.ts：对
/// https://auth.kimi.com 的 RFC 8628 设备授权（JSON 响应）；access token
/// 以 Authorization: Bearer 访问 https://api.kimi.com/coding。
/// </summary>
public sealed class KimiCodingOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    private const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    private const string DefaultOauthHost = "https://auth.kimi.com";
    private const int DeviceCodeTimeoutSeconds = 15 * 60;
    private const int DefaultPollIntervalSeconds = 5;
    private const int RequestTimeoutMs = 30 * 1000;
    private const int RefreshMaxRetries = 3;

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>休眠实现（测试注入）。对应 TS sleep。</summary>
    internal Func<int, CancellationToken, Task> SleepAsync { get; init; } = Sleep.DelayAsync;

    public string Name => "Kimi Code (subscription)";

    public bool IsSubscription => true;

    public string? LoginLabel => "Sign in with Kimi Code";

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var oauthHost = GetOauthHost();
        var device = await StartDeviceAuthorizationAsync(oauthHost, cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.DeviceCode(
            device.UserCode, device.VerificationUriComplete, device.IntervalSeconds, device.ExpiresInSeconds));
        var token = await PollForTokenAsync(oauthHost, device, cancellationToken).ConfigureAwait(false);
        return new Credential.OAuth(token.Refresh, token.Access, token.Expires);
    }

    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        var token = await RefreshTokenAsync(GetOauthHost(), credential.Refresh, cancellationToken).ConfigureAwait(false);
        return new Credential.OAuth(token.Refresh, token.Access, token.Expires);
    }

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth
        {
            Headers = new Dictionary<string, string?> { ["Authorization"] = $"Bearer {credential.Access}" },
        });

    private static string GetOauthHost()
    {
        var @override = ProviderEnv.GetValue("KIMI_CODE_OAUTH_HOST") ?? ProviderEnv.GetValue("KIMI_OAUTH_HOST");
        return (@override ?? DefaultOauthHost).TrimEnd('/');
    }

    private sealed record DeviceAuthorization(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string VerificationUriComplete,
        int IntervalSeconds,
        int ExpiresInSeconds);

    private sealed record TokenResponse(string Access, string Refresh, long Expires);

    /// <summary>验证 URI 在用户浏览器里打开；只信任 http(s)。</summary>
    private static bool IsTrustedHttpUrl(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var url = new Uri(value);
            return url.Scheme is "https" or "http";
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(
        string oauthHost, CancellationToken cancellationToken)
    {
        var response = await OAuthHttp.PostFormAsync(_client,
            $"{oauthHost}/api/oauth/device_authorization",
            new Dictionary<string, string> { ["client_id"] = ClientId },
            cancellationToken, RequestTimeoutMs).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var text = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Kimi Code device authorization failed with status {(int)response.StatusCode}{(text.Length > 0 ? $": {text}" : "")}");
        }

        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        var deviceCode = json?.Str("device_code");
        var userCode = json?.Str("user_code");
        var verificationUri = json?.Str("verification_uri");
        var verificationUriComplete = json?.Str("verification_uri_complete");
        if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 }
            || verificationUri is not { Length: > 0 } || verificationUriComplete is not { Length: > 0 }
            || !IsTrustedHttpUrl(verificationUriComplete) || !IsTrustedHttpUrl(verificationUri))
        {
            throw new InvalidOperationException(
                $"Invalid Kimi Code device authorization response: {json?.ToJsonString() ?? "null"}");
        }

        var interval = json!.Num("interval");
        var expiresIn = json!.Num("expires_in");
        return new DeviceAuthorization(
            deviceCode, userCode, verificationUri, verificationUriComplete,
            IntervalSeconds: interval is { } intervalValue && !double.IsNaN(intervalValue) && intervalValue > 0
                ? (int)intervalValue : DefaultPollIntervalSeconds,
            ExpiresInSeconds: expiresIn is { } expiresValue && !double.IsNaN(expiresValue) && expiresValue > 0
                ? (int)expiresValue : DeviceCodeTimeoutSeconds);
    }

    private TokenResponse ParseTokenResponse(JsonObject? json, string operation)
    {
        var accessToken = json?.Str("access_token");
        var refreshToken = json?.Str("refresh_token");
        var expiresIn = json?.Num("expires_in");
        if (accessToken is not { Length: > 0 } || refreshToken is not { Length: > 0 }
            || expiresIn is not { } expiresValue || double.IsNaN(expiresValue) || double.IsInfinity(expiresValue)
            || expiresValue <= 0)
        {
            throw new InvalidOperationException(
                $"Kimi Code token {operation} response missing fields: {json?.ToJsonString() ?? "null"}");
        }
        return new TokenResponse(accessToken, refreshToken, Now() + (long)(expiresValue * 1000));
    }

    private async Task<TokenResponse> PollForTokenAsync(
        string oauthHost, DeviceAuthorization device, CancellationToken cancellationToken)
    {
        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<TokenResponse>
        {
            IntervalSeconds = device.IntervalSeconds,
            ExpiresInSeconds = device.ExpiresInSeconds,
            WaitBeforeFirstPoll = true,
            Signal = cancellationToken,
            Poll = async () =>
            {
                var response = await OAuthHttp.PostFormAsync(_client,
                    $"{oauthHost}/api/oauth/token",
                    new Dictionary<string, string>
                    {
                        ["client_id"] = ClientId,
                        ["device_code"] = device.DeviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    },
                    cancellationToken, RequestTimeoutMs).ConfigureAwait(false);

                if ((int)response.StatusCode >= 500)
                {
                    var text = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
                    return new DeviceCodePollResult<TokenResponse>.Failed(
                        $"Kimi Code device token request failed with status {(int)response.StatusCode}{(text.Length > 0 ? $": {text}" : "")}");
                }

                var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && json?.Str("access_token") is { Length: > 0 })
                {
                    try
                    {
                        return new DeviceCodePollResult<TokenResponse>.Complete(
                            ParseTokenResponse(json, "poll"));
                    }
                    catch (Exception error)
                    {
                        return new DeviceCodePollResult<TokenResponse>.Failed(error.Message);
                    }
                }

                var error2 = json?.Str("error");
                var description = json?.Str("error_description") is { Length: > 0 } d ? $": {d}" : "";
                if (error2 == "authorization_pending")
                    return new DeviceCodePollResult<TokenResponse>.Pending();
                if (error2 == "slow_down")
                {
                    var interval = json?.Num("interval");
                    return new DeviceCodePollResult<TokenResponse>.SlowDown(
                        interval is { } intervalValue && intervalValue > 0 ? (int)intervalValue : null);
                }
                if (error2 == "expired_token")
                {
                    return new DeviceCodePollResult<TokenResponse>.Failed(
                        "Kimi Code device authorization expired. Please restart login.");
                }
                if (error2 == "access_denied")
                {
                    return new DeviceCodePollResult<TokenResponse>.Failed("Kimi Code login was denied.");
                }
                return new DeviceCodePollResult<TokenResponse>.Failed(
                    $"Kimi Code device token request failed (status {(int)response.StatusCode}){((error2 is { Length: > 0 }) ? $": {error2}{description}" : "")}");
            },
        }).ConfigureAwait(false);
    }

    private static bool IsRetryableRefreshFailure(int statusCode) => statusCode == 429 || statusCode >= 500;

    private async Task<TokenResponse> RefreshTokenAsync(
        string oauthHost, string refreshToken, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= RefreshMaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                await SleepAsync(1000 * (int)Math.Pow(2, attempt - 1), cancellationToken).ConfigureAwait(false);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Kimi Code token refresh aborted", cancellationToken);
            }

            HttpResponseMessage response;
            try
            {
                response = await OAuthHttp.PostFormAsync(_client,
                    $"{oauthHost}/api/oauth/token",
                    new Dictionary<string, string>
                    {
                        ["client_id"] = ClientId,
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken,
                    },
                    cancellationToken, RequestTimeoutMs).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
                continue;
            }

            var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseTokenResponse(json, "refresh");
            }

            // 401/403：存储凭据已死；Models 清掉它并提示重新登录。
            if ((int)response.StatusCode is 401 or 403 || json?.Str("error") == "invalid_grant")
            {
                var description = json?.Str("error_description") is { Length: > 0 } d ? $": {d}" : "";
                throw new InvalidOperationException(
                    $"Kimi Code token refresh unauthorized (status {(int)response.StatusCode}){description}");
            }

            if (IsRetryableRefreshFailure((int)response.StatusCode) && attempt < RefreshMaxRetries)
            {
                lastError = new InvalidOperationException(
                    $"Kimi Code token refresh failed with status {(int)response.StatusCode}");
                continue;
            }

            var body = json?.ToJsonString() ?? "";
            throw new InvalidOperationException(
                $"Kimi Code token refresh failed with status {(int)response.StatusCode}{(body.Length > 0 ? $": {body}" : "")}");
        }

        throw lastError ?? new InvalidOperationException("Kimi Code token refresh failed");
    }
}
