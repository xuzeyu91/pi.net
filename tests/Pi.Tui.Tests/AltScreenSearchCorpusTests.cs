using System.Globalization;
using System.Text;
using System.Text.Json;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>alt-screen-search.ts</c> port.
/// </summary>
/// <remarks>
/// <para>
/// <c>alt-screen-search-corpus.json</c> holds vectors captured by running the original TypeScript
/// module (the reference) over a systematic sweep:
/// <list type="bullet">
/// <item>25 queries (empty, whitespace-only, case variants, multi-word, regex metacharacters,
/// CJK, emoji, no-match) × 13 rendered transcript line sets for
/// <see cref="AltScreenSearchIndex.FindMatches"/> plus the match-key derivation,</item>
/// <item>sequential <see cref="AltScreenSearchIndex.Search"/> call chains exercising the corpus /
/// match cache invalidation (<c>changed</c> flag),</item>
/// <item>the <see cref="AltScreenSearchComponent"/> render sweep (12 widths × input / result-count /
/// hover states) including the <c>getNavigationDirectionAt</c> probes on row 2 and row 0,</item>
/// <item>the <c>setHoveredNavigationDirection</c> transition table,</item>
/// <item>the custom <c>navigationButtonStyle</c> callback, and</item>
/// <item>the focused / unfocused render.</item>
/// </list>
/// The vectors contain no machine-dependent input, so they are location independent.
/// </para>
/// </remarks>
public class AltScreenSearchCorpusTests
{
    // ------------------------------------------------------------------
    // Corpus loading
    // ------------------------------------------------------------------

    private static List<JsonElement> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "alt-screen-search-corpus.json");
        using var stream = File.OpenRead(path);
        var vectors = JsonSerializer.Deserialize<List<JsonElement>>(stream);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static readonly Lazy<List<JsonElement>> Data = new(LoadCorpus);

    private static string Kind(JsonElement vector) =>
        vector.GetProperty("kind").GetString() ?? "";

    private static List<string> Lines(JsonElement vector) =>
        vector.GetProperty("lines").EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    private static string Query(JsonElement vector) =>
        vector.GetProperty("query").GetString() ?? "";

    private static int? IntOrNull(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"expected number or null, got {element.ValueKind}");
    }

    /// <summary>Canonical serialization of one match's segments: <c>row:startCol:endCol</c> joined by <c>;</c>.</summary>
    private static string SerializeMatch(AltScreenSearchMatch match) =>
        string.Join(";", match.Segments.Select(s => $"{s.Row}:{s.StartCol}:{s.EndCol}"));

    private static List<string> SerializeMatches(IEnumerable<AltScreenSearchMatch> matches) =>
        matches.Select(SerializeMatch).ToList();

    // ------------------------------------------------------------------
    // Theory data
    // ------------------------------------------------------------------

    public static TheoryData<int> MatchIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Kind(Data.Value[i]) == "match")
            {
                data.Add(i);
            }
        }

        return data;
    }

    public static TheoryData<int> IndexIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Kind(Data.Value[i]) == "index")
            {
                data.Add(i);
            }
        }

        return data;
    }

    public static TheoryData<int> RenderIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Kind(Data.Value[i]) == "render")
            {
                data.Add(i);
            }
        }

        return data;
    }

    // ------------------------------------------------------------------
    // findAltScreenSearchMatches + getAltScreenSearchMatchKey
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MatchIndexes))]
    public void FindMatches_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var lines = Lines(vector);
        var query = Query(vector);

        var actual = AltScreenSearchIndex.FindMatches(lines, query);
        Assert.Equal(SerializeMatches(DeserializeMatches(vector)), SerializeMatches(actual));
        Assert.Equal(MatchKeys(vector), actual.Select(AltScreenSearchIndex.GetMatchKey).ToList());
    }

    private static List<AltScreenSearchMatch> DeserializeMatches(JsonElement vector)
    {
        var result = new List<AltScreenSearchMatch>();
        foreach (var match in vector.GetProperty("matches").EnumerateArray())
        {
            var segments = new List<AltScreenSearchSegment>();
            foreach (var segment in match.GetProperty("segments").EnumerateArray())
            {
                segments.Add(new AltScreenSearchSegment
                {
                    Row = segment.GetProperty("row").GetInt32(),
                    StartCol = segment.GetProperty("startCol").GetInt32(),
                    EndCol = segment.GetProperty("endCol").GetInt32(),
                });
            }

            result.Add(new AltScreenSearchMatch { Segments = segments });
        }

        return result;
    }

    private static List<string> MatchKeys(JsonElement vector) =>
        vector.GetProperty("keys").EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    // ------------------------------------------------------------------
    // AltScreenSearchIndex.search cache semantics
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(IndexIndexes))]
    public void Search_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var expected = new List<(int Count, bool Changed)>();
        foreach (var result in vector.GetProperty("results").EnumerateArray())
        {
            expected.Add((result.GetProperty("count").GetInt32(), result.GetProperty("changed").GetBoolean()));
        }

        var searchIndex = new AltScreenSearchIndex();
        var actual = new List<(int Count, bool Changed)>();
        foreach (var step in vector.GetProperty("steps").EnumerateArray())
        {
            var stepLines = step.GetProperty("lines").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var stepQuery = step.GetProperty("query").GetString() ?? "";
            var result = searchIndex.Search(stepLines, stepQuery);
            actual.Add((result.Matches.Count, result.Changed));
        }

        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------
    // AltScreenSearchComponent.render + getNavigationDirectionAt
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RenderIndexes))]
    public void Render_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var state = vector.GetProperty("state");
        var width = vector.GetProperty("width").GetInt32();

        // Fresh component per vector; the reference set the query through handleInput.
        var component = new AltScreenSearchComponent(_ => { });
        component.HandleInput(state.GetProperty("input").GetString() ?? "");
        component.SetResult(state.GetProperty("resultIndex").GetInt32(), state.GetProperty("resultCount").GetInt32());
        component.SetHoveredNavigationDirection(IntOrNull(state.GetProperty("hover")));

        Assert.Equal(Lines(vector), component.Render(width).ToList());

        var expectedDirs = vector.GetProperty("dirs").EnumerateArray().Select(IntOrNull).ToList();
        var actualDirs = new List<int?>();
        for (var column = 0; column < width; column++)
        {
            actualDirs.Add(component.GetNavigationDirectionAt(2, column));
        }

        Assert.Equal(expectedDirs, actualDirs);

        var expectedRow0 = vector.GetProperty("dirsRow0").EnumerateArray().Select(IntOrNull).ToList();
        var actualRow0 = new List<int?>();
        for (var column = 0; column < width; column++)
        {
            actualRow0.Add(component.GetNavigationDirectionAt(0, column));
        }

        Assert.Equal(expectedRow0, actualRow0);
    }

    // ------------------------------------------------------------------
    // navigationButtonStyle callback
    // ------------------------------------------------------------------

    [Fact]
    public void RenderStyled_MatchesTypeScriptReference()
    {
        // One shared component, mirroring the reference capture: the query accumulates across
        // the three vectors because each step feeds the state's input through handleInput.
        var component = new AltScreenSearchComponent(
            _ => { },
            (text, hovered) => hovered ? $"[{text}]" : $"<{text}>");

        foreach (var vector in Data.Value.Where(v => Kind(v) == "renderStyled"))
        {
            var state = vector.GetProperty("state");
            component.HandleInput(state.GetProperty("input").GetString() ?? "");
            component.SetResult(state.GetProperty("resultIndex").GetInt32(), state.GetProperty("resultCount").GetInt32());
            component.SetHoveredNavigationDirection(IntOrNull(state.GetProperty("hover")));
            Assert.Equal(Lines(vector), component.Render(vector.GetProperty("width").GetInt32()).ToList());
        }
    }

    // ------------------------------------------------------------------
    // focused render
    // ------------------------------------------------------------------

    [Fact]
    public void Focused_MatchesTypeScriptReference()
    {
        foreach (var vector in Data.Value.Where(v => Kind(v) == "focused"))
        {
            var component = new AltScreenSearchComponent(_ => { });
            component.Focused = vector.GetProperty("value").GetBoolean();
            Assert.Equal(Lines(vector), component.Render(vector.GetProperty("width").GetInt32()).ToList());
        }
    }

    // ------------------------------------------------------------------
    // setHoveredNavigationDirection transition table
    // ------------------------------------------------------------------

    [Fact]
    public void SetHoveredNavigationDirection_MatchesTypeScriptReference()
    {
        var vector = Data.Value.Single(v => Kind(v) == "hover");
        var expected = vector.GetProperty("returns").EnumerateArray().Select(e => e.GetBoolean()).ToList();

        var component = new AltScreenSearchComponent(_ => { });
        var actual = new List<bool>();
        foreach (var direction in vector.GetProperty("sequence").EnumerateArray().Select(IntOrNull))
        {
            actual.Add(component.SetHoveredNavigationDirection(direction));
        }

        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsComplete()
    {
        var vectors = Data.Value;
        Assert.Equal(521, vectors.Count);
        Assert.DoesNotContain(vectors, v => Kind(v) == "");

        var kinds = vectors.GroupBy(Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(403, kinds["match"]);
        Assert.Equal(4, kinds["index"]);
        Assert.Equal(108, kinds["render"]);
        Assert.Equal(1, kinds["hover"]);
        Assert.Equal(3, kinds["renderStyled"]);
        Assert.Equal(2, kinds["focused"]);
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var vectors = Data.Value;

        // Empty, whitespace-only and no-match queries are all present.
        var queries = vectors.Where(v => Kind(v) == "match").Select(Query).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("", queries);
        Assert.Contains("  ", queries);
        Assert.Contains("nomatch", queries);

        // Regex metacharacter queries exercise escapeRegExp.
        Assert.Contains("a.b", queries);
        Assert.Contains("c++", queries);
        Assert.Contains("(x)", queries);

        // Case-insensitive matching is covered by the case variants.
        Assert.Contains("BUILD", queries);
        Assert.Contains("Build", queries);

        // Multi-word queries with irregular whitespace exercise normalizeQuery.
        Assert.Contains("  node   build  ", queries);

        // Cross-row queries span two transcript rows through the corpus separator.
        Assert.Contains("bar foobar", queries);
        Assert.Contains("build.ts error", queries);
        Assert.Contains("module 'foo' at", queries);

        // Non-ASCII (CJK, accented, emoji) queries and line sets are covered.
        Assert.Contains("你好", queries);
        Assert.Contains("日本語", queries);
        Assert.Contains("café", queries);
        Assert.Contains("👋", queries);

        // Both empty and non-empty transcript line sets are present.
        var lineSets = vectors.Where(v => Kind(v) == "match").Select(v => Lines(v).Count).ToHashSet();
        Assert.Contains(0, lineSets);
        Assert.Contains(4, lineSets);

        // Cross-row matches (a query spanning two rows through the corpus separator) are covered.
        var crossRow = vectors
            .Where(v => Kind(v) == "match")
            .SelectMany(v => DeserializeMatches(v))
            .Any(match => match.Segments.Count > 1 && match.Segments[0].Row != match.Segments[^1].Row);
        Assert.True(crossRow, "expected at least one multi-row match");

        // The render sweep covers the degenerate widths and the button layout.
        var widths = vectors.Where(v => Kind(v) == "render").Select(v => v.GetProperty("width").GetInt32()).ToHashSet();
        Assert.Contains(1, widths);
        Assert.Contains(120, widths);

        // Navigation directions are probed on both the button row and a non-button row.
        var directions = vectors
            .Where(v => Kind(v) == "render")
            .SelectMany(v => v.GetProperty("dirs").EnumerateArray().Select(IntOrNull))
            .ToHashSet();
        Assert.Contains(-1, directions);
        Assert.Contains(1, directions);
        Assert.Contains(null, directions);
    }
}
