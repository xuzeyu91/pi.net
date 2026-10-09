using System.Globalization;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of the <c>brace-expansion</c> package: expand <c>{a,b}</c> sets and <c>{1..5}</c> sequences the way
/// Bash does. Used by <see cref="Glob.BraceExpand"/>.
/// </summary>
/// <remarks>
/// <para>
/// The JS module guards against three denial-of-service shapes (CVE-2026-14257 and friends) with four
/// independent limits: <c>max</c> (results), <c>maxLength</c> (accumulated characters), <c>maxDepth</c>
/// (nesting) and <c>maxRewrites</c> (the <c>{a},b}</c> restart quirk). All four are ported, including the
/// iterative (rather than recursive) tail scan that keeps the stack flat.
/// </para>
/// <para>
/// Differences from the JS original:
/// <list type="bullet">
/// <item>The escaping sentinels are fixed NUL-delimited literals instead of <c>Math.random()</c>-derived
/// ones. They are internal only — both versions assume the input cannot contain the sentinel.</item>
/// <item>Sequence arithmetic runs on <see cref="double"/> like JS, and <see cref="NumericToString"/> covers
/// the integer range JS prints as plain digits (<c>|n| &lt; 1e21</c>). Beyond that JS switches to exponential
/// notation, which is not reproduced; such bounds are unreachable from a glob pattern.</item>
/// </list>
/// </para>
/// <para>
/// One upstream comment is stale and is deliberately not followed: the source says a leading <c>{}</c> makes
/// <c>{},a}b</c> "expand to nothing", but the code returns the input verbatim. The port follows the code.
/// </para>
/// </remarks>
public static class BraceExpansion
{
    public const int ExpansionMax = 100_000;

    public const int ExpansionMaxLength = 4_000_000;

    public const int ExpansionMaxDepth = 1_000;

    public const int ExpansionMaxRewrites = 1_000;

    private const string EscSlash = "\0SLASH\0";

    private const string EscOpen = "\0OPEN\0";

    private const string EscClose = "\0CLOSE\0";

    private const string EscComma = "\0COMMA\0";

    private const string EscPeriod = "\0PERIOD\0";

    private static readonly System.Text.RegularExpressions.Regex CommaNotCommaThenBrace =
        new(@",(?!,).*\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex EndsWithDollar =
        new(@"\$$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex NumericSequence =
        new(@"^-?\d+\.\.-?\d+(?:\.\.-?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex AlphaSequence =
        new(@"^[a-zA-Z]\.\.[a-zA-Z](?:\.\.-?\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex SequenceSeparator =
        new(@"\.\.", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex PaddedNumber =
        new(@"^-?0\d", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Limits applied to one expansion. Mirrors the JS <c>options</c> object.</summary>
    public sealed record Options
    {
        public int Max { get; init; } = ExpansionMax;

        public int MaxLength { get; init; } = ExpansionMaxLength;

        public int MaxDepth { get; init; } = ExpansionMaxDepth;

        public int MaxRewrites { get; init; } = ExpansionMaxRewrites;
    }

    /// <summary>JS <c>expand(str, options)</c>.</summary>
    public static IReadOnlyList<string> Expand(string str, Options? options = null)
    {
        if (str.Length == 0) return [];

        var max = options?.Max ?? ExpansionMax;
        var maxLength = options?.MaxLength ?? ExpansionMaxLength;
        var maxDepth = options?.MaxDepth ?? ExpansionMaxDepth;
        var maxRewrites = options?.MaxRewrites ?? ExpansionMaxRewrites;

        // Bash 4.3 preserves the first two bytes of a leading `{}` but only at the top level, so
        // `{},a}b` expands to nothing while `a{},b}c` becomes `[a}c, abc]`. Escaping the leading `{}`
        // reproduces that quirk.
        if (JsString.Slice(str, 0, 2) == "{}") str = "\\{\\}" + JsString.Slice(str, 2);

        var expanded = Expand_(EscapeBraces(str), max, maxLength, maxDepth, 0, maxRewrites, true);
        var result = new List<string>(expanded.Count);
        foreach (var item in expanded) result.Add(UnescapeBraces(item));
        return result;
    }

    private static string EscapeBraces(string str)
    {
        // Order matters: the backslash-pair escape has to run before the single-escape ones.
        return str
            .Replace("\\\\", EscSlash, StringComparison.Ordinal)
            .Replace("\\{", EscOpen, StringComparison.Ordinal)
            .Replace("\\}", EscClose, StringComparison.Ordinal)
            .Replace("\\,", EscComma, StringComparison.Ordinal)
            .Replace("\\.", EscPeriod, StringComparison.Ordinal);
    }

    private static string UnescapeBraces(string str)
    {
        return str
            .Replace(EscSlash, "\\", StringComparison.Ordinal)
            .Replace(EscOpen, "{", StringComparison.Ordinal)
            .Replace(EscClose, "}", StringComparison.Ordinal)
            .Replace(EscComma, ",", StringComparison.Ordinal)
            .Replace(EscPeriod, ".", StringComparison.Ordinal);
    }

    /// <summary>Like <c>target.push(...items)</c> without the array-spread stack overflow.</summary>
    private static void PushAll(List<string> target, List<string> items)
    {
        foreach (var item in items) target.Add(item);
    }

    /// <summary>JS <c>parseCommaParts</c>: <c>split(",")</c> that treats <c>{a,{b,c},d}</c> as three members.</summary>
    private static List<string> ParseCommaParts(string str)
    {
        var parts = new List<string>();
        var carry = "";
        while (true)
        {
            var m = BalancedMatch.Balanced("{", "}", str);
            if (m is null)
            {
                var tail = new List<string>(str.Split(','));
                tail[0] = carry + tail[0];
                PushAll(parts, tail);
                return parts;
            }

            var p = new List<string>(m.Pre.Split(','));
            p[0] = carry + p[0];
            p[^1] += "{" + m.Body + "}";
            if (m.Post.Length == 0)
            {
                PushAll(parts, p);
                return parts;
            }

            carry = p[^1];
            p.RemoveAt(p.Count - 1);
            PushAll(parts, p);
            str = m.Post;
        }
    }

    private static string Embrace(string str) => "{" + str + "}";

    private static bool IsPadded(string el) => PaddedNumber.IsMatch(el);

    private static bool Lte(double i, double y) => i <= y;

    private static bool Gte(double i, double y) => i >= y;

    /// <summary>
    /// Build <c>acc[a] + pre + values[v]</c> for every combination, capping the result count at
    /// <paramref name="max"/> and the accumulated characters at <paramref name="maxLength"/>.
    /// </summary>
    private static List<string> Combine(
        List<string> acc, string pre, List<string> values, int max, int maxLength, bool dropEmpties)
    {
        var result = new List<string>();
        var length = 0;
        foreach (var a in acc)
        {
            foreach (var v in values)
            {
                if (result.Count >= max) return result;
                var expansion = a + pre + v;
                // Bash drops empty results at the top level, and does so before they count against `max`.
                if (dropEmpties && expansion.Length == 0) continue;
                if (length + expansion.Length > maxLength) return result;
                result.Add(expansion);
                length += expansion.Length;
            }
        }

        return result;
    }

    /// <summary>JS <c>numeric(str)</c>: the parsed number, or the first code unit when it is not numeric.</summary>
    private static double Numeric(string str)
    {
        var parsed = JsRegex.ParseInt(str, 10);
        if (parsed is not null) return parsed.Value;
        return str.Length > 0 ? str[0] : double.NaN;
    }

    /// <summary>JS <c>String(number)</c> for the integer range that prints as plain digits.</summary>
    private static string NumericToString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == Math.Floor(value) && Math.Abs(value) < 1e21)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        // JS switches to exponential notation past 1e21; such bounds are unreachable from a glob pattern,
        // so the .NET rendering is accepted here (documented in the type remarks).
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The expansion values of a single numeric or alphabetic sequence body.</summary>
    private static List<string> ExpandSequence(string body, bool isAlphaSequence, int max, int maxLength)
    {
        var n = SequenceSeparator.Split(body);
        var result = new List<string>();
        if (n.Length < 2) return result;

        var x = Numeric(n[0]);
        var y = Numeric(n[1]);
        if (double.IsNaN(x) || double.IsNaN(y)) return result;
        var width = Math.Max(n[0].Length, n[1].Length);
        var incr = n.Length == 3 ? Math.Max(Math.Abs(Numeric(n[2])), 1) : 1;
        Func<double, double, bool> test = Lte;
        var reverse = y < x;
        if (reverse)
        {
            incr *= -1;
            test = Gte;
        }

        var pad = false;
        foreach (var part in n)
        {
            if (IsPadded(part)) pad = true;
        }

        var length = 0;
        for (var i = x; test(i, y) && result.Count < max; i += incr)
        {
            string c;
            if (isAlphaSequence)
            {
                c = ((char)((int)i & 0xFFFF)).ToString();
                if (c == "\\") c = "";
            }
            else
            {
                c = NumericToString(i);
                if (pad)
                {
                    var need = width - c.Length;
                    if (need > 0)
                    {
                        var z = new string('0', need);
                        c = i < 0 ? "-" + z + JsString.Slice(c, 1) : z + c;
                    }
                }
            }

            if (length + c.Length > maxLength) break;
            result.Add(c);
            length += c.Length;
        }

        return result;
    }

    private static List<string> Expand_(
        string str, int max, int maxLength, int maxDepth, int depth, int maxRewrites, bool isTop)
    {
        // Too deeply nested to keep following: treat the rest as literal. Truncating rather than throwing
        // keeps `Expand` total, matching `max` and `maxLength`.
        if (depth > maxDepth) return [str];

        var acc = new List<string> { "" };
        var rewrites = 0;
        var dropEmpties = false;
        var firstGroup = true;
        while (true)
        {
            var m = BalancedMatch.Balanced("{", "}", str);
            if (m is null) return Combine(acc, str, [""], max, maxLength, dropEmpties);

            var pre = m.Pre;
            if (EndsWithDollar.IsMatch(pre))
            {
                acc = Combine(acc, pre + "{" + m.Body + "}", [""], max, maxLength,
                    dropEmpties && m.Post.Length == 0);
                firstGroup = false;
                if (m.Post.Length == 0) break;
                str = m.Post;
                continue;
            }

            var isNumericSequence = NumericSequence.IsMatch(m.Body);
            var isAlphaSequence = AlphaSequence.IsMatch(m.Body);
            var isSequence = isNumericSequence || isAlphaSequence;
            var isOptions = m.Body.Contains(',', StringComparison.Ordinal);
            if (!isSequence && !isOptions)
            {
                // Bash's `{a},b}` quirk: absorb one `}` and restart the scan.
                if (rewrites < maxRewrites && CommaNotCommaThenBrace.IsMatch(m.Post))
                {
                    rewrites++;
                    str = m.Pre + "{" + m.Body + EscClose + m.Post;
                    isTop = true;
                    continue;
                }

                // Nothing here expands, so the whole remaining string is literal.
                return Combine(acc, pre + "{" + m.Body + "}" + m.Post, [""], max, maxLength, dropEmpties);
            }

            if (firstGroup)
            {
                dropEmpties = isTop && !isSequence;
                firstGroup = false;
            }

            List<string> values;
            if (isSequence)
            {
                values = ExpandSequence(m.Body, isAlphaSequence, max, maxLength);
            }
            else
            {
                var n = ParseCommaParts(m.Body);
                if (n.Count == 1)
                {
                    // x{{a,b}}y ==> x{a}y x{b}y
                    var nested = new List<string>();
                    foreach (var item in Expand_(n[0], max, maxLength, maxDepth, depth + 1, maxRewrites, false))
                    {
                        nested.Add(Embrace(item));
                    }

                    n = nested;
                    if (n.Count == 1)
                    {
                        acc = Combine(acc, pre + n[0], [""], max, maxLength, dropEmpties && m.Post.Length == 0);
                        if (m.Post.Length == 0) break;
                        str = m.Post;
                        continue;
                    }
                }

                // Values that `combine` is going to drop as empty produce no result, so they must not count
                // against `max` — otherwise `{a,,b}` with `max: 2` would stop at `['a', '']`.
                var dropsEmpties = dropEmpties && m.Post.Length == 0 && pre.Length == 0;
                foreach (var entry in acc)
                {
                    if (!dropsEmpties) break;
                    if (entry.Length > 0) dropsEmpties = false;
                }

                values = [];
                var valuesLength = 0;
                var stop = false;
                foreach (var member in n)
                {
                    if (stop) break;
                    foreach (var v in Expand_(member, max, maxLength, maxDepth, depth + 1, maxRewrites, false))
                    {
                        if (dropsEmpties && v.Length == 0) continue;
                        if (values.Count >= max || valuesLength + v.Length > maxLength)
                        {
                            stop = true;
                            break;
                        }

                        values.Add(v);
                        valuesLength += v.Length;
                    }
                }
            }

            acc = Combine(acc, pre, values, max, maxLength, dropEmpties && m.Post.Length == 0);
            if (m.Post.Length == 0) break;
            str = m.Post;
        }

        return acc;
    }
}
