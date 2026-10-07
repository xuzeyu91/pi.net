using System.Globalization;
using System.Text;

namespace Pi.Tui;

/// <summary>
/// Port of <c>utils.ts</c>'s width model: grapheme segmentation (via .NET <see cref="StringInfo"/>,
/// which implements UAX #29 extended grapheme clusters) plus a compact East Asian Width table.
///
/// Deviation from TS: the TS code depends on the <c>get-east-asian-width</c> npm package and the
/// <c>\p{RGI_Emoji}</c> / <c>\p{Default_Ignorable_Code_Point}</c> Unicode property regexes. .NET has
/// no built-in equivalent, so this port uses an explicit wide-range table and an emoji-presentation
/// heuristic. The tables cover the ranges that occur in real terminal text (CJK, Hangul, fullwidth
/// forms, the common emoji blocks); obscure code points may fall back to width 1 where the TS code
/// would report 2.
/// </summary>
public static class UnicodeWidth
{
    private const int WidthCacheSize = 512;
    private static readonly Dictionary<string, int> WidthCache = new();
    private static readonly Queue<string> WidthCacheOrder = new();

    /// <summary>Enumerate UAX #29 extended grapheme clusters.</summary>
    public static IEnumerable<string> Graphemes(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }

    /// <summary>Fast path: every UTF-16 unit is printable ASCII (0x20..0x7E).</summary>
    public static bool IsPrintableAscii(string str)
    {
        foreach (var ch in str)
        {
            if (ch < 0x20 || ch > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Calculate the visible width of a string in terminal columns.</summary>
    public static int VisibleWidth(string str)
    {
        if (str.Length == 0)
        {
            return 0;
        }
        if (IsPrintableAscii(str))
        {
            return str.Length;
        }
        if (WidthCache.TryGetValue(str, out var cached))
        {
            return cached;
        }

        var clean = str;
        if (clean.Contains('\t'))
        {
            clean = clean.Replace("\t", "   ");
        }
        if (clean.Contains('\x1b'))
        {
            clean = Ansi.StripTerminalSequences(clean);
        }

        var width = 0;
        foreach (var segment in Graphemes(clean))
        {
            width += GraphemeWidth(segment);
        }

        if (WidthCache.Count >= WidthCacheSize)
        {
            var first = WidthCacheOrder.Dequeue();
            WidthCache.Remove(first);
        }
        WidthCache[str] = width;
        WidthCacheOrder.Enqueue(str);
        return width;
    }

    /// <summary>Calculate the terminal width of a single grapheme cluster.</summary>
    public static int GraphemeWidth(string segment)
    {
        if (segment == "\t")
        {
            return 3;
        }

        // Some marks occupy cells even without a base character.
        if (IsAllTerminalSpacingMark(segment))
        {
            return RuneCount(segment);
        }

        // Zero-width clusters.
        if (IsAllZeroWidth(segment))
        {
            return 0;
        }

        // Emoji check with pre-filter.
        if (CouldBeEmoji(segment) && IsRgiEmoji(segment))
        {
            return 2;
        }

        // Get the base visible code point.
        var baseText = StripLeadingNonPrinting(segment);
        if (baseText.Length == 0)
        {
            return 0;
        }
        var baseRunes = baseText.EnumerateRunes().ToArray();
        var cp = baseRunes[0].Value;

        // Regional indicator symbols are usually rendered full-width.
        if (cp is >= 0x1f1e6 and <= 0x1f1ff)
        {
            return 2;
        }

        var width = EastAsianWidth(cp);

        var followsMark = false;
        for (var idx = 1; idx < baseRunes.Length; idx++)
        {
            var rune = baseRunes[idx];
            var single = rune.ToString();
            if (IsTerminalSpacingMarkRune(rune.Value))
            {
                width += 1;
                followsMark = false;
            }
            else if (IsMarkRune(rune.Value))
            {
                followsMark = true;
            }
            else if (!IsNonPrintingRune(rune.Value))
            {
                var c = rune.Value;
                if (followsMark || (c >= 0xff00 && c <= 0xffef))
                {
                    width += EastAsianWidth(c);
                }
                else if (c is 0x0e33 or 0x0eb3)
                {
                    width += 1;
                }
                followsMark = false;
            }
            _ = single;
        }

        return width;
    }

    private static int RuneCount(string s)
    {
        var count = 0;
        foreach (var _ in s.EnumerateRunes())
        {
            count++;
        }
        return count;
    }

    /// <summary>Return the terminal-cell range occupied by the grapheme at a visible column.</summary>
    public static (int Start, int End)? GetGraphemeCellRange(string line, int column)
    {
        var currentCol = 0;
        var i = 0;
        while (i < line.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(line, i);
            if (ansi is { } code)
            {
                i += code.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && Ansi.ExtractAnsiCode(line, textEnd) is null)
            {
                textEnd++;
            }
            foreach (var segment in Graphemes(line.Substring(i, textEnd - i)))
            {
                var width = GraphemeWidth(segment);
                if (width > 0 && column >= currentCol && column < currentCol + width)
                {
                    return (currentCol, currentCol + width);
                }
                currentCol += width;
            }
            i = textEnd;
        }
        return null;
    }

    /// <summary>Return the OSC 8 hyperlink covering a visible terminal column, or null.</summary>
    public static string? GetOsc8LinkAtColumn(string line, int column)
    {
        string? activeUrl = null;
        var currentCol = 0;
        var i = 0;
        while (i < line.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(line, i);
            if (ansi is { } code)
            {
                var parse = AnsiCodeTracker.ParseOsc8Hyperlink(code.Code);
                if (parse.IsOsc8)
                {
                    activeUrl = parse.Hyperlink?.Url;
                }
                i += code.Length;
                continue;
            }
            var textEnd = i;
            while (textEnd < line.Length && Ansi.ExtractAnsiCode(line, textEnd) is null)
            {
                textEnd++;
            }
            foreach (var segment in Graphemes(line.Substring(i, textEnd - i)))
            {
                var width = segment == "\t" ? 3 : GraphemeWidth(segment);
                if (column >= currentCol && column < currentCol + width)
                {
                    return activeUrl;
                }
                currentCol += width;
            }
            i = textEnd;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Character classification (mirrors utils.ts regexes)
    // ------------------------------------------------------------------

    private static bool IsZeroWidthRune(int cp) =>
        IsControlRune(cp) || IsMarkRune(cp) || IsSurrogateRune(cp) || IsDefaultIgnorableRune(cp);

    private static bool IsControlRune(int cp)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat is UnicodeCategory.Control or UnicodeCategory.Format;
    }

    private static bool IsMarkRune(int cp)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    private static bool IsSurrogateRune(int cp) => cp is >= 0xd800 and <= 0xdfff;

    private static bool IsDefaultIgnorableRune(int cp)
    {
        // Approximation of \p{Default_Ignorable_Code_Point} via the format category and the
        // well-known fixed ranges.
        if (CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.Format)
        {
            return true;
        }
        return cp is 0x00ad or 0x034f or 0x115f or 0x1160 or 0x17b4 or 0x17b5 or 0x180b or 0x180c
            or 0x180d or 0x180e or 0x180f or 0x200b or 0x200c or 0x200d or 0x200e or 0x200f
            or 0x202a or 0x202b or 0x202c or 0x202d or 0x202e or 0x2060 or 0x2061 or 0x2062
            or 0x2063 or 0x2064 or 0x2065 or 0x2066 or 0x2067 or 0x2068 or 0x2069 or 0x206a
            or 0x206b or 0x206c or 0x206d or 0x206e or 0x206f or 0x3164 or 0xfeff or 0xffa0
            or 0x1bca0 or 0x1bca1 or 0x1bca2 or 0x1bca3 or 0x1d173 or 0x1d174 or 0x1d175
            or 0x1d176 or 0x1d177 or 0x1d178 or 0x1d179 or 0x1d17a or 0xe0000 or 0xe0001
            or 0xe0002 or 0xe0003 or 0xe0004 or 0xe0005 or 0xe0006 or 0xe0007 or 0xe0008
            or 0xe0009 or 0xe000a or 0xe000b or 0xe000c or 0xe000d or 0xe000e or 0xe000f
            or 0xe0010 or 0xe0011 or 0xe0012 or 0xe0013 or 0xe0014 or 0xe0015 or 0xe0016
            or 0xe0017 or 0xe0018 or 0xe0019 or 0xe001a or 0xe001b or 0xe001c or 0xe001d
            or 0xe001e or 0xe001f or 0xe0080 or 0xe00ff or 0xe01f0;
    }

    private static bool IsAllZeroWidth(string segment)
    {
        foreach (var rune in segment.EnumerateRunes())
        {
            if (!IsZeroWidthRune(rune.Value))
            {
                return false;
            }
        }
        return segment.Length > 0;
    }

    private static string StripLeadingNonPrinting(string segment)
    {
        var idx = 0;
        while (idx < segment.Length)
        {
            var status = Rune.DecodeFromUtf16(segment.AsSpan(idx), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                break;
            }
            if (!IsZeroWidthRune(rune.Value))
            {
                break;
            }
            idx += consumed;
        }
        return idx == 0 ? segment : segment.Substring(idx);
    }

    private static bool IsNonPrintingRune(int cp) => IsZeroWidthRune(cp);

    private static bool IsTerminalSpacingMarkRune(int cp)
    {
        if (cp is 0x1734 or 0x302e or 0x302f)
        {
            return false;
        }
        if (cp is 0x065f or 0x0f7f or 0x102b or 0x102c or 0x1031 or 0x1033 or 0x1034 or 0x1035
            or 0x1038 or 0x103a or 0x103b or 0x103c or 0x103d or 0x103e)
        {
            return true;
        }
        return CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.SpacingCombiningMark;
    }

    private static bool IsAllTerminalSpacingMark(string segment)
    {
        var any = false;
        foreach (var rune in segment.EnumerateRunes())
        {
            any = true;
            if (!IsTerminalSpacingMarkRune(rune.Value))
            {
                return false;
            }
        }
        return any;
    }

    private static bool CouldBeEmoji(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }
        var rune = segment.EnumerateRunes().First();
        var cp = rune.Value;
        return (cp is >= 0x1f000 and <= 0x1fbff)
            || (cp is >= 0x2300 and <= 0x23ff)
            || (cp is >= 0x2600 and <= 0x27bf)
            || (cp is >= 0x2b50 and <= 0x2b55)
            || segment.Contains('\ufe0f')
            || segment.Length > 2;
    }

    private static bool IsRgiEmoji(string segment)
    {
        if (segment.Contains('\u200d') || segment.Contains('\ufe0f'))
        {
            return true;
        }
        var runes = segment.EnumerateRunes().ToArray();
        if (runes.Length == 2 && runes[0].Value is >= 0x1f1e6 and <= 0x1f1ff
            && runes[1].Value is >= 0x1f1e6 and <= 0x1f1ff)
        {
            return true;
        }
        return runes.Length == 1 && IsEmojiPresentationRune(runes[0].Value);
    }

    private static bool IsEmojiPresentationRune(int cp) => InRanges(cp, EmojiPresentationRanges);

    // ------------------------------------------------------------------
    // East Asian Width / emoji presentation tables
    // ------------------------------------------------------------------

    private static readonly (int Start, int End)[] WideRanges =
    {
        (0x1100, 0x115f), (0x231a, 0x231b), (0x2329, 0x232a), (0x23e9, 0x23ec), (0x23f0, 0x23f0),
        (0x23f3, 0x23f3), (0x25fd, 0x25fe), (0x2614, 0x2615), (0x2648, 0x2653), (0x267f, 0x267f),
        (0x2693, 0x2693), (0x26a1, 0x26a1), (0x26aa, 0x26ab), (0x26bd, 0x26be), (0x26c4, 0x26c5),
        (0x26ce, 0x26ce), (0x26d4, 0x26d4), (0x26ea, 0x26ea), (0x26f2, 0x26f3), (0x26f5, 0x26f5),
        (0x26fa, 0x26fa), (0x26fd, 0x26fd), (0x2705, 0x2705), (0x270a, 0x270b), (0x2728, 0x2728),
        (0x274c, 0x274c), (0x274e, 0x274e), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27b0, 0x27b0), (0x27bf, 0x27bf), (0x2b1b, 0x2b1c), (0x2b50, 0x2b50), (0x2b55, 0x2b55),
        (0x2e80, 0x2e99), (0x2e9b, 0x2ef3), (0x2f00, 0x2fd5), (0x2ff0, 0x2ffb), (0x3000, 0x303e),
        (0x3041, 0x3096), (0x3099, 0x30ff), (0x3105, 0x312f), (0x3131, 0x318e), (0x3190, 0x31e3),
        (0x31f0, 0x321e), (0x3220, 0x3247), (0x3250, 0x4dbf), (0x4e00, 0xa48c), (0xa490, 0xa4c6),
        (0xa960, 0xa97c), (0xac00, 0xd7a3), (0xf900, 0xfaff), (0xfe10, 0xfe19), (0xfe30, 0xfe52),
        (0xfe54, 0xfe66), (0xfe68, 0xfe6b), (0xff01, 0xff60), (0xffe0, 0xffe6), (0x16fe0, 0x16fe4),
        (0x16ff0, 0x16ff1), (0x17000, 0x187f7), (0x18800, 0x18cd5), (0x1b000, 0x1b122),
        (0x1b150, 0x1b152), (0x1b164, 0x1b167), (0x1b170, 0x1b2fb), (0x1f004, 0x1f004),
        (0x1f0cf, 0x1f0cf), (0x1f18e, 0x1f18e), (0x1f191, 0x1f19a), (0x1f200, 0x1f202),
        (0x1f210, 0x1f23b), (0x1f240, 0x1f248), (0x1f250, 0x1f251), (0x1f260, 0x1f265),
        (0x1f300, 0x1f320), (0x1f32d, 0x1f335), (0x1f337, 0x1f37c), (0x1f37e, 0x1f393),
        (0x1f3a0, 0x1f3ca), (0x1f3cf, 0x1f3d3), (0x1f3e0, 0x1f3f0), (0x1f3f4, 0x1f3f4),
        (0x1f3f8, 0x1f43e), (0x1f440, 0x1f440), (0x1f442, 0x1f4fc), (0x1f4ff, 0x1f53d),
        (0x1f54b, 0x1f54e), (0x1f550, 0x1f567), (0x1f57a, 0x1f57a), (0x1f595, 0x1f596),
        (0x1f5a4, 0x1f5a4), (0x1f5fb, 0x1f64f), (0x1f680, 0x1f6c5), (0x1f6cc, 0x1f6cc),
        (0x1f6d0, 0x1f6d2), (0x1f6d5, 0x1f6d7), (0x1f6dc, 0x1f6df), (0x1f6eb, 0x1f6ec),
        (0x1f6f4, 0x1f6fc), (0x1f7e0, 0x1f7eb), (0x1f7f0, 0x1f7f0), (0x1f90c, 0x1f93a),
        (0x1f93c, 0x1f945), (0x1f947, 0x1f9ff), (0x1fa70, 0x1fa7c), (0x1fa80, 0x1fa88),
        (0x1fa90, 0x1fabd), (0x1fabf, 0x1fac5), (0x1face, 0x1fadb), (0x1fae0, 0x1fae8),
        (0x1faf0, 0x1faf8), (0x20000, 0x2fffd), (0x30000, 0x3fffd),
    };

    private static readonly (int Start, int End)[] EmojiPresentationRanges =
    {
        (0x231a, 0x231b), (0x23e9, 0x23ec), (0x23f0, 0x23f0), (0x23f3, 0x23f3), (0x25fd, 0x25fe),
        (0x2614, 0x2615), (0x2648, 0x2653), (0x267f, 0x267f), (0x2693, 0x2693), (0x26a1, 0x26a1),
        (0x26aa, 0x26ab), (0x26bd, 0x26be), (0x26c4, 0x26c5), (0x26ce, 0x26ce), (0x26d4, 0x26d4),
        (0x26ea, 0x26ea), (0x26f2, 0x26f3), (0x26f5, 0x26f5), (0x26fa, 0x26fa), (0x26fd, 0x26fd),
        (0x2705, 0x2705), (0x270a, 0x270b), (0x2728, 0x2728), (0x274c, 0x274c), (0x274e, 0x274e),
        (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27b0, 0x27b0), (0x27bf, 0x27bf),
        (0x2b1b, 0x2b1c), (0x2b50, 0x2b50), (0x2b55, 0x2b55), (0x1f004, 0x1f004), (0x1f0cf, 0x1f0cf),
        (0x1f18e, 0x1f18e), (0x1f191, 0x1f19a), (0x1f1e6, 0x1f1ff), (0x1f201, 0x1f201),
        (0x1f21a, 0x1f21a), (0x1f22f, 0x1f22f), (0x1f232, 0x1f236), (0x1f238, 0x1f23a),
        (0x1f250, 0x1f251), (0x1f300, 0x1f320), (0x1f32d, 0x1f335), (0x1f337, 0x1f37c),
        (0x1f37e, 0x1f393), (0x1f3a0, 0x1f3ca), (0x1f3cf, 0x1f3d3), (0x1f3e0, 0x1f3f0),
        (0x1f3f4, 0x1f3f4), (0x1f3f8, 0x1f43e), (0x1f440, 0x1f440), (0x1f442, 0x1f4fc),
        (0x1f4ff, 0x1f53d), (0x1f54b, 0x1f54e), (0x1f550, 0x1f567), (0x1f57a, 0x1f57a),
        (0x1f595, 0x1f596), (0x1f5a4, 0x1f5a4), (0x1f5fb, 0x1f64f), (0x1f680, 0x1f6c5),
        (0x1f6cc, 0x1f6cc), (0x1f6d0, 0x1f6d2), (0x1f6d5, 0x1f6d7), (0x1f6dc, 0x1f6df),
        (0x1f6eb, 0x1f6ec), (0x1f6f4, 0x1f6fc), (0x1f7e0, 0x1f7eb), (0x1f7f0, 0x1f7f0),
        (0x1f90c, 0x1f93a), (0x1f93c, 0x1f945), (0x1f947, 0x1f9ff), (0x1fa70, 0x1fa7c),
        (0x1fa80, 0x1fa88), (0x1fa90, 0x1fabd), (0x1fabf, 0x1fac5), (0x1face, 0x1fadb),
        (0x1fae0, 0x1fae8), (0x1faf0, 0x1faf8),
    };

    /// <summary>Terminal cell width from East Asian Width: 2 for wide/fullwidth, else 1.</summary>
    public static int EastAsianWidth(int cp) => InRanges(cp, WideRanges) ? 2 : 1;

    private static bool InRanges(int cp, (int Start, int End)[] ranges)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var (start, end) = ranges[mid];
            if (cp < start)
            {
                hi = mid - 1;
            }
            else if (cp > end)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
}
