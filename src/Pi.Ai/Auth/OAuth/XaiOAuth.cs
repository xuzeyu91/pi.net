using System.Text.Json.Nodes;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// xAI OAuth 设备码流。对应 TS auth/oauth/xai.ts。验证 URI 强制 https
/// （防恶意响应诱导 open 执行任意程序）；refresh 响应可能不带 refresh_token
/// （未轮换时保留旧值）。
/// </summary>
public sealed class XaiOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";

    // 在报告的过期之前就刷新，避免请求中途令牌失效。
    private const long RefreshSkewMs = 5 * 60 * 1000;
    private const long DefaultTokenLifetimeSeconds = 3600;

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "xAI (Grok/X subscription)";

    public bool IsSubscription => true;

    public string? LoginLabel => "Sign in with SuperGrok or X Premium";

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var device = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.DeviceCode(
            device.UserCode,
            device.VerificationUriComplete ?? device.VerificationUri,
            device.IntervalSeconds,
            (int)device.ExpiresInSeconds));
        return await PollForTokensAsync(device, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        var response = await PostFormAsync(TokenUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = credential.Refresh,
        }, cancellationToken).ConfigureAwait(false);
        if (!response.Response.IsSuccessStatusCode) throw RequestFailure("token refresh", response.Response, response.Body);
        return CredentialsFromTokenResponse(response.Body, credential.Refresh);
    }

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private string RequiredString(JsonObject body, string field)
    {
        if (body.Str(field) is not { Length: > 0 } value)
        {
            throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");
        }
        return value;
    }

    private static long PositiveNumber(JsonObject body, string field)
    {
        if (body.Num(field) is not { } value || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            throw new InvalidOperationException($"Invalid xAI OAuth response field: {field}");
        }
        return (long)value;
    }

    /// <summary>验证 URI 强制 https（见文件头）。</summary>
    private static string ValidateVerificationUri(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || url.Scheme != "https")
        {
            throw new InvalidOperationException("Untrusted verification URI in xAI OAuth response");
        }
        return url.ToString();
    }

    private async Task<(HttpResponseMessage Response, JsonObject Body)> PostFormAsync(
        string url, IReadOnlyDictionary<string, string> fields, CancellationToken signal)
    {
        HttpResponseMessage response;
        try
        {
            response = await OAuthHttp.PostFormAsync(_client, url, fields, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", signal);
        }

        JsonObject body;
        try
        {
            var parsed = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
            body = parsed ?? [];
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", signal);
        }
        catch
        {
            throw new InvalidOperationException(
                $"xAI OAuth returned invalid JSON (HTTP {(int)response.StatusCode})");
        }
        return (response, body);
    }

    private static Exception RequestFailure(string action, HttpResponseMessage response, JsonObject body)
    {
        var error = body.Str("error");
        var description = body.Str("error_description");
        var detail = string.Join(": ", new[] { error, description }.Where(part => part is not null));
        return new InvalidOperationException(
            $"xAI OAuth {action} failed (HTTP {(int)response.StatusCode}){(detail.Length > 0 ? $": {detail}" : "")}");
    }

    private sealed record XaiDeviceCode(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        int? IntervalSeconds,
        long ExpiresInSeconds);

    private XaiDeviceCode ParseDeviceCode(JsonObject body)
    {
        // RFC 8628 允许 interval 0（无最小等待）；非正/畸形值回落到轮询器默认值而不是报错。
        var verificationUriComplete = body.Str("verification_uri_complete") is { Length: > 0 } complete
            ? ValidateVerificationUri(complete)
            : null;
        return new XaiDeviceCode(
            RequiredString(body, "device_code"),
            RequiredString(body, "user_code"),
            ValidateVerificationUri(RequiredString(body, "verification_uri")),
            verificationUriComplete,
            body.PositiveInt("interval"),
            PositiveNumber(body, "expires_in"));
    }

    private Credential.OAuth CredentialsFromTokenResponse(JsonObject body, string? previousRefreshToken = null)
    {
        var access = RequiredString(body, "access_token");
        // xAI 在 token 未轮换时可能省略 refresh_token。
        var refresh = !body.Has("refresh_token") && previousRefreshToken is not null
            ? previousRefreshToken
            : RequiredString(body, "refresh_token");
        var expiresInSeconds = !body.Has("expires_in")
            ? DefaultTokenLifetimeSeconds
            : PositiveNumber(body, "expires_in");
        return new Credential.OAuth(refresh, access, Now() + expiresInSeconds * 1000 - RefreshSkewMs);
    }

    private async Task<XaiDeviceCode> RequestDeviceCodeAsync(CancellationToken signal)
    {
        var (response, body) = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["referrer"] = "pi",
        }, signal).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw RequestFailure("device authorization", response, body);
        return ParseDeviceCode(body);
    }

    private async Task<Credential.OAuth> PollForTokensAsync(XaiDeviceCode device, CancellationToken signal)
    {
        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<Credential.OAuth>
        {
            IntervalSeconds = device.IntervalSeconds,
            ExpiresInSeconds = (int)device.ExpiresInSeconds,
            WaitBeforeFirstPoll = true,
            Signal = signal,
            Poll = async () =>
            {
                var (response, body) = await PostFormAsync(TokenUrl, new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = device.DeviceCode,
                }, signal).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return new DeviceCodePollResult<Credential.OAuth>.Complete(
                        CredentialsFromTokenResponse(body));
                }

                switch (body.Str("error"))
                {
                    case "authorization_pending":
                        return new DeviceCodePollResult<Credential.OAuth>.Pending();
                    case "slow_down":
                        var interval = body.Num("interval");
                        return new DeviceCodePollResult<Credential.OAuth>.SlowDown(
                            interval is { } value && !double.IsNaN(value) ? (int)value : null);
                    case "access_denied" or "authorization_denied":
                        return new DeviceCodePollResult<Credential.OAuth>.Failed(
                            "xAI device authorization was denied");
                    case "expired_token":
                        return new DeviceCodePollResult<Credential.OAuth>.Failed("xAI device code expired");
                    default:
                        return new DeviceCodePollResult<Credential.OAuth>.Failed(
                            RequestFailure("device token polling", response, body).Message);
                }
            },
        }).ConfigureAwait(false);
    }
}
