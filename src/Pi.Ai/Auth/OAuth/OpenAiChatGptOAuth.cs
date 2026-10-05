using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// Sign in with ChatGPT——OpenAI Responses API 令牌共享。对应 TS
/// auth/oauth/openai-chatgpt.ts：无 client secret 的公客户端流；每次登录注册
/// 新动态 client（回调携带签发的 client_id），结果 access token 直连 api.openai.com。
/// </summary>
public sealed class OpenAiChatGptOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    private const string DynamicClientId = "dynamic_agent_client";
    private const string AgentNameHint = "Pi";
    private const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
    private const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    private const string Resource = "https://api.openai.com/v1";
    private const int CallbackPort = 1455;
    private const string CallbackPath = "/auth/callback";
    private const string RedirectUri = "http://127.0.0.1:1455/auth/callback";
    private const string DirectTokenScope = "chatgpt.tokens.use.direct";
    private const string Scope = $"openid profile email offline_access resource.invoke {DirectTokenScope}";

    // 在真实过期前很久就刷新，保证请求永远不带着将过期的令牌启动。
    private const long ExpiryMarginMs = 3 * 60 * 1000;

    private static string CallbackHost => ProviderEnv.GetValue("PI_OAUTH_CALLBACK_HOST") ?? "127.0.0.1";

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "OpenAI (ChatGPT subscription)";

    public bool IsSubscription => true;

    public string? LoginLabel => "Sign in with ChatGPT";

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var hostId = AgentHostId(options?.GetDeviceId);
        var pkce = Pkce.Generate();
        var state = RandomValue();
        var nonce = RandomValue();
        // 没有这个服务器时，浏览器回调会打到占用端口的别人（另一个未完成登录或
        // Codex CLI），被对方以 state mismatch 拒掉。这里直接报清晰错误。
        ChatGptCallbackServer callback;
        try
        {
            callback = ChatGptCallbackServer.Start(state, interaction.Signal);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode is 32 or 183)
        {
            throw new InvalidOperationException(
                $"Port {CallbackPort} is in use, probably by an unfinished login in another pi session or by the Codex CLI. Cancel that login and try again.",
                ex);
        }

        var query = new Dictionary<string, string>
        {
            ["client_id"] = DynamicClientId,
            ["agent_name_hint"] = AgentNameHint,
            ["ext_agent_host_id"] = hostId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["resource"] = Resource,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["nonce"] = nonce,
        };
        var authorizationUrl = $"{AuthorizeUrl}?{await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}";
        interaction.Notify(new AuthEvent.AuthUrl(authorizationUrl,
            "Complete sign-in in your browser. If the callback does not complete, paste the final redirect URL here."));

        var manualCts = new CancellationTokenSource();
        var linkedPromptCts = CancellationTokenSource.CreateLinkedTokenSource(manualCts.Token, interaction.Signal);
        var manualCode = interaction
            .PromptAsync(new AuthPrompt.ManualCode(
                "Complete login in your browser, or paste the final redirect URL here:",
                RedirectUri), linkedPromptCts.Token)
            .ContinueWith(t => t.Status == TaskStatus.RanToCompletion
                ? AuthorizationResultFromManualInput(t.Result, state)
                : throw t.Exception!.GetBaseException(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var result = await Task.WhenAny(callback.Result, manualCode).ConfigureAwait(false);
            var authorization = await result.ConfigureAwait(false);
            interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for tokens..."));
            return await ExchangeAuthorizationCodeAsync(
                authorization.Code, pkce.Verifier, authorization.ClientId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interaction.Signal.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", interaction.Signal);
        }
        finally
        {
            manualCts.Cancel();
            linkedPromptCts.Dispose();
            callback.Dispose();
        }
    }

    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.ClientId))
        {
            throw new InvalidOperationException(
                "Stored OpenAI OAuth credential does not contain an issued client ID; reconnect ChatGPT");
        }
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credential.ClientId,
            ["refresh_token"] = credential.Refresh,
            ["resource"] = Resource,
        }, cancellationToken).ConfigureAwait(false);
        return CredentialFromTokenResponse(token, credential.ClientId);
    }

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private sealed record AuthorizationResult(string Code, string ClientId);

    private sealed record TokenResponse(
        string? AccessToken, string? RefreshToken, string? IdToken, string? Scope, double? ExpiresIn);

    /// <summary>OpenAI 用形如 urn:uuid:&lt;uuid&gt; 的稳定 URI 标识安装（agent host）。</summary>
    private static string AgentHostId(Func<string>? getDeviceId)
    {
        var deviceId = getDeviceId?.Invoke();
        if (deviceId is null || !Guid.TryParseExact(deviceId, "D", out _))
        {
            throw new InvalidOperationException(
                "Sign in with ChatGPT requires a device ID (UUID) for this installation");
        }
        return $"urn:uuid:{deviceId.ToLowerInvariant()}";
    }

    private static string RandomValue()
        => Pkce.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static AuthorizationResult AuthorizationResultFromCallback(Uri url, string expectedState)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var code = query["code"];
        if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("Missing authorization code");
        var state = query["state"];
        if (string.IsNullOrEmpty(state)) throw new InvalidOperationException("Missing OAuth state");
        if (state != expectedState) throw new InvalidOperationException("OAuth state mismatch");
        var clientId = query["client_id"]?.Trim();
        if (string.IsNullOrEmpty(clientId))
        {
            throw new InvalidOperationException(
                "OpenAI OAuth registration callback did not contain an issued client ID");
        }
        return new AuthorizationResult(code, clientId);
    }

    private static AuthorizationResult AuthorizationResultFromManualInput(string input, string expectedState)
    {
        Uri url;
        try
        {
            url = new Uri(input.Trim());
        }
        catch (UriFormatException)
        {
            throw new InvalidOperationException("Paste the full callback URL from the browser");
        }
        var expected = new Uri(RedirectUri);
        if (url.Scheme != expected.Scheme || url.Authority != expected.Authority || url.AbsolutePath != expected.AbsolutePath)
        {
            throw new InvalidOperationException($"The pasted callback URL must start with {RedirectUri}");
        }
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var error = query["error"];
        if (error is not null) throw new InvalidOperationException($"ChatGPT authorization failed: {error}");
        return AuthorizationResultFromCallback(url, expectedState);
    }

    /// <summary>专用 loopback 回调服务器。对应 TS openai-chatgpt.ts 内嵌的 <c>startCallbackServer</c>。</summary>
    private sealed class ChatGptCallbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<AuthorizationResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _signalRegistration;

        public Task<AuthorizationResult> Result => _result.Task;

        private ChatGptCallbackServer(CancellationToken signal)
        {
            if (signal.CanBeCanceled)
                _signalRegistration = signal.Register(static (self, ct) =>
                    ((ChatGptCallbackServer)self!)._result.TrySetException(
                        new OperationCanceledException("Login cancelled", ct)), this);
        }

        public static ChatGptCallbackServer Start(string expectedState, CancellationToken signal)
        {
            var instance = new ChatGptCallbackServer(signal);
            instance._listener.Prefixes.Add($"http://{CallbackHost}:{CallbackPort}/");
            instance._listener.Start();
            _ = instance.AcceptLoopAsync(expectedState);
            return instance;
        }

        private async Task AcceptLoopAsync(string expectedState)
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    Handle(context, expectedState);
                }
            }
            catch (Exception error)
            {
                _result.TrySetException(error);
            }
        }

        private void Handle(HttpListenerContext context, string expectedState)
        {
            var response = context.Response;
            try
            {
                var url = context.Request.Url ?? new Uri(RedirectUri);
                if (url.AbsolutePath != CallbackPath)
                {
                    SendHtml(response, 404, OAuthPage.ErrorHtml("Callback route not found."));
                    return;
                }

                var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                var error = query["error"];
                if (error is not null)
                {
                    SendHtml(response, 400, OAuthPage.ErrorHtml("ChatGPT was not connected.", $"Error: {error}"));
                    _result.TrySetException(new InvalidOperationException($"ChatGPT authorization failed: {error}"));
                    return;
                }

                AuthorizationResult authorization;
                try
                {
                    authorization = AuthorizationResultFromCallback(url, expectedState);
                }
                catch (Exception error2)
                {
                    var message = error2.Message;
                    SendHtml(response, 400, OAuthPage.ErrorHtml(message));
                    return;
                }

                SendHtml(response, 200, OAuthPage.SuccessHtml(
                    "ChatGPT authentication completed. You can close this window."));
                _result.TrySetResult(authorization);
            }
            catch
            {
                SendHtmlSafe(response, 500, OAuthPage.ErrorHtml("Internal error while processing the callback."));
            }
        }

        public void Dispose()
        {
            _signalRegistration.Dispose();
            try { _listener.Stop(); } catch { /* 已关闭 */ }
            try { _listener.Close(); } catch { /* 已关闭 */ }
            GC.SuppressFinalize(this);
        }

        private static void SendHtml(HttpListenerResponse response, int status, string body)
        {
            response.StatusCode = status;
            response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Dispose();
        }

        private static void SendHtmlSafe(HttpListenerResponse response, int status, string body)
        {
            try { SendHtml(response, status, body); } catch { try { response.Abort(); } catch { /* ignore */ } }
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(
        IReadOnlyDictionary<string, string> body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(body),
        };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenAI OAuth token request failed ({(int)response.StatusCode}): {(responseBody.Length > 0 ? responseBody : response.ReasonPhrase ?? "")}");
        }
        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI OAuth token response must be an object");
        return new TokenResponse(
            AccessToken: json.Str("access_token"),
            RefreshToken: json.Str("refresh_token"),
            IdToken: json.Str("id_token"),
            Scope: json.Str("scope"),
            ExpiresIn: json.Num("expires_in"));
    }

    private static string RequireTokenString(string? value, string field)
    {
        if (value is null || value.Trim().Length == 0)
        {
            throw new InvalidOperationException($"OpenAI OAuth token response has invalid {field}");
        }
        return value;
    }

    private Credential.OAuth CredentialFromTokenResponse(TokenResponse token, string clientId)
    {
        var access = RequireTokenString(token.AccessToken, "access_token");
        var refresh = RequireTokenString(token.RefreshToken, "refresh_token");
        var scope = RequireTokenString(token.Scope, "scope");
        if (token.ExpiresIn is not { } expiresIn || double.IsNaN(expiresIn) || double.IsInfinity(expiresIn) || expiresIn <= 0)
        {
            throw new InvalidOperationException("OpenAI OAuth token response has invalid expires_in");
        }
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(DirectTokenScope))
        {
            throw new InvalidOperationException($"OpenAI OAuth grant did not include {DirectTokenScope}");
        }
        return new Credential.OAuth(refresh, access, Now() + (long)(expiresIn * 1000) - ExpiryMarginMs)
        {
            ClientId = clientId,
            Scopes = scopes,
        };
    }

    private async Task<Credential.OAuth> ExchangeAuthorizationCodeAsync(
        string code, string verifier, string clientId, CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri,
            ["resource"] = Resource,
        }, cancellationToken).ConfigureAwait(false);
        // Pi 不用 ID token 识别用户或读取资料；存在性检查保留为 token 响应契约。
        if (token.IdToken is null || token.IdToken.Trim().Length == 0)
        {
            throw new InvalidOperationException("OpenAI OAuth token response did not contain an ID token");
        }
        return CredentialFromTokenResponse(token, clientId);
    }
}
