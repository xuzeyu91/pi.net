using System.Text.RegularExpressions;
using Pi.Tui.Components;

namespace Pi.Tui;

/// <summary>A rectangle in terminal cell coordinates (port of <c>LayoutRect</c>). Mutable: the layout engine translates boxes in place.</summary>
public sealed class LayoutRect
{
    public LayoutRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>A laid-out component and its geometry (port of <c>LayoutBox</c>).</summary>
public sealed class LayoutBox
{
    public required IComponent Component { get; init; }

    public required LayoutRect Rect { get; set; }

    public required LayoutRect Clip { get; set; }

    public required List<LayoutBox> Children { get; init; }

    public LayoutBox? Parent { get; set; }

    /// <summary>Rendered lines for leaf boxes.</summary>
    public string[]? Lines { get; init; }

    /// <summary>Row offset applied when the leaf is scrolled to keep the cursor line visible.</summary>
    public int LineOffset { get; init; }

    public ScrollView? ScrollView { get; init; }

    public string[]? ScrollContentLines { get; init; }

    public int Layer { get; init; }
}

/// <summary>A fully laid-out frame (port of <c>LayoutFrame</c>).</summary>
public sealed class LayoutFrame
{
    public required LayoutBox Root { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required string[] Lines { get; init; }

    public ScrollView? PrimaryScrollView { get; init; }
}

/// <summary>Scrollbar placement for a scroll box (port of <c>ScrollbarGeometry</c>).</summary>
public readonly record struct ScrollbarGeometry(
    int Column,
    int TrackTop,
    int TrackHeight,
    int ThumbTop,
    int ThumbHeight,
    int MaxScrollTop);

/// <summary>Per-frame layout state threaded through the layout walk.</summary>
internal sealed class LayoutContext
{
    public required LayoutViewport Viewport { get; init; }

    public Dictionary<IComponent, Dictionary<int, string[]>> RenderCache { get; } =
        new(ReferenceEqualityComparer.Instance);

    public required Action RequestRender { get; init; }

    public ScrollView? PrimaryScrollView { get; set; }
}

/// <summary>Port of <c>layout.ts</c>: the flex/stack layout solver and box painter.</summary>
public static partial class Layout
{
    [GeneratedRegex(@"^(?:\x1b\]133;[ABC](?:\x07|\x1b\\))+")]
    private static partial Regex Osc133ZonePrefix();

    /// <summary>Intersection of two rectangles; never negative in either dimension.</summary>
    public static LayoutRect Intersect(LayoutRect a, LayoutRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new LayoutRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static string[] RenderCached(LayoutContext context, IComponent component, int width)
    {
        var safeWidth = Math.Max(1, (int)Math.Floor((double)width));
        if (!context.RenderCache.TryGetValue(component, out var widths))
        {
            widths = [];
            context.RenderCache[component] = widths;
        }
        if (!widths.TryGetValue(safeWidth, out var lines))
        {
            lines = component.Render(safeWidth);
            widths[safeWidth] = lines;
        }
        return lines;
    }

    private static int MeasureHeight(LayoutContext context, IComponent component, int width) =>
        RenderCached(context, component, width).Length;

    private static int MeasureWidth(LayoutContext context, IComponent component, int width)
    {
        var max = 0;
        foreach (var line in RenderCached(context, component, width))
        {
            max = Math.Max(max, UnicodeWidth.VisibleWidth(line));
        }
        return max;
    }

    private static LayoutBox WithParent(LayoutBox box, LayoutBox parent)
    {
        box.Parent = parent;
        return box;
    }

    private static void TranslateBox(LayoutBox box, int deltaY)
    {
        box.Rect.Y += deltaY;
        foreach (var child in box.Children)
        {
            TranslateBox(child, deltaY);
        }
    }

    private static void UpdateClips(LayoutBox box, LayoutRect parentClip)
    {
        box.Clip = Intersect(parentClip, box.Rect);
        foreach (var child in box.Children)
        {
            UpdateClips(child, box.Clip);
        }
    }

    private static LayoutBox LayoutComponent(
        LayoutContext context,
        IComponent component,
        int x,
        int y,
        int width,
        int? height,
        LayoutRect clip)
    {
        var safeWidth = Math.Max(1, (int)Math.Floor((double)width));
        var node = LayoutNodes.GetLayoutNode(component);
        if (node is null)
        {
            var lines = RenderCached(context, component, safeWidth);
            var allocatedHeight = height is { } fixedHeight ? Math.Max(0, fixedHeight) : lines.Length;
            var lineOffset = 0;
            if (lines.Length > allocatedHeight && allocatedHeight > 0)
            {
                var cursorLine = Array.FindIndex(lines, line => line.Contains(Tui.CursorMarker, StringComparison.Ordinal));
                if (cursorLine >= allocatedHeight)
                {
                    lineOffset = cursorLine - allocatedHeight + 1;
                }
            }
            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            return new LayoutBox
            {
                Component = component,
                Rect = rect,
                Clip = Intersect(clip, rect),
                Children = [],
                Lines = lines,
                LineOffset = lineOffset,
                Layer = 0,
            };
        }

        if (node is ScrollLayoutNode scroll)
        {
            var previousScrollTop = scroll.State.ScrollTop;
            var contentWidth = scroll.State.GetContentWidth(safeWidth);
            var childBox = LayoutComponent(context, scroll.Component, x, y - previousScrollTop, contentWidth, null, clip);
            var contentHeight = childBox.Rect.Height;
            var viewportHeight = height is { } fixedHeight ? Math.Max(0, fixedHeight) : contentHeight;
            scroll.State.UpdateLayout(contentHeight, viewportHeight, context.RequestRender);
            TranslateBox(childBox, previousScrollTop - scroll.State.ScrollTop);
            var scrollView = scroll.State as ScrollView;
            if (scroll.State.Primary || context.PrimaryScrollView is null)
            {
                context.PrimaryScrollView = scrollView;
            }
            var rect = new LayoutRect(x, y, safeWidth, viewportHeight);
            var childClip = Intersect(clip, rect);
            var box = new LayoutBox
            {
                Component = component,
                Rect = rect,
                Clip = childClip,
                Children = [childBox],
                ScrollView = scrollView,
                ScrollContentLines = RenderCached(context, scroll.Component, contentWidth),
                Layer = 0,
            };
            childBox.Parent = box;
            UpdateClips(childBox, childClip);
            return box;
        }

        var stack = (StackLayoutNode)node;
        var entries = Stack.VisibleStackEntries(stack.Entries, context.Viewport);
        var gapTotal = Math.Max(0, entries.Count - 1) * stack.Gap;

        if (stack.IsVertical)
        {
            var intrinsicHeights = entries
                .Select(entry => entry.Basis ?? MeasureHeight(context, entry.Component, safeWidth))
                .ToList();
            var sizes = Stack.AllocateStackSizes(entries, intrinsicHeights, height, stack.Gap);
            var naturalHeight = sizes.Sum() + gapTotal;
            var allocatedHeight = height is { } fixedHeight ? Math.Max(0, fixedHeight) : naturalHeight;
            var rect = new LayoutRect(x, y, safeWidth, allocatedHeight);
            var box = new LayoutBox
            {
                Component = component,
                Rect = rect,
                Clip = Intersect(clip, rect),
                Children = [],
                Layer = 0,
            };
            var childY = y;
            for (var index = 0; index < entries.Count; index++)
            {
                box.Children.Add(WithParent(
                    LayoutComponent(context, entries[index].Component, x, childY, safeWidth, sizes[index], box.Clip),
                    box));
                childY += sizes[index] + stack.Gap;
            }
            return box;
        }

        var intrinsicWidths = entries
            .Select(entry => entry.Basis ?? MeasureWidth(context, entry.Component, safeWidth))
            .ToList();
        var widths = Stack.AllocateStackSizes(entries, intrinsicWidths, safeWidth, stack.Gap);
        var childIntrinsicHeights = new int[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            childIntrinsicHeights[index] = MeasureHeight(context, entries[index].Component, Math.Max(1, widths[index]));
        }
        var allocatedCrossHeight = height is { } crossHeight
            ? Math.Max(0, crossHeight)
            : childIntrinsicHeights.DefaultIfEmpty(0).Max();
        var hRect = new LayoutRect(x, y, safeWidth, allocatedCrossHeight);
        var hBox = new LayoutBox
        {
            Component = component,
            Rect = hRect,
            Clip = Intersect(clip, hRect),
            Children = [],
            Layer = 0,
        };
        var childX = x;
        for (var index = 0; index < entries.Count; index++)
        {
            var naturalChildHeight = childIntrinsicHeights[index];
            var childHeight = stack.Align == StackAlign.Stretch
                ? allocatedCrossHeight
                : Math.Min(allocatedCrossHeight, naturalChildHeight);
            var childY = y;
            if (stack.Align == StackAlign.Center)
            {
                childY += (allocatedCrossHeight - childHeight) / 2;
            }
            else if (stack.Align == StackAlign.End)
            {
                childY += allocatedCrossHeight - childHeight;
            }
            var childWidth = widths[index];
            if (childWidth == 0)
            {
                hBox.Children.Add(new LayoutBox
                {
                    Component = entries[index].Component,
                    Rect = new LayoutRect(childX, childY, 0, childHeight),
                    Clip = new LayoutRect(childX, childY, 0, 0),
                    Children = [],
                    Parent = hBox,
                    Layer = 0,
                });
            }
            else
            {
                hBox.Children.Add(WithParent(
                    LayoutComponent(context, entries[index].Component, childX, childY, childWidth, childHeight, hBox.Clip),
                    hBox));
            }
            childX += childWidth + stack.Gap;
        }
        return hBox;
    }

    /// <summary>Overwrite the single cell at <paramref name="column"/> with <paramref name="replacement"/>, preserving styling.</summary>
    internal static string ReplaceScrollbarCell(
        string line,
        int column,
        int totalWidth,
        string replacement,
        bool preserveTargetBackground)
    {
        if (TerminalImage.IsImageLine(line))
        {
            return line;
        }

        var graphemeRange = UnicodeWidth.GetGraphemeCellRange(line, column);
        var start = graphemeRange?.Start ?? column;
        var end = graphemeRange?.End ?? column + 1;
        var before = TextLayout.SliceByColumn(line, 0, start, true);
        var target = TextLayout.SliceByColumn(line, start, end - start, true);
        var after = TextLayout.SliceByColumn(line, end, Math.Max(0, totalWidth - end), true);

        var targetPrefix = "";
        var targetIndex = 0;
        while (targetIndex < target.Length)
        {
            var ansi = Ansi.ExtractAnsiCode(target, targetIndex);
            if (ansi is not { } code)
            {
                break;
            }
            targetPrefix += code.Code;
            targetIndex += code.Length;
        }

        var beforePadding = new string(' ', Math.Max(0, start - UnicodeWidth.VisibleWidth(before)));
        var cellPaddingBefore = new string(' ', Math.Max(0, column - start));
        var cellPaddingAfter = new string(' ', Math.Max(0, end - column - 1));
        var background = preserveTargetBackground ? Ansi.GetActiveBackgroundAnsi(targetPrefix) : "";
        var targetStyle = $"\x1b[0m\x1b]8;;\x07{background}";
        return $"{before}{beforePadding}{targetStyle}{cellPaddingBefore}{replacement}{cellPaddingAfter}{after}";
    }

    /// <summary>Scrollbar placement for a scroll box, or null when no scrollbar is drawn.</summary>
    public static ScrollbarGeometry? GetScrollbarGeometry(LayoutBox box, bool includeHiddenAuto = false)
    {
        if (box.ScrollView is not { } scrollView || box.Rect.Width <= 0 || box.Rect.Height <= 0)
        {
            return null;
        }

        var contentHeight = box.Children.Count > 0
            ? box.Children[0].Rect.Height
            : box.ScrollContentLines?.Length ?? 0;
        var trackHeight = box.Rect.Height;
        var canRevealHiddenAuto = includeHiddenAuto
            && scrollView.Scrollbar == ScrollViewScrollbar.Auto
            && contentHeight > trackHeight;
        if (!scrollView.IsScrollbarVisible && !canRevealHiddenAuto)
        {
            return null;
        }

        var minThumbHeight = Math.Min(2, trackHeight);
        // JS divides by contentHeight unconditionally: 0 yields Infinity, which Math.min then clamps
        // to the track height. Mirror that instead of throwing on integer division by zero.
        var ratio = contentHeight == 0
            ? double.PositiveInfinity
            : (double)trackHeight * trackHeight / contentHeight;
        var thumbHeight = (int)Math.Max(minThumbHeight, Math.Min((double)trackHeight, JsMath.Round(ratio)));
        var maxScrollTop = Math.Max(0, contentHeight - trackHeight);
        var maxThumbTop = trackHeight - thumbHeight;
        var thumbOffset = maxScrollTop == 0
            ? 0
            : (int)JsMath.Round((double)scrollView.ScrollTop / maxScrollTop * maxThumbTop);
        var column = box.Rect.X + box.Rect.Width - 1;
        if (column < box.Clip.X || column >= box.Clip.X + box.Clip.Width)
        {
            return null;
        }

        return new ScrollbarGeometry(
            column,
            box.Rect.Y,
            trackHeight,
            box.Rect.Y + thumbOffset,
            thumbHeight,
            maxScrollTop);
    }

    private static void PaintScrollbar(LayoutBox box, string[] screen, int totalWidth)
    {
        var geometry = GetScrollbarGeometry(box);
        if (geometry is not { } geo || box.ScrollView is not { } scrollView)
        {
            return;
        }

        for (var offset = 0; offset < geo.TrackHeight; offset++)
        {
            var row = geo.TrackTop + offset;
            if (row < box.Clip.Y || row >= box.Clip.Y + box.Clip.Height || row < 0 || row >= screen.Length)
            {
                continue;
            }
            var isThumb = row >= geo.ThumbTop && row < geo.ThumbTop + geo.ThumbHeight;
            var replacement = isThumb
                ? scrollView.ScrollbarThumbStyle(scrollView.IsScrollbarActive ? "█" : "┃")
                : scrollView.ScrollbarTrackStyle("│");
            screen[row] = ReplaceScrollbarCell(
                screen[row],
                geo.Column,
                totalWidth,
                replacement,
                scrollView.Scrollbar != ScrollViewScrollbar.Always);
        }
    }

    private static void PaintBox(LayoutBox box, string[] screen, int totalWidth)
    {
        if (box.Lines is { } lines)
        {
            var offset = box.LineOffset;
            var firstRow = Math.Max(Math.Max(box.Rect.Y, box.Clip.Y), 0);
            var lastRow = Math.Min(Math.Min(box.Rect.Y + box.Rect.Height, box.Clip.Y + box.Clip.Height), screen.Length);
            for (var row = firstRow; row < lastRow; row++)
            {
                var sourceIndex = offset + row - box.Rect.Y;
                if (sourceIndex < 0 || sourceIndex >= lines.Length)
                {
                    continue;
                }
                var line = Osc133ZonePrefix().Replace(lines[sourceIndex], "");
                var imageMetadata = TerminalImage.GetKittyImageMetadata(line);
                if (imageMetadata is { } metadata)
                {
                    var clipBottom = Math.Min(screen.Length, box.Clip.Y + box.Clip.Height);
                    var visibleRows = Math.Min(metadata.Rows, clipBottom - row);
                    if (visibleRows < metadata.Rows)
                    {
                        line = TerminalImage.CropKittyImageLine(line, 0, visibleRows);
                    }
                }
                // Fast path: a full-width box painting onto an untouched row can use the source line
                // reference directly. Compositing would rebuild the row through ANSI/grapheme
                // segmentation every frame; padding is unnecessary because rows are written with
                // erase-line and the final width clamp still truncates over-wide lines.
                if (box.Rect.X == 0 && box.Rect.Width >= totalWidth && (TerminalImage.IsImageLine(line) || string.IsNullOrEmpty(screen[row])))
                {
                    screen[row] = line;
                }
                else
                {
                    screen[row] = Tui.CompositeTuiLine(screen[row], line, box.Rect.X, box.Rect.Width, totalWidth);
                }
            }
        }

        foreach (var child in box.Children)
        {
            PaintBox(child, screen, totalWidth);
        }

        if (box.ScrollView is { } scrollView
            && box.ScrollContentLines is { } scrollContentLines
            && scrollView.ScrollTop > 0
            && box.Rect.Height > 0)
        {
            for (var imageRow = scrollView.ScrollTop - 1; imageRow >= 0; imageRow--)
            {
                var imageLine = imageRow < scrollContentLines.Length ? scrollContentLines[imageRow] : "";
                var metadata = TerminalImage.GetKittyImageMetadata(imageLine);
                if (metadata is { } meta)
                {
                    var hiddenRows = scrollView.ScrollTop - imageRow;
                    if (hiddenRows < meta.Rows)
                    {
                        var visibleRows = Math.Min(box.Rect.Height, meta.Rows - hiddenRows);
                        var cropped = TerminalImage.CropKittyImageLine(imageLine, hiddenRows, visibleRows);
                        if (box.Rect.X == 0 && box.Rect.Width >= totalWidth)
                        {
                            screen[box.Rect.Y] = cropped;
                        }
                    }
                    break;
                }
                if (imageLine.Length != 0)
                {
                    break;
                }
            }
        }

        PaintScrollbar(box, screen, totalWidth);
    }

    /// <summary>Lay out <paramref name="root"/> and paint it into a fresh screen buffer.</summary>
    public static LayoutFrame RenderLayoutFrame(IComponent root, int width, int height, Action requestRender)
    {
        var safeWidth = Math.Max(1, (int)Math.Floor((double)width));
        var safeHeight = Math.Max(1, (int)Math.Floor((double)height));
        var context = new LayoutContext
        {
            Viewport = new LayoutViewport(safeWidth, safeHeight),
            RequestRender = requestRender,
        };
        var rootBox = LayoutComponent(context, root, 0, 0, safeWidth, safeHeight, new LayoutRect(0, 0, safeWidth, safeHeight));
        var lines = new string[safeHeight];
        Array.Fill(lines, "");
        PaintBox(rootBox, lines, safeWidth);
        return new LayoutFrame
        {
            Root = rootBox,
            Width = safeWidth,
            Height = safeHeight,
            Lines = lines,
            PrimaryScrollView = context.PrimaryScrollView,
        };
    }

    private static bool ContainsPoint(LayoutRect rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>Return the visual hit path from the deepest component to the layout root.</summary>
    public static List<LayoutBox> GetLayoutBoxesAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(LayoutBox Box, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!ContainsPoint(box.Clip, x, y))
            {
                return;
            }
            result.Add((box, depth));
            foreach (var child in box.Children)
            {
                Visit(child, depth + 1);
            }
        }

        Visit(frame.Root, 0);
        result.Sort((a, b) =>
        {
            var byLayer = b.Box.Layer.CompareTo(a.Box.Layer);
            return byLayer != 0 ? byLayer : b.Depth.CompareTo(a.Depth);
        });
        return result.Select(entry => entry.Box).ToList();
    }

    /// <summary>Find the layout box that owns <paramref name="scrollView"/>.</summary>
    public static LayoutBox? GetScrollViewBox(LayoutFrame frame, ScrollView scrollView)
    {
        LayoutBox? Visit(LayoutBox box)
        {
            if (ReferenceEquals(box.ScrollView, scrollView))
            {
                return box;
            }
            foreach (var child in box.Children)
            {
                var match = Visit(child);
                if (match is not null)
                {
                    return match;
                }
            }
            return null;
        }

        return Visit(frame.Root);
    }

    /// <summary>Scroll views under the given point, innermost first.</summary>
    public static List<ScrollView> GetScrollViewsAt(LayoutFrame frame, int x, int y)
    {
        var result = new List<(ScrollView ScrollView, int Depth)>();
        void Visit(LayoutBox box, int depth)
        {
            if (!ContainsPoint(box.Clip, x, y))
            {
                return;
            }
            if (box.ScrollView is { } scrollView && ContainsPoint(box.Rect, x, y))
            {
                result.Add((scrollView, depth));
            }
            foreach (var child in box.Children)
            {
                Visit(child, depth + 1);
            }
        }

        Visit(frame.Root, 0);
        result.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        return result.Select(entry => entry.ScrollView).ToList();
    }
}
