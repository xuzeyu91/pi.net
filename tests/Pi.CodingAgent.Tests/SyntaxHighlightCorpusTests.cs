using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/syntax-highlight.ts</c> against <c>syntax-highlight-corpus.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two halves are checked separately. <see cref="SyntaxHighlight.RenderHighlightedHtml"/> is a faithful
/// port and is compared against the original's output directly. The tokenizer sits behind
/// <see cref="SyntaxHighlight.HighlighterOverride"/>, so the corpus records the raw highlight.js HTML
/// and the test feeds it back through a stub — that way the comparison exercises this port's rendering
/// rather than the absent tokenizer.
/// </para>
/// <para>
/// The theme formatters cannot be serialised, so the corpus names them and both sides map the same id
/// to the same transform. See divergence C23 in <c>docs/coding-agent-porting-status.md</c>.
/// </para>
/// </remarks>
public class SyntaxHighlightCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "syntax-highlight-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches:\n  " + string.Join("\n  ", failures);

    /// <summary>The named formatters, mirroring the generator's map.</summary>
    private static readonly Dictionary<string, HighlightFormatter> Formatters = new(StringComparer.Ordinal)
    {
        ["upper"] = text => text.ToUpperInvariant(),
        ["bracket"] = text => $"[{text}]",
        ["len"] = text => $"<{text.Length}>{text}",
        ["star"] = text => $"*{text}*",
    };

    private static Dictionary<string, HighlightFormatter> ResolveTheme(JsonElement theme)
    {
        var resolved = new Dictionary<string, HighlightFormatter>(StringComparer.Ordinal);
        foreach (var entry in theme.EnumerateObject())
        {
            resolved[entry.Name] = Formatters[entry.Value.GetString()!];
        }

        return resolved;
    }

    private static string DescribeTheme(JsonElement theme) =>
        !theme.EnumerateObject().Any()
            ? "{}"
            : "{" + string.Join(", ", theme.EnumerateObject().Select(entry => $"{entry.Name}={entry.Value.GetString()}")) + "}";

    [Fact]
    public void RenderHighlightedHtml_Crafted_MatchesTypeScript()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("crafted").EnumerateArray())
        {
            count++;
            var label = $"{vector.GetProperty("label").GetString()} theme={DescribeTheme(vector.GetProperty("theme"))}";
            var html = vector.GetProperty("html").GetString()!;
            var expected = vector.GetProperty("output").GetString()!;
            var actual = SyntaxHighlight.RenderHighlightedHtml(html, ResolveTheme(vector.GetProperty("theme")));
            Check(failures, label, expected, actual);
        }

        Assert.True(count > 0, "the crafted section must not be empty");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void RenderHighlightedHtml_FromHighlightJs_MatchesTypeScript()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("highlighted").EnumerateArray())
        {
            count++;
            if (!vector.TryGetProperty("rawHtml", out var rawHtml))
            {
                continue;
            }

            var label = $"{vector.GetProperty("label").GetString()} theme={DescribeTheme(vector.GetProperty("theme"))}";
            var expected = vector.GetProperty("output").GetString()!;
            var actual = SyntaxHighlight.RenderHighlightedHtml(rawHtml.GetString()!, ResolveTheme(vector.GetProperty("theme")));
            Check(failures, label, expected, actual);
        }

        Assert.True(count > 0, "the highlighted section must not be empty");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// <c>highlight()</c> must pick the same branch as the original — including the truthiness test on
    /// <c>options.language</c>, which sends an empty string down the auto path — and pass the code,
    /// language, ignoreIllegals and languageSubset through unchanged.
    /// </summary>
    [Fact]
    public void Highlight_DelegatesAndMatchesTypeScript()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var section in new[] { "highlighted", "auto" })
        {
            foreach (var vector in Corpus.GetProperty(section).EnumerateArray())
            {
                if (!vector.TryGetProperty("rawHtml", out var rawHtml))
                {
                    continue;
                }

                count++;
                var label = vector.GetProperty("label").GetString()!;
                var code = vector.GetProperty("code").GetString()!;
                var language = vector.TryGetProperty("language", out var languageValue) && languageValue.ValueKind != JsonValueKind.Null
                    ? languageValue.GetString()
                    : null;
                var subset = vector.TryGetProperty("languageSubset", out var subsetValue) && subsetValue.ValueKind == JsonValueKind.Array
                    ? subsetValue.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : null;

                var stub = new RecordingHighlighter(rawHtml.GetString()!);
                SyntaxHighlight.HighlighterOverride = () => stub;
                try
                {
                    var actual = SyntaxHighlight.Highlight(code, new HighlightOptions
                    {
                        Language = language,
                        LanguageSubset = subset,
                        Theme = ResolveTheme(vector.GetProperty("theme")),
                    });

                    Check(failures, $"{label} output", vector.GetProperty("output").GetString()!, actual);
                    Check(failures, $"{label} call", ExpectedCall(code, language, subset), stub.Describe());
                }
                finally
                {
                    SyntaxHighlight.HighlighterOverride = null;
                }
            }
        }

        Assert.True(count > 0, "no highlight() vectors were replayed");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>The corpus records what real highlight.js answered; the seam must relay it verbatim.</summary>
    [Fact]
    public void SupportsLanguage_DelegatesToTokenizer()
    {
        var failures = new List<string>();
        var vectors = Corpus.GetProperty("supportsLanguage").EnumerateArray().ToArray();
        var stub = new RecordingHighlighter(string.Empty);

        SyntaxHighlight.HighlighterOverride = () => stub;
        try
        {
            foreach (var vector in vectors)
            {
                var name = vector.GetProperty("name").GetString()!;
                Check(
                    failures,
                    $"supportsLanguage({name}) with stub",
                    vector.GetProperty("supported").GetBoolean(),
                    SyntaxHighlight.SupportsLanguage(name));
            }
        }
        finally
        {
            SyntaxHighlight.HighlighterOverride = null;
        }

        // Without a tokenizer the truthful answer is "no languages", which is the documented gap.
        foreach (var vector in vectors)
        {
            var name = vector.GetProperty("name").GetString()!;
            Check(failures, $"supportsLanguage({name}) with no tokenizer", false, SyntaxHighlight.SupportsLanguage(name));
        }

        Assert.True(vectors.Length > 0, "the supportsLanguage section must not be empty");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// The fallback tokenizer must be highlight.js's own plaintext output. The corpus's <c>plaintext</c>
    /// vectors hold the real thing, so replaying them through the default highlighter is a genuine
    /// equivalence check rather than a restatement of the fallback's implementation.
    /// </summary>
    [Fact]
    public void PlainTextFallback_MatchesHighlightJsPlaintext()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("highlighted").EnumerateArray())
        {
            if (vector.GetProperty("language").GetString() != "plaintext")
            {
                continue;
            }

            count++;
            var label = vector.GetProperty("label").GetString()!;
            var code = vector.GetProperty("code").GetString()!;

            // No language, so the original takes the auto path; with a plaintext-only tokenizer that is
            // the same output highlight.js produces for the plaintext language.
            var actual = SyntaxHighlight.Highlight(code, new HighlightOptions
            {
                Theme = ResolveTheme(vector.GetProperty("theme")),
            });

            Check(failures, label, vector.GetProperty("output").GetString()!, actual);
        }

        Assert.True(count > 0, "no plaintext vectors were replayed");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static string ExpectedCall(string code, string? language, string[]? subset)
    {
        var target = string.IsNullOrEmpty(language)
            ? $"highlightAuto(subset={(subset is null ? "null" : "[" + string.Join(",", subset) + "]")})"
            : $"highlight({language}, ignoreIllegals=False)";
        return $"code={Quote(code)} {target}";
    }

    private static string Quote(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void Check(List<string> failures, string label, string expected, string actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}:\n      expected {Quote(expected)}\n      got      {Quote(actual)}");
        }
    }

    private static void Check(List<string> failures, string label, bool expected, bool actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected}, got {actual}");
        }
    }

    /// <summary>Answers from the corpus and records how it was called.</summary>
    private sealed class RecordingHighlighter : IHighlighter
    {
        private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            "python",
            "javascript",
            "json",
            "bash",
            "rust",
            "csharp",
            "go",
            "typescript",
            "plaintext",
        };

        private readonly string _html;
        private string? _call;

        public RecordingHighlighter(string html) => _html = html;

        public string Highlight(string code, string language, bool ignoreIllegals)
        {
            _call = $"code={Quote(code)} highlight({language}, ignoreIllegals={ignoreIllegals})";
            return _html;
        }

        public string HighlightAuto(string code, IReadOnlyList<string>? languageSubset)
        {
            var subset = languageSubset is null ? "null" : "[" + string.Join(",", languageSubset) + "]";
            _call = $"code={Quote(code)} highlightAuto(subset={subset})";
            return _html;
        }

        public bool SupportsLanguage(string name) => Known.Contains(name);

        public Task LoadAllLanguagesAsync() => Task.CompletedTask;

        public string Describe() => _call ?? "<not called>";
    }
}
