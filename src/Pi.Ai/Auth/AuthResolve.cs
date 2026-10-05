using Pi.Ai.Utils;

namespace Pi.Ai.Auth;

/// <summary>认证解析覆盖项。对应 TS <c>AuthResolutionOverrides</c>（auth/resolve.ts）。</summary>
public sealed record AuthResolutionOverrides
{
    public string? ApiKey { get; init; }

    /// <summary>provider 作用域环境覆盖（叠加在 ambient 之上）。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>要求的 OAuth 令牌剩余有效期；缺省五分钟。</summary>
    public long? MinOAuthValidityMs { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>
/// 一个 <c>Models</c> 集合内所有操作共享的认证解析。对应 TS
/// <c>resolveProviderAuth</c>（auth/resolve.ts）：存储凭据拥有 provider——
/// 只有未存储时才咨询 ambient/env。刷新失败或凭据类型无对应 handler 时不做
/// 静默 env 回退。
/// </summary>
public static class AuthResolve
{
    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public static Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private const long DefaultOAuthMinimumValidityMs = 5 * 60 * 1000;
    private const int DefaultOAuthRefreshTimeoutMs = 15_000;

    public static Task<AuthResult?> ResolveProviderAuthAsync(
        string providerId,
        ProviderAuth auth,
        ICredentialStore credentials,
        IAuthContext authContext,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var overrideSignal = overrides?.Signal ?? default;
        if (overrideSignal.CanBeCanceled && cancellationToken.CanBeCanceled)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(overrideSignal, cancellationToken);
            return ResolveWithSignalAsync(providerId, auth, credentials, authContext, overrides, linked.Token);
        }
        var signal = overrideSignal.CanBeCanceled ? overrideSignal : cancellationToken;
        return ResolveWithSignalAsync(providerId, auth, credentials, authContext, overrides, signal);
    }

    private static async Task<AuthResult?> ResolveWithSignalAsync(
        string providerId,
        ProviderAuth auth,
        ICredentialStore credentials,
        IAuthContext authContext,
        AuthResolutionOverrides? overrides,
        CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var requestAuthContext = overrides?.Env is { Count: > 0 } env
            ? new OverlayEnvAuthContext(authContext, env)
            : authContext;

        // 显式 apiKey 覆盖 + provider 支持 api-key：合成凭据直接解析。
        if (overrides?.ApiKey is not null && auth.ApiKey is not null)
        {
            return await ResolveApiKeyAsync(requestAuthContext, auth.ApiKey, providerId,
                new Credential.ApiKey(overrides.ApiKey, overrides.Env), signal).ConfigureAwait(false);
        }

        var stored = await ReadCredentialAsync(credentials, providerId, signal).ConfigureAwait(false);
        if (stored is not null)
        {
            switch (stored)
            {
                case Credential.OAuth oauth when auth.OAuth is not null:
                    return await ResolveStoredOAuthAsync(
                        credentials, providerId, auth.OAuth, oauth, signal,
                        overrides?.MinOAuthValidityMs).ConfigureAwait(false);
                case Credential.ApiKey apiKey when auth.ApiKey is not null:
                {
                    if (overrides?.Env is { } overrideEnv)
                    {
                        var merged = new Dictionary<string, string>();
                        foreach (var (key, value) in apiKey.Env ?? new Dictionary<string, string>()) merged[key] = value;
                        foreach (var (key, value) in overrideEnv) merged[key] = value;
                        apiKey = new Credential.ApiKey(apiKey.Key, merged);
                    }
                    return await ResolveApiKeyAsync(
                        requestAuthContext, auth.ApiKey, providerId, apiKey, signal).ConfigureAwait(false);
                }
                default:
                    // 存储凭据类型没有匹配的 handler：不做静默回退。
                    return null;
            }
        }

        // ambient（环境变量、AWS profile、ADC 文件）。
        return auth.ApiKey is not null
            ? await ResolveApiKeyAsync(requestAuthContext, auth.ApiKey, providerId, null, signal).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// OAuth 解析 + 双检锁：剩余有效期不足五分钟的令牌进锁，锁内复查过期，
    /// 全局刷新一次并在释放前持久化轮换凭据。对应 TS <c>resolveStoredOAuth</c>。
    /// fn 返回 null（期间已登出/已被他人刷新）时，modify 解析为写后凭据——
    /// 后者让本请求直接复用刚刷新的令牌。
    /// </summary>
    private static async Task<AuthResult?> ResolveStoredOAuthAsync(
        ICredentialStore credentials,
        string providerId,
        IOAuthAuth oauth,
        Credential.OAuth stored,
        CancellationToken signal,
        long? minOAuthValidityMs = null)
    {
        var minimumValidityMs = Math.Max(DefaultOAuthMinimumValidityMs, minOAuthValidityMs ?? 0);
        bool ExpiresSoon(Credential.OAuth credential) => Now() + minimumValidityMs >= credential.Expires;
        var credential = stored;

        if (ExpiresSoon(credential))
        {
            // 乐观检查说已过期；权威检查在锁内运行。
            Credential? post;
            try
            {
                post = await credentials.ModifyAsync(providerId, async current =>
                {
                    if (current is not Credential.OAuth currentOauth) return null; // 期间已登出
                    if (!ExpiresSoon(currentOauth)) return null;                   // 其他进程/请求已刷新
                    try
                    {
                        using var refreshSignal = CancellationTokenSource.CreateLinkedTokenSource(signal);
                        refreshSignal.CancelAfter(DefaultOAuthRefreshTimeoutMs);
                        return await oauth.RefreshAsync(currentOauth, refreshSignal.Token).ConfigureAwait(false);
                    }
                    catch (ModelsError)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        throw new ModelsError(ModelsErrorCode.OAuth,
                            $"OAuth refresh failed for {providerId}", error);
                    }
                }, signal).ConfigureAwait(false);
            }
            catch (ModelsError)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelsError(ModelsErrorCode.Auth,
                    $"Credential store modify failed for {providerId}", error);
            }
            if (post is not Credential.OAuth postOauth) return null; // 期间已登出
            credential = postOauth;
            // 常规五分钟窗口触发刷新但不构成 provider 契约；显式调用方（如
            // bearer-token 导出）刷新后才要求请求的最小有效期。
            if (minOAuthValidityMs is not null && ExpiresSoon(credential))
            {
                throw new ModelsError(ModelsErrorCode.OAuth,
                    $"OAuth refresh returned a token that expires too soon for {providerId}");
            }
        }

        try
        {
            return new AuthResult
            {
                Auth = await oauth.ToAuthAsync(credential, signal).ConfigureAwait(false),
                Source = "OAuth",
            };
        }
        catch (ModelsError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.OAuth,
                $"OAuth auth derivation failed for {providerId}", error);
        }
    }

    private static async Task<AuthResult?> ResolveApiKeyAsync(
        IAuthContext authContext,
        IApiKeyAuth apiKey,
        string providerId,
        Credential.ApiKey? credential,
        CancellationToken signal)
    {
        try
        {
            return await apiKey.ResolveAsync(credential, authContext, signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"API key auth failed for provider {providerId}", error);
        }
    }

    private static async Task<Credential?> ReadCredentialAsync(
        ICredentialStore credentials, string providerId, CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store read failed for {providerId}", error);
        }
    }
}
