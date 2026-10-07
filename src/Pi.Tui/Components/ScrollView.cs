namespace Pi.Tui.Components;

/// <summary>Scrollbar visibility mode (port of <c>ScrollViewScrollbar</c>).</summary>
public enum ScrollViewScrollbar
{
    Hidden,
    Auto,
    Always,
}

/// <summary>Options for <see cref="ScrollView"/> (port of <c>ScrollViewOptions</c>).</summary>
public sealed class ScrollViewOptions
{
    /// <summary>Only <c>"vertical"</c> is supported; anything else throws.</summary>
    public string? Axis { get; init; }

    /// <summary><c>"end"</c> keeps the view pinned to the bottom while content grows.</summary>
    public string? Follow { get; init; }

    public bool Primary { get; init; }

    public ScrollOverscroll Overscroll { get; init; } = ScrollOverscroll.Chain;

    public ScrollViewScrollbar Scrollbar { get; init; } = ScrollViewScrollbar.Hidden;

    public Func<string, string>? ScrollbarTrackStyle { get; init; }

    public Func<string, string>? ScrollbarThumbStyle { get; init; }

    public int? ScrollbarHideDelayMs { get; init; }
}

/// <summary>Options for <see cref="ScrollView.ScrollTo"/>.</summary>
public sealed class ScrollViewScrollToOptions
{
    /// <summary>Keep follow-end disabled even when the target is the current content end.</summary>
    public bool DisableFollow { get; init; }
}

/// <summary>
/// A vertically scrolling viewport wrapping exactly one child (port of <c>components/scroll-view.ts</c>).
/// </summary>
/// <remarks>
/// Deviation from TS (T14): the transient-scrollbar <c>setTimeout</c> becomes a
/// <see cref="System.Threading.Timer"/>. Node's <c>unref()</c> has no .NET equivalent, but a
/// <see cref="System.Threading.Timer"/> likewise does not keep the process alive.
/// </remarks>
public sealed class ScrollView : Container, ILayoutComponent, IScrollLayoutState
{
    private readonly IComponent _child;
    private readonly Func<string, string> _scrollbarTrackStyle;
    private readonly Func<string, string> _scrollbarThumbStyle;
    private readonly int _scrollbarHideDelayMs;
    private readonly object _gate = new();

    private ScrollViewScrollbar _currentScrollbar;
    private int _currentScrollTop;
    private int _contentHeight;
    private int _currentViewportHeight;
    private bool _followingEnd;
    private bool _followSuppressedAtEnd;
    private Action? _requestRenderCallback;
    private bool _transientScrollbarVisible;
    private bool _scrollbarActive;
    private Timer? _scrollbarHideTimer;

    public ScrollView(IComponent component, ScrollViewOptions? options = null)
    {
        options ??= new ScrollViewOptions();
        if (options.Axis is not null && options.Axis != "vertical")
        {
            throw new ArgumentException($"Unsupported ScrollView axis: {options.Axis}", nameof(options));
        }

        _child = component;
        Children.Add(component);
        FollowEnd = options.Follow == "end";
        _followingEnd = FollowEnd;
        Primary = options.Primary;
        Overscroll = options.Overscroll;
        _currentScrollbar = options.Scrollbar;
        _scrollbarTrackStyle = options.ScrollbarTrackStyle ?? (text => $"\x1b[90m{text}\x1b[39m");
        _scrollbarThumbStyle = options.ScrollbarThumbStyle ?? (text => $"\x1b[37m{text}\x1b[39m");
        _scrollbarHideDelayMs = Math.Max(0, options.ScrollbarHideDelayMs ?? 1000);
    }

    /// <summary>The wrapped child component.</summary>
    public IComponent Child => _child;

    /// <summary>Whether the view pins itself to the end of the content.</summary>
    public bool FollowEnd { get; }

    public bool Primary { get; }

    public ScrollOverscroll Overscroll { get; }

    public Func<string, string> ScrollbarTrackStyle => _scrollbarTrackStyle;

    public Func<string, string> ScrollbarThumbStyle => _scrollbarThumbStyle;

    public int ScrollTop => _currentScrollTop;

    public bool IsFollowingEnd => _followingEnd;

    public int ViewportHeight => _currentViewportHeight;

    public ScrollViewScrollbar Scrollbar => _currentScrollbar;

    public bool IsScrollbarVisible
    {
        get
        {
            if (Scrollbar == ScrollViewScrollbar.Always)
            {
                return _currentViewportHeight > 0;
            }
            return Scrollbar == ScrollViewScrollbar.Auto
                && _contentHeight > _currentViewportHeight
                && _transientScrollbarVisible;
        }
    }

    public bool IsScrollbarActive => _scrollbarActive;

    public void SetScrollbar(ScrollViewScrollbar scrollbar)
    {
        if (scrollbar == _currentScrollbar)
        {
            return;
        }
        _currentScrollbar = scrollbar;
        if (scrollbar != ScrollViewScrollbar.Auto)
        {
            HideTransientScrollbar();
        }
        else if (_scrollbarActive)
        {
            MarkScrollbarActivity();
        }
        _requestRenderCallback?.Invoke();
    }

    public int GetContentWidth(int width) =>
        Scrollbar == ScrollViewScrollbar.Always && width > 1 ? width - 1 : width;

    private void MarkScrollbarActivity()
    {
        if (Scrollbar != ScrollViewScrollbar.Auto || _contentHeight <= _currentViewportHeight)
        {
            return;
        }
        _transientScrollbarVisible = true;
        lock (_gate)
        {
            _scrollbarHideTimer?.Dispose();
            _scrollbarHideTimer = null;
            if (_scrollbarActive)
            {
                return;
            }
            _scrollbarHideTimer = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        _scrollbarHideTimer = null;
                        _transientScrollbarVisible = false;
                    }
                    _requestRenderCallback?.Invoke();
                },
                null,
                _scrollbarHideDelayMs,
                Timeout.Infinite);
        }
    }

    private void HideTransientScrollbar()
    {
        _transientScrollbarVisible = false;
        lock (_gate)
        {
            if (_scrollbarHideTimer is null)
            {
                return;
            }
            _scrollbarHideTimer.Dispose();
            _scrollbarHideTimer = null;
        }
    }

    public void SetScrollbarActive(bool active)
    {
        if (active == _scrollbarActive)
        {
            return;
        }
        _scrollbarActive = active;
        MarkScrollbarActivity();
        _requestRenderCallback?.Invoke();
    }

    public void ScrollTo(int scrollTop, ScrollViewScrollToOptions? options = null)
    {
        options ??= new ScrollViewScrollToOptions();
        var maxScrollTop = Math.Max(0, _contentHeight - _currentViewportHeight);
        var next = Math.Max(0, Math.Min(maxScrollTop, scrollTop));
        var nextFollowSuppressedAtEnd = options.DisableFollow && next == maxScrollTop;
        var nextFollowingEnd = !nextFollowSuppressedAtEnd && FollowEnd && next == maxScrollTop;
        if (next == _currentScrollTop
            && nextFollowingEnd == _followingEnd
            && nextFollowSuppressedAtEnd == _followSuppressedAtEnd)
        {
            return;
        }
        var moved = next != _currentScrollTop;
        _currentScrollTop = next;
        _followingEnd = nextFollowingEnd;
        _followSuppressedAtEnd = nextFollowSuppressedAtEnd;
        if (moved)
        {
            MarkScrollbarActivity();
        }
        _requestRenderCallback?.Invoke();
    }

    /// <summary>Scroll by <paramref name="lines"/>. Returns the unconsumed remainder.</summary>
    public int ScrollBy(int lines)
    {
        var requested = lines;
        if (requested == 0)
        {
            return 0;
        }
        var maxScrollTop = Math.Max(0, _contentHeight - _currentViewportHeight);
        var start = _followingEnd ? maxScrollTop : _currentScrollTop;
        var next = Math.Max(0, Math.Min(maxScrollTop, start + requested));
        var moved = next - start;
        var wasFollowingEnd = _followingEnd;
        _currentScrollTop = next;
        _followingEnd = FollowEnd && next == maxScrollTop;
        _followSuppressedAtEnd = false;
        if (moved != 0)
        {
            MarkScrollbarActivity();
        }
        if (moved != 0 || _followingEnd != wasFollowingEnd)
        {
            _requestRenderCallback?.Invoke();
        }
        return requested - moved;
    }

    public void ScrollToStart()
    {
        var atEndBecauseContentFits = FollowEnd && _contentHeight <= _currentViewportHeight;
        var changed = _currentScrollTop != 0 || _followingEnd != atEndBecauseContentFits;
        _currentScrollTop = 0;
        _followingEnd = atEndBecauseContentFits;
        _followSuppressedAtEnd = false;
        if (changed)
        {
            MarkScrollbarActivity();
            _requestRenderCallback?.Invoke();
        }
    }

    public void ScrollToEnd()
    {
        var next = Math.Max(0, _contentHeight - _currentViewportHeight);
        var changed = _currentScrollTop != next || _followingEnd != FollowEnd;
        _currentScrollTop = next;
        _followingEnd = FollowEnd;
        _followSuppressedAtEnd = false;
        if (changed)
        {
            MarkScrollbarActivity();
            _requestRenderCallback?.Invoke();
        }
    }

    public void UpdateLayout(int contentHeight, int viewportHeight, Action requestRender)
    {
        _contentHeight = Math.Max(0, contentHeight);
        _currentViewportHeight = Math.Max(0, viewportHeight);
        _requestRenderCallback = requestRender;
        var maxScrollTop = Math.Max(0, _contentHeight - _currentViewportHeight);
        _currentScrollTop = _followingEnd ? maxScrollTop : Math.Max(0, Math.Min(_currentScrollTop, maxScrollTop));
        if (_currentScrollTop < maxScrollTop)
        {
            _followSuppressedAtEnd = false;
        }
        if (FollowEnd && _currentScrollTop == maxScrollTop && !_followSuppressedAtEnd)
        {
            _followingEnd = true;
        }
        if (_contentHeight <= _currentViewportHeight)
        {
            HideTransientScrollbar();
        }
    }

    public override void AddChild(IComponent component) => throw new InvalidOperationException("ScrollView has exactly one child");

    public override void RemoveChild(IComponent component) => throw new InvalidOperationException("ScrollView child cannot be removed");

    public override void Clear() => throw new InvalidOperationException("ScrollView child cannot be cleared");

    public override string[] Render(int width)
    {
        var contentWidth = GetContentWidth(width);
        var lines = _child.Render(contentWidth);
        if (contentWidth == width)
        {
            return lines;
        }
        var padded = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            padded[i] = lines[i] + " ";
        }
        return padded;
    }

    public ILayoutNode GetLayoutNode() => new ScrollLayoutNode { Component = _child, State = this };
}
