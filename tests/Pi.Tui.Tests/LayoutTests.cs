using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Component that renders a fixed set of lines at any width (no padding), for layout tests.</summary>
internal sealed class FixedLines : IComponent
{
    private readonly string[] _lines;

    public FixedLines(params string[] lines) => _lines = lines;

    public string[] Render(int width) => _lines;

    public void Invalidate()
    {
    }
}

public class LayoutLeafTests
{
    [Fact]
    public void LeafBox_GetsItsIntrinsicHeightAndClipIntersection()
    {
        var child = new FixedLines("a", "b");
        var frame = Layout.RenderLayoutFrame(child, 10, 5, () => { });

        Assert.Equal(10, frame.Width);
        Assert.Equal(5, frame.Height);
        Assert.Equal(0, frame.Root.Rect.Y);
        Assert.Equal(5, frame.Root.Rect.Height);
        Assert.Equal(10, frame.Root.Clip.Width);
        Assert.Same(child, frame.Root.Component);
        Assert.Null(frame.PrimaryScrollView);
    }

    [Fact]
    public void Paint_OnlyWritesRowsThatHaveSourceLines()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("a", "b"), 10, 5, () => { });

        Assert.Equal(["a", "b", "", "", ""], frame.Lines);
    }

    [Fact]
    public void RootWidthIsClampedToAtLeastOne()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("a"), 0, 0, () => { });

        Assert.Equal(1, frame.Width);
        Assert.Equal(1, frame.Height);
    }

    [Fact]
    public void CursorMarkerLineIsScrolledIntoViewWhenTheBoxIsClipped()
    {
        var cursor = Tui.CursorMarker;
        var child = new FixedLines("one", "two", "three", $"four{cursor}");
        var root = new VStack(
            [StackChild.Of(child, new StackEntryOptions { Basis = 2 })],
            new StackOptions { Align = StackAlign.Stretch });

        var frame = Layout.RenderLayoutFrame(root, 10, 4, () => { });

        var leaf = frame.Root.Children[0];
        Assert.Equal(2, leaf.Rect.Height);
        Assert.Equal(2, leaf.LineOffset);
        // The box paints lines 2 and 3, so the cursor line stays on screen.
        Assert.StartsWith("three", frame.Lines[0]);
        Assert.StartsWith("four", frame.Lines[1]);
    }
}

public class LayoutStackTests
{
    [Fact]
    public void VStack_StacksChildrenVertically()
    {
        var root = new VStack([new FixedLines("a", "b"), new FixedLines("c")]);
        var frame = Layout.RenderLayoutFrame(root, 10, 5, () => { });

        Assert.Equal(2, frame.Root.Children.Count);
        Assert.Equal(0, frame.Root.Children[0].Rect.Y);
        Assert.Equal(2, frame.Root.Children[0].Rect.Height);
        Assert.Equal(2, frame.Root.Children[1].Rect.Y);
        Assert.Equal(1, frame.Root.Children[1].Rect.Height);
        Assert.Equal(["a", "b", "c", "", ""], frame.Lines);
    }

    [Fact]
    public void VStack_HonoursTheGap()
    {
        var root = new VStack([new FixedLines("a"), new FixedLines("b")], new StackOptions { Gap = 1 });
        var frame = Layout.RenderLayoutFrame(root, 10, 5, () => { });

        Assert.Equal(0, frame.Root.Children[0].Rect.Y);
        Assert.Equal(2, frame.Root.Children[1].Rect.Y);
        Assert.Equal(["a", "", "b", "", ""], frame.Lines);
    }

    [Fact]
    public void VStack_GrowsChildrenToFillTheAvailableHeight()
    {
        var root = new VStack(
            [StackChild.Of(new FixedLines("a"), new StackEntryOptions { Grow = 1 })],
            new StackOptions { Align = StackAlign.Stretch });

        var frame = Layout.RenderLayoutFrame(root, 10, 4, () => { });

        Assert.Equal(4, frame.Root.Children[0].Rect.Height);
    }

    [Fact]
    public void VStack_HiddenEntriesAreSkippedAndLeaveNoGap()
    {
        var root = new VStack(
            [
                StackChild.Of(new FixedLines("a")),
                StackChild.Of(new FixedLines("hidden"), new StackEntryOptions { Visible = _ => false }),
                StackChild.Of(new FixedLines("b")),
            ],
            new StackOptions { Gap = 1 });

        var frame = Layout.RenderLayoutFrame(root, 10, 5, () => { });

        Assert.Equal(2, frame.Root.Children.Count);
        Assert.Equal(0, frame.Root.Children[0].Rect.Y);
        Assert.Equal(2, frame.Root.Children[1].Rect.Y);
        Assert.Equal(["a", "", "b", "", ""], frame.Lines);
    }

    [Fact]
    public void HStack_LaysChildrenSideBySide()
    {
        var root = new HStack([new FixedLines("aa"), new FixedLines("bb")]);
        var frame = Layout.RenderLayoutFrame(root, 10, 2, () => { });

        Assert.Equal(2, frame.Root.Children.Count);
        Assert.Equal(0, frame.Root.Children[0].Rect.X);
        Assert.Equal(2, frame.Root.Children[1].Rect.X);
        Assert.Equal("aabb", Ansi.StripTerminalSequences(frame.Lines[0]).TrimEnd());
    }

    [Fact]
    public void HStack_AlignCenterVerticallyCentresShorterChildren()
    {
        var root = new HStack(
            [new FixedLines("a")],
            new StackOptions { Align = StackAlign.Center });

        var frame = Layout.RenderLayoutFrame(root, 10, 4, () => { });

        var child = frame.Root.Children[0];
        // allocatedCrossHeight is the box height (4); a single-line child centres at floor(3/2) == 1.
        Assert.Equal(1, child.Rect.Y);
        Assert.Equal(1, child.Rect.Height);
        Assert.Equal("", frame.Lines[0]);
        // A narrow child is composited into the full row, so padding and reset sequences are present.
        Assert.Equal("a", Ansi.StripTerminalSequences(frame.Lines[1]).TrimEnd());
    }

    [Fact]
    public void HStack_AlignEndPushesChildrenToTheBottom()
    {
        var root = new HStack(
            [new FixedLines("a")],
            new StackOptions { Align = StackAlign.End });

        var frame = Layout.RenderLayoutFrame(root, 10, 4, () => { });

        Assert.Equal(3, frame.Root.Children[0].Rect.Y);
        Assert.Equal("a", Ansi.StripTerminalSequences(frame.Lines[3]).TrimEnd());
    }

    [Fact]
    public void HStack_StretchGivesChildrenTheFullCrossHeight()
    {
        var root = new HStack([new FixedLines("a")], new StackOptions { Align = StackAlign.Stretch });
        var frame = Layout.RenderLayoutFrame(root, 10, 4, () => { });

        Assert.Equal(4, frame.Root.Children[0].Rect.Height);
    }

    [Fact]
    public void HStack_ZeroWidthChildrenKeepAnEmptyBox()
    {
        var root = new HStack(
            [
                StackChild.Of(new FixedLines("a"), new StackEntryOptions { Basis = 0 }),
                StackChild.Of(new FixedLines("b")),
            ]);

        var frame = Layout.RenderLayoutFrame(root, 10, 2, () => { });

        Assert.Equal(0, frame.Root.Children[0].Rect.Width);
        Assert.Equal(0, frame.Root.Children[0].Clip.Width);
        Assert.Equal(0, frame.Root.Children[1].Rect.X);
    }

    [Fact]
    public void StacksExposeLayoutNodes()
    {
        var vstack = new VStack([new FixedLines("a")]);
        var hstack = new HStack([new FixedLines("a")]);

        var vnode = Assert.IsType<StackLayoutNode>(LayoutNodes.GetLayoutNode(vstack));
        Assert.True(vnode.IsVertical);
        Assert.Single(vnode.Entries);

        var hnode = Assert.IsType<StackLayoutNode>(LayoutNodes.GetLayoutNode(hstack));
        Assert.False(hnode.IsVertical);
    }

    [Fact]
    public void LeafComponentsHaveNoLayoutNode()
    {
        Assert.Null(LayoutNodes.GetLayoutNode(new FixedLines("a")));
    }
}

public class ScrollViewLayoutTests
{
    private static FixedLines Content(int count) =>
        new([.. Enumerable.Range(0, count).Select(i => $"L{i}")]);

    [Fact]
    public void ScrollBox_ClipsContentToTheViewport()
    {
        var scrollView = new ScrollView(Content(10));
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.Equal(3, frame.Root.Rect.Height);
        Assert.Equal(10, frame.Root.Children[0].Rect.Height);
        Assert.Equal(["L0", "L1", "L2"], frame.Lines);
        Assert.Same(scrollView, frame.PrimaryScrollView);
    }

    [Fact]
    public void ScrollingTranslatesTheContentBox()
    {
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        scrollView.ScrollBy(5);
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.Equal(5, scrollView.ScrollTop);
        Assert.Equal(["L5", "L6", "L7"], frame.Lines);
    }

    [Fact]
    public void ScrollByClampsAndReturnsTheUnconsumedRemainder()
    {
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        // Scrolling past the end consumes only the available distance and reports the remainder,
        // matching TS `scrollBy`, which returns `requested - moved` (here -100 - (-7) == -93).
        Assert.Equal(93, scrollView.ScrollBy(100));
        Assert.Equal(7, scrollView.ScrollTop);
        Assert.Equal(-93, scrollView.ScrollBy(-100));
        Assert.Equal(0, scrollView.ScrollTop);
    }

    [Fact]
    public void FollowEndPinsToTheBottomAsContentGrows()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Follow = "end" });
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.True(scrollView.IsFollowingEnd);
        Assert.Equal(7, scrollView.ScrollTop);
    }

    [Fact]
    public void ScrollToWithDisableFollowSuppressesFollowEndAtTheBottom()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Follow = "end" });
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        scrollView.ScrollTo(7, new ScrollViewScrollToOptions { DisableFollow = true });

        Assert.False(scrollView.IsFollowingEnd);
        Assert.Equal(7, scrollView.ScrollTop);
    }

    [Fact]
    public void ScrollToStartAndEndMoveToTheBounds()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Follow = "end" });
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        scrollView.ScrollToStart();
        Assert.Equal(0, scrollView.ScrollTop);

        scrollView.ScrollToEnd();
        Assert.Equal(7, scrollView.ScrollTop);
        Assert.True(scrollView.IsFollowingEnd);
    }

    [Fact]
    public void ScrollToClampsIntoRange()
    {
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        scrollView.ScrollTo(999);
        Assert.Equal(7, scrollView.ScrollTop);

        scrollView.ScrollTo(-5);
        Assert.Equal(0, scrollView.ScrollTop);
    }

    [Fact]
    public void UpdateLayoutClampsScrollTopWhenContentShrinks()
    {
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });
        scrollView.ScrollTo(7);

        scrollView.UpdateLayout(4, 3, () => { });

        Assert.Equal(1, scrollView.ScrollTop);
    }

    [Fact]
    public void ScrollViewRejectsChildMutation()
    {
        var scrollView = new ScrollView(new FixedLines("a"));

        Assert.Throws<InvalidOperationException>(() => scrollView.AddChild(new FixedLines("b")));
        Assert.Throws<InvalidOperationException>(() => scrollView.RemoveChild(scrollView.Child));
        Assert.Throws<InvalidOperationException>(scrollView.Clear);
    }

    [Fact]
    public void UnsupportedAxisIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ScrollView(new FixedLines("a"), new ScrollViewOptions { Axis = "horizontal" }));
    }

    [Fact]
    public void AlwaysScrollbarReservesAColumnAndPadsTheContent()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Always });
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        // The content column shrinks by one, so the scrollbar fits in the reserved column.
        Assert.Equal(9, scrollView.GetContentWidth(10));
        Assert.True(scrollView.IsScrollbarVisible);
        Assert.Equal(10, UnicodeWidth.VisibleWidth(frame.Lines[0]));
        Assert.EndsWith("┃", Ansi.StripTerminalSequences(frame.Lines[0]));
    }

    [Fact]
    public void HiddenScrollbarUsesTheFullWidth()
    {
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.Equal(10, scrollView.GetContentWidth(10));
        Assert.False(scrollView.IsScrollbarVisible);
    }

    [Fact]
    public void AutoScrollbarStaysHiddenUntilScrolled()
    {
        var scrollView = new ScrollView(
            Content(10),
            new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Auto, ScrollbarHideDelayMs = 5000 });

        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });
        Assert.False(scrollView.IsScrollbarVisible);

        scrollView.ScrollBy(2);
        Assert.True(scrollView.IsScrollbarVisible);

        scrollView.ScrollToStart();
        Assert.True(scrollView.IsScrollbarVisible);
    }

    [Fact]
    public void AutoScrollbarHidesAfterTheDelay()
    {
        var scrollView = new ScrollView(
            Content(10),
            new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Auto, ScrollbarHideDelayMs = 20 });

        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });
        scrollView.ScrollBy(2);
        Assert.True(scrollView.IsScrollbarVisible);

        var deadline = Environment.TickCount64 + 3000;
        while (scrollView.IsScrollbarVisible && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.False(scrollView.IsScrollbarVisible);
    }

    [Fact]
    public void AutoScrollbarNeverShowsWhenContentFits()
    {
        var scrollView = new ScrollView(Content(2), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Auto });
        Layout.RenderLayoutFrame(scrollView, 10, 5, () => { });

        scrollView.ScrollBy(1);

        Assert.False(scrollView.IsScrollbarVisible);
    }

    [Fact]
    public void SetScrollbarTogglesVisibilityAndRequestsARender()
    {
        var renders = 0;
        var scrollView = new ScrollView(Content(10));
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => renders++);

        scrollView.SetScrollbar(ScrollViewScrollbar.Always);
        Assert.Equal(ScrollViewScrollbar.Always, scrollView.Scrollbar);
        Assert.True(renders > 0);

        var afterFirst = renders;
        scrollView.SetScrollbar(ScrollViewScrollbar.Always);
        Assert.Equal(afterFirst, renders);
    }

    [Fact]
    public void ScrollbarActivityIsTracked()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Always });
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.False(scrollView.IsScrollbarActive);
        scrollView.SetScrollbarActive(true);
        Assert.True(scrollView.IsScrollbarActive);
    }

    [Fact]
    public void ScrollViewExposesAScrollLayoutNode()
    {
        var child = new FixedLines("a");
        var scrollView = new ScrollView(child, new ScrollViewOptions { Primary = true });

        var node = Assert.IsType<ScrollLayoutNode>(LayoutNodes.GetLayoutNode(scrollView));
        Assert.Same(child, node.Component);
        Assert.Same(scrollView, node.State);
        Assert.True(node.State.Primary);
    }
}

public class ScrollbarGeometryTests
{
    private static FixedLines Content(int count) =>
        new([.. Enumerable.Range(0, count).Select(i => $"L{i}")]);

    [Fact]
    public void NoGeometryWithoutAScrollView()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("a"), 10, 3, () => { });

        Assert.Null(Layout.GetScrollbarGeometry(frame.Root));
    }

    [Fact]
    public void AlwaysScrollbarProducesGeometryAtTheRightEdge()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Always });
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var geometry = Layout.GetScrollbarGeometry(frame.Root);
        Assert.NotNull(geometry);
        Assert.Equal(9, geometry!.Value.Column);
        Assert.Equal(0, geometry.Value.TrackTop);
        Assert.Equal(3, geometry.Value.TrackHeight);
        Assert.Equal(2, geometry.Value.ThumbHeight);
        Assert.Equal(0, geometry.Value.ThumbTop);
        Assert.Equal(7, geometry.Value.MaxScrollTop);
    }

    [Fact]
    public void ThumbMovesWithScrollTop()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Always });
        Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });
        scrollView.ScrollTo(7);
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var geometry = Layout.GetScrollbarGeometry(frame.Root);
        Assert.Equal(1, geometry!.Value.ThumbTop);
    }

    [Fact]
    public void HiddenAutoCanBeRevealedExplicitly()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Auto });
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        Assert.Null(Layout.GetScrollbarGeometry(frame.Root));
        Assert.NotNull(Layout.GetScrollbarGeometry(frame.Root, includeHiddenAuto: true));
    }

    [Fact]
    public void ScrollbarIsPaintedIntoTheScreenBuffer()
    {
        var scrollView = new ScrollView(Content(10), new ScrollViewOptions { Scrollbar = ScrollViewScrollbar.Always });
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var row = Ansi.StripTerminalSequences(frame.Lines[0]);
        Assert.Equal(10, UnicodeWidth.VisibleWidth(row));
        Assert.EndsWith("┃", row);
    }

    [Fact]
    public void ReplaceScrollbarCellKeepsTheSurroundingTextAndAppendsTheGlyph()
    {
        var result = Layout.ReplaceScrollbarCell("abcdef", 3, 6, "│", true);

        var stripped = Ansi.StripTerminalSequences(result);
        Assert.Equal("abc│ef", stripped);
    }

    [Fact]
    public void ReplaceScrollbarCellLeavesImageLinesUntouched()
    {
        var imageLine = "\x1b_Ga=T;AAAA\x1b\\";

        Assert.Equal(imageLine, Layout.ReplaceScrollbarCell(imageLine, 3, 6, "│", true));
    }
}

public class LayoutHitTestingTests
{
    private static FixedLines Content(int count) =>
        new([.. Enumerable.Range(0, count).Select(i => $"L{i}")]);

    [Fact]
    public void GetLayoutBoxesAtReturnsInnermostFirst()
    {
        var child = Content(10);
        var scrollView = new ScrollView(child);
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var boxes = Layout.GetLayoutBoxesAt(frame, 0, 1);

        Assert.Equal(2, boxes.Count);
        Assert.Same(child, boxes[0].Component);
        Assert.Same(scrollView, boxes[1].Component);
    }

    [Fact]
    public void GetLayoutBoxesAtMissesOutsideTheClip()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("a"), 10, 3, () => { });

        Assert.Empty(Layout.GetLayoutBoxesAt(frame, 0, 99));
    }

    [Fact]
    public void GetScrollViewBoxFindsTheOwningBox()
    {
        var scrollView = new ScrollView(Content(10));
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var box = Layout.GetScrollViewBox(frame, scrollView);

        Assert.NotNull(box);
        Assert.Same(scrollView, box!.Component);
    }

    [Fact]
    public void GetScrollViewBoxReturnsNullForAnUnknownView()
    {
        var frame = Layout.RenderLayoutFrame(new ScrollView(Content(10)), 10, 3, () => { });

        Assert.Null(Layout.GetScrollViewBox(frame, new ScrollView(Content(2))));
    }

    [Fact]
    public void GetScrollViewsAtReportsTheScrollViewUnderThePoint()
    {
        var scrollView = new ScrollView(Content(10));
        var frame = Layout.RenderLayoutFrame(scrollView, 10, 3, () => { });

        var views = Layout.GetScrollViewsAt(frame, 0, 1);

        Assert.Single(views);
        Assert.Same(scrollView, views[0]);
        Assert.Empty(Layout.GetScrollViewsAt(frame, 0, 99));
    }

    [Fact]
    public void NestedScrollViewsAreReportedInnermostFirst()
    {
        var inner = new ScrollView(Content(10));
        var outer = new ScrollView(inner);
        var frame = Layout.RenderLayoutFrame(outer, 10, 3, () => { });

        var views = Layout.GetScrollViewsAt(frame, 0, 1);

        Assert.Equal(2, views.Count);
        Assert.Same(inner, views[0]);
        Assert.Same(outer, views[1]);
        // The first scroll node encountered owns the frame.
        Assert.Same(inner, frame.PrimaryScrollView);
    }
}

public class LayoutAnsiTests
{
    [Fact]
    public void Osc133ZonePrefixesAreStrippedWhenPainting()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("\x1b]133;A\x07hello"), 10, 1, () => { });

        Assert.Equal("hello", frame.Lines[0]);
    }

    [Fact]
    public void Osc133PrefixStrippingKeepsTheRestOfTheLine()
    {
        var frame = Layout.RenderLayoutFrame(new FixedLines("\x1b]133;A\x07\x1b]133;B\x07x\x1b[31mred\x1b[0m"), 20, 1, () => { });

        Assert.StartsWith("x", frame.Lines[0]);
        Assert.Contains("red", frame.Lines[0]);
    }
}
