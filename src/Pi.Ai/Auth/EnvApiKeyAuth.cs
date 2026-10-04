namespace Pi.Ai.Auth;

/// <summary>
/// 标准 api-key 认证：存储凭据 key 优先，否则按顺序取第一个已设置的环境变量。
/// 对应 TS <c>envApiKeyAuth</c>（auth/helpers.ts）。
/// </summary>
public sealed class EnvApiKeyAuth(string name, IReadOnlyList<string> envVars)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> EnvVars { get; } = envVars;

    /// <summary>
    /// 解析：stored credential.key 优先 → envVars 顺序取第一个非空。
    /// 返回 null = 未配置（对齐 TS resolve 返回 undefined）。
    /// </summary>
    public async Task<AuthResult?> ResolveAsync(
        Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
    {
        if (credential?.Key is { Length: > 0 } storedKey)
        {
            return new AuthResult
            {
                Auth = new ModelAuth { ApiKey = storedKey },
                Env = credential.Env,
                Source = "stored credential",
            };
        }
        foreach (var envVar in EnvVars)
        {
            var value = await ctx.EnvAsync(envVar, cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                return new AuthResult
                {
                    Auth = new ModelAuth { ApiKey = value },
                    Source = envVar,
                };
            }
        }
        return null;
    }
}
