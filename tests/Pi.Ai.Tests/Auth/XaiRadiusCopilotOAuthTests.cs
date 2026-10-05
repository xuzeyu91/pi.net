using Pi.Ai.Utils;
using System.Net;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>xAI OAuth 流测试。</summary>
public class XaiOAuthTests
{
    private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";

    private static HttpResponseMessage DeviceCodeResponse(string verificationUri = "https://accounts.x.ai/device")
        => FakeResponse.Json(new JsonObject
        {
            ["device_code"] = "dc-1",
            ["user_code"] = "GROK-CODE",
            ["verification_uri"] = verificationUri,
            ["expires_in"] = 600,
        });

    private static HttpResponseMessage TokenResponse(string refreshToken = "rt-1", long expiresIn = 3600)
    {
        var body = new JsonObject
        {
            ["access_token"] = "at-1",
            ["expires_in"] = expiresIn,
        };
        if (refreshToken is not null) body["refresh_token"] = refreshToken;
        return FakeResponse.Json(body);
    }

    [Fact]
    public async Task DeviceFlowLoginPollsUntilAuthorized()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                _ => DeviceCodeResponse(),
                _ => FakeResponse.Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.BadRequest),
                _ => TokenResponse());
            var oauth = new XaiOAuth(new HttpClient(handler)) { Now = () => 1_000_000 };
            var interaction = new FakeInteraction();

            var credentials = await oauth.LoginAsync(interaction.ToInteraction());

            Assert.Equal("at-1", credentials.Access);
            Assert.Equal("rt-1", credentials.Refresh);
            Assert.Equal(1_000_000 + 3600_000 - 5 * 60_000, credentials.Expires); // 5 分钟提前量

            var deviceCode = interaction.Events.OfType<AuthEvent.DeviceCode>().Single();
            Assert.Equal("GROK-CODE", deviceCode.UserCode);

            var start = handler.Requests[0];
            Assert.Equal(DeviceCodeUrl, start.Url);
            Assert.Equal("pi", AssertX.Form(start.Body, "referrer"));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task RejectsNonHttpsVerificationUri()
    {
        var handler = new FakeHttpHandler(_ => DeviceCodeResponse("http://evil.example/device"));
        var oauth = new XaiOAuth(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oauth.LoginAsync(new ProviderAuthInteraction(new FakeInteraction())));
        Assert.Equal("Untrusted verification URI in xAI OAuth response", error.Message);
    }

    [Fact]
    public async Task RefreshKeepsUnrotatedRefreshToken()
    {
        var handler = new FakeHttpHandler(_ => TokenResponse(refreshToken: null!));
        var oauth = new XaiOAuth(new HttpClient(handler)) { Now = () => 2_000_000 };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("keep-me", "old", 1));

        Assert.Equal("keep-me", refreshed.Refresh); // 未轮换：保留旧 refresh token
        Assert.Equal("at-1", refreshed.Access);
        Assert.Equal("refresh_token", AssertX.Form(handler.Requests.Single().Body, "grant_type"));
        Assert.Equal("keep-me", AssertX.Form(handler.Requests.Single().Body, "refresh_token"));
    }
}

/// <summary>Radius 网关 OAuth 流测试。</summary>
public class RadiusOAuthTests
{
    private const string Gateway = "https://gw.test";

    private static HttpResponseMessage TokenResponse()
        => FakeResponse.Json(new JsonObject
        {
            ["access_token"] = "gw-access",
            ["refresh_token"] = "gw-refresh",
            ["expires_in"] = 3600,
            ["scope"] = "gateway",
        });

    [Fact]
    public async Task DeviceCodeLoginPollsUntilAuthorized()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                // 1. device authorization
                _ => FakeResponse.Json(new JsonObject
                {
                    ["device_code"] = "dc",
                    ["user_code"] = "USER",
                    ["verification_uri"] = $"{Gateway}/activate",
                    ["expires_in"] = 600,
                    ["interval"] = 0,
                }),
                // 2. poll → pending（OAuth error 语义）
                _ => FakeResponse.Json(new JsonObject { ["error"] = "authorization_pending" }, HttpStatusCode.Forbidden),
                // 3. poll → tokens
                _ => TokenResponse());
            var oauth = new RadiusOAuth(new RadiusOAuth.Options("Radius", Gateway), new HttpClient(handler))
            {
                Now = () => 1_000_000,
            };
            var interaction = new FakeInteraction { PromptHandler = async (_, _) => "device-code" };

            var credentials = await oauth.LoginAsync(interaction.ToInteraction());

            Assert.Equal("gw-access", credentials.Access);
            Assert.Equal("gw-refresh", credentials.Refresh);
            Assert.Equal("gateway", credentials.Scope);
            Assert.Equal(1_000_000 + 3600_000 - 60_000, credentials.Expires); // 60 秒 skew

            var start = handler.Requests[0];
            Assert.Equal($"{Gateway}/v1/oauth/device", start.Url);
            Assert.Equal("pi-gateway", AssertX.Form(start.Body, "client_id"));
            Assert.Equal("gateway offline_access", AssertX.Form(start.Body, "scope"));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task BrowserLoginReceivesRealLoopbackCallback()
    {
        try
        {
            var handler = new FakeHttpHandler(
                // 1. discovery
                _ => FakeResponse.Json(new JsonObject { ["authorizationEndpoint"] = $"{Gateway}/authorize" }),
                // 2. token exchange（浏览器回调触发）
                _ => TokenResponse());
            var oauth = new RadiusOAuth(new RadiusOAuth.Options("Radius", Gateway), new HttpClient(handler))
            {
                Now = () => 1_000_000,
            };
            var interaction = new FakeInteraction { PromptHandler = async (_, _) => "browser" };
            string? authUrl = null;
            var captured = new CapturingInteraction(interaction, e =>
            {
                if (e is AuthEvent.AuthUrl url) authUrl = url.Url;
            });

            var loginTask = oauth.LoginAsync(captured.ToInteraction());
            // 等浏览器授权地址就绪，再以真实 HTTP 回调 loopback 服务器。
            while (authUrl is null) await Task.Delay(10);
            using var http = new HttpClient();
            var callbackUrl = $"http://127.0.0.1:1456/oauth/callback?code=cb&state={AssertX.Query(authUrl!, "state")}";
            await http.GetAsync(callbackUrl);

            var credentials = await loginTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("gw-access", credentials.Access);

            var exchange = handler.Requests[1];
            Assert.Equal($"{Gateway}/v1/oauth/token", exchange.Url);
            Assert.Equal("authorization_code", AssertX.Form(exchange.Body, "grant_type"));
            Assert.Equal("cb", AssertX.Form(exchange.Body, "code"));
            Assert.Equal("http://127.0.0.1:1456/oauth/callback", AssertX.Form(exchange.Body, "redirect_uri"));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task RefreshUsesRefreshGrant()
    {
        var handler = new FakeHttpHandler(_ => TokenResponse());
        var oauth = new RadiusOAuth(new RadiusOAuth.Options("Radius", Gateway), new HttpClient(handler))
        {
            Now = () => 1_000_000,
        };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("r", "a", 1));

        Assert.Equal("gw-refresh", refreshed.Refresh);
        Assert.Equal("refresh_token", AssertX.Form(handler.Requests.Single().Body, "grant_type"));
    }

    [Fact]
    public void NormalizesGatewayUrls()
    {
        Assert.Equal("https://gw.test", RadiusConfig.NormalizeGatewayUrl("gw.test"));
        Assert.Equal("https://gw.test", RadiusConfig.NormalizeGatewayUrl("https://gw.test///"));
        Assert.Equal("http://gw.test", RadiusConfig.NormalizeGatewayUrl("http://gw.test"));
    }
}

/// <summary>GitHub Copilot OAuth 流测试。</summary>
public class GitHubCopilotOAuthTests
{
    private static HttpResponseMessage DeviceCode()
        => FakeResponse.Json(new JsonObject
        {
            ["device_code"] = "dc",
            ["user_code"] = "PANEL-WORD",
            ["verification_uri"] = "https://github.com/login/device",
            ["expires_in"] = 900,
            ["interval"] = 0,
        });

    private static HttpResponseMessage CopilotToken()
        => FakeResponse.Json(new JsonObject
        {
            ["token"] = "tid=1;exp=2;proxy-ep=proxy.individual.githubcopilot.com;",
            ["expires_at"] = DateTimeOffset.Now.ToUnixTimeSeconds() + 1800,
        });

    private static HttpResponseMessage ModelsResponse()
        => FakeResponse.Json(new JsonObject
        {
            ["data"] = new JsonArray(
                new JsonObject { ["id"] = "picker-model", ["model_picker_enabled"] = true, ["policy"] = new JsonObject { ["state"] = "enabled" } },
                new JsonObject { ["id"] = "policy-model", ["model_picker_enabled"] = true, ["policy"] = new JsonObject { ["state"] = "unconfigured" } },
                new JsonObject
                {
                    ["id"] = "toolless-model",
                    ["model_picker_enabled"] = true,
                    ["capabilities"] = new JsonObject { ["supports"] = new JsonObject { ["tool_calls"] = false } },
                }),
        });

    [Fact]
    public async Task FullLoginEnablesUnconfiguredKnownModels()
    {
        try
        {
            DeviceCodeFlow.DefaultSleepAsync = (_, _) => Task.CompletedTask;
            var handler = new FakeHttpHandler(
                // 1. device code
                _ => DeviceCode(),
                // 2. GitHub access token poll
                _ => FakeResponse.Json(new JsonObject { ["access_token"] = "ghu-token" }),
                // 3. copilot token
                _ => CopilotToken(),
                // 4. models
                _ => ModelsResponse(),
                // 5. policy enable
                _ => FakeResponse.Json([]));
            var oauth = new GitHubCopilotOAuth(new HttpClient(handler), knownChatModelIds: new HashSet<string> { "policy-model" });
            var interaction = new FakeInteraction { PromptHandler = async (_, _) => "" }; // 空 = github.com

            var credentials = await oauth.LoginAsync(interaction.ToInteraction());

            Assert.Equal("tid=1;exp=2;proxy-ep=proxy.individual.githubcopilot.com;", credentials.Access);
            // 可用 = picker 可用 + 启用成功的 policy 模型；tool_calls=false 的被剔除。
            Assert.Equal(["picker-model", "policy-model"], credentials.AvailableModelIds);

            var enable = handler.Requests[4];
            Assert.Equal("https://api.individual.githubcopilot.com/models/policy-model/policy", enable.Url);
            Assert.Contains("chat-policy", enable.Headers["openai-intent"]);
            Assert.Contains("Enabling models...", interaction.Events.OfType<AuthEvent.Progress>().Select(e => e.Message));
        }
        finally
        {
            DeviceCodeFlow.DefaultSleepAsync = null;
        }
    }

    [Fact]
    public async Task ToAuthDerivesBaseUrlFromProxyEndpoint()
    {
        var oauth = new GitHubCopilotOAuth();
        var credential = new Credential.OAuth("r", "tid=1;exp=2;proxy-ep=proxy.individual.githubcopilot.com;", 1);
        var auth = await oauth.ToAuthAsync(credential);
        Assert.Equal("https://api.individual.githubcopilot.com", auth.BaseUrl);

        var enterprise = new Credential.OAuth("r", "plain-token", 1) { EnterpriseUrl = "company.ghe.com" };
        var auth2 = await oauth.ToAuthAsync(enterprise);
        Assert.Equal("https://copilot-api.company.ghe.com", auth2.BaseUrl);
    }

    [Fact]
    public async Task RefreshFetchesModelsAndStoresAvailableIds()
    {
        var handler = new FakeHttpHandler(
            // 1. copilot token
            _ => CopilotToken(),
            // 2. models
            _ => ModelsResponse());
        var oauth = new GitHubCopilotOAuth(new HttpClient(handler), knownChatModelIds: new HashSet<string>());

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("ghu-refresh", "old", 1));

        Assert.Equal("ghu-refresh", refreshed.Refresh);
        // 空 known 表只影响 policy 回退；picker-enabled 的模型仍计入 available。
        Assert.Equal(["picker-model", "policy-model"], refreshed.AvailableModelIds);
        var copilotTokenRequest = handler.Requests[0];
        Assert.Equal("https://api.github.com/copilot_internal/v2/token", copilotTokenRequest.Url);
        Assert.Equal("Bearer ghu-refresh", copilotTokenRequest.Headers["Authorization"]);
        Assert.Equal("https://api.individual.githubcopilot.com/models", handler.Requests[1].Url);
    }
}
