using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.OAuth;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>OAuth PKCE 流程与回调服务器端到端测试。</summary>
public class OAuthFlowTests
{
    /// <summary>记录调用的内存 provider。</summary>
    private sealed class TestProvider : OAuthFlow.IOAuthClientProvider
    {
        public string RedirectUrl { get; } = "http://localhost:8765/callback";
        public OAuthTypes.ClientMetadata ClientMetadata { get; } = new([]);
        public string? AuthorizationCode { get; set; }
        public string? IssuedVerifier { get; private set; }
        public Uri? RedirectedTo { get; private set; }
        public OAuthTypes.OAuthTokens? SavedTokens { get; private set; }
        public string? RegisteredScope { get; private set; }

        private OAuthTypes.ClientInformation? _registered;

        public Task<OAuthTypes.ClientInformation?> ClientInformation(CancellationToken cancellationToken = default)
            => Task.FromResult(_registered);

        public Task SaveClientInformation(
            OAuthTypes.ClientInformation information, CancellationToken cancellationToken = default)
        {
            _registered = information;
            Registered = information;
            return Task.CompletedTask;
        }

        public OAuthTypes.ClientInformation? Registered { get; private set; }

        public Task<OAuthTypes.OAuthTokens?> Tokens(CancellationToken cancellationToken = default)
            => Task.FromResult<OAuthTypes.OAuthTokens?>(null);

        public Task SaveTokens(OAuthTypes.OAuthTokens tokens, CancellationToken cancellationToken = default)
        {
            SavedTokens = tokens;
            return Task.CompletedTask;
        }

        public Task RedirectToAuthorization(Uri url, CancellationToken cancellationToken = default)
        {
            RedirectedTo = url;
            return Task.CompletedTask;
        }

        public Task SaveCodeVerifier(string verifier, CancellationToken cancellationToken = default)
        {
            IssuedVerifier = verifier;
            return Task.CompletedTask;
        }

        public Task<string> CodeVerifier(CancellationToken cancellationToken = default)
            => Task.FromResult(IssuedVerifier ?? throw new InvalidOperationException("no verifier"));
    }

    /// <summary>假授权服务器：注册 → 授权码换 token。</summary>
    private sealed class FakeAuthServer
    {
        public string? LastGrantType;
        public string? LastCodeVerifier;

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken _)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/register"))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"client_id":"client-1","client_secret":"secret-1","redirect_uris":["http://localhost:8765/callback"]}""",
                        Encoding.UTF8, "application/json"),
                });
            }
            if (path.EndsWith("/token"))
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var form = System.Web.HttpUtility.ParseQueryString(body);
                LastGrantType = form["grant_type"];
                LastCodeVerifier = form["code_verifier"];
                // 无 metadata 时默认 client_secret_basic：secret 在 Authorization 头里。
                Assert.Equal("Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("client-1:secret-1")),
                    request.Headers.Authorization?.ToString());
                var payload = new JsonObject
                {
                    ["access_token"] = "at-1",
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 3600,
                };
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task FullFlowRegistersExchangesAndSavesTokens()
    {
        var server = new FakeAuthServer();
        var provider = new TestProvider();
        var fetch = new OAuthDiscovery.McpFetch(server.SendAsync);

        // 第一轮：无授权码 → 动态注册 + 重定向（REDIRECT）。
        var result = await OAuthFlow.AuthorizeMcp(provider, new OAuthFlow.FlowOptions
        {
            ServerUrl = "https://api.example.com",
            Fetch = fetch,
            SkipIssuerValidation = true,
        });
        Assert.Equal(OAuthFlow.FlowResult.Redirect, result);
        Assert.NotNull(provider.Registered);
        Assert.NotNull(provider.RedirectedTo);
        Assert.Contains("code_challenge_method=S256", provider.RedirectedTo!.ToString());
        Assert.NotNull(provider.IssuedVerifier);

        // 第二轮：带授权码 → code 交换（verifier 必须与保存的一致）。
        result = await OAuthFlow.AuthorizeMcp(provider, new OAuthFlow.FlowOptions
        {
            ServerUrl = "https://api.example.com",
            AuthorizationCode = "auth-code-1",
            Fetch = fetch,
            SkipIssuerValidation = true,
        });
        Assert.Equal(OAuthFlow.FlowResult.Authorized, result);
        Assert.Equal("authorization_code", server.LastGrantType);
        Assert.Equal(provider.IssuedVerifier, server.LastCodeVerifier);
        Assert.Equal("at-1", provider.SavedTokens?.AccessToken);
    }

    [Fact]
    public void StepUpScopeMergesGrantedAndChallenged()
    {
        Assert.Equal("read write", OAuthFlow.StepUpScope("read", "write"));
        Assert.Equal("read", OAuthFlow.StepUpScope("read", "read"));
        Assert.Null(OAuthFlow.StepUpScope("read", null));
        Assert.Equal("write", OAuthFlow.StepUpScope(null, "write"));
    }

    [Fact]
    public async Task CallbackServerDeliversCodeAndRejectsBadState()
    {
        await using var server = await OAuthCallbackServer.ListenAsync(new OAuthCallbackServerOptions
        {
            Port = 0,
            TimeoutMs = 5000,
        });

        var pending = server.WaitForCallbackAsync("state-1");
        // 模拟浏览器回调。
        using var client = new HttpClient();
        var callbackUrl = server.RedirectUrl.Replace("/callback", $"/callback?code=abc&state=state-1&iss=https%3A%2F%2Fauth.example.com");
        var response = await client.GetAsync(callbackUrl);
        Assert.True(response.IsSuccessStatusCode);
        var callback = await pending;
        Assert.Equal("abc", callback.Code);
        Assert.Equal("state-1", callback.State);
        Assert.Equal("https://auth.example.com", callback.Iss);

        // 未知 state → 400。
        var bad = await client.GetAsync(server.RedirectUrl + "?code=x&state=unknown");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task MemoryStoreSerializesWritesAndIsolatesServers()
    {
        var store = new MemoryOAuthStateStore();
        var providerA = new McpOAuthProvider("https://a.example.com", "http://localhost:1/cb",
            new OAuthTypes.ClientMetadata([]), (_, _) => Task.CompletedTask, store: store);
        var providerB = new McpOAuthProvider("https://b.example.com", "http://localhost:2/cb",
            new OAuthTypes.ClientMetadata([]), (_, _) => Task.CompletedTask, store: store);

        await providerA.SaveTokens(new OAuthTypes.OAuthTokens("token-a", "Bearer"));
        Assert.Equal("token-a", (await providerA.Tokens())?.AccessToken);
        Assert.Null(await providerB.Tokens()); // 跨服务器隔离

        // 并发写串行化：最后保存的胜出且不互相覆盖丢失。
        await Task.WhenAll(
            providerA.SaveTokens(new OAuthTypes.OAuthTokens("t1", "Bearer", ExpiresIn: 10)),
            providerA.SaveTokens(new OAuthTypes.OAuthTokens("t2", "Bearer", ExpiresIn: 20)));
        Assert.Equal("t2", (await providerA.Tokens())?.AccessToken);
        Assert.NotNull((await providerA.Tokens())?.ExpiresIn);
    }
}
