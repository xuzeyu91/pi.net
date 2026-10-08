using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Pi.Tui.Components;

namespace Pi.Tui;

/// <summary>Result of a clipboard write: success, or a message to flash on failure.</summary>
/// <remarks>
/// TS models this as <c>Promise&lt;boolean | string&gt;</c>: <c>true</c> on success, a message to show on
/// failure, or <c>false</c> for a generic error. <see cref="Success"/> maps to <c>true</c>;
/// <see cref="Message"/> carries the string form when one was returned.
/// </remarks>
public readonly record struct ClipboardCopyResult(bool Success, string? Message)
{
    public static ClipboardCopyResult Ok => new(true, null);

    public static ClipboardCopyResult Fail(string? message = null) => new(false, message);
}

/// <summary>Options for <see cref="TuiAltScreen"/> (port of <c>TuiAltScreenOptions</c>).</summary>
public sealed class TuiAltScreenOptions
{
    /// <summary>
    /// Logical lines moved for each mouse-wheel event (default: 1). <see cref="WheelScrollLines.Auto"/>
    /// accelerates fast wheel spins on terminals that send one event per notch. Alt+wheel moves five
    /// times as far.
    /// </summary>
    public WheelScrollLines? WheelScrollLines { get; init; }

    /// <summary>Capture mouse events for viewport scrolling and application-owned text selection.</summary>
    public bool? Mouse { get; init; }

    /// <summary>Style a non-current transcript search match.</summary>
    public Func<string, string>? SearchMatchStyle { get; init; }

    /// <summary>Style the current transcript search match.</summary>
    public Func<string, string>? SearchCurrentMatchStyle { get; init; }

    /// <summary>Style a transcript search navigation button.</summary>
    public Func<string, bool, string>? SearchNavigationButtonStyle { get; init; }

    /// <summary>
    /// Render a clickable jump-to-end label. It is centered on the last row of a follow-end primary
    /// scroll view while that view is scrolled away from its end.
    /// </summary>
    public Func<string>? ScrollToEndIndicator { get; init; }

    /// <summary>Open an OSC 8 hyperlink activated with a primary-button click.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>Handle an unmodified secondary-button press for clipboard paste. Currently enabled on Windows only.</summary>
    public Action? OnRightClickPaste { get; init; }

    /// <summary>Automatically copy selected text to the clipboard on mouse release (default: true).</summary>
    public bool? CopyOnSelect { get; init; }

    /// <summary>
    /// Copy selected text to the system clipboard. Return <see cref="ClipboardCopyResult.Ok"/> on success,
    /// a message to display on failure, or a failed result without a message for a generic error. When
    /// omitted, the selection is copied via an OSC 52 write.
    /// </summary>
    public Func<string, Task<ClipboardCopyResult>>? CopySelection { get; init; }
}

/// <summary>
/// Alternate-screen TUI with a scrollable, application-owned viewport (port of <c>tui-alt-screen.ts</c>).
/// </summary>
/// <remarks>
/// Deviations from TS: <c>setInterval</c> for selection auto-scroll becomes a
/// <see cref="System.Threading.Timer"/> (Node's <c>unref()</c> has no .NET equivalent, but a
/// <see cref="System.Threading.Timer"/> likewise does not keep the process alive); <c>process.platform</c>
/// and <c>process.env</c> checks use <see cref="OperatingSystem"/> and
/// <see cref="Environment.GetEnvironmentVariable(string)"/>; the file-based debug logging of the TS base
/// class is not ported.
/// </remarks>
public sealed class TuiAltScreen : TuiBase, IViewportTui
{
    private const string EnterAltScreen = "\x1b[?1049h";
    private const string ExitAltScreen = "\x1b[?1049l";
    private const string DisableAutowrap = "\x1b[?7l";
    private const string EnableAutowrap = "\x1b[?7h";
    private const string EnableButtonMotionMouse = "\x1b[?1000h\x1b[?1002h\x1b[?1004h\x1b[?1006h";
    private const string EnableAllMotionMouse = "\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1004h\x1b[?1006h";
    private const string DisableMouse = "\x1b[?1006l\x1b[?1004l\x1b[?1003l\x1b[?1002l\x1b[?1000l";
    private const string FocusIn = "\x1b[I";
    private const string FocusOut = "\x1b[O";
    private const string BeginSynchronizedOutput = "\x1b[?2026h";
    private const string EndSynchronizedOutput = "\x1b[?2026l";
    private const int PageScrollOverlap = 4;
    private const int AltWheelScrollMultiplier = 5;
    private const int MaxCachedOffscreenKittyImages = 16;
    private const long MaxCachedOffscreenKittyTransmissionBytes = 32L * 1024 * 1024;
    private const long MaxCachedOffscreenKittyDecodedBytes = 64L * 1024 * 1024;
    private const int DoubleClickIntervalMs = 500;
    private const int CopyErrorFlashDurationMs = 5000;

    // Regular mode delegates double-click selection to the terminal emulator. Fullscreen owns mouse
    // selection, so mirror common terminal word-selection behavior by keeping paths and kebab-case
    // tokens whole.
    private static readonly HashSet<string> TerminalWordSelectionJoiners = new(StringComparer.Ordinal) { "/", "-" };

    private static readonly Regex Osc133ZonePrefix = new(@"^(?:\x1b\]133;[ABC](?:\x07|\x1b\\))+", RegexOptions.Compiled);
    private static readonly Regex Osc133PromptStart = new(@"^\x1b\]133;A(?:\x07|\x1b\\)", RegexOptions.Compiled);
    private static readonly Regex SgrMousePattern = new(@"^\x1b\[<([0-9]+);([0-9]+);([0-9]+)([Mm])$", RegexOptions.Compiled);
    private static readonly Regex SgrWheelPattern = new(@"^\x1b\[<([0-9]+);([0-9]+);([0-9]+)[Mm]$", RegexOptions.Compiled);
    private static readonly Regex SgrMouseSequencePattern = new(@"^\x1b\[<[0-9]+;[0-9]+;[0-9]+[Mm]$", RegexOptions.Compiled);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private string[] _previousScreen = [];
    private string[] _lastDocument = [];
    private int _previousScreenWidth;
    private int _previousScreenHeight;
    private IComponent? _layoutRoot;
    private LayoutFrame? _currentLayout;
    private readonly IComponent _implicitDocument;
    private readonly ScrollView _implicitScrollView;
    private readonly AltScreenFlashContainer _flashes;
    private bool _altScreenActive;
    private ImageProtocol _imageProtocol = ImageProtocol.None;
    private TerminalCapabilities? _savedCapabilities;
    private readonly KittyImageCache _uploadedKittyImages = new();
    private SelectionPoint? _selectionAnchor;
    private SelectionPoint? _selectionFocus;
    private SelectionGranularity _selectionGranularity = SelectionGranularity.Character;
    private SelectionRange? _selectionInitialRange;
    private ClickTarget? _lastClick;
    private (int X, int Y)? _selectionDragPointer;
    private int _selectionAutoScrollDirection;
    private Timer? _selectionAutoScrollTimer;
    private bool _selectionPressActive;
    private ScrollbarDrag? _scrollbarDrag;
    private ScrollView? _scrollbarHover;
    private ScrollToEndIndicatorRect? _scrollToEndIndicatorRect;
    private ActiveSearch? _activeSearch;
    private string? _pressedUrl;
    private bool _selectionDragged;
    private TuiMouseDispatchTarget? _mouseCapture;
    private TuiMouseDispatchTarget? _mousePressTarget;
    private (int X, int Y)? _mousePressPoint;
    private bool _mousePressMoved;
    private ComponentClick? _lastComponentClick;
    private readonly WheelScrollAccelerator _wheelScroll;
    private readonly bool _mouseEnabled;
    private readonly Func<string, string> _searchMatchStyle;
    private readonly Func<string, string> _searchCurrentMatchStyle;
    private readonly Func<string, bool, string> _searchNavigationButtonStyle;
    private readonly Func<string>? _scrollToEndIndicator;
    private readonly Action<string>? _openUrl;
    private readonly Action? _onRightClickPaste;
    private bool _copyOnSelect;
    private readonly Func<string, Task<ClipboardCopyResult>>? _copySelection;

    public TuiAltScreen(ITerminal terminal, bool? showHardwareCursor = null, TuiAltScreenOptions? options = null)
        : base(terminal, showHardwareCursor)
    {
        options ??= new TuiAltScreenOptions();
        _implicitDocument = new ImplicitDocument(this);
        _implicitScrollView = new ScrollView(_implicitDocument, new ScrollViewOptions { Follow = "end", Primary = true });
        _flashes = new AltScreenFlashContainer(() => RequestRender());
        _wheelScroll = new WheelScrollAccelerator(options.WheelScrollLines ?? (WheelScrollLines)1);
        _mouseEnabled = options.Mouse ?? true;
        _searchMatchStyle = options.SearchMatchStyle ?? (text => $"\x1b[4m{text}\x1b[24m");
        _searchCurrentMatchStyle = options.SearchCurrentMatchStyle ?? (text => $"\x1b[1;7m{text}\x1b[22;27m");
        _searchNavigationButtonStyle = options.SearchNavigationButtonStyle ?? ((text, _) => text);
        _scrollToEndIndicator = options.ScrollToEndIndicator;
        _openUrl = options.OpenUrl;
        _onRightClickPaste = options.OnRightClickPaste;
        _copyOnSelect = options.CopyOnSelect ?? true;
        _copySelection = options.CopySelection;
        AddInputListener(data =>
        {
            var consume = HandleViewportInput(data);
            return consume is null ? null : new TuiInputListenerResult { Consume = consume.Value };
        });
    }

    public override TuiMode Mode => TuiMode.Fullscreen;

    /// <summary>Top content line currently shown at the top of the viewport.</summary>
    public int ViewportTop => GetPrimaryScrollView().ScrollTop;

    /// <summary>Whether the viewport is pinned to the end of the output.</summary>
    public bool IsFollowingOutput => GetPrimaryScrollView().IsFollowingEnd;

    public void SetWheelScrollLines(WheelScrollLines lines) => _wheelScroll.Lines = lines;

    public bool GetCopyOnSelect() => _copyOnSelect;

    public void SetCopyOnSelect(bool enabled) => _copyOnSelect = enabled;

    /// <summary>Whether the fullscreen viewport has a non-empty active text selection.</summary>
    public bool HasActiveSelection() => GetActiveSelectionText() is not null;

    /// <summary>Copy the active fullscreen text selection, if any, using the configured selection clipboard path.</summary>
    public Task<bool> CopyActiveSelectionToClipboardAsync()
    {
        var text = GetActiveSelectionText();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }
        return CopyTextToClipboardAsync(text);
    }

    /// <summary>Drop the text selection and multi-click history, e.g. before the host replaces the transcript.</summary>
    public void ResetTextSelection()
    {
        ClearTextSelection();
        _lastClick = null;
    }

    /// <summary>The lines of the last rendered frame, one per terminal row, as written to the terminal.</summary>
    public string[] GetScreenLines() => [.. _previousScreen];

    public void SetLayoutRoot(IComponent? component)
    {
        if (ReferenceEquals(_layoutRoot, component))
        {
            return;
        }
        _layoutRoot = component;
        _currentLayout = null;
        RequestRender();
    }

    public override string[] Render(int width) => _layoutRoot?.Render(width) ?? base.Render(width);

    protected override IReadOnlyList<IComponent> GetMountedRoots() =>
        _layoutRoot is not null ? [_layoutRoot] : Children;

    private ScrollView GetPrimaryScrollView() => _currentLayout?.PrimaryScrollView ?? _implicitScrollView;

    protected override void BeforeTerminalStart()
    {
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        StopScrollbarHover();
        StopScrollbarDrag();
        _flashes.Dispose();
        _altScreenActive = true;
        var capabilities = TerminalImage.GetCapabilities();
        _imageProtocol = capabilities.Images;
        _uploadedKittyImages.Clear();
        if (capabilities.Images == ImageProtocol.Iterm2)
        {
            _savedCapabilities = capabilities;
            TerminalImage.SetCapabilities(capabilities with { Images = ImageProtocol.None });
            Invalidate();
        }
        _lastDocument = [];
        _selectionAnchor = null;
        _selectionFocus = null;
        _selectionGranularity = SelectionGranularity.Character;
        _selectionInitialRange = null;
        _lastClick = null;
        _pressedUrl = null;
        _selectionDragged = false;
        ClearComponentMouseGesture();
        _lastComponentClick = null;
        ResetRenderState();
        var term = Environment.GetEnvironmentVariable("TERM")?.ToLowerInvariant() ?? "";
        // Multiplexers can lag when every pointer movement is forwarded. Button-motion tracking
        // preserves clicks, wheel events, selections, and scrollbar dragging.
        var mouseSequence =
            Environment.GetEnvironmentVariable("TMUX") is not null
            || Environment.GetEnvironmentVariable("ZELLIJ") is not null
            || Environment.GetEnvironmentVariable("STY") is not null
            || term.StartsWith("tmux", StringComparison.Ordinal)
            || term.StartsWith("screen", StringComparison.Ordinal)
                ? EnableButtonMotionMouse
                : EnableAllMotionMouse;
        Terminal.Write(
            $"{EnterAltScreen}{DisableAutowrap}{(_mouseEnabled ? mouseSequence : "")}\x1b[2J\x1b[H\x1b[?25l");
    }

    protected override void BeforeTerminalStop(TuiStopOptions options)
    {
        CloseSearch();
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        StopScrollbarHover();
        StopScrollbarDrag();
        ClearComponentMouseGesture();
        _flashes.Dispose();
        if (!_altScreenActive)
        {
            return;
        }
        Terminal.Write(
            $"{BeginSynchronizedOutput}{DeleteKittyImages()}{(_mouseEnabled ? DisableMouse : "")}{EnableAutowrap}{EndSynchronizedOutput}");
        _uploadedKittyImages.Clear();
    }

    protected override void AfterTerminalStop(TuiStopOptions options)
    {
        if (!_altScreenActive)
        {
            return;
        }
        _altScreenActive = false;
        if (options.PreserveScreen)
        {
            Terminal.Write($"{BeginSynchronizedOutput}{ExitAltScreen}\x1b[?25h{EndSynchronizedOutput}");
        }
        else
        {
            var width = Math.Max(1, Terminal.Columns);
            var documentLines = Render(width).Select(line => Osc133ZonePrefix.Replace(line, "")).ToArray();
            _lastDocument = ApplyLineResets(documentLines.Select(line => line.Replace(Tui.CursorMarker, "", StringComparison.Ordinal)).ToArray())
                .Select(line => TerminalImage.IsImageLine(line) || UnicodeWidth.VisibleWidth(line) <= width
                    ? line
                    : TextLayout.SliceByColumn(line, 0, width, strict: true))
                .ToArray();
            var buffer = $"{BeginSynchronizedOutput}{ExitAltScreen}{DisableAutowrap}";
            for (var row = 0; row < _lastDocument.Length; row++)
            {
                if (row > 0)
                {
                    buffer += "\r\n";
                }
                buffer += $"\r\x1b[2K{_lastDocument[row]}";
            }
            buffer += $"\x1b[0m{EnableAutowrap}\r\n\x1b[?25h{EndSynchronizedOutput}";
            Terminal.Write(buffer);
        }
        if (_savedCapabilities is not null)
        {
            TerminalImage.SetCapabilities(_savedCapabilities);
            _savedCapabilities = null;
        }
    }

    private string DeleteKittyImages() => _imageProtocol == ImageProtocol.Kitty ? TerminalImage.DeleteAllKittyImages() : "";

    private (string[] Lines, string EvictedImageDeletion) PrepareKittyScreen(string[] screen)
    {
        var visibleImageIds = new HashSet<long>();
        var lines = new string[screen.Length];
        for (var index = 0; index < screen.Length; index++)
        {
            var line = screen[index];
            var placement = TerminalImage.GetKittyImagePlacement(line);
            if (placement is null)
            {
                lines[index] = line;
                continue;
            }
            visibleImageIds.Add(placement.ImageId);

            var cachedImage = _uploadedKittyImages.TryGet(placement.ImageId);
            var nextCachedImage = new CachedKittyImage
            {
                TransmissionGeneration = placement.TransmissionGeneration,
                TransmissionBytes = placement.TransmissionBytes,
                EstimatedDecodedBytes = placement.EstimatedDecodedBytes,
            };
            // JS Map semantics: re-inserting moves the key to the end of the iteration order.
            _uploadedKittyImages.Set(placement.ImageId, nextCachedImage);

            lines[index] = cachedImage is not null && cachedImage.TransmissionGeneration == placement.TransmissionGeneration
                ? placement.ReplacementLine
                : line;
        }

        var cachedOffscreenImageCount = 0;
        var cachedOffscreenTransmissionBytes = 0L;
        var cachedOffscreenDecodedBytes = 0L;
        foreach (var (imageId, cachedImage) in _uploadedKittyImages.Entries)
        {
            if (visibleImageIds.Contains(imageId))
            {
                continue;
            }
            cachedOffscreenImageCount += 1;
            cachedOffscreenTransmissionBytes += cachedImage.TransmissionBytes;
            cachedOffscreenDecodedBytes += cachedImage.EstimatedDecodedBytes;
        }

        var evictedImageDeletion = "";
        // Snapshot so the eviction pass can remove entries while walking them; only removals happen here,
        // so the remaining order is unchanged and matches the JS Map iteration.
        foreach (var (imageId, cachedImage) in _uploadedKittyImages.Snapshot())
        {
            if (cachedOffscreenImageCount <= MaxCachedOffscreenKittyImages
                && cachedOffscreenTransmissionBytes <= MaxCachedOffscreenKittyTransmissionBytes
                && cachedOffscreenDecodedBytes <= MaxCachedOffscreenKittyDecodedBytes)
            {
                break;
            }
            if (visibleImageIds.Contains(imageId))
            {
                continue;
            }
            evictedImageDeletion += TerminalImage.DeleteKittyImage(imageId);
            _uploadedKittyImages.Remove(imageId);
            cachedOffscreenImageCount -= 1;
            cachedOffscreenTransmissionBytes -= cachedImage.TransmissionBytes;
            cachedOffscreenDecodedBytes -= cachedImage.EstimatedDecodedBytes;
        }
        return (lines, evictedImageDeletion);
    }

    protected override void ResetRenderState()
    {
        _previousScreen = [];
        _previousScreenWidth = 0;
        _previousScreenHeight = 0;
        _currentLayout = null;
    }

    public void ScrollBy(int lines)
    {
        GetPrimaryScrollView().ScrollBy(lines);
        RequestRender();
    }

    public void ScrollToTop()
    {
        GetPrimaryScrollView().ScrollToStart();
        RequestRender();
    }

    public void ScrollToBottom()
    {
        GetPrimaryScrollView().ScrollToEnd();
        RequestRender();
    }

    private void ScrollToPrompt(int direction)
    {
        if (_currentLayout is null)
        {
            return;
        }
        var scrollView = GetPrimaryScrollView();
        var lines = Layout.GetScrollViewBox(_currentLayout, scrollView)?.ScrollContentLines;
        if (lines is null)
        {
            return;
        }

        for (var row = scrollView.ScrollTop + direction; row >= 0 && row < lines.Length; row += direction)
        {
            if (!Osc133PromptStart.IsMatch(lines[row]))
            {
                continue;
            }
            scrollView.ScrollTo(row);
            RequestRender();
            return;
        }
    }

    private void ToggleSearch()
    {
        if (_activeSearch is not null)
        {
            CloseSearch();
            return;
        }
        var component = new AltScreenSearchComponent(UpdateSearchQuery, _searchNavigationButtonStyle);
        var search = new ActiveSearch
        {
            Component = component,
            Index = new AltScreenSearchIndex(),
            Query = "",
            Matches = [],
            SelectedIndex = -1,
            AnchorRow = GetPrimaryScrollView().ScrollTop,
            SelectionMode = SearchSelectionMode.Query,
        };
        _activeSearch = search;
        search.Overlay = ShowOverlay(component, new OverlayOptions
        {
            Anchor = OverlayAnchor.TopRight,
            Width = "40%",
            MinWidth = 32,
            Margin = 1,
        });
    }

    private void CloseSearch()
    {
        var search = _activeSearch;
        if (search is null)
        {
            return;
        }
        _activeSearch = null;
        search.Overlay?.Hide();
        RequestRender();
    }

    private void UpdateSearchQuery(string query)
    {
        var search = _activeSearch;
        if (search is null || query == search.Query)
        {
            return;
        }
        var selected = search.SelectedIndex >= 0 && search.SelectedIndex < search.Matches.Count
            ? search.Matches[search.SelectedIndex]
            : null;
        search.AnchorRow = selected is { Segments.Count: > 0 } ? selected.Segments[0].Row : GetPrimaryScrollView().ScrollTop;
        search.Query = query;
        search.SelectionMode = SearchSelectionMode.Query;
        search.Component.SetResult(-1, 0);
        RequestRender();
    }

    private void NavigateSearch(int direction)
    {
        var search = _activeSearch;
        if (string.IsNullOrEmpty(search?.Query))
        {
            return;
        }
        search.SelectionMode = direction < 0 ? SearchSelectionMode.Previous : SearchSelectionMode.Next;
        RequestRender();
    }

    private int? GetSearchNavigationDirectionAt(int x, int y)
    {
        var search = _activeSearch;
        var bounds = search?.Overlay?.GetBounds();
        if (search is null || bounds is null)
        {
            return null;
        }
        if (x < bounds.Col || x >= bounds.Col + bounds.Width || y < bounds.Row || y >= bounds.Row + bounds.Height)
        {
            return null;
        }
        return search.Component.GetNavigationDirectionAt(y - bounds.Row, x - bounds.Col);
    }

    private bool HandleSearchMouseEvent(SgrMouseEvent @event)
    {
        var search = _activeSearch;
        if (search is null)
        {
            return false;
        }
        var direction = GetSearchNavigationDirectionAt(@event.X, @event.Y);
        if (search.Component.SetHoveredNavigationDirection(direction))
        {
            RequestRender();
        }
        if (direction is null || @event.Release || (@event.Button & 32) != 0 || (@event.Button & 3) != 0)
        {
            return false;
        }
        NavigateSearch(direction.Value);
        return true;
    }

    private bool RefreshSearch(LayoutFrame layout)
    {
        var search = _activeSearch;
        if (search is null)
        {
            return false;
        }
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        var box = Layout.GetScrollViewBox(layout, scrollView);
        var lines = box?.ScrollContentLines;
        if (lines is null || string.IsNullOrWhiteSpace(search.Query))
        {
            search.Matches = [];
            search.SelectedIndex = -1;
            search.SelectedKey = null;
            search.SelectionMode = SearchSelectionMode.Retain;
            search.Component.SetResult(-1, 0);
            return false;
        }

        var shouldRevealSelection = search.SelectionMode != SearchSelectionMode.Retain;
        var result = search.Index.Search(lines, search.Query);
        var matches = result.Matches;
        search.Matches = matches;
        if (!result.Changed && search.SelectionMode == SearchSelectionMode.Retain)
        {
            return false;
        }

        var exactIndex = result.Changed
            ? search.SelectedKey is null
                ? -1
                : matches.FindIndex(match => AltScreenSearchIndex.GetMatchKey(match) == search.SelectedKey)
            : search.SelectedIndex;
        var selectedIndex = -1;
        if (matches.Count > 0)
        {
            if (search.SelectionMode == SearchSelectionMode.Query)
            {
                var low = 0;
                var high = matches.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if ((matches[middle].Segments.Count > 0 ? matches[middle].Segments[0].Row : 0) < search.AnchorRow)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }
                selectedIndex = low < matches.Count ? low : 0;
            }
            else if (search.SelectionMode == SearchSelectionMode.Next)
            {
                var baseIndex = exactIndex >= 0 ? exactIndex : Math.Min(search.SelectedIndex, matches.Count - 1);
                selectedIndex = baseIndex < 0 ? 0 : (baseIndex + 1) % matches.Count;
            }
            else if (search.SelectionMode == SearchSelectionMode.Previous)
            {
                var baseIndex = exactIndex >= 0 ? exactIndex : Math.Min(search.SelectedIndex, matches.Count - 1);
                selectedIndex = baseIndex < 0 ? matches.Count - 1 : (baseIndex - 1 + matches.Count) % matches.Count;
            }
            else
            {
                selectedIndex = exactIndex >= 0 ? exactIndex : Math.Min(Math.Max(0, search.SelectedIndex), matches.Count - 1);
            }
        }

        search.SelectedIndex = selectedIndex;
        search.SelectedKey = selectedIndex >= 0 ? AltScreenSearchIndex.GetMatchKey(matches[selectedIndex]) : null;
        search.SelectionMode = SearchSelectionMode.Retain;
        search.Component.SetResult(selectedIndex, matches.Count);
        if (!shouldRevealSelection)
        {
            return false;
        }

        var selectedMatch = selectedIndex >= 0 && selectedIndex < matches.Count ? matches[selectedIndex] : null;
        var firstSegment = selectedMatch is { Segments.Count: > 0 } ? selectedMatch.Segments[0] : null;
        var lastSegment = selectedMatch is { Segments.Count: > 0 } ? selectedMatch.Segments[^1] : null;
        if (box is null || firstSegment is null || lastSegment is null || scrollView.ViewportHeight <= 0)
        {
            return false;
        }
        var before = scrollView.ScrollTop;
        var visibleBottom = before + scrollView.ViewportHeight - 1;
        var target = before;
        if (firstSegment.Row < before || lastSegment.Row > visibleBottom)
        {
            target = firstSegment.Row - scrollView.ViewportHeight / 3;
        }
        scrollView.ScrollTo(target, new ScrollViewScrollToOptions { DisableFollow = true });
        return scrollView.ScrollTop != before;
    }

    /// <summary>Show a transient message in the alternate-screen flash stack.</summary>
    public void Flash(string message, int? durationMs = null)
    {
        _flashes.Flash(message, durationMs ?? 1000);
    }

    private bool ShouldDeferViewportInputToOverlay() =>
        IsOverlayFocused() && _activeSearch?.Overlay?.IsFocused() != true;

    private void ClearComponentMouseGesture()
    {
        _mouseCapture = null;
        _mousePressTarget = null;
        _mousePressPoint = null;
        _mousePressMoved = false;
    }

    /// <summary>Handle raw terminal input; returns whether the event was consumed (null = not consumed).</summary>
    private bool? HandleViewportInput(string data)
    {
        if (data == FocusOut)
        {
            var hadActiveSelection = _selectionPressActive;
            var hadNonEmptyActiveSelection = hadActiveSelection && GetSelectionBounds() is not null;
            _selectionPressActive = false;
            StopSelectionAutoScroll();
            StopScrollbarHover();
            if (_activeSearch?.Component.SetHoveredNavigationDirection(null) == true)
            {
                RequestRender();
            }
            StopScrollbarDrag();
            _pressedUrl = null;
            _selectionDragged = false;
            ClearComponentMouseGesture();
            _lastComponentClick = null;
            if (hadActiveSelection)
            {
                _selectionAnchor = null;
                _selectionFocus = null;
                _selectionGranularity = SelectionGranularity.Character;
                _selectionInitialRange = null;
                if (hadNonEmptyActiveSelection)
                {
                    RequestRender();
                }
            }
            _lastClick = null;
            return true;
        }
        if (data == FocusIn)
        {
            return true;
        }

        var wheelEvent = ParseWheelEvent(data);
        if (wheelEvent is not null)
        {
            var lines = _wheelScroll.Next(wheelEvent.Direction, NowMs());
            // SGR mouse button codes use bit 3 (value 8) for the Alt modifier.
            var wheelDelta = wheelEvent.Direction * ((wheelEvent.Button & 8) != 0 ? lines * AltWheelScrollMultiplier : lines);
            var @event = CreateMouseEvent(TuiMouseEventType.Wheel, wheelEvent.Button, wheelEvent.X, wheelEvent.Y, wheelDelta: wheelDelta);
            var overlay = DispatchMouseToOverlay(@event);
            var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(@event));
            if (result is not null)
            {
                if (ApplyMouseDispatchResult(@event, result))
                {
                    RequestRender();
                }
                return true;
            }
            if (ShouldDeferViewportInputToOverlay())
            {
                return null;
            }
            RouteWheel(wheelEvent, wheelDelta);
            return true;
        }
        var mouseEvent = ParseSgrMouseEvent(data);
        if (mouseEvent is not null)
        {
            HandleMouseEvent(mouseEvent);
            return true;
        }
        if (IsMouseSequence(data))
        {
            return true;
        }

        var keybindings = GlobalKeybindings.Get();
        var isRelease = Keys.IsKeyRelease(data);
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenSearch))
        {
            if (!isRelease)
            {
                ToggleSearch();
            }
            return true;
        }
        if (_activeSearch?.Overlay?.IsFocused() == true)
        {
            if (keybindings.Matches(data, TuiKeybindingIds.AltScreenSearchNext))
            {
                if (!isRelease)
                {
                    NavigateSearch(1);
                }
                return true;
            }
            if (keybindings.Matches(data, TuiKeybindingIds.AltScreenSearchPrevious))
            {
                if (!isRelease)
                {
                    NavigateSearch(-1);
                }
                return true;
            }
            if (keybindings.Matches(data, TuiKeybindingIds.AltScreenSearchClose))
            {
                if (!isRelease)
                {
                    CloseSearch();
                }
                return true;
            }
        }
        if (ShouldDeferViewportInputToOverlay())
        {
            return null;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenPageUp))
        {
            if (!isRelease)
            {
                ScrollBy(-Math.Max(1, GetPrimaryScrollView().ViewportHeight - PageScrollOverlap));
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenPageDown))
        {
            if (!isRelease)
            {
                ScrollBy(Math.Max(1, GetPrimaryScrollView().ViewportHeight - PageScrollOverlap));
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenHalfPageUp))
        {
            if (!isRelease)
            {
                ScrollBy(-Math.Max(1, GetPrimaryScrollView().ViewportHeight / 2));
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenHalfPageDown))
        {
            if (!isRelease)
            {
                ScrollBy(Math.Max(1, GetPrimaryScrollView().ViewportHeight / 2));
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenLineUp))
        {
            if (!isRelease)
            {
                ScrollBy(-1);
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenLineDown))
        {
            if (!isRelease)
            {
                ScrollBy(1);
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenPreviousPrompt))
        {
            if (!isRelease)
            {
                ScrollToPrompt(-1);
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenNextPrompt))
        {
            if (!isRelease)
            {
                ScrollToPrompt(1);
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenTop))
        {
            if (!isRelease)
            {
                ScrollToTop();
            }
            return true;
        }
        if (keybindings.Matches(data, TuiKeybindingIds.AltScreenBottom))
        {
            if (!isRelease)
            {
                ScrollToBottom();
            }
            return true;
        }
        return null;
    }

    private static TuiMouseButton DecodeMouseButton(int button) => (button & 3) switch
    {
        0 => TuiMouseButton.Left,
        1 => TuiMouseButton.Middle,
        2 => TuiMouseButton.Right,
        _ => TuiMouseButton.None,
    };

    private TuiMouseEvent CreateMouseEvent(
        TuiMouseEventType type,
        int button,
        int x,
        int y,
        int? wheelDelta = null,
        int? clickCount = null)
    {
        return new TuiMouseEvent
        {
            Type = type,
            Button = type == TuiMouseEventType.Wheel ? TuiMouseButton.None : DecodeMouseButton(button),
            X = x,
            Y = y,
            ScreenX = x,
            ScreenY = y,
            Width = Math.Max(1, Terminal.Columns),
            Height = Math.Max(1, Terminal.Rows),
            Shift = (button & 4) != 0,
            Alt = (button & 8) != 0,
            Ctrl = (button & 16) != 0,
            WheelDelta = wheelDelta,
            ClickCount = clickCount,
        };
    }

    private TuiMouseDispatchResult? DispatchMouseToLayout(TuiMouseEvent @event)
    {
        if (_currentLayout is null)
        {
            return null;
        }
        var visited = new HashSet<IComponent>();
        foreach (var box in Layout.GetLayoutBoxesAt(_currentLayout, @event.ScreenX, @event.ScreenY))
        {
            if (visited.Contains(box.Component))
            {
                continue;
            }
            if (LayoutNodes.GetLayoutNode(box.Component) is not null && UsesDefaultHandleMouse(box.Component))
            {
                continue;
            }
            visited.Add(box.Component);
            var local = @event.Clone();
            local.X = @event.ScreenX - box.Rect.X;
            local.Y = @event.ScreenY - box.Rect.Y;
            local.Width = box.Rect.Width;
            local.Height = box.Rect.Height;
            var result = MouseDispatch.Dispatch(box.Component, local);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// TS compares <c>component.handleMouse === Container.prototype.handleMouse</c>: only a
    /// <see cref="Container"/> that does not override the base implementation counts as a pass-through.
    /// </summary>
    private static bool UsesDefaultHandleMouse(IComponent component)
    {
        if (component is not Container)
        {
            return false;
        }
        var method = component.GetType().GetMethod(nameof(IComponent.HandleMouse), [typeof(TuiMouseEvent)]);
        return method is not null && method.DeclaringType == typeof(Container);
    }

    private bool ApplyMouseDispatchResult(TuiMouseEvent @event, TuiMouseDispatchResult result)
    {
        var focusTarget = ResolveMouseFocusTarget(result.FocusTarget ?? result.Target.Component);
        var focusChanged = result.Focus == true && !ReferenceEquals(GetFocusedComponent(), focusTarget);
        if (result.Focus == true)
        {
            SetFocus(focusTarget);
        }
        if (result.Capture == true)
        {
            _mouseCapture = result.Target;
        }
        return result.Render ?? (focusChanged
            || @event.Type is TuiMouseEventType.Press
            || @event.Type is TuiMouseEventType.Click
            || @event.Type is TuiMouseEventType.Drag
            || @event.Type is TuiMouseEventType.Wheel);
    }

    private static TuiMouseDispatchResult? DispatchMouseToTarget(TuiMouseEvent @event, TuiMouseDispatchTarget target) =>
        MouseDispatch.Dispatch(target.Component, MouseDispatch.Retarget(@event, target));

    private int GetComponentClickCount(TuiMouseDispatchTarget target, int x, int y)
    {
        var now = NowUnixMs();
        var previous = _lastComponentClick;
        var count =
            previous is not null
            && now - previous.Timestamp <= DoubleClickIntervalMs
            && ReferenceEquals(previous.Component, target.Component)
            && previous.X == x
            && previous.Y == y
                ? (previous.Count % 3) + 1
                : 1;
        _lastComponentClick = new ComponentClick
        {
            Timestamp = now,
            Count = count,
            Component = target.Component,
            X = x,
            Y = y,
        };
        return count;
    }

    private void ClearTextSelection()
    {
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        _selectionAnchor = null;
        _selectionFocus = null;
        _selectionGranularity = SelectionGranularity.Character;
        _selectionInitialRange = null;
        _pressedUrl = null;
        _selectionDragged = false;
    }

    private void HandleMouseEvent(SgrMouseEvent raw)
    {
        var isMotion = (raw.Button & 32) != 0;
        var type = raw.Release
            ? TuiMouseEventType.Release
            : isMotion
                ? DecodeMouseButton(raw.Button) == TuiMouseButton.None
                    ? TuiMouseEventType.Move
                    : TuiMouseEventType.Drag
                : TuiMouseEventType.Press;
        var @event = CreateMouseEvent(type, raw.Button, raw.X, raw.Y);

        if (_mouseCapture is not null || _mousePressTarget is not null)
        {
            var target = _mouseCapture ?? _mousePressTarget!;
            if (_mousePressPoint is { } pressPoint && (raw.X != pressPoint.X || raw.Y != pressPoint.Y))
            {
                _mousePressMoved = true;
                _lastComponentClick = null;
            }
            var render = false;
            var targetResult = DispatchMouseToTarget(@event, target);
            if (targetResult is not null)
            {
                render = ApplyMouseDispatchResult(@event, targetResult);
            }
            if (raw.Release)
            {
                if (!_mousePressMoved && _mousePressPoint is { } releasePoint && releasePoint.X == raw.X && releasePoint.Y == raw.Y)
                {
                    var clickEvent = CreateMouseEvent(
                        TuiMouseEventType.Click, raw.Button, raw.X, raw.Y, clickCount: GetComponentClickCount(target, raw.X, raw.Y));
                    var clickResult = DispatchMouseToTarget(clickEvent, target);
                    if (clickResult is not null)
                    {
                        render = ApplyMouseDispatchResult(clickEvent, clickResult) || render;
                    }
                }
                ClearComponentMouseGesture();
            }
            if (render)
            {
                RequestRender();
            }
            return;
        }

        if (HandleSearchMouseEvent(raw))
        {
            return;
        }

        var overlay = DispatchMouseToOverlay(@event);
        if (!overlay.Hit)
        {
            if (HandleScrollToEndIndicatorMouseEvent(raw))
            {
                return;
            }
            var scrollbarHandled = HandleScrollbarMouseEvent(raw);
            if (_scrollbarDrag is null)
            {
                UpdateScrollbarHover(raw.X, raw.Y);
            }
            if (scrollbarHandled)
            {
                return;
            }
        }
        else
        {
            StopScrollbarHover();
        }

        var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(@event));
        if (result is not null)
        {
            var render = ApplyMouseDispatchResult(@event, result);
            if (type == TuiMouseEventType.Press)
            {
                ClearTextSelection();
                _mousePressTarget = result.Target;
                _mousePressPoint = (raw.X, raw.Y);
                _mousePressMoved = false;
            }
            if (render)
            {
                RequestRender();
            }
            return;
        }

        if (HandleRightClickPaste(raw))
        {
            return;
        }
        HandleSelectionMouseEvent(raw);
    }

    private static WheelEvent? ParseWheelEvent(string data)
    {
        var sgr = SgrWheelPattern.Match(data);
        if (sgr.Success)
        {
            var button = int.Parse(sgr.Groups[1].Value, CultureInfo.InvariantCulture);
            if ((button & 64) == 0)
            {
                return null;
            }
            var direction = button & 3;
            if (direction != 0 && direction != 1)
            {
                return null;
            }
            return new WheelEvent
            {
                Direction = direction == 0 ? -1 : 1,
                X = int.Parse(sgr.Groups[2].Value, CultureInfo.InvariantCulture) - 1,
                Y = int.Parse(sgr.Groups[3].Value, CultureInfo.InvariantCulture) - 1,
                Button = button,
            };
        }
        if (data.Length == 6 && data.StartsWith("\x1b[M", StringComparison.Ordinal))
        {
            var button = data[3] - 32;
            if ((button & 64) == 0)
            {
                return null;
            }
            var direction = button & 3;
            if (direction != 0 && direction != 1)
            {
                return null;
            }
            return new WheelEvent
            {
                Direction = direction == 0 ? -1 : 1,
                X = data[4] - 33,
                Y = data[5] - 33,
                Button = button,
            };
        }
        return null;
    }

    private void RouteWheel(WheelEvent @event, int delta)
    {
        var remaining = delta;
        var seen = new HashSet<ScrollView>();
        foreach (var scrollView in _currentLayout is not null ? Layout.GetScrollViewsAt(_currentLayout, @event.X, @event.Y) : [])
        {
            seen.Add(scrollView);
            remaining = scrollView.ScrollBy(remaining);
            if (remaining == 0 || scrollView.Overscroll == ScrollOverscroll.Contain)
            {
                break;
            }
        }
        var primary = GetPrimaryScrollView();
        if (remaining != 0 && !seen.Contains(primary))
        {
            primary.ScrollBy(remaining);
        }
        UpdateScrollbarHover(@event.X, @event.Y);
        RequestRender();
    }

    private static SgrMouseEvent? ParseSgrMouseEvent(string data)
    {
        var match = SgrMousePattern.Match(data);
        if (!match.Success)
        {
            return null;
        }
        return new SgrMouseEvent
        {
            Button = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            X = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1,
            Y = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) - 1,
            Release = match.Groups[4].Value == "m",
        };
    }

    private bool HandleRightClickPaste(SgrMouseEvent @event)
    {
        if (_onRightClickPaste is null
            || !OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("TERM_PROGRAM")?.ToLowerInvariant() == "vscode"
            || @event.Release
            || @event.Button != 2)
        {
            return false;
        }
        try
        {
            _onRightClickPaste();
        }
        catch
        {
            // Clipboard paste is best-effort.
        }
        return true;
    }

    private bool HandleScrollToEndIndicatorMouseEvent(SgrMouseEvent @event)
    {
        var rect = _scrollToEndIndicatorRect;
        if (rect is null || @event.Release || (@event.Button & 32) != 0 || (@event.Button & 3) != 0)
        {
            return false;
        }
        if (@event.Y != rect.Row || @event.X < rect.Column || @event.X >= rect.Column + rect.Width)
        {
            return false;
        }
        ScrollToBottom();
        return true;
    }

    private ScrollbarTarget? GetScrollbarTargetAt(int x, int y, bool includeHiddenAuto = false)
    {
        if (HasOverlay() || _currentLayout is null)
        {
            return null;
        }
        foreach (var scrollView in Layout.GetScrollViewsAt(_currentLayout, x, y))
        {
            var box = Layout.GetScrollViewBox(_currentLayout, scrollView);
            var geometry = box is not null ? Layout.GetScrollbarGeometry(box, includeHiddenAuto) : null;
            if (geometry is { } geo
                && x == geo.Column
                && y >= geo.TrackTop
                && y < geo.TrackTop + geo.TrackHeight)
            {
                return new ScrollbarTarget { ScrollView = scrollView, Geometry = geo };
            }
        }
        return null;
    }

    private void SetScrollbarHover(ScrollView? scrollView)
    {
        if (ReferenceEquals(scrollView, _scrollbarHover))
        {
            return;
        }
        _scrollbarHover?.SetScrollbarActive(false);
        _scrollbarHover = scrollView;
        _scrollbarHover?.SetScrollbarActive(true);
    }

    private void UpdateScrollbarHover(int x, int y) => SetScrollbarHover(GetScrollbarTargetAt(x, y, true)?.ScrollView);

    private void StopScrollbarHover() => SetScrollbarHover(null);

    private static void ScrollScrollbarToPointer(
        ScrollView scrollView,
        ScrollbarGeometry geometry,
        int pointerY,
        int grabOffset)
    {
        var maxThumbOffset = geometry.TrackHeight - geometry.ThumbHeight;
        var thumbOffset = Math.Max(0, Math.Min(maxThumbOffset, pointerY - geometry.TrackTop - grabOffset));
        var scrollTop = maxThumbOffset == 0
            ? 0
            : (int)JsMath.Round((double)thumbOffset / maxThumbOffset * geometry.MaxScrollTop);
        scrollView.ScrollTo(scrollTop);
    }

    private bool HandleScrollbarMouseEvent(SgrMouseEvent @event)
    {
        if (_scrollbarDrag is not null)
        {
            if (@event.Release)
            {
                StopScrollbarDrag();
                return true;
            }
            var box = _currentLayout is not null
                ? Layout.GetScrollViewBox(_currentLayout, _scrollbarDrag.ScrollView)
                : null;
            var geometry = box is not null ? Layout.GetScrollbarGeometry(box) : null;
            if (geometry is { } geo)
            {
                ScrollScrollbarToPointer(_scrollbarDrag.ScrollView, geo, @event.Y, _scrollbarDrag.GrabOffset);
            }
            return true;
        }

        if (@event.Release || (@event.Button & 32) != 0 || (@event.Button & 3) != 0)
        {
            return false;
        }
        var target = GetScrollbarTargetAt(@event.X, @event.Y);
        if (target is null)
        {
            return false;
        }
        StopSelectionAutoScroll();
        _selectionPressActive = false;
        _selectionAnchor = null;
        _selectionFocus = null;
        _selectionGranularity = SelectionGranularity.Character;
        _selectionInitialRange = null;
        _lastClick = null;
        _pressedUrl = null;
        _selectionDragged = false;

        SetScrollbarHover(target.ScrollView);
        var onThumb = @event.Y >= target.Geometry.ThumbTop
            && @event.Y < target.Geometry.ThumbTop + target.Geometry.ThumbHeight;
        var grabOffset = onThumb
            ? @event.Y - target.Geometry.ThumbTop
            : target.Geometry.ThumbHeight / 2;
        if (!onThumb)
        {
            ScrollScrollbarToPointer(target.ScrollView, target.Geometry, @event.Y, grabOffset);
        }
        _scrollbarDrag = new ScrollbarDrag { ScrollView = target.ScrollView, GrabOffset = grabOffset };
        return true;
    }

    private void StopScrollbarDrag() => _scrollbarDrag = null;

    private SelectionPoint? GetScrollSelectionPoint(ScrollView scrollView, int x, int y)
    {
        if (_currentLayout is null)
        {
            return null;
        }
        var box = Layout.GetScrollViewBox(_currentLayout, scrollView);
        if (box is null || box.Rect.Height <= 0 || box.Clip.Height <= 0)
        {
            return null;
        }
        var visibleTop = Math.Max(Math.Max(0, box.Rect.Y), box.Clip.Y);
        var visibleBottom = Math.Min(
            Math.Min(Terminal.Rows - 1, box.Rect.Y + box.Rect.Height - 1),
            box.Clip.Y + box.Clip.Height - 1);
        if (visibleBottom < visibleTop)
        {
            return null;
        }
        var pointerRow = Math.Max(visibleTop, Math.Min(visibleBottom, y));
        var maxContentRow = Math.Max(0, (box.ScrollContentLines?.Length ?? 1) - 1);
        return new SelectionPoint
        {
            Row = Math.Max(0, Math.Min(maxContentRow, scrollView.ScrollTop + pointerRow - box.Rect.Y)),
            Col = Math.Max(0, Math.Min(box.Rect.Width - 1, x - box.Rect.X)),
            ScrollView = scrollView,
        };
    }

    private SelectionPoint GetSelectionPoint(SgrMouseEvent @event, ScrollView? scrollView = null)
    {
        if (scrollView is not null)
        {
            var point = GetScrollSelectionPoint(scrollView, @event.X, @event.Y);
            if (point is not null)
            {
                return point;
            }
        }
        return new SelectionPoint
        {
            Row = Math.Max(0, Math.Min(Terminal.Rows - 1, @event.Y)),
            Col = Math.Max(0, Math.Min(Terminal.Columns - 1, @event.X)),
        };
    }

    private string GetSelectionSourceLine(SelectionPoint point)
    {
        if (point.ScrollView is not null && _currentLayout is not null)
        {
            var lines = Layout.GetScrollViewBox(_currentLayout, point.ScrollView)?.ScrollContentLines;
            if (lines is not null)
            {
                return point.Row >= 0 && point.Row < lines.Length ? lines[point.Row] : "";
            }
        }
        return point.Row >= 0 && point.Row < _previousScreen.Length ? _previousScreen[point.Row] : "";
    }

    private SelectionRange? GetWordSelection(SelectionPoint point)
    {
        var line = Ansi.StripTerminalSequences(GetSelectionSourceLine(point));
        var segments = new List<(int Start, int End, bool Selectable, bool Joiner)>();
        var start = 0;
        foreach (var segment in WordNavigation.SegmentWords(line))
        {
            var end = start + UnicodeWidth.VisibleWidth(segment.Segment);
            var joiner = TerminalWordSelectionJoiners.Contains(segment.Segment);
            segments.Add((start, end, segment.IsWordLike || joiner, joiner));
            start = end;
        }
        var clickedSegmentIndex = segments.FindIndex(segment => point.Col >= segment.Start && point.Col < segment.End);
        if (clickedSegmentIndex < 0)
        {
            return null;
        }

        static bool CanJoin((int Start, int End, bool Selectable, bool Joiner) left, (int Start, int End, bool Selectable, bool Joiner) right) =>
            left.Selectable && right.Selectable && (left.Joiner || right.Joiner);
        var selectionStart = segments[clickedSegmentIndex].Start;
        var selectionEnd = segments[clickedSegmentIndex].End;
        for (var index = clickedSegmentIndex; index > 0 && CanJoin(segments[index - 1], segments[index]); index--)
        {
            selectionStart = segments[index - 1].Start;
        }
        for (var index = clickedSegmentIndex; index < segments.Count - 1 && CanJoin(segments[index], segments[index + 1]); index++)
        {
            selectionEnd = segments[index + 1].End;
        }
        return new SelectionRange
        {
            Start = point.With(col: selectionStart),
            End = point.With(col: selectionEnd, boundary: true),
        };
    }

    private SelectionRange GetLineSelection(SelectionPoint point) => new()
    {
        Start = point.With(col: 0),
        End = point.With(col: UnicodeWidth.VisibleWidth(GetSelectionSourceLine(point)), boundary: true),
    };

    private void UpdateSelectionFocus(SelectionPoint point)
    {
        if (_selectionGranularity == SelectionGranularity.Character || _selectionInitialRange is null)
        {
            _selectionFocus = point;
            return;
        }
        var range = _selectionGranularity == SelectionGranularity.Word
            ? GetWordSelection(point)
            : GetLineSelection(point);
        if (range is null)
        {
            return;
        }
        var initial = _selectionInitialRange;
        var targetBeforeInitial =
            range.Start.Row < initial.Start.Row
            || (range.Start.Row == initial.Start.Row && range.Start.Col < initial.Start.Col);
        if (targetBeforeInitial)
        {
            _selectionAnchor = initial.End;
            _selectionFocus = range.Start;
        }
        else
        {
            _selectionAnchor = initial.Start;
            _selectionFocus = range.End;
        }
    }

    private int GetClickCount(SelectionPoint point, SelectionRange? word)
    {
        var now = NowUnixMs();
        var previous = _lastClick;
        var count =
            word is not null
            && previous is not null
            && now - previous.Timestamp <= DoubleClickIntervalMs
            && previous.Row == point.Row
            && ReferenceEquals(previous.ScrollView, point.ScrollView)
            && previous.WordStart == word.Start.Col
            && previous.WordEnd == word.End.Col
                ? (previous.Count % 3) + 1
                : 1;
        _lastClick = word is not null
            ? new ClickTarget
            {
                Timestamp = now,
                Count = count,
                Row = point.Row,
                ScrollView = point.ScrollView,
                WordStart = word.Start.Col,
                WordEnd = word.End.Col,
            }
            : null;
        return count;
    }

    private void UpdateSelectionAutoScroll(SgrMouseEvent @event)
    {
        var scrollView = _selectionAnchor?.ScrollView;
        if (scrollView is null || _currentLayout is null)
        {
            StopSelectionAutoScroll();
            return;
        }
        var box = Layout.GetScrollViewBox(_currentLayout, scrollView);
        if (box is null || box.Rect.Height <= 0 || box.Clip.Height <= 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        var visibleTop = Math.Max(Math.Max(0, box.Rect.Y), box.Clip.Y);
        var visibleBottom = Math.Min(
            Math.Min(Terminal.Rows - 1, box.Rect.Y + box.Rect.Height - 1),
            box.Clip.Y + box.Clip.Height - 1);
        _selectionDragPointer = (@event.X, @event.Y);
        _selectionAutoScrollDirection = @event.Y <= visibleTop ? -1 : @event.Y >= visibleBottom ? 1 : 0;
        if (_selectionAutoScrollDirection == 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        if (_selectionAutoScrollTimer is not null)
        {
            return;
        }
        _selectionAutoScrollTimer = new Timer(_ => AutoScrollSelection(), null, 50, 50);
    }

    private void AutoScrollSelection()
    {
        var scrollView = _selectionAnchor?.ScrollView;
        var pointer = _selectionDragPointer;
        var direction = _selectionAutoScrollDirection;
        if (scrollView is null || pointer is null || direction == 0)
        {
            StopSelectionAutoScroll();
            return;
        }
        var remaining = scrollView.ScrollBy(direction);
        if (remaining == direction)
        {
            StopSelectionAutoScroll();
            return;
        }
        var point = GetScrollSelectionPoint(scrollView, pointer.Value.X, pointer.Value.Y);
        if (point is not null)
        {
            UpdateSelectionFocus(point);
        }
        RequestRender();
    }

    private void StopSelectionAutoScroll()
    {
        if (_selectionAutoScrollTimer is not null)
        {
            _selectionAutoScrollTimer.Dispose();
            _selectionAutoScrollTimer = null;
        }
        _selectionAutoScrollDirection = 0;
        _selectionDragPointer = null;
    }

    private void HandleSelectionMouseEvent(SgrMouseEvent @event)
    {
        var button = @event.Button & 3;
        if (button != 0 && !(@event.Release && button == 3))
        {
            return;
        }
        var anchorScrollView = _selectionAnchor?.ScrollView;
        var point = GetSelectionPoint(@event, anchorScrollView);
        if (@event.Release)
        {
            if (!_selectionPressActive)
            {
                return;
            }
            _selectionPressActive = false;
            StopSelectionAutoScroll();
            if (_selectionAnchor is null)
            {
                return;
            }
            UpdateSelectionFocus(point);
            var isClick =
                !_selectionDragged
                && ReferenceEquals(_selectionAnchor.ScrollView, point.ScrollView)
                && _selectionAnchor.Row == point.Row
                && _selectionAnchor.Col == point.Col;
            var clickedUrl = isClick ? _pressedUrl : null;
            _pressedUrl = null;
            if (clickedUrl is not null && _openUrl is not null)
            {
                _selectionAnchor = null;
                _selectionFocus = null;
                try
                {
                    _openUrl(clickedUrl);
                }
                catch
                {
                    // URL activation is best-effort.
                }
                RequestRender();
                return;
            }
            if (isClick)
            {
                var clickEvent = CreateMouseEvent(
                    TuiMouseEventType.Click, @event.Button, @event.X, @event.Y, clickCount: _lastClick?.Count ?? 1);
                var overlay = DispatchMouseToOverlay(clickEvent);
                var result = overlay.Result ?? (overlay.Hit ? null : DispatchMouseToLayout(clickEvent));
                if (result is not null)
                {
                    var render = ApplyMouseDispatchResult(clickEvent, result);
                    ClearTextSelection();
                    if (render)
                    {
                        RequestRender();
                    }
                    return;
                }
            }
            if (_copyOnSelect)
            {
                _ = CopySelectionToClipboardAsync();
            }
            RequestRender();
            return;
        }
        if ((@event.Button & 32) != 0)
        {
            if (!_selectionPressActive || _selectionAnchor is null)
            {
                return;
            }
            _selectionDragged = true;
            _lastClick = null;
            _pressedUrl = null;
            UpdateSelectionFocus(point);
            UpdateSelectionAutoScroll(@event);
            RequestRender();
            return;
        }
        StopSelectionAutoScroll();
        _selectionPressActive = true;
        var scrollView =
            !HasOverlay() && _currentLayout is not null
                ? Layout.GetScrollViewsAt(_currentLayout, @event.X, @event.Y).FirstOrDefault()
                : null;
        var anchor = GetSelectionPoint(@event, scrollView);
        var word = GetWordSelection(anchor);
        var clickCount = GetClickCount(anchor, word);
        var range = clickCount == 2 ? word : clickCount == 3 ? GetLineSelection(anchor) : null;
        _selectionGranularity = range is not null
            ? clickCount == 2 ? SelectionGranularity.Word : SelectionGranularity.Line
            : SelectionGranularity.Character;
        _selectionInitialRange = range;
        _selectionAnchor = range?.Start ?? anchor;
        _selectionFocus = range?.End ?? anchor;
        _selectionDragged = false;
        _pressedUrl = range is not null
            ? null
            : UnicodeWidth.GetOsc8LinkAtColumn(
                _previousScreen.Length > Math.Max(0, Math.Min(Terminal.Rows - 1, @event.Y))
                    ? _previousScreen[Math.Max(0, Math.Min(Terminal.Rows - 1, @event.Y))]
                    : "",
                Math.Max(0, Math.Min(Terminal.Columns - 1, @event.X)));
        RequestRender();
    }

    private SelectionRange? GetSelectionBounds()
    {
        if (_selectionAnchor is null || _selectionFocus is null)
        {
            return null;
        }
        if (!ReferenceEquals(_selectionAnchor.ScrollView, _selectionFocus.ScrollView))
        {
            return null;
        }
        var anchorBeforeFocus =
            _selectionAnchor.Row < _selectionFocus.Row
            || (_selectionAnchor.Row == _selectionFocus.Row && _selectionAnchor.Col < _selectionFocus.Col);
        if (_selectionAnchor.Row == _selectionFocus.Row && _selectionAnchor.Col == _selectionFocus.Col)
        {
            return null;
        }
        return anchorBeforeFocus
            ? new SelectionRange { Start = _selectionAnchor, End = _selectionFocus }
            : new SelectionRange { Start = _selectionFocus, End = _selectionAnchor };
    }

    private (int Start, int End) GetSelectionColumns(
        string line,
        int row,
        SelectionRange selection,
        int minColumn = 0,
        int? maxColumn = null)
    {
        var lineWidth = UnicodeWidth.VisibleWidth(line);
        var maxCol = maxColumn ?? lineWidth;
        var start = Math.Max(0, minColumn);
        var end = Math.Min(lineWidth, maxCol);
        if (row == selection.Start.Row)
        {
            start = UnicodeWidth.GetGraphemeCellRange(line, selection.Start.Col)?.Start
                ?? Math.Min(selection.Start.Col, lineWidth);
        }
        if (row == selection.End.Row)
        {
            end = selection.End.Boundary
                ? Math.Min(selection.End.Col, lineWidth)
                : UnicodeWidth.GetGraphemeCellRange(line, selection.End.Col)?.End
                    ?? Math.Min(selection.End.Col + 1, lineWidth);
        }
        return (Math.Max(minColumn, start), Math.Min(maxCol, end));
    }

    private string? GetActiveSelectionText()
    {
        var selection = GetSelectionBounds();
        if (selection is null)
        {
            return null;
        }
        IReadOnlyList<string> sourceLines = _previousScreen;
        if (selection.Start.ScrollView is not null)
        {
            if (_currentLayout is null)
            {
                return null;
            }
            var box = Layout.GetScrollViewBox(_currentLayout, selection.Start.ScrollView);
            if (box?.ScrollContentLines is not { } contentLines)
            {
                return null;
            }
            sourceLines = contentLines;
        }
        var lines = new List<string>();
        for (var row = selection.Start.Row; row <= selection.End.Row; row++)
        {
            var line = row >= 0 && row < sourceLines.Count ? sourceLines[row] : "";
            var columns = GetSelectionColumns(line, row, selection);
            lines.Add(JsString.TrimEnd(
                Ansi.StripTerminalSequences(TextLayout.SliceByColumn(line, columns.Start, Math.Max(0, columns.End - columns.Start), true))));
        }
        var text = string.Join("\n", lines);
        return text.Length == 0 ? null : text;
    }

    private async Task<bool> CopySelectionToClipboardAsync()
    {
        var text = GetActiveSelectionText();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        return await CopyTextToClipboardAsync(text).ConfigureAwait(false);
    }

    private async Task<bool> CopyTextToClipboardAsync(string text)
    {
        // Prefer an injected clipboard implementation (native clipboard + platform tools with a
        // verified success path) when the host app provides one. A bare OSC 52 write can show
        // "Copied!" while leaving the system clipboard untouched (e.g. macOS Terminal.app, tmux
        // without OSC 52 clipboard passthrough), so only report success when it actually copies.
        if (_copySelection is not null)
        {
            var result = await _copySelection(text).ConfigureAwait(false);
            var ok = result.Success;
            Flash(
                ok ? "Copied!" : result.Message ?? "Copy failed",
                ok ? null : CopyErrorFlashDurationMs);
            return ok;
        }
        Terminal.Write($"\x1b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\x07");
        Flash("Copied!");
        return true;
    }

    private string ApplySearchTextHighlight(string text, bool current)
    {
        var style = current ? _searchCurrentMatchStyle : _searchMatchStyle;
        var result = new StringBuilder();
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(text, index);
            if (ansi is null)
            {
                index += 1;
                continue;
            }
            if (index > plainStart)
            {
                result.Append(style(text[plainStart..index]));
            }
            result.Append(ansi.Value.Code);
            index += ansi.Value.Length;
            plainStart = index;
        }
        if (plainStart < text.Length)
        {
            result.Append(style(text[plainStart..]));
        }
        return result.ToString();
    }

    private string[] ApplySearchHighlights(string[] screen, LayoutFrame layout)
    {
        var search = _activeSearch;
        if (search is null || search.SelectedIndex < 0 || search.Matches.Count == 0)
        {
            return screen;
        }
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        var box = Layout.GetScrollViewBox(layout, scrollView);
        if (box is null)
        {
            return screen;
        }

        var rangesByRow = new Dictionary<int, List<SearchHighlightRange>>();
        var scrollbarColumn = Layout.GetScrollbarGeometry(box)?.Column;
        var minRow = Math.Max(Math.Max(0, box.Rect.Y), box.Clip.Y);
        var maxRow = Math.Min(Math.Min(screen.Length, box.Rect.Y + box.Rect.Height), box.Clip.Y + box.Clip.Height);
        var minColumn = Math.Max(Math.Max(0, box.Rect.X), box.Clip.X);
        var maxColumn = Math.Min(
            Math.Min(Terminal.Columns, box.Rect.X + box.Rect.Width),
            Math.Min(box.Clip.X + box.Clip.Width, scrollbarColumn ?? int.MaxValue));
        var minContentRow = scrollView.ScrollTop + minRow - box.Rect.Y;
        var maxContentRow = scrollView.ScrollTop + maxRow - box.Rect.Y - 1;
        var low = 0;
        var high = search.Matches.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var match = search.Matches[middle];
            var lastRow = match.Segments.Count > 0 ? match.Segments[^1].Row : -1;
            if (lastRow < minContentRow)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (var matchIndex = low; matchIndex < search.Matches.Count; matchIndex++)
        {
            var match = search.Matches[matchIndex];
            if ((match.Segments.Count > 0 ? match.Segments[0].Row : 0) > maxContentRow)
            {
                break;
            }
            foreach (var segment in match.Segments)
            {
                var row = box.Rect.Y + segment.Row - scrollView.ScrollTop;
                if (row < minRow || row >= maxRow)
                {
                    continue;
                }
                var startCol = Math.Max(minColumn, box.Rect.X + segment.StartCol);
                var endCol = Math.Min(maxColumn, box.Rect.X + segment.EndCol);
                if (endCol <= startCol)
                {
                    continue;
                }
                if (!rangesByRow.TryGetValue(row, out var ranges))
                {
                    ranges = [];
                    rangesByRow[row] = ranges;
                }
                ranges.Add(new SearchHighlightRange
                {
                    StartCol = startCol,
                    EndCol = endCol,
                    Current = matchIndex == search.SelectedIndex,
                });
            }
        }

        var result = (string[])screen.Clone();
        foreach (var (row, ranges) in rangesByRow)
        {
            var line = row >= 0 && row < result.Length ? result[row] : "";
            if (TerminalImage.IsImageLine(line))
            {
                continue;
            }
            var lineWidth = UnicodeWidth.VisibleWidth(line);
            // JS sort is stable; LINQ's OrderByDescending is too, unlike List.Sort.
            foreach (var range in ranges.OrderByDescending(r => r.StartCol))
            {
                var startCol = Math.Min(range.StartCol, lineWidth);
                var endCol = Math.Min(range.EndCol, lineWidth);
                if (endCol <= startCol)
                {
                    continue;
                }
                var before = TextLayout.SliceByColumn(line, 0, startCol, true);
                var highlighted = TextLayout.SliceByColumn(line, startCol, endCol - startCol, true);
                var after = TextLayout.SliceByColumn(line, endCol, Math.Max(0, lineWidth - endCol), true);
                line = $"{before}{ApplySearchTextHighlight(highlighted, range.Current)}{after}";
            }
            result[row] = line;
        }
        return result;
    }

    private static string ApplySelectionHighlight(string text)
    {
        var result = new StringBuilder("\x1b[7m");
        var index = 0;
        while (index < text.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(text, index);
            if (ansi is null)
            {
                result.Append(text[index]);
                index += 1;
                continue;
            }
            result.Append(ansi.Value.Code);
            if (ansi.Value.Code.EndsWith('m'))
            {
                result.Append("\x1b[7m");
            }
            index += ansi.Value.Length;
        }
        return $"{result}\x1b[27m";
    }

    private string[] ApplySelection(string[] screen, LayoutFrame? layout = null)
    {
        layout ??= _currentLayout;
        var selection = GetSelectionBounds();
        if (selection is null)
        {
            return screen;
        }
        var screenSelection = selection;
        var minRow = 0;
        var maxRow = screen.Length - 1;
        var minColumn = 0;
        var maxColumn = Terminal.Columns;
        if (selection.Start.ScrollView is not null)
        {
            if (layout is null)
            {
                return screen;
            }
            var box = Layout.GetScrollViewBox(layout, selection.Start.ScrollView);
            if (box is null)
            {
                return screen;
            }
            minRow = Math.Max(Math.Max(0, box.Rect.Y), box.Clip.Y);
            maxRow = Math.Min(
                Math.Min(screen.Length - 1, box.Rect.Y + box.Rect.Height - 1),
                box.Clip.Y + box.Clip.Height - 1);
            minColumn = Math.Max(Math.Max(0, box.Rect.X), box.Clip.X);
            maxColumn = Math.Min(
                Math.Min(Terminal.Columns, box.Rect.X + box.Rect.Width),
                box.Clip.X + box.Clip.Width);
            screenSelection = new SelectionRange
            {
                Start = selection.Start.With(
                    row: box.Rect.Y + selection.Start.Row - selection.Start.ScrollView.ScrollTop,
                    col: box.Rect.X + selection.Start.Col),
                End = selection.End.With(
                    row: box.Rect.Y + selection.End.Row - selection.Start.ScrollView.ScrollTop,
                    col: box.Rect.X + selection.End.Col),
            };
        }
        return screen.Select((line, row) =>
        {
            if (row < minRow
                || row > maxRow
                || row < screenSelection.Start.Row
                || row > screenSelection.End.Row
                || TerminalImage.IsImageLine(line))
            {
                return line;
            }
            var lineWidth = UnicodeWidth.VisibleWidth(line);
            var columns = GetSelectionColumns(line, row, screenSelection, minColumn, maxColumn);
            if (columns.End <= columns.Start)
            {
                return line;
            }
            var before = TextLayout.SliceByColumn(line, 0, columns.Start, true);
            var selected = TextLayout.SliceByColumn(line, columns.Start, columns.End - columns.Start, true);
            var after = TextLayout.SliceByColumn(line, columns.End, Math.Max(0, lineWidth - columns.End), true);
            return $"{before}{ApplySelectionHighlight(selected)}{after}";
        }).ToArray();
    }

    private static bool IsMouseSequence(string data) =>
        SgrMouseSequencePattern.IsMatch(data) || (data.Length == 6 && data.StartsWith("\x1b[M", StringComparison.Ordinal));

    private string[] CompositeScrollToEndIndicator(string[] screen, LayoutFrame layout, int width)
    {
        _scrollToEndIndicatorRect = null;
        var scrollView = layout.PrimaryScrollView ?? _implicitScrollView;
        if (_scrollToEndIndicator is null || !scrollView.FollowEnd || scrollView.IsFollowingEnd)
        {
            return screen;
        }
        var box = Layout.GetScrollViewBox(layout, scrollView);
        var clip = box?.Clip;
        if (clip is null || clip.Width <= 0 || clip.Height <= 0)
        {
            return screen;
        }
        var row = clip.Y + clip.Height - 1;
        if (row >= screen.Length || TerminalImage.IsImageLine(screen[row]))
        {
            return screen;
        }
        var scrollbarColumn = box is not null ? Layout.GetScrollbarGeometry(box)?.Column : null;
        var label = TextLayout.TruncateToWidth(_scrollToEndIndicator(), clip.Width, "");
        var labelWidth = UnicodeWidth.VisibleWidth(label);
        var column = clip.X + (clip.Width - labelWidth) / 2;
        var rightEdge = scrollbarColumn ?? clip.X + clip.Width;
        var availableWidth = Math.Max(0, rightEdge - column);
        var text = TextLayout.TruncateToWidth(label, availableWidth, "");
        var textWidth = UnicodeWidth.VisibleWidth(text);
        if (textWidth == 0)
        {
            return screen;
        }
        var result = (string[])screen.Clone();
        result[row] = Tui.CompositeTuiLine(result[row], text, column, textWidth, width);
        _scrollToEndIndicatorRect = new ScrollToEndIndicatorRect { Row = row, Column = column, Width = textWidth };
        return result;
    }

    private string[] CompositeFlashes(string[] screen, int width, int height)
    {
        var flashLines = _flashes.Render(width);
        // TS uses `slice(-height)`: a zero height keeps every line rather than none.
        if (height != 0 && flashLines.Length > height)
        {
            flashLines = flashLines.Skip(flashLines.Length - height).ToArray();
        }
        if (flashLines.Length == 0)
        {
            return screen;
        }
        var result = new List<string>(screen);
        while (result.Count < height)
        {
            result.Add("");
        }
        for (var row = 0; row < flashLines.Length; row++)
        {
            var line = flashLines[row];
            var flashWidth = UnicodeWidth.VisibleWidth(line);
            if (flashWidth == 0)
            {
                continue;
            }
            result[row] = Tui.CompositeTuiLine(result[row], line, width - flashWidth, flashWidth, width);
        }
        return result.ToArray();
    }

    protected override void DoRender()
    {
        if (Stopped || !_altScreenActive)
        {
            return;
        }
        var width = Math.Max(1, Terminal.Columns);
        var height = Math.Max(1, Terminal.Rows);
        var root = _layoutRoot ?? (IComponent)_implicitScrollView;
        var nextLayout = Layout.RenderLayoutFrame(root, width, height, () => RequestRender());
        if (RefreshSearch(nextLayout))
        {
            nextLayout = Layout.RenderLayoutFrame(root, width, height, () => RequestRender());
        }
        var screen = nextLayout.Lines.Select(line => Osc133ZonePrefix.Replace(line, "")).ToArray();
        screen = ApplySearchHighlights(screen, nextLayout);
        screen = CompositeScrollToEndIndicator(screen, nextLayout, width);
        screen = CompositeOverlays(screen, width, height);
        if (screen.Length > height)
        {
            screen = screen.Skip(screen.Length - height).ToArray();
        }
        screen = ApplySelection(screen, nextLayout);
        screen = CompositeFlashes(screen, width, height);

        var cursorPos = ExtractCursorPosition(screen, height);
        screen = ApplyLineResets(screen).Select(line =>
        {
            if (TerminalImage.IsImageLine(line) || UnicodeWidth.VisibleWidth(line) <= width)
            {
                return line;
            }
            return TextLayout.SliceByColumn(line, 0, width, strict: true);
        }).ToArray();

        var fullRedraw =
            _previousScreen.Length == 0 || _previousScreenWidth != width || _previousScreenHeight != height;
        var changedRows = new bool[screen.Length];
        for (var row = 0; row < screen.Length; row++)
        {
            changedRows[row] = row >= _previousScreen.Length || screen[row] != _previousScreen[row];
        }
        var imageAnchorsNeedRedraw = screen
            .Select((line, row) => changedRows[row]
                && (TerminalImage.IsImageLine(line)
                    || (row < _previousScreen.Length && TerminalImage.IsImageLine(_previousScreen[row]))))
            .Any(value => value);
        var isWezTerm = Environment.GetEnvironmentVariable("WEZTERM_PANE") is not null
            || Environment.GetEnvironmentVariable("TERM_PROGRAM")?.ToLowerInvariant() == "wezterm";
        var imageCellsNeedRedraw =
            !imageAnchorsNeedRedraw
            && isWezTerm
            && _imageProtocol == ImageProtocol.Kitty
            && changedRows.Any(value => value)
            && screen.Select((line, row) =>
            {
                var placementRows = TerminalImage.GetKittyImagePlacementRows(line);
                if (placementRows is null)
                {
                    return false;
                }
                for (var coveredRow = row; coveredRow < row + placementRows.Value; coveredRow++)
                {
                    if (coveredRow < changedRows.Length && changedRows[coveredRow])
                    {
                        return true;
                    }
                }
                return false;
            }).Any(value => value);
        var imagesNeedRedraw = imageAnchorsNeedRedraw || imageCellsNeedRedraw;
        var redrawImages = fullRedraw || imagesNeedRedraw;
        var hadUploadedKittyImages = _uploadedKittyImages.Count > 0;
        var preparedKittyScreen =
            redrawImages && _imageProtocol == ImageProtocol.Kitty
                ? PrepareKittyScreen(screen)
                : (Lines: screen, EvictedImageDeletion: "");

        var buffer = new StringBuilder(BeginSynchronizedOutput);
        if (fullRedraw)
        {
            FullRedrawCount += 1;
            var clearImages =
                _imageProtocol == ImageProtocol.Kitty && hadUploadedKittyImages
                    ? TerminalImage.DeleteAllKittyPlacements()
                    : DeleteKittyImages();
            buffer.Append($"{clearImages}\x1b[2J");
        }
        else if (imagesNeedRedraw)
        {
            if (_imageProtocol == ImageProtocol.Iterm2)
            {
                buffer.Append("\x1b[2J");
            }
            else if (_imageProtocol == ImageProtocol.Kitty)
            {
                buffer.Append(TerminalImage.DeleteAllKittyPlacements());
            }
        }
        buffer.Append(preparedKittyScreen.EvictedImageDeletion);

        // WezTerm erases intersecting Kitty image cells when a later row write touches a covered row.
        // Draw image placements after every clear and text write so nothing later intersects them;
        // preserve the existing interleaved output for text-only frames and every other terminal.
        var drawKittyImagesLast =
            redrawImages && _imageProtocol == ImageProtocol.Kitty && screen.Any(TerminalImage.IsImageLine) && isWezTerm;
        if (drawKittyImagesLast)
        {
            for (var row = 0; row < height; row++)
            {
                if (!fullRedraw && !imagesNeedRedraw && row < _previousScreen.Length && screen[row] == _previousScreen[row])
                {
                    continue;
                }
                buffer.Append($"\x1b[{row + 1};1H\x1b[2K");
            }
            for (var row = 0; row < height; row++)
            {
                if (!fullRedraw && !imagesNeedRedraw && row < _previousScreen.Length && screen[row] == _previousScreen[row])
                {
                    continue;
                }
                if (TerminalImage.IsImageLine(preparedKittyScreen.Lines[row]))
                {
                    continue;
                }
                buffer.Append($"\x1b[{row + 1};1H{preparedKittyScreen.Lines[row]}");
            }
            for (var row = 0; row < height; row++)
            {
                if (!fullRedraw && !imagesNeedRedraw && row < _previousScreen.Length && screen[row] == _previousScreen[row])
                {
                    continue;
                }
                if (!TerminalImage.IsImageLine(preparedKittyScreen.Lines[row]))
                {
                    continue;
                }
                buffer.Append($"\x1b[{row + 1};1H{preparedKittyScreen.Lines[row]}");
            }
        }
        else
        {
            for (var row = 0; row < height; row++)
            {
                if (!fullRedraw && !imagesNeedRedraw && row < _previousScreen.Length && screen[row] == _previousScreen[row])
                {
                    continue;
                }
                buffer.Append($"\x1b[{row + 1};1H\x1b[2K{preparedKittyScreen.Lines[row]}");
            }
        }

        if (cursorPos is { } cursor)
        {
            buffer.Append($"\x1b[{cursor.Row + 1};{Math.Min(width, cursor.Col) + 1}H");
            buffer.Append(GetShowHardwareCursor() ? "\x1b[?25h" : "\x1b[?25l");
        }
        else
        {
            buffer.Append("\x1b[?25l");
        }
        buffer.Append(EndSynchronizedOutput);
        Terminal.Write(buffer.ToString());

        _previousScreen = screen;
        _previousScreenWidth = width;
        _previousScreenHeight = height;
        _currentLayout = nextLayout;
    }

    private static double NowMs() => Clock.Elapsed.TotalMilliseconds;

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ------------------------------------------------------------------
    // Implicit document: the mounted roots, wrapped by the default scroll view
    // ------------------------------------------------------------------

    /// <summary>
    /// TS builds the default scroll view's child as an object literal that forwards to the TUI base
    /// implementation. C# needs a concrete type; the base calls are exposed through private members the
    /// nested type can reach.
    /// </summary>
    private sealed class ImplicitDocument : IComponent
    {
        private readonly TuiAltScreen _tui;

        public ImplicitDocument(TuiAltScreen tui) => _tui = tui;

        public string[] Render(int width) => _tui.RenderMountedChildren(width);

        public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event) => _tui.HandleMouseOnMountedChildren(@event);

        public void Invalidate() => _tui.InvalidateMountedChildren();
    }

    private string[] RenderMountedChildren(int width) => base.Render(width);

    private TuiMouseEventResult? HandleMouseOnMountedChildren(TuiMouseEvent @event) => base.HandleMouse(@event);

    private void InvalidateMountedChildren()
    {
        foreach (var child in Children)
        {
            child.Invalidate();
        }
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private enum SelectionGranularity
    {
        Character,
        Word,
        Line,
    }

    private enum SearchSelectionMode
    {
        Query,
        Retain,
        Next,
        Previous,
    }

    private sealed class CachedKittyImage
    {
        public required long TransmissionGeneration { get; init; }
        public required long TransmissionBytes { get; init; }
        public required long EstimatedDecodedBytes { get; init; }
    }

    private sealed class SelectionPoint
    {
        public required int Row { get; init; }
        public required int Col { get; init; }
        public ScrollView? ScrollView { get; init; }

        /// <summary>Whether this point lies between terminal cells rather than on a cell.</summary>
        public bool Boundary { get; init; }

        public SelectionPoint With(int? row = null, int? col = null, bool? boundary = null) => new()
        {
            Row = row ?? Row,
            Col = col ?? Col,
            ScrollView = ScrollView,
            Boundary = boundary ?? Boundary,
        };
    }

    private sealed class SelectionRange
    {
        public required SelectionPoint Start { get; init; }
        public required SelectionPoint End { get; init; }
    }

    private sealed class ClickTarget
    {
        public required long Timestamp { get; init; }
        public required int Count { get; init; }
        public required int Row { get; init; }
        public ScrollView? ScrollView { get; init; }
        public required int WordStart { get; init; }
        public required int WordEnd { get; init; }
    }

    private sealed class ComponentClick
    {
        public required long Timestamp { get; init; }
        public required int Count { get; init; }
        public required IComponent Component { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
    }

    private sealed class SgrMouseEvent
    {
        public required int Button { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required bool Release { get; init; }
    }

    private sealed class WheelEvent
    {
        /// <summary>-1 for wheel-up, 1 for wheel-down.</summary>
        public required int Direction { get; init; }
        public required int X { get; init; }
        public required int Y { get; init; }
        public required int Button { get; init; }
    }

    private sealed class ScrollbarDrag
    {
        public required ScrollView ScrollView { get; init; }
        public required int GrabOffset { get; init; }
    }

    private sealed class ScrollbarTarget
    {
        public required ScrollView ScrollView { get; init; }
        public required ScrollbarGeometry Geometry { get; init; }
    }

    private sealed class ScrollToEndIndicatorRect
    {
        public required int Row { get; init; }
        public required int Column { get; init; }
        public required int Width { get; init; }
    }

    private sealed class SearchHighlightRange
    {
        public required int StartCol { get; init; }
        public required int EndCol { get; init; }
        public required bool Current { get; init; }
    }

    private sealed class ActiveSearch
    {
        public required AltScreenSearchComponent Component { get; init; }
        public required AltScreenSearchIndex Index { get; init; }
        public IOverlayHandle? Overlay { get; set; }
        public required string Query { get; set; }
        public required List<AltScreenSearchMatch> Matches { get; set; }
        public required int SelectedIndex { get; set; }
        public string? SelectedKey { get; set; }
        public required int AnchorRow { get; set; }
        public required SearchSelectionMode SelectionMode { get; set; }
    }

    /// <summary>
    /// Insertion-ordered image cache mirroring JS <c>Map</c> semantics: <see cref="Set"/> on an existing
    /// key moves it to the end, which is what the LRU eviction pass relies on.
    /// </summary>
    private sealed class KittyImageCache
    {
        private readonly List<KeyValuePair<long, CachedKittyImage>> _entries = [];

        public int Count => _entries.Count;

        public IReadOnlyList<KeyValuePair<long, CachedKittyImage>> Entries => _entries;

        public CachedKittyImage? TryGet(long key)
        {
            foreach (var entry in _entries)
            {
                if (entry.Key == key)
                {
                    return entry.Value;
                }
            }
            return null;
        }

        public void Set(long key, CachedKittyImage value)
        {
            Remove(key);
            _entries.Add(new KeyValuePair<long, CachedKittyImage>(key, value));
        }

        public void Remove(long key)
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                if (_entries[index].Key == key)
                {
                    _entries.RemoveAt(index);
                    return;
                }
            }
        }

        public void Clear() => _entries.Clear();

        public List<KeyValuePair<long, CachedKittyImage>> Snapshot() => [.. _entries];
    }
}
