using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of the <c>balanced-match</c> package: find the outermost balanced pair of delimiters in a string.
/// </summary>
/// <remarks>
/// Used by the ported <c>brace-expansion</c> to walk <c>{...}</c> groups. The JS module also accepts
/// <see cref="Regex"/> delimiters; that form is ported as an overload, although brace expansion only ever
/// passes the literal <c>{</c> and <c>}</c>.
/// </remarks>
public static class BalancedMatch
{
    /// <summary>JS <c>balanced(a, b, str)</c> for literal delimiters.</summary>
    public static BalancedMatchResult? Balanced(string a, string b, string str)
    {
        var range = Range(a, b, str);
        if (range is null) return null;
        return Compose(str, range.Value.Start, range.Value.End, a.Length, b.Length);
    }

    /// <summary>JS <c>balanced(a, b, str)</c> for regex delimiters.</summary>
    public static BalancedMatchResult? Balanced(Regex a, Regex b, string str)
    {
        // JS: `const ma = a instanceof RegExp ? maybeMatch(a, str) : a` — the delimiter collapses to the
        // matched text, and `range` then works on plain strings.
        var ma = MaybeMatch(a, str);
        var mb = MaybeMatch(b, str);
        if (ma is null || mb is null) return null;
        var range = Range(ma, mb, str);
        if (range is null) return null;
        return Compose(str, range.Value.Start, range.Value.End, ma.Length, mb.Length);
    }

    /// <summary>JS <c>range(a, b, str)</c>.</summary>
    public static (int Start, int End)? Range(string a, string b, string str)
    {
        var ai = JsString.IndexOf(str, a, 0);
        var bi = JsString.IndexOf(str, b, ai + 1);
        var i = ai;
        if (ai < 0 || bi <= 0) return null;

        // a === b: the first two occurrences are the pair.
        if (string.Equals(a, b, StringComparison.Ordinal)) return (ai, bi);

        var begs = new List<int>();
        var left = str.Length;
        var right = -1;
        var haveRight = false;
        (int Start, int End)? result = null;
        while (i >= 0 && result is null)
        {
            if (i == ai)
            {
                begs.Add(i);
                ai = JsString.IndexOf(str, a, i + 1);
            }
            else if (begs.Count == 1)
            {
                var r = begs[^1];
                begs.RemoveAt(begs.Count - 1);
                result = (r, bi);
            }
            else
            {
                var beg = begs[^1];
                begs.RemoveAt(begs.Count - 1);
                if (beg < left)
                {
                    left = beg;
                    right = bi;
                    haveRight = true;
                }

                bi = JsString.IndexOf(str, b, i + 1);
            }

            i = ai < bi && ai >= 0 ? ai : bi;
        }

        if (begs.Count > 0 && haveRight) result = (left, right);
        return result;
    }

    private static BalancedMatchResult Compose(string str, int start, int end, int aLength, int bLength)
        => new()
        {
            Start = start,
            End = end,
            Pre = JsString.Slice(str, 0, start),
            // JS: str.slice(r[0] + ma.length, r[1]) — an end before the start yields "".
            Body = JsString.Slice(str, start + aLength, end),
            Post = JsString.Slice(str, end + bLength, str.Length),
        };

    private static string? MaybeMatch(Regex regex, string str)
    {
        var match = regex.Match(str);
        return match.Success ? match.Value : null;
    }
}

/// <summary>Result of <see cref="BalancedMatch.Balanced(string, string, string)"/>.</summary>
public sealed record BalancedMatchResult
{
    public required int Start { get; init; }

    public required int End { get; init; }

    public required string Pre { get; init; }

    public required string Body { get; init; }

    public required string Post { get; init; }
}
