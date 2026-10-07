namespace Pi.Tui.Components;

/// <summary>Text component that truncates to fit viewport width (port of <c>components/truncated-text.ts</c>).</summary>
public sealed class TruncatedText : IComponent
{
    private readonly string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;

    public TruncatedText(string text, int paddingX = 0, int paddingY = 0)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
    }

    public string[] Render(int width)
    {
        var result = new List<string>();
        var emptyLine = new string(' ', width);

        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        var availableWidth = Math.Max(1, width - _paddingX * 2);

        var singleLineText = _text;
        var newlineIndex = _text.IndexOf('\n');
        if (newlineIndex != -1)
        {
            singleLineText = _text.Substring(0, newlineIndex);
        }

        var displayText = TextLayout.TruncateToWidth(singleLineText, availableWidth);
        var leftPadding = new string(' ', _paddingX);
        var rightPadding = new string(' ', _paddingX);
        var lineWithPadding = leftPadding + displayText + rightPadding;

        var lineVisibleWidth = UnicodeWidth.VisibleWidth(lineWithPadding);
        var paddingNeeded = Math.Max(0, width - lineVisibleWidth);
        result.Add(lineWithPadding + new string(' ', paddingNeeded));

        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(emptyLine);
        }

        return result.ToArray();
    }
}
