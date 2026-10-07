namespace Pi.Tui.Components;

/// <summary>Horizontal stack (port of <c>components/h-stack.ts</c>).</summary>
public sealed class HStack : Stack
{
    public HStack(IEnumerable<StackChild>? children = null, StackOptions? options = null) : base(children, options)
    {
    }

    public HStack(IEnumerable<IComponent> children, StackOptions? options = null)
        : base(children.Select(c => StackChild.Of(c)), options)
    {
    }
    public override string[] Render(int width)
    {
        var safeWidth = Math.Max(1, width);
        var viewport = new LayoutViewport(safeWidth, int.MaxValue);
        var entries = VisibleStackEntries(Entries, viewport);
        if (entries.Count == 0)
        {
            return Array.Empty<string>();
        }

        var intrinsicWidths = entries
            .Select(entry => entry.Component.Render(safeWidth).Select(UnicodeWidth.VisibleWidth).DefaultIfEmpty(0).Max())
            .ToList();
        var widths = AllocateStackSizes(entries, intrinsicWidths, safeWidth, Gap);
        var rendered = new List<string[]>();
        for (var index = 0; index < entries.Count; index++)
        {
            rendered.Add(widths[index] == 0 ? Array.Empty<string>() : entries[index].Component.Render(widths[index]));
        }

        var height = rendered.Select(lines => lines.Length).DefaultIfEmpty(0).Max();
        var result = new string[height];
        for (var i = 0; i < height; i++)
        {
            result[i] = "";
        }

        var x = 0;
        for (var index = 0; index < rendered.Count; index++)
        {
            var lines = rendered[index];
            var childWidth = widths[index];
            var offset = Align switch
            {
                StackAlign.Center => (height - lines.Length) / 2,
                StackAlign.End => height - lines.Length,
                _ => 0,
            };
            for (var row = 0; row < lines.Length; row++)
            {
                var target = row + offset;
                if (target < 0 || target >= result.Length)
                {
                    continue;
                }
                result[target] = Tui.CompositeTuiLine(result[target], lines[row], x, childWidth, safeWidth);
            }
            x += childWidth + Gap;
        }
        return result;
    }
}
