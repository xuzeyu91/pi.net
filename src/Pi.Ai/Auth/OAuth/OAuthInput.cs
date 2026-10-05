using Pi.Ai.Utils;
namespace Pi.Ai.Auth.OAuth;

/// <summary>provider 环境值解析：作用域覆盖 → 进程环境。对应 TS <c>getProviderEnvValue</c>（utils/provider-env.ts，Bun 沙箱回退不适用）。</summary>
public static class ProviderEnv
{
    public static string? GetValue(string name, IReadOnlyDictionary<string, string>? env = null)
    {
        if (env is not null && env.TryGetValue(name, out var scoped) && scoped.Length > 0) return scoped;
        var value = Environment.GetEnvironmentVariable(name);
        return value is { Length: > 0 } ? value : null;
    }
}

/// <summary>回调地址解析辅助（多家流程共用）。</summary>
public static class OAuthInput
{
    /// <summary>
    /// 解析用户粘贴的授权输入：完整重定向 URL、<c>code#state</c>、查询串或裸 code。
    /// 对应 TS <c>parseAuthorizationInput</c>（anthropic/openai-codex 各有一份，行为一致）。
    /// </summary>
    public static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (value.Length == 0) return (null, null);

        try
        {
            var url = new Uri(value);
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            return (query["code"], query["state"]);
        }
        catch (UriFormatException)
        {
            // 不是 URL
        }

        var hashIndex = value.IndexOf('#');
        if (hashIndex >= 0)
        {
            var code = value[..hashIndex];
            var state = value[(hashIndex + 1)..];
            return (code.Length > 0 ? code : null, state.Length > 0 ? state : null);
        }

        if (value.Contains("code=", StringComparison.Ordinal))
        {
            var query = System.Web.HttpUtility.ParseQueryString(value);
            return (query["code"], query["state"]);
        }

        return (value, null);
    }

    /// <summary>16 随机字节的十六进制串（state）。对应 TS <c>createState</c>。</summary>
    public static string RandomHexState()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
