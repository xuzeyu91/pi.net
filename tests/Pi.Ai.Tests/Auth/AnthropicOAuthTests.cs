using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>Anthropic OAuth 流测试。对应 TS anthropic-oauth.test.ts 的核心用例。</summary>
public class AnthropicOAuthTests
{
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    private static FakeHttpHandler TokenHandler()
        => new(request =>
        {
            Assert.Equal(TokenUrl, request.RequestUri?.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            return FakeResponse.Json(new JsonObject
            {
                ["access_token"] = "access-token",
                ["refresh_token"] = "refresh-token",
                ["expires_in"] = 3600,
            });
        });

    private static JsonObject TokenBody(FakeHttpHandler handler)
    {
        var request = handler.Requests.Single();
        Assert.Equal("POST", request.Method);
        return JsonNode.Parse(request.Body!)!.AsObject();
    }

    [Fact]
    public async Task KeepsLocalhostRedirectUriForManualCallbackLogin()
    {
        var handler = TokenHandler();
        var oauth = new AnthropicOAuth(new HttpClient(handler)) { Now = () => 1_000_000 };
        var interaction = new FakeInteraction();
        string? authUrl = null;

        // notify 先于手动 prompt 触发，借助包装交互在 notify 时记录 authUrl。
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl authUrlEvent) authUrl = authUrlEvent.Url;
        });
        interaction.PromptHandler = async (prompt, _) => prompt switch
        {
            AuthPrompt.Select => "browser",
            AuthPrompt.ManualCode when authUrl is not null =>
                $"{AssertX.Query(authUrl!, "redirect_uri")}?code=manual-code&state={AssertX.Query(authUrl!, "state")}",
            _ => throw new InvalidOperationException("unexpected prompt"),
        };

        var credentials = await oauth.LoginAsync(captured.ToInteraction());

        Assert.Equal("access-token", credentials.Access);
        Assert.Equal("refresh-token", credentials.Refresh);
        Assert.Equal(1_000_000 + 3600_000 - 5 * 60_000, credentials.Expires);

        var body = TokenBody(handler);
        Assert.Equal("authorization_code", body.Str("grant_type"));
        Assert.Equal("manual-code", body.Str("code"));
        Assert.Equal("http://localhost:53692/callback", body.Str("redirect_uri"));
        Assert.Equal(AssertX.Query(authUrl!, "state"), body.Str("state"));
        Assert.Single(handler.Requests); // 端口 53692 的回调服务器未收到浏览器回调
    }

    [Fact]
    public async Task OffersBrowserFirstAndUsesSelectedCopyCodeFlow()
    {
        var handler = TokenHandler();
        var oauth = new AnthropicOAuth(new HttpClient(handler)) { Now = () => 1_000_000 };
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl authUrlEvent) authUrl = authUrlEvent.Url;
        });
        var selects = new List<AuthPrompt.Select>();
        interaction.PromptHandler = async (prompt, _) => prompt switch
        {
            AuthPrompt.Select select => Capture(select),
            AuthPrompt.ManualCode => $"copied-code#{AssertX.Query(authUrl!, "state")}",
            _ => throw new InvalidOperationException("unexpected prompt"),
        };
        string Capture(AuthPrompt.Select select)
        {
            selects.Add(select);
            return "copy_code";
        }

        var credentials = await oauth.LoginAsync(captured.ToInteraction());

        Assert.Equal("access-token", credentials.Access);
        Assert.Equal("refresh-token", credentials.Refresh);
        Assert.Equal("https://platform.claude.com/oauth/code/callback", AssertX.Query(authUrl!, "redirect_uri"));
        var body = TokenBody(handler);
        Assert.Equal("copied-code", body.Str("code"));
        Assert.Equal("https://platform.claude.com/oauth/code/callback", body.Str("redirect_uri"));
        Assert.Equal(AssertX.Query(authUrl!, "state"), body.Str("state"));

        var select = Assert.Single(selects);
        Assert.Equal("Select Anthropic login method:", select.Message);
        Assert.Equal(["browser", "copy_code"], select.Options.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task RejectsStateMismatchFromManualInput()
    {
        var handler = TokenHandler();
        var oauth = new AnthropicOAuth(new HttpClient(handler));
        var interaction = new FakeInteraction();
        string? authUrl = null;
        var captured = new CapturingInteraction(interaction, e =>
        {
            if (e is AuthEvent.AuthUrl authUrlEvent) authUrl = authUrlEvent.Url;
        });
        interaction.PromptHandler = async (prompt, _) => prompt switch
        {
            AuthPrompt.Select => "browser",
            AuthPrompt.ManualCode => $"http://localhost:53692/callback?code=x&state=TAMPERED",
            _ => throw new InvalidOperationException("unexpected prompt"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.LoginAsync(captured.ToInteraction()));
        Assert.Equal("OAuth state mismatch", error.Message);
        Assert.Empty(handler.Requests); // 未发起 token 交换
    }

    [Fact]
    public async Task RefreshesTokenWithRefreshGrant()
    {
        var handler = new FakeHttpHandler(_ => FakeResponse.Json(new JsonObject
        {
            ["access_token"] = "new-access",
            ["refresh_token"] = "new-refresh",
            ["expires_in"] = 3600,
        }));
        var oauth = new AnthropicOAuth(new HttpClient(handler)) { Now = () => 5_000_000 };

        var refreshed = await oauth.RefreshAsync(new Credential.OAuth("old-refresh", "old-access", 1));

        Assert.Equal("new-refresh", refreshed.Refresh);
        Assert.Equal("new-access", refreshed.Access);
        Assert.Equal(5_000_000 + 3600_000 - 5 * 60_000, refreshed.Expires);
        var request = handler.Requests.Single();
        Assert.Equal(TokenUrl, request.Url);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("refresh_token", body.Str("grant_type"));
        Assert.Equal("old-refresh", body.Str("refresh_token"));
    }

    [Fact]
    public async Task ToAuthUsesAccessTokenAsApiKey()
    {
        var oauth = new AnthropicOAuth();
        var auth = await oauth.ToAuthAsync(new Credential.OAuth("r", "a", 1));
        Assert.Equal("a", auth.ApiKey);
    }
}

