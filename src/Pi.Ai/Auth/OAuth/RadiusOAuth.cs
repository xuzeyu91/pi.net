using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// Radius 网关 OAuth 流。对应 TS auth/oauth/radius.ts：Radius 是 pi-messages
/// 网关；OAuth 客户端 API 在网关上，只有浏览器授权端点做发现。浏览器方式
/// （端口 1456 回调）与 device-code 方式可选。
/// </summary>
public sealed class RadiusOAuth : IOAuthAuth
{
    private const string CallbackHost = "127.0.0.1";
    private const int CallbackPort = 1456;
    private const string CallbackPath = "/oauth/callback";
    private const string RedirectUri = "http://127.0.0.1:1456/oauth/callback";
    private const long TokenExpirySkewMs = 60_000;
    private const string LoginMethodBrowser = "browser";
    private const string LoginMethodDeviceCode = "device-code";
    private const string OAuthClientId = "pi-gateway";
    private const string OAuthScope = "gateway offline_access";
    private const string OAuthDeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly string _name;
    private readonly string _gateway;
    private readonly HttpClient _client;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>选项。对应 TS <c>RadiusOAuthOptions</c>。</summary>
    public sealed record Options(string Name, string Gateway);

    public RadiusOAuth(Options options, HttpClient? httpClient = null)
    {
        _name = options.Name;
        _gateway = RadiusConfig.NormalizeGatewayUrl(options.Gateway);
        _client = httpClient ?? OAuthHttp.Shared;
    }

    public string Name => _name;

    public bool IsSubscription => false;

    public string? LoginLabel => null;

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var loginMethod = await interaction.PromptAsync(
            new AuthPrompt.Select($"Sign in to {_name}:",
            [
                new AuthSelectOption(LoginMethodBrowser, "Sign in with browser (recommended)"),
                new AuthSelectOption(LoginMethodDeviceCode,
                    "Sign in with device code (when signing in from another device)"),
            ]), cancellationToken).ConfigureAwait(false);

        if (loginMethod == LoginMethodDeviceCode)
        {
            return await LoginWithDeviceCodeAsync(interaction, cancellationToken).ConfigureAwait(false);
        }
        if (loginMethod == LoginMethodBrowser)
        {
            var discovery = await LoadDiscoveryAsync(interaction.Signal).ConfigureAwait(false);
            return await LoginWithBrowserAsync(discovery.AuthorizationEndpoint, interaction, cancellationToken)
                .ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Unknown {_name} sign-in method: {loginMethod}");
    }

    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
        => await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = OAuthClientId,
            ["refresh_token"] = credential.Refresh,
        }, cancellationToken).ConfigureAwait(false);

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private sealed record RadiusDiscovery(string AuthorizationEndpoint);

    private async Task<RadiusDiscovery> LoadDiscoveryAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_gateway}/v1/oauth");
        request.Headers.Accept.ParseAdd("application/json");
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not load Radius OAuth config from {_gateway}: {(int)response.StatusCode} {await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false)}");
        }

        var discovery = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        if (discovery?.Str("authorizationEndpoint") is not { } endpoint)
        {
            throw new InvalidOperationException($"Invalid Radius OAuth config from {_gateway}");
        }
        return new RadiusDiscovery(endpoint);
    }

    /// <summary>带 OAuth error/error_description 的响应错误。对应 TS <c>OAuthResponseError</c>。</summary>
    public sealed class OAuthResponseError : Exception
    {
        public OAuthResponseError(int status, string? oauthError, string? description, string message)
            : base($"{message}: {Detail(oauthError, description, status)}")
        {
            Status = status;
            OAuthError = oauthError;
        }

        public int Status { get; }

        public string? OAuthError { get; }

        private static string Detail(string? oauthError, string? description, int status)
            => oauthError is not null
                ? description is not null ? $"{oauthError}: {description}" : oauthError
                : description ?? status.ToString();
    }

    private async Task<OAuthResponseError> ReadResponseErrorAsync(HttpResponseMessage response, string message)
    {
        var text = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
        string? oauthError = null;
        string? description = null;

        if (text.Length > 0)
        {
            try
            {
                var data = JsonNode.Parse(text) as JsonObject;
                oauthError = data?.Str("error");
                description = data?.Str("error_description");
            }
            catch
            {
                description = text;
            }
        }

        return new OAuthResponseError((int)response.StatusCode, oauthError, description, message);
    }

    private async Task<Credential.OAuth> RequestTokenAsync(
        IReadOnlyDictionary<string, string> body, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_gateway}/v1/oauth/token")
            {
                Content = new FormUrlEncodedContent(body),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Content!.Headers.ContentType!.CharSet = "utf-8";
            response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ReadResponseErrorAsync(response, "Radius OAuth token request failed").ConfigureAwait(false);
        }

        var data = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Radius OAuth token request failed: invalid JSON response");

        return new Credential.OAuth(
            data.Str("refresh_token") ?? "",
            data.Str("access_token") ?? "",
            Now() + (long)((data.Num("expires_in") ?? 0) * 1000) - TokenExpirySkewMs)
        {
            Scope = data.Str("scope"),
        };
    }

    private async Task<Credential.OAuth> LoginWithBrowserAsync(
        string authorizationEndpoint, ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var pkce = Pkce.Generate();
        var state = Guid.NewGuid().ToString();
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = OAuthClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = OAuthScope,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["handoff"] = "url",
            ["state"] = state,
        };
        var authorizeUrl = $"{authorizationEndpoint}?{await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}";

        var callback = OAuthCallbackServer<Credential.OAuth>.Start(new OAuthCallbackServerOptions<Credential.OAuth>
        {
            ProviderName = "Radius",
            Host = CallbackHost,
            Port = CallbackPort,
            Path = CallbackPath,
            State = state,
            Complete = code => RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthClientId,
                ["redirect_uri"] = RedirectUri,
                ["code"] = code,
                ["code_verifier"] = pkce.Verifier,
            }, interaction.Signal),
            Signal = interaction.Signal,
        });
        interaction.Notify(new AuthEvent.Progress($"Listening for OAuth callback on {RedirectUri}"));
        interaction.Notify(new AuthEvent.AuthUrl(authorizeUrl, "Continue in your browser."));

        try
        {
            var credential = await callback.WaitAsync().ConfigureAwait(false);
            return credential ?? throw new InvalidOperationException("OAuth callback did not complete.");
        }
        finally
        {
            callback.Dispose();
        }
    }

    private sealed record DeviceAuthorizationResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int ExpiresIn,
        int? Interval);

    private async Task<DeviceAuthorizationResponse> RequestDeviceAuthorizationAsync(
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await OAuthHttp.PostFormAsync(_client, $"{_gateway}/v1/oauth/device",
                new Dictionary<string, string> { ["client_id"] = OAuthClientId, ["scope"] = OAuthScope },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await ReadResponseErrorAsync(response, "Radius OAuth device authorization failed").ConfigureAwait(false);
        }

        var data = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        if (data.Str("device_code") is not { Length: > 0 } deviceCode
            || data.Str("user_code") is not { Length: > 0 } userCode
            || data.Str("verification_uri") is not { Length: > 0 } verificationUri
            || data.Num("expires_in") is not { } expiresIn)
        {
            throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        }

        return new DeviceAuthorizationResponse(
            deviceCode, userCode, verificationUri, (int)expiresIn,
            Interval: data.PositiveInt("interval"));
    }

    private async Task<Credential.OAuth> LoginWithDeviceCodeAsync(
        ProviderAuthInteraction interaction, CancellationToken cancellationToken)
    {
        var device = await RequestDeviceAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.DeviceCode(
            device.UserCode, device.VerificationUri, device.Interval, device.ExpiresIn));

        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<Credential.OAuth>
        {
            IntervalSeconds = device.Interval,
            ExpiresInSeconds = device.ExpiresIn,
            Signal = cancellationToken,
            Poll = async () =>
            {
                try
                {
                    var credentials = await RequestTokenAsync(new Dictionary<string, string>
                    {
                        ["grant_type"] = OAuthDeviceCodeGrantType,
                        ["client_id"] = OAuthClientId,
                        ["device_code"] = device.DeviceCode,
                    }, cancellationToken).ConfigureAwait(false);
                    return new DeviceCodePollResult<Credential.OAuth>.Complete(credentials);
                }
                catch (OAuthResponseError error)
                {
                    switch (error.OAuthError)
                    {
                        case "authorization_pending":
                            return new DeviceCodePollResult<Credential.OAuth>.Pending();
                        case "slow_down":
                            return new DeviceCodePollResult<Credential.OAuth>.SlowDown();
                        case "expired_token":
                            return new DeviceCodePollResult<Credential.OAuth>.Failed("Device authorization expired.");
                        case "access_denied":
                            return new DeviceCodePollResult<Credential.OAuth>.Failed("Device authorization was denied.");
                        default:
                            throw;
                    }
                }
            },
        }).ConfigureAwait(false);
    }
}
