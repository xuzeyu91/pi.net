using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of minimatch's <c>brace-expressions.js</c>: turn a glob character class (<c>[a-z]</c>,
/// <c>[!abc]</c>, <c>[[:alpha:]]</c>) into an equivalent regular-expression source.
/// </summary>
/// <remarks>
/// <para>
/// The POSIX class table is iterated in insertion order, exactly like <c>Object.entries</c> — the order is
/// observable, because the first matching class wins and <c>[[:alpha:]]</c> is a prefix of nothing else but
/// <c>[[:alnum:]]</c> is checked first.
/// </para>
/// <para>
/// The third element of a table entry means "negated": <c>[[:graph:]]</c> is
/// <c>\p{Z}\p{C}</c> collected into the *negated* bucket rather than the positive one.
/// </para>
/// </remarks>
public static class GlobClass
{
    /// <summary>Result of <see cref="ParseClass"/>: <c>[src, needUflag, consumed, magic]</c>.</summary>
    public readonly record struct ParsedClass(string Src, bool NeedUFlag, int Consumed, bool Magic);

    private sealed record PosixClass(string Name, string Translation, bool NeedsUFlag, bool Negated = false);

    /// <summary>Insertion order is load-bearing; see the type remarks.</summary>
    private static readonly PosixClass[] PosixClasses =
    [
        new("[:alnum:]", @"\p{L}\p{Nl}\p{Nd}", true),
        new("[:alpha:]", @"\p{L}\p{Nl}", true),
        new("[:ascii:]", @"\x00-\x7f", false),
        new("[:blank:]", @"\p{Zs}\t", true),
        new("[:cntrl:]", @"\p{Cc}", true),
        new("[:digit:]", @"\p{Nd}", true),
        new("[:graph:]", @"\p{Z}\p{C}", true, true),
        new("[:lower:]", @"\p{Ll}", true),
        new("[:print:]", @"\p{C}", true),
        new("[:punct:]", @"\p{P}", true),
        new("[:space:]", @"\p{Z}\t\r\n\v\f", true),
        new("[:upper:]", @"\p{Lu}", true),
        new("[:word:]", @"\p{L}\p{Nl}\p{Nd}\p{Pc}", true),
        new("[:xdigit:]", "A-Fa-f0-9", false),
    ];

    /// <summary>Only a few characters need escaping inside a brace expression: <c>[ \ ] -</c>.</summary>
    private static readonly Regex BraceEscapePattern = new(@"[\[\]\\-]", RegexOptions.CultureInvariant);

    /// <summary>All regular-expression metacharacters.</summary>
    private static readonly Regex RegExpEscapePattern = new(@"[-[\]{}()*+?.,\\^$|#\s]", RegexOptions.CultureInvariant);

    /// <summary>JS <c>braceEscape</c>.</summary>
    internal static string BraceEscape(string s) => BraceEscapePattern.Replace(s, "\\$&");

    /// <summary>JS <c>regexpEscape</c>.</summary>
    internal static string RegExpEscape(string s) => RegExpEscapePattern.Replace(s, "\\$&");

    /// <summary>
    /// JS <c>parseClass(glob, position)</c>. Returns the regex source, whether the <c>u</c> flag is needed,
    /// how many characters were consumed (0 means "not a class, treat as literal") and whether the class
    /// counts as magic.
    /// </summary>
    public static ParsedClass ParseClass(string glob, int position)
    {
        var pos = position;
        if (JsString.CharAt(glob, pos) != '[')
        {
            throw new InvalidOperationException("not in a brace expression");
        }

        var ranges = new List<string>();
        var negs = new List<string>();
        var i = pos + 1;
        var sawStart = false;
        var uflag = false;
        var escaping = false;
        var negate = false;
        var endPos = pos;
        var rangeStart = "";

        while (i < glob.Length)
        {
            var c = JsString.Slice(glob, i, i + 1);
            if ((c == "!" || c == "^") && i == pos + 1)
            {
                negate = true;
                i++;
                continue;
            }

            if (c == "]" && sawStart && !escaping)
            {
                endPos = i + 1;
                break;
            }

            sawStart = true;
            if (c == "\\")
            {
                if (!escaping)
                {
                    escaping = true;
                    i++;
                    continue;
                }

                // Escaped backslash: fall through and treat it like a normal character.
            }

            if (c == "[" && !escaping)
            {
                // Either a POSIX class, a collation equivalent, or just a literal '['.
                var matchedClass = false;
                foreach (var entry in PosixClasses)
                {
                    if (!JsString.StartsWith(JsString.Slice(glob, i), entry.Name)) continue;
                    // `[a-[:alpha]]` is invalid, unlike `[a-[]`.
                    if (rangeStart.Length > 0)
                    {
                        return new ParsedClass("$.", false, glob.Length - pos, true);
                    }

                    i += entry.Name.Length;
                    if (entry.Negated) negs.Add(entry.Translation);
                    else ranges.Add(entry.Translation);
                    uflag = uflag || entry.NeedsUFlag;
                    matchedClass = true;
                    break;
                }

                if (matchedClass) continue;
            }

            // From here on it is just a normal character.
            escaping = false;
            if (rangeStart.Length > 0)
            {
                // Drop the range if it is not valid; the rest of the class can still match.
                if (string.CompareOrdinal(c, rangeStart) > 0)
                {
                    ranges.Add(BraceEscape(rangeStart) + "-" + BraceEscape(c));
                }
                else if (string.Equals(c, rangeStart, StringComparison.Ordinal))
                {
                    ranges.Add(BraceEscape(c));
                }

                rangeStart = "";
                i++;
                continue;
            }

            // Could be the start of a range: `c-d`, `c-]`, `c-<more…>]`, or just `c]`.
            if (JsString.StartsWith(JsString.Slice(glob, i + 1), "-]"))
            {
                ranges.Add(BraceEscape(c + "-"));
                i += 2;
                continue;
            }

            if (JsString.StartsWith(JsString.Slice(glob, i + 1), "-"))
            {
                rangeStart = c;
                i += 2;
                continue;
            }

            // Not the start of a range: a single character.
            ranges.Add(BraceEscape(c));
            i++;
        }

        if (endPos < i)
        {
            // Never saw the end of the class. Not a valid class, but it may still match as a literal.
            return new ParsedClass("", false, 0, false);
        }

        // No ranges and no negates: a class that cannot match anything poisons the whole glob.
        if (ranges.Count == 0 && negs.Count == 0)
        {
            return new ParsedClass("$.", false, glob.Length - pos, true);
        }

        // One positive single-character range is not magic — `[_]` is a legitimate way to escape `_`.
        if (negs.Count == 0 && ranges.Count == 1 && SingleCharRange.IsMatch(ranges[0]) && !negate)
        {
            var r = ranges[0].Length == 2 ? JsString.Slice(ranges[0], -1) : ranges[0];
            return new ParsedClass(RegExpEscape(r), false, endPos - pos, false);
        }

        var sranges = "[" + (negate ? "^" : "") + string.Join("", ranges) + "]";
        var snegs = "[" + (negate ? "" : "^") + string.Join("", negs) + "]";
        var comb = ranges.Count > 0 && negs.Count > 0 ? "(" + sranges + "|" + snegs + ")"
            : ranges.Count > 0 ? sranges
            : snegs;
        return new ParsedClass(comb, uflag, endPos - pos, true);
    }

    private static readonly Regex SingleCharRange = new(@"^\\?.$", RegexOptions.CultureInvariant);
}
