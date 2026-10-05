using System.Text.RegularExpressions;

namespace Pi.Ai.Utils;

/// <summary>
/// 移除字符串中未配对的 Unicode 代理项字符。对应 TS <c>sanitizeSurrogates</c>
/// （utils/sanitize-unicode.ts）。未配对代理项会导致多数 provider 的 JSON 序列化
/// 失败；正常成对的代理项（emoji 等 BMP 之外字符）不受影响。
/// </summary>
public static partial class SanitizeUnicode
{
    [GeneratedRegex(@"[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]")]
    private static partial Regex UnpairedSurrogateRegex();

    public static string SanitizeSurrogates(string text)
        => UnpairedSurrogateRegex().Replace(text, string.Empty);
}
