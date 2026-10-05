using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>OpenAI Codex OAuth 流测试（端口 1455 与 ChatGPT 共用，串行执行）。</summary>
[Collection("port-1455")]
public class OpenAiCodexOAuthTests
{
    private const string TokenUrl = "https://auth.openai.com/oauth/token";

    private static string Jwt(string accountId)
    {
        static string B64(string json) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var header = B64("""{"alg":"HS256"}""");
        var payload = B64(new JsonObject
        {
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId },
        }.ToJsonString());
        return $"{header}.{payload}.sig";
    }

    private static HttpResponseMessage TokenResponse(string accessToken = "access-token")
        => FakeResponse.Json(new JsonObject
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = "refresh-token",
            ["expires_in"] = 3600,
        });

    [Fact]
    public async Task ManualCallbackLoginExtractsAccountIdFromJwt()
    {
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl url) authUrl = url.Url;
        });
        interaction.PromptHandler = async (prompt, _) => prompt switch
        {
            AuthPrompt.Select => "browser",
            AuthPrompt.ManualCode when authUrl is not null =>
                $"http://localhost:1455/auth/callback?code=manual-code&state={AssertX.Query(authUrl!, "state")}",
            _ => throw new InvalidOperationException("unexpected prompt"),
        };
        var handler = new FakeHttpHandler(_ => TokenResponse(Jwt("acct-123")));
        var oauth = new OpenAiCodexOAuth(new HttpClient(handler)) { Now = () => 1_000_000 };

        var credentials = await oauth.LoginAsync(captured.ToInteraction());

        Assert.Equal("acct-123", credentials.AccountId);
        Assert.Equal("refresh-token", credentials.Refresh);
        Assert.Equal(1_000_000 + 3600_000, credentials.Expires);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(TokenUrl, request.Url);
        Assert.Equal("authorization_code", AssertX.Form(request.Body, "grant_type"));
        Assert.Equal("manual-code", AssertX.Form(request.Body, "code"));
        Assert.Equal("http://localhost:1455/auth/callback", AssertX.Form(request.Body, "redirect_uri"));
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", AssertX.Form(request.Body, "client_id"));
    }

    [Fact]
    public async Task MissingAccountIdFails()
    {
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl url) authUrl = url.Url;
        });
        interaction.PromptHandler = async (prompt, _) => prompt switch
        {
            AuthPrompt.Select => "browser",
            AuthPrompt.ManualCode =>
                $"http://localhost:1455/auth/callback?code=manual-code&state={AssertX.Query(authUrl!, "state")}",
            _ => throw new InvalidOperationException("unexpected prompt"),
        };
        var handler = new FakeHttpHandler(_ => TokenResponse("no.jwt.claims"));
        var oauth = new OpenAiCodexOAuth(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.LoginAsync(captured.ToInteraction()));
        Assert.Equal("Failed to extract accountId from token", error.Message);
    }

    [Fact]
    public async Task DeviceCodeLoginPollsUntilAuthorized()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                // 1. device usercode
                _ => FakeResponse.Json(new JsonObject
                {
                    ["device_auth_id"] = "da-1",
                    ["user_code"] = "ABCD-1234",
                    ["interval"] = 0,
                }),
                // 2. device token poll → pending
                _ => FakeResponse.Json(new JsonObject { ["error"] = "deviceauth_authorization_pending" },
                    HttpStatusCode.Forbidden),
                // 3. device token poll → authorized
                _ => FakeResponse.Json(new JsonObject
                {
                    ["authorization_code"] = "auth-code",
                    ["code_verifier"] = "server-verifier",
                }),
                // 4. token exchange
                _ => TokenResponse(Jwt("acct-9")));
            var oauth = new OpenAiCodexOAuth(new HttpClient(handler)) { Now = () => 2_000_000 };
            var interaction = new FakeInteraction { PromptHandler = async (_, _) => "device_code" };

            var credentials = await oauth.LoginAsync(new ProviderAuthInteraction(interaction), null, default);

            Assert.Equal("acct-9", credentials.AccountId);
            var deviceCodeEvent = interaction.Events.OfType<AuthEvent.DeviceCode>().Single();
            Assert.Equal("ABCD-1234", deviceCodeEvent.UserCode);
            Assert.Equal("https://auth.openai.com/codex/device", deviceCodeEvent.VerificationUri);

            Assert.Equal(4, handler.Requests.Count);
            var exchange = handler.Requests[3];
            Assert.Equal(TokenUrl, exchange.Url);
            Assert.Equal("auth-code", AssertX.Form(exchange.Body, "code"));
            Assert.Equal("server-verifier", AssertX.Form(exchange.Body, "code_verifier"));
            Assert.Equal("https://auth.openai.com/deviceauth/callback", AssertX.Form(exchange.Body, "redirect_uri"));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task RefreshReturnsRotatedCredentialWithAccountId()
    {
        var handler = new FakeHttpHandler(_ => TokenResponse(Jwt("acct-77")));
        var oauth = new OpenAiCodexOAuth(new HttpClient(handler)) { Now = () => 3_000_000 };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("r", "a", 1));

        Assert.Equal("acct-77", refreshed.AccountId);
        Assert.Equal(3_000_000 + 3600_000, refreshed.Expires);
        Assert.Equal("refresh_token", AssertX.Form(handler.Requests.Single().Body, "grant_type"));
    }
}

/// <summary>Sign in with ChatGPT 流测试（固定端口 1455，与 Codex 串行）。</summary>
[Collection("port-1455")]
public class OpenAiChatGptOAuthTests
{
    private const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    private const string RedirectUri = "http://127.0.0.1:1455/auth/callback";
    private const string DeviceId = "0f8b7b28-6f4a-4b1e-9d2b-9a6e5a5f1c3d";

    private static HttpResponseMessage TokenResponse()
        => FakeResponse.Json(new JsonObject
        {
            ["access_token"] = "user-access",
            ["refresh_token"] = "user-refresh",
            ["expires_in"] = 3600,
            ["id_token"] = "id-token-value",
            ["scope"] = "openid profile email chatgpt.tokens.use.direct",
        });

    [Fact]
    public async Task LoginRequiresDeviceId()
    {
        var oauth = new OpenAiChatGptOAuth(new HttpClient(new FakeHttpHandler()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.LoginAsync(new ProviderAuthInteraction(new FakeInteraction())));
        Assert.Contains("device ID (UUID)", error.Message);
    }

    [Fact]
    public async Task ManualRedirectUrlLoginPersistsIssuedClientId()
    {
        var handler = new FakeHttpHandler(_ => TokenResponse());
        var oauth = new OpenAiChatGptOAuth(new HttpClient(handler)) { Now = () => 10_000_000 };
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl url) authUrl = url.Url;
        });
        interaction.PromptHandler = async (_, _) =>
            $"{RedirectUri}?code=cb-code&state={AssertX.Query(authUrl!, "state")}&client_id=issued-client";

        var credentials = await oauth.LoginAsync(captured.ToInteraction(),
            new LoginOptions { GetDeviceId = () => DeviceId });

        Assert.Equal("user-access", credentials.Access);
        Assert.Equal("issued-client", credentials.ClientId);
        Assert.Contains("chatgpt.tokens.use.direct", credentials.Scopes!);
        Assert.Equal(10_000_000 + 3600_000 - 3 * 60_000, credentials.Expires); // 3 分钟提前量

        var request = Assert.Single(handler.Requests);
        Assert.Equal(TokenUrl, request.Url);
        Assert.Equal("authorization_code", AssertX.Form(request.Body, "grant_type"));
        Assert.Equal("issued-client", AssertX.Form(request.Body, "client_id"));
        Assert.Equal("https://api.openai.com/v1", AssertX.Form(request.Body, "resource"));
        Assert.Equal("urn:uuid:" + DeviceId, AssertX.Query(authUrl!, "ext_agent_host_id"));
    }

    [Fact]
    public async Task MissingDirectScopeFails()
    {
        var handler = new FakeHttpHandler(_ => FakeResponse.Json(new JsonObject
        {
            ["access_token"] = "a",
            ["refresh_token"] = "r",
            ["expires_in"] = 3600,
            ["id_token"] = "i",
            ["scope"] = "openid",
        }));
        var oauth = new OpenAiChatGptOAuth(new HttpClient(handler));
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl url) authUrl = url.Url;
        });
        interaction.PromptHandler = async (_, _) =>
            $"{RedirectUri}?code=cb-code&state={AssertX.Query(authUrl!, "state")}&client_id=issued-client";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.LoginAsync(captured.ToInteraction(), new LoginOptions { GetDeviceId = () => DeviceId }));
        Assert.Contains("chatgpt.tokens.use.direct", error.Message);
    }

    [Fact]
    public async Task RefreshRequiresIssuedClientId()
    {
        var oauth = new OpenAiChatGptOAuth(new HttpClient(new FakeHttpHandler()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.RefreshAsync(new Credential.OAuth("r", "a", 1)));
        Assert.Contains("does not contain an issued client ID", error.Message);
    }
}

/// <summary>OpenRouter OAuth 流测试。</summary>
public class OpenRouterOAuthTests
{
    private const string KeysUrl = "https://openrouter.ai/api/v1/auth/keys";

    [Fact]
    public async Task ManualCodeExchangesForPermanentKey()
    {
        var handler = new FakeHttpHandler(_ => FakeResponse.Json(new JsonObject
        {
            ["key"] = "sk-or-v1-key",
        }));
        var oauth = new OpenRouterOAuth(new HttpClient(handler)) { Now = () => 1_000 };
        var interaction = new FakeInteraction { PromptHandler = async (_, _) => "the-code" };

        var credentials = await oauth.LoginAsync(interaction.ToInteraction());

        Assert.Equal("sk-or-v1-key", credentials.Access);
        Assert.Equal("", credentials.Refresh);
        Assert.Equal(9007199254740991, credentials.Expires); // JS Number.MAX_SAFE_INTEGER

        var request = Assert.Single(handler.Requests);
        Assert.Equal(KeysUrl, request.Url);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("the-code", body.Str("code"));
        Assert.Equal("S256", body.Str("code_challenge_method"));
        Assert.NotNull(body.Str("code_verifier"));
    }

    [Fact]
    public async Task ExchangeErrorSurfacesDetail()
    {
        var handler = new FakeHttpHandler(_ => FakeResponse.Json(
            new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "code expired" },
            HttpStatusCode.BadRequest));
        var oauth = new OpenRouterOAuth(new HttpClient(handler));
        var interaction = new FakeInteraction { PromptHandler = async (_, _) => "bad-code" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.LoginAsync(interaction.ToInteraction()));
        Assert.Contains("OpenRouter OAuth key exchange failed (HTTP 400)", error.Message);
        Assert.Contains("code expired", error.Message);
    }

    [Fact]
    public async Task RefreshIsANoOpForPermanentKeys()
    {
        var oauth = new OpenRouterOAuth();
        var credential = new Credential.OAuth("", "key", 1);
        Assert.Same(credential, await oauth.RefreshAsync(credential));
    }
}

/// <summary>Kimi Code OAuth 流测试。</summary>
public class KimiCodingOAuthTests
{
    private const string OauthHost = "https://auth.kimi.com";

    private static HttpResponseMessage DeviceAuthorization()
        => FakeResponse.Json(new JsonObject
        {
            ["user_code"] = "ABCD-1234",
            ["device_code"] = "device-code-123",
            ["verification_uri"] = "https://www.kimi.com/code",
            ["verification_uri_complete"] = "https://www.kimi.com/code?user_code=ABCD-1234",
            ["interval"] = 0,
            ["expires_in"] = 600,
        });

    [Fact]
    public async Task DeviceFlowLoginCompletesAfterPending()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                _ => DeviceAuthorization(),
                _ => FakeResponse.Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.BadRequest),
                _ => FakeResponse.Json(new JsonObject
                {
                    ["access_token"] = "access-token",
                    ["refresh_token"] = "refresh-token",
                    ["expires_in"] = 3600,
                }));
            var oauth = new KimiCodingOAuth(new HttpClient(handler)) { Now = () => 1_000_000 };
            var interaction = new FakeInteraction();

            var credentials = await oauth.LoginAsync(interaction.ToInteraction());

            Assert.Equal("access-token", credentials.Access);
            Assert.Equal("refresh-token", credentials.Refresh);
            Assert.Equal(1_000_000 + 3600_000, credentials.Expires);

            var deviceCode = interaction.Events.OfType<AuthEvent.DeviceCode>().Single();
            Assert.Equal("ABCD-1234", deviceCode.UserCode);
            Assert.Equal("https://www.kimi.com/code?user_code=ABCD-1234", deviceCode.VerificationUri);

            Assert.Equal(3, handler.Requests.Count);
            var authorization = handler.Requests[0];
            Assert.Equal($"{OauthHost}/api/oauth/device_authorization", authorization.Url);
            Assert.Equal("17e5f671-d194-4dfb-9706-5516cb48c098", AssertX.Form(authorization.Body, "client_id"));
            var poll = handler.Requests[1];
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", AssertX.Form(poll.Body, "grant_type"));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task RefreshRetriesOnServerErrorsThenSucceeds()
    {
        var handler = new FakeHttpHandler(
            _ => FakeResponse.Text("boom", HttpStatusCode.InternalServerError),
            _ => FakeResponse.Text("boom", HttpStatusCode.InternalServerError),
            _ => FakeResponse.Json(new JsonObject
            {
                ["access_token"] = "new-access",
                ["refresh_token"] = "new-refresh",
                ["expires_in"] = 3600,
            }));
        var sleeps = new List<int>();
        var oauth = new KimiCodingOAuth(new HttpClient(handler))
        {
            Now = () => 5_000_000,
            SleepAsync = (ms, _) =>
            {
                sleeps.Add(ms);
                return Task.CompletedTask;
            },
        };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("old", "a", 1));

        Assert.Equal("new-access", refreshed.Access);
        Assert.Equal([1000, 2000], sleeps); // 指数退避 1s → 2s
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task UnauthorizedRefreshThrowsImmediately()
    {
        var handler = new FakeHttpHandler(
            _ => FakeResponse.Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.Unauthorized));
        var oauth = new KimiCodingOAuth(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.RefreshAsync(new Credential.OAuth("dead", "a", 1)));
        Assert.Contains("Kimi Code token refresh unauthorized (status 401)", error.Message);
    }

    [Fact]
    public async Task ToAuthSendsBearerHeader()
    {
        var oauth = new KimiCodingOAuth();
        var auth = await oauth.ToAuthAsync(new Credential.OAuth("r", "tok", 1));
        Assert.Equal("Bearer tok", auth.Headers!["Authorization"]);
    }
}

/// <summary>Meta OAuth 流测试。</summary>
public class MetaOAuthTests
{
    [Fact]
    public async Task DeviceFlowMintsApiKey()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                // 1. device authorization
                _ => FakeResponse.Json(new JsonObject
                {
                    ["device_code"] = "dc",
                    ["user_code"] = "USER-CODE",
                    ["verification_uri"] = "https://www.meta.com/device",
                    ["interval"] = 0,
                    ["expires_in"] = 600,
                }),
                // 2. poll → pending
                _ => FakeResponse.Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.BadRequest),
                // 3. poll → identity token
                _ => FakeResponse.Json(new JsonObject { ["access_token"] = "identity-token" }),
                // 4. mint
                _ => FakeResponse.Json(new JsonObject { ["api_key"] = "minted-key" }));
            var oauth = new MetaOAuth(new HttpClient(handler)) { Now = () => 86_400_000 };
            var interaction = new FakeInteraction();

            var credentials = await oauth.LoginAsync(interaction.ToInteraction());

            Assert.Equal("identity-token", credentials.Refresh);
            Assert.Equal("minted-key", credentials.Access);
            Assert.Equal(86_400_000 + 24 * 60 * 60 * 1000, credentials.Expires);

            var mint = handler.Requests[3];
            Assert.Equal("https://api.meta.ai/muse-code/key", mint.Url);
            Assert.Equal("Bearer identity-token", mint.Headers["Authorization"]);
            Assert.Equal("1.0.0", mint.Headers["x-api-version"]);
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task ExpiredSessionOnMintFails()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                _ => FakeResponse.Json(new JsonObject
                {
                    ["device_code"] = "dc",
                    ["user_code"] = "USER-CODE",
                    ["verification_uri"] = "https://www.meta.com/device",
                    ["expires_in"] = 600,
                }),
                _ => FakeResponse.Json(new JsonObject { ["access_token"] = "identity-token" }),
                _ => FakeResponse.Json(new JsonObject { ["detail"] = "stale" }, HttpStatusCode.Unauthorized));
            var oauth = new MetaOAuth(new HttpClient(handler));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                oauth.LoginAsync(new ProviderAuthInteraction(new FakeInteraction())));
            Assert.Contains("Meta session expired (status 401)", error.Message);
            Assert.Contains("stale", error.Message);
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task RefreshReMintsKey()
    {
        var handler = new FakeHttpHandler(
            _ => FakeResponse.Json(new JsonObject { ["api_key"] = "new-key" }));
        var oauth = new MetaOAuth(new HttpClient(handler)) { Now = () => 1 };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("identity", "old-key", 1));

        Assert.Equal("new-key", refreshed.Access);
        Assert.Equal("identity", refreshed.Refresh);
    }
}
