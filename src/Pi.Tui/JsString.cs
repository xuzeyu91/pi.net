using System.Text;

namespace Pi.Tui;

/// <summary>
/// Helpers that reproduce JavaScript string semantics. .NET differs from JS in a few places that
/// matter for the ported modules (see T15 and T23 in <c>docs/tui-porting-status.md</c>): the
/// whitespace set used by <c>trim</c>/<c>\s</c>, the clamping rules of <c>slice</c>, and the
/// Unicode full case mapping used by <c>toLowerCase</c>.
/// </summary>
internal static class JsString
{
    /// <summary>
    /// The JavaScript <c>String.prototype.trim</c> whitespace set. Note this is deliberately not
    /// <see cref="char.IsWhiteSpace(char)"/>: JS includes U+FEFF (zero width no-break space) but
    /// excludes U+0085 (next line), which is the exact opposite of .NET.
    /// </summary>
    public static bool IsWhitespace(char c) => c switch
    {
        ' ' or '\t' or '\n' or '\v' or '\f' or '\r' or '\u00a0' or '\u1680' or '\u2028' or '\u2029'
            or '\u202f' or '\u205f' or '\u3000' or '\ufeff' => true,
        _ => c >= '\u2000' && c <= '\u200a',
    };

    public static string TrimStart(string value)
    {
        var index = 0;
        while (index < value.Length && IsWhitespace(value[index]))
        {
            index++;
        }

        return index == 0 ? value : value.Substring(index);
    }

    public static string TrimEnd(string value)
    {
        var index = value.Length;
        while (index > 0 && IsWhitespace(value[index - 1]))
        {
            index--;
        }

        return index == value.Length ? value : value.Substring(0, index);
    }

    public static string Trim(string value) => TrimEnd(TrimStart(value));

    /// <summary>
    /// JavaScript <c>String.prototype.slice(start, end)</c>. Negative indices count back from the
    /// end and are clamped to zero; indices past the end are clamped to the length.
    /// </summary>
    public static string Slice(string value, int start, int? end = null)
    {
        var length = value.Length;
        var from = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        var to = end is null
            ? length
            : end.Value < 0
                ? Math.Max(length + end.Value, 0)
                : Math.Min(end.Value, length);
        return to <= from ? "" : value.Substring(from, to - from);
    }

    /// <summary>
    /// JavaScript <c>String.prototype[i]</c>: reads <c>undefined</c> (falsy) past the end. The
    /// sentinel <c>'\0'</c> stands in for <c>undefined</c>; it is never a real delimiter or wrapper.
    /// </summary>
    public static char CharAt(string value, int index) => index >= 0 && index < value.Length ? value[index] : '\0';

    /// <summary>Number of code points, matching JS <c>Array.from(value).length</c>.</summary>
    public static int CodePointLength(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// JavaScript <c>String.prototype.toLowerCase</c>, which applies Unicode <em>full</em> case
    /// mapping. <see cref="string.ToLowerInvariant"/> applies <em>simple</em> case mapping, so this
    /// patches the two differences (see <see cref="JsCaseData"/>):
    /// <list type="bullet">
    /// <item>56 code points .NET has no mapping for at all, most notably U+0130 (İ), which expands
    /// to two code points.</item>
    /// <item>U+03A3 (Σ), which takes the word-final form U+03C2 (ς) per the Final_Sigma rule.</item>
    /// </list>
    /// Every other code point matches <c>Rune.ToLowerInvariant</c> exactly.
    /// </summary>
    public static string ToLowerCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // Fast path: a string that is pure ASCII without upper-case letters cannot change, and
        // cannot contain U+03A3 or any of the override code points.
        var plain = true;
        foreach (var c in value)
        {
            if (c >= '\u0080' || (c >= 'A' && c <= 'Z'))
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return value;
        }

        var runes = value.EnumerateRunes().ToArray();
        var builder = new StringBuilder(value.Length + 4);
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            var codePoint = rune.Value;
            if (codePoint == Sigma)
            {
                builder.Append(IsFinalSigma(runes, i) ? '\u03c2' : '\u03c3');
                continue;
            }

            builder.Append(
                JsCaseData.LowerOverrides.TryGetValue(codePoint, out var mapped)
                    ? mapped
                    : Rune.ToLowerInvariant(rune).ToString());
        }

        return builder.ToString();
    }

    /// <summary>Whether a code point has the Unicode <c>Cased</c> property (V8's view of it).</summary>
    public static bool IsCased(int codePoint) => InRanges(JsCaseData.CasedRanges, codePoint);

    /// <summary>Whether a code point has the Unicode <c>Case_Ignorable</c> property (V8's view).</summary>
    public static bool IsCaseIgnorable(int codePoint) => InRanges(JsCaseData.CaseIgnorableRanges, codePoint);

    private const int Sigma = 0x03a3;

    /// <summary>
    /// The Unicode Final_Sigma condition: <paramref name="index"/> points at U+03A3, which is
    /// word-final when it is preceded by a cased letter and not followed by one, ignoring
    /// case-ignorable characters on both sides.
    /// </summary>
    private static bool IsFinalSigma(Rune[] runes, int index)
    {
        var precededByCased = false;
        for (var i = index - 1; i >= 0; i--)
        {
            if (IsCaseIgnorable(runes[i].Value))
            {
                continue;
            }

            precededByCased = IsCased(runes[i].Value);
            break;
        }

        if (!precededByCased)
        {
            return false;
        }

        for (var i = index + 1; i < runes.Length; i++)
        {
            if (IsCaseIgnorable(runes[i].Value))
            {
                continue;
            }

            return !IsCased(runes[i].Value);
        }

        return true;
    }

    /// <summary>Binary search over a flat table of inclusive (start, end) code point pairs.</summary>
    internal static bool InRanges(ReadOnlySpan<int> ranges, int codePoint)
    {
        var low = 0;
        var high = (ranges.Length / 2) - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (codePoint < ranges[mid * 2])
            {
                high = mid - 1;
            }
            else if (codePoint > ranges[(mid * 2) + 1])
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
