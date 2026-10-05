using System.Text.Json.Nodes;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// OpenRouter OAuth PKCE 流。对应 TS auth/oauth/openrouter.ts：授权码换取的是
/// 永久用户受控 API key（而非 access/refresh 对）；回调由临时端口的一次性
/// loopback 服务器处理，与手动粘贴竞速（远程/无头会话用）。
/// </summary>
public sealed class OpenRouterOAuth(HttpClient? httpClient = null) : IOAuthAuth
{
    private const string AuthorizeUrl = "https://openrouter.ai/auth";
    private const string TokenUrl = "https://openrouter.ai/api/v1/auth/keys";
    private const int LoginTimeoutMs = 5 * 60 * 1000;
    private const int TokenExchangeTimeoutMs = 30_000;

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    public string Name => "OpenRouter OAuth";

    public bool IsSubscription => false;

    public string? LoginLabel => "Sign in with OpenRouter";

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pkce = Pkce.Generate();
        // OpenRouter 不回传 state；随机路径防止杂散请求完成登录。
        var callback = OAuthCallbackServer<Credential.OAuth>.Start(new OAuthCallbackServerOptions<Credential.OAuth>
        {
            ProviderName = "OpenRouter",
            Host = CallbackHost,
            Port = 0,
            Path = $"/oauth/callback/{Guid.NewGuid()}",
            Complete = (code) => ExchangeAuthorizationCodeAsync(code, pkce.Verifier, interaction.Signal),
            Signal = interaction.Signal,
            TimeoutMs = LoginTimeoutMs,
        });

        try
        {
            var query = new Dictionary<string, string>
            {
                ["callback_url"] = callback.RedirectUri,
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = "S256",
            };
            var authorizeUrl = $"{AuthorizeUrl}?{await new FormUrlEncodedContent(query).ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}";

            interaction.Notify(new AuthEvent.Progress(
                $"Listening for OpenRouter OAuth callback on {callback.RedirectUri}"));
            interaction.Notify(new AuthEvent.AuthUrl(authorizeUrl,
                "Complete sign-in in your browser. If the browser is on another machine, paste the final redirect URL here."));

            var result = await OAuthCallbackFlow.WaitForCallbackOrManualInput(
                interaction, callback,
                new ManualPrompt(
                    "Complete sign-in in your browser, or paste the authorization code / redirect URL here:",
                    callback.RedirectUri)).ConfigureAwait(false);
            if (result is CallbackOrManualInput<Credential.OAuth>.Callback browserCallback)
            {
                return browserCallback.Value;
            }
            var input = ((CallbackOrManualInput<Credential.OAuth>.Manual)result).Input;
            var code = ParseAuthorizationInput(input);
            if (code is null) throw new InvalidOperationException("Missing authorization code");
            interaction.Notify(new AuthEvent.Progress("Exchanging authorization code for an API key..."));
            return await ExchangeAuthorizationCodeAsync(code, pkce.Verifier, interaction.Signal).ConfigureAwait(false);
        }
        finally
        {
            callback.Dispose();
        }
    }

    public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
        // 永久 API key：无可刷新之物。
        => Task.FromResult(credential);

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private static string CallbackHost => ProviderEnv.GetValue("PI_OAUTH_CALLBACK_HOST") ?? "127.0.0.1";

    /// <summary>从粘贴输入提取 code（URL/查询串/裸 code）。</summary>
    private static string? ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return null;

        try
        {
            return System.Web.HttpUtility.ParseQueryString(new Uri(value).Query)["code"];
        }
        catch (UriFormatException)
        {
            // 不是 URL
        }

        if (value.Contains("code=", StringComparison.Ordinal))
        {
            return System.Web.HttpUtility.ParseQueryString(value)["code"];
        }

        return value;
    }

    /// <summary>错误详情：error_description → message → error → error.message。</summary>
    private static string? ErrorDetail(JsonObject body)
    {
        if (body.Str("error_description") is { } description) return description;
        if (body.Str("message") is { } message) return message;
        if (body.Str("error") is { } error) return error;
        if (body.Obj("error") is { } errorObject) return errorObject.Str("message");
        return null;
    }

    private async Task<Credential.OAuth> ExchangeAuthorizationCodeAsync(
        string code, string verifier, CancellationToken signal)
    {
        if (signal.IsCancellationRequested) throw new OperationCanceledException("Login cancelled", signal);
        using var timeoutCts = new CancellationTokenSource(TokenExchangeTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(signal, timeoutCts.Token);

        HttpResponseMessage response;
        JsonObject body = [];
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["code"] = code,
                        ["code_verifier"] = verifier,
                        ["code_challenge_method"] = "S256",
                    }.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            response = await _client.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            try
            {
                if (await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false) is { } parsed)
                {
                    body = parsed;
                }
            }
            catch
            {
                if (response.IsSuccessStatusCode) throw new InvalidOperationException("OpenRouter OAuth returned invalid JSON");
            }
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", signal);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("OpenRouter OAuth token exchange timed out");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = ErrorDetail(body);
            throw new InvalidOperationException(
                $"OpenRouter OAuth key exchange failed (HTTP {(int)response.StatusCode}){(detail is not null ? $": {detail}" : "")}");
        }

        if (body.Str("key") is not { Length: > 0 } key)
        {
            throw new InvalidOperationException("OpenRouter OAuth response carries no \"key\"");
        }

        // Number.MAX_SAFE_INTEGER：永久 key 用 JS 安全整数上界表达"不过期"。
        return new Credential.OAuth("", key, 9007199254740991);
    }
}
