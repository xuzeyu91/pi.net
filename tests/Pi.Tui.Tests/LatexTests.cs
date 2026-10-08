using System.Text.Json;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential test for <see cref="Latex.Render"/>.
/// </summary>
/// <remarks>
/// <c>latex-corpus.json</c> holds 2,822 (source, display, expected) vectors captured by running the
/// original TypeScript <c>renderLatex</c> (the reference implementation) over two input sets:
/// <list type="bullet">
/// <item>every case from the upstream <c>packages/tui/test/latex.test.ts</c> suite, and</item>
/// <item>a systematic sweep that exercises every entry of each lookup table (all symbols, named
/// operators, limit operators, relations, negations, blackboard letters, accents, spacing/size/font
/// commands, wrappers), every environment, all script/root/fraction shapes, plus malformed input.</item>
/// </list>
/// A null <c>expected</c> means the reference returned <c>undefined</c> (unsupported syntax).
/// Regenerate with the harness described in <c>docs/tui-porting-status.md</c>.
/// </remarks>
public class LatexTests
{
    private sealed record CorpusCase(string Source, bool Display, string? Expected);

    private static List<CorpusCase> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "latex-corpus.json");
        using var stream = File.OpenRead(path);
        var cases = JsonSerializer.Deserialize<List<CorpusCase>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(cases);
        return cases;
    }

    private static readonly Lazy<List<CorpusCase>> Corpus = new(LoadCorpus);

    private static readonly Lazy<Dictionary<(string Source, bool Display), string?>> Expected =
        new(() => Corpus.Value.ToDictionary(entry => (entry.Source, entry.Display), entry => entry.Expected));

    public static TheoryData<string, bool> CorpusKeys()
    {
        var data = new TheoryData<string, bool>();
        foreach (var testCase in Corpus.Value)
        {
            data.Add(testCase.Source, testCase.Display);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusKeys))]
    public void RenderMatchesTheTypeScriptReference(string source, bool display)
    {
        var expected = Expected.Value[(source, display)];

        var actual = Latex.Render(source, new RenderLatexOptions { Display = display });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CorpusIsFullyCovered()
    {
        // Guards against the corpus silently shrinking (e.g. a truncated fixture).
        Assert.True(Corpus.Value.Count >= 2800, $"corpus has only {Corpus.Value.Count} cases");
    }

    [Fact]
    public void CorpusCoversBothDisplayModesAndUnsupportedInput()
    {
        Assert.Contains(Corpus.Value, entry => entry.Display);
        Assert.Contains(Corpus.Value, entry => !entry.Display);
        Assert.Contains(Corpus.Value, entry => entry.Expected is null);
    }
}
