using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pi.Tui.Components;

namespace Pi.Tui;

/// <summary>A source span mapping a run of corpus text back to a screen cell range (port of <c>SearchSourceSpan</c>).</summary>
internal sealed class SearchSourceSpan
{
    public int TextStart { get; set; }

    public int TextEnd { get; set; }

    public int Row { get; set; }

    public int StartCol { get; set; }

    public int EndCol { get; set; }

    public bool LinearColumns { get; set; }
}

/// <summary>The flattened, searchable text of a rendered transcript plus its cell-range spans (port of <c>SearchCorpus</c>).</summary>
internal sealed class SearchCorpus
{
    public string Text { get; set; } = "";

    public List<SearchSourceSpan> Spans { get; set; } = [];
}

/// <summary>A contiguous highlighted cell range on one row (port of <c>AltScreenSearchSegment</c>).</summary>
public sealed class AltScreenSearchSegment
{
    public int Row { get; set; }

    public int StartCol { get; set; }

    public int EndCol { get; set; }
}

/// <summary>One search match, expressed as one or more cell ranges (port of <c>AltScreenSearchMatch</c>).</summary>
public sealed class AltScreenSearchMatch
{
    public List<AltScreenSearchSegment> Segments { get; set; } = [];
}

/// <summary>Result of <see cref="AltScreenSearchIndex.Search"/> (port of <c>AltScreenSearchResult</c>).</summary>
public sealed class AltScreenSearchResult
{
    public List<AltScreenSearchMatch> Matches { get; init; } = [];

    public bool Changed { get; init; }
}

/// <summary>
/// Pure helpers backing the alternate-screen transcript search (port of the module-private functions in
/// <c>alt-screen-search.ts</c>).
/// </summary>
internal static class AltScreenSearchCore
{
    private static readonly Regex PrintableAscii = new(@"^[\x20-\x7e]*$", RegexOptions.Compiled);

    private static readonly Regex EscapeChars = new("[.*+?^${}()|\\[\\]\\\\]", RegexOptions.Compiled);

    /// <summary>Port of <c>normalizeQuery</c>: collapse JS-whitespace runs to a single space and JS-trim.</summary>
    public static string NormalizeQuery(string query)
    {
        var sb = new StringBuilder();
        var started = false;
        var pendingSpace = false;
        foreach (var c in query)
        {
            if (JsString.IsWhitespace(c))
            {
                if (started)
                {
                    pendingSpace = true;
                }
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(c);
                started = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Port of <c>escapeRegExp</c>: backslash-escape the JS regex metacharacters.</summary>
    public static string EscapeRegExp(string text) => EscapeChars.Replace(text, m => "\\" + m.Value);

    /// <summary>Port of <c>buildSearchCorpus</c>.</summary>
    public static SearchCorpus BuildSearchCorpus(IReadOnlyList<string> lines)
    {
        var chunks = new List<string>();
        var spans = new List<SearchSourceSpan>();
        var textLength = 0;
        var pendingSeparator = false;

        void AppendSeparator()
        {
            if (!pendingSeparator)
            {
                return;
            }
            chunks.Add(" ");
            textLength += 1;
            pendingSeparator = false;
        }

        for (var row = 0; row < lines.Count; row++)
        {
            var line = Ansi.StripTerminalSequences(lines[row] ?? "");
            var column = 0;

            if (PrintableAscii.IsMatch(line))
            {
                var index = 0;
                while (index < line.Length)
                {
                    if (line[index] == '\x20')
                    {
                        if (textLength > 0)
                        {
                            pendingSeparator = true;
                        }
                        column += 1;
                        index += 1;
                        continue;
                    }
                    var end = index + 1;
                    while (end < line.Length && line[end] != '\x20')
                    {
                        end += 1;
                    }
                    AppendSeparator();
                    var text = JsString.Slice(line, index, end);
                    chunks.Add(text);
                    spans.Add(new SearchSourceSpan
                    {
                        TextStart = textLength,
                        TextEnd = textLength + text.Length,
                        Row = row,
                        StartCol = column,
                        EndCol = column + text.Length,
                        LinearColumns = true,
                    });
                    textLength += text.Length;
                    column += text.Length;
                    index = end;
                }
            }
            else
            {
                foreach (var grapheme in SegmentGraphemes(line))
                {
                    var text = grapheme;
                    var width = UnicodeWidth.VisibleWidth(text);
                    if (text.Length > 0 && text.All(JsString.IsWhitespace))
                    {
                        if (textLength > 0)
                        {
                            pendingSeparator = true;
                        }
                        column += width;
                        continue;
                    }
                    AppendSeparator();
                    chunks.Add(text);
                    spans.Add(new SearchSourceSpan
                    {
                        TextStart = textLength,
                        TextEnd = textLength + text.Length,
                        Row = row,
                        StartCol = column,
                        EndCol = column + width,
                        LinearColumns = false,
                    });
                    textLength += text.Length;
                    column += width;
                }
            }
            if (textLength > 0)
            {
                pendingSeparator = true;
            }
        }

        return new SearchCorpus { Text = string.Concat(chunks), Spans = spans };
    }

    /// <summary>Port of <c>findSearchCorpusMatches</c>.</summary>
    public static List<AltScreenSearchMatch> FindSearchCorpusMatches(SearchCorpus corpus, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0)
        {
            return [];
        }
        var expression = new Regex(EscapeRegExp(normalizedQuery), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var matches = new List<AltScreenSearchMatch>();
        var spanIndex = 0;

        foreach (Match match in expression.Matches(corpus.Text))
        {
            var start = match.Index;
            var end = start + match.Length;
            while (spanIndex < corpus.Spans.Count && corpus.Spans[spanIndex].TextEnd <= start)
            {
                spanIndex += 1;
            }

            var segments = new List<AltScreenSearchSegment>();
            for (var index = spanIndex; index < corpus.Spans.Count; index++)
            {
                var span = corpus.Spans[index];
                if (span.TextStart >= end)
                {
                    break;
                }
                if (span.TextEnd <= start)
                {
                    continue;
                }
                var startCol = span.LinearColumns
                    ? span.StartCol + Math.Max(start, span.TextStart) - span.TextStart
                    : span.StartCol;
                var endCol = span.LinearColumns ? span.StartCol + Math.Min(end, span.TextEnd) - span.TextStart : span.EndCol;
                var previous = segments.Count > 0 ? segments[^1] : null;
                if (previous is not null && previous.Row == span.Row && startCol <= previous.EndCol)
                {
                    previous.EndCol = Math.Max(previous.EndCol, endCol);
                }
                else
                {
                    segments.Add(new AltScreenSearchSegment { Row = span.Row, StartCol = startCol, EndCol = endCol });
                }
            }
            while (spanIndex < corpus.Spans.Count && corpus.Spans[spanIndex].TextEnd <= end)
            {
                spanIndex += 1;
            }
            if (segments.Count > 0)
            {
                matches.Add(new AltScreenSearchMatch { Segments = segments });
            }
        }

        return matches;
    }

    private static IEnumerable<string> SegmentGraphemes(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }
}

/// <summary>
/// Caches the searchable corpus and matches while the rendered transcript lines stay unchanged
/// (port of <c>AltScreenSearchIndex</c>).
/// </summary>
public sealed class AltScreenSearchIndex
{
    private string[]? _sourceLines;
    private SearchCorpus? _corpus;
    private string? _normalizedQuery;
    private List<AltScreenSearchMatch> _matches = [];

    /// <summary>Port of <c>AltScreenSearchIndex.search</c>.</summary>
    public AltScreenSearchResult Search(IReadOnlyList<string> lines, string query)
    {
        var sourceChanged = _sourceLines is null || _sourceLines.Length != lines.Count;
        if (!sourceChanged && _sourceLines is not null)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                if (_sourceLines[index] == lines[index])
                {
                    continue;
                }
                sourceChanged = true;
                break;
            }
        }
        if (sourceChanged || _corpus is null)
        {
            _sourceLines = [.. lines];
            _corpus = AltScreenSearchCore.BuildSearchCorpus(lines);
        }

        var normalizedQuery = AltScreenSearchCore.NormalizeQuery(query);
        var changed = sourceChanged || normalizedQuery != _normalizedQuery;
        if (changed)
        {
            _normalizedQuery = normalizedQuery;
            _matches = AltScreenSearchCore.FindSearchCorpusMatches(_corpus, normalizedQuery);
        }
        return new AltScreenSearchResult { Matches = _matches, Changed = changed };
    }

    /// <summary>Port of the module-level <c>findAltScreenSearchMatches</c>.</summary>
    public static List<AltScreenSearchMatch> FindMatches(IReadOnlyList<string> lines, string query)
    {
        var normalizedQuery = AltScreenSearchCore.NormalizeQuery(query);
        return normalizedQuery.Length != 0
            ? AltScreenSearchCore.FindSearchCorpusMatches(AltScreenSearchCore.BuildSearchCorpus(lines), normalizedQuery)
            : [];
    }

    /// <summary>Port of the module-level <c>getAltScreenSearchMatchKey</c>.</summary>
    public static string GetMatchKey(AltScreenSearchMatch match)
    {
        if (match.Segments.Count == 0)
        {
            return "";
        }
        var first = match.Segments[0];
        var last = match.Segments[^1];
        return $"{first.Row}:{first.StartCol}:{last.Row}:{last.EndCol}";
    }
}

/// <summary>
/// The transcript search input box with match counter and prev/next navigation buttons
/// (port of <c>AltScreenSearchComponent</c>).
/// </summary>
public sealed class AltScreenSearchComponent : IComponent, IFocusable
{
    private readonly Input _input = new(new InputOptions
    {
        Prompt = " ",
        Placeholder = "Find in transcript",
        PlaceholderStyle = text => $"\x1b[2m{text}\x1b[22m",
    });
    private readonly Action<string> _onQueryChange;
    private readonly Func<string, bool, string> _navigationButtonStyle;

    private int _resultCount;
    private int _resultIndex = -1;
    private int _previousButtonStart = -1;
    private int _previousButtonEnd = -1;
    private int _nextButtonStart = -1;
    private int _nextButtonEnd = -1;
    private int? _hoveredNavigationDirection;
    private bool _focused;

    public AltScreenSearchComponent(
        Action<string> onQueryChange,
        Func<string, bool, string>? navigationButtonStyle = null)
    {
        _onQueryChange = onQueryChange;
        _navigationButtonStyle = navigationButtonStyle ?? DefaultNavigationButtonStyle;
    }

    private static string DefaultNavigationButtonStyle(string text, bool hovered) => text;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    public void SetResult(int index, int count)
    {
        _resultIndex = index;
        _resultCount = count;
    }

    public int? GetNavigationDirectionAt(int row, int column)
    {
        if (row != 2)
        {
            return null;
        }
        if (column >= _previousButtonStart && column < _previousButtonEnd)
        {
            return -1;
        }
        if (column >= _nextButtonStart && column < _nextButtonEnd)
        {
            return 1;
        }
        return null;
    }

    public bool SetHoveredNavigationDirection(int? direction)
    {
        if (direction == _hoveredNavigationDirection)
        {
            return false;
        }
        _hoveredNavigationDirection = direction;
        return true;
    }

    public void HandleInput(string data)
    {
        var previous = _input.GetValue();
        _input.HandleInput(data);
        var query = _input.GetValue();
        if (query != previous)
        {
            _onQueryChange(query);
        }
    }

    public void Invalidate() => _input.Invalidate();

    public string[] Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var innerWidth = Math.Max(0, safeWidth - 2);

        string FormatKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "Unbound";
            }
            var parts = key.Split("+");
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (OperatingSystem.IsMacOS() && JsString.ToLowerCase(part) == "alt")
                {
                    parts[i] = "Option";
                }
                else
                {
                    parts[i] = part.Length > 0 ? JsString.ToUpperCase(part[..1]) + part[1..] : part;
                }
            }
            return string.Join("+", parts);
        }

        var keybindings = GlobalKeybindings.Get();
        var previousKey = FormatKey(keybindings.GetKeys(TuiKeybindingIds.AltScreenSearchPrevious).FirstOrDefault());
        var nextKey = FormatKey(keybindings.GetKeys(TuiKeybindingIds.AltScreenSearchNext).FirstOrDefault());
        var query = _input.GetValue();
        var result = query.Length == 0
            ? ""
            : _resultCount == 0
                ? "No matches"
                : $"{_resultIndex + 1}/{_resultCount}";
        var resultSpace = Math.Max(0, innerWidth - 3);
        var visibleResult = TextLayout.TruncateToWidth(result, resultSpace, "");
        var resultText = visibleResult.Length != 0 ? $"\x1b[2m {visibleResult} \x1b[22m" : "";
        var inputWidth = Math.Max(0, innerWidth - UnicodeWidth.VisibleWidth(resultText));
        var inputLine = TextLayout.TruncateToWidth(_input.Render(Math.Max(1, inputWidth)).FirstOrDefault() ?? "", inputWidth, "");
        var inputPadding = new string(' ', Math.Max(0, inputWidth - UnicodeWidth.VisibleWidth(inputLine)));
        var content = $"{inputLine}{inputPadding}{resultText}";

        var previousButton = $"↑ {previousKey}";
        var nextButton = $"↓ {nextKey}";
        var separator = " · ";
        const int outerGapWidth = 1;
        var availableControlsWidth = Math.Max(0, innerWidth - (outerGapWidth * 2) - 1);
        var controlsWidth = UnicodeWidth.VisibleWidth(previousButton) + UnicodeWidth.VisibleWidth(separator) + UnicodeWidth.VisibleWidth(nextButton);
        if (controlsWidth > availableControlsWidth)
        {
            previousButton = "↑";
            nextButton = "↓";
            separator = " ";
            controlsWidth = UnicodeWidth.VisibleWidth(previousButton) + UnicodeWidth.VisibleWidth(separator) + UnicodeWidth.VisibleWidth(nextButton);
        }
        var showButtons = controlsWidth <= availableControlsWidth;
        var renderedButtons = showButtons
            ? _navigationButtonStyle(previousButton, _hoveredNavigationDirection == -1)
              + separator
              + _navigationButtonStyle(nextButton, _hoveredNavigationDirection == 1)
            : "";
        var outerGapsWidth = showButtons ? outerGapWidth * 2 : 0;
        var rightRuleWidth = renderedButtons.Length != 0 && innerWidth > controlsWidth + outerGapsWidth ? 1 : 0;
        var leftRuleWidth = Math.Max(
            0,
            innerWidth - (showButtons ? controlsWidth : 0) - outerGapsWidth - rightRuleWidth);
        var previousStart = 1 + leftRuleWidth + outerGapWidth;
        _previousButtonStart = showButtons ? previousStart : -1;
        _previousButtonEnd = showButtons ? previousStart + UnicodeWidth.VisibleWidth(previousButton) : -1;
        _nextButtonStart = showButtons ? _previousButtonEnd + UnicodeWidth.VisibleWidth(separator) : -1;
        _nextButtonEnd = showButtons ? _nextButtonStart + UnicodeWidth.VisibleWidth(nextButton) : -1;

        if (safeWidth == 1)
        {
            return ["┌", "│", "└"];
        }
        return
        [
            $"┌{new string('─', innerWidth)}┐",
            $"│{content}│",
            $"└{new string('─', leftRuleWidth)}{(renderedButtons.Length != 0 ? " " : "")}{renderedButtons}{(renderedButtons.Length != 0 ? " " : "")}{new string('─', rightRuleWidth)}┘",
        ];
    }
}
