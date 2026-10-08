using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

public class ComponentTests
{
    [Fact]
    public void Text_WrapsPadsAndAddsVerticalPadding()
    {
        var lines = new Text("hello", 1, 1).Render(20);
        Assert.Equal(3, lines.Length);
        Assert.Equal(20, UnicodeWidth.VisibleWidth(lines[0]));
        Assert.Equal(20, UnicodeWidth.VisibleWidth(lines[1]));
        Assert.Equal(" hello", Ansi.StripTerminalSequences(lines[1]).TrimEnd());
    }

    [Fact]
    public void Text_EmptyRendersNothing()
    {
        Assert.Empty(new Text("   ").Render(10));
    }

    [Fact]
    public void Text_AppliesCustomBackground()
    {
        var lines = new Text("hi", 0, 0, s => $"[{s}]").Render(4);
        Assert.Single(lines);
        Assert.StartsWith("[", lines[0]);
        Assert.EndsWith("]", lines[0]);
    }

    [Fact]
    public void Spacer_RendersEmptyLines()
    {
        var lines = new Spacer(3).Render(10);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal("", l));
    }

    [Fact]
    public void Box_AddsPaddingAroundChildren()
    {
        var box = new Box(1, 1);
        box.AddChild(new Text("hi", 0, 0));
        var lines = box.Render(10);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal(10, UnicodeWidth.VisibleWidth(l)));
        Assert.Contains("hi", Ansi.StripTerminalSequences(lines[1]));
    }

    [Fact]
    public void TruncatedText_TruncatesSingleLine()
    {
        var lines = new TruncatedText("hello world", 0, 0).Render(8);
        Assert.Single(lines);
        Assert.Equal(8, UnicodeWidth.VisibleWidth(lines[0]));
        Assert.Equal("hello...", Ansi.StripTerminalSequences(lines[0]));
    }

    [Fact]
    public void VStack_StacksChildrenVertically()
    {
        var stack = new VStack(new IComponent[]
        {
            new Text("a", 0, 0),
            new Text("b", 0, 0),
        });
        var lines = stack.Render(5);
        Assert.Equal(2, lines.Length);
        Assert.Equal("a", Ansi.StripTerminalSequences(lines[0]).TrimEnd());
        Assert.Equal("b", Ansi.StripTerminalSequences(lines[1]).TrimEnd());
    }

    [Fact]
    public void VStack_HonoursGap()
    {
        var stack = new VStack(new IComponent[]
        {
            new Text("a", 0, 0),
            new Text("b", 0, 0),
        }, new StackOptions { Gap = 1 });
        var lines = stack.Render(5);
        Assert.Equal(3, lines.Length);
        Assert.Equal("", lines[1]);
    }

    [Fact]
    public void HStack_PlacesChildrenSideBySide()
    {
        var stack = new HStack(new IComponent[]
        {
            new Text("a", 0, 0),
            new Text("b", 0, 0),
        });
        var lines = stack.Render(10);
        Assert.Single(lines);
        Assert.True(UnicodeWidth.VisibleWidth(lines[0]) <= 10);
        var stripped = Ansi.StripTerminalSequences(lines[0]);
        Assert.Contains("a", stripped);
        Assert.Contains("b", stripped);
    }

    [Fact]
    public void Stack_VisiblePredicateHidesEntries()
    {
        var hidden = new Text("hidden", 0, 0);
        var shown = new Text("shown", 0, 0);
        var stack = new VStack();
        stack.AddChild(shown);
        stack.AddChild(hidden, new StackEntryOptions { Visible = _ => false });
        var lines = stack.Render(10);
        Assert.Single(lines);
        Assert.Contains("shown", Ansi.StripTerminalSequences(lines[0]));
    }

    [Fact]
    public void Container_InvalidatesChildren()
    {
        var child = new Text("hi", 0, 0);
        var container = new Container();
        container.AddChild(child);
        container.Invalidate(); // should not throw
        Assert.Single(container.Children);
    }
}

public class TuiMainScreenTests
{
    [Fact]
    public void FirstRender_WritesContentInSynchronizedOutput()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new Text("hello", 0, 0));
        tui.RenderNow();

        Assert.StartsWith("\x1b[?2026h", terminal.Output);
        // The synchronized-output close bracket is emitted before the hardware-cursor update, so the
        // frame is not the very last write.
        Assert.Contains("\x1b[?2026l", terminal.Output);
        Assert.Contains("hello", terminal.Output);
    }

    [Fact]
    public void SecondRender_OnlyEmitsChangedLines()
    {
        var terminal = new StringTerminal(20, 5);
        var text = new Text("hello", 0, 0);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(text);
        tui.RenderNow();

        terminal.ClearOutput();
        text.SetText("world");
        tui.RenderNow();

        var output = terminal.Output;
        Assert.Contains("world", output);
        Assert.DoesNotContain("hello", output);
    }

    [Fact]
    public void WidthChange_TriggersFullRedraw()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new Text("hello", 0, 0));
        tui.RenderNow();

        terminal.ClearOutput();
        terminal.Resize(30, 5);
        tui.RenderNow();

        Assert.Contains("\x1b[2J", terminal.Output);
        Assert.Contains("hello", terminal.Output);
    }

    [Fact]
    public void OverWideLine_ThrowsAndStops()
    {
        var terminal = new StringTerminal(5, 5);
        var tui = new TuiMainScreen(terminal);
        var raw = new RawComponent("ok");
        tui.AddChild(raw);
        tui.RenderNow(); // first render writes without the width guard

        raw.Line = "this line is far too wide";
        Assert.Throws<InvalidOperationException>(() => tui.RenderNow());
    }

    [Fact]
    public void FullRedraws_CountsClearedFrames()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new Text("hello", 0, 0));
        tui.RenderNow();
        Assert.Equal(1, tui.FullRedraws); // first render counts as a full render
        terminal.Resize(30, 5);
        tui.RenderNow();
        Assert.Equal(2, tui.FullRedraws);
    }

    [Fact]
    public void Overlay_CompositesOverContent()
    {
        var terminal = new StringTerminal(10, 3);
        var tui = new TuiMainScreen(terminal);
        tui.AddChild(new Text("base", 0, 0));
        tui.RenderNow();

        terminal.ClearOutput();
        tui.ShowOverlay(new Text("OVR", 0, 0), new OverlayOptions { Anchor = OverlayAnchor.Center });
        tui.RenderNow();

        Assert.Contains("OVR", terminal.Output);
        tui.Stop();
    }

    private sealed class RawComponent : IComponent
    {
        public RawComponent(string line) => Line = line;

        public string Line { get; set; }

        public string[] Render(int width) => new[] { Line };
    }
}
