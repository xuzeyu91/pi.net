namespace Pi.Tui.Components;

/// <summary>Spacer component that renders empty lines (port of <c>components/spacer.ts</c>).</summary>
public sealed class Spacer : IComponent
{
    private int _lines;

    public Spacer(int lines = 1) => _lines = lines;

    public void SetLines(int lines) => _lines = lines;

    public string[] Render(int width)
    {
        var result = new string[Math.Max(0, _lines)];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = "";
        }
        return result;
    }
}
