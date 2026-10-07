namespace Pi.Tui.Components;

/// <summary>Per-child sizing options for stacks.</summary>
public sealed class StackEntryOptions
{
    /// <summary>Fixed basis size, or null for "auto" (intrinsic).</summary>
    public int? Basis { get; set; }

    public int? Grow { get; set; }
    public int? Shrink { get; set; }
    public int? MinSize { get; set; }
    public int? MaxSize { get; set; }
    public Func<LayoutViewport, bool>? Visible { get; set; }
}

/// <summary>A component paired with its stack entry options.</summary>
public sealed class StackEntry
{
    public required IComponent Component { get; init; }
    public StackEntryOptions Options { get; init; } = new();
}

/// <summary>A stack child: either a bare component or a <see cref="StackEntry"/>.</summary>
public sealed class StackChild
{
    public IComponent? Component { get; init; }
    public StackEntry? Entry { get; init; }

    public static implicit operator StackChild(StackEntry entry) => new() { Entry = entry };

    public static StackChild Of(IComponent component, StackEntryOptions? options = null) =>
        options is null ? new StackChild { Component = component } : new StackChild { Entry = new StackEntry { Component = component, Options = options } };
}

/// <summary>Options for stack layout.</summary>
public sealed class StackOptions
{
    public int? Gap { get; set; }
    public StackAlign Align { get; set; } = StackAlign.Stretch;
}

/// <summary>Base class for vertical and horizontal stacks (port of <c>components/stack.ts</c>).</summary>
public abstract class Stack : Container, ILayoutComponent
{
    protected readonly List<StackLayoutEntry> Entries = new();
    protected readonly int Gap;
    protected readonly StackAlign Align;

    /// <summary>Whether this stack lays out along the vertical axis.</summary>
    protected abstract bool IsVertical { get; }

    protected Stack(IEnumerable<StackChild>? children = null, StackOptions? options = null)
    {
        options ??= new StackOptions();
        Gap = NormalizeSize(options.Gap, 0);
        Align = options.Align;
        if (children is not null)
        {
            foreach (var child in children)
            {
                if (child.Entry is { } entry)
                {
                    AddChild(entry.Component, entry.Options);
                }
                else if (child.Component is { } component)
                {
                    AddChild(component);
                }
            }
        }
    }

    public void AddChild(IComponent component, StackEntryOptions options)
    {
        base.AddChild(component);
        Entries.Add(new StackLayoutEntry
        {
            Component = component,
            Basis = options.Basis,
            Grow = options.Grow is { } grow ? NormalizeSize(grow, 0) : null,
            Shrink = options.Shrink is { } shrink ? NormalizeSize(shrink, 1) : null,
            MinSize = options.MinSize is { } minSize ? NormalizeSize(minSize, 0) : null,
            MaxSize = options.MaxSize is { } maxSize ? NormalizeSize(maxSize, int.MaxValue) : null,
            Visible = options.Visible,
        });
    }

    public override void AddChild(IComponent component)
    {
        base.AddChild(component);
        Entries.Add(new StackLayoutEntry { Component = component });
    }

    public override void RemoveChild(IComponent component)
    {
        base.RemoveChild(component);
        var index = Entries.FindIndex(entry => entry.Component == component);
        if (index != -1)
        {
            Entries.RemoveAt(index);
        }
    }

    public override void Clear()
    {
        base.Clear();
        Entries.Clear();
    }

    internal static int NormalizeSize(int? value, int fallback) =>
        value is null ? fallback : Math.Max(0, value.Value);

    /// <summary>Expose this stack to the layout engine.</summary>
    public ILayoutNode GetLayoutNode() => new StackLayoutNode
    {
        IsVertical = IsVertical,
        Entries = Entries,
        Gap = Gap,
        Align = Align,
    };

    /// <summary>Filter entries by their visibility predicate.</summary>
    public static List<StackLayoutEntry> VisibleStackEntries(IReadOnlyList<StackLayoutEntry> entries, LayoutViewport viewport) =>
        entries.Where(entry => entry.Visible?.Invoke(viewport) ?? true).ToList();

    private static int ClampSize(int size, StackLayoutEntry entry)
    {
        var min = Math.Max(0, entry.MinSize ?? 0);
        var max = Math.Max(min, entry.MaxSize ?? int.MaxValue);
        return Math.Max(min, Math.Min(max, Math.Max(0, size)));
    }

    private static void Distribute(int[] sizes, IReadOnlyList<StackLayoutEntry> entries, int amount, bool grow)
    {
        var remaining = amount;
        while (remaining > 0)
        {
            var candidates = new List<int>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (grow)
                {
                    if ((entry.Grow ?? 0) > 0 && sizes[index] < (entry.MaxSize ?? int.MaxValue))
                    {
                        candidates.Add(index);
                    }
                }
                else if ((entry.Shrink ?? 1) > 0 && sizes[index] > (entry.MinSize ?? 0))
                {
                    candidates.Add(index);
                }
            }
            if (candidates.Count == 0)
            {
                return;
            }

            var totalWeight = 0;
            foreach (var index in candidates)
            {
                var entry = entries[index];
                totalWeight += grow ? (entry.Grow ?? 0) : (entry.Shrink ?? 1) * Math.Max(1, sizes[index]);
            }

            var distributed = 0;
            foreach (var index in candidates)
            {
                if (remaining <= 0)
                {
                    break;
                }
                var entry = entries[index];
                var weight = grow ? (entry.Grow ?? 0) : (entry.Shrink ?? 1) * Math.Max(1, sizes[index]);
                var proposed = Math.Max(1, remaining * weight / totalWeight);
                var capacity = grow
                    ? (entry.MaxSize ?? int.MaxValue) - sizes[index]
                    : sizes[index] - (entry.MinSize ?? 0);
                var delta = Math.Min(Math.Min(remaining, proposed), capacity);
                if (delta <= 0)
                {
                    continue;
                }
                sizes[index] += grow ? delta : -delta;
                remaining -= delta;
                distributed += delta;
            }
            if (distributed == 0)
            {
                return;
            }
        }
    }

    /// <summary>Allocate sizes along the main axis (port of <c>allocateStackSizes</c>).</summary>
    public static int[] AllocateStackSizes(
        IReadOnlyList<StackLayoutEntry> entries,
        IReadOnlyList<int> intrinsicSizes,
        int? availableSize,
        int gap)
    {
        var sizes = new int[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var basis = entry.Basis ?? (intrinsicSizes.Count > index ? intrinsicSizes[index] : 0);
            sizes[index] = ClampSize(basis, entry);
        }
        if (availableSize is not { } available)
        {
            return sizes;
        }

        var contentSize = Math.Max(0, available - Math.Max(0, entries.Count - 1) * gap);
        var total = sizes.Sum();
        if (total < contentSize)
        {
            Distribute(sizes, entries, contentSize - total, grow: true);
        }
        else if (total > contentSize)
        {
            Distribute(sizes, entries, total - contentSize, grow: false);
        }
        return sizes;
    }
}
