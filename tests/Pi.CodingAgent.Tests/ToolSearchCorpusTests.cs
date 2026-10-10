using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.ToolSearch;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the tool-search extension (port of
/// <c>extensions/tool-search/{tool.ts,index.ts}</c>, batch 4d-6). Replays <c>tool-search-corpus.json</c>,
/// which the TS implementation produced: the stemmer (through <c>tokenize</c>, its only caller), the
/// tokenizer, the search-document builder, the BM25 ranker (default and custom parameters), the
/// <c>tool_search</c> tool's <c>execute</c>, and the schema-identity guard.
/// </summary>
public class ToolSearchCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tool-search-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ constants

    [Fact]
    public void Constants_MatchTypeScript()
    {
        var constants = Corpus.GetProperty("constants");
        // Both sides are pinned: the constant is the port's value, the corpus the TS oracle.
        Assert.Equal(ToolSearch.ToolSearchToolName, constants.GetProperty("toolSearchToolName").GetString());
        Assert.Equal(ToolSearch.DefaultToolSearchLimit, constants.GetProperty("defaultToolSearchLimit").GetInt32());
        Assert.Equal(ToolSearch.ToolSearchDescription, constants.GetProperty("description").GetString());
        // Compare structurally: key order is not part of the schema, and TypeBox emits `required`
        // before `properties` while the C# literal is written in declaration order.
        var expectedSchema = JsonNode.Parse(constants.GetProperty("schema").GetRawText());
        var actualSchema = JsonNode.Parse(JsonSerializer.Serialize(ToolSearch.ToolSearchSchema.JsonSchema));
        Assert.True(JsonNode.DeepEquals(expectedSchema, actualSchema), $"schema mismatch: {actualSchema?.ToJsonString()}");
    }

    // ------------------------------------------------------------------ stem (via tokenize)

    [Theory]
    [MemberData(nameof(StemKeys))]
    public void Stem_MatchesTypeScript(string input)
    {
        var vector = FindByProperty(Corpus.GetProperty("stem"), "input", input);
        Assert.Equal(vector.GetProperty("stem").GetString(), ToolSearch.Stem(input));
    }

    public static IEnumerable<object[]> StemKeys() =>
        Corpus.GetProperty("stem").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("input").GetString()! });

    // ------------------------------------------------------------------ tokenize

    [Theory]
    [MemberData(nameof(TokenizeKeys))]
    public void Tokenize_MatchesTypeScript(string input)
    {
        var vector = FindByProperty(Corpus.GetProperty("tokenize"), "input", input);
        var expected = vector.GetProperty("terms").EnumerateArray().Select(term => term.GetString()!).ToList();
        Assert.Equal(expected, ToolSearch.Tokenize(input));
    }

    public static IEnumerable<object[]> TokenizeKeys() =>
        Corpus.GetProperty("tokenize").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("input").GetString()! });

    // ------------------------------------------------------------------ createToolSearchDocument

    [Theory]
    [MemberData(nameof(DocumentKeys))]
    public void CreateToolSearchDocument_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("documents"), "label", label);
        var tool = ReadTool(vector.GetProperty("tool"));
        var ns = ReadNamespace(vector.GetProperty("namespace"));
        var expected = vector.GetProperty("expected");

        var document = ToolSearch.CreateToolSearchDocument(tool, ns);

        Assert.Equal(expected.GetProperty("name").GetString(), document.Name);
        Assert.Equal(expected.GetProperty("text").GetString(), document.Text);
    }

    public static IEnumerable<object[]> DocumentKeys() =>
        Corpus.GetProperty("documents").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("label").GetString()! });

    // ------------------------------------------------------------------ Bm25Ranker

    [Fact]
    public void Bm25Ranker_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("rank").EnumerateArray())
        {
            var documents = ReadDocuments(vector.GetProperty("documents"));
            var matches = new ToolSearch.Bm25Ranker().Rank(
                vector.GetProperty("query").GetString()!,
                documents,
                vector.GetProperty("limit").GetInt32());
            CheckMatches($"rank {vector.GetProperty("query").GetString()} limit {vector.GetProperty("limit").GetInt32()}",
                vector.GetProperty("expected"), matches);
        }
    }

    [Fact]
    public void Bm25Ranker_CustomParameters_MatchTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("rankCustom").EnumerateArray())
        {
            var k1 = vector.GetProperty("k1").ValueKind == JsonValueKind.Null ? (double?)null : vector.GetProperty("k1").GetDouble();
            var b = vector.GetProperty("b").ValueKind == JsonValueKind.Null ? (double?)null : vector.GetProperty("b").GetDouble();
            var documents = ReadDocuments(vector.GetProperty("documents"));
            var matches = new ToolSearch.Bm25Ranker(k1, b).Rank(
                vector.GetProperty("query").GetString()!,
                documents,
                vector.GetProperty("limit").GetInt32());
            CheckMatches($"rankCustom k1={k1} b={b} query={vector.GetProperty("query").GetString()}",
                vector.GetProperty("expected"), matches);
        }
    }

    private static void CheckMatches(string label, JsonElement expected, IReadOnlyList<ToolSearchMatch> actual)
    {
        var expectedMatches = expected.EnumerateArray().ToList();
        Assert.Equal(expectedMatches.Count, actual.Count);
        for (var index = 0; index < expectedMatches.Count; index++)
        {
            Assert.Equal(expectedMatches[index].GetProperty("name").GetString(), actual[index].Name);
            var expectedScore = expectedMatches[index].GetProperty("score").GetDouble();
            Assert.True(
                Math.Abs(expectedScore - actual[index].Score) < 1e-9,
                $"{label} #{index}: expected score {expectedScore}, got {actual[index].Score}");
        }
    }

    // ------------------------------------------------------------------ tool_search execute

    [Fact]
    public async Task Execute_MatchesTypeScript()
    {
        var allTools = Corpus.GetProperty("allTools").EnumerateArray().Select(ReadTool).ToList();
        foreach (var vector in Corpus.GetProperty("execute").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var withTools = vector.GetProperty("withTools").GetBoolean();
            var active = vector.GetProperty("activeBefore").EnumerateArray().Select(name => name.GetString()!).ToList();
            var fake = new FakeTools(allTools, active);
            var definition = ToolSearch.CreateToolSearchToolDefinition(
                withTools ? new ToolSearchToolOptions { Tools = fake } : new ToolSearchToolOptions());

            var args = new Dictionary<string, object?> { ["query"] = vector.GetProperty("query").GetString() };
            var limit = vector.GetProperty("limit");
            if (limit.ValueKind == JsonValueKind.Number)
            {
                args["limit"] = JsonValue.Create(limit.GetDouble());
            }

            var expectedError = vector.GetProperty("error");
            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => definition.Execute("call-1", args, CancellationToken.None, null, null));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
                Assert.Equal(active, fake.Active);
                continue;
            }

            var result = await definition.Execute("call-1", args, CancellationToken.None, null, null);

            var expectedText = vector.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal(expectedText, Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);

            var expectedLoaded = vector.GetProperty("details").GetProperty("loaded")
                .EnumerateArray().Select(name => name.GetString()!).ToList();
            var details = Assert.IsType<ToolSearchToolDetails>(result.Details);
            Assert.Equal(expectedLoaded, details.Loaded);

            var expectedActiveAfter = vector.GetProperty("activeAfter").EnumerateArray().Select(name => name.GetString()!).ToList();
            Assert.Equal(expectedActiveAfter, fake.Active);
            _ = label;
        }
    }

    // ------------------------------------------------------------------ schema-identity guard

    [Fact]
    public void IsToolSearchTool_UsesSchemaIdentity()
    {
        var identity = Corpus.GetProperty("identity");
        var matching = new ToolInfo
        {
            Name = ToolSearch.ToolSearchToolName,
            Description = "x",
            Parameters = ToolSearch.ToolSearchSchema,
            Exposure = ToolExposure.ModelOnly,
            SourceInfo = SourceInfos.CreateSynthetic("builtin:test", "builtin"),
        };
        Assert.Equal(identity.GetProperty("match").GetBoolean(), ToolSearch.IsToolSearchTool(matching));

        var nameOnly = matching with { Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }) };
        Assert.Equal(identity.GetProperty("nameOnly").GetBoolean(), ToolSearch.IsToolSearchTool(nameOnly));

        var otherName = matching with { Name = "other" };
        Assert.Equal(identity.GetProperty("otherName").GetBoolean(), ToolSearch.IsToolSearchTool(otherName));
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Corpus_IsComplete()
    {
        // Lower bounds, so a truncated fixture cannot silently turn the suite green.
        Assert.Equal(28, Corpus.GetProperty("stem").GetArrayLength());
        Assert.Equal(22, Corpus.GetProperty("tokenize").GetArrayLength());
        Assert.Equal(9, Corpus.GetProperty("documents").GetArrayLength());
        Assert.Equal(13, Corpus.GetProperty("rank").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("rankCustom").GetArrayLength());
        Assert.Equal(12, Corpus.GetProperty("execute").GetArrayLength());

        // Both outcomes of `execute` must be present: successes and the validation errors
        // (empty query ×2, zero/negative/fractional limit).
        var execute = Corpus.GetProperty("execute").EnumerateArray().ToList();
        Assert.Equal(5, execute.Count(vector => vector.GetProperty("error").ValueKind != JsonValueKind.Null));
        Assert.Contains(execute, vector => vector.GetProperty("details").ValueKind != JsonValueKind.Null
            && vector.GetProperty("details").GetProperty("loaded").GetArrayLength() > 1);

        // At least one ranker case returns nothing (empty query / stop words / no match).
        Assert.Contains(Corpus.GetProperty("rank").EnumerateArray(),
            vector => vector.GetProperty("expected").GetArrayLength() == 0);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class FakeTools(IReadOnlyList<ToolInfo> all, List<string> active) : IToolSearchTools
    {
        public List<string> Active { get; } = active;

        public IReadOnlyList<string> GetActiveTools() => Active.ToList();

        public IReadOnlyList<ToolInfo> GetAllTools() => all;

        public void SetActiveTools(IReadOnlyList<string> toolNames)
        {
            Active.Clear();
            Active.AddRange(toolNames);
        }
    }

    private static string MessageOf(string error) => error[(error.IndexOf(": ", StringComparison.Ordinal) + 2)..];

    private static JsonElement FindByProperty(JsonElement array, string property, string value) =>
        array.EnumerateArray().First(vector => vector.GetProperty(property).GetString() == value);

    private static ToolInfo ReadTool(JsonElement element)
    {
        var exposure = element.TryGetProperty("exposure", out var exposureElement)
            ? exposureElement.GetString()!
            : ToolExposure.Direct;
        return new ToolInfo
        {
            Name = element.GetProperty("name").GetString()!,
            Description = element.GetProperty("description").GetString()!,
            Parameters = new ToolSchema((Dictionary<string, object?>)ToPlain(element.GetProperty("parameters"))!),
            Exposure = exposure,
            Namespace = ReadNamespace(element.TryGetProperty("namespace", out var ns) ? ns : default),
            SourceInfo = SourceInfos.CreateSynthetic("builtin:test", "builtin"),
        };
    }

    private static ToolNamespace? ReadNamespace(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ToolNamespace
        {
            Name = element.GetProperty("name").GetString()!,
            Description = element.TryGetProperty("description", out var description) ? description.GetString() : null,
            Instructions = element.TryGetProperty("instructions", out var instructions) ? instructions.GetString() : null,
        };
    }

    private static List<ToolSearchDocument> ReadDocuments(JsonElement array) =>
        array.EnumerateArray()
            .Select(document => new ToolSearchDocument(
                document.GetProperty("name").GetString()!,
                document.GetProperty("text").GetString()!))
            .ToList();

    private static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ToPlain(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble(),
        _ => null,
    };
}
