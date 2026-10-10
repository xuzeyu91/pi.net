using System.Text.Json;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Replays the differential corpus for the usage / cost / estimate slice:
/// <c>calculateCost</c> (rate lookup + the Anthropic 1h cache-write premium + four-bucket total),
/// <c>calculateContextTokens</c>'s <c>totalTokens ||</c> fallback, and the two text/content estimators.
/// Regenerate with <c>node tools/gen-ai-usage-corpus.mjs &gt; tests/Pi.Ai.Tests/usage-corpus.json</c>.
/// </summary>
/// <remarks>
/// The <c>cost.tiers</c> section is intentionally absent: <see cref="ModelCostRates"/> has no
/// <c>Tiers</c> field and the catalog parser/writer drop them (see
/// <see cref="ModelOperations.CalculateCost"/>). The round-trip test at the bottom pins the JSON shape
/// that session JSONL depends on — in particular that an absent <c>cacheWrite1h</c>/<c>reasoning</c> is
/// omitted while an explicit <c>0</c> is written.
/// </remarks>
public class UsageCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    /// <summary>项目通行的 wire 约定：camelCase（各 provider 的解析端也按此读取）。</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "usage-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Section(string name) => Corpus.GetProperty(name).EnumerateArray().ToArray();

    private static ModelSpec ModelWith(JsonElement rates) => new()
    {
        Id = "corpus", Name = "Corpus", Api = "corpus", Provider = "corpus",
        BaseUrl = "https://corpus.invalid/v1", Input = ["text"],
        Cost = new ModelCostRates(
            rates.GetProperty("input").GetDouble(),
            rates.GetProperty("output").GetDouble(),
            rates.GetProperty("cacheRead").GetDouble(),
            rates.GetProperty("cacheWrite").GetDouble()),
    };

    private static Usage UsageFrom(JsonElement usage) => new(
        usage.GetProperty("input").GetInt64(),
        usage.GetProperty("output").GetInt64(),
        usage.GetProperty("cacheRead").GetInt64(),
        usage.GetProperty("cacheWrite").GetInt64())
    {
        CacheWrite1h = usage.TryGetProperty("cacheWrite1h", out var oneHour) ? oneHour.GetInt64() : null,
    };

    private static bool Differs(double expected, double actual)
        => Math.Abs(expected - actual) > 1e-12 * Math.Max(1, Math.Abs(expected));

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(11, Section("calculateCost").Length);
        Assert.Equal(6, Section("calculateContextTokens").Length);
        Assert.Equal(9, Section("estimateTextTokens").Length);
        Assert.Equal(8, Section("estimateTextAndImageContentTokens").Length);
    }

    [Fact]
    public void CalculateCost_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("calculateCost"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var priced = ModelOperations.CalculateCost(ModelWith(vector.GetProperty("rates")), UsageFrom(vector.GetProperty("usage")));
            var expected = vector.GetProperty("cost");
            foreach (var bucket in new[] { "input", "output", "cacheRead", "cacheWrite", "total" })
            {
                var want = expected.GetProperty(bucket).GetDouble();
                var got = bucket switch
                {
                    "input" => priced.Cost.Input,
                    "output" => priced.Cost.Output,
                    "cacheRead" => priced.Cost.CacheRead,
                    "cacheWrite" => priced.Cost.CacheWrite,
                    _ => priced.Cost.Total,
                };
                if (Differs(want, got)) failures.Add($"{name}.{bucket}: expected {want:R}, got {got:R}");
            }
        }

        Assert.True(failures.Count == 0, "calculateCost mismatches:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void CalculateContextTokens_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("calculateContextTokens"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var usage = UsageFrom(vector.GetProperty("usage"));
            usage = usage with { TotalTokens = vector.GetProperty("usage").GetProperty("totalTokens").GetInt64() };
            var expected = vector.GetProperty("tokens").GetInt64();
            var actual = Estimate.CalculateContextTokens(usage);
            if (expected != actual) failures.Add($"{name}: expected {expected}, got {actual}");
        }

        Assert.True(failures.Count == 0, "calculateContextTokens mismatches:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EstimateTextTokens_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("estimateTextTokens"))
        {
            var text = vector.GetProperty("text").GetString()!;
            var expected = vector.GetProperty("tokens").GetInt64();
            var actual = Estimate.EstimateTextTokens(text);
            if (expected != actual) failures.Add($"{Quote(text)}: expected {expected}, got {actual}");
        }

        Assert.True(failures.Count == 0, "estimateTextTokens mismatches:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EstimateTextAndImageContentTokens_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("estimateTextAndImageContentTokens"))
        {
            var content = vector.GetProperty("content");
            var expected = vector.GetProperty("tokens").GetInt64();
            long actual;
            string label;
            if (content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString()!;
                label = $"string {Quote(text)}";
                actual = Estimate.EstimateTextAndImageContentTokens(text);
            }
            else
            {
                var blocks = new List<ContentBlock>();
                foreach (var block in content.EnumerateArray())
                {
                    blocks.Add(block.GetProperty("type").GetString() switch
                    {
                        "text" => new TextContent(block.GetProperty("text").GetString()!),
                        _ => new ImageContent(
                            block.GetProperty("data").GetString()!,
                            block.GetProperty("mimeType").GetString()),
                    });
                }

                label = $"blocks[{blocks.Count}]";
                actual = Estimate.EstimateTextAndImageContentTokens(blocks);
            }

            if (expected != actual) failures.Add($"{label}: expected {expected}, got {actual}");
        }

        Assert.True(failures.Count == 0,
            "estimateTextAndImageContentTokens mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Session JSONL stores <see cref="Usage"/> verbatim, so the wire shape must match TS:
    /// <c>cost</c> is always a five-bucket object, and the optional counters are omitted only when
    /// absent — an explicit <c>0</c> is written (TS omits <c>undefined</c>, not <c>0</c>).
    /// </summary>
    [Fact]
    public void UsageJson_MatchesTypeScriptWireShape()
    {
        var full = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
            new Usage(10, 5, 2, 3) { CacheWrite1h = 1, Reasoning = 0, TotalTokens = 20, Cost = new UsageCost(1, 2, 3, 4, 10) },
            WireOptions));
        Assert.Equal(10, full.GetProperty("input").GetInt64());
        Assert.Equal(5, full.GetProperty("output").GetInt64());
        Assert.Equal(2, full.GetProperty("cacheRead").GetInt64());
        Assert.Equal(3, full.GetProperty("cacheWrite").GetInt64());
        Assert.Equal(1, full.GetProperty("cacheWrite1h").GetInt64());
        // reasoning 为 0 时必须写出（TS 只在 undefined 时省略）。
        Assert.Equal(0, full.GetProperty("reasoning").GetInt64());
        Assert.Equal(20, full.GetProperty("totalTokens").GetInt64());
        var cost = full.GetProperty("cost");
        Assert.Equal(new[] { "input", "output", "cacheRead", "cacheWrite", "total" }, cost.EnumerateObject().Select(p => p.Name));
        Assert.Equal(10, cost.GetProperty("total").GetDouble());

        var minimal = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new Usage(1, 2), WireOptions));
        Assert.False(minimal.TryGetProperty("cacheWrite1h", out _));
        Assert.False(minimal.TryGetProperty("reasoning", out _));
        Assert.Equal(0, minimal.GetProperty("totalTokens").GetInt64());
        // 缺省 cost 仍是完整的零桶对象，不是 null。
        Assert.Equal(0, minimal.GetProperty("cost").GetProperty("total").GetDouble());
    }

    private static string Quote(string value) => value
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);
}
