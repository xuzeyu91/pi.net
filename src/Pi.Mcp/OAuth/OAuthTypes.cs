using System.Text.Json.Nodes;

namespace Pi.Mcp.OAuth;

/// <summary>
/// OAuth 元数据与令牌类型 + 依赖无关的结构化解析。
/// 对应 TS <c>oauth/types.ts</c>（改编自 modelcontextprotocol/typescript-sdk v1.29.0）。
/// </summary>
public static class OAuthTypes
{
    /// <summary>受保护资源元数据（RFC 9728）。对应 TS <c>OAuthProtectedResourceMetadata</c>。</summary>
    public sealed record ProtectedResourceMetadata(
        string Resource,
        IReadOnlyList<string>? AuthorizationServers = null,
        IReadOnlyList<string>? ScopesSupported = null) : AdditionalFields;

    /// <summary>授权服务器元数据（RFC 8414）。对应 TS <c>AuthorizationServerMetadata</c>。</summary>
    public sealed record AuthorizationServerMetadata(
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string? RegistrationEndpoint = null,
        IReadOnlyList<string>? ScopesSupported = null,
        IReadOnlyList<string>? ResponseTypesSupported = null,
        IReadOnlyList<string>? GrantTypesSupported = null,
        IReadOnlyList<string>? TokenEndpointAuthMethodsSupported = null,
        IReadOnlyList<string>? CodeChallengeMethodsSupported = null,
        bool? ClientIdMetadataDocumentSupported = null,
        bool? AuthorizationResponseIssParameterSupported = null) : AdditionalFields
    {
        /// <summary>必须项校验用：response_types_supported 是必填字符串数组。</summary>
        public IReadOnlyList<string> RequiredResponseTypes => ResponseTypesSupported ?? [];
    }

    /// <summary>令牌响应。对应 TS <c>OAuthTokens</c>。</summary>
    public sealed record OAuthTokens(
        string AccessToken,
        string TokenType,
        double? ExpiresIn = null,
        string? Scope = null,
        string? RefreshToken = null,
        string? IdToken = null);

    /// <summary>客户端元数据。对应 TS <c>OAuthClientMetadata</c>。</summary>
    public sealed record ClientMetadata(
        IReadOnlyList<string> RedirectUris,
        string? TokenEndpointAuthMethod = null,
        string? GrantTypes = null,
        string? ResponseTypes = null,
        string? ClientName = null,
        string? Scope = null);

    /// <summary>客户端注册信息。对应 TS <c>OAuthClientInformationFull</c>。</summary>
    public sealed record ClientInformation(
        string ClientId,
        string? ClientSecret = null,
        double? ClientIdIssuedAt = null,
        double? ClientSecretExpiresAt = null,
        IReadOnlyList<string>? RedirectUris = null,
        string? TokenEndpointAuthMethod = null,
        string? ClientName = null);

    /// <summary>发现状态。对应 TS <c>OAuthDiscoveryState</c>。</summary>
    public sealed record DiscoveryState(
        string AuthorizationServerUrl,
        AuthorizationServerMetadata? AuthorizationServerMetadata = null,
        ProtectedResourceMetadata? ResourceMetadata = null,
        string? ResourceMetadataUrl = null);

    /// <summary>WWW-Authenticate 挑战解析结果。对应 TS <c>OAuthChallenge</c>。</summary>
    public sealed record Challenge(string? ResourceMetadataUrl = null, string? Scope = null, string? Error = null, string? ErrorDescription = null);

    /// <summary>承载扩展字段（[key: string]: unknown）的基类。</summary>
    public abstract record AdditionalFields
    {
        /// <summary>元数据里的未识别字段原样保留。</summary>
        public Dictionary<string, JsonNode?>? Extra { get; init; }
    }

    // ─── 解析器 ─────────────────────────────────────────────────────────

    /// <summary>解析受保护资源元数据。对应 TS <c>parseProtectedResourceMetadata</c>。</summary>
    public static ProtectedResourceMetadata ParseProtectedResourceMetadata(JsonObject input)
    {
        var map = Object(input, "OAuth protected resource metadata");
        return new ProtectedResourceMetadata(
            Resource: SafeUrl(RequiredString(map, "resource", "OAuth protected resource metadata resource")),
            AuthorizationServers: OptionalStrings(map, "authorization_servers")?
                .Select(url => SafeUrl(url)).ToList(),
            ScopesSupported: OptionalStrings(map, "scopes_supported"))
        { Extra = Compact(map, "resource", "authorization_servers", "scopes_supported") };
    }

    /// <summary>解析授权服务器元数据。对应 TS <c>parseAuthorizationServerMetadata</c>。</summary>
    public static AuthorizationServerMetadata ParseAuthorizationServerMetadata(JsonObject input)
    {
        var map = Object(input, "authorization server metadata");
        var responseTypes = OptionalStrings(map, "response_types_supported");
        if (responseTypes is null) throw new ArgumentException("Invalid response_types_supported");
        return new AuthorizationServerMetadata(
            Issuer: SafeUrl(RequiredString(map, "issuer", "authorization server issuer")),
            AuthorizationEndpoint: SafeUrl(RequiredString(map, "authorization_endpoint", "authorization endpoint")),
            TokenEndpoint: SafeUrl(RequiredString(map, "token_endpoint", "token endpoint")),
            RegistrationEndpoint: OptionalUrl(map, "registration_endpoint"),
            ScopesSupported: OptionalStrings(map, "scopes_supported"),
            ResponseTypesSupported: responseTypes,
            GrantTypesSupported: OptionalStrings(map, "grant_types_supported"),
            TokenEndpointAuthMethodsSupported: OptionalStrings(map, "token_endpoint_auth_methods_supported"),
            CodeChallengeMethodsSupported: OptionalStrings(map, "code_challenge_methods_supported"),
            ClientIdMetadataDocumentSupported: OptionalBool(map, "client_id_metadata_document_supported"),
            AuthorizationResponseIssParameterSupported: OptionalBool(map, "authorization_response_iss_parameter_supported"))
        { Extra = Compact(map, "issuer", "authorization_endpoint", "token_endpoint", "registration_endpoint",
            "scopes_supported", "response_types_supported", "grant_types_supported",
            "token_endpoint_auth_methods_supported", "code_challenge_methods_supported",
            "client_id_metadata_document_supported", "authorization_response_iss_parameter_supported") };
    }

    /// <summary>解析令牌响应。对应 TS <c>parseOAuthTokens</c>（null/空串视为缺省）。</summary>
    public static OAuthTokens ParseOAuthTokens(JsonObject input)
    {
        var map = Object(input, "OAuth token response");
        // Number(null) 是 0 会立刻标记过期——absent 语义避免这个坑。
        double? expires = null;
        if (map.TryGetValue("expires_in", out var rawExpires) && Absent(rawExpires) is false)
        {
            if (rawExpires is not JsonValue value || !value.TryGetValue<double>(out var parsed)
                || !double.IsFinite(parsed))
                throw new ArgumentException("Invalid expires_in");
            expires = parsed;
        }
        return new OAuthTokens(
            AccessToken: RequiredString(map, "access_token", "access_token"),
            TokenType: RequiredString(map, "token_type", "token_type"),
            ExpiresIn: expires,
            Scope: OptionalString(map, "scope"),
            RefreshToken: OptionalString(map, "refresh_token"),
            IdToken: OptionalString(map, "id_token"));
    }

    /// <summary>解析客户端注册响应。对应 TS <c>parseClientInformation</c>。</summary>
    public static ClientInformation ParseClientInformation(JsonObject input)
    {
        var map = Object(input, "OAuth client registration response");
        return new ClientInformation(
            ClientId: RequiredString(map, "client_id", "client_id"),
            ClientSecret: OptionalString(map, "client_secret"),
            ClientIdIssuedAt: map.TryGetValue("client_id_issued_at", out var issued) && issued is JsonValue iv
                && iv.TryGetValue<double>(out var issuedAt) ? issuedAt : null,
            ClientSecretExpiresAt: map.TryGetValue("client_secret_expires_at", out var secretExp) && secretExp is JsonValue sv
                && sv.TryGetValue<double>(out var secretAt) ? secretAt : null,
            RedirectUris: OptionalStrings(map, "redirect_uris") ?? [],
            TokenEndpointAuthMethod: OptionalString(map, "token_endpoint_auth_method"),
            ClientName: OptionalString(map, "client_name"));
    }

    // ─── 基础校验辅助 ────────────────────────────────────────────────────

    private static Dictionary<string, JsonNode?> Object(JsonObject value, string name)
    {
        if (value is null) throw new ArgumentException($"Invalid {name}");
        return value.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>丢弃 undefined 值字段，剩余键原样保留（扩展字段）。</summary>
    private static Dictionary<string, JsonNode?> Compact(
        Dictionary<string, JsonNode?> map, params string[] consumed)
        => map.Where(kv => !consumed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static string RequiredString(Dictionary<string, JsonNode?> map, string key, string name)
        => map.TryGetValue(key, out var value) && value is JsonValue { } jsonValue
            && jsonValue.TryGetValue<string>(out var text) && text.Length > 0
            ? text : throw new ArgumentException($"Invalid {name}");

    /// <summary>null 与空串视为缺省：服务器对无值字段会发送 <c>scope: ""</c>。</summary>
    private static bool Absent(JsonNode? value)
        => value is null || (value is JsonValue { } v && v.TryGetValue<string>(out var s) && s.Length == 0);

    private static string? OptionalString(Dictionary<string, JsonNode?> map, string name)
        => !map.TryGetValue(name, out var value) || Absent(value) ? null : RequiredString(map, name, name);

    private static List<string>? OptionalStrings(Dictionary<string, JsonNode?> map, string name)
    {
        if (!map.TryGetValue(name, out var value) || value is null) return null;
        if (value is not JsonArray array || array.Any(item => item is not JsonValue { } v || !v.TryGetValue<string>(out _)))
            throw new ArgumentException($"Invalid {name}");
        return array.Select(item => item is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : throw new ArgumentException($"Invalid {name}")).ToList();
    }

    private static bool? OptionalBool(Dictionary<string, JsonNode?> map, string name)
        => map.TryGetValue(name, out var value) && value is JsonValue { } jsonValue
            && jsonValue.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>校验 URL 且拒绝 javascript:/data:/vbscript: 协议。</summary>
    private static string SafeUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
            throw new ArgumentException($"Invalid URL: {text}");
        if (url.Scheme is "javascript" or "data" or "vbscript")
            throw new ArgumentException($"Invalid URL scheme: {url.Scheme}");
        return text;
    }

    private static string? OptionalUrl(Dictionary<string, JsonNode?> map, string name)
        => !map.TryGetValue(name, out var value) || Absent(value) ? null : SafeUrl(RequiredString(map, name, name));
}
