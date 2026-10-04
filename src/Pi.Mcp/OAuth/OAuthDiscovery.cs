using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.OAuth;

/// <summary>
/// OAuth 服务发现：WWW-Authenticate 挑战解析、受保护资源/授权服务器元数据探测。
/// 对应 TS <c>oauth/discovery.ts</c>。HTTP 抽象为 <see cref="McpFetch"/>（与 TS 的
/// McpFetch 同构），测试可注入假实现。
/// </summary>
public static class OAuthDiscovery
{
    /// <summary>HTTP 获取委托。对应 TS <c>McpFetch</c>。</summary>
    public delegate Task<HttpResponseMessage> McpFetch(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>4xx 与 502 表示「不在这里」，发现流程尝试下一个候选 URL。</summary>
    private static bool IsDiscoveryMiss(int status) => status is >= 400 and < 500 || status == 502;

    /// <summary><c>/.well-known/&lt;kind&gt;&lt;path&gt;</c> 的路径后缀；根路径为空串。</summary>
    private static string PathSuffix(string pathname) => pathname.EndsWith("/") ? pathname[..^1] : pathname;

    /// <summary>从 WWW-Authenticate 头解析单个参数（带引号或裸值）。对应 TS <c>field</c>。</summary>
    private static string? Field(string header, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            header, $@"(?:^|[,\s]){name}=(?:""([^""]*)""|([^\s,]+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // 空值（scope=""）无信息量，视为缺省。
        var captured = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : null;
        return string.IsNullOrEmpty(captured) ? null : captured;
    }

    /// <summary>解析 WWW-Authenticate 挑战。对应 TS <c>parseWwwAuthenticate</c>。</summary>
    public static OAuthTypes.Challenge ParseWwwAuthenticate(string? header)
    {
        if (string.IsNullOrEmpty(header)) return new OAuthTypes.Challenge();
        var scheme = header.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (scheme is not ("bearer" or "dpop")) return new OAuthTypes.Challenge();
        var resourceMetadata = Field(header, "resource_metadata");
        Uri? resourceMetadataUrl = null;
        if (resourceMetadata is not null && Uri.TryCreate(resourceMetadata, UriKind.Absolute, out var parsed))
            resourceMetadataUrl = parsed;
        return new OAuthTypes.Challenge(
            ResourceMetadataUrl: resourceMetadataUrl?.ToString(),
            Scope: Field(header, "scope"),
            Error: Field(header, "error"),
            ErrorDescription: Field(header, "error_description"));
    }

    private static async Task<HttpResponseMessage> FetchMetadataAsync(
        Uri url, McpFetch fetch, string protocolVersion, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        return await fetch(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发现受保护资源元数据：优先 <c>resourceMetadataUrl</c>，否则按路径后缀探测，
    /// 4xx/502 时回退到根路径。对应 TS <c>discoverProtectedResourceMetadata</c>。
    /// </summary>
    public static async Task<OAuthTypes.ProtectedResourceMetadata> DiscoverProtectedResourceMetadata(
        string serverUrl,
        string? resourceMetadataUrl = null,
        McpFetch? fetch = null,
        string? protocolVersion = null,
        CancellationToken cancellationToken = default)
    {
        var server = new Uri(serverUrl);
        var effectiveFetch = fetch ?? DefaultFetch;
        var version = protocolVersion ?? McpProtocolVersions.Latest;

        HttpResponseMessage response;
        if (resourceMetadataUrl is not null)
        {
            response = await FetchMetadataAsync(new Uri(resourceMetadataUrl), effectiveFetch, version, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var suffix = PathSuffix(server.AbsolutePath);
            response = await FetchMetadataAsync(
                new Uri(server, $"/.well-known/oauth-protected-resource{suffix}"), effectiveFetch, version,
                cancellationToken).ConfigureAwait(false);
            if (server.AbsolutePath != "/" && IsDiscoveryMiss((int)response.StatusCode))
            {
                // 路径后缀探测失败：回退到源根路径。
                response.Dispose();
                response = await FetchMetadataAsync(
                    new Uri(server, "/.well-known/oauth-protected-resource"), effectiveFetch, version,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"HTTP {(int)response.StatusCode} loading OAuth protected resource metadata");
            return OAuthTypes.ParseProtectedResourceMetadata(
                JsonNode.Parse(body) as JsonObject ?? throw new ArgumentException("Invalid OAuth protected resource metadata"));
        }
    }

    /// <summary>
    /// 构造授权服务器发现 URL 候选（oauth / oidc / 路径内 oidc）。
    /// 对应 TS <c>buildAuthorizationServerDiscoveryUrls</c>。
    /// </summary>
    public static IReadOnlyList<(Uri Url, string Type)> BuildAuthorizationServerDiscoveryUrls(string authorizationServerUrl)
    {
        var issuer = new Uri(authorizationServerUrl);
        var path = PathSuffix(issuer.AbsolutePath);
        var urls = new List<(Uri, string)>
        {
            (new Uri(issuer, $"/.well-known/oauth-authorization-server{path}"), "oauth"),
            (new Uri(issuer, $"/.well-known/openid-configuration{path}"), "oidc"),
        };
        if (path.Length > 0)
            urls.Add((new Uri(issuer, $"{path}/.well-known/openid-configuration"), "oidc"));
        return urls;
    }

    /// <summary>
    /// 发现授权服务器元数据：依次尝试候选 URL，4xx/502 继续下一个；
    /// 校验 issuer 与输入一致（可跳过）。对应 TS <c>discoverAuthorizationServerMetadata</c>。
    /// </summary>
    public static async Task<OAuthTypes.AuthorizationServerMetadata?> DiscoverAuthorizationServerMetadata(
        string authorizationServerUrl,
        McpFetch? fetch = null,
        string? protocolVersion = null,
        bool skipIssuerValidation = false,
        CancellationToken cancellationToken = default)
    {
        var effectiveFetch = fetch ?? DefaultFetch;
        foreach (var (url, _) in BuildAuthorizationServerDiscoveryUrls(authorizationServerUrl))
        {
            using var response = await FetchMetadataAsync(
                url, effectiveFetch, protocolVersion ?? McpProtocolVersions.Latest, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (IsDiscoveryMiss((int)response.StatusCode)) continue;
                throw new InvalidOperationException(
                    $"HTTP {(int)response.StatusCode} loading authorization server metadata from {url}");
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var metadata = OAuthTypes.ParseAuthorizationServerMetadata(
                JsonNode.Parse(body) as JsonObject ?? throw new ArgumentException("Invalid authorization server metadata"));
            if (!skipIssuerValidation)
            {
                var expected = authorizationServerUrl;
                // URL 解析会给裸源加尾斜杠，两侧统一去掉再比较。
                static string Trim(string value) => value.EndsWith("/") ? value[..^1] : value;
                if (Trim(metadata.Issuer) != Trim(expected))
                    throw new OAuthIssuerMismatchError(expected, metadata.Issuer);
            }
            return metadata;
        }
        return null;
    }

    /// <summary>根路径资源地址：去掉 hash。对应 TS <c>resourceUrlFromServerUrl</c>。</summary>
    public static string ResourceUrlFromServerUrl(string value)
    {
        var builder = new UriBuilder(value) { Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    private static async Task<HttpResponseMessage> DefaultFetch(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = new HttpClient();
        using (request) return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
