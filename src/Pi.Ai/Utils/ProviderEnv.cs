namespace Pi.Ai.Utils;

/// <summary>
/// provider 环境取值：作用域覆盖 → 进程环境变量。对应 TS <c>getProviderEnvValue</c>
/// （utils/provider-env.ts；Bun 沙箱 /proc/self/environ 兜底为运行时特有，C# 无对应问题）。
/// TS 的 <c>ProviderEnv</c> 类型在 C# 即 <c>IReadOnlyDictionary&lt;string, string&gt;?</c>
/// （与 Auth 层既有约定一致）。
/// </summary>
public static class ProviderEnvValue
{
    public static string? Get(string name, IReadOnlyDictionary<string, string>? env = null)
        => env is not null && env.TryGetValue(name, out var scoped) && scoped.Length > 0
            ? scoped
            : Environment.GetEnvironmentVariable(name);
}
