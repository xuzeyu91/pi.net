namespace Pi.Mcp.OAuth;

/// <summary>OAuth 协议错误（error code + 可选 error_uri）。对应 TS <c>OAuthError</c>。</summary>
public sealed class OAuthError(string code, string message, string? errorUri = null)
    : Exception(message.Length > 0 ? message : code)
{
    public string Code { get; } = code;

    public string? ErrorUri { get; } = errorUri;
}

/// <summary>授权响应的 iss 与预期的授权服务器不符（RFC 9207）。对应 TS <c>OAuthIssuerMismatchError</c>。</summary>
public sealed class OAuthIssuerMismatchError(string expected, string? received)
    : Exception($"OAuth issuer mismatch: expected \"{expected}\", received {(received is null ? "none" : $"\"{received}\"")}")
{
    public string Expected { get; } = expected;

    /// <summary>服务器承诺了 iss 参数但响应未携带时为 null。</summary>
    public string? Received { get; } = received;
}

/// <summary>拒绝把 OAuth 凭据发往非 HTTPS 端点。对应 TS <c>OAuthInsecureEndpointError</c>。</summary>
public sealed class OAuthInsecureEndpointError(string endpoint)
    : Exception($"Refusing to send OAuth credentials to non-HTTPS endpoint {endpoint}")
{
    public string Endpoint { get; } = endpoint;
}

/// <summary>动态客户端注册失败。对应 TS <c>OAuthRegistrationError</c>。</summary>
public sealed class OAuthRegistrationError(int status, string body)
    : Exception($"OAuth dynamic client registration failed with status {status}: {body}")
{
    public int Status { get; } = status;

    public string Body { get; } = body;
}

/// <summary>需要用户交互完成授权（浏览器打开授权 URL）。对应 TS <c>McpOAuthAuthorizationRequiredError</c>。</summary>
public sealed class McpOAuthAuthorizationRequiredError()
    : Exception("MCP OAuth authorization requires user interaction");
