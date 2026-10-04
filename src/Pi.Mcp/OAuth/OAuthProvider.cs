using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Pi.Mcp.OAuth;

/// <summary>单台 MCP 服务器的 OAuth 持久状态。对应 TS <c>McpOAuthState</c>。</summary>
public sealed record McpOAuthState
{
    [JsonPropertyName("serverUrl")]
    public required string ServerUrl { get; init; }

    [JsonPropertyName("clientInformation")]
    public OAuthTypes.ClientInformation? ClientInformation { get; init; }

    [JsonPropertyName("tokens")]
    public OAuthTypes.OAuthTokens? Tokens { get; init; }

    /// <summary>access token 过期时刻（epoch 毫秒），保存时按 expires_in 换算。</summary>
    [JsonPropertyName("tokensExpireAt")]
    public long? TokensExpireAt { get; init; }

    [JsonPropertyName("codeVerifier")]
    public string? CodeVerifier { get; init; }

    [JsonPropertyName("oauthState")]
    public string? OAuthState { get; init; }

    [JsonPropertyName("discovery")]
    public OAuthTypes.DiscoveryState? Discovery { get; init; }
}

/// <summary>状态存储抽象（应用注入持久化实现）。对应 TS <c>McpOAuthStateStore</c>。</summary>
public interface McpOAuthStateStore
{
    Task<McpOAuthState?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(McpOAuthState state, CancellationToken cancellationToken = default);
}

/// <summary>内存存储（深拷贝隔离）。对应 TS <c>MemoryOAuthStateStore</c>。</summary>
public sealed class MemoryOAuthStateStore : McpOAuthStateStore
{
    private McpOAuthState? _value;

    public Task<McpOAuthState?> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_value is null ? null : JsonSerializerRoundTrip(_value));

    public Task SaveAsync(McpOAuthState state, CancellationToken cancellationToken = default)
    {
        _value = JsonSerializerRoundTrip(state);
        return Task.CompletedTask;
    }

    /// <summary>用 JSON 往返模拟 structuredClone 的深拷贝语义。</summary>
    private static McpOAuthState? JsonSerializerRoundTrip(McpOAuthState value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<McpOAuthState>(json);
    }
}

/// <summary>
/// 默认有状态提供者（精确对应一台 MCP 服务器 URL；跨服务器凭据不串用）。
/// 对应 TS <c>McpOAuthProvider</c>。写操作串行化（writes 链），读等最后一个写。
/// </summary>
public sealed class McpOAuthProvider : OAuthFlow.IOAuthClientProvider
{
    private readonly string _serverUrl;
    private readonly OAuthTypes.ClientInformation? _configuredClient;
    private readonly McpOAuthStateStore _store;
    private readonly Func<Uri, CancellationToken, Task> _onRedirect;
    private Task _writes = Task.CompletedTask;

    public string RedirectUrl { get; }

    public OAuthTypes.ClientMetadata ClientMetadata { get; }

    public OAuthFlow.ClientMetadataDocument? ClientMetadataDocumentHook { get; init; }

    public OAuthFlow.AddClientAuthentication? AddClientAuthenticationHook { get; init; }

    public McpOAuthProvider(
        string serverUrl,
        string redirectUrl,
        OAuthTypes.ClientMetadata clientMetadata,
        Func<Uri, CancellationToken, Task> onRedirect,
        string? clientId = null,
        string? clientSecret = null,
        McpOAuthStateStore? store = null,
        OAuthFlow.ClientMetadataDocument? clientMetadataDocument = null)
    {
        _serverUrl = new Uri(serverUrl).ToString();
        RedirectUrl = redirectUrl;
        ClientMetadata = clientMetadata with
        {
            RedirectUris = clientMetadata.RedirectUris is { Count: > 0 } uris ? uris : [redirectUrl],
            Scope = clientMetadata.Scope,
            TokenEndpointAuthMethod = clientMetadata.TokenEndpointAuthMethod
                ?? (clientSecret is not null ? "client_secret_post" : "none"),
        };
        ClientMetadataDocumentHook = clientMetadataDocument;
        _configuredClient = clientId is not null
            ? new OAuthTypes.ClientInformation(ClientId: clientId, ClientSecret: clientSecret)
            : null;
        _store = store ?? new MemoryOAuthStateStore();
        _onRedirect = onRedirect;
    }

    public OAuthFlow.ClientMetadataDocument? ClientMetadataDocument(OAuthTypes.AuthorizationServerMetadata? metadata)
        => ClientMetadataDocumentHook;

    public async Task<string?> State(CancellationToken cancellationToken = default)
    {
        var existing = (await LoadAsync(cancellationToken).ConfigureAwait(false)).OAuthState;
        if (existing is not null) return existing;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await UpdateAsync(value => value with { OAuthState = state }, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<OAuthTypes.ClientInformation?> ClientInformation(CancellationToken cancellationToken = default)
        => _configuredClient ?? (await LoadAsync(cancellationToken).ConfigureAwait(false)).ClientInformation;

    public async Task SaveClientInformation(
        OAuthTypes.ClientInformation information, CancellationToken cancellationToken = default)
    {
        if (_configuredClient is not null) return;
        await UpdateAsync(value => value with { ClientInformation = information }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OAuthTypes.OAuthTokens?> Tokens(CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false)).Tokens;

    public async Task SaveTokens(OAuthTypes.OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        var expiresAt = tokens.ExpiresIn is { } expiresIn
            ? DateTimeOffset.Now.ToUnixTimeMilliseconds() + (long)(expiresIn * 1000) : (long?)null;
        await UpdateAsync(value => value with
        {
            Tokens = tokens,
            TokensExpireAt = expiresAt,
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task RedirectToAuthorization(Uri url, CancellationToken cancellationToken = default)
        => _onRedirect(url, cancellationToken);

    public async Task SaveCodeVerifier(string verifier, CancellationToken cancellationToken = default)
        => await UpdateAsync(value => value with { CodeVerifier = verifier }, cancellationToken).ConfigureAwait(false);

    public async Task<string> CodeVerifier(CancellationToken cancellationToken = default)
    {
        var verifier = (await LoadAsync(cancellationToken).ConfigureAwait(false)).CodeVerifier;
        return verifier ?? throw new InvalidOperationException("No OAuth PKCE code verifier is stored");
    }

    public async Task InvalidateCredentials(string kind, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(value =>
        {
            var next = value;
            if (kind is "all" or "client") next = next with { ClientInformation = null };
            if (kind is "all" or "tokens") next = next with { Tokens = null, TokensExpireAt = null };
            if (kind is "all" or "verifier") next = next with { CodeVerifier = null };
            if (kind is "all" or "discovery") next = next with { Discovery = null };
            if (kind == "all") next = next with { OAuthState = null };
            return next;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDiscoveryState(
        OAuthTypes.DiscoveryState state, CancellationToken cancellationToken = default)
        => await UpdateAsync(value => value with { Discovery = state }, cancellationToken).ConfigureAwait(false);

    public async Task<OAuthTypes.DiscoveryState?> DiscoveryState(CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false)).Discovery;

    private async Task<McpOAuthState> LoadAsync(CancellationToken cancellationToken)
    {
        await _writes.ConfigureAwait(false);
        return Own(await _store.LoadAsync(cancellationToken).ConfigureAwait(false)) with { };
    }

    private async Task UpdateAsync(Func<McpOAuthState, McpOAuthState> update, CancellationToken cancellationToken)
    {
        _writes = _writes.ContinueWith(async _ =>
        {
            var current = Own(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));
            await _store.SaveAsync(update(current), cancellationToken).ConfigureAwait(false);
        }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        await _writes.ConfigureAwait(false);
    }

    /// <summary>其他服务器 URL 的存储状态一律忽略，凭据不跨服务器泄漏。</summary>
    private McpOAuthState Own(McpOAuthState? state)
        => state?.ServerUrl == _serverUrl ? state : new McpOAuthState { ServerUrl = _serverUrl };
}
