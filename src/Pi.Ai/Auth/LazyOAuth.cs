namespace Pi.Ai.Auth;

/// <summary>
/// 懒加载 OAuth 认证包装。对应 TS <c>lazyOAuth</c>（auth/helpers.ts）：provider 定义
/// 可以在不引用具体实现的情况下声明 OAuth 能力，流程在首次 login/refresh/toAuth 时加载。
/// </summary>
public sealed class LazyOAuthAuth : IOAuthAuth
{
    private readonly Lazy<IOAuthAuth> _loaded;

    public LazyOAuthAuth(string name, Func<IOAuthAuth> load,
        bool isSubscription = false, string? loginLabel = null)
    {
        Name = name;
        IsSubscription = isSubscription;
        LoginLabel = loginLabel;
        _loaded = new Lazy<IOAuthAuth>(load);
    }

    public string Name { get; }

    public bool IsSubscription { get; }

    public string? LoginLabel { get; }

    public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
        => _loaded.Value.LoginAsync(interaction, options, cancellationToken);

    public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
        => _loaded.Value.RefreshAsync(credential, cancellationToken);

    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
        => _loaded.Value.ToAuthAsync(credential, cancellationToken);
}
