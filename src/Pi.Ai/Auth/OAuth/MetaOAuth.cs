using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// Meta Model API OAuth 流。对应 TS auth/oauth/meta.ts：RFC 8628 设备授权
/// （https://auth.meta.com，JSON 响应）。Meta 把身份与 API 访问拆开——身份令牌
/// 不能直接推理，要通过 Muse Code key-mint 端点换 Model API key（key 存活约一天）。
/// 身份令牌存为 refresh、铸造的 key 存为 access，标准 OAuth 调度器过期即重新铸造；
/// 身份令牌本身不可续（refresh_token grant 返回 404），mint 返回 401/403 即会话已死。
/// </summary>
public sealed class MetaOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    // Muse Code CLI client id。
    private const string ClientId = "1031625952748946";
    private const string AuthHost = "https://auth.meta.com";
    private const string DeviceAuthorizationUrl = $"{AuthHost}/oidc/device/authorization/";
    private const string DeviceTokenUrl = $"{AuthHost}/oidc/device/token/";
    private const string ApiKeyMintUrl = "https://api.meta.ai/muse-code/key";
    private const long ApiKeyLifetimeMs = 24 * 60 * 60 * 1000;
    private const int RequestTimeoutMs = 30 * 1000;

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "Meta (Muse subscription)";

    public bool IsSubscription => true;

    public string? LoginLabel => "Sign in with Meta";

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var device = await StartDeviceAuthorizationAsync(cancellationToken).ConfigureAwait(false);
            interaction.Notify(new AuthEvent.DeviceCode(
                device.UserCode, device.VerificationUri, device.IntervalSeconds, device.ExpiresInSeconds));
            var identityToken = await PollForIdentityTokenAsync(device, cancellationToken).ConfigureAwait(false);
            interaction.Notify(new AuthEvent.Progress("Enabling Meta Model API access..."));
            return await MintApiKeyAsync(identityToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (interaction.Signal.IsCancellationRequested)
        {
            // 进行中的请求因中止而失败时，统一报 Login cancelled（登录 UI 按消息匹配）。
            throw new OperationCanceledException("Login cancelled", interaction.Signal);
        }
    }

    public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
        => MintApiKeyAsync(credential.Refresh, cancellationToken);

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private sealed record DeviceAuthorization(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int? IntervalSeconds,
        int? ExpiresInSeconds);

    /// <summary>错误详情：error_description → detail → message → error。</summary>
    private static string ErrorDetail(JsonObject? json)
    {
        if (json is null) return "";
        foreach (var key in new[] { "error_description", "detail", "message", "error" })
        {
            if (json.Str(key) is { } value && value.Trim().Length > 0) return $": {value.Trim()}";
        }
        return "";
    }

    /// <summary>验证 URI 在用户浏览器里打开；只信任 http(s)。</summary>
    private static string? TrustedHttpUrl(object? value)
    {
        if (value is not string { Length: > 0 } text) return null;
        try
        {
            var url = new Uri(text);
            return url.Scheme is "https" or "http" ? url.ToString() : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(CancellationToken cancellationToken)
    {
        var response = await OAuthHttp.PostFormAsync(_client, DeviceAuthorizationUrl,
            new Dictionary<string, string> { ["client_id"] = ClientId },
            cancellationToken, RequestTimeoutMs).ConfigureAwait(false);
        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Meta device authorization failed with status {(int)response.StatusCode}{ErrorDetail(json)}");
        }
        var deviceCode = json?.Str("device_code");
        var userCode = json?.Str("user_code");
        var verificationUri = TrustedHttpUrl(json?.Str("verification_uri_complete"))
            ?? TrustedHttpUrl(json?.Str("verification_uri"));
        if (deviceCode is not { Length: > 0 } || userCode is not { Length: > 0 } || verificationUri is null)
        {
            throw new InvalidOperationException(
                $"Invalid Meta device authorization response: {json?.ToJsonString() ?? "null"}");
        }
        return new DeviceAuthorization(
            deviceCode, userCode, verificationUri,
            IntervalSeconds: json!.PositiveInt("interval"),
            ExpiresInSeconds: json!.PositiveInt("expires_in"));
    }

    private async Task<string> PollForIdentityTokenAsync(
        DeviceAuthorization device, CancellationToken cancellationToken)
    {
        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<string>
        {
            IntervalSeconds = device.IntervalSeconds,
            ExpiresInSeconds = device.ExpiresInSeconds,
            WaitBeforeFirstPoll = true,
            Signal = cancellationToken,
            Poll = async () =>
            {
                var response = await OAuthHttp.PostFormAsync(_client, DeviceTokenUrl,
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = device.DeviceCode,
                        ["client_id"] = ClientId,
                    },
                    cancellationToken, RequestTimeoutMs).ConfigureAwait(false);
                var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && json?.Str("access_token") is { Length: > 0 } accessToken)
                {
                    return new DeviceCodePollResult<string>.Complete(accessToken);
                }
                switch (json?.Str("error"))
                {
                    case "authorization_pending":
                        return new DeviceCodePollResult<string>.Pending();
                    case "slow_down":
                        return new DeviceCodePollResult<string>.SlowDown(json!.PositiveInt("interval"));
                    case "access_denied":
                        return new DeviceCodePollResult<string>.Failed("Meta login was denied.");
                    case "expired_token":
                        return new DeviceCodePollResult<string>.Failed(
                            "Meta device authorization expired. Please restart login.");
                    default:
                        return new DeviceCodePollResult<string>.Failed(
                            $"Meta device token request failed with status {(int)response.StatusCode}{ErrorDetail(json)}");
                }
            },
        }).ConfigureAwait(false);
    }

    /// <summary>身份令牌换 Model API key。key 有效期约一天。</summary>
    private async Task<Credential.OAuth> MintApiKeyAsync(string identityToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiKeyMintUrl)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", identityToken);
        request.Headers.Add("x-api-version", "1.0.0");
        var response = await OAuthHttp.SendAsync(_client, request, cancellationToken, RequestTimeoutMs).ConfigureAwait(false);
        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        if ((int)response.StatusCode is 401 or 403)
        {
            // 身份令牌不可续（见文件头注释）；只能重新走设备流。
            throw new InvalidOperationException(
                $"Meta session expired (status {(int)response.StatusCode}). Run `/login meta` to sign in again.{ErrorDetail(json)}");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Meta API key mint failed with status {(int)response.StatusCode}{ErrorDetail(json)}");
        }
        var apiKey = json?.Str("api_key");
        if (apiKey is not { Length: > 0 })
        {
            var actionUrl = TrustedHttpUrl(json?.Str("action_url"));
            throw new InvalidOperationException(
                $"Meta did not issue an API key.{(actionUrl is not null ? $" Complete setup at {actionUrl}" : "")}");
        }
        return new Credential.OAuth(identityToken, apiKey, Now() + ApiKeyLifetimeMs);
    }
}
