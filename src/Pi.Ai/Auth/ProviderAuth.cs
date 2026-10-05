namespace Pi.Ai.Auth;

/// <summary>api-key 认证可用性检查结果。对应 TS <c>AuthCheck</c>。</summary>
public sealed record AuthCheck
{
    public string? Source { get; init; }

    public CredentialKind Type { get; init; }
}

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

    /// <summary>OAuth 登录选项的选择器标签，如 "Sign in with SuperGrok or X Premium"。</summary>
    string? LoginLabel => null;

    /// <summary>交互式登录，产出初始凭据（网络调用）。</summary>
    Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>交换刷新令牌（网络调用；invalid_grant 等失败抛出）。Models 在 store 锁内调用。</summary>
    Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default);

    /// <summary>从有效凭据无副作用地派生请求认证（支持 per-credential baseUrl）。</summary>
    Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default);
}

/// <summary>
/// api-key 认证：存储 key/provider 环境变量 + 环境源（env、AWS profile、ADC 文件）。
/// 纯环境源 provider 的 <see cref="LoginAsync"/> 为 null（对应 TS 可选 login）。
/// 对应 TS <c>ApiKeyAuth</c>（auth/types.ts）。
/// </summary>
public interface IApiKeyAuth
{
    /// <summary>显示名，如 "Anthropic API key"。</summary>
    string Name { get; }

    /// <summary>是否提供交互式登录（TS login 缺省 = 纯环境源）。</summary>
    bool HasLogin => false;

    /// <summary>交互式登录（提示输入 key/provider env）。仅 <see cref="HasLogin"/> 为 true 时被调用。</summary>
    Task<Credential.ApiKey> LoginAsync(ProviderAuthInteraction interaction,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{Name} does not support interactive login");

    /// <summary>
    /// 从存储凭据和/或环境源解析认证，按字段合并（credential.key ?? env("...")）。
    /// 返回 null = 未配置。解析是 provider 作用域的；模型专属端点准备发生在解析之后。
    /// </summary>
    Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider 认证组合：apiKey/oauth 至少其一存在——即便纯环境源或无密钥本地
/// 服务器也提供 apiKey 认证（其 resolve 报告 provider 是否已配置）。
/// 对应 TS <c>ProviderAuth</c>（auth/types.ts）。
/// </summary>
public sealed record ProviderAuth
{
    public IApiKeyAuth? ApiKey { get; init; }

    public IOAuthAuth? OAuth { get; init; }
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
