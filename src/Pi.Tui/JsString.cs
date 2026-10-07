namespace Pi.Tui;

/// <summary>
/// Helpers that reproduce JavaScript string semantics. .NET differs from JS in a few places that
/// matter for the ported modules (see T15 in <c>docs/tui-porting-status.md</c>): the whitespace set
/// used by <c>trim</c>/<c>\s</c>, and the clamping rules of <c>slice</c>.
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
}
