using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public class TextEngineTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("你好", 4)]
    [InlineData("a你b", 4)]
    [InlineData("héllo", 5)]
    public void VisibleWidth_MeasuresBasicStrings(string text, int expected)
    {
        Assert.Equal(expected, UnicodeWidth.VisibleWidth(text));
    }

    [Fact]
    public void VisibleWidth_StripsAnsiAndExpandsTabs()
    {
        Assert.Equal(3, UnicodeWidth.VisibleWidth("\x1b[31mabc\x1b[0m"));
        Assert.Equal(5, UnicodeWidth.VisibleWidth("a\tb"));
        // OSC 8 hyperlinks are stripped; only the link text ("link") is measured.
        Assert.Equal(4, UnicodeWidth.VisibleWidth("\x1b]8;;https://x\x07link\x1b]8;;\x07"));
    }

    [Fact]
    public void VisibleWidth_CountsEmojiAsTwo()
    {
        Assert.Equal(2, UnicodeWidth.VisibleWidth("\U0001F600"));
        Assert.Equal(4, UnicodeWidth.VisibleWidth("\U0001F600\U0001F600"));
    }

    [Fact]
    public void ExtractAnsiCode_RecognisesCsiOscAndApc()
    {
        var csi = Ansi.ExtractAnsiCode("\x1b[31mX", 0);
        Assert.NotNull(csi);
        Assert.Equal("\x1b[31m", csi!.Value.Code);
        Assert.Equal(5, csi.Value.Length);

        var osc = Ansi.ExtractAnsiCode("\x1b]8;;http://x\x07Y", 0);
        Assert.NotNull(osc);
        Assert.Equal("\x1b]8;;http://x\x07", osc!.Value.Code);

        var apc = Ansi.ExtractAnsiCode("\x1b_pi:c\x07Z", 0);
        Assert.NotNull(apc);
        Assert.Equal("\x1b_pi:c\x07", apc!.Value.Code);
    }

    [Fact]
    public void StripTerminalSequences_RemovesEscapes()
    {
        Assert.Equal("hello", Ansi.StripTerminalSequences("\x1b[1mhel\x1b[0mlo"));
    }

    [Fact]
    public void NormalizeTerminalOutput_ExpandsTabsAndThaiAm()
    {
        Assert.Equal("a   b", Ansi.NormalizeTerminalOutput("a\tb"));
        Assert.Equal("\u0e4d\u0e32", Ansi.NormalizeTerminalOutput("\u0e33"));
    }

    [Fact]
    public void TruncateToWidth_AppendsEllipsisAndPads()
    {
        Assert.Equal("hello...", Ansi.StripTerminalSequences(TextLayout.TruncateToWidth("hello world", 8)));
        Assert.Equal("hello", TextLayout.TruncateToWidth("hello", 10));
        Assert.Equal("hello     ", TextLayout.TruncateToWidth("hello", 10, pad: true));
        Assert.Equal("", TextLayout.TruncateToWidth("hello", 0));
    }

    [Fact]
    public void TruncateToWidth_HandlesWideCharacters()
    {
        var result = Ansi.StripTerminalSequences(TextLayout.TruncateToWidth("你好世界", 4));
        Assert.Equal("...", result);
        Assert.Equal(3, UnicodeWidth.VisibleWidth(result));
    }

    [Fact]
    public void TruncateToWidth_PreservesAnsiWhenFits()
    {
        var text = "\x1b[31mred\x1b[0m";
        Assert.Equal(text, TextLayout.TruncateToWidth(text, 10));
    }

    [Fact]
    public void SliceByColumn_ExtractsVisibleRange()
    {
        Assert.Equal("world", TextLayout.SliceByColumn("hello world", 6, 5));
        Assert.Equal("ell", TextLayout.SliceByColumn("hello", 1, 3));
        Assert.Equal("", TextLayout.SliceByColumn("hello", 0, 0));
    }

    [Fact]
    public void SliceWithWidth_ReportsActualWidth()
    {
        var (text, width) = TextLayout.SliceWithWidth("hello", 0, 3);
        Assert.Equal("hel", text);
        Assert.Equal(3, width);
    }

    [Fact]
    public void WrapTextWithAnsi_WrapsOnWordBoundaries()
    {
        Assert.Equal(new[] { "hello", "world" }, TextLayout.WrapTextWithAnsi("hello world", 5));
    }

    [Fact]
    public void WrapTextWithAnsi_BreaksLongWords()
    {
        var lines = TextLayout.WrapTextWithAnsi("abcdefghij", 4);
        Assert.Equal(new[] { "abcd", "efgh", "ij" }, lines);
    }

    [Fact]
    public void WrapTextWithAnsi_PreservesNewlines()
    {
        Assert.Equal(new[] { "a", "b" }, TextLayout.WrapTextWithAnsi("a\nb", 10));
    }

    [Fact]
    public void WrapTextWithAnsi_CarriesAnsiAcrossLines()
    {
        var lines = TextLayout.WrapTextWithAnsi("\x1b[31mhello world\x1b[0m", 5);
        Assert.Equal(2, lines.Count);
        // Second line re-opens the red foreground.
        Assert.Contains("\x1b[31m", lines[1]);
    }

    [Fact]
    public void ApplyBackgroundToLine_PadsToWidth()
    {
        Assert.Equal("[ab   ]", TextLayout.ApplyBackgroundToLine("ab", 5, s => $"[{s}]"));
    }

    [Fact]
    public void ExtractSegments_SplitsAroundOverlay()
    {
        var (before, beforeWidth, after, afterWidth) = TextLayout.ExtractSegments("abcdef", 2, 4, 2, strictAfter: true);
        Assert.Equal("ab", before);
        Assert.Equal(2, beforeWidth);
        Assert.Equal("ef", after);
        Assert.Equal(2, afterWidth);
    }

    [Fact]
    public void CompositeTuiLine_OverlaysAtColumn()
    {
        var result = Tui.CompositeTuiLine("abcdef", "XY", 2, 2, 6);
        Assert.Equal(6, UnicodeWidth.VisibleWidth(result));
        Assert.Equal("abXYef", Ansi.StripTerminalSequences(result));
    }

    [Fact]
    public void SplitLines_HandlesCrLfAndCr()
    {
        Assert.Equal(new[] { "a", "b", "c" }, TextLayout.SplitLines("a\r\nb\rc"));
    }

    [Fact]
    public void GraphemeCellRange_FindsWideCharacter()
    {
        var range = UnicodeWidth.GetGraphemeCellRange("a你b", 1);
        Assert.NotNull(range);
        Assert.Equal(1, range!.Value.Start);
        Assert.Equal(3, range.Value.End);
    }
}
