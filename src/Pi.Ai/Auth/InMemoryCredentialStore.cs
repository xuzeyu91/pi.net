namespace Pi.Ai.Auth;

/// <summary>
/// 默认内存凭据存储：应用注入持久化实现。按 provider 串行化写。
/// 对应 TS <c>InMemoryCredentialStore</c>（promise 链 → SemaphoreSlim）。
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, Credential> _credentials = [];
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];
    private readonly object _lock = new();

    private SemaphoreSlim LockFor(string providerId)
    {
        lock (_lock)
        {
            if (!_locks.TryGetValue(providerId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[providerId] = gate;
            }
            return gate;
        }
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Credential? credential;
        lock (_lock) _credentials.TryGetValue(providerId, out credential);
        return Task.FromResult(credential);
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CredentialInfo> infos;
        lock (_lock)
        {
            infos = _credentials
                .Select(kv => new CredentialInfo(kv.Key, kv.Value.Kind))
                .ToList();
        }
        return Task.FromResult<IReadOnlyList<CredentialInfo>>(infos);
    }

    public async Task<Credential?> ModifyAsync(string providerId,
        Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default)
    {
        var gate = LockFor(providerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Credential? current;
            lock (_lock) _credentials.TryGetValue(providerId, out current);
            var next = await fn(current).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (next is not null)
            {
                lock (_lock) _credentials[providerId] = next;
                return next;
            }
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var gate = LockFor(providerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock) _credentials.Remove(providerId);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Api-key 认证：存储 key/provider env 加环境来源。对应 TS <c>ApiKeyAuth</c> 的解析核心
/// （resolve.ts）：逐字段合并 credential.key ?? env("...")、credential.env?.NAME ?? env("...")。
/// </summary>
public static class AuthResolver
{
    /// <summary>
    /// 解析 api-key 认证：凭据优先、环境变量兜底。全部字段皆空 = 未配置（null）。
    /// 对应 TS <c>resolveApiKeyAuth</c> 的合并语义。
    /// </summary>
    public static async Task<AuthResult?> ResolveApiKeyAuthAsync(
        string apiKeyEnvName,
        Credential.ApiKey? credential,
        IAuthContext ctx,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = credential?.Key ?? await ctx.EnvAsync(apiKeyEnvName, cancellationToken).ConfigureAwait(false);
        var env = new Dictionary<string, string>();
        if (credential?.Env is { } credentialEnv)
        {
            foreach (var (name, value) in credentialEnv)
            {
                var resolved = value ?? await ctx.EnvAsync(name, cancellationToken).ConfigureAwait(false);
                if (resolved is not null) env[name] = resolved;
            }
        }
        if (apiKey is null && env.Count == 0) return null;
        return new AuthResult
        {
            Auth = new ModelAuth { ApiKey = apiKey },
            Env = env.Count > 0 ? env : null,
            Source = source ?? apiKeyEnvName,
        };
    }
}
