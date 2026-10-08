using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Minimal <see cref="ITui"/> stub that only records render requests, so <c>Loader</c> tests stay
/// deterministic (a real <c>TuiMainScreen</c> would schedule its own render timer).
/// </summary>
internal sealed class FakeTui : ITui
{
    public int RenderRequests { get; private set; }

    public TuiMode Mode => TuiMode.Regular;

    public List<IComponent> Children { get; } = [];

    public ITerminal Terminal { get; } = new StringTerminal();

    public Action? OnDebug { get; set; }

    public int FullRedraws => 0;

    public void AddChild(IComponent component) => Children.Add(component);

    public void RemoveChild(IComponent component) => Children.Remove(component);

    public void Clear() => Children.Clear();

    public bool GetShowHardwareCursor() => false;

    public void SetShowHardwareCursor(bool enabled)
    {
    }

    public bool GetClearOnShrink() => false;

    public void SetClearOnShrink(bool enabled)
    {
    }

    public void SetFocus(IComponent? component)
    {
    }

    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => null!;

    public void HideOverlay()
    {
    }

    public bool HasOverlay() => false;

    public void Start()
    {
    }

    public void Stop(TuiStopOptions? options = null)
    {
    }

    public void RenderNow(bool force = false)
    {
    }

    public void RequestRender(bool force = false) => RenderRequests++;

    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => () => { };

    public void RemoveInputListener(Func<string, TuiInputListenerResult?> listener)
    {
    }

    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => () => { };

    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
    }

    public Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs) => Task.FromResult<RgbColor?>(null);

    public Task<TerminalColorScheme?> QueryTerminalColorSchemeAsync(int timeoutMs) => Task.FromResult<TerminalColorScheme?>(null);

    public string[] Render(int width) => [];

    public void HandleInput(string data)
    {
    }

    public void Invalidate()
    {
    }
}

public class LoaderTests
{
    private const int Width = 40;

    private static (Loader Loader, FakeTui Tui) Create(
        string message = "Loading...",
        LoaderIndicatorOptions? indicator = null,
        Func<string, string>? spinner = null,
        Func<string, string>? messageColor = null)
    {
        var tui = new FakeTui();
        var loader = new Loader(tui, spinner ?? (s => s), messageColor ?? (s => s), message, indicator);
        return (loader, tui);
    }

    /// <summary>The rendered single content line, trimmed (line 0 is the blank prefix line).</summary>
    private static string Line(Loader loader) => loader.Render(Width)[1].Trim();

    [Fact]
    public void Render_PrependsABlankLineAndPadsToWidth()
    {
        var (loader, _) = Create();
        var lines = loader.Render(Width);

        Assert.Equal(2, lines.Length);
        Assert.Equal("", lines[0]);
        Assert.Equal(Width, lines[1].Length);
        Assert.Equal("⠋ Loading...", lines[1].Trim());
    }

    [Fact]
    public void Constructor_UsesDefaultFramesAndSpinnerColouring()
    {
        var (loader, _) = Create(spinner: s => $"<{s}>");

        // No indicator options were supplied, so renderIndicatorVerbatim is false and the spinner
        // colour function is applied to the frame.
        Assert.Equal("<⠋> Loading...", Line(loader));
    }

    [Fact]
    public void SetIndicator_SuppliedOptionsRenderTheFrameVerbatim()
    {
        var (loader, _) = Create(spinner: s => $"<{s}>", indicator: new LoaderIndicatorOptions { Frames = ["*"] });

        // A single frame means no animation, and the frame bypasses the spinner colour function.
        Assert.Equal("* Loading...", Line(loader));
    }

    [Fact]
    public void SetIndicator_EmptyFramesHidesTheIndicator()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = [] });

        Assert.Equal("Loading...", Line(loader));
    }

    [Fact]
    public void SetIndicator_CustomFramesAreUsed()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["A", "B"], IntervalMs = 5 });

        Assert.Equal("A Loading...", Line(loader));
    }

    [Fact]
    public void SetMessage_ReplacesTheMessageAndKeepsTheIndicator()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["*"] });

        loader.SetMessage("Working...");

        Assert.Equal("* Working...", Line(loader));
    }

    [Fact]
    public void MessageIsPassedThroughTheMessageColourFunction()
    {
        var (loader, _) = Create(messageColor: s => $"[{s}]", indicator: new LoaderIndicatorOptions { Frames = ["*"] });

        Assert.Equal("* [Loading...]", Line(loader));
    }

    [Fact]
    public void Invalidate_RefreshesTheDisplayedText()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["*"] });
        loader.SetMessage("first");

        // SetMessage already refreshes; Invalidate must not lose the current message.
        loader.Invalidate();

        Assert.Equal("* first", Line(loader));
    }

    [Fact]
    public void UpdatesRequestARender()
    {
        var (loader, tui) = Create(indicator: new LoaderIndicatorOptions { Frames = ["*"] });
        var before = tui.RenderRequests;

        loader.SetMessage("another");

        Assert.True(tui.RenderRequests > before);
    }

    [Fact]
    public void SingleFrameDoesNotAnimate()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["*"], IntervalMs = 5 });
        var first = Line(loader);

        Thread.Sleep(60);

        Assert.Equal(first, Line(loader));
        loader.Stop();
    }

    [Fact]
    public void Start_AnimatesThroughTheFrames()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 5 });

        try
        {
            var seen = new HashSet<string> { Line(loader) };
            var deadline = Environment.TickCount64 + 3000;
            while (seen.Count < 3 && Environment.TickCount64 < deadline)
            {
                seen.Add(Line(loader));
                Thread.Sleep(5);
            }

            Assert.Equal(3, seen.Count);
        }
        finally
        {
            loader.Stop();
        }
    }

    [Fact]
    public void Stop_HaltsTheAnimation()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 5 });
        Thread.Sleep(30);

        loader.Stop();
        var frozen = Line(loader);
        Thread.Sleep(60);

        Assert.Equal(frozen, Line(loader));
    }

    [Fact]
    public void SetIndicator_RestartsTheAnimationFromTheFirstFrame()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 5 });
        Thread.Sleep(40);

        loader.SetIndicator(new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 5 });

        Assert.Equal("a Loading...", Line(loader));
        loader.Stop();
    }

    [Fact]
    public void StartIsIdempotentAndDoesNotStackTimers()
    {
        var (loader, _) = Create(indicator: new LoaderIndicatorOptions { Frames = ["a", "b", "c"], IntervalMs = 5 });

        loader.Start();
        loader.Start();
        loader.Start();

        Assert.Equal("a Loading...", Line(loader));
        loader.Stop();
    }
}

public class CancellableLoaderTests
{
    private const int Width = 40;

    private static (CancellableLoader Loader, FakeTui Tui) Create()
    {
        var tui = new FakeTui();
        var loader = new CancellableLoader(
            tui,
            s => s,
            s => s,
            "Working...",
            new LoaderIndicatorOptions { Frames = ["*"] });
        return (loader, tui);
    }

    [Fact]
    public void StartsNotAborted()
    {
        var (loader, _) = Create();

        Assert.False(loader.Aborted);
        Assert.False(loader.Signal.IsCancellationRequested);
        loader.Dispose();
    }

    [Fact]
    public void Escape_AbortsAndInvokesTheHandler()
    {
        var (loader, _) = Create();
        var aborted = 0;
        loader.OnAbort = () => aborted++;

        loader.HandleInput("\x1b");

        Assert.True(loader.Aborted);
        Assert.True(loader.Signal.IsCancellationRequested);
        Assert.Equal(1, aborted);
        loader.Dispose();
    }

    [Fact]
    public void CtrlC_AlsoAborts()
    {
        var (loader, _) = Create();
        loader.OnAbort = () => { };

        loader.HandleInput("\x03");

        Assert.True(loader.Aborted);
        loader.Dispose();
    }

    [Fact]
    public void OtherKeysDoNotAbort()
    {
        var (loader, _) = Create();
        var aborted = 0;
        loader.OnAbort = () => aborted++;

        loader.HandleInput("a");
        loader.HandleInput("\r");

        Assert.False(loader.Aborted);
        Assert.Equal(0, aborted);
        loader.Dispose();
    }

    [Fact]
    public void AbortWithoutAHandlerDoesNotThrow()
    {
        var (loader, _) = Create();

        loader.HandleInput("\x1b");

        Assert.True(loader.Aborted);
        loader.Dispose();
    }

    [Fact]
    public void DisposeStopsTheAnimationButLeavesTheTokenUsable()
    {
        var (loader, _) = Create();
        Thread.Sleep(20);

        loader.Dispose();
        var frozen = loader.Render(Width)[1];
        Thread.Sleep(60);

        Assert.Equal(frozen, loader.Render(Width)[1]);
        // Matching TS dispose() (which only calls stop()), the token is still observable afterwards.
        Assert.False(loader.Aborted);
    }

    [Fact]
    public void InheritsTheLoaderRendering()
    {
        var (loader, _) = Create();
        var lines = loader.Render(Width);

        Assert.Equal("", lines[0]);
        Assert.Equal("* Working...", lines[1].Trim());
        loader.Dispose();
    }
}
