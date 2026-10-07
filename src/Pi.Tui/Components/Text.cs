namespace Pi.Tui.Components;

/// <summary>Text component - displays multi-line text with word wrapping (port of <c>components/text.ts</c>).</summary>
public sealed class Text : IComponent
{
    private string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _customBgFn;

    private string? _cachedText;
    private int? _cachedWidth;
    private string[]? _cachedLines;

    public Text(string text = "", int paddingX = 1, int paddingY = 1, Func<string, string>? customBgFn = null)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
        _customBgFn = customBgFn;
    }

    public void SetText(string text)
    {
        _text = text;
        Invalidate();
    }

    public void SetCustomBgFn(Func<string, string>? customBgFn)
    {
        _customBgFn = customBgFn;
        Invalidate();
    }

    public void Invalidate()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public string[] Render(int width)
    {
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == width)
        {
            return _cachedLines;
        }

        if (_text.Length == 0 || _text.Trim().Length == 0)
        {
            var empty = Array.Empty<string>();
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = empty;
            return empty;
        }

        var normalizedText = _text.Replace("\t", "   ");
        var contentWidth = Math.Max(1, width - _paddingX * 2);
        var wrappedLines = TextLayout.WrapTextWithAnsi(normalizedText, contentWidth);

        var leftMargin = new string(' ', _paddingX);
        var rightMargin = new string(' ', _paddingX);
        var contentLines = new List<string>();

        foreach (var line in wrappedLines)
        {
            var lineWithMargins = leftMargin + line + rightMargin;
            if (_customBgFn is { } bg)
            {
                contentLines.Add(TextLayout.ApplyBackgroundToLine(lineWithMargins, width, bg));
            }
            else
            {
                var visibleLen = UnicodeWidth.VisibleWidth(lineWithMargins);
                var paddingNeeded = Math.Max(0, width - visibleLen);
                contentLines.Add(lineWithMargins + new string(' ', paddingNeeded));
            }
        }

        var emptyLine = new string(' ', width);
        var emptyLines = new List<string>();
        for (var i = 0; i < _paddingY; i++)
        {
            emptyLines.Add(_customBgFn is { } bg ? TextLayout.ApplyBackgroundToLine(emptyLine, width, bg) : emptyLine);
        }

        var result = new List<string>();
        result.AddRange(emptyLines);
        result.AddRange(contentLines);
        result.AddRange(emptyLines);

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result.ToArray();

        return result.Count > 0 ? _cachedLines : new[] { "" };
    }
}
