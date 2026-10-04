using Headers = System.Net.Http.Headers.HttpRequestHeaders;
using System.Net.Http.Headers;
using System.Web;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Mcp.OAuth;

/// <summary>给 MCP HTTP 传输供应 bearer token，并可在 401 后刷新。对应 TS <c>auth-provider.ts</c>。</summary>
public interface AuthProvider
{
    /// <summary>当前 access token（无则 null）。</summary>
    Task<string?> Token(CancellationToken cancellationToken = default);
}

/// <summary>
/// OAuth PKCE 授权码流程。对应 TS <c>oauth/flow.ts</c>（改编自
/// modelcontextprotocol/typescript-sdk v1.29.0）。核心入口 <see cref="AuthorizeMcp"/>：
/// 发现 → 客户端注册/复用 → 刷新或新授权 → 失败凭据失效重试。
/// </summary>
public static class OAuthFlow
{
    /// <summary>补充客户端认证（Basic 头或表单字段）的钩子。对应 TS <c>AddClientAuthentication</c>。</summary>
    public delegate Task AddClientAuthentication(
        Headers headers, Dictionary<string, string> parameters, Uri url,
        OAuthTypes.AuthorizationServerMetadata? metadata);

    /// <summary>客户端 ID 元数据文档：用作 client_id 的 https URL 及其列出的 redirect URI。</summary>
    public sealed record ClientMetadataDocument(string Url, string RedirectUrl);

    /// <summary>流程结果：AUTHORIZED（已拿到令牌）或 REDIRECT（需浏览器授权）。</summary>
    public enum FlowResult { Authorized, Redirect }

    /// <summary>token 端点请求所需的公共参数。对应 TS <c>TokenRequestOptions</c>。</summary>
    public sealed record TokenRequestOptions(
        OAuthTypes.AuthorizationServerMetadata? Metadata,
        OAuthTypes.ClientInformation ClientInformation,
        string? Resource = null,
        AddClientAuthentication? AddClientAuthentication = null,
        OAuthDiscovery.McpFetch? Fetch = null);

    /// <summary>流程选项。对应 TS <c>OAuthFlowOptions</c>。</summary>
    public sealed record FlowOptions
    {
        public required string ServerUrl { get; init; }
        public string? AuthorizationCode { get; init; }

        /// <summary> delivering authorizationCode 的授权响应的 iss 参数（RFC 9207）。</summary>
        public string? Iss { get; init; }
        public string? Scope { get; init; }
        public string? ResourceMetadataUrl { get; init; }

        /// <summary>替代发现流程的授权服务器元数据文档（信任配置；须 https，回环除外）。</summary>
        public string? AuthorizationServerMetadataUrl { get; init; }
        public OAuthDiscovery.McpFetch? Fetch { get; init; }
        public bool SkipIssuerValidation { get; init; }

        /// <summary>跳过刷新直奔授权重定向（如服务器要求当前 grant 缺少的 scope）。</summary>
        public bool SkipRefresh { get; init; }
    }

    /// <summary>客户端提供者契约（凭据存储 + 重定向）。对应 TS <c>OAuthClientProvider</c>。</summary>
    public interface IOAuthClientProvider
    {
        string RedirectUrl { get; }
        OAuthTypes.ClientMetadata ClientMetadata { get; }

        /// <summary>改用 Client ID Metadata Document 标识而非动态注册；返回 null 则注册。</summary>
        ClientMetadataDocument? ClientMetadataDocument(OAuthTypes.AuthorizationServerMetadata? metadata) => null;

        Task<string?> State(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        Task<OAuthTypes.ClientInformation?> ClientInformation(CancellationToken cancellationToken = default);
        Task SaveClientInformation(OAuthTypes.ClientInformation information, CancellationToken cancellationToken = default);
        Task<OAuthTypes.OAuthTokens?> Tokens(CancellationToken cancellationToken = default);
        Task SaveTokens(OAuthTypes.OAuthTokens tokens, CancellationToken cancellationToken = default);
        Task RedirectToAuthorization(Uri url, CancellationToken cancellationToken = default);
        Task SaveCodeVerifier(string verifier, CancellationToken cancellationToken = default);
        Task<string> CodeVerifier(CancellationToken cancellationToken = default);
        Task InvalidateCredentials(string kind, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task SaveDiscoveryState(OAuthTypes.DiscoveryState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        Task<OAuthTypes.DiscoveryState?> DiscoveryState(CancellationToken cancellationToken = default) => Task.FromResult<OAuthTypes.DiscoveryState?>(null);
        AddClientAuthentication? AddClientAuthenticationHook => null;
    }

    // ─── 端点安全与客户端认证 ────────────────────────────────────────────

    private static bool Loopback(string hostname)
        => hostname is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>端点必须 https（回环地址除外）。对应 TS <c>secureEndpoint</c>。</summary>
    private static Uri SecureEndpoint(string value)
    {
        var url = new Uri(value);
        if (url.Scheme != "https" && !Loopback(url.Host))
            throw new OAuthInsecureEndpointError(url.ToString());
        return url;
    }

    private enum ClientAuthMethod { ClientSecretBasic, ClientSecretPost, None }

    private static ClientAuthMethod SelectClientAuthMethod(
        OAuthTypes.ClientInformation information, IReadOnlyList<string>? supported)
    {
        var supportedList = supported ?? [];
        if (supportedList.Count == 0)
            return information.ClientSecret is not null ? ClientAuthMethod.ClientSecretBasic : ClientAuthMethod.None;
        if (information.ClientSecret is not null && supportedList.Contains("client_secret_basic"))
            return ClientAuthMethod.ClientSecretBasic;
        if (information.ClientSecret is not null && supportedList.Contains("client_secret_post"))
            return ClientAuthMethod.ClientSecretPost;
        if (supportedList.Contains("none")) return ClientAuthMethod.None;
        return information.ClientSecret is not null ? ClientAuthMethod.ClientSecretPost : ClientAuthMethod.None;
    }

    private static void ApplyClientAuthentication(
        ClientAuthMethod method, OAuthTypes.ClientInformation information,
        Headers headers, Dictionary<string, string> parameters)
    {
        if (method == ClientAuthMethod.ClientSecretBasic)
        {
            if (information.ClientSecret is null)
                throw new InvalidOperationException("client_secret_basic requires a client secret");
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{information.ClientId}:{information.ClientSecret}"));
            headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }
        else
        {
            parameters["client_id"] = information.ClientId;
            if (method == ClientAuthMethod.ClientSecretPost && information.ClientSecret is not null)
                parameters["client_secret"] = information.ClientSecret;
        }
    }

    // ─── PKCE ───────────────────────────────────────────────────────────

    /// <summary>生成 PKCE verifier 与 S256 challenge。对应 TS <c>pkce()</c>。</summary>
    private static (string Verifier, string Challenge) Pkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(bytes);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return (verifier, Base64UrlEncode(digest));
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ─── 授权与令牌端点 ─────────────────────────────────────────────────

    /// <summary>构造授权 URL 并生成 verifier。对应 TS <c>startAuthorization</c>。</summary>
    public static async Task<(Uri AuthorizationUrl, string CodeVerifier)> StartAuthorizationAsync(
        string authorizationServerUrl,
        OAuthTypes.AuthorizationServerMetadata? metadata,
        OAuthTypes.ClientInformation clientInformation,
        string redirectUrl,
        string? scope = null,
        string? state = null,
        string? resource = null,
        CancellationToken cancellationToken = default)
    {
        if (metadata is not null && !metadata.RequiredResponseTypes.Contains("code"))
            throw new InvalidOperationException("Authorization server does not support authorization codes");
        if (metadata?.CodeChallengeMethodsSupported is { } methods && !methods.Contains("S256"))
            throw new InvalidOperationException("Authorization server does not support PKCE S256");

        var url = metadata?.AuthorizationEndpoint is { } endpoint
            ? new Uri(endpoint) : new Uri(new Uri(authorizationServerUrl), "/authorize");
        var (verifier, challenge) = Pkce();
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = clientInformation.ClientId;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        query["redirect_uri"] = redirectUrl;
        if (state is not null) query["state"] = state;
        if (scope is not null)
        {
            query["scope"] = scope;
            if (scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
                query["prompt"] = "consent";
        }
        if (resource is not null) query["resource"] = resource;

        var builder = new UriBuilder(url) { Query = query.ToString() };
        return (builder.Uri, verifier);
    }

    /// <summary>令牌端点请求公共路径。对应 TS <c>tokenRequest</c>（先查 body 再查状态码）。</summary>
    private static async Task<OAuthTypes.OAuthTokens> TokenRequestAsync(
        string authorizationServerUrl, TokenRequestOptions options,
        Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var url = SecureEndpoint(options.Metadata?.TokenEndpoint
            ?? new Uri(new Uri(authorizationServerUrl), "/token").ToString());
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.ParseAdd("application/json");

        if (options.AddClientAuthentication is not null)
        {
            await options.AddClientAuthentication(request.Headers, parameters, url, options.Metadata)
                .ConfigureAwait(false);
        }
        else
        {
            ApplyClientAuthentication(
                SelectClientAuthMethod(options.ClientInformation, options.Metadata?.TokenEndpointAuthMethodsSupported),
                options.ClientInformation, request.Headers, parameters);
        }
        // 认证字段写入后再序列化表单（resource 追加也在此处）。
        var form = options.Resource is not null
            ? new Dictionary<string, string>(parameters) { ["resource"] = options.Resource } : parameters;
        request.Content = new FormUrlEncodedContent(form);

        var fetch = options.Fetch ?? new OAuthDiscovery.McpFetch(async (r, ct) =>
        {
            using var client = new HttpClient();
            return await client.SendAsync(r, ct).ConfigureAwait(false);
        });
        using var response = await fetch(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 服务器可能用任何状态码报告 OAuth 错误，所以先查 body 再查状态码。
        JsonNode? value = null;
        try { value = JsonNode.Parse(text); } catch { }
        if (value is JsonObject obj && obj["error"] is JsonValue { } errorValue
            && errorValue.TryGetValue<string>(out var error))
        {
            var description = obj["error_description"] is JsonValue { } dv && dv.TryGetValue<string>(out var d) ? d : error;
            var errorUri = obj["error_uri"] is JsonValue { } uv && uv.TryGetValue<string>(out var u) ? u : null;
            throw new OAuthError(error, description, errorUri);
        }
        if (!response.IsSuccessStatusCode)
            throw new OAuthError("server_error", $"HTTP {(int)response.StatusCode}: {text}");
        return OAuthTypes.ParseOAuthTokens(
            value as JsonObject ?? throw new ArgumentException("Invalid token response"));
    }

    /// <summary>动态客户端注册。对应 TS <c>registerClient</c>。</summary>
    public static async Task<OAuthTypes.ClientInformation> RegisterClientAsync(
        string authorizationServerUrl,
        OAuthTypes.AuthorizationServerMetadata? metadata,
        OAuthTypes.ClientMetadata clientMetadata,
        string? scope = null,
        OAuthDiscovery.McpFetch? fetch = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = metadata?.RegistrationEndpoint;
        if (metadata is not null && endpoint is null)
            throw new InvalidOperationException("Authorization server does not support dynamic client registration");
        var url = endpoint ?? new Uri(new Uri(authorizationServerUrl), "/register").ToString();

        var body = new JsonObject
        {
            ["redirect_uris"] = new JsonArray(clientMetadata.RedirectUris.Select(u => JsonValue.Create(u)).ToArray()),
        };
        if (clientMetadata.TokenEndpointAuthMethod is not null) body["token_endpoint_auth_method"] = clientMetadata.TokenEndpointAuthMethod;
        if (clientMetadata.ClientName is not null) body["client_name"] = clientMetadata.ClientName;
        if (clientMetadata.Scope is not null) body["scope"] = clientMetadata.Scope;
        if (scope is not null) body["scope"] = scope;

        var effectiveFetch = fetch ?? new OAuthDiscovery.McpFetch(async (r, ct) =>
        {
            using var client = new HttpClient();
            return await client.SendAsync(r, ct).ConfigureAwait(false);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await effectiveFetch(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OAuthRegistrationError((int)response.StatusCode, text);
        return OAuthTypes.ParseClientInformation(
            JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("Invalid client registration response"));
    }

    /// <summary>授权码换取令牌。对应 TS <c>exchangeAuthorizationCode</c>。</summary>
    public static Task<OAuthTypes.OAuthTokens> ExchangeAuthorizationCodeAsync(
        string authorizationServerUrl, TokenRequestOptions options,
        string code, string codeVerifier, string redirectUrl, CancellationToken cancellationToken = default)
        => TokenRequestAsync(authorizationServerUrl, options, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUrl,
        }, cancellationToken);

    /// <summary>刷新令牌（响应缺 refresh_token 时保留旧值）。对应 TS <c>refreshAuthorization</c>。</summary>
    public static async Task<OAuthTypes.OAuthTokens> RefreshAuthorizationAsync(
        string authorizationServerUrl, TokenRequestOptions options,
        string refreshToken, CancellationToken cancellationToken = default)
    {
        var tokens = await TokenRequestAsync(authorizationServerUrl, options, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken).ConfigureAwait(false);
        return tokens with { RefreshToken = tokens.RefreshToken ?? refreshToken };
    }

    /// <summary>响应缺 scope 时以请求的 scope 补齐（RFC 6749 §5.1）。</summary>
    private static OAuthTypes.OAuthTokens WithScope(OAuthTypes.OAuthTokens tokens, string? scope)
        => tokens.Scope is null && scope is not null ? tokens with { Scope = scope } : tokens;

    /// <summary>
    /// step-up 授权的 scope：被挑战的 scope 加上已授予的（挑战可能只列缺失项，
    /// 只拿缺失项会丢掉旧 token 的权限，SEP-2350）。
    /// </summary>
    public static string? StepUpScope(string? granted, string? challenged)
    {
        if (challenged is null) return null;
        var scopes = new List<string>();
        foreach (var part in new[] { granted, challenged })
            scopes.AddRange(part?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []);
        return string.Join(" ", scopes.Distinct());
    }

    // ─── 主流程 ─────────────────────────────────────────────────────────

    private static async Task<FlowResult> RunFlowAsync(
        IOAuthClientProvider provider, FlowOptions options, CancellationToken cancellationToken)
    {
        var metadataUrl = options.AuthorizationServerMetadataUrl is not null
            ? SecureEndpoint(options.AuthorizationServerMetadataUrl).ToString() : null;
        // 配置了元数据 URL 时不缓存发现结果——换 URL 立即生效。
        var cached = metadataUrl is not null ? null : await provider.DiscoveryState(cancellationToken).ConfigureAwait(false);
        var (authorizationServerUrl, metadata, resourceMetadata) = cached?.AuthorizationServerUrl is not null
            ? (cached.AuthorizationServerUrl,
                cached.AuthorizationServerMetadata
                    ?? await OAuthDiscovery.DiscoverAuthorizationServerMetadata(
                        cached.AuthorizationServerUrl, options.Fetch,
                        skipIssuerValidation: options.SkipIssuerValidation, cancellationToken: cancellationToken)
                        .ConfigureAwait(false),
                cached.ResourceMetadata)
            : await DiscoverServerInfoAsync(options, metadataUrl, cancellationToken).ConfigureAwait(false);

        if (metadataUrl is null)
        {
            await provider.SaveDiscoveryState(new OAuthTypes.DiscoveryState(
                authorizationServerUrl, metadata, resourceMetadata,
                options.ResourceMetadataUrl), cancellationToken).ConfigureAwait(false);
        }
        var resource = SelectResource(options.ServerUrl, resourceMetadata);
        // 对齐 TS 的 || 链：空 scope（scopes_supported: []）应落到下一个来源。
        string? scope = options.Scope;
        if (string.IsNullOrEmpty(scope))
        {
            var supported = resourceMetadata?.ScopesSupported;
            scope = supported is { Count: > 0 } ? string.Join(" ", supported) : provider.ClientMetadata.Scope;
        }

        var stored = await provider.ClientInformation(cancellationToken).ConfigureAwait(false);
        var clientDocument = stored is null ? provider.ClientMetadataDocument(metadata) : null;
        if (clientDocument is not null)
        {
            var documentUrl = new Uri(clientDocument.Url);
            if (documentUrl.Scheme != "https:" || documentUrl.AbsolutePath == "/")
                throw new InvalidOperationException("Invalid OAuth client metadata URL");
        }
        var client = stored;
        if (client is null && clientDocument is not null)
            client = new OAuthTypes.ClientInformation(ClientId: clientDocument.Url);
        if (client is null)
        {
            if (options.AuthorizationCode is not null)
                throw new InvalidOperationException("OAuth client information is missing during code exchange");
            client = await RegisterClientAsync(authorizationServerUrl, metadata, provider.ClientMetadata,
                scope, options.Fetch, cancellationToken).ConfigureAwait(false);
            await provider.SaveClientInformation(client, cancellationToken).ConfigureAwait(false);
        }
        // 文档的 redirect URI 可能与 provider 的不同（例如服务器专用路径）。
        var redirectUrl = clientDocument?.RedirectUrl ?? provider.RedirectUrl;
        var tokenOptions = new TokenRequestOptions(metadata, client, resource,
            provider.AddClientAuthenticationHook, options.Fetch);

        if (options.AuthorizationCode is not null)
        {
            // RFC 9207：绝不把其他授权服务器发的 code 发给这一台。
            var iss = options.Iss;
            if (metadata is not null && (iss is not null || metadata.AuthorizationResponseIssParameterSupported == true)
                && iss != metadata.Issuer)
            {
                throw new OAuthIssuerMismatchError(metadata.Issuer, iss);
            }
            var tokens = await ExchangeAuthorizationCodeAsync(authorizationServerUrl, tokenOptions,
                options.AuthorizationCode, await provider.CodeVerifier(cancellationToken).ConfigureAwait(false),
                redirectUrl, cancellationToken).ConfigureAwait(false);
            await provider.SaveTokens(WithScope(tokens, scope), cancellationToken).ConfigureAwait(false);
            return FlowResult.Authorized;
        }

        var existing = options.SkipRefresh ? null : await provider.Tokens(cancellationToken).ConfigureAwait(false);
        if (existing?.RefreshToken is not null)
        {
            try
            {
                var refreshed = await RefreshAuthorizationAsync(authorizationServerUrl, tokenOptions,
                    existing.RefreshToken, cancellationToken).ConfigureAwait(false);
                await provider.SaveTokens(WithScope(refreshed, existing.Scope), cancellationToken)
                    .ConfigureAwait(false);
                return FlowResult.Authorized;
            }
            catch (OAuthInsecureEndpointError) { throw; }
            catch (OAuthError error)
            {
                if (error.Code != "server_error") throw;
            }
        }

        var state = await provider.State(cancellationToken).ConfigureAwait(false);
        var authorization = await StartAuthorizationAsync(authorizationServerUrl, metadata, client,
            redirectUrl, scope, state, resource, cancellationToken).ConfigureAwait(false);
        await provider.SaveCodeVerifier(authorization.CodeVerifier, cancellationToken).ConfigureAwait(false);
        await provider.RedirectToAuthorization(authorization.AuthorizationUrl, cancellationToken).ConfigureAwait(false);
        return FlowResult.Redirect;
    }

    /// <summary>发现服务器信息（受保护资源 + 授权服务器）。对应 TS <c>discoverOAuthServerInfo</c> 分支。</summary>
    private static async Task<(string Url, OAuthTypes.AuthorizationServerMetadata?, OAuthTypes.ProtectedResourceMetadata?)>
        DiscoverServerInfoAsync(FlowOptions options, string? metadataUrl, CancellationToken cancellationToken)
    {
        OAuthTypes.ProtectedResourceMetadata? resourceMetadata = null;
        try
        {
            resourceMetadata = await OAuthDiscovery.DiscoverProtectedResourceMetadata(
                options.ServerUrl, options.ResourceMetadataUrl, options.Fetch,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }

        if (metadataUrl is not null)
        {
            var effectiveFetch = options.Fetch ?? new OAuthDiscovery.McpFetch(async (r, ct) =>
            {
                using var client = new HttpClient();
                return await client.SendAsync(r, ct).ConfigureAwait(false);
            });
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await effectiveFetch(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"HTTP {(int)response.StatusCode} loading authorization server metadata from {metadataUrl}");
            var parsed = OAuthTypes.ParseAuthorizationServerMetadata(
                JsonNode.Parse(body) as JsonObject ?? throw new ArgumentException("Invalid authorization server metadata"));
            return (parsed.Issuer, parsed, resourceMetadata);
        }

        var authorizationServerUrl = resourceMetadata?.AuthorizationServers is { Count: > 0 } servers
            ? servers[0]
            : new Uri(new Uri(options.ServerUrl), "/").ToString();
        var metadata = await OAuthDiscovery.DiscoverAuthorizationServerMetadata(
            authorizationServerUrl, options.Fetch, skipIssuerValidation: options.SkipIssuerValidation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (authorizationServerUrl, metadata, resourceMetadata);
    }

    /// <summary>origin/路径前缀匹配选择资源标识。对应 TS <c>selectResource</c>。</summary>
    public static string? SelectResource(string serverUrl, OAuthTypes.ProtectedResourceMetadata? metadata)
    {
        if (metadata is null) return null;
        var requested = new Uri(ResourceUrlFromServerUrl(serverUrl));
        var configured = new Uri(metadata.Resource);
        if (requested.Scheme != configured.Scheme || requested.Host != configured.Host || requested.Port != configured.Port)
            throw new InvalidOperationException(
                $"Protected resource {metadata.Resource} does not match MCP server {requested}");
        var requestedPath = requested.AbsolutePath.EndsWith("/") ? requested.AbsolutePath : $"{requested.AbsolutePath}/";
        var configuredPath = configured.AbsolutePath.EndsWith("/") ? configured.AbsolutePath : $"{configured.AbsolutePath}/";
        if (!requestedPath.StartsWith(configuredPath, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Protected resource {metadata.Resource} does not match MCP server {requested}");
        return metadata.Resource;
    }

    private static string ResourceUrlFromServerUrl(string value)
    {
        var builder = new UriBuilder(value) { Fragment = string.Empty };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// 流程入口：invalid_client/unauthorized_client → 全部凭据失效重试；
    /// invalid_grant → 仅令牌失效重试。对应 TS <c>authorizeMcp</c>。
    /// </summary>
    public static async Task<FlowResult> AuthorizeMcp(
        IOAuthClientProvider provider, FlowOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunFlowAsync(provider, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthError error) when (error.Code is "invalid_client" or "unauthorized_client")
        {
            await provider.InvalidateCredentials("all", cancellationToken).ConfigureAwait(false);
            return await RunFlowAsync(provider, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthError error) when (error.Code == "invalid_grant")
        {
            await provider.InvalidateCredentials("tokens", cancellationToken).ConfigureAwait(false);
            return await RunFlowAsync(provider, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
