namespace Pi.Tui.Components;

/// <summary>Box component - a container that applies padding and background to all children.</summary>
public sealed class Box : IComponent
{
    private sealed record RenderCache(string[] ChildLines, int Width, string? BgSample, string[] Lines);

    public List<IComponent> Children { get; } = new();

    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _bgFn;
    private RenderCache? _cache;

    public Box(int paddingX = 1, int paddingY = 1, Func<string, string>? bgFn = null)
    {
        _paddingX = paddingX;
        _paddingY = paddingY;
        _bgFn = bgFn;
    }

    public void AddChild(IComponent component)
    {
        Children.Add(component);
        InvalidateCache();
    }

    public void RemoveChild(IComponent component)
    {
        if (Children.Remove(component))
        {
            InvalidateCache();
        }
    }

    public void Clear()
    {
        Children.Clear();
        InvalidateCache();
    }

    public void SetBgFn(Func<string, string>? bgFn) => _bgFn = bgFn;

    private void InvalidateCache() => _cache = null;

    private bool MatchCache(int width, string[] childLines, string? bgSample)
    {
        var cache = _cache;
        return cache is not null
            && cache.Width == width
            && cache.BgSample == bgSample
            && cache.ChildLines.Length == childLines.Length
            && cache.ChildLines.SequenceEqual(childLines);
    }

    public void Invalidate()
    {
        InvalidateCache();
        foreach (var child in Children)
        {
            child.Invalidate();
        }
    }

    public string[] Render(int width)
    {
        if (Children.Count == 0)
        {
            return Array.Empty<string>();
        }

        var contentWidth = Math.Max(1, width - _paddingX * 2);
        var leftPad = new string(' ', _paddingX);

        var childLines = new List<string>();
        foreach (var child in Children)
        {
            foreach (var line in child.Render(contentWidth))
            {
                childLines.Add(leftPad + line);
            }
        }

        if (childLines.Count == 0)
        {
            return Array.Empty<string>();
        }

        var bgSample = _bgFn?.Invoke("test");

        if (MatchCache(width, childLines.ToArray(), bgSample))
        {
            return _cache!.Lines;
        }

        var result = new List<string>();
        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(ApplyBg("", width));
        }
        foreach (var line in childLines)
        {
            result.Add(ApplyBg(line, width));
        }
        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(ApplyBg("", width));
        }

        _cache = new RenderCache(childLines.ToArray(), width, bgSample, result.ToArray());
        return _cache.Lines;
    }

    private string ApplyBg(string line, int width)
    {
        var visLen = UnicodeWidth.VisibleWidth(line);
        var padNeeded = Math.Max(0, width - visLen);
        var padded = line + new string(' ', padNeeded);
        return _bgFn is { } bg ? TextLayout.ApplyBackgroundToLine(padded, width, bg) : padded;
    }
}
