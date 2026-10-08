using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui.Components;

/// <summary>A chunk of word-wrapped text plus its position in the original line (port of <c>TextChunk</c>).</summary>
public readonly record struct TextChunk(string Text, int StartIndex, int EndIndex);

/// <summary>Colour callbacks for the editor (port of <c>EditorTheme</c>).</summary>
public sealed class EditorTheme
{
    public required Func<string, string> BorderColor { get; init; }

    public required SelectListTheme SelectList { get; init; }
}

/// <summary>Construction options for <see cref="Editor"/> (port of <c>EditorOptions</c>).</summary>
public sealed class EditorOptions
{
    /// <summary>
    /// TS <c>paddingX?: number</c>. A <see cref="double"/> so that the JS <c>Number.isFinite</c>
    /// guards (NaN / Infinity) can be reproduced.
    /// </summary>
    public double? PaddingX { get; set; }

    /// <summary>TS <c>autocompleteMaxVisible?: number</c>; clamped to 3..20.</summary>
    public double? AutocompleteMaxVisible { get; set; }
}

/// <summary>
/// Autocomplete provider (port of the TS <c>AutocompleteProvider</c> interface).
///
/// Deviation T17: the TS <c>AbortSignal</c> becomes a <see cref="CancellationToken"/>. The optional
/// <c>shouldTriggerFileCompletion</c> becomes a default interface method returning <c>true</c>, which
/// matches the TS "method absent means allowed" behaviour.
/// </summary>
public interface IAutocompleteProvider
{
    /// <summary>Characters that naturally trigger this provider at token boundaries.</summary>
    IReadOnlyList<string>? TriggerCharacters { get; }

    Task<AutocompleteSuggestions?> GetSuggestionsAsync(
        string[] lines,
        int cursorLine,
        int cursorCol,
        AutocompleteRequest options);

    CompletionApplication ApplyCompletion(
        string[] lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix);

    bool ShouldTriggerFileCompletion(string[] lines, int cursorLine, int cursorCol) => true;
}

/// <summary>
/// Multi-line editor (port of <c>components/editor.ts</c>, the largest single file in the TUI
/// package).
///
/// Behaviour is locked by ~22,300 differential observations in <c>editor-corpus.json</c>, generated
/// by running the original TypeScript implementation (see <c>docs/tui-porting-status.md</c>).
///
/// Deviations:
/// <list type="bullet">
/// <item>T24: <see cref="WordWrapLine"/> does not reproduce the upstream infinite recursion when a
/// single grapheme is wider than <c>maxWidth</c> (the TS call never returns; it raises
/// <c>RangeError: Maximum call stack size exceeded</c>). The grapheme is emitted as one oversized
/// chunk instead.</item>
/// <item>T17: autocomplete cancellation uses <see cref="CancellationTokenSource"/>.</item>
/// <item>T22: word segmentation for CJK text relies on <c>Intl.Segmenter</c>'s dictionary in TS;
/// C# groups runs of word-like characters instead. Steps from the first CJK word-motion operation
/// onward are therefore excluded from the differential corpus (see <c>icuFrom</c> there).</item>
/// <item>T25: JS <c>await</c> always defers to the microtask queue; C# resumes inline for an
/// already-completed task. <see cref="JsAwaitAsync"/> restores the TS ordering so the autocomplete
/// menu is never observable synchronously.</item>
/// </list>
/// </summary>
public class Editor : IEditorComponent, IFocusable
{
    // ---------------------------------------------------------------------------------------------
    // Module-level constants and helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Matches <c>[paste #1 +123 lines]</c> / <c>[paste #2 1234 chars]</c>.</summary>
    private static readonly Regex PasteMarkerRegex =
        new(@"\[paste #([0-9]+)( ([0-9]+ lines|\+[0-9]+ lines|[0-9]+ chars))?\]", RegexOptions.Compiled);

    /// <summary>Anchored single-segment version, used to detect a merged marker segment.</summary>
    private static readonly Regex PasteMarkerSingle =
        new(@"^\[paste #([0-9]+)( ([0-9]+ lines|\+[0-9]+ lines|[0-9]+ chars))?\]$", RegexOptions.Compiled);

    private static readonly SelectListLayoutOptions SlashCommandSelectListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    private const int AttachmentAutocompleteDebounceMs = 20;

    private static readonly string[] DefaultAutocompleteTriggerCharacters = ["@", "#"];

    /// <summary>Characters that may wrap an autocomplete token, e.g. <c>(@src/foo</c> or <c>`@src/foo</c>.</summary>
    private const string AutocompleteWrappers = "([{<`";

    /// <summary>Check whether a segment is a merged paste marker.</summary>
    private static bool IsPasteMarker(string segment) =>
        segment.Length >= 10 && PasteMarkerSingle.IsMatch(segment);

    /// <summary>
    /// Wrap a segmenter and merge graphemes that fall inside paste markers into single atomic
    /// segments, so cursor movement, deletion and word-wrap treat a marker as one unit. Only markers
    /// whose numeric id exists in <paramref name="validIds"/> are merged.
    /// </summary>
    private static List<(int Index, string Segment)> SegmentWithMarkers(
        string text,
        IReadOnlyList<(int Index, string Segment)> baseSegments,
        HashSet<int> validIds)
    {
        // Fast path: no paste markers in the text or no valid ids.
        if (validIds.Count == 0 || !text.Contains("[paste #", StringComparison.Ordinal))
        {
            return [.. baseSegments];
        }

        var markers = new List<(int Start, int End)>();
        foreach (Match match in PasteMarkerRegex.Matches(text))
        {
            var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!validIds.Contains(id))
            {
                continue;
            }

            markers.Add((match.Index, match.Index + match.Length));
        }

        if (markers.Count == 0)
        {
            return [.. baseSegments];
        }

        var result = new List<(int Index, string Segment)>(baseSegments.Count);
        var markerIndex = 0;

        foreach (var segment in baseSegments)
        {
            while (markerIndex < markers.Count && markers[markerIndex].End <= segment.Index)
            {
                markerIndex++;
            }

            var insideMarker = markerIndex < markers.Count &&
                               segment.Index >= markers[markerIndex].Start &&
                               segment.Index < markers[markerIndex].End;

            if (insideMarker)
            {
                // Only the first segment of the marker emits a merged segment; the rest are skipped.
                if (segment.Index == markers[markerIndex].Start)
                {
                    var marker = markers[markerIndex];
                    result.Add((marker.Start, text.Substring(marker.Start, marker.End - marker.Start)));
                }
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    /// <summary>
    /// Split a line into word-wrapped chunks, wrapping at word boundaries where possible and falling
    /// back to character-level wrapping for words longer than the available width.
    /// </summary>
    /// <param name="line">The line to wrap.</param>
    /// <param name="maxWidth">Maximum visible width per chunk.</param>
    /// <param name="preSegmented">Optional pre-segmented graphemes (e.g. paste-marker aware).</param>
    public static List<TextChunk> WordWrapLine(
        string line,
        int maxWidth,
        IReadOnlyList<(int Index, string Segment)>? preSegmented = null)
    {
        if (line.Length == 0 || maxWidth <= 0)
        {
            return [new TextChunk("", 0, 0)];
        }

        if (UnicodeWidth.VisibleWidth(line) <= maxWidth)
        {
            return [new TextChunk(line, 0, line.Length)];
        }

        var chunks = new List<TextChunk>();
        List<(int Index, string Segment)> segments = preSegmented is null
            ? [.. UnicodeWidth.GraphemesWithIndex(line)]
            : [.. preSegmented];

        var currentWidth = 0;
        var chunkStart = 0;

        // Wrap opportunity: the position after the last whitespace before a non-whitespace grapheme.
        var wrapOppIndex = -1;
        var wrapOppWidth = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var grapheme = segment.Segment;
            var graphemeWidth = UnicodeWidth.VisibleWidth(grapheme);
            var charIndex = segment.Index;
            var isWhitespace = !IsPasteMarker(grapheme) && TextLayout.IsWhitespaceChar(grapheme);

            // Overflow check before advancing.
            if (currentWidth + graphemeWidth > maxWidth)
            {
                if (wrapOppIndex >= 0 && currentWidth - wrapOppWidth + graphemeWidth <= maxWidth)
                {
                    // Backtrack to the last wrap opportunity.
                    chunks.Add(new TextChunk(JsString.Slice(line, chunkStart, wrapOppIndex), chunkStart, wrapOppIndex));
                    chunkStart = wrapOppIndex;
                    currentWidth -= wrapOppWidth;
                }
                else if (chunkStart < charIndex)
                {
                    // No viable wrap opportunity: force-break at the current position.
                    chunks.Add(new TextChunk(JsString.Slice(line, chunkStart, charIndex), chunkStart, charIndex));
                    chunkStart = charIndex;
                    currentWidth = 0;
                }

                wrapOppIndex = -1;
            }

            if (graphemeWidth > maxWidth)
            {
                // The segment is a single atomic unit wider than maxWidth. TS re-wraps it at
                // grapheme granularity by recursing, keeping the segment logically atomic for
                // cursor movement and editing while splitting it purely for visual layout.
                var subGraphemes = UnicodeWidth.GraphemesWithIndex(grapheme).ToList();
                if (subGraphemes.Count <= 1)
                {
                    // Deviation T24. When the segment is a *single* grapheme wider than maxWidth
                    // (any CJK/emoji text at layout width 1) the TS recursion calls itself with
                    // the same grapheme and the same maxWidth forever and raises
                    // "RangeError: Maximum call stack size exceeded". Such an input cannot be
                    // represented as corpus data, so it is emitted as one oversized chunk
                    // instead: no text is lost and the caller cannot be crashed.
                    chunks.Add(new TextChunk(grapheme, charIndex, charIndex + grapheme.Length));
                    chunkStart = charIndex + grapheme.Length;
                    currentWidth = 0;
                    wrapOppIndex = -1;
                    continue;
                }

                // Multi-grapheme atomic segment (e.g. a paste marker in a narrow terminal):
                // recurse exactly like TS, offsetting the sub-chunk indices back into `line`.
                var subChunks = WordWrapLine(grapheme, maxWidth, subGraphemes);
                for (var j = 0; j < subChunks.Count - 1; j++)
                {
                    var sub = subChunks[j];
                    chunks.Add(new TextChunk(sub.Text, charIndex + sub.StartIndex, charIndex + sub.EndIndex));
                }

                var last = subChunks[^1];
                chunkStart = charIndex + last.StartIndex;
                currentWidth = UnicodeWidth.VisibleWidth(last.Text);
                wrapOppIndex = -1;
                continue;
            }

            currentWidth += graphemeWidth;

            // Record a wrap opportunity: whitespace followed by non-whitespace, or a boundary where
            // either side is CJK.
            var hasNext = i + 1 < segments.Count;
            if (isWhitespace && hasNext &&
                (IsPasteMarker(segments[i + 1].Segment) || !TextLayout.IsWhitespaceChar(segments[i + 1].Segment)))
            {
                wrapOppIndex = segments[i + 1].Index;
                wrapOppWidth = currentWidth;
            }
            else if (!isWhitespace && hasNext && !TextLayout.IsWhitespaceChar(segments[i + 1].Segment))
            {
                var isCjk = !IsPasteMarker(grapheme) && JsCjk.IsCjkBreak(grapheme);
                var nextIsCjk = !IsPasteMarker(segments[i + 1].Segment) && JsCjk.IsCjkBreak(segments[i + 1].Segment);
                if (isCjk || nextIsCjk)
                {
                    wrapOppIndex = segments[i + 1].Index;
                    wrapOppWidth = currentWidth;
                }
            }
        }

        // The final chunk is empty only when a T24 oversized grapheme ended exactly at the end of the
        // line; the TS code path that could produce that never returns, so no empty chunk is emitted.
        if (chunkStart < line.Length || chunks.Count == 0)
        {
            chunks.Add(new TextChunk(JsString.Slice(line, chunkStart), chunkStart, line.Length));
        }

        return chunks;
    }

    /// <summary>Build the top/bottom border, including the scroll indicator when lines are hidden.</summary>
    private static string CreateScrollBorder(string direction, int hiddenLineCount, int width)
    {
        var availableWidth = Math.Max(0, width);
        var label = $" {direction} {hiddenLineCount} more ";
        var labelWidth = UnicodeWidth.VisibleWidth(label);
        if (labelWidth + 2 <= availableWidth)
        {
            var leftWidth = (int)Math.Floor((availableWidth - labelWidth) / 2.0);
            return new string('─', leftWidth) + label + new string('─', availableWidth - leftWidth - labelWidth);
        }

        var indicator = $"─── {direction} {hiddenLineCount} more ";
        var remaining = availableWidth - UnicodeWidth.VisibleWidth(indicator);
        if (remaining >= 0)
        {
            return indicator + new string('─', remaining);
        }

        var ellipsis = JsString.Slice("...", 0, availableWidth);
        var indicatorWidth = availableWidth - UnicodeWidth.VisibleWidth(ellipsis);
        return TextLayout.SliceByColumn(indicator, 0, indicatorWidth, true) + ellipsis;
    }

    // ---------------------------------------------------------------------------------------------
    // Nested state types
    // ---------------------------------------------------------------------------------------------

    private sealed class EditorState
    {
        public List<string> Lines { get; set; } = [""];

        public int CursorLine { get; set; }

        public int CursorCol { get; set; }
    }

    /// <summary>Undo snapshot: editor text state plus the paste registry.</summary>
    private sealed class EditorSnapshot
    {
        public required EditorState State { get; init; }

        public required Dictionary<int, string> Pastes { get; init; }

        public required int PasteCounter { get; init; }
    }

    private sealed class LayoutLine
    {
        public required string Text { get; init; }

        public required bool HasCursor { get; init; }

        public int? CursorPos { get; init; }
    }

    private readonly record struct VisualLine(int LogicalLine, int StartCol, int Length);

    /// <summary>Undo coalescing state (port of the TS <c>lastAction</c> union).</summary>
    private enum LastAction
    {
        None,
        Kill,
        Yank,
        TypeWord,
    }

    private enum JumpMode
    {
        Forward,
        Backward,
    }

    private enum AutocompleteStateKind
    {
        Regular,
        Force,
    }

    private enum SegmentMode
    {
        Word,
        Grapheme,
    }

    /// <summary>TS <c>{ force: boolean; explicitTab: boolean }</c>.</summary>
    private readonly record struct AutocompleteRequestKind(bool Force, bool ExplicitTab);

    // ---------------------------------------------------------------------------------------------
    // Fields
    // ---------------------------------------------------------------------------------------------

    private EditorState _state = new();
    private readonly EditorTheme _theme;
    private int _paddingX;

    // Last render geometry, used for cursor navigation and mouse hit-testing.
    private int _lastWidth = 80;
    private int _renderedVisibleLineCount = 1;
    private int _renderedAutocompleteHeight;

    private int _scrollOffset;

    private IAutocompleteProvider? _autocompleteProvider;
    private readonly List<string> _autocompleteTriggerCharacters = [.. DefaultAutocompleteTriggerCharacters];
    private SelectList? _autocompleteList;
    private AutocompleteStateKind? _autocompleteState;
    private string _autocompletePrefix = "";
    private int _autocompleteMaxVisible = 5;
    private CancellationTokenSource? _autocompleteAbort;
    private CancellationTokenSource? _autocompleteDebounceTimer;
    private Task _autocompleteRequestTask = Task.CompletedTask;
    private int _autocompleteStartToken;
    private int _autocompleteRequestId;

    // Paste tracking for large pastes.
    private Dictionary<int, string> _pastes = [];
    private int _pasteCounter;

    // Bracketed paste buffering.
    private string _pasteBuffer = "";
    private bool _isInPaste;

    // Prompt history for up/down navigation.
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private EditorState? _historyDraft;

    private readonly KillRing _killRing = new();
    private LastAction _lastAction = LastAction.None;

    private JumpMode? _jumpMode;

    // Preferred visual column for vertical cursor movement (sticky column).
    private int? _preferredVisualCol;

    // When the cursor is snapped to the start of an atomic segment the pre-snap column is kept here.
    private int? _snappedFromCursorCol;

    private readonly UndoStack<EditorSnapshot> _undoStack;

    public Editor(ITui tui, EditorTheme theme, EditorOptions? options = null)
    {
        Tui = tui;
        _theme = theme;
        BorderColor = theme.BorderColor;

        var paddingX = options?.PaddingX ?? 0;
        _paddingX = double.IsFinite(paddingX) ? Math.Max(0, (int)Math.Floor(paddingX)) : 0;

        var maxVisible = options?.AutocompleteMaxVisible ?? 5;
        _autocompleteMaxVisible = double.IsFinite(maxVisible)
            ? Math.Max(3, Math.Min(20, (int)Math.Floor(maxVisible)))
            : 5;

        _undoStack = new UndoStack<EditorSnapshot>(CloneSnapshot);
    }

    /// <summary>Focusable interface - set by TUI when focus changes.</summary>
    public bool Focused { get; set; }

    /// <summary>TS <c>protected tui: TUI</c>.</summary>
    protected ITui Tui { get; }

    /// <summary>Border colour (can be changed dynamically).</summary>
    public Func<string, string> BorderColor { get; set; }

    /// <summary>Called when the user submits.</summary>
    public Action<string>? OnSubmit { get; set; }

    /// <summary>Called whenever the text changes.</summary>
    public Action<string>? OnChange { get; set; }

    /// <summary>When true, Enter does not submit.</summary>
    public bool DisableSubmit { get; set; }

    /// <summary>
    /// The in-flight autocomplete task chain. Exposed so the differential corpus can flush pending
    /// work deterministically (the TS harness awaits two <c>setImmediate</c> ticks instead).
    /// </summary>
    internal Task AutocompleteRequestTask => _autocompleteRequestTask;

    internal int ScrollOffset => _scrollOffset;

    internal int LastLayoutWidth => _lastWidth;

    // ---------------------------------------------------------------------------------------------
    // Small utilities
    // ---------------------------------------------------------------------------------------------

    private static string LineAt(IReadOnlyList<string> lines, int index) =>
        index >= 0 && index < lines.Count ? lines[index] : "";

    private static EditorState CloneState(EditorState state) => new()
    {
        Lines = [.. state.Lines],
        CursorLine = state.CursorLine,
        CursorCol = state.CursorCol,
    };

    private static EditorSnapshot CloneSnapshot(EditorSnapshot snapshot) => new()
    {
        State = CloneState(snapshot.State),
        Pastes = new Dictionary<int, string>(snapshot.Pastes),
        PasteCounter = snapshot.PasteCounter,
    };

    /// <summary>JS <c>String.prototype.indexOf(search, from)</c>.</summary>
    private static int JsIndexOf(string line, string search, int from)
    {
        if (search.Length == 0)
        {
            return Math.Min(Math.Max(from, 0), line.Length);
        }

        if (from < 0)
        {
            from = 0;
        }

        return from > line.Length ? -1 : line.IndexOf(search, from, StringComparison.Ordinal);
    }

    /// <summary>JS <c>String.prototype.lastIndexOf(search, from)</c>.</summary>
    private static int JsLastIndexOf(string line, string search, int from)
    {
        if (search.Length == 0)
        {
            return Math.Min(Math.Max(from, 0), line.Length);
        }

        if (line.Length == 0)
        {
            return -1;
        }

        if (from < 0)
        {
            from = 0;
        }

        // JS clamps the start position to the string length and then searches for the last match
        // whose start index is <= that position.
        var start = Math.Min(from, line.Length);
        if (start > line.Length - search.Length)
        {
            start = line.Length - search.Length;
        }

        return start < 0 ? -1 : line.LastIndexOf(search, start, StringComparison.Ordinal);
    }

    /// <summary>JS <c>String.prototype.replace(search, "")</c> - replaces the first occurrence only.</summary>
    private static string ReplaceFirst(string value, string search)
    {
        var index = value.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? value : value.Remove(index, search.Length);
    }

    private HashSet<int> ValidPasteIds() => [.. _pastes.Keys];

    private static List<(int Index, string Segment, bool IsWordLike)> WordSegmentsWithIndex(string text)
    {
        var result = new List<(int Index, string Segment, bool IsWordLike)>();
        var index = 0;
        foreach (var segment in WordNavigation.SegmentWords(text))
        {
            result.Add((index, segment.Segment, segment.IsWordLike));
            index += segment.Segment.Length;
        }

        return result;
    }

    private List<(int Index, string Segment)> Segment(string text, SegmentMode mode) =>
        SegmentWithMarkers(
            text,
            mode == SegmentMode.Word
                ? [.. WordSegmentsWithIndex(text).Select(entry => (entry.Index, entry.Segment))]
                : new List<(int Index, string Segment)>(UnicodeWidth.GraphemesWithIndex(text)),
            ValidPasteIds());

    /// <summary>
    /// Word segments for cursor navigation. Mirrors TS <c>this.segment(text, "word")</c>: the
    /// segments are paste-marker aware and a merged marker segment has no <c>isWordLike</c> flag
    /// (falsy), exactly like the object literal built by <c>segmentWithMarkers</c>.
    /// </summary>
    private IEnumerable<WordSegment> WordSegmentsForNavigation(string text)
    {
        var raw = WordSegmentsWithIndex(text);
        var wordLikeByIndex = new Dictionary<int, bool>();
        foreach (var entry in raw)
        {
            wordLikeByIndex[entry.Index] = entry.IsWordLike;
        }

        foreach (var segment in SegmentWithMarkers(text, [.. raw.Select(e => (e.Index, e.Segment))], ValidPasteIds()))
        {
            var isWordLike = !IsPasteMarker(segment.Segment) &&
                             wordLikeByIndex.TryGetValue(segment.Index, out var value) &&
                             value;
            yield return new WordSegment(segment.Segment, isWordLike);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Autocomplete token matching
    // ---------------------------------------------------------------------------------------------

    private static bool IsAutocompleteSeparatorChar(char c) => JsCjk.IsAutocompleteSeparator(c);

    private static bool IsAutocompleteWrapper(char c) => AutocompleteWrappers.Contains(c);

    private static bool IsRunToEnd(string text, int from, Func<char, bool> predicate)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (!predicate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Emulates the TS autocomplete trigger regexes:
    /// <c>${boundary}[([{&lt;`]*(?:@"[^"]*|[triggers]${suffix})$</c> for the trigger pattern and
    /// <c>${boundary}[([{&lt;`]*(?:@(?:"[^"]*|${suffix})|[triggers \ @]${suffix})$</c> for the
    /// debounce pattern, where <c>${suffix}</c> is a greedy run of non-separator characters.
    ///
    /// Both reduce to the same predicate because <c>@</c> is always a member of the trigger set
    /// (<c>SetAutocompleteTriggerCharacters</c> starts from the defaults and only appends). The
    /// boundary is the start of the text or a separator; the token must run to the very end.
    /// </summary>
    private static bool MatchesAutocompleteToken(string text, IReadOnlyList<string> triggerCharacters)
    {
        for (var boundary = 0; boundary <= text.Length; boundary++)
        {
            if (boundary > 0 && !IsAutocompleteSeparatorChar(text[boundary - 1]))
            {
                continue;
            }

            var wrappers = 0;
            while (boundary + wrappers < text.Length && IsAutocompleteWrapper(text[boundary + wrappers]))
            {
                wrappers++;
            }

            // The TS character class is greedy but backtracking, so every wrapper count is tried.
            for (var used = wrappers; used >= 0; used--)
            {
                var start = boundary + used;
                if (start >= text.Length)
                {
                    continue;
                }

                // @"[^"]* - a quoted prefix may contain separators.
                if (text[start] == '@' && start + 1 < text.Length && text[start + 1] == '"' &&
                    IsRunToEnd(text, start + 2, c => c != '"'))
                {
                    return true;
                }

                if (triggerCharacters.Contains(text[start].ToString(), StringComparer.Ordinal) &&
                    IsRunToEnd(text, start + 1, c => !IsAutocompleteSeparatorChar(c)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Public accessors
    // ---------------------------------------------------------------------------------------------

    public int GetPaddingX() => _paddingX;

    public void SetPaddingX(double padding)
    {
        var newPadding = double.IsFinite(padding) ? Math.Max(0, (int)Math.Floor(padding)) : 0;
        if (_paddingX != newPadding)
        {
            _paddingX = newPadding;
            Tui.RequestRender();
        }
    }

    public int GetAutocompleteMaxVisible() => _autocompleteMaxVisible;

    public void SetAutocompleteMaxVisible(double maxVisible)
    {
        var newMaxVisible = double.IsFinite(maxVisible)
            ? Math.Max(3, Math.Min(20, (int)Math.Floor(maxVisible)))
            : 5;
        if (_autocompleteMaxVisible != newMaxVisible)
        {
            _autocompleteMaxVisible = newMaxVisible;
            Tui.RequestRender();
        }
    }

    public void SetAutocompleteProvider(IAutocompleteProvider provider)
    {
        CancelAutocomplete();
        _autocompleteProvider = provider;
        SetAutocompleteTriggerCharacters(provider.TriggerCharacters ?? []);
    }

    /// <summary>Add a prompt to history for up/down arrow navigation.</summary>
    public void AddToHistory(string text)
    {
        var trimmed = JsString.Trim(text);
        if (trimmed.Length == 0)
        {
            return;
        }

        // Don't add consecutive duplicates.
        if (_history.Count > 0 && _history[0] == trimmed)
        {
            return;
        }

        _history.Insert(0, trimmed);

        // Limit history size.
        if (_history.Count > 100)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    public void Invalidate()
    {
        // No cached state to invalidate currently.
    }

    protected virtual string RenderTopBorder(int width, int hiddenLineCount) =>
        BorderColor(hiddenLineCount > 0 ? CreateScrollBorder("↑", hiddenLineCount, width) : new string('─', width));

    protected virtual string RenderBottomBorder(int width, int hiddenLineCount) =>
        BorderColor(hiddenLineCount > 0 ? CreateScrollBorder("↓", hiddenLineCount, width) : new string('─', width));

    // ---------------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------------

    public string[] Render(int width)
    {
        var maxPadding = Math.Max(0, (int)Math.Floor((width - 1) / 2.0));
        var paddingX = Math.Min(_paddingX, maxPadding);
        var contentWidth = Math.Max(1, width - (paddingX * 2));

        // Layout width: with padding the cursor can overflow into it, without padding we reserve one
        // column for the cursor.
        var layoutWidth = Math.Max(1, contentWidth - (paddingX != 0 ? 0 : 1));
        _lastWidth = layoutWidth;

        var layoutLines = LayoutText(layoutWidth);

        // Max visible lines: 30% of the terminal height, minimum 5.
        var terminalRows = Tui.Terminal.Rows;
        var maxVisibleLines = Math.Max(5, (int)Math.Floor(terminalRows * 0.3));

        var cursorLineIndex = layoutLines.FindIndex(line => line.HasCursor);
        if (cursorLineIndex == -1)
        {
            cursorLineIndex = 0;
        }

        if (cursorLineIndex < _scrollOffset)
        {
            _scrollOffset = cursorLineIndex;
        }
        else if (cursorLineIndex >= _scrollOffset + maxVisibleLines)
        {
            _scrollOffset = cursorLineIndex - maxVisibleLines + 1;
        }

        var maxScrollOffset = Math.Max(0, layoutLines.Count - maxVisibleLines);
        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScrollOffset));

        var visibleLines = new List<LayoutLine>();
        for (var i = _scrollOffset; i < Math.Min(layoutLines.Count, _scrollOffset + maxVisibleLines); i++)
        {
            visibleLines.Add(layoutLines[i]);
        }

        _renderedVisibleLineCount = visibleLines.Count;

        var result = new List<string>();
        var leftPadding = new string(' ', paddingX);
        var rightPadding = leftPadding;

        result.Add(RenderTopBorder(width, _scrollOffset));

        var emitCursorMarker = Focused;

        foreach (var layoutLine in visibleLines)
        {
            var displayText = layoutLine.Text;
            var lineVisibleWidth = UnicodeWidth.VisibleWidth(layoutLine.Text);
            var cursorInPadding = false;

            if (layoutLine.HasCursor && layoutLine.CursorPos is { } cursorPos)
            {
                var before = JsString.Slice(displayText, 0, cursorPos);
                var after = JsString.Slice(displayText, cursorPos);

                // Zero-width hardware cursor marker, emitted before the fake cursor for IME placement.
                var marker = emitCursorMarker ? Pi.Tui.Tui.CursorMarker : "";

                if (after.Length > 0)
                {
                    // Cursor is on a grapheme - replace it with a highlighted version. The TS code
                    // segments with `this.segment(after, "grapheme")`, which is paste-marker aware,
                    // so a cursor at the start of a merged marker highlights the whole marker.
                    var firstGrapheme = Segment(after, SegmentMode.Grapheme).FirstOrDefault().Segment ?? "";
                    var restAfter = JsString.Slice(after, firstGrapheme.Length);
                    var cursor = $"\x1b[7m{firstGrapheme}\x1b[0m";
                    displayText = before + marker + cursor + restAfter;
                }
                else
                {
                    // Cursor is at the end - add a highlighted space.
                    var cursor = "\x1b[7m \x1b[0m";
                    displayText = before + marker + cursor;
                    lineVisibleWidth += 1;
                    if (lineVisibleWidth > contentWidth && paddingX > 0)
                    {
                        cursorInPadding = true;
                    }
                }
            }

            var padding = new string(' ', Math.Max(0, contentWidth - lineVisibleWidth));
            var lineRightPadding = cursorInPadding ? JsString.Slice(rightPadding, 1) : rightPadding;

            result.Add(leftPadding + displayText + padding + lineRightPadding);
        }

        var linesBelow = layoutLines.Count - (_scrollOffset + visibleLines.Count);
        result.Add(RenderBottomBorder(width, linesBelow));

        _renderedAutocompleteHeight = 0;
        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            var autocompleteResult = _autocompleteList.Render(contentWidth);
            _renderedAutocompleteHeight = autocompleteResult.Length;
            foreach (var line in autocompleteResult)
            {
                var lineWidth = UnicodeWidth.VisibleWidth(line);
                var linePadding = new string(' ', Math.Max(0, contentWidth - lineWidth));
                result.Add(leftPadding + line + linePadding + rightPadding);
            }
        }

        return [.. result];
    }

    // ---------------------------------------------------------------------------------------------
    // Mouse
    // ---------------------------------------------------------------------------------------------

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        var autocompleteStartRow = _renderedVisibleLineCount + 2;
        if (_autocompleteState is not null &&
            _autocompleteList is not null &&
            @event.Y >= autocompleteStartRow &&
            @event.Y < autocompleteStartRow + _renderedAutocompleteHeight)
        {
            var maxPadding = Math.Max(0, (int)Math.Floor((@event.Width - 1) / 2.0));
            var paddingX = Math.Min(_paddingX, maxPadding);
            var contentWidth = Math.Max(1, @event.Width - (paddingX * 2));
            var forwarded = @event.Clone();
            forwarded.X = @event.X - paddingX;
            forwarded.Y = @event.Y - autocompleteStartRow;
            forwarded.Width = contentWidth;
            forwarded.Height = _renderedAutocompleteHeight;
            var result = _autocompleteList.HandleMouse(forwarded);
            return result is null
                ? null
                : new TuiMouseEventResult
                {
                    Handled = result.Handled,
                    Capture = result.Capture,
                    Focus = true,
                    Render = result.Render,
                };
        }

        // Leave press/drag/release unhandled so the renderer's screen-level text selection can run
        // over the editor rows. The renderer synthesizes a click when press and release land on the
        // same cell without movement, which is the gesture that positions the cursor.
        if (@event.Type != TuiMouseEventType.Click || @event.Button != TuiMouseButton.Left)
        {
            return null;
        }

        if (@event.Y <= 0 || @event.Y > _renderedVisibleLineCount)
        {
            return new TuiMouseEventResult { Handled = true, Focus = true };
        }

        var visualLines = BuildVisualLineMap(_lastWidth);
        var visualLineIndex = _scrollOffset + @event.Y - 1;
        if (visualLineIndex < 0 || visualLineIndex >= visualLines.Count)
        {
            return new TuiMouseEventResult { Handled = true, Focus = true };
        }

        var visualLine = visualLines[visualLineIndex];
        var logicalLine = LineAt(_state.Lines, visualLine.LogicalLine);
        var chunkEnd = visualLine.StartCol + visualLine.Length;
        var chunk = JsString.Slice(logicalLine, visualLine.StartCol, chunkEnd);
        var clickMaxPadding = Math.Max(0, (int)Math.Floor((@event.Width - 1) / 2.0));
        var clickPaddingX = Math.Min(_paddingX, clickMaxPadding);
        var targetColumn = Math.Max(0, @event.X - clickPaddingX);
        var visibleColumn = 0;
        var targetIndex = chunk.Length;
        var lastGraphemeIndex = 0;
        foreach (var grapheme in Segment(chunk, SegmentMode.Grapheme))
        {
            var nextColumn = visibleColumn + UnicodeWidth.VisibleWidth(grapheme.Segment);
            lastGraphemeIndex = grapheme.Index;
            if (targetColumn < nextColumn)
            {
                targetIndex = grapheme.Index;
                break;
            }

            visibleColumn = nextColumn;
        }

        var isLastSegment =
            visualLineIndex == visualLines.Count - 1 ||
            visualLines[visualLineIndex + 1].LogicalLine != visualLine.LogicalLine;
        if (!isLastSegment && targetIndex == chunk.Length && chunk.Length > 0)
        {
            targetIndex = lastGraphemeIndex;
        }

        _state.CursorLine = visualLine.LogicalLine;
        SetCursorCol(visualLine.StartCol + targetIndex);
        _lastAction = LastAction.None;
        ExitHistoryBrowsing();
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
        }

        return new TuiMouseEventResult { Handled = true, Focus = true };
    }

    // ---------------------------------------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------------------------------------

    public void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();

        // Handle character jump mode (awaiting the next character to jump to).
        if (_jumpMode is not null)
        {
            // Cancel if the hotkey is pressed again.
            if (kb.Matches(data, TuiKeybindingIds.EditorJumpForward) ||
                kb.Matches(data, TuiKeybindingIds.EditorJumpBackward))
            {
                _jumpMode = null;
                return;
            }

            var printable = Keys.DecodePrintableKey(data) ??
                            (JsString.CharAt(data, 0) >= 32 ? data : null);
            if (printable is not null)
            {
                var direction = _jumpMode.Value;
                _jumpMode = null;
                JumpToChar(printable, direction);
                return;
            }

            // Control character - cancel and fall through to normal handling.
            _jumpMode = null;
        }

        // Handle bracketed paste mode.
        if (data.Contains("\x1b[200~", StringComparison.Ordinal))
        {
            _isInPaste = true;
            _pasteBuffer = "";
            data = ReplaceFirst(data, "\x1b[200~");
        }

        if (_isInPaste)
        {
            _pasteBuffer += data;
            var endIndex = _pasteBuffer.IndexOf("\x1b[201~", StringComparison.Ordinal);
            if (endIndex != -1)
            {
                var pasteContent = _pasteBuffer.Substring(0, endIndex);
                if (pasteContent.Length > 0)
                {
                    HandlePaste(pasteContent);
                }

                _isInPaste = false;
                var remaining = JsString.Slice(_pasteBuffer, endIndex + 6);
                _pasteBuffer = "";
                if (remaining.Length > 0)
                {
                    HandleInput(remaining);
                }

                return;
            }

            return;
        }

        // Ctrl+C - let the parent handle (exit/clear).
        if (kb.Matches(data, TuiKeybindingIds.InputCopy))
        {
            return;
        }

        // Undo.
        if (kb.Matches(data, TuiKeybindingIds.EditorUndo))
        {
            Undo();
            return;
        }

        // Handle autocomplete mode.
        if (_autocompleteState is not null && _autocompleteList is not null)
        {
            if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
            {
                CancelAutocomplete();
                return;
            }

            if (kb.Matches(data, TuiKeybindingIds.SelectUp) || kb.Matches(data, TuiKeybindingIds.SelectDown))
            {
                _autocompleteList.HandleInput(data);
                return;
            }

            if (kb.Matches(data, TuiKeybindingIds.InputTab))
            {
                var selected = _autocompleteList.GetSelectedItem();
                if (selected is not null && _autocompleteProvider is not null)
                {
                    PushUndoSnapshot();
                    _lastAction = LastAction.None;
                    var result = _autocompleteProvider.ApplyCompletion(
                        [.. _state.Lines],
                        _state.CursorLine,
                        _state.CursorCol,
                        ToAutocompleteItem(selected),
                        _autocompletePrefix);
                    _state.Lines = [.. result.Lines];
                    _state.CursorLine = result.CursorLine;
                    SetCursorCol(result.CursorCol);
                    CancelAutocomplete();
                    OnChange?.Invoke(GetText());
                }

                return;
            }

            if (kb.Matches(data, TuiKeybindingIds.SelectConfirm))
            {
                var selected = _autocompleteList.GetSelectedItem();
                if (selected is not null && _autocompleteProvider is not null)
                {
                    PushUndoSnapshot();
                    _lastAction = LastAction.None;
                    var result = _autocompleteProvider.ApplyCompletion(
                        [.. _state.Lines],
                        _state.CursorLine,
                        _state.CursorCol,
                        ToAutocompleteItem(selected),
                        _autocompletePrefix);
                    _state.Lines = [.. result.Lines];
                    _state.CursorLine = result.CursorLine;
                    SetCursorCol(result.CursorCol);

                    if (_autocompletePrefix.StartsWith('/'))
                    {
                        CancelAutocomplete();

                        // Fall through to submit.
                    }
                    else
                    {
                        CancelAutocomplete();
                        OnChange?.Invoke(GetText());
                        return;
                    }
                }
            }
        }

        // Tab - trigger completion.
        if (kb.Matches(data, TuiKeybindingIds.InputTab) && _autocompleteState is null)
        {
            HandleTabCompletion();
            return;
        }

        // Deletion actions.
        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteToLineEnd))
        {
            DeleteToEndOfLine();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteToLineStart))
        {
            DeleteToStartOfLine();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteWordBackward))
        {
            DeleteWordBackwards();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteWordForward))
        {
            DeleteWordForward();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteCharBackward) || Keys.MatchesKey(data, "shift+backspace"))
        {
            HandleBackspace();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorDeleteCharForward) || Keys.MatchesKey(data, "shift+delete"))
        {
            HandleForwardDelete();
            return;
        }

        // Kill ring actions.
        if (kb.Matches(data, TuiKeybindingIds.EditorYank))
        {
            Yank();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorYankPop))
        {
            YankPop();
            return;
        }

        // Dedicated history actions always browse entries instead of moving the cursor.
        if (kb.Matches(data, TuiKeybindingIds.EditorHistoryPrevious))
        {
            CancelAutocomplete();
            NavigateHistory(-1);
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorHistoryNext))
        {
            CancelAutocomplete();
            NavigateHistory(1);
            return;
        }

        // Cursor movement actions.
        if (kb.Matches(data, TuiKeybindingIds.EditorCursorLineStart))
        {
            MoveToLineStart();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorLineEnd))
        {
            MoveToLineEnd();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorWordLeft))
        {
            MoveWordBackwards();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorWordRight))
        {
            MoveWordForwards();
            return;
        }

        // New line.
        if (kb.Matches(data, TuiKeybindingIds.InputNewLine) ||
            (JsString.CharAt(data, 0) == '\n' && data.Length > 1) ||
            data == "\x1b\r" ||
            data == "\x1b[13;2~" ||
            (data.Length > 1 && data.Contains('\x1b') && data.Contains('\r')) ||
            data == "\n")
        {
            if (ShouldSubmitOnBackslashEnter(data, kb))
            {
                HandleBackspace();
                SubmitValue();
                return;
            }

            AddNewLine();
            return;
        }

        // Submit (Enter).
        if (kb.Matches(data, TuiKeybindingIds.InputSubmit))
        {
            if (DisableSubmit)
            {
                return;
            }

            // Workaround for terminals without Shift+Enter support: a backslash before the cursor is
            // deleted and replaced by a newline instead of submitting.
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            if (_state.CursorCol > 0 && JsString.CharAt(currentLine, _state.CursorCol - 1) == '\\')
            {
                HandleBackspace();
                AddNewLine();
                return;
            }

            SubmitValue();
            return;
        }

        // Arrow key navigation (with history support).
        if (kb.Matches(data, TuiKeybindingIds.EditorCursorUp))
        {
            if (IsOnFirstVisualLine() && (IsEditorEmpty() || _historyIndex > -1 || _state.CursorCol == 0))
            {
                NavigateHistory(-1);
            }
            else if (IsOnFirstVisualLine())
            {
                MoveToLineStart();
            }
            else
            {
                MoveCursor(-1, 0);
            }

            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorDown))
        {
            if (_historyIndex > -1 && IsOnLastVisualLine())
            {
                NavigateHistory(1);
            }
            else if (IsOnLastVisualLine())
            {
                MoveToLineEnd();
            }
            else
            {
                MoveCursor(1, 0);
            }

            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorRight))
        {
            MoveCursor(0, 1);
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorCursorLeft))
        {
            MoveCursor(0, -1);
            return;
        }

        // Page up/down - scroll by page and move cursor.
        if (kb.Matches(data, TuiKeybindingIds.EditorPageUp))
        {
            PageScroll(-1);
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorPageDown))
        {
            PageScroll(1);
            return;
        }

        // Character jump mode triggers.
        if (kb.Matches(data, TuiKeybindingIds.EditorJumpForward))
        {
            _jumpMode = JumpMode.Forward;
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.EditorJumpBackward))
        {
            _jumpMode = JumpMode.Backward;
            return;
        }

        // Shift+Space - insert a regular space.
        if (Keys.MatchesKey(data, "shift+space"))
        {
            InsertCharacter(" ");
            return;
        }

        var decoded = Keys.DecodePrintableKey(data);
        if (decoded is not null)
        {
            InsertCharacter(decoded);
            return;
        }

        // Regular characters.
        if (JsString.CharAt(data, 0) >= 32)
        {
            InsertCharacter(data);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------------

    private List<LayoutLine> LayoutText(int contentWidth)
    {
        var layoutLines = new List<LayoutLine>();

        if (_state.Lines.Count == 0 || (_state.Lines.Count == 1 && _state.Lines[0] == ""))
        {
            layoutLines.Add(new LayoutLine { Text = "", HasCursor = true, CursorPos = 0 });
            return layoutLines;
        }

        for (var i = 0; i < _state.Lines.Count; i++)
        {
            var line = _state.Lines[i];
            var isCurrentLine = i == _state.CursorLine;
            var lineVisibleWidth = UnicodeWidth.VisibleWidth(line);

            if (lineVisibleWidth <= contentWidth)
            {
                layoutLines.Add(isCurrentLine
                    ? new LayoutLine { Text = line, HasCursor = true, CursorPos = _state.CursorCol }
                    : new LayoutLine { Text = line, HasCursor = false });
                continue;
            }

            var chunks = WordWrapLine(line, contentWidth, Segment(line, SegmentMode.Grapheme));

            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var cursorPos = _state.CursorCol;
                var isLastChunk = chunkIndex == chunks.Count - 1;

                var hasCursorInChunk = false;
                var adjustedCursorPos = 0;

                if (isCurrentLine)
                {
                    if (isLastChunk)
                    {
                        // Last chunk: the cursor belongs here if >= startIndex.
                        hasCursorInChunk = cursorPos >= chunk.StartIndex;
                        adjustedCursorPos = cursorPos - chunk.StartIndex;
                    }
                    else
                    {
                        // Non-last chunk: the cursor belongs here if in [startIndex, endIndex).
                        hasCursorInChunk = cursorPos >= chunk.StartIndex && cursorPos < chunk.EndIndex;
                        if (hasCursorInChunk)
                        {
                            adjustedCursorPos = cursorPos - chunk.StartIndex;

                            // Clamp in case the cursor was in trimmed whitespace.
                            if (adjustedCursorPos > chunk.Text.Length)
                            {
                                adjustedCursorPos = chunk.Text.Length;
                            }
                        }
                    }
                }

                layoutLines.Add(hasCursorInChunk
                    ? new LayoutLine { Text = chunk.Text, HasCursor = true, CursorPos = adjustedCursorPos }
                    : new LayoutLine { Text = chunk.Text, HasCursor = false });
            }
        }

        return layoutLines;
    }

    // ---------------------------------------------------------------------------------------------
    // Text access
    // ---------------------------------------------------------------------------------------------

    public string GetText() => string.Join("\n", _state.Lines);

    private string ExpandPasteMarkers(string text)
    {
        var result = text;
        foreach (var (pasteId, pasteContent) in _pastes)
        {
            var markerRegex = new Regex(
                $"\\[paste #{pasteId}( ([0-9]+ lines|\\+[0-9]+ lines|[0-9]+ chars))?\\]",
                RegexOptions.None);
            var content = pasteContent;
            result = markerRegex.Replace(result, _ => content);
        }

        return result;
    }

    /// <summary>Get text with paste markers expanded to their actual content.</summary>
    public string GetExpandedText() => ExpandPasteMarkers(string.Join("\n", _state.Lines));

    public string[] GetLines() => [.. _state.Lines];

    public (int Line, int Col) GetCursor() => (_state.CursorLine, _state.CursorCol);

    public void SetText(string text)
    {
        CancelAutocomplete();
        _lastAction = LastAction.None;
        ExitHistoryBrowsing();
        var normalized = NormalizeText(text);

        // Push an undo snapshot if the content differs, so programmatic changes are undoable.
        if (GetText() != normalized)
        {
            PushUndoSnapshot();
        }

        _pastes.Clear();
        _pasteCounter = 0;
        SetTextInternal(normalized);
    }

    /// <summary>Insert text at the current cursor position (atomic for undo).</summary>
    public void InsertTextAtCursor(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        CancelAutocomplete();
        PushUndoSnapshot();
        _lastAction = LastAction.None;
        ExitHistoryBrowsing();
        InsertTextAtCursorInternal(text);
    }

    /// <summary>Normalize line endings and expand tabs to four spaces.</summary>
    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\t", "    ", StringComparison.Ordinal);

    private void InsertTextAtCursorInternal(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var normalized = NormalizeText(text);
        var insertedLines = normalized.Split('\n');

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        var beforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
        var afterCursor = JsString.Slice(currentLine, _state.CursorCol);

        if (insertedLines.Length == 1)
        {
            _state.Lines[_state.CursorLine] = beforeCursor + normalized + afterCursor;
            SetCursorCol(_state.CursorCol + normalized.Length);
        }
        else
        {
            var next = new List<string>();
            for (var i = 0; i < _state.CursorLine; i++)
            {
                next.Add(_state.Lines[i]);
            }

            next.Add(beforeCursor + insertedLines[0]);
            for (var i = 1; i < insertedLines.Length - 1; i++)
            {
                next.Add(insertedLines[i]);
            }

            next.Add(insertedLines[^1] + afterCursor);
            for (var i = _state.CursorLine + 1; i < _state.Lines.Count; i++)
            {
                next.Add(_state.Lines[i]);
            }

            _state.Lines = next;
            _state.CursorLine += insertedLines.Length - 1;
            SetCursorCol(insertedLines[^1].Length);
        }

        OnChange?.Invoke(GetText());
    }

    private void InsertCharacter(string character, bool skipUndoCoalescing = false)
    {
        ExitHistoryBrowsing();

        // Undo coalescing (fish-style):
        // - consecutive word chars coalesce into one undo unit
        // - a space captures state before itself
        // - each space is separately undoable
        if (!skipUndoCoalescing)
        {
            if (TextLayout.IsWhitespaceChar(character) || _lastAction != LastAction.TypeWord)
            {
                PushUndoSnapshot();
            }

            _lastAction = LastAction.TypeWord;
        }

        var line = LineAt(_state.Lines, _state.CursorLine);
        var before = JsString.Slice(line, 0, _state.CursorCol);
        var after = JsString.Slice(line, _state.CursorCol);

        _state.Lines[_state.CursorLine] = before + character + after;
        SetCursorCol(_state.CursorCol + character.Length);

        OnChange?.Invoke(GetText());

        if (_autocompleteState is null)
        {
            if (character == "/" && IsAtStartOfMessage())
            {
                TryTriggerAutocomplete();
            }
            else if (_autocompleteTriggerCharacters.Contains(character, StringComparer.Ordinal))
            {
                var currentLine = LineAt(_state.Lines, _state.CursorLine);
                var textBeforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
                if (MatchesAutocompleteToken(textBeforeCursor, _autocompleteTriggerCharacters))
                {
                    TryTriggerAutocomplete();
                }
            }
            else if (IsWordLikeChar(character))
            {
                var currentLine = LineAt(_state.Lines, _state.CursorLine);
                var textBeforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
                if (IsInSlashCommandContext(textBeforeCursor))
                {
                    TryTriggerAutocomplete();
                }
                else if (MatchesAutocompleteToken(textBeforeCursor, _autocompleteTriggerCharacters))
                {
                    TryTriggerAutocomplete();
                }
            }
        }
        else
        {
            UpdateAutocomplete();
        }
    }

    /// <summary>
    /// TS <c>/[a-zA-Z0-9.\-_]/.test(char) || cjkBreakRegex.test(char)</c>. Both regexes are
    /// unanchored, so an input chunk such as <c>"@src/"</c> (the harness types whole chunks) counts
    /// as word-like when <em>any</em> of its characters matches.
    /// </summary>
    private static bool IsWordLikeChar(string character)
    {
        foreach (var c in character)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                c is '.' or '-' or '_')
            {
                return true;
            }
        }

        return JsCjk.IsCjkBreak(character);
    }

    private void HandlePaste(string pastedText)
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = LastAction.None;

        PushUndoSnapshot();

        // Some terminals re-encode control bytes inside bracketed paste as CSI-u Ctrl+<letter>
        // sequences (ESC [ <codepoint> ; 5 u). Decode those back to their literal byte so the filter
        // below preserves newlines instead of stripping ESC and leaking the printable tail.
        var decodedText = Regex.Replace(
            pastedText,
            "\x1b\\[([0-9]+);5u",
            match =>
            {
                var code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (code is >= 97 and <= 122)
                {
                    return ((char)(code - 96)).ToString();
                }

                if (code is >= 65 and <= 90)
                {
                    return ((char)(code - 64)).ToString();
                }

                return match.Value;
            });

        var cleanText = NormalizeText(decodedText);

        // Filter out non-printable characters except newlines.
        var filtered = new StringBuilder(cleanText.Length);
        foreach (var c in cleanText)
        {
            if (c == '\n' || c >= 32)
            {
                filtered.Append(c);
            }
        }

        var filteredText = filtered.ToString();

        // If pasting a path and the character before the cursor is a word character, prepend a space.
        if (filteredText.Length > 0 && (filteredText[0] is '/' or '~' or '.'))
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var charBeforeCursor = _state.CursorCol > 0 ? JsString.CharAt(currentLine, _state.CursorCol - 1) : '\0';
            if (charBeforeCursor != '\0' && IsJsWordChar(charBeforeCursor))
            {
                filteredText = " " + filteredText;
            }
        }

        var pastedLines = filteredText.Split('\n');
        var totalChars = filteredText.Length;

        if (pastedLines.Length > 10 || totalChars > 1000)
        {
            _pasteCounter++;
            var pasteId = _pasteCounter;
            _pastes[pasteId] = filteredText;

            var marker = pastedLines.Length > 10
                ? $"[paste #{pasteId} +{pastedLines.Length} lines]"
                : $"[paste #{pasteId} {totalChars} chars]";
            InsertTextAtCursorInternal(marker);
            return;
        }

        InsertTextAtCursorInternal(filteredText);
    }

    /// <summary>TS <c>/\w/.test(char)</c> - ASCII word character.</summary>
    private static bool IsJsWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private void AddNewLine()
    {
        CancelAutocomplete();
        ExitHistoryBrowsing();
        _lastAction = LastAction.None;

        PushUndoSnapshot();

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        var before = JsString.Slice(currentLine, 0, _state.CursorCol);
        var after = JsString.Slice(currentLine, _state.CursorCol);

        _state.Lines[_state.CursorLine] = before;
        _state.Lines.Insert(_state.CursorLine + 1, after);

        _state.CursorLine++;
        SetCursorCol(0);

        OnChange?.Invoke(GetText());
    }

    private bool ShouldSubmitOnBackslashEnter(string data, KeybindingsManager kb)
    {
        if (DisableSubmit)
        {
            return false;
        }

        if (!Keys.MatchesKey(data, "enter"))
        {
            return false;
        }

        var submitKeys = kb.GetKeys(TuiKeybindingIds.InputSubmit);
        var hasShiftEnter = submitKeys.Contains("shift+enter", StringComparer.Ordinal) ||
                            submitKeys.Contains("shift+return", StringComparer.Ordinal);
        if (!hasShiftEnter)
        {
            return false;
        }

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        return _state.CursorCol > 0 && JsString.CharAt(currentLine, _state.CursorCol - 1) == '\\';
    }

    private void SubmitValue()
    {
        CancelAutocomplete();
        var result = JsString.Trim(ExpandPasteMarkers(string.Join("\n", _state.Lines)));

        _state = new EditorState { Lines = [""], CursorLine = 0, CursorCol = 0 };
        _pastes.Clear();
        _pasteCounter = 0;
        ExitHistoryBrowsing();
        _scrollOffset = 0;
        _undoStack.Clear();
        _lastAction = LastAction.None;

        OnChange?.Invoke("");
        OnSubmit?.Invoke(result);
    }

    private void HandleBackspace()
    {
        ExitHistoryBrowsing();
        _lastAction = LastAction.None;

        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();

            // Delete the grapheme before the cursor (handles emoji, combining characters, ...).
            var line = LineAt(_state.Lines, _state.CursorLine);
            var beforeCursor = JsString.Slice(line, 0, _state.CursorCol);
            var graphemes = Segment(beforeCursor, SegmentMode.Grapheme);
            var lastGrapheme = graphemes.Count > 0 ? graphemes[^1].Segment : "";
            var graphemeLength = graphemes.Count > 0 ? lastGrapheme.Length : 1;
            var pasteMatch = graphemes.Count > 0 ? PasteMarkerSingle.Match(lastGrapheme) : null;

            if (pasteMatch is { Success: true })
            {
                var targetId = int.Parse(pasteMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                _pastes.Remove(targetId);
                _pasteCounter--;

                // Shift registry entries down in ascending id order, independent of marker order.
                var higherIds = _pastes.Keys.Where(id => id > targetId).OrderBy(id => id).ToList();
                foreach (var id in higherIds)
                {
                    _pastes[id - 1] = _pastes[id];
                    _pastes.Remove(id);
                }

                // Renumber markers with ids greater than the removed one.
                for (var i = 0; i < _state.Lines.Count; i++)
                {
                    _state.Lines[i] = PasteMarkerRegex.Replace(
                        _state.Lines[i],
                        match =>
                        {
                            var x = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            if (x <= targetId)
                            {
                                return match.Value;
                            }

                            // TS interpolates `suffixGroup` directly, which renders the literal
                            // string "undefined" when the optional suffix group did not participate.
                            var suffix = match.Groups[2].Success ? match.Groups[2].Value : "undefined";
                            return $"[paste #{x - 1}{suffix}]";
                        });
                }
            }

            line = LineAt(_state.Lines, _state.CursorLine);
            var before = JsString.Slice(line, 0, _state.CursorCol - graphemeLength);
            var after = JsString.Slice(line, _state.CursorCol);

            _state.Lines[_state.CursorLine] = before + after;
            SetCursorCol(_state.CursorCol - graphemeLength);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();

            // Merge with the previous line.
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var previousLine = LineAt(_state.Lines, _state.CursorLine - 1);

            _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
            _state.Lines.RemoveAt(_state.CursorLine);

            _state.CursorLine--;
            SetCursorCol(previousLine.Length);
        }

        OnChange?.Invoke(GetText());

        // Update or re-trigger autocomplete after backspace.
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
        }
        else
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var textBeforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
            if (IsInSlashCommandContext(textBeforeCursor))
            {
                TryTriggerAutocomplete();
            }
            else if (MatchesAutocompleteToken(textBeforeCursor, _autocompleteTriggerCharacters))
            {
                TryTriggerAutocomplete();
            }
        }
    }

    /// <summary>Set the cursor column and clear the sticky column state.</summary>
    private void SetCursorCol(int col)
    {
        _state.CursorCol = col;
        _preferredVisualCol = null;
        _snappedFromCursorCol = null;
    }

    /// <summary>
    /// Move the cursor to a target visual line, applying the sticky-column decision table. Shared by
    /// <see cref="MoveCursor"/> and <see cref="PageScroll"/>.
    /// </summary>
    private void MoveToVisualLine(IReadOnlyList<VisualLine> visualLines, int currentVisualLine, int targetVisualLine)
    {
        if (currentVisualLine < 0 || currentVisualLine >= visualLines.Count ||
            targetVisualLine < 0 || targetVisualLine >= visualLines.Count)
        {
            return;
        }

        var currentVl = visualLines[currentVisualLine];
        var targetVl = visualLines[targetVisualLine];

        // When the cursor was snapped to a segment start, resolve the pre-snap position against the
        // visual line it belongs to.
        int currentVisualCol;
        if (_snappedFromCursorCol is { } snapped)
        {
            var vlIndex = FindVisualLineAt(visualLines, currentVl.LogicalLine, snapped);
            currentVisualCol = snapped - visualLines[vlIndex].StartCol;
        }
        else
        {
            currentVisualCol = _state.CursorCol - currentVl.StartCol;
        }

        var isLastSourceSegment =
            currentVisualLine == visualLines.Count - 1 ||
            visualLines[currentVisualLine + 1].LogicalLine != currentVl.LogicalLine;
        var sourceMaxVisualCol = isLastSourceSegment ? currentVl.Length : Math.Max(0, currentVl.Length - 1);

        var isLastTargetSegment =
            targetVisualLine == visualLines.Count - 1 ||
            visualLines[targetVisualLine + 1].LogicalLine != targetVl.LogicalLine;
        var targetMaxVisualCol = isLastTargetSegment ? targetVl.Length : Math.Max(0, targetVl.Length - 1);

        var moveToVisualCol = ComputeVerticalMoveColumn(currentVisualCol, sourceMaxVisualCol, targetMaxVisualCol);

        _state.CursorLine = targetVl.LogicalLine;
        var targetCol = targetVl.StartCol + moveToVisualCol;
        var logicalLine = LineAt(_state.Lines, targetVl.LogicalLine);
        _state.CursorCol = Math.Min(targetCol, logicalLine.Length);

        // Snap the cursor to an atomic segment boundary so it never lands inside a multi-grapheme
        // unit (e.g. a paste marker).
        foreach (var segment in Segment(logicalLine, SegmentMode.Grapheme))
        {
            if (segment.Index > _state.CursorCol)
            {
                break;
            }

            if (segment.Segment.Length <= 1)
            {
                continue;
            }

            if (_state.CursorCol < segment.Index + segment.Segment.Length)
            {
                var isContinuation = segment.Index < targetVl.StartCol;
                var isMovingDown = targetVisualLine > currentVisualLine;

                if (isContinuation && isMovingDown)
                {
                    // The segment started on a previous visual line and was already visited on the
                    // way down. Skip the remaining continuation lines.
                    var segEnd = segment.Index + segment.Segment.Length;
                    var next = targetVisualLine + 1;
                    while (next < visualLines.Count &&
                           visualLines[next].LogicalLine == targetVl.LogicalLine &&
                           visualLines[next].StartCol < segEnd)
                    {
                        next++;
                    }

                    if (next < visualLines.Count)
                    {
                        MoveToVisualLine(visualLines, currentVisualLine, next);
                        return;
                    }
                }

                // Snap to the start of the segment and remember the pre-snap position.
                _snappedFromCursorCol = _state.CursorCol;
                _state.CursorCol = segment.Index;
                return;
            }
        }

        _snappedFromCursorCol = null;
    }

    /// <summary>
    /// Compute the target visual column for vertical cursor movement. Implements the sticky-column
    /// decision table:
    /// <code>
    /// | P | S | T | U | Scenario                                             | Set Preferred | Move To     |
    /// |---|---|---|---| ---------------------------------------------------- |---------------|-------------|
    /// | 0 | * | 0 | - | Start nav, target fits                               | null          | current     |
    /// | 0 | * | 1 | - | Start nav, target shorter                            | current       | target end  |
    /// | 1 | 0 | 0 | 0 | Clamped, target fits preferred                       | null          | preferred   |
    /// | 1 | 0 | 0 | 1 | Clamped, target longer but still can't fit preferred | keep          | target end  |
    /// | 1 | 0 | 1 | - | Clamped, target even shorter                         | keep          | target end  |
    /// | 1 | 1 | 0 | - | Rewrapped, target fits current                       | null          | current     |
    /// | 1 | 1 | 1 | - | Rewrapped, target shorter than current               | current       | target end  |
    /// </code>
    /// Where P = preferred column is set, S = cursor in the middle of the source line,
    /// T = target line shorter than the current visual column, U = target line shorter than the
    /// preferred column.
    /// </summary>
    private int ComputeVerticalMoveColumn(int currentVisualCol, int sourceMaxVisualCol, int targetMaxVisualCol)
    {
        var hasPreferred = _preferredVisualCol is not null;
        var cursorInMiddle = currentVisualCol < sourceMaxVisualCol;
        var targetTooShort = targetMaxVisualCol < currentVisualCol;

        if (!hasPreferred || cursorInMiddle)
        {
            if (targetTooShort)
            {
                _preferredVisualCol = currentVisualCol;
                return targetMaxVisualCol;
            }

            _preferredVisualCol = null;
            return currentVisualCol;
        }

        var targetCantFitPreferred = targetMaxVisualCol < _preferredVisualCol!.Value;
        if (targetTooShort || targetCantFitPreferred)
        {
            return targetMaxVisualCol;
        }

        var result = _preferredVisualCol!.Value;
        _preferredVisualCol = null;
        return result;
    }

    private void MoveToLineStart()
    {
        _lastAction = LastAction.None;
        SetCursorCol(0);
    }

    private void MoveToLineEnd()
    {
        _lastAction = LastAction.None;
        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        SetCursorCol(currentLine.Length);
    }

    private void DeleteToStartOfLine()
    {
        ExitHistoryBrowsing();

        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        if (_state.CursorCol > 0)
        {
            PushUndoSnapshot();

            var deletedText = JsString.Slice(currentLine, 0, _state.CursorCol);
            _killRing.Push(deletedText, new KillRingPushOptions
            {
                Prepend = true,
                Accumulate = _lastAction == LastAction.Kill,
            });
            _lastAction = LastAction.Kill;

            _state.Lines[_state.CursorLine] = JsString.Slice(currentLine, _state.CursorCol);
            SetCursorCol(0);
        }
        else if (_state.CursorLine > 0)
        {
            PushUndoSnapshot();

            _killRing.Push("\n", new KillRingPushOptions { Prepend = true, Accumulate = _lastAction == LastAction.Kill });
            _lastAction = LastAction.Kill;

            var previousLine = LineAt(_state.Lines, _state.CursorLine - 1);
            _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
            _state.Lines.RemoveAt(_state.CursorLine);
            _state.CursorLine--;
            SetCursorCol(previousLine.Length);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteToEndOfLine()
    {
        ExitHistoryBrowsing();

        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();

            var deletedText = JsString.Slice(currentLine, _state.CursorCol);
            _killRing.Push(deletedText, new KillRingPushOptions
            {
                Prepend = false,
                Accumulate = _lastAction == LastAction.Kill,
            });
            _lastAction = LastAction.Kill;

            _state.Lines[_state.CursorLine] = JsString.Slice(currentLine, 0, _state.CursorCol);
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();

            _killRing.Push("\n", new KillRingPushOptions { Prepend = false, Accumulate = _lastAction == LastAction.Kill });
            _lastAction = LastAction.Kill;

            var nextLine = LineAt(_state.Lines, _state.CursorLine + 1);
            _state.Lines[_state.CursorLine] = currentLine + nextLine;
            _state.Lines.RemoveAt(_state.CursorLine + 1);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteWordBackwards()
    {
        ExitHistoryBrowsing();

        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        // If at the start of the line, behave like backspace at column 0 (merge with previous line).
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                PushUndoSnapshot();

                _killRing.Push("\n", new KillRingPushOptions { Prepend = true, Accumulate = _lastAction == LastAction.Kill });
                _lastAction = LastAction.Kill;

                var previousLine = LineAt(_state.Lines, _state.CursorLine - 1);
                _state.Lines[_state.CursorLine - 1] = previousLine + currentLine;
                _state.Lines.RemoveAt(_state.CursorLine);
                _state.CursorLine--;
                SetCursorCol(previousLine.Length);
            }
        }
        else
        {
            PushUndoSnapshot();

            // Save lastAction before the cursor movement resets it.
            var wasKill = _lastAction == LastAction.Kill;

            var oldCursorCol = _state.CursorCol;
            MoveWordBackwards();
            var deleteFrom = _state.CursorCol;
            SetCursorCol(oldCursorCol);

            var deletedText = JsString.Slice(currentLine, deleteFrom, _state.CursorCol);
            _killRing.Push(deletedText, new KillRingPushOptions { Prepend = true, Accumulate = wasKill });
            _lastAction = LastAction.Kill;

            _state.Lines[_state.CursorLine] =
                JsString.Slice(currentLine, 0, deleteFrom) + JsString.Slice(currentLine, _state.CursorCol);
            SetCursorCol(deleteFrom);
        }

        OnChange?.Invoke(GetText());
    }

    private void DeleteWordForward()
    {
        ExitHistoryBrowsing();

        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        // If at the end of the line, merge with the next line (delete the newline).
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                PushUndoSnapshot();

                _killRing.Push("\n", new KillRingPushOptions { Prepend = false, Accumulate = _lastAction == LastAction.Kill });
                _lastAction = LastAction.Kill;

                var nextLine = LineAt(_state.Lines, _state.CursorLine + 1);
                _state.Lines[_state.CursorLine] = currentLine + nextLine;
                _state.Lines.RemoveAt(_state.CursorLine + 1);
            }
        }
        else
        {
            PushUndoSnapshot();

            var wasKill = _lastAction == LastAction.Kill;

            var oldCursorCol = _state.CursorCol;
            MoveWordForwards();
            var deleteTo = _state.CursorCol;
            SetCursorCol(oldCursorCol);

            var deletedText = JsString.Slice(currentLine, _state.CursorCol, deleteTo);
            _killRing.Push(deletedText, new KillRingPushOptions { Prepend = false, Accumulate = wasKill });
            _lastAction = LastAction.Kill;

            _state.Lines[_state.CursorLine] =
                JsString.Slice(currentLine, 0, _state.CursorCol) + JsString.Slice(currentLine, deleteTo);
        }

        OnChange?.Invoke(GetText());
    }

    private void HandleForwardDelete()
    {
        ExitHistoryBrowsing();
        _lastAction = LastAction.None;

        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        if (_state.CursorCol < currentLine.Length)
        {
            PushUndoSnapshot();

            // Delete the grapheme at the cursor position.
            var afterCursor = JsString.Slice(currentLine, _state.CursorCol);
            var graphemes = Segment(afterCursor, SegmentMode.Grapheme);
            var graphemeLength = graphemes.Count > 0 ? graphemes[0].Segment.Length : 1;

            var before = JsString.Slice(currentLine, 0, _state.CursorCol);
            var after = JsString.Slice(currentLine, _state.CursorCol + graphemeLength);
            _state.Lines[_state.CursorLine] = before + after;
        }
        else if (_state.CursorLine < _state.Lines.Count - 1)
        {
            PushUndoSnapshot();

            var nextLine = LineAt(_state.Lines, _state.CursorLine + 1);
            _state.Lines[_state.CursorLine] = currentLine + nextLine;
            _state.Lines.RemoveAt(_state.CursorLine + 1);
        }

        OnChange?.Invoke(GetText());

        // Update or re-trigger autocomplete after forward delete.
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
        }
        else
        {
            var line = LineAt(_state.Lines, _state.CursorLine);
            var textBeforeCursor = JsString.Slice(line, 0, _state.CursorCol);
            if (IsInSlashCommandContext(textBeforeCursor))
            {
                TryTriggerAutocomplete();
            }
            else if (MatchesAutocompleteToken(textBeforeCursor, _autocompleteTriggerCharacters))
            {
                TryTriggerAutocomplete();
            }
        }
    }

    /// <summary>Map visual lines onto logical positions.</summary>
    private List<VisualLine> BuildVisualLineMap(int width)
    {
        var visualLines = new List<VisualLine>();

        for (var i = 0; i < _state.Lines.Count; i++)
        {
            var line = _state.Lines[i];
            var lineVisibleWidth = UnicodeWidth.VisibleWidth(line);
            if (line.Length == 0)
            {
                visualLines.Add(new VisualLine(i, 0, 0));
            }
            else if (lineVisibleWidth <= width)
            {
                visualLines.Add(new VisualLine(i, 0, line.Length));
            }
            else
            {
                foreach (var chunk in WordWrapLine(line, width, Segment(line, SegmentMode.Grapheme)))
                {
                    visualLines.Add(new VisualLine(i, chunk.StartIndex, chunk.EndIndex - chunk.StartIndex));
                }
            }
        }

        return visualLines;
    }

    /// <summary>Find the visual line index containing the given logical position.</summary>
    private static int FindVisualLineAt(IReadOnlyList<VisualLine> visualLines, int line, int col)
    {
        for (var i = 0; i < visualLines.Count; i++)
        {
            var vl = visualLines[i];
            if (vl.LogicalLine != line)
            {
                continue;
            }

            var offset = col - vl.StartCol;

            // For the last segment of a logical line the cursor may sit at `length`.
            var isLastSegmentOfLine =
                i == visualLines.Count - 1 || visualLines[i + 1].LogicalLine != vl.LogicalLine;
            if (offset >= 0 && (offset < vl.Length || (isLastSegmentOfLine && offset == vl.Length)))
            {
                return i;
            }
        }

        return visualLines.Count - 1;
    }

    private int FindCurrentVisualLine(IReadOnlyList<VisualLine> visualLines) =>
        FindVisualLineAt(visualLines, _state.CursorLine, _state.CursorCol);

    private void MoveCursor(int deltaLine, int deltaCol)
    {
        _lastAction = LastAction.None;
        var visualLines = BuildVisualLineMap(_lastWidth);
        var currentVisualLine = FindCurrentVisualLine(visualLines);

        if (deltaLine != 0)
        {
            var targetVisualLine = currentVisualLine + deltaLine;
            if (targetVisualLine >= 0 && targetVisualLine < visualLines.Count)
            {
                MoveToVisualLine(visualLines, currentVisualLine, targetVisualLine);
            }
        }

        if (deltaCol != 0)
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);

            if (deltaCol > 0)
            {
                if (_state.CursorCol < currentLine.Length)
                {
                    var afterCursor = JsString.Slice(currentLine, _state.CursorCol);
                    var graphemes = Segment(afterCursor, SegmentMode.Grapheme);
                    SetCursorCol(_state.CursorCol + (graphemes.Count > 0 ? graphemes[0].Segment.Length : 1));
                }
                else if (_state.CursorLine < _state.Lines.Count - 1)
                {
                    _state.CursorLine++;
                    SetCursorCol(0);
                }
                else if (currentVisualLine >= 0 && currentVisualLine < visualLines.Count)
                {
                    _preferredVisualCol = _state.CursorCol - visualLines[currentVisualLine].StartCol;
                }
            }
            else
            {
                if (_state.CursorCol > 0)
                {
                    var beforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
                    var graphemes = Segment(beforeCursor, SegmentMode.Grapheme);
                    SetCursorCol(_state.CursorCol - (graphemes.Count > 0 ? graphemes[^1].Segment.Length : 1));
                }
                else if (_state.CursorLine > 0)
                {
                    _state.CursorLine--;
                    var prevLine = LineAt(_state.Lines, _state.CursorLine);
                    SetCursorCol(prevLine.Length);
                }
            }
        }

        // Keep an open autocomplete picker in sync with the new cursor position.
        if (_autocompleteState is not null)
        {
            UpdateAutocomplete();
        }
    }

    /// <summary>Scroll by a page (<paramref name="direction"/> -1 for up, 1 for down).</summary>
    private void PageScroll(int direction)
    {
        _lastAction = LastAction.None;
        var terminalRows = Tui.Terminal.Rows;
        var pageSize = Math.Max(5, (int)Math.Floor(terminalRows * 0.3));

        var visualLines = BuildVisualLineMap(_lastWidth);
        var currentVisualLine = FindCurrentVisualLine(visualLines);
        var targetVisualLine = Math.Max(0, Math.Min(visualLines.Count - 1, currentVisualLine + (direction * pageSize)));

        MoveToVisualLine(visualLines, currentVisualLine, targetVisualLine);
    }

    private void MoveWordBackwards()
    {
        _lastAction = LastAction.None;
        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        // If at the start of the line, move to the end of the previous line.
        if (_state.CursorCol == 0)
        {
            if (_state.CursorLine > 0)
            {
                _state.CursorLine--;
                var prevLine = LineAt(_state.Lines, _state.CursorLine);
                SetCursorCol(prevLine.Length);
            }

            return;
        }

        SetCursorCol(WordNavigation.FindWordBackward(currentLine, _state.CursorCol, new WordNavigationOptions
        {
            Segment = WordSegmentsForNavigation,
            IsAtomicSegment = IsPasteMarker,
        }));
    }

    private void Yank()
    {
        if (_killRing.Length == 0)
        {
            return;
        }

        PushUndoSnapshot();

        var text = _killRing.Peek()!;
        InsertYankedText(text);

        _lastAction = LastAction.Yank;
    }

    /// <summary>Cycle through the kill ring (only immediately after yank or yank-pop).</summary>
    private void YankPop()
    {
        if (_lastAction != LastAction.Yank || _killRing.Length <= 1)
        {
            return;
        }

        PushUndoSnapshot();

        DeleteYankedText();

        _killRing.Rotate();

        var text = _killRing.Peek()!;
        InsertYankedText(text);

        _lastAction = LastAction.Yank;
    }

    private void InsertYankedText(string text)
    {
        ExitHistoryBrowsing();
        var lines = text.Split('\n');

        if (lines.Length == 1)
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var before = JsString.Slice(currentLine, 0, _state.CursorCol);
            var after = JsString.Slice(currentLine, _state.CursorCol);
            _state.Lines[_state.CursorLine] = before + text + after;
            SetCursorCol(_state.CursorCol + text.Length);
        }
        else
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var before = JsString.Slice(currentLine, 0, _state.CursorCol);
            var after = JsString.Slice(currentLine, _state.CursorCol);

            _state.Lines[_state.CursorLine] = before + lines[0];

            for (var i = 1; i < lines.Length - 1; i++)
            {
                _state.Lines.Insert(_state.CursorLine + i, lines[i]);
            }

            var lastLineIndex = _state.CursorLine + lines.Length - 1;
            _state.Lines.Insert(lastLineIndex, lines[^1] + after);

            _state.CursorLine = lastLineIndex;
            SetCursorCol(lines[^1].Length);
        }

        OnChange?.Invoke(GetText());
    }

    /// <summary>
    /// Delete the previously yanked text (used by yank-pop). The yanked text is derived from the end
    /// of the kill ring since it has not been rotated yet.
    /// </summary>
    private void DeleteYankedText()
    {
        var yankedText = _killRing.Peek();
        if (yankedText is null)
        {
            return;
        }

        var yankLines = yankedText.Split('\n');

        if (yankLines.Length == 1)
        {
            var currentLine = LineAt(_state.Lines, _state.CursorLine);
            var deleteLength = yankedText.Length;
            var before = JsString.Slice(currentLine, 0, _state.CursorCol - deleteLength);
            var after = JsString.Slice(currentLine, _state.CursorCol);
            _state.Lines[_state.CursorLine] = before + after;
            SetCursorCol(_state.CursorCol - deleteLength);
        }
        else
        {
            var startLine = _state.CursorLine - (yankLines.Length - 1);
            var startCol = LineAt(_state.Lines, startLine).Length - yankLines[0].Length;
            var afterCursor = JsString.Slice(LineAt(_state.Lines, _state.CursorLine), _state.CursorCol);
            var beforeYank = JsString.Slice(LineAt(_state.Lines, startLine), 0, startCol);

            _state.Lines.RemoveRange(startLine, yankLines.Length);
            _state.Lines.Insert(startLine, beforeYank + afterCursor);

            _state.CursorLine = startLine;
            SetCursorCol(startCol);
        }

        OnChange?.Invoke(GetText());
    }

    private void PushUndoSnapshot() => _undoStack.Push(new EditorSnapshot
    {
        State = _state,
        Pastes = _pastes,
        PasteCounter = _pasteCounter,
    });

    private void Undo()
    {
        ExitHistoryBrowsing();
        var snapshot = _undoStack.Pop();
        if (snapshot is null)
        {
            return;
        }

        _state = CloneState(snapshot.State);
        _pastes = new Dictionary<int, string>(snapshot.Pastes);
        _pasteCounter = snapshot.PasteCounter;
        _lastAction = LastAction.None;
        _preferredVisualCol = null;
        OnChange?.Invoke(GetText());
    }

    /// <summary>
    /// Jump to the first occurrence of a character in the given direction. Multi-line search,
    /// case-sensitive, skipping the current cursor position.
    /// </summary>
    private void JumpToChar(string character, JumpMode direction)
    {
        _lastAction = LastAction.None;
        var isForward = direction == JumpMode.Forward;
        var lines = _state.Lines;

        var end = isForward ? lines.Count : -1;
        var step = isForward ? 1 : -1;

        for (var lineIndex = _state.CursorLine; lineIndex != end; lineIndex += step)
        {
            var line = LineAt(lines, lineIndex);
            var isCurrentLine = lineIndex == _state.CursorLine;

            var index = isForward
                ? JsIndexOf(line, character, isCurrentLine ? _state.CursorCol + 1 : 0)
                : JsLastIndexOf(line, character, isCurrentLine ? _state.CursorCol - 1 : line.Length);

            if (index != -1)
            {
                _state.CursorLine = lineIndex;
                SetCursorCol(index);
                return;
            }
        }

        // No match found - the cursor stays in place.
    }

    private void MoveWordForwards()
    {
        _lastAction = LastAction.None;
        var currentLine = LineAt(_state.Lines, _state.CursorLine);

        // If at the end of the line, move to the start of the next line.
        if (_state.CursorCol >= currentLine.Length)
        {
            if (_state.CursorLine < _state.Lines.Count - 1)
            {
                _state.CursorLine++;
                SetCursorCol(0);
            }

            return;
        }

        SetCursorCol(WordNavigation.FindWordForward(currentLine, _state.CursorCol, new WordNavigationOptions
        {
            Segment = WordSegmentsForNavigation,
            IsAtomicSegment = IsPasteMarker,
        }));
    }

    // ---------------------------------------------------------------------------------------------
    // History
    // ---------------------------------------------------------------------------------------------

    private bool IsEditorEmpty() => _state.Lines.Count == 1 && _state.Lines[0] == "";

    private bool IsOnFirstVisualLine()
    {
        var visualLines = BuildVisualLineMap(_lastWidth);
        return FindCurrentVisualLine(visualLines) == 0;
    }

    private bool IsOnLastVisualLine()
    {
        var visualLines = BuildVisualLineMap(_lastWidth);
        return FindCurrentVisualLine(visualLines) == visualLines.Count - 1;
    }

    private void NavigateHistory(int direction)
    {
        _lastAction = LastAction.None;
        if (_history.Count == 0)
        {
            return;
        }

        var newIndex = _historyIndex - direction;
        if (newIndex < -1 || newIndex >= _history.Count)
        {
            return;
        }

        // Capture state when first entering history browsing mode.
        if (_historyIndex == -1 && newIndex >= 0)
        {
            PushUndoSnapshot();
            _historyDraft = CloneState(_state);
        }

        _historyIndex = newIndex;

        if (_historyIndex == -1)
        {
            var draft = _historyDraft;
            _historyDraft = null;
            if (draft is not null)
            {
                _state = draft;
                _preferredVisualCol = null;
                _snappedFromCursorCol = null;
                _scrollOffset = 0;
                OnChange?.Invoke(GetText());
            }
            else
            {
                SetTextInternal("");
            }
        }
        else
        {
            SetTextInternal(
                LineAt(_history, _historyIndex),
                direction == -1 ? CursorPlacement.Start : CursorPlacement.End);
        }
    }

    private enum CursorPlacement
    {
        Start,
        End,
    }

    private void ExitHistoryBrowsing()
    {
        _historyIndex = -1;
        _historyDraft = null;
    }

    /// <summary>Internal SetText that does not reset history state - used by <see cref="NavigateHistory"/>.</summary>
    private void SetTextInternal(string text, CursorPlacement cursorPlacement = CursorPlacement.End)
    {
        var lines = text.Split('\n');
        _state.Lines = [.. lines];
        _state.CursorLine = cursorPlacement == CursorPlacement.Start ? 0 : _state.Lines.Count - 1;
        SetCursorCol(cursorPlacement == CursorPlacement.Start ? 0 : LineAt(_state.Lines, _state.CursorLine).Length);

        // Reset scroll - Render() will adjust to show the cursor.
        _scrollOffset = 0;

        OnChange?.Invoke(GetText());
    }

    // ---------------------------------------------------------------------------------------------
    // Slash-command / autocomplete context
    // ---------------------------------------------------------------------------------------------

    /// <summary>The slash menu is only allowed on the first line of the editor.</summary>
    private bool IsSlashMenuAllowed() => _state.CursorLine == 0;

    private bool IsAtStartOfMessage()
    {
        if (!IsSlashMenuAllowed())
        {
            return false;
        }

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        var beforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
        var trimmed = JsString.Trim(beforeCursor);
        return trimmed.Length == 0 || trimmed == "/";
    }

    private bool IsInSlashCommandContext(string textBeforeCursor) =>
        IsSlashMenuAllowed() && JsString.TrimStart(textBeforeCursor).StartsWith('/');

    /// <summary>
    /// Find the best autocomplete item index for the given prefix, or -1 when nothing matches.
    /// Priority: exact match, then first prefix match. Case-sensitive, matching <c>value</c> only.
    /// </summary>
    private static int GetBestAutocompleteMatchIndex(IReadOnlyList<AutocompleteItem> items, string prefix)
    {
        if (prefix.Length == 0)
        {
            return -1;
        }

        var firstPrefixIndex = -1;

        for (var i = 0; i < items.Count; i++)
        {
            var value = items[i].Value;
            if (value == prefix)
            {
                return i;
            }

            if (firstPrefixIndex == -1 && value.StartsWith(prefix, StringComparison.Ordinal))
            {
                firstPrefixIndex = i;
            }
        }

        return firstPrefixIndex;
    }

    private SelectList CreateAutocompleteList(string prefix, IReadOnlyList<AutocompleteItem> items)
    {
        var layout = prefix.StartsWith('/') ? SlashCommandSelectListLayout : null;
        var list = new SelectList(
            items.Select(item => new SelectItem
            {
                Value = item.Value,
                Label = item.Label,
                Description = item.Description,
            }),
            _autocompleteMaxVisible,
            _theme.SelectList,
            layout);

        list.OnSelect = selected =>
        {
            if (_autocompleteProvider is null)
            {
                return;
            }

            PushUndoSnapshot();
            _lastAction = LastAction.None;
            var result = _autocompleteProvider.ApplyCompletion(
                [.. _state.Lines],
                _state.CursorLine,
                _state.CursorCol,
                ToAutocompleteItem(selected),
                _autocompletePrefix);
            _state.Lines = [.. result.Lines];
            _state.CursorLine = result.CursorLine;
            SetCursorCol(result.CursorCol);
            CancelAutocomplete();
            OnChange?.Invoke(GetText());
        };

        return list;
    }

    private static AutocompleteItem ToAutocompleteItem(SelectItem item) => new()
    {
        Value = item.Value,
        Label = item.Label,
        Description = item.Description,
    };

    private void TryTriggerAutocomplete(bool explicitTab = false) =>
        RequestAutocomplete(new AutocompleteRequestKind(false, explicitTab));

    private void HandleTabCompletion()
    {
        if (_autocompleteProvider is null)
        {
            return;
        }

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        var beforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);

        if (IsInSlashCommandContext(beforeCursor) && !JsString.TrimStart(beforeCursor).Contains(' '))
        {
            HandleSlashCommandCompletion();
        }
        else
        {
            ForceFileAutocomplete(true);
        }
    }

    private void HandleSlashCommandCompletion() =>
        RequestAutocomplete(new AutocompleteRequestKind(false, true));

    private void ForceFileAutocomplete(bool explicitTab = false) =>
        RequestAutocomplete(new AutocompleteRequestKind(true, explicitTab));

    private void RequestAutocomplete(AutocompleteRequestKind options)
    {
        if (_autocompleteProvider is null)
        {
            return;
        }

        if (options.Force &&
            !_autocompleteProvider.ShouldTriggerFileCompletion(_state.Lines.ToArray(), _state.CursorLine, _state.CursorCol))
        {
            return;
        }

        CancelAutocompleteRequest();
        var startToken = ++_autocompleteStartToken;

        var debounceMs = GetAutocompleteDebounceMs(options);
        if (debounceMs > 0)
        {
            var source = new CancellationTokenSource();
            _autocompleteDebounceTimer = source;
            _ = DebounceThenStartAsync(startToken, options, debounceMs, source);
            return;
        }

        _ = StartAutocompleteRequestAsync(startToken, options);
    }

    private async Task DebounceThenStartAsync(
        int startToken,
        AutocompleteRequestKind options,
        int debounceMs,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(debounceMs, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await StartAutocompleteRequestAsync(startToken, options).ConfigureAwait(false);

        // Cleared only after the request has been started, so that
        // WaitForAutocompleteIdleAsync can rely on "timer set => work may still be pending".
        if (ReferenceEquals(_autocompleteDebounceTimer, source))
        {
            _autocompleteDebounceTimer = null;
        }
    }

    /// <summary>
    /// True when no autocomplete debounce timer is armed and no request is in flight. Used by the
    /// differential harness to wait for quiescence without racing the timer's own thread.
    /// </summary>
    internal bool IsAutocompleteIdle => _autocompleteDebounceTimer is null && _autocompleteRequestTask.IsCompleted;

    /// <summary>
    /// True while an autocomplete request chain is still running. Unlike
    /// <see cref="IsAutocompleteIdle"/> this ignores an armed debounce timer, so the differential
    /// harness can tell "the chain still has work to do" apart from "we are waiting for the clock".
    /// </summary>
    internal bool IsAutocompleteRequestInFlight => !_autocompleteRequestTask.IsCompleted;

    private void SetAutocompleteTriggerCharacters(IReadOnlyList<string> triggerCharacters)
    {
        var next = new List<string>(DefaultAutocompleteTriggerCharacters);
        foreach (var character in triggerCharacters)
        {
            if (character.Length != 1 ||
                character == "/" ||
                TextLayout.IsWhitespaceChar(character) ||
                next.Contains(character, StringComparer.Ordinal))
            {
                continue;
            }

            next.Add(character);
        }

        _autocompleteTriggerCharacters.Clear();
        _autocompleteTriggerCharacters.AddRange(next);
    }

    private int GetAutocompleteDebounceMs(AutocompleteRequestKind options)
    {
        if (options.ExplicitTab || options.Force)
        {
            return 0;
        }

        var currentLine = LineAt(_state.Lines, _state.CursorLine);
        var textBeforeCursor = JsString.Slice(currentLine, 0, _state.CursorCol);
        return MatchesAutocompleteToken(textBeforeCursor, _autocompleteTriggerCharacters)
            ? AttachmentAutocompleteDebounceMs
            : 0;
    }

    private async Task StartAutocompleteRequestAsync(int startToken, AutocompleteRequestKind options)
    {
        var previousTask = _autocompleteRequestTask;
        var task = RunChainedAutocompleteRequestAsync(previousTask, startToken, options);
        _autocompleteRequestTask = task;
        await task.ConfigureAwait(false);
    }

    private async Task RunChainedAutocompleteRequestAsync(
        Task previousTask,
        int startToken,
        AutocompleteRequestKind options)
    {
        // T25: see JsAwaitAsync.
        await JsAwaitAsync(previousTask).ConfigureAwait(false);
        if (startToken != _autocompleteStartToken || _autocompleteProvider is null)
        {
            return;
        }

        var controller = new CancellationTokenSource();
        _autocompleteAbort = controller;
        var requestId = ++_autocompleteRequestId;
        var snapshotText = GetText();
        var snapshotLine = _state.CursorLine;
        var snapshotCol = _state.CursorCol;

        await RunAutocompleteRequestAsync(requestId, controller, snapshotText, snapshotLine, snapshotCol, options)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Emulates a JS <c>await</c>, which always defers to the single-threaded microtask queue - even
    /// when the awaited promise is already settled. C# instead resumes inline for an already
    /// completed task and on a thread-pool thread otherwise, so a continuation can run
    /// *concurrently* with the code that triggered it. Because every differential scenario observes
    /// the editor between operations, that difference is directly visible (the TS menu opens one
    /// operation later than a naive C# port would). See deviation T25.
    /// </summary>
    private async Task JsAwaitAsync(Task task)
    {
        await task.ConfigureAwait(false);
        await DeferAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The "microtask hop" of <see cref="JsAwaitAsync"/>. Production uses <see cref="Task.Yield"/>;
    /// the differential harness replaces it with a <see cref="JsMicrotaskSource"/> it resumes
    /// between operations, which makes the interleaving deterministic instead of racing the thread
    /// pool. See deviation T25.
    /// </summary>
    internal Func<JsMicrotask>? AutocompleteDeferral { get; set; }

    private async Task DeferAsync()
    {
        if (AutocompleteDeferral is { } deferral)
        {
            await deferral();
            return;
        }

        await Task.Yield();
    }

    private async Task RunAutocompleteRequestAsync(
        int requestId,
        CancellationTokenSource controller,
        string snapshotText,
        int snapshotLine,
        int snapshotCol,
        AutocompleteRequestKind options)
    {
        if (_autocompleteProvider is null)
        {
            return;
        }

        // T25: the TS provider is an `async` function, so awaiting its promise always defers one
        // microtask even when it resolves immediately. Mirror that so the menu/apply step is not
        // observable synchronously.
        var suggestionsTask = _autocompleteProvider.GetSuggestionsAsync(
            [.. _state.Lines],
            _state.CursorLine,
            _state.CursorCol,
            new AutocompleteRequest(controller.Token, options.Force));
        await JsAwaitAsync(suggestionsTask).ConfigureAwait(false);
        var suggestions = await suggestionsTask.ConfigureAwait(false);

        if (!IsAutocompleteRequestCurrent(requestId, controller, snapshotText, snapshotLine, snapshotCol))
        {
            return;
        }

        _autocompleteAbort = null;

        if (suggestions is null || suggestions.Items.Count == 0)
        {
            CancelAutocomplete();
            Tui.RequestRender();
            return;
        }

        if (options.Force && options.ExplicitTab && suggestions.Items.Count == 1)
        {
            var item = suggestions.Items[0];
            PushUndoSnapshot();
            _lastAction = LastAction.None;
            var result = _autocompleteProvider.ApplyCompletion(
                [.. _state.Lines],
                _state.CursorLine,
                _state.CursorCol,
                item,
                suggestions.Prefix);
            _state.Lines = [.. result.Lines];
            _state.CursorLine = result.CursorLine;
            SetCursorCol(result.CursorCol);
            OnChange?.Invoke(GetText());
            Tui.RequestRender();
            return;
        }

        ApplyAutocompleteSuggestions(suggestions, options.Force ? AutocompleteStateKind.Force : AutocompleteStateKind.Regular);
        Tui.RequestRender();
    }

    private bool IsAutocompleteRequestCurrent(
        int requestId,
        CancellationTokenSource controller,
        string snapshotText,
        int snapshotLine,
        int snapshotCol) =>
        !controller.IsCancellationRequested &&
        requestId == _autocompleteRequestId &&
        GetText() == snapshotText &&
        _state.CursorLine == snapshotLine &&
        _state.CursorCol == snapshotCol;

    private void ApplyAutocompleteSuggestions(AutocompleteSuggestions suggestions, AutocompleteStateKind state)
    {
        _autocompletePrefix = suggestions.Prefix;
        _autocompleteList = CreateAutocompleteList(suggestions.Prefix, suggestions.Items);

        var bestMatchIndex = GetBestAutocompleteMatchIndex(suggestions.Items, suggestions.Prefix);
        if (bestMatchIndex >= 0)
        {
            _autocompleteList.SetSelectedIndex(bestMatchIndex);
        }

        _autocompleteState = state;
    }

    private void CancelAutocompleteRequest()
    {
        _autocompleteStartToken += 1;
        if (_autocompleteDebounceTimer is { } timer)
        {
            timer.Cancel();
            timer.Dispose();
            _autocompleteDebounceTimer = null;
        }

        _autocompleteAbort?.Cancel();
        _autocompleteAbort?.Dispose();
        _autocompleteAbort = null;
    }

    private void ClearAutocompleteUi()
    {
        _autocompleteState = null;
        _autocompleteList = null;
        _autocompletePrefix = "";
    }

    private void CancelAutocomplete()
    {
        CancelAutocompleteRequest();
        ClearAutocompleteUi();
    }

    public bool IsShowingAutocomplete() => _autocompleteState is not null;

    private void UpdateAutocomplete()
    {
        if (_autocompleteState is null || _autocompleteProvider is null)
        {
            return;
        }

        RequestAutocomplete(new AutocompleteRequestKind(_autocompleteState == AutocompleteStateKind.Force, false));
    }
}
