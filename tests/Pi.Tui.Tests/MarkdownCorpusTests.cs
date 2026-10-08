using System.Text.Json;
using System.Text.Json.Serialization;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>components/markdown.ts</c> port.
/// </summary>
/// <remarks>
/// <c>markdown-corpus.json</c> holds vectors captured by running the original TypeScript
/// <c>Markdown</c> component (the reference) over a systematic sweep:
/// <list type="bullet">
/// <item>159 markdown inputs (headings, paragraphs, inline styles, lists, blockquotes, code
/// blocks, tables, hr, html, latex, escapes, CJK/emoji, malformed and streaming fragments)
/// × 11 widths × 3 padding combinations,</item>
/// <item>the options matrix (preserveOrderedListMarkers / preserveBackslashEscapes /
/// renderLatex) over a representative subset,</item>
/// <item>the <c>transform</c> callback sequence (including cache invalidation),</item>
/// <item>the default text style path (foreground + background + decorations),</item>
/// <item>the optional <c>highlightCode</c> / <c>codeBlockIndent</c> theme members,</item>
/// <item>links with OSC 8 hyperlink support disabled (parenthesised URL fallback), and</item>
/// <item>the setText / invalidate caching behaviour.</item>
/// </list>
/// The vectors contain no machine-dependent input, so they are location independent.
/// Regenerate with the harness described in <c>docs/tui-porting-status.md</c>.
/// </remarks>
public class MarkdownCorpusTests
{
    // ------------------------------------------------------------------
    // chalk semantics
    // ------------------------------------------------------------------

    /// <summary>
    /// A faithful subset of chalk v4/v5's <c>applyStyle</c>, matching the shim the corpus was
    /// captured with: empty strings pass through, nested resets are re-opened with the full
    /// open sequence, and newlines are encased with close/open (CRLF-aware).
    /// </summary>
    private sealed class ChalkStyle
    {
        private readonly (string Open, string Close)[] _chain;

        private ChalkStyle((string Open, string Close)[] chain) => _chain = chain;

        public static readonly ChalkStyle Bold = new([("\x1b[1m", "\x1b[22m")]);

        public static readonly ChalkStyle Dim = new([("\x1b[2m", "\x1b[22m")]);

        public static readonly ChalkStyle Italic = new([("\x1b[3m", "\x1b[23m")]);

        public static readonly ChalkStyle Underline = new([("\x1b[4m", "\x1b[24m")]);

        public static readonly ChalkStyle Strikethrough = new([("\x1b[9m", "\x1b[29m")]);

        public static readonly ChalkStyle Blue = new([("\x1b[34m", "\x1b[39m")]);

        public static readonly ChalkStyle Cyan = new([("\x1b[36m", "\x1b[39m")]);

        public static readonly ChalkStyle Yellow = new([("\x1b[33m", "\x1b[39m")]);

        public static readonly ChalkStyle Green = new([("\x1b[32m", "\x1b[39m")]);

        public static readonly ChalkStyle Red = new([("\x1b[31m", "\x1b[39m")]);

        public static readonly ChalkStyle BgBlue = new([("\x1b[44m", "\x1b[49m")]);

        public ChalkStyle Then(ChalkStyle inner) => new([.. _chain, .. inner._chain]);

        public Func<string, string> ToFunction() => Apply;

        private string Apply(string value)
        {
            if (value.Length == 0)
            {
                return value;
            }

            var openAll = string.Concat(_chain.Select(style => style.Open));
            var closeAll = string.Concat(_chain.Select(style => style.Close).Reverse());
            if (value.Contains('\x1b'))
            {
                foreach (var style in _chain)
                {
                    value = value.Replace(style.Close, openAll, StringComparison.Ordinal);
                }
            }

            if (value.Contains('\n'))
            {
                value = EncaseCRLF(value, closeAll, openAll);
            }

            return openAll + value + closeAll;
        }

        // Port of chalk's stringEncaseCRLFWithFirstIndex.
        private static string EncaseCRLF(string value, string prefix, string postfix)
        {
            var result = "";
            var endIndex = 0;
            var index = value.IndexOf('\n');
            while (index != -1)
            {
                var gotCR = index > 0 && value[index - 1] == '\r';
                result += value[endIndex..(gotCR ? index - 1 : index)]
                    + prefix + (gotCR ? "\r\n" : "\n") + postfix;
                endIndex = index + 1;
                index = value.IndexOf('\n', endIndex);
            }

            return result + value[endIndex..];
        }
    }

    // ------------------------------------------------------------------
    // Themes
    // ------------------------------------------------------------------

    private static MarkdownTheme NewTheme() => new()
    {
        Heading = ChalkStyle.Bold.Then(ChalkStyle.Cyan).ToFunction(),
        Link = ChalkStyle.Blue.ToFunction(),
        LinkUrl = ChalkStyle.Dim.ToFunction(),
        Code = ChalkStyle.Yellow.ToFunction(),
        CodeBlock = ChalkStyle.Green.ToFunction(),
        CodeBlockBorder = ChalkStyle.Dim.ToFunction(),
        Quote = ChalkStyle.Italic.ToFunction(),
        QuoteBorder = ChalkStyle.Dim.ToFunction(),
        Hr = ChalkStyle.Dim.ToFunction(),
        ListBullet = ChalkStyle.Cyan.ToFunction(),
        Bold = ChalkStyle.Bold.ToFunction(),
        Italic = ChalkStyle.Italic.ToFunction(),
        Strikethrough = ChalkStyle.Strikethrough.ToFunction(),
        Underline = ChalkStyle.Underline.ToFunction(),
    };

    private static MarkdownTheme NewHighlightTheme() => new()
    {
        Heading = ChalkStyle.Bold.Then(ChalkStyle.Cyan).ToFunction(),
        Link = ChalkStyle.Blue.ToFunction(),
        LinkUrl = ChalkStyle.Dim.ToFunction(),
        Code = ChalkStyle.Yellow.ToFunction(),
        CodeBlock = ChalkStyle.Green.ToFunction(),
        CodeBlockBorder = ChalkStyle.Dim.ToFunction(),
        Quote = ChalkStyle.Italic.ToFunction(),
        QuoteBorder = ChalkStyle.Dim.ToFunction(),
        Hr = ChalkStyle.Dim.ToFunction(),
        ListBullet = ChalkStyle.Cyan.ToFunction(),
        Bold = ChalkStyle.Bold.ToFunction(),
        Italic = ChalkStyle.Italic.ToFunction(),
        Strikethrough = ChalkStyle.Strikethrough.ToFunction(),
        Underline = ChalkStyle.Underline.ToFunction(),
        HighlightCode = (code, lang) => code
            .Split('\n')
            .Select(line => $"{lang ?? ""}:{line}")
            .ToArray(),
        CodeBlockIndent = "    ",
    };

    private static DefaultTextStyle NewDefaultTextStyle() => new()
    {
        Color = ChalkStyle.Red.ToFunction(),
        BgColor = ChalkStyle.BgBlue.ToFunction(),
        Bold = true,
        Italic = true,
    };

    // ------------------------------------------------------------------
    // Corpus models
    // ------------------------------------------------------------------

    private sealed record TransformCall(string Source, int AvailableWidth);

    private sealed record Vector(
        int Id,
        string Section,
        string? Input,
        int? Width,
        int? PaddingX,
        int? PaddingY,
        JsonElement? Options,
        List<string>? Lines,
        string? Error,
        List<TransformCall>? Calls,
        List<List<string>>? Snapshots);

    private static List<Vector> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "markdown-corpus.json");
        using var stream = File.OpenRead(path);
        var vectors = JsonSerializer.Deserialize<List<Vector>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(vectors);
        return vectors;
    }

    private static readonly Lazy<List<Vector>> Data = new(LoadCorpus);

    public MarkdownCorpusTests()
    {
        // The vectors were captured with a fixed capability set; make sure a previous test has
        // not left an override behind (the assembly disables parallelization, so the process-wide
        // capability cache is shared).
        TerminalImage.ResetCapabilitiesCache();
    }

    private static void SetHyperlinks(bool enabled)
    {
        TerminalImage.SetCapabilities(new TerminalCapabilities(ImageProtocol.None, true, enabled));
    }

    // ------------------------------------------------------------------
    // Theory data
    // ------------------------------------------------------------------

    public static TheoryData<int> SectionIndexes(string section)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Data.Value[i].Section == section)
            {
                data.Add(i);
            }
        }

        return data;
    }

    public static TheoryData<int> RenderIndexes() => SectionIndexes("render");

    public static TheoryData<int> OptionsIndexes() => SectionIndexes("options");

    public static TheoryData<int> DefaultStyleIndexes() => SectionIndexes("defaultStyle");

    public static TheoryData<int> HighlightIndexes() => SectionIndexes("highlight");

    public static TheoryData<int> NoHyperlinksIndexes() => SectionIndexes("noHyperlinks");

    // ------------------------------------------------------------------
    // render(width)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RenderIndexes))]
    public void Render_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        SetHyperlinks(true);
        var markdown = new Markdown(
            vector.Input ?? "",
            vector.PaddingX ?? 0,
            vector.PaddingY ?? 0,
            NewTheme());
        Assert.Equal(vector.Lines, markdown.Render(vector.Width ?? 0));
    }

    [Theory]
    [MemberData(nameof(OptionsIndexes))]
    public void Options_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        SetHyperlinks(true);
        var options = new MarkdownOptions();
        if (vector.Options is { } raw)
        {
            if (raw.TryGetProperty("preserveOrderedListMarkers", out var preserveMarkers))
            {
                options.PreserveOrderedListMarkers = preserveMarkers.GetBoolean();
            }

            if (raw.TryGetProperty("preserveBackslashEscapes", out var preserveEscapes))
            {
                options.PreserveBackslashEscapes = preserveEscapes.GetBoolean();
            }

            if (raw.TryGetProperty("renderLatex", out var renderLatex))
            {
                options.RenderLatex = renderLatex.GetBoolean();
            }
        }

        var markdown = new Markdown(vector.Input ?? "", 1, 0, NewTheme(), null, options);
        Assert.Equal(vector.Lines, markdown.Render(vector.Width ?? 0));
    }

    [Theory]
    [MemberData(nameof(DefaultStyleIndexes))]
    public void DefaultTextStyle_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        SetHyperlinks(true);
        var markdown = new Markdown(
            vector.Input ?? "",
            1,
            1,
            NewTheme(),
            NewDefaultTextStyle());
        Assert.Equal(vector.Lines, markdown.Render(vector.Width ?? 0));
    }

    [Theory]
    [MemberData(nameof(HighlightIndexes))]
    public void HighlightCodeTheme_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        SetHyperlinks(true);
        var markdown = new Markdown(vector.Input ?? "", 1, 0, NewHighlightTheme());
        Assert.Equal(vector.Lines, markdown.Render(vector.Width ?? 0));
    }

    [Theory]
    [MemberData(nameof(NoHyperlinksIndexes))]
    public void WithoutHyperlinks_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        SetHyperlinks(false);
        var markdown = new Markdown(vector.Input ?? "", 1, 0, NewTheme());
        Assert.Equal(vector.Lines, markdown.Render(vector.Width ?? 0));
    }

    // ------------------------------------------------------------------
    // transform callback + caching
    // ------------------------------------------------------------------

    [Fact]
    public void Transform_MatchesTypeScriptReference()
    {
        var vector = Data.Value.Single(v => v.Section == "transform");
        SetHyperlinks(true);
        var calls = new List<TransformCall>();
        var markdown = new Markdown("source", 2, 0, NewTheme(), null, new MarkdownOptions
        {
            Transform = (source, availableWidth) =>
            {
                calls.Add(new TransformCall(source, availableWidth));
                return $"{source} {availableWidth}";
            },
        });

        markdown.Render(80);
        markdown.Render(80);
        markdown.Render(60);
        markdown.SetText("updated");
        markdown.Render(60);
        markdown.Invalidate();
        markdown.Render(60);

        Assert.Equal(vector.Calls, calls);
    }

    [Fact]
    public void Caching_MatchesTypeScriptReference()
    {
        var vector = Data.Value.Single(v => v.Section == "cache");
        SetHyperlinks(true);
        var markdown = new Markdown("first", 1, 0, NewTheme());
        var snapshots = new List<List<string>>
        {
            markdown.Render(40).ToList(),
        };

        markdown.SetText("second");
        snapshots.Add(markdown.Render(40).ToList());
        snapshots.Add(markdown.Render(40).ToList());
        markdown.Invalidate();
        snapshots.Add(markdown.Render(40).ToList());

        Assert.Equal(vector.Snapshots, snapshots);
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsComplete()
    {
        var vectors = Data.Value;
        Assert.True(vectors.Count >= 4900, $"expected at least 4900 vectors, got {vectors.Count}");
        Assert.DoesNotContain(vectors, v => v.Error is not null);

        var sections = vectors.GroupBy(v => v.Section).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(7, sections.Count);
        Assert.True(sections["render"] >= 4800, $"render vectors: {sections["render"]}");
        Assert.True(sections["options"] >= 100, $"options vectors: {sections["options"]}");
        Assert.True(sections["defaultStyle"] >= 30, $"defaultStyle vectors: {sections["defaultStyle"]}");
        Assert.True(sections["highlight"] >= 10, $"highlight vectors: {sections["highlight"]}");
        Assert.True(sections["noHyperlinks"] >= 20, $"noHyperlinks vectors: {sections["noHyperlinks"]}");
        Assert.Equal(1, sections["transform"]);
        Assert.Equal(1, sections["cache"]);
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var vectors = Data.Value;

        // Both empty and non-empty render results are present (the empty-source early return
        // and the normal path).
        Assert.Contains(vectors, v => v.Lines is { Count: 0 });
        Assert.Contains(vectors, v => v.Lines is { Count: > 0 });

        // Every block construct the renderer special-cases is exercised.
        var inputs = vectors.Select(v => v.Input ?? "").ToHashSet(StringComparer.Ordinal);
        Assert.Contains(inputs, input => input.StartsWith('#'));
        Assert.Contains(inputs, input => input.StartsWith('-'));
        Assert.Contains(inputs, input => input.StartsWith('1'));
        Assert.Contains(inputs, input => input.StartsWith('>'));
        Assert.Contains(inputs, input => input.StartsWith('|'));
        Assert.Contains(inputs, input => input.StartsWith("```"));
        Assert.Contains(inputs, input => input == "---");
        Assert.Contains(inputs, input => input.StartsWith('<'));
        Assert.Contains(inputs, input => input.Contains('$'));
        Assert.Contains(inputs, input => input.Contains("~~"));
        Assert.Contains(inputs, input => input.Contains("]("));
        Assert.Contains(inputs, input => input.Contains('\\'));
        Assert.Contains(inputs, input => input.Contains("你好"));

        // The narrow widths that force the table fallback and the wrapping paths are present.
        Assert.Contains(vectors, v => v.Width is <= 5);
        Assert.Contains(vectors, v => v.Width is >= 80);

        // Both hyperlink states are covered: the hyperlink-disabled section plus the bare URL /
        // e-mail inputs that exercise the link autolink paths.
        Assert.Contains(vectors, v => v.Section == "noHyperlinks");
        Assert.Contains(vectors, v => v.Input is "https://bare.example.com" or "foo@bar.com");

        // The default text style path (background colour + decorations) is covered.
        Assert.Contains(vectors, v => v.Section == "defaultStyle");

        // The optional theme members are covered.
        Assert.Contains(vectors, v => v.Section == "highlight");
    }
}
