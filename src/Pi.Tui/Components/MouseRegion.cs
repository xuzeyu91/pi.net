namespace Pi.Tui.Components;

/// <summary>
/// Fallback handler for mouse events the wrapped child did not consume
/// (port of the TS <c>MouseRegionHandler</c> type).
/// </summary>
public delegate TuiMouseEventResult? MouseRegionHandler(TuiMouseEvent @event);

/// <summary>
/// Adds mouse handling to an existing component without changing its rendering
/// (port of <c>components/mouse-region.ts</c>).
/// </summary>
public sealed class MouseRegion : IComponent
{
    private readonly IComponent _child;
    private readonly MouseRegionHandler _onMouse;

    public MouseRegion(IComponent child, MouseRegionHandler onMouse)
    {
        _child = child;
        _onMouse = onMouse;
    }

    public string[] Render(int width) => _child.Render(width);

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        var childResult = MouseDispatch.Dispatch(_child, @event);
        return childResult ?? _onMouse(@event);
    }

    public void Invalidate() => _child.Invalidate();
}
