using System.Diagnostics;

namespace Pi.Tui;

/// <summary>Component interface - all components must implement this (port of <c>tui.ts</c>'s <c>Component</c>).</summary>
public interface IComponent
{
    /// <summary>Render the component to lines for the given viewport width.</summary>
    string[] Render(int width);

    /// <summary>Optional handler for keyboard input when the component has focus.</summary>
    void HandleInput(string data)
    {
    }

    /// <summary>If true, the component receives key release events (Kitty protocol). Default false.</summary>
    bool WantsKeyRelease => false;

    /// <summary>Invalidate any cached rendering state.</summary>
    void Invalidate()
    {
    }
}

/// <summary>Components that can receive focus and display a hardware cursor.</summary>
public interface IFocusable
{
    /// <summary>Set by TUI when focus changes. The component should emit <see cref="Tui.CursorMarker"/> when true.</summary>
    bool Focused { get; set; }
}

/// <summary>Anchor position for overlays.</summary>
public enum OverlayAnchor
{
    Center,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    TopCenter,
    BottomCenter,
    LeftCenter,
    RightCenter,
}

/// <summary>Value that can be absolute (number) or a percentage (e.g. "50%").</summary>
public readonly struct SizeValue
{
    public bool IsPercent { get; }

    /// <summary>Absolute value when <see cref="IsPercent"/> is false, otherwise the percentage (0..100).</summary>
    public double Value { get; }

    private SizeValue(bool isPercent, double value)
    {
        IsPercent = isPercent;
        Value = value;
    }

    public static SizeValue Absolute(double value) => new(false, value);

    public static SizeValue Percent(double percent) => new(true, percent);

    public static implicit operator SizeValue(int value) => Absolute(value);

    public static implicit operator SizeValue(double value) => Absolute(value);

    public static implicit operator SizeValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith('%') && double.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), out var pct))
        {
            return Percent(pct);
        }
        if (double.TryParse(trimmed, out var abs))
        {
            return Absolute(abs);
        }
        throw new FormatException($"Invalid SizeValue: '{value}'");
    }

    /// <summary>Resolve against a reference size, or null when unset/invalid.</summary>
    public double? Resolve(double referenceSize)
    {
        if (!IsPercent)
        {
            return Value;
        }
        return Math.Floor(referenceSize * Value / 100.0);
    }
}

/// <summary>Margin configuration for overlays.</summary>
public readonly struct OverlayMargin
{
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public int Left { get; init; }

    public static implicit operator OverlayMargin(int all) => new() { Top = all, Right = all, Bottom = all, Left = all };
}

/// <summary>Options for overlay positioning and sizing.</summary>
public sealed class OverlayOptions
{
    public SizeValue? Width { get; set; }
    public int? MinWidth { get; set; }
    public SizeValue? MaxHeight { get; set; }
    public OverlayAnchor? Anchor { get; set; }
    public int? OffsetX { get; set; }
    public int? OffsetY { get; set; }
    public SizeValue? Row { get; set; }
    public SizeValue? Col { get; set; }
    public OverlayMargin? Margin { get; set; }
    public Func<int, int, bool>? Visible { get; set; }
    public bool NonCapturing { get; set; }
}

/// <summary>Options for <see cref="OverlayHandle.Unfocus"/>.</summary>
public sealed class OverlayUnfocusOptions
{
    public IComponent? Target { get; set; }
}

/// <summary>Handle returned by <see cref="TuiBase.ShowOverlay"/> for controlling an overlay.</summary>
public interface IOverlayHandle
{
    void Hide();
    void SetHidden(bool hidden);
    bool IsHidden();
    void Focus();
    void Unfocus(OverlayUnfocusOptions? options = null);
    bool IsFocused();
}

/// <summary>Result of a TUI input listener: consume the event and/or replace the data.</summary>
public sealed class TuiInputListenerResult
{
    public bool Consume { get; init; }
    public string? Data { get; set; }

    public static readonly TuiInputListenerResult Consumed = new() { Consume = true };
}

/// <summary>TUI mode.</summary>
public enum TuiMode
{
    Regular,
    Fullscreen,
}

/// <summary>Options for <see cref="ITui.Stop"/>.</summary>
public sealed class TuiStopOptions
{
    public bool PreserveScreen { get; set; }
}

/// <summary>Shared interface for TUI renderers (port of <c>tui.ts</c>'s <c>TUI</c>).</summary>
public interface ITui : IComponent
{
    TuiMode Mode { get; }
    List<IComponent> Children { get; }
    ITerminal Terminal { get; }
    Action? OnDebug { get; set; }
    int FullRedraws { get; }
    void AddChild(IComponent component);
    void RemoveChild(IComponent component);
    void Clear();
    bool GetShowHardwareCursor();
    void SetShowHardwareCursor(bool enabled);
    bool GetClearOnShrink();
    void SetClearOnShrink(bool enabled);
    void SetFocus(IComponent? component);
    IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null);
    void HideOverlay();
    bool HasOverlay();
    void Start();
    void Stop(TuiStopOptions? options = null);
    void RenderNow(bool force = false);
    void RequestRender(bool force = false);
    Action AddInputListener(Func<string, TuiInputListenerResult?> listener);
    void RemoveInputListener(Func<string, TuiInputListenerResult?> listener);
    Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener);
    void SetTerminalColorSchemeNotifications(bool enabled);
    Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs);
    Task<TerminalColorScheme?> QueryTerminalColorSchemeAsync(int timeoutMs);
}

/// <summary>Base class for TUI renderers: component container, focus, overlays, render scheduling.</summary>
public abstract class TuiBase : Container, ITui
{
    private const int MinRenderIntervalMs = 16;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly object _renderLock = new();
    private readonly HashSet<Func<string, TuiInputListenerResult?>> _inputListeners = new();
    private readonly List<OverlayStackEntry> _overlayStack = new();
    private readonly List<(Action<TerminalColorScheme> Listener, Action Unsubscribe)> _colorSchemeListeners = new();

    private IComponent? _focusedComponent;
    private bool _renderRequested;
    private bool _immediateRenderScheduled;
    private Timer? _renderTimer;
    private long _lastRenderAt;
    private int _focusOrderCounter;
    private OverlayFocusRestoreState _overlayFocusRestore = OverlayFocusRestoreState.Inactive;
    private bool _terminalColorSchemeNotificationsEnabled;
    private bool _showHardwareCursor;
    private bool _clearOnShrink;
    private int _pendingOsc11Replies;
    private readonly Queue<PendingOsc11Query> _pendingOsc11Queries = new();
    private bool _stopped;

    protected int FullRedrawCount;

    /// <summary>Whether the renderer has been stopped.</summary>
    protected bool Stopped => _stopped;

    protected TuiBase(ITerminal terminal, bool? showHardwareCursor = null)
    {
        Terminal = terminal;
        _showHardwareCursor = showHardwareCursor ?? Environment.GetEnvironmentVariable("PI_HARDWARE_CURSOR") == "1";
        _clearOnShrink = Environment.GetEnvironmentVariable("PI_CLEAR_ON_SHRINK") == "1";
    }

    public abstract TuiMode Mode { get; }

    public ITerminal Terminal { get; }

    public Action? OnDebug { get; set; }

    public int FullRedraws => FullRedrawCount;

    protected abstract void DoRender();

    protected virtual void ResetRenderState()
    {
    }

    protected virtual void BeforeTerminalStart()
    {
    }

    protected virtual void AfterTerminalStart()
    {
    }

    protected virtual void BeforeTerminalStop(TuiStopOptions options)
    {
    }

    protected virtual void AfterTerminalStop(TuiStopOptions options)
    {
    }

    protected virtual IReadOnlyList<IComponent> GetMountedRoots() => Children;

    protected bool HasOverlayEntries => _overlayStack.Count > 0;

    private static long NowMs() => Clock.ElapsedMilliseconds;

    // ------------------------------------------------------------------
    // Hardware cursor / shrink
    // ------------------------------------------------------------------

    public bool GetShowHardwareCursor() => _showHardwareCursor;

    public void SetShowHardwareCursor(bool enabled)
    {
        if (_showHardwareCursor == enabled)
        {
            return;
        }
        _showHardwareCursor = enabled;
        if (!enabled)
        {
            Terminal.HideCursor();
        }
        RequestRender();
    }

    public bool GetClearOnShrink() => _clearOnShrink;

    public void SetClearOnShrink(bool enabled) => _clearOnShrink = enabled;

    public IComponent? GetFocusedComponent() => _focusedComponent;

    // ------------------------------------------------------------------
    // Focus
    // ------------------------------------------------------------------

    public void SetFocus(IComponent? component) => SetFocusInternal(component, OverlayFocusRestorePolicy.Clear);

    private void SetFocusInternal(IComponent? component, OverlayFocusRestorePolicy overlayFocusRestore)
    {
        var previousFocus = _focusedComponent;
        var nextFocus = component;
        var previousFocusedOverlay = previousFocus is not null
            ? FindOverlay(previousFocus, visibleOnly: true)
            : null;
        var nextFocusIsOverlay = nextFocus is not null && _overlayStack.Any(e => e.Component == nextFocus);
        var restoreState = GetVisibleOverlayFocusRestore();

        if (nextFocus is not null && !nextFocusIsOverlay)
        {
            if (restoreState is BlockedOverlayFocusRestoreState blocked && blocked.BlockedBy == previousFocus)
            {
                if (blocked.Resume is FocusTargetResume || !IsComponentMounted(blocked.BlockedBy!))
                {
                    nextFocus = ResolveBlockedOverlayFocusResume(blocked);
                }
                else
                {
                    _overlayFocusRestore = new BlockedOverlayFocusRestoreState(blocked.Overlay, nextFocus, blocked.Resume);
                }
            }
            else if (previousFocusedOverlay is not null
                && restoreState is EligibleOverlayFocusRestoreState eligible
                && eligible.Overlay == previousFocusedOverlay
                && !IsOverlayFocusAncestor(previousFocusedOverlay, nextFocus))
            {
                _overlayFocusRestore = new BlockedOverlayFocusRestoreState(
                    previousFocusedOverlay, nextFocus, new RestoreOverlayResume());
            }
        }
        else if (nextFocus is null)
        {
            if (restoreState is BlockedOverlayFocusRestoreState blocked && blocked.BlockedBy == previousFocus)
            {
                nextFocus = ResolveBlockedOverlayFocusResume(blocked);
            }
            else if (overlayFocusRestore == OverlayFocusRestorePolicy.Clear)
            {
                ClearOverlayFocusRestore();
            }
        }

        if (_focusedComponent is IFocusable prev)
        {
            prev.Focused = false;
        }

        _focusedComponent = nextFocus;

        if (nextFocus is IFocusable next)
        {
            next.Focused = true;
        }

        var focusedOverlay = nextFocus is not null ? FindOverlay(nextFocus, visibleOnly: true) : null;
        if (focusedOverlay is not null)
        {
            _overlayFocusRestore = new EligibleOverlayFocusRestoreState(focusedOverlay);
        }
    }

    private void ClearOverlayFocusRestore() => _overlayFocusRestore = OverlayFocusRestoreState.Inactive;

    private void ClearOverlayFocusRestoreFor(OverlayStackEntry overlay)
    {
        if (_overlayFocusRestore is not OverlayFocusRestoreState.InactiveState && _overlayFocusRestore.Overlay == overlay)
        {
            ClearOverlayFocusRestore();
        }
    }

    private IComponent? ResolveBlockedOverlayFocusResume(BlockedOverlayFocusRestoreState restoreState)
    {
        if (restoreState.Resume is RestoreOverlayResume)
        {
            return restoreState.Overlay.Component;
        }
        ClearOverlayFocusRestore();
        return ((FocusTargetResume)restoreState.Resume).Target;
    }

    private OverlayFocusRestoreState GetVisibleOverlayFocusRestore()
    {
        var state = _overlayFocusRestore;
        if (state is OverlayFocusRestoreState.InactiveState)
        {
            return state;
        }
        if (state.Overlay is null || !_overlayStack.Contains(state.Overlay) || !IsOverlayVisible(state.Overlay))
        {
            return OverlayFocusRestoreState.Inactive;
        }
        return state;
    }

    private bool IsOverlayFocusAncestor(OverlayStackEntry entry, IComponent component)
    {
        var visited = new HashSet<IComponent>();
        var current = entry.PreFocus;
        while (current is not null && visited.Add(current))
        {
            if (current == component)
            {
                return true;
            }
            current = _overlayStack.FirstOrDefault(o => o.Component == current)?.PreFocus;
        }
        return false;
    }

    private void RetargetOverlayPreFocus(OverlayStackEntry removed)
    {
        foreach (var overlay in _overlayStack)
        {
            if (overlay != removed && overlay.PreFocus == removed.Component)
            {
                overlay.PreFocus = removed.PreFocus;
            }
        }
    }

    private bool IsComponentMounted(IComponent component) =>
        GetMountedRoots().Any(root => ContainsComponent(root, component));

    private static bool ContainsComponent(IComponent root, IComponent target)
    {
        if (root == target)
        {
            return true;
        }
        if (root is not Container container)
        {
            return false;
        }
        return container.Children.Any(child => ContainsComponent(child, target));
    }

    // ------------------------------------------------------------------
    // Overlays
    // ------------------------------------------------------------------

    private OverlayStackEntry? FindOverlay(IComponent component, bool visibleOnly)
    {
        foreach (var entry in _overlayStack)
        {
            if (entry.Component == component && (!visibleOnly || IsOverlayVisible(entry)))
            {
                return entry;
            }
        }
        return null;
    }

    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null)
    {
        var entry = new OverlayStackEntry
        {
            Component = component,
            Options = options,
            PreFocus = _focusedComponent,
            Hidden = false,
            FocusOrder = ++_focusOrderCounter,
        };
        _overlayStack.Add(entry);
        if (!(options?.NonCapturing ?? false) && IsOverlayVisible(entry))
        {
            SetFocus(component);
        }
        Terminal.HideCursor();
        RequestRender();
        return new OverlayHandleImpl(this, entry);
    }

    public void HideOverlay()
    {
        if (_overlayStack.Count == 0)
        {
            return;
        }
        var overlay = _overlayStack[^1];
        RemoveOverlayEntry(overlay);
        if (_focusedComponent == overlay.Component)
        {
            var topVisible = GetTopmostVisibleOverlay();
            SetFocus(topVisible?.Component ?? overlay.PreFocus);
        }
        if (_overlayStack.Count == 0)
        {
            Terminal.HideCursor();
        }
        RequestRender();
    }

    private void RemoveOverlayEntry(OverlayStackEntry entry)
    {
        ClearOverlayFocusRestoreFor(entry);
        RetargetOverlayPreFocus(entry);
        _overlayStack.Remove(entry);
    }

    public bool HasOverlay() => _overlayStack.Any(IsOverlayVisible);

    private bool IsOverlayVisible(OverlayStackEntry entry)
    {
        if (entry.Hidden)
        {
            return false;
        }
        if (entry.Options?.Visible is { } visible)
        {
            return visible(Terminal.Columns, Terminal.Rows);
        }
        return true;
    }

    private OverlayStackEntry? GetTopmostVisibleOverlay()
    {
        OverlayStackEntry? topmost = null;
        foreach (var overlay in _overlayStack)
        {
            if ((overlay.Options?.NonCapturing ?? false) || !IsOverlayVisible(overlay))
            {
                continue;
            }
            if (topmost is null || overlay.FocusOrder > topmost.FocusOrder)
            {
                topmost = overlay;
            }
        }
        return topmost;
    }

    private sealed class OverlayHandleImpl : IOverlayHandle
    {
        private readonly TuiBase _tui;
        private readonly OverlayStackEntry _entry;

        public OverlayHandleImpl(TuiBase tui, OverlayStackEntry entry)
        {
            _tui = tui;
            _entry = entry;
        }

        public void Hide()
        {
            if (!_tui._overlayStack.Contains(_entry))
            {
                return;
            }
            _tui.RemoveOverlayEntry(_entry);
            if (_tui._focusedComponent == _entry.Component)
            {
                var topVisible = _tui.GetTopmostVisibleOverlay();
                _tui.SetFocus(topVisible?.Component ?? _entry.PreFocus);
            }
            if (_tui._overlayStack.Count == 0)
            {
                _tui.Terminal.HideCursor();
            }
            _tui.RequestRender();
        }

        public void SetHidden(bool hidden)
        {
            if (_entry.Hidden == hidden)
            {
                return;
            }
            _entry.Hidden = hidden;
            if (hidden)
            {
                _tui.ClearOverlayFocusRestoreFor(_entry);
                if (_tui._focusedComponent == _entry.Component)
                {
                    var topVisible = _tui.GetTopmostVisibleOverlay();
                    _tui.SetFocus(topVisible?.Component ?? _entry.PreFocus);
                }
            }
            else if (!(_entry.Options?.NonCapturing ?? false) && _tui.IsOverlayVisible(_entry))
            {
                _entry.FocusOrder = ++_tui._focusOrderCounter;
                _tui.SetFocus(_entry.Component);
            }
            _tui.RequestRender();
        }

        public bool IsHidden() => _entry.Hidden;

        public void Focus()
        {
            if (!_tui._overlayStack.Contains(_entry) || !_tui.IsOverlayVisible(_entry))
            {
                return;
            }
            _entry.FocusOrder = ++_tui._focusOrderCounter;
            _tui.SetFocus(_entry.Component);
            _tui.RequestRender();
        }

        public void Unfocus(OverlayUnfocusOptions? options = null)
        {
            var isFocused = _tui._focusedComponent == _entry.Component;
            var restoreState = _tui._overlayFocusRestore;
            var hasPendingRestore = restoreState is not OverlayFocusRestoreState.InactiveState && restoreState.Overlay == _entry;
            if (!isFocused && !hasPendingRestore)
            {
                return;
            }
            if (restoreState is BlockedOverlayFocusRestoreState blocked
                && blocked.Overlay == _entry
                && _tui._focusedComponent == blocked.BlockedBy)
            {
                if (options is not null)
                {
                    _tui._overlayFocusRestore = new BlockedOverlayFocusRestoreState(
                        _entry, blocked.BlockedBy, new FocusTargetResume(options.Target));
                }
                else
                {
                    _tui.ClearOverlayFocusRestore();
                }
                _tui.RequestRender();
                return;
            }
            _tui.ClearOverlayFocusRestoreFor(_entry);
            if (isFocused || options is not null)
            {
                var topVisible = _tui.GetTopmostVisibleOverlay();
                var fallbackTarget = topVisible is not null && topVisible != _entry ? topVisible.Component : _entry.PreFocus;
                _tui.SetFocus(options is not null ? options.Target : fallbackTarget);
            }
            _tui.RequestRender();
        }

        public bool IsFocused() => _tui._focusedComponent == _entry.Component;
    }

    public override void Invalidate()
    {
        foreach (var root in GetMountedRoots())
        {
            root.Invalidate();
        }
        foreach (var overlay in _overlayStack)
        {
            overlay.Component.Invalidate();
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void Start()
    {
        _stopped = false;
        BeforeTerminalStart();
        Terminal.Start(HandleTerminalInput, () => RequestRender());
        AfterTerminalStart();
        Terminal.HideCursor();
        if (_terminalColorSchemeNotificationsEnabled)
        {
            Terminal.Write("\x1b[?2031h");
        }
        RequestRender();
    }

    public void Stop(TuiStopOptions? options = null)
    {
        options ??= new TuiStopOptions();
        _stopped = true;
        CancelRenderTimer();
        if (_terminalColorSchemeNotificationsEnabled)
        {
            Terminal.Write("\x1b[?2031l");
        }
        BeforeTerminalStop(options);
        Terminal.ShowCursor();
        Terminal.Stop();
        AfterTerminalStop(options);
    }

    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener)
    {
        _inputListeners.Add(listener);
        return () => _inputListeners.Remove(listener);
    }

    public void RemoveInputListener(Func<string, TuiInputListenerResult?> listener) => _inputListeners.Remove(listener);

    public void RenderNow(bool force = false)
    {
        if (force)
        {
            ResetRenderState();
        }
        _renderRequested = false;
        CancelRenderTimer();
        _lastRenderAt = NowMs();
        DoRender();
    }

    public void RequestRender(bool force = false)
    {
        if (force)
        {
            ResetRenderState();
            RequestImmediateRender();
            return;
        }
        lock (_renderLock)
        {
            if (_renderRequested)
            {
                return;
            }
            _renderRequested = true;
        }
        ScheduleRender();
    }

    private void RequestImmediateRender()
    {
        lock (_renderLock)
        {
            CancelRenderTimerLocked();
            _renderRequested = true;
            if (_immediateRenderScheduled)
            {
                return;
            }
            _immediateRenderScheduled = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_renderLock)
            {
                _immediateRenderScheduled = false;
                if (_stopped || !_renderRequested)
                {
                    return;
                }
                CancelRenderTimerLocked();
                _renderRequested = false;
                _lastRenderAt = NowMs();
            }
            DoRender();
        });
    }

    private void CancelRenderTimer()
    {
        lock (_renderLock)
        {
            CancelRenderTimerLocked();
        }
    }

    private void CancelRenderTimerLocked()
    {
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    private void ScheduleRender()
    {
        lock (_renderLock)
        {
            if (_stopped || _renderTimer is not null || !_renderRequested)
            {
                return;
            }
            var elapsed = NowMs() - _lastRenderAt;
            var delay = Math.Max(0, MinRenderIntervalMs - (int)elapsed);
            _renderTimer = new Timer(_ => OnRenderTimer(), null, delay, Timeout.Infinite);
        }
    }

    private void OnRenderTimer()
    {
        lock (_renderLock)
        {
            _renderTimer?.Dispose();
            _renderTimer = null;
            if (_stopped || !_renderRequested)
            {
                return;
            }
            _renderRequested = false;
            _lastRenderAt = NowMs();
        }
        DoRender();
        lock (_renderLock)
        {
            if (_renderRequested)
            {
                ScheduleRender();
            }
        }
    }

    private void HandleTerminalInput(string data)
    {
        if (_inputListeners.Count > 0)
        {
            var current = data;
            foreach (var listener in _inputListeners.ToArray())
            {
                var result = listener(current);
                if (result?.Consume ?? false)
                {
                    return;
                }
                if (result?.Data is not null)
                {
                    current = result.Data;
                }
            }
            if (current.Length == 0)
            {
                return;
            }
            data = current;
        }

        if (Keys.MatchesKey(data, "shift+ctrl+d") && OnDebug is { } onDebug)
        {
            onDebug();
            return;
        }

        var focusedOverlay = _overlayStack.FirstOrDefault(o => o.Component == _focusedComponent);
        if (focusedOverlay is not null && !IsOverlayVisible(focusedOverlay))
        {
            var topVisible = GetTopmostVisibleOverlay();
            if (topVisible is not null)
            {
                SetFocus(topVisible.Component);
            }
            else
            {
                SetFocusInternal(focusedOverlay.PreFocus, OverlayFocusRestorePolicy.Preserve);
            }
        }

        var focusIsOverlay = _overlayStack.Any(o => o.Component == _focusedComponent);
        if (!focusIsOverlay)
        {
            var restoreState = GetVisibleOverlayFocusRestore();
            if (restoreState is EligibleOverlayFocusRestoreState eligible)
            {
                SetFocus(eligible.Overlay.Component);
            }
            else if (restoreState is BlockedOverlayFocusRestoreState blocked && blocked.BlockedBy != _focusedComponent)
            {
                if (blocked.Resume is RestoreOverlayResume)
                {
                    SetFocus(blocked.Overlay.Component);
                }
                else
                {
                    ClearOverlayFocusRestore();
                    SetFocus(((FocusTargetResume)blocked.Resume).Target);
                }
            }
        }

        if (_focusedComponent is not null)
        {
            if (Keys.IsKeyRelease(data) && !_focusedComponent.WantsKeyRelease)
            {
                return;
            }
            _focusedComponent.HandleInput(data);
            RequestImmediateRender();
        }
    }

    // ------------------------------------------------------------------
    // Overlay layout / compositing
    // ------------------------------------------------------------------

    protected string[] CompositeOverlays(string[] lines, int termWidth, int termHeight)
    {
        if (_overlayStack.Count == 0)
        {
            return lines;
        }
        var result = new List<string>(lines);

        var rendered = new List<(string[] OverlayLines, int Row, int Col, int Width)>();
        var minLinesNeeded = result.Count;

        var visibleEntries = _overlayStack.Where(IsOverlayVisible).OrderBy(e => e.FocusOrder).ToList();
        foreach (var entry in visibleEntries)
        {
            var initialLayout = ResolveOverlayLayoutFull(entry.Options, 0, termWidth, termHeight);
            var width = initialLayout.Width;
            var maxHeight = initialLayout.MaxHeight;
            var overlayLines = entry.Component.Render(width);
            if (maxHeight is { } mh && overlayLines.Length > mh)
            {
                overlayLines = overlayLines.Take(mh).ToArray();
            }
            var finalLayout = ResolveOverlayLayoutFull(entry.Options, overlayLines.Length, termWidth, termHeight);
            rendered.Add((overlayLines, finalLayout.Row, finalLayout.Col, width));
            minLinesNeeded = Math.Max(minLinesNeeded, finalLayout.Row + overlayLines.Length);
        }

        var workingHeight = Math.Max(Math.Max(result.Count, termHeight), minLinesNeeded);
        while (result.Count < workingHeight)
        {
            result.Add("");
        }

        var viewportStart = Math.Max(0, workingHeight - termHeight);
        foreach (var (overlayLines, row, col, w) in rendered)
        {
            for (var i = 0; i < overlayLines.Length; i++)
            {
                var idx = viewportStart + row + i;
                if (idx >= 0 && idx < result.Count)
                {
                    var truncated = UnicodeWidth.VisibleWidth(overlayLines[i]) > w
                        ? TextLayout.SliceByColumn(overlayLines[i], 0, w, strict: true)
                        : overlayLines[i];
                    result[idx] = Tui.CompositeTuiLine(result[idx], truncated, col, w, termWidth);
                }
            }
        }

        return result.ToArray();
    }

    private (int Width, int? MaxHeight) ResolveOverlayLayout(OverlayOptions? options, int overlayHeight, int termWidth, int termHeight)
    {
        var layout = ResolveOverlayLayoutFull(options, overlayHeight, termWidth, termHeight);
        return (layout.Width, layout.MaxHeight);
    }

    private (int Width, int Row, int Col, int? MaxHeight) ResolveOverlayLayoutFull(        OverlayOptions? options, int overlayHeight, int termWidth, int termHeight)
    {
        var opt = options ?? new OverlayOptions();
        var margin = opt.Margin ?? new OverlayMargin();
        var marginTop = Math.Max(0, margin.Top);
        var marginRight = Math.Max(0, margin.Right);
        var marginBottom = Math.Max(0, margin.Bottom);
        var marginLeft = Math.Max(0, margin.Left);

        var availWidth = Math.Max(1, termWidth - marginLeft - marginRight);
        var availHeight = Math.Max(1, termHeight - marginTop - marginBottom);

        double width = opt.Width?.Resolve(termWidth) ?? Math.Min(80, availWidth);
        if (opt.MinWidth is { } minWidth)
        {
            width = Math.Max(width, minWidth);
        }
        width = Math.Max(1, Math.Min(width, availWidth));
        var widthInt = (int)width;

        double? maxHeight = opt.MaxHeight?.Resolve(termHeight);
        if (maxHeight is { } mh)
        {
            maxHeight = Math.Max(1, Math.Min(mh, availHeight));
        }
        var effectiveHeight = maxHeight is { } eh ? Math.Min(overlayHeight, (int)eh) : overlayHeight;

        int row;
        if (opt.Row is { } rowValue)
        {
            if (rowValue.IsPercent)
            {
                var maxRow = Math.Max(0, availHeight - effectiveHeight);
                row = marginTop + (int)Math.Floor(maxRow * rowValue.Value / 100.0);
            }
            else
            {
                row = (int)rowValue.Value;
            }
        }
        else
        {
            row = ResolveAnchorRow(opt.Anchor ?? OverlayAnchor.Center, effectiveHeight, availHeight, marginTop);
        }

        int col;
        if (opt.Col is { } colValue)
        {
            if (colValue.IsPercent)
            {
                var maxCol = Math.Max(0, availWidth - widthInt);
                col = marginLeft + (int)Math.Floor(maxCol * colValue.Value / 100.0);
            }
            else
            {
                col = (int)colValue.Value;
            }
        }
        else
        {
            col = ResolveAnchorCol(opt.Anchor ?? OverlayAnchor.Center, widthInt, availWidth, marginLeft);
        }

        if (opt.OffsetY is { } offsetY)
        {
            row += offsetY;
        }
        if (opt.OffsetX is { } offsetX)
        {
            col += offsetX;
        }

        row = Math.Max(marginTop, Math.Min(row, termHeight - marginBottom - effectiveHeight));
        col = Math.Max(marginLeft, Math.Min(col, termWidth - marginRight - widthInt));

        return (widthInt, row, col, maxHeight is { } mh2 ? (int)mh2 : null);
    }

    private static int ResolveAnchorRow(OverlayAnchor anchor, int height, int availHeight, int marginTop) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => marginTop,
        OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight => marginTop + availHeight - height,
        _ => marginTop + (availHeight - height) / 2,
    };

    private static int ResolveAnchorCol(OverlayAnchor anchor, int width, int availWidth, int marginLeft) => anchor switch
    {
        OverlayAnchor.TopLeft or OverlayAnchor.LeftCenter or OverlayAnchor.BottomLeft => marginLeft,
        OverlayAnchor.TopRight or OverlayAnchor.RightCenter or OverlayAnchor.BottomRight => marginLeft + availWidth - width,
        _ => marginLeft + (availWidth - width) / 2,
    };

    protected string[] ApplyLineResets(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = Ansi.NormalizeTerminalOutput(lines[i]) + Tui.SegmentReset;
        }
        return lines;
    }

    protected (int Row, int Col)? ExtractCursorPosition(string[] lines, int height)
    {
        var viewportTop = Math.Max(0, lines.Length - height);
        for (var row = lines.Length - 1; row >= viewportTop; row--)
        {
            var line = lines[row];
            var markerIndex = line.IndexOf(Tui.CursorMarker, StringComparison.Ordinal);
            if (markerIndex != -1)
            {
                var beforeMarker = line.Substring(0, markerIndex);
                var col = UnicodeWidth.VisibleWidth(beforeMarker);
                lines[row] = line.Substring(0, markerIndex) + line.Substring(markerIndex + Tui.CursorMarker.Length);
                return (row, col);
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Terminal color queries (simplified)
    // ------------------------------------------------------------------

    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
        if (_terminalColorSchemeNotificationsEnabled == enabled)
        {
            return;
        }
        _terminalColorSchemeNotificationsEnabled = enabled;
        if (!_stopped)
        {
            Terminal.Write(enabled ? "\x1b[?2031h" : "\x1b[?2031l");
        }
    }

    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener)
    {
        _colorSchemeListeners.Add((listener, () => { }));
        return () => _colorSchemeListeners.RemoveAll(x => x.Listener == listener);
    }

    public Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<RgbColor?>();
        var query = new PendingOsc11Query(tcs);
        _pendingOsc11Queries.Enqueue(query);
        _pendingOsc11Replies++;
        query.Timer = new Timer(_ => SettleOsc11Query(query, null), null, timeoutMs, Timeout.Infinite);
        Terminal.Write("\x1b]11;?\x07");
        return tcs.Task;
    }

    private void SettleOsc11Query(PendingOsc11Query query, RgbColor? rgb)
    {
        if (query.Settled)
        {
            return;
        }
        query.Settled = true;
        query.Timer?.Dispose();
        query.Tcs.TrySetResult(rgb);
    }

    public Task<TerminalColorScheme?> QueryTerminalColorSchemeAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<TerminalColorScheme?>();
        Action<TerminalColorScheme>? handler = null;
        Timer? timer = null;
        var settled = false;

        void Settle(TerminalColorScheme? scheme)
        {
            if (settled)
            {
                return;
            }
            settled = true;
            timer?.Dispose();
            if (handler is not null)
            {
                _colorSchemeListeners.RemoveAll(x => x.Listener == handler);
            }
            tcs.TrySetResult(scheme);
        }

        handler = scheme => Settle(scheme);
        _colorSchemeListeners.Add((handler, () => { }));
        timer = new Timer(_ => Settle(null), null, timeoutMs, Timeout.Infinite);
        Terminal.Write("\x1b[?996n");
        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // Nested overlay state
    // ------------------------------------------------------------------

    private enum OverlayFocusRestorePolicy
    {
        Clear,
        Preserve,
    }

    private sealed class OverlayStackEntry
    {
        public required IComponent Component { get; init; }
        public OverlayOptions? Options { get; init; }
        public required IComponent? PreFocus { get; set; }
        public required bool Hidden { get; set; }
        public required int FocusOrder { get; set; }
    }

    private abstract class OverlayFocusRestoreState
    {
        public static readonly InactiveState InactiveInstance = new();
        public abstract OverlayStackEntry? Overlay { get; }
        public static OverlayFocusRestoreState Inactive => InactiveInstance;

        public sealed class InactiveState : OverlayFocusRestoreState
        {
            public override OverlayStackEntry? Overlay => null;
        }
    }

    private sealed class EligibleOverlayFocusRestoreState : OverlayFocusRestoreState
    {
        public EligibleOverlayFocusRestoreState(OverlayStackEntry overlay) => Overlay = overlay;
        public override OverlayStackEntry Overlay { get; }
    }

    private sealed class BlockedOverlayFocusRestoreState : OverlayFocusRestoreState
    {
        public BlockedOverlayFocusRestoreState(OverlayStackEntry overlay, IComponent? blockedBy, OverlayFocusResume resume)
        {
            Overlay = overlay;
            BlockedBy = blockedBy;
            Resume = resume;
        }

        public override OverlayStackEntry Overlay { get; }
        public IComponent? BlockedBy { get; }
        public OverlayFocusResume Resume { get; }
    }

    private abstract class OverlayFocusResume
    {
    }

    private sealed class RestoreOverlayResume : OverlayFocusResume
    {
    }

    private sealed class FocusTargetResume : OverlayFocusResume
    {
        public FocusTargetResume(IComponent? target) => Target = target;
        public IComponent? Target { get; }
    }

    private sealed class PendingOsc11Query
    {
        public PendingOsc11Query(TaskCompletionSource<RgbColor?> tcs) => Tcs = tcs;
        public TaskCompletionSource<RgbColor?> Tcs { get; }
        public bool Settled { get; set; }
        public Timer? Timer { get; set; }
    }
}

/// <summary>Container - a component that contains other components.</summary>
public class Container : IComponent
{
    public List<IComponent> Children { get; } = new();

    public virtual void AddChild(IComponent component) => Children.Add(component);

    public virtual void RemoveChild(IComponent component) => Children.Remove(component);

    public virtual void Clear() => Children.Clear();

    public virtual void Invalidate()
    {
        foreach (var child in Children)
        {
            child.Invalidate();
        }
    }

    public virtual string[] Render(int width)
    {
        var lines = new List<string>();
        foreach (var child in Children)
        {
            lines.AddRange(child.Render(width));
        }
        return lines.ToArray();
    }
}

/// <summary>Static helpers from <c>tui.ts</c>.</summary>
public static class Tui
{
    public const string CursorMarker = "\x1b_pi:c\x07";

    /// <summary>Segment reset: SGR reset plus OSC 8 hyperlink close.</summary>
    public const string SegmentReset = "\x1b[0m\x1b]8;;\x07";

    /// <summary>Type guard for <see cref="IFocusable"/>.</summary>
    public static bool IsFocusable(IComponent? component) => component is IFocusable;

    /// <summary>Composite overlay content into a terminal line at a fixed column.</summary>
    public static string CompositeTuiLine(string baseLine, string overlayLine, int startCol, int overlayWidth, int totalWidth)
    {
        if (TerminalImage.IsImageLine(baseLine))
        {
            return baseLine;
        }

        var afterStart = startCol + overlayWidth;
        var (before, beforeWidth, after, afterWidth) =
            TextLayout.ExtractSegments(baseLine, startCol, afterStart, totalWidth - afterStart, strictAfter: true);
        var (overlayText, overlayTextWidth) = TextLayout.SliceWithWidth(overlayLine, 0, overlayWidth, strict: true);
        var beforePad = Math.Max(0, startCol - beforeWidth);
        var overlayPad = Math.Max(0, overlayWidth - overlayTextWidth);
        var actualBeforeWidth = Math.Max(startCol, beforeWidth);
        var actualOverlayWidth = Math.Max(overlayWidth, overlayTextWidth);
        var afterTarget = Math.Max(0, totalWidth - actualBeforeWidth - actualOverlayWidth);
        var afterPad = Math.Max(0, afterTarget - afterWidth);
        var result = before
            + new string(' ', beforePad)
            + SegmentReset
            + overlayText
            + new string(' ', overlayPad)
            + SegmentReset
            + after
            + new string(' ', afterPad);

        return UnicodeWidth.VisibleWidth(result) <= totalWidth
            ? result
            : TextLayout.SliceByColumn(result, 0, totalWidth, strict: true);
    }
}
