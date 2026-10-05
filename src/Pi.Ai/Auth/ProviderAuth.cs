namespace Pi.Ai.Auth;

/// <summary>
/// OAuth 认证：refresh/toAuth 拆分让 Models 拥有锁定刷新模式——
/// refresh 产出凭据，toAuth 从存储后的凭据派生请求认证。
/// 对应 TS <c>OAuthAuth</c>（auth/types.ts）。
/// </summary>
public interface IOAuthAuth
{
    /// <summary>显示名，如 "Anthropic (Claude Pro/Max)"。</summary>
    string Name { get; }

    /// <summary>是否订阅支撑（Claude Pro/Max 等）。</summary>
    bool IsSubscription { get; }

    /// <summary>交换刷新令牌（网络调用；invalid_grant 等失败抛出）。Models 在 store 锁内调用。</summary>
    Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default);

    /// <summary>从有效凭据无副作用地派生请求认证（支持 per-credential baseUrl）。</summary>
    Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// OAuth 刷新编排器：过期凭据在 store 的 modify 锁内刷新——并发请求不会
/// 用轮换前的 refresh token 双重刷新。对应 TS <c>Models.getAuth()</c> 的锁定刷新模式。
/// </summary>
public sealed class OAuthRefreshCoordinator(ICredentialStore store, Func<IOAuthAuth?> authLoader)
{
    /// <summary>时钟（测试可注入）。</summary>
    public Func<long> Now { get; set; } = () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>
    /// 确保凭据新鲜并派生请求认证：api_key 直接 toAuth；OAuth 未过期直接用；
    /// 过期则锁内 refresh（fn 看到当前凭据，轮换安全），存回后 toAuth。
    /// </summary>
    public async Task<ModelAuth?> GetAuthAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        ModelAuth? result = null;
        await store.ModifyAsync(providerId, async current =>
        {
            switch (current)
            {
                case Credential.ApiKey apiKey:
                    result = new ModelAuth { ApiKey = apiKey.Key };
                    return current;
                case Credential.OAuth oauth:
                {
                    if (oauth.Expires > Now())
                    {
                        // 未过期：不触碰网络。
                        result = await ToAuthAsync(oauth, cancellationToken).ConfigureAwait(false);
                        return current;
                    }
                    var auth = authLoader();
                    if (auth is null)
                    {
                        throw new InvalidOperationException($"Provider {providerId} has no OAuth flow");
                    }
                    var refreshed = await auth.RefreshAsync(oauth, cancellationToken).ConfigureAwait(false);
                    result = await auth.ToAuthAsync(refreshed, cancellationToken).ConfigureAwait(false);
                    return refreshed;
                }
                default:
                    return current; // 未配置：由调用方决定语义
            }
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<ModelAuth> ToAuthAsync(Credential.OAuth oauth, CancellationToken cancellationToken)
    {
        var auth = authLoader()
            ?? throw new InvalidOperationException("Provider has no OAuth flow");
        return await auth.ToAuthAsync(oauth, cancellationToken).ConfigureAwait(false);
    }
}
