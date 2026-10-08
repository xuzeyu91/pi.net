using System.Globalization;
using System.Text;

namespace Pi.Tui;

/// <summary>A word segment: the text plus whether it is word-like (Intl.Segmenter's <c>isWordLike</c>).</summary>
public readonly record struct WordSegment(string Segment, bool IsWordLike);

/// <summary>Options for word navigation (port of <c>word-navigation.ts</c>'s options).</summary>
public sealed class WordNavigationOptions
{
    /// <summary>Custom segmenter returning word segments for the given text.</summary>
    public Func<string, IEnumerable<WordSegment>>? Segment { get; set; }

    /// <summary>Predicate identifying atomic segments treated as single units (e.g. paste markers).</summary>
    public Func<string, bool>? IsAtomicSegment { get; set; }
}

/// <summary>
/// Port of <c>word-navigation.ts</c>: pure cursor movement over word/punctuation boundaries.
///
/// Deviation from TS: <c>Intl.Segmenter</c> word granularity is approximated by grouping consecutive
/// runes of the same class (whitespace / word-like / other). Word-like covers Unicode letters,
/// digits and combining marks, so CJK ideographs count as words just as they do in Intl.Segmenter.
/// </summary>
public static class WordNavigation
{
    public static IEnumerable<WordSegment> SegmentWords(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var sb = new StringBuilder();
        int? currentClass = null;

        int Classify(int cp)
        {
            if (JsString.IsWhitespace((char)cp))
            {
                return 0;
            }
            return IsWordLike(cp) ? 1 : 2;
        }

        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var segment = (string)enumerator.Current;
            var cp = segment.EnumerateRunes().First().Value;
            var cls = Classify(cp);
            if (currentClass is not null && cls != currentClass)
            {
                yield return new WordSegment(sb.ToString(), currentClass == 1);
                sb.Clear();
            }
            currentClass = cls;
            sb.Append(segment);
        }

        if (sb.Length > 0)
        {
            yield return new WordSegment(sb.ToString(), currentClass == 1);
        }
    }

    private static bool IsWordLike(int cp)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        return cat is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    /// <summary>Find the cursor position after moving one word backward from <paramref name="cursor"/>.</summary>
    public static int FindWordBackward(string text, int cursor, WordNavigationOptions? options = null)
    {
        if (cursor <= 0)
        {
            return 0;
        }

        var textBeforeCursor = text.Substring(0, cursor);
        var segments = (options?.Segment is { } seg ? seg(textBeforeCursor) : SegmentWords(textBeforeCursor)).ToList();
        var isAtomic = options?.IsAtomicSegment;
        var newCursor = cursor;

        bool Atomic(WordSegment s) => isAtomic?.Invoke(s.Segment) ?? false;

        while (segments.Count > 0 && !Atomic(segments[^1]) && IsWhitespace(segments[^1].Segment))
        {
            newCursor -= segments[^1].Segment.Length;
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0)
        {
            return newCursor;
        }

        var last = segments[^1];
        if (Atomic(last))
        {
            newCursor -= last.Segment.Length;
        }
        else if (last.IsWordLike)
        {
            var segment = last.Segment;
            var lastPunct = -1;
            var lastPunctLength = 0;
            for (var i = 0; i < segment.Length; i++)
            {
                if (TextLayout.IsPunctuationChar(segment[i].ToString()))
                {
                    lastPunct = i;
                    lastPunctLength = 1;
                }
            }
            if (lastPunct < 0)
            {
                newCursor -= segment.Length;
            }
            else
            {
                newCursor -= segment.Length - (lastPunct + lastPunctLength);
            }
        }
        else
        {
            while (segments.Count > 0 && !Atomic(segments[^1]) && !segments[^1].IsWordLike && !IsWhitespace(segments[^1].Segment))
            {
                newCursor -= segments[^1].Segment.Length;
                segments.RemoveAt(segments.Count - 1);
            }
        }

        return newCursor;
    }

    /// <summary>Find the cursor position after moving one word forward from <paramref name="cursor"/>.</summary>
    public static int FindWordForward(string text, int cursor, WordNavigationOptions? options = null)
    {
        if (cursor >= text.Length)
        {
            return text.Length;
        }

        var textAfterCursor = text.Substring(cursor);
        var segments = (options?.Segment is { } seg ? seg(textAfterCursor) : SegmentWords(textAfterCursor)).ToList();
        var isAtomic = options?.IsAtomicSegment;
        var newCursor = cursor;
        var index = 0;

        bool Atomic(WordSegment s) => isAtomic?.Invoke(s.Segment) ?? false;

        while (index < segments.Count && !Atomic(segments[index]) && IsWhitespace(segments[index].Segment))
        {
            newCursor += segments[index].Segment.Length;
            index++;
        }

        if (index >= segments.Count)
        {
            return newCursor;
        }

        var next = segments[index];
        if (Atomic(next))
        {
            newCursor += next.Segment.Length;
        }
        else if (next.IsWordLike)
        {
            var punctIndex = -1;
            for (var i = 0; i < next.Segment.Length; i++)
            {
                if (TextLayout.IsPunctuationChar(next.Segment[i].ToString()))
                {
                    punctIndex = i;
                    break;
                }
            }
            newCursor += punctIndex >= 0 ? punctIndex : next.Segment.Length;
        }
        else
        {
            while (index < segments.Count && !Atomic(segments[index]) && !segments[index].IsWordLike && !IsWhitespace(segments[index].Segment))
            {
                newCursor += segments[index].Segment.Length;
                index++;
            }
        }

        return newCursor;
    }

    private static bool IsWhitespace(string s) => TextLayout.IsWhitespaceChar(s);
}
