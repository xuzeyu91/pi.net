namespace Pi.Tui;

/// <summary>Viewport passed to layout visibility predicates.</summary>
public readonly record struct LayoutViewport(int Width, int Height);

/// <summary>An entry in a stack layout node.</summary>
public sealed class StackLayoutEntry
{
    public required IComponent Component { get; init; }

    /// <summary>Fixed basis size, or null for "auto" (intrinsic).</summary>
    public int? Basis { get; init; }

    public int? Grow { get; init; }
    public int? Shrink { get; init; }
    public int? MinSize { get; init; }
    public int? MaxSize { get; init; }
    public Func<LayoutViewport, bool>? Visible { get; init; }
}

/// <summary>A vertical or horizontal stack layout node.</summary>
public sealed class StackLayoutNode : ILayoutNode
{
    public required bool IsVertical { get; init; }
    public required IReadOnlyList<StackLayoutEntry> Entries { get; init; }
    public int Gap { get; init; }
    public StackAlign Align { get; init; } = StackAlign.Stretch;
}

/// <summary>Cross-axis alignment for stacks.</summary>
public enum StackAlign
{
    Stretch,
    Start,
    Center,
    End,
}

/// <summary>How a scroll view behaves when it cannot consume a scroll request.</summary>
public enum ScrollOverscroll
{
    Chain,
    Contain,
}

/// <summary>
/// Mutable layout state exposed by a scroll node. Kept separate from <see cref="Components.ScrollView"/>
/// so the layout engine does not depend on the concrete component type.
/// </summary>
public interface IScrollLayoutState
{
    int ScrollTop { get; }

    bool Primary { get; }

    ScrollOverscroll Overscroll { get; }

    int ViewportHeight { get; }

    int GetContentWidth(int width);

    void UpdateLayout(int contentHeight, int viewportHeight, Action requestRender);
}

/// <summary>A scroll layout node: wraps exactly one child component.</summary>
public sealed class ScrollLayoutNode : ILayoutNode
{
    public required IComponent Component { get; init; }
    public required IScrollLayoutState State { get; init; }
}

/// <summary>Marker for the two layout node shapes (stack or scroll).</summary>
public interface ILayoutNode
{
}

/// <summary>Port of <c>layout-node.ts</c>: components that participate in layout expose a node.</summary>
public interface ILayoutComponent : IComponent
{
    /// <summary>Return the layout node describing this component.</summary>
    ILayoutNode GetLayoutNode();
}

/// <summary>Helpers for the layout-node protocol.</summary>
public static class LayoutNodes
{
    /// <summary>Return the layout node of a component, or null when it is a leaf.</summary>
    public static ILayoutNode? GetLayoutNode(IComponent component) =>
        component is ILayoutComponent layout ? layout.GetLayoutNode() : null;
}
