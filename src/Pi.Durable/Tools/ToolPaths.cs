using System.Text;
using System.Text.RegularExpressions;
using Pi.Chord.Context;
using Pi.Durable.Env;

namespace Pi.Durable.Tools;

/// <summary>
/// 工具路径归一与宽松解析。对应 TS <c>tools/path-utils.ts</c>。
/// </summary>
/// <remarks>
/// 归一化把各种 Unicode 空白（NBSP 等）折成普通空格，并剥掉前导的 <c>@</c>；
/// 读取路径解析还会为「同一路径的多种等价写法」逐个探测存在性。
/// </remarks>
public static partial class ToolPaths
{
    /// <summary>窄不换行空格：macOS 会把 " 9.00 AM" 这类时间里的空格写成它。</summary>
    private const string NarrowNoBreakSpace = "\u202F";

    /// <summary>各种 Unicode 空白字符。对应 TS <c>UNICODE_SPACES</c>。</summary>
    [GeneratedRegex(@"[\u00A0\u2000-\u200A\u202F\u205F\u3000]")]
    private static partial Regex UnicodeSpaces();

    /// <summary>路径里的 <c> (AM).</c> / <c> (PM).</c>（大小写不敏感）。对应 TS 的对应正则。</summary>
    [GeneratedRegex(@" (AM|PM)\.", RegexOptions.IgnoreCase)]
    private static partial Regex Meridiem();

    /// <summary>单引号（含左右弯引号）。</summary>
    [GeneratedRegex("'")]
    private static partial Regex Apostrophe();

    /// <summary>归一化工具路径：Unicode 空白折成普通空格，剥掉一个前导 <c>@</c>。</summary>
    public static string NormalizeToolPath(string path)
    {
        var normalized = UnicodeSpaces().Replace(path, " ");
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    /// <summary>把工具路径解析为绝对路径（失败即抛出）。对应 TS <c>resolveToolPath</c>。</summary>
    public static async Task<string> ResolveToolPathAsync(IExecutionEnv env, string path, Context context)
        => (await env.AbsolutePathAsync(NormalizeToolPath(path), context).ConfigureAwait(false)).GetOrThrow();

    /// <summary>
    /// 读取路径的宽松解析：在若干等价写法中挑出实际存在的那一个，都不存在时返回原解析结果。
    /// 对应 TS <c>resolveReadToolPath</c>（模型从 macOS 粘贴的路径常带窄空格或弯引号）。
    /// </summary>
    public static async Task<string> ResolveReadToolPathAsync(IExecutionEnv env, string path, Context context)
    {
        var resolved = await ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
        var variants = new List<string>
        {
            resolved,
            Meridiem().Replace(resolved, $" {NarrowNoBreakSpace}$1."),
            resolved.Normalize(NormalizationForm.FormD),
            Apostrophe().Replace(resolved, "\u2019"),
            Apostrophe().Replace(resolved.Normalize(NormalizationForm.FormD), "\u2019"),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (!seen.Add(variant)) continue;
            var exists = await env.ExistsAsync(variant, context).ConfigureAwait(false);
            if (exists.GetOrThrow()) return variant;
        }

        return resolved;
    }
}

/// <summary>
/// <see cref="ToolPaths"/> 的可读别名（与 TS 模块名一致）。
/// </summary>
public static class PathUtils
{
    public static string NormalizeToolPath(string path) => ToolPaths.NormalizeToolPath(path);

    public static Task<string> ResolveToolPathAsync(IExecutionEnv env, string path, Context context)
        => ToolPaths.ResolveToolPathAsync(env, path, context);

    public static Task<string> ResolveReadToolPathAsync(IExecutionEnv env, string path, Context context)
        => ToolPaths.ResolveReadToolPathAsync(env, path, context);
}
