using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Regex building blocks that reproduce JavaScript regex semantics in .NET.
/// </summary>
/// <remarks>
/// The two traps this exists for are the ones already recorded for the tui port:
/// <list type="bullet">
/// <item><description>
/// JS <c>\d</c> is <c>[0-9]</c>, while .NET <c>\d</c> is <c>\p{Nd}</c> (it also matches Arabic-Indic
/// and other Unicode decimal digits). Every ported pattern writes the class out explicitly.
/// </description></item>
/// <item><description>
/// JS <c>\s</c> and .NET <c>\s</c> are different sets: JS includes U+FEFF and excludes U+0085, .NET
/// is the other way round. <see cref="WhitespaceClass"/> is the JS set, written out explicitly.
/// </description></item>
/// </list>
/// </remarks>
public static class JsRegex
{
    /// <summary>JS <c>\s</c> as a character class, for embedding into a pattern.</summary>
    public const string WhitespaceClass =
        "[\\t\\n\\v\\f\\r \\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF]";

    /// <summary>JS <c>\d</c> as a character class.</summary>
    public const string DigitClass = "[0-9]";

    /// <summary>
    /// JS <c>Number.parseInt</c>: skip leading JS whitespace, take an optional sign, then consume the
    /// longest run of digits valid for <paramref name="radix"/>. Returns <see langword="null"/> where JS
    /// returns <c>NaN</c>.
    /// </summary>
    /// <remarks>
    /// Two details are easy to miss. First, the spec sets <c>stripPrefix</c> for an explicit radix of 16,
    /// so <c>parseInt("0x1f", 16)</c> is 31 rather than 0. Second, V8 accumulates in a bignum and rounds
    /// once at the end, so naive double accumulation diverges on long inputs
    /// (<c>parseInt("9" * 24, 10)</c> is <c>1e24</c>, not <c>1.0000000000000003e24</c>).
    /// </remarks>
    public static double? ParseInt(string value, int radix)
    {
        var index = 0;
        while (index < value.Length && JsString.IsWhitespace(value[index]))
        {
            index++;
        }

        var negative = false;
        if (index < value.Length && (value[index] == '+' || value[index] == '-'))
        {
            negative = value[index] == '-';
            index++;
        }

        // `stripPrefix` is true only for radix 16.
        if (radix == 16 && index + 1 < value.Length && value[index] == '0' && (value[index + 1] is 'x' or 'X'))
        {
            index += 2;
        }

        var start = index;
        var result = BigInteger.Zero;
        while (index < value.Length)
        {
            var digit = DigitValue(value[index]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            result = (result * radix) + digit;
            index++;
        }

        if (index == start)
        {
            return null;
        }

        // V8 converts its exact bignum to a double with a single correctly-rounded step. .NET's
        // `(double)BigInteger` is not that: it drops the low bits instead of rounding to nearest, so
        // `11111111111111111` would become ...110 instead of JS's ...112. Going through the decimal
        // parser, which *is* correctly rounded, matches V8. Values past the double range become
        // ±Infinity, as they do in JS.
        var magnitude = double.Parse(result.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
        return negative ? -magnitude : magnitude;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Compile a JS regex source that has already been rewritten to .NET syntax. Kept so every call site
    /// goes through one place if the options ever need to change (JS has no implicit singleline flag).
    /// </summary>
    public static Regex Compile(string pattern) => new(pattern, RegexOptions.Compiled);
}
