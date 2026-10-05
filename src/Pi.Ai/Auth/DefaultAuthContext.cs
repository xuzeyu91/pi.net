namespace Pi.Ai.Auth;

/// <summary>
/// 默认认证上下文：环境变量取自进程环境（<see cref="Environment.GetEnvironmentVariable"/>），
/// 文件存在检查支持 <c>~</c> 前缀（展开到用户主目录）。
/// 对应 TS <c>defaultProviderAuthContext</c>（auth/context.ts）。
/// </summary>
public sealed class DefaultAuthContext : IAuthContext
{
    public static readonly DefaultAuthContext Instance = new();

    public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return Task.FromResult<string?>(
            value is not null && value.Trim().Length > 0 ? value : null);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = path;
            if (resolved.StartsWith('~'))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                resolved = home + resolved[1..];
            }
            return Task.FromResult(File.Exists(resolved));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// 在基础上下文上叠加 provider env 覆盖：env[name] 优先，回落到 base。
/// 对应 TS <c>overlayEnvAuthContext</c>（auth/resolve.ts）。
/// </summary>
public sealed class OverlayEnvAuthContext(IAuthContext baseContext, IReadOnlyDictionary<string, string> env)
    : IAuthContext
{
    public async Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
    {
        if (env.TryGetValue(name, out var value) && value.Length > 0) return value;
        return await baseContext.EnvAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => baseContext.FileExistsAsync(path, cancellationToken);
}
