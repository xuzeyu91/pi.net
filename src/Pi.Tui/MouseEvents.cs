namespace Pi.Tui;

/// <summary>Kind of a normalized mouse event (port of the TS <c>TuiMouseEventType</c> union).</summary>
public enum TuiMouseEventType
{
    Press,
    Release,
    Move,
    Drag,
    Click,
    Wheel,
}

/// <summary>Mouse button (port of the TS <c>TuiMouseButton</c> union).</summary>
public enum TuiMouseButton
{
    Left,
    Middle,
    Right,
    None,
}

/// <summary>Normalized cell-based mouse event. Coordinates are zero-based (port of <c>tui.ts</c>).</summary>
public sealed class TuiMouseEvent
{
    public TuiMouseEventType Type { get; set; }

    public TuiMouseButton Button { get; set; }

    /// <summary>Coordinates local to the receiving component.</summary>
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>Absolute terminal coordinates.</summary>
    public int ScreenX { get; set; }

    public int ScreenY { get; set; }

    /// <summary>Current component bounds.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    public bool Shift { get; set; }

    public bool Alt { get; set; }

    public bool Ctrl { get; set; }

    /// <summary>Logical lines. Negative values scroll up.</summary>
    public int? WheelDelta { get; set; }

    /// <summary>Consecutive click count when <see cref="Type"/> is <see cref="TuiMouseEventType.Click"/>.</summary>
    public int? ClickCount { get; set; }

    public TuiMouseEvent Clone() => (TuiMouseEvent)MemberwiseClone();
}

/// <summary>What a component wants the renderer to do after a mouse event.</summary>
public class TuiMouseEventResult
{
    /// <summary>Stop propagation and suppress renderer-level fallback behavior.</summary>
    public bool? Handled { get; set; }

    /// <summary>Route subsequent drag/release events to this component. Implies handled.</summary>
    public bool? Capture { get; set; }

    /// <summary>Give keyboard focus to this component. Implies handled.</summary>
    public bool? Focus { get; set; }

    /// <summary>
    /// Explicitly request or suppress a render. Move and release default to false; press, click, drag
    /// and wheel default to true.
    /// </summary>
    public bool? Render { get; set; }
}

/// <summary>Internal target metadata used by containers and alternate-screen dispatch.</summary>
public sealed class TuiMouseDispatchTarget
{
    public required IComponent Component { get; init; }

    public required int OriginX { get; init; }

    public required int OriginY { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

/// <summary>Result of dispatching to a concrete component.</summary>
public sealed class TuiMouseDispatchResult : TuiMouseEventResult
{
    public required TuiMouseDispatchTarget Target { get; init; }

    /// <summary>Keyboard focus target, which may be a delegating parent container.</summary>
    public IComponent? FocusTarget { get; set; }
}

/// <summary>Helpers shared by the mouse dispatch path (port of <c>tui.ts</c>).</summary>
public static class MouseDispatch
{
    /// <summary>
    /// Dispatch an event to a component and retain the exact target and coordinate transform.
    /// Containers use this when forwarding events to nested children.
    /// </summary>
    public static TuiMouseDispatchResult? Dispatch(IComponent component, TuiMouseEvent @event)
    {
        var result = component.HandleMouse(@event);
        if (result is null)
        {
            return null;
        }

        if (result is TuiMouseDispatchResult forwarded)
        {
            // The component forwarded the event to a child it hosts. Like a delegating container, it
            // routes keys to that child itself, so it keeps keyboard focus. Focusing the child directly
            // would leave focus on a detached component once the host removes it, e.g. a closed
            // settings submenu.
            if (forwarded.Focus == true && TuiComponents.HasHandleInput(component))
            {
                forwarded.FocusTarget = component;
            }

            return forwarded;
        }

        if (result.Handled != true && result.Capture != true && result.Focus != true)
        {
            return null;
        }

        return new TuiMouseDispatchResult
        {
            Handled = true,
            Capture = result.Capture,
            Render = result.Render,
            Focus = result.Focus,
            FocusTarget = result.Focus == true ? component : null,
            Target = new TuiMouseDispatchTarget
            {
                Component = component,
                OriginX = @event.ScreenX - @event.X,
                OriginY = @event.ScreenY - @event.Y,
                Width = @event.Width,
                Height = @event.Height,
            },
        };
    }

    /// <summary>Recreate local coordinates for a previously dispatched mouse target.</summary>
    public static TuiMouseEvent Retarget(TuiMouseEvent @event, TuiMouseDispatchTarget target)
    {
        var clone = @event.Clone();
        clone.X = @event.ScreenX - target.OriginX;
        clone.Y = @event.ScreenY - target.OriginY;
        clone.Width = target.Width;
        clone.Height = target.Height;
        return clone;
    }
}

/// <summary>Helpers for inspecting components at runtime.</summary>
internal static class TuiComponents
{
    /// <summary>
    /// TS checks <c>component.handleInput</c> for truthiness to decide whether a delegating container
    /// keeps keyboard focus. In C# the method always exists (it has an interface default), so instead
    /// check whether the component overrides that default.
    /// </summary>
    public static bool HasHandleInput(IComponent component)
    {
        var method = component.GetType().GetMethod(nameof(IComponent.HandleInput), Type.EmptyTypes);
        return method is not null && method.DeclaringType != typeof(IComponent);
    }
}
