using System.Text;

namespace Pi.Codemode;

/// <summary>
/// 脚本引用工具时使用的标识符。对应 TS <c>toCodemodeIdentifier</c>（identifier.ts）：
/// 非合法 JavaScript 标识符字符变成 <c>_</c>；<c>mcp__docs__search</c> 原样保留，
/// <c>my-tool</c> 变成 <c>my_tool</c>。
/// </summary>
public static class CodemodeIdentifiers
{
    public static string ToCodemodeIdentifier(string name)
    {
        var identifier = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var valid = identifier.Length == 0 ? IsIdentifierStart(rune) : IsIdentifierPart(rune);
            identifier.Append(valid ? rune.ToString() : "_");
        }
        return identifier.Length == 0 ? "_" : identifier.ToString();
    }

    private static bool IsIdentifierStart(Rune rune)
        => rune.Value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_' or '$';

    private static bool IsIdentifierPart(Rune rune)
        => IsIdentifierStart(rune) || rune.Value is >= '0' and <= '9';
}
