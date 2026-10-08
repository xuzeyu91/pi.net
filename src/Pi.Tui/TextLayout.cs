using System.Text;

namespace Pi.Tui;

/// <summary>
/// Port of <c>utils.ts</c>'s text layout: ANSI-aware wrapping, truncation, column slicing and
/// overlay segment extraction. All functions preserve escape sequences and count only visible cells.
/// </summary>
public static class TextLayout
{
    private const string Esc = "\x1b";
    private const string Reset = "\x1b[0m";

    private static readonly HashSet<char> SymbolChars = new("`-=[]\\;',./!@#$%^&*()_+|~{}:<>?".ToCharArray());

    private static readonly HashSet<char> PunctuationChars =
        new("(){}[]<>.,;:'\"!?+-=*/\\|&%^$#@~`".ToCharArray());

    /// <summary>Split text into word/whitespace/CJK tokens, keeping ANSI codes attached to visible content.</summary>
    private static List<string> SplitIntoTokensWithAnsi(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        string? currentKind = null;
        var i = 0;

        void FlushCurrent()
        {
            if (current.Length == 0)
            {
                return;
            }
            tokens.Add(current.ToString());
            current.Clear();
            currentKind = null;
        }

        while (i < text.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(text, i);
            if (ansi is { } code)
            {
                pendingAnsi.Append(code.Code);
                i += code.Length;
                continue;
            }

            var end = i;
            while (end < text.Length && Ansi.ExtractAnsiCode(text, end) is null)
            {
                end++;
            }

            foreach (var segment in UnicodeWidth.Graphemes(text.Substring(i, end - i)))
            {
                var isSpace = segment == " ";
                if (!isSpace && IsCjkBreakSegment(segment))
                {
                    FlushCurrent();
                    tokens.Add(pendingAnsi + segment);
                    pendingAnsi.Clear();
                    continue;
                }

                var kind = isSpace ? "space" : "word";
                if (current.Length > 0 && currentKind != kind)
                {
                    FlushCurrent();
                }

                if (pendingAnsi.Length > 0)
                {
                    current.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }

                currentKind = kind;
                current.Append(segment);
            }

            i = end;
        }

        if (pendingAnsi.Length > 0)
        {
            if (current.Length > 0)
            {
                current.Append(pendingAnsi);
            }
            else if (tokens.Count > 0)
            {
                tokens[^1] += pendingAnsi.ToString();
            }
            else
            {
                current.Append(pendingAnsi);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Wrap text with ANSI codes preserved. Only word-wraps; does not pad.</summary>
    public static List<string> WrapTextWithAnsi(string text, int width)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new List<string> { "" };
        }

        var inputLines = SplitLines(text);
        var result = new List<string>();
        var tracker = new AnsiCodeTracker();

        foreach (var inputLine in inputLines)
        {
            var prefix = result.Count > 0 ? tracker.GetActiveCodes() : "";
            foreach (var wrappedLine in WrapSingleLine(prefix + inputLine, width))
            {
                result.Add(wrappedLine);
            }
            UpdateTrackerFromText(inputLine, tracker);
        }

        return result.Count > 0 ? result : new List<string> { "" };
    }

    private static List<string> WrapSingleLine(string line, int width)
    {
        if (string.IsNullOrEmpty(line))
        {
            return new List<string> { "" };
        }

        var visibleLength = UnicodeWidth.VisibleWidth(line);
        if (visibleLength <= width)
        {
            return new List<string> { line };
        }

        var wrapped = new List<string>();
        var tracker = new AnsiCodeTracker();
        var tokens = SplitIntoTokensWithAnsi(line);

        var currentLine = "";
        var currentVisibleLength = 0;

        foreach (var token in tokens)
        {
            var tokenVisibleLength = UnicodeWidth.VisibleWidth(token);
            var isWhitespace = token.Trim().Length == 0;

            if (tokenVisibleLength > width && !isWhitespace)
            {
                if (currentLine.Length > 0)
                {
                    var lineEndReset = tracker.GetLineEndReset();
                    if (lineEndReset.Length > 0)
                    {
                        currentLine += lineEndReset;
                    }
                    wrapped.Add(currentLine);
                    currentLine = "";
                    currentVisibleLength = 0;
                }

                var broken = BreakLongWord(token, width, tracker);
                for (var i = 0; i < broken.Count - 1; i++)
                {
                    wrapped.Add(broken[i]);
                }
                currentLine = broken[^1];
                currentVisibleLength = UnicodeWidth.VisibleWidth(currentLine);
                continue;
            }

            var totalNeeded = currentVisibleLength + tokenVisibleLength;

            if (totalNeeded > width && currentVisibleLength > 0)
            {
                var lineToWrap = currentLine.TrimEnd();
                var lineEndReset = tracker.GetLineEndReset();
                if (lineEndReset.Length > 0)
                {
                    lineToWrap += lineEndReset;
                }
                wrapped.Add(lineToWrap);
                if (isWhitespace)
                {
                    currentLine = tracker.GetActiveCodes();
                    currentVisibleLength = 0;
                }
                else
                {
                    currentLine = tracker.GetActiveCodes() + token;
                    currentVisibleLength = tokenVisibleLength;
                }
            }
            else
            {
                currentLine += token;
                currentVisibleLength += tokenVisibleLength;
            }

            UpdateTrackerFromText(token, tracker);
        }

        if (currentLine.Length > 0)
        {
            wrapped.Add(currentLine);
        }

        if (wrapped.Count == 0)
        {
            return new List<string> { "" };
        }
        for (var i = 0; i < wrapped.Count; i++)
        {
            wrapped[i] = wrapped[i].TrimEnd();
        }
        return wrapped;
    }

    private static List<string> BreakLongWord(string word, int width, AnsiCodeTracker tracker)
    {
        var lines = new List<string>();
        var currentLine = new StringBuilder(tracker.GetActiveCodes());
        var currentWidth = 0;

        var segments = new List<(bool IsAnsi, string Value)>();
        var i = 0;
        while (i < word.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(word, i);
            if (ansi is { } code)
            {
                segments.Add((true, code.Code));
                i += code.Length;
            }
            else
            {
                var end = i;
                while (end < word.Length && Ansi.ExtractAnsiCode(word, end) is null)
                {
                    end++;
                }
                foreach (var seg in UnicodeWidth.Graphemes(word.Substring(i, end - i)))
                {
                    segments.Add((false, seg));
                }
                i = end;
            }
        }

        foreach (var (isAnsi, value) in segments)
        {
            if (isAnsi)
            {
                currentLine.Append(value);
                tracker.Process(value);
                continue;
            }

            if (value.Length == 0)
            {
                continue;
            }

            var graphemeWidth = UnicodeWidth.VisibleWidth(value);
            if (currentWidth + graphemeWidth > width)
            {
                var lineEndReset = tracker.GetLineEndReset();
                if (lineEndReset.Length > 0)
                {
                    currentLine.Append(lineEndReset);
                }
                lines.Add(currentLine.ToString());
                currentLine.Clear();
                currentLine.Append(tracker.GetActiveCodes());
                currentWidth = 0;
            }

            currentLine.Append(value);
            currentWidth += graphemeWidth;
        }

        if (currentLine.Length > 0)
        {
            lines.Add(currentLine.ToString());
        }

        return lines.Count > 0 ? lines : new List<string> { "" };
    }

    /// <summary>Apply a background color to a line, padding it to the full width first.</summary>
    public static string ApplyBackgroundToLine(string line, int width, Func<string, string> bgFn)
    {
        var visibleLen = UnicodeWidth.VisibleWidth(line);
        var paddingNeeded = Math.Max(0, width - visibleLen);
        var withPadding = line + new string(' ', paddingNeeded);
        return bgFn(withPadding);
    }

    /// <summary>Truncate text to a maximum visible width, appending an ellipsis when needed.</summary>
    public static string TruncateToWidth(string text, int maxWidth, string ellipsis = "...", bool pad = false)
    {
        if (maxWidth <= 0)
        {
            return "";
        }
        if (text.Length == 0)
        {
            return pad ? new string(' ', maxWidth) : "";
        }

        var ellipsisWidth = UnicodeWidth.VisibleWidth(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            var textWidth = UnicodeWidth.VisibleWidth(text);
            if (textWidth <= maxWidth)
            {
                return pad ? text + new string(' ', maxWidth - textWidth) : text;
            }
            var clippedEllipsis = TruncateFragmentToWidth(ellipsis, maxWidth);
            if (clippedEllipsis.Width == 0)
            {
                return pad ? new string(' ', maxWidth) : "";
            }
            return FinalizeTruncatedResult("", 0, clippedEllipsis.Text, clippedEllipsis.Width, maxWidth, pad);
        }

        if (UnicodeWidth.IsPrintableAscii(text))
        {
            if (text.Length <= maxWidth)
            {
                return pad ? text + new string(' ', maxWidth - text.Length) : text;
            }
            var target = maxWidth - ellipsisWidth;
            return FinalizeTruncatedResult(text.Substring(0, target), target, ellipsis, ellipsisWidth, maxWidth, pad);
        }

        var targetWidth = maxWidth - ellipsisWidth;
        var result = new StringBuilder();
        var pendingAnsi = new StringBuilder();
        var visibleSoFar = 0;
        var keptWidth = 0;
        var keepContiguousPrefix = true;
        var overflowed = false;
        bool exhaustedInput;
        var hasAnsi = text.Contains('\x1b');
        var hasTabs = text.Contains('\t');

        if (!hasAnsi && !hasTabs)
        {
            foreach (var segment in UnicodeWidth.Graphemes(text))
            {
                var w = UnicodeWidth.GraphemeWidth(segment);
                if (keepContiguousPrefix && keptWidth + w <= targetWidth)
                {
                    result.Append(segment);
                    keptWidth += w;
                }
                else
                {
                    keepContiguousPrefix = false;
                }
                visibleSoFar += w;
                if (visibleSoFar > maxWidth)
                {
                    overflowed = true;
                    break;
                }
            }
            exhaustedInput = !overflowed;
        }
        else
        {
            var i = 0;
            while (i < text.Length)
            {
                var ansi = Ansi.ExtractAnsiCode(text, i);
                if (ansi is { } code)
                {
                    pendingAnsi.Append(code.Code);
                    i += code.Length;
                    continue;
                }

                if (text[i] == '\t')
                {
                    if (keepContiguousPrefix && keptWidth + 3 <= targetWidth)
                    {
                        if (pendingAnsi.Length > 0)
                        {
                            result.Append(pendingAnsi);
                            pendingAnsi.Clear();
                        }
                        result.Append('\t');
                        keptWidth += 3;
                    }
                    else
                    {
                        keepContiguousPrefix = false;
                        pendingAnsi.Clear();
                    }
                    visibleSoFar += 3;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }
                    i++;
                    continue;
                }

                var end = i;
                while (end < text.Length && text[end] != '\t' && Ansi.ExtractAnsiCode(text, end) is null)
                {
                    end++;
                }

                foreach (var segment in UnicodeWidth.Graphemes(text.Substring(i, end - i)))
                {
                    var w = UnicodeWidth.GraphemeWidth(segment);
                    if (keepContiguousPrefix && keptWidth + w <= targetWidth)
                    {
                        if (pendingAnsi.Length > 0)
                        {
                            result.Append(pendingAnsi);
                            pendingAnsi.Clear();
                        }
                        result.Append(segment);
                        keptWidth += w;
                    }
                    else
                    {
                        keepContiguousPrefix = false;
                        pendingAnsi.Clear();
                    }

                    visibleSoFar += w;
                    if (visibleSoFar > maxWidth)
                    {
                        overflowed = true;
                        break;
                    }
                }
                if (overflowed)
                {
                    break;
                }
                i = end;
            }
            exhaustedInput = i >= text.Length;
        }

        if (!overflowed && exhaustedInput)
        {
            return pad ? text + new string(' ', Math.Max(0, maxWidth - visibleSoFar)) : text;
        }

        return FinalizeTruncatedResult(result.ToString(), keptWidth, ellipsis, ellipsisWidth, maxWidth, pad);
    }

    private static (string Text, int Width) TruncateFragmentToWidth(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length == 0)
        {
            return ("", 0);
        }

        if (UnicodeWidth.IsPrintableAscii(text))
        {
            var clipped = text.Substring(0, Math.Min(maxWidth, text.Length));
            return (clipped, clipped.Length);
        }

        var hasAnsi = text.Contains('\x1b');
        var hasTabs = text.Contains('\t');
        if (!hasAnsi && !hasTabs)
        {
            var sb = new StringBuilder();
            var w = 0;
            foreach (var segment in UnicodeWidth.Graphemes(text))
            {
                var gw = UnicodeWidth.GraphemeWidth(segment);
                if (w + gw > maxWidth)
                {
                    break;
                }
                sb.Append(segment);
                w += gw;
            }
            return (sb.ToString(), w);
        }

        var result = new StringBuilder();
        var width = 0;
        var i = 0;
        var pendingAnsi = new StringBuilder();

        while (i < text.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(text, i);
            if (ansi is { } code)
            {
                pendingAnsi.Append(code.Code);
                i += code.Length;
                continue;
            }

            if (text[i] == '\t')
            {
                if (width + 3 > maxWidth)
                {
                    break;
                }
                if (pendingAnsi.Length > 0)
                {
                    result.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }
                result.Append('\t');
                width += 3;
                i++;
                continue;
            }

            var end = i;
            while (end < text.Length && text[end] != '\t' && Ansi.ExtractAnsiCode(text, end) is null)
            {
                end++;
            }

            foreach (var segment in UnicodeWidth.Graphemes(text.Substring(i, end - i)))
            {
                var gw = UnicodeWidth.GraphemeWidth(segment);
                if (width + gw > maxWidth)
                {
                    return (result.ToString(), width);
                }
                if (pendingAnsi.Length > 0)
                {
                    result.Append(pendingAnsi);
                    pendingAnsi.Clear();
                }
                result.Append(segment);
                width += gw;
            }
            i = end;
        }

        return (result.ToString(), width);
    }

    private static string FinalizeTruncatedResult(
        string prefix, int prefixWidth, string ellipsis, int ellipsisWidth, int maxWidth, bool pad)
    {
        var hyperlinkClose = GetActiveOsc8Close(prefix);
        var visibleWidth = prefixWidth + ellipsisWidth;
        string result;
        if (ellipsis.Length > 0)
        {
            result = $"{prefix}{hyperlinkClose}{Reset}{ellipsis}{Reset}";
        }
        else
        {
            result = $"{prefix}{hyperlinkClose}{Reset}";
        }
        return pad ? result + new string(' ', Math.Max(0, maxWidth - visibleWidth)) : result;
    }

    /// <summary>Extract a range of visible columns from a line, handling ANSI codes and wide chars.</summary>
    public static string SliceByColumn(string line, int startCol, int length, bool strict = false) =>
        SliceWithWidth(line, startCol, length, strict).Text;

    /// <summary>Like <see cref="SliceByColumn"/> but also returns the visible width of the result.</summary>
    public static (string Text, int Width) SliceWithWidth(string line, int startCol, int length, bool strict = false)
    {
        if (length <= 0)
        {
            return ("", 0);
        }
        var endCol = startCol + length;
        var result = new StringBuilder();
        var resultWidth = 0;
        var currentCol = 0;
        var i = 0;
        var pendingAnsi = new StringBuilder();

        while (i < line.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(line, i);
            if (ansi is { } code)
            {
                if (currentCol >= startCol && currentCol < endCol)
                {
                    result.Append(code.Code);
                }
                else if (currentCol < startCol)
                {
                    pendingAnsi.Append(code.Code);
                }
                i += code.Length;
                continue;
            }

            var textEnd = i;
            while (textEnd < line.Length && Ansi.ExtractAnsiCode(line, textEnd) is null)
            {
                textEnd++;
            }

            foreach (var segment in UnicodeWidth.Graphemes(line.Substring(i, textEnd - i)))
            {
                var w = UnicodeWidth.GraphemeWidth(segment);
                var inRange = currentCol >= startCol && currentCol < endCol;
                var fits = !strict || currentCol + w <= endCol;
                if (inRange && fits)
                {
                    if (pendingAnsi.Length > 0)
                    {
                        result.Append(pendingAnsi);
                        pendingAnsi.Clear();
                    }
                    result.Append(segment);
                    resultWidth += w;
                }
                currentCol += w;
                if (currentCol >= endCol)
                {
                    break;
                }
            }
            i = textEnd;
            if (currentCol >= endCol)
            {
                break;
            }
        }
        return (result.ToString(), resultWidth);
    }

    /// <summary>
    /// Extract "before" and "after" segments around an overlay region in a single pass, carrying
    /// styling from before the overlay into the "after" segment.
    /// </summary>
    public static (string Before, int BeforeWidth, string After, int AfterWidth) ExtractSegments(
        string line, int beforeEnd, int afterStart, int afterLen, bool strictAfter = false)
    {
        var before = new StringBuilder();
        var beforeWidth = 0;
        var after = new StringBuilder();
        var afterWidth = 0;
        var currentCol = 0;
        var i = 0;
        var pendingAnsiBefore = new StringBuilder();
        var afterStarted = false;
        var afterEnd = afterStart + afterLen;
        var tracker = new AnsiCodeTracker();

        while (i < line.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(line, i);
            if (ansi is { } code)
            {
                tracker.Process(code.Code);
                if (currentCol < beforeEnd)
                {
                    pendingAnsiBefore.Append(code.Code);
                }
                else if (currentCol >= afterStart && currentCol < afterEnd && afterStarted)
                {
                    after.Append(code.Code);
                }
                i += code.Length;
                continue;
            }

            var textEnd = i;
            while (textEnd < line.Length && Ansi.ExtractAnsiCode(line, textEnd) is null)
            {
                textEnd++;
            }

            foreach (var segment in UnicodeWidth.Graphemes(line.Substring(i, textEnd - i)))
            {
                var w = UnicodeWidth.GraphemeWidth(segment);

                if (currentCol < beforeEnd && currentCol + w <= beforeEnd)
                {
                    if (pendingAnsiBefore.Length > 0)
                    {
                        before.Append(pendingAnsiBefore);
                        pendingAnsiBefore.Clear();
                    }
                    before.Append(segment);
                    beforeWidth += w;
                }
                else if (currentCol >= afterStart && currentCol < afterEnd)
                {
                    var fits = !strictAfter || currentCol + w <= afterEnd;
                    if (fits)
                    {
                        if (!afterStarted)
                        {
                            after.Append(tracker.GetActiveCodes());
                            afterStarted = true;
                        }
                        after.Append(segment);
                        afterWidth += w;
                    }
                }

                currentCol += w;
                if (afterLen <= 0 ? currentCol >= beforeEnd : currentCol >= afterEnd)
                {
                    break;
                }
            }
            i = textEnd;
            if (afterLen <= 0 ? currentCol >= beforeEnd : currentCol >= afterEnd)
            {
                break;
            }
        }

        return (before.ToString(), beforeWidth, after.ToString(), afterWidth);
    }

    private static string GetActiveOsc8Close(string prefix)
    {
        if (!prefix.Contains("\x1b]8;"))
        {
            return "";
        }

        ActiveHyperlink? active = null;
        var i = 0;
        while (i < prefix.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(prefix, i);
            if (ansi is { } code)
            {
                var parse = AnsiCodeTracker.ParseOsc8Hyperlink(code.Code);
                if (parse.IsOsc8)
                {
                    active = parse.Hyperlink;
                }
                i += code.Length;
            }
            else
            {
                i++;
            }
        }
        return active is { } link ? $"{Esc}]8;;{link.Terminator}" : "";
    }

    private static void UpdateTrackerFromText(string text, AnsiCodeTracker tracker)
    {
        var i = 0;
        while (i < text.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(text, i);
            if (ansi is { } code)
            {
                tracker.Process(code.Code);
                i += code.Length;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// Check if a grapheme is a CJK break opportunity (Han/Hiragana/Katakana/Hangul/Bopomofo).
    /// Delegates to the exact probed table in <see cref="JsCjk"/>; an earlier hand-written range
    /// approximation missed Script_Extensions code points such as U+00B7 and U+3001.
    /// </summary>
    private static bool IsCjkBreakSegment(string segment) => JsCjk.IsCjkBreak(segment);

    /// <summary>
    /// JS <c>/\s/.test(ch)</c>. The regex is unanchored, so a multi-character argument only needs one
    /// match. The JavaScript whitespace set is deliberately not <see cref="char.IsWhiteSpace(char)"/>:
    /// JS includes U+FEFF and excludes U+0085 (see T15 in <c>docs/tui-porting-status.md</c>).
    /// </summary>
    public static bool IsWhitespaceChar(string ch)
    {
        foreach (var c in ch)
        {
            if (JsString.IsWhitespace(c))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsPunctuationChar(string ch) =>
        ch.Length == 1 && PunctuationChars.Contains(ch[0]);

    /// <summary>Split on CRLF / CR / LF, matching JS <c>String.split(/\r\n|\r|\n/)</c>.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\r')
            {
                lines.Add(text.Substring(start, i - start));
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                start = i;
            }
            else if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                i++;
                start = i;
            }
            else
            {
                i++;
            }
        }
        lines.Add(text.Substring(start));
        return lines;
    }
}
