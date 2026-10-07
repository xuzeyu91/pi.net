namespace Pi.Tui.Components;

/// <summary>Vertical stack (port of <c>components/v-stack.ts</c>).</summary>
public sealed class VStack : Stack
{
    public VStack(IEnumerable<StackChild>? children = null, StackOptions? options = null) : base(children, options)
    {
    }

    public VStack(IEnumerable<IComponent> children, StackOptions? options = null)
        : base(children.Select(c => StackChild.Of(c)), options)
    {
    }

    protected override bool IsVertical => true;

    public override string[] Render(int width)
    {
        var viewport = new LayoutViewport(Math.Max(1, width), int.MaxValue);
        var entries = VisibleStackEntries(Entries, viewport);
        var rendered = entries.Select(entry => entry.Component.Render(viewport.Width)).ToList();
        var sizes = AllocateStackSizes(entries, rendered.Select(lines => lines.Length).ToList(), null, Gap);

        var lines = new List<string>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                for (var gap = 0; gap < Gap; gap++)
                {
                    lines.Add("");
                }
            }
            var childLines = rendered[index].Take(sizes[index]).ToList();
            lines.AddRange(childLines);
            for (var padding = childLines.Count; padding < sizes[index]; padding++)
            {
                lines.Add("");
            }
        }
        return lines.ToArray();
    }
}
