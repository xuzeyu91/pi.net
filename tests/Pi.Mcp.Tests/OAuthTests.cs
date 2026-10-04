using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.OAuth;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>OAuth 类型解析与发现流程测试。</summary>
public class OAuthTests
{
    [Fact]
    public void ParseWwwAuthenticateExtractsFields()
    {
        var header = "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\", " +
            "scope=\"files:read\", error=\"insufficient_scope\", error_description=\"expired\"";
        var challenge = OAuthDiscovery.ParseWwwAuthenticate(header);

        Assert.Equal("https://example.com/.well-known/oauth-protected-resource", challenge.ResourceMetadataUrl);
        Assert.Equal("files:read", challenge.Scope);
        Assert.Equal("insufficient_scope", challenge.Error);
        Assert.Equal("expired", challenge.ErrorDescription);

        // 非 bearer/dpop scheme 与空头都返回空挑战。
        Assert.Null(OAuthDiscovery.ParseWwwAuthenticate("Basic realm=x").Error);
        Assert.Null(OAuthDiscovery.ParseWwwAuthenticate(null).Scope);
        // 空值 scope="" 视为缺省。
        Assert.Null(OAuthDiscovery.ParseWwwAuthenticate("Bearer scope=\"\"").Scope);
    }

    [Fact]
    public void ParsesAuthorizationServerMetadata()
    {
        var metadata = OAuthTypes.ParseAuthorizationServerMetadata(new JsonObject
        {
            ["issuer"] = "https://auth.example.com",
            ["authorization_endpoint"] = "https://auth.example.com/authorize",
            ["token_endpoint"] = "https://auth.example.com/token",
            ["response_types_supported"] = new JsonArray("code"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["scope"] = "", // 空串视为缺省
        });
        Assert.Equal("https://auth.example.com", metadata.Issuer);
        Assert.Equal(["code"], metadata.RequiredResponseTypes);
        Assert.Equal(["S256"], metadata.CodeChallengeMethodsSupported);
        Assert.Null(metadata.ScopesSupported);
    }

    [Fact]
    public void MetadataValidationRejectsBadInput()
    {
        // 缺必填 response_types_supported。
        Assert.Throws<ArgumentException>(() => OAuthTypes.ParseAuthorizationServerMetadata(new JsonObject
        {
            ["issuer"] = "https://a.com",
            ["authorization_endpoint"] = "https://a.com/authorize",
            ["token_endpoint"] = "https://a.com/token",
        }));

        // javascript: 协议 URL 被拒绝。
        Assert.Throws<ArgumentException>(() => OAuthTypes.ParseAuthorizationServerMetadata(new JsonObject
        {
            ["issuer"] = "javascript:alert(1)",
            ["authorization_endpoint"] = "https://a.com/authorize",
            ["token_endpoint"] = "https://a.com/token",
            ["response_types_supported"] = new JsonArray("code"),
        }));

        // expires_in 的 null 视为缺省而非 0。
        var tokens = OAuthTypes.ParseOAuthTokens(new JsonObject
        {
            ["access_token"] = "at",
            ["token_type"] = "Bearer",
            ["expires_in"] = null,
            ["scope"] = "",
        });
        Assert.Null(tokens.ExpiresIn);
        Assert.Null(tokens.Scope);
    }

    [Fact]
    public void BuildsDiscoveryUrlCandidates()
    {
        var urls = OAuthDiscovery.BuildAuthorizationServerDiscoveryUrls("https://auth.example.com");
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://auth.example.com/.well-known/oauth-authorization-server", urls[0].Url.ToString());
        Assert.Equal("https://auth.example.com/.well-known/openid-configuration", urls[1].Url.ToString());

        // 带路径的 issuer 多一个路径内 oidc 候选（相对路径拼在 origin 之后）。
        var pathed = OAuthDiscovery.BuildAuthorizationServerDiscoveryUrls("https://auth.example.com/realms/main");
        Assert.Equal(3, pathed.Count);
        Assert.Equal("https://auth.example.com/realms/main/.well-known/openid-configuration", pathed[2].Url.ToString());
    }

    [Fact]
    public async Task DiscoveryValidatesIssuerAndTriesNextCandidate()
    {
        // 第一个候选 404（oauth metadata 不存在），第二个返回 issuer 不匹配的文档 → 抛 mismatch。
        var handler = new FakeHttpHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.Contains("oauth-authorization-server")
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"issuer":"https://other.example.com","authorization_endpoint":"https://other.example.com/a","token_endpoint":"https://other.example.com/t","response_types_supported":["code"]}""",
                        Encoding.UTF8, "application/json"),
                }));
        var fetch = new OAuthDiscovery.McpFetch(handler.SendAsync);
        await Assert.ThrowsAsync<OAuthIssuerMismatchError>(() =>
            OAuthDiscovery.DiscoverAuthorizationServerMetadata("https://auth.example.com", fetch));
    }

    [Fact]
    public async Task ProtectedResourceDiscoveryFallsBackToRoot()
    {
        // 路径后缀 404 → 回退根路径命中。
        var handler = new FakeHttpHandler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/mcp")
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"resource":"https://example.com/mcp","authorization_servers":["https://auth.example.com"]}""",
                        Encoding.UTF8, "application/json"),
                }));
        var metadata = await OAuthDiscovery.DiscoverProtectedResourceMetadata(
            "https://example.com/mcp", fetch: new OAuthDiscovery.McpFetch(handler.SendAsync));
        Assert.Equal("https://example.com/mcp", metadata.Resource);
        Assert.Equal(["https://auth.example.com"], metadata.AuthorizationServers);
    }

    /// <summary>可编程假 HTTP 处理器。</summary>
    private sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken _)
            => responder(request);
    }
}
