using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/git.ts</c> and the ported <c>hosted-git-info</c> recognition half against
/// <c>git-corpus.json</c>.
/// </summary>
/// <remarks>
/// The corpus is produced by <c>tools/gen-coding-agent-git-corpus.mjs</c>, which drives the original
/// TypeScript in Node. The <c>hostedGitInfoFromUrl</c> section records the six fields <c>git.ts</c> reads
/// from <c>fromUrl</c>; the rest of the library is not ported, so nothing else is compared.
/// </remarks>
public class GitCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "git-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches:\n  " + string.Join("\n  ", failures);

    private static string Quote(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal);

    private static string Text(string? value) => value is null ? "null" : $"\"{Quote(value)}\"";

    [Fact]
    public void HostedGitInfoFromUrl_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("hostedGitInfoFromUrl").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("info");
            var actual = HostedGit.FromUrl(input);

            var expectedText = expected.ValueKind == JsonValueKind.Null ? "null" : DescribeExpected(expected);
            var actualText = Describe(actual);
            if (expectedText != actualText)
            {
                failures.Add(
                    $"fromUrl(\"{Quote(input)}\"):\n" +
                    $"      expected {expectedText}\n" +
                    $"      got      {actualText}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>Read the corpus object into the same shape <see cref="Describe"/> produces.</summary>
    private static string DescribeExpected(JsonElement info) =>
        "{ type: " + Text(info.GetProperty("type").GetString())
        + ", domain: " + Text(info.GetProperty("domain").GetString())
        + ", user: " + Text(info.GetProperty("user").GetString())
        + ", project: " + Text(info.GetProperty("project").GetString())
        + ", committish: " + Text(info.GetProperty("committish").GetString())
        + ", default: " + Text(info.GetProperty("default").GetString()) + " }";

    private static string Describe(HostedGitInfo? info) => info is null
        ? "null"
        : "{ type: " + Text(info.Type)
        + ", domain: " + Text(info.Domain)
        + ", user: " + Text(info.User)
        + ", project: " + Text(info.Project)
        + ", committish: " + Text(info.Committish)
        + ", default: " + Text(info.Default) + " }";

    [Fact]
    public void ParseGitUrl_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("parseGitUrl").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("source");
            var actual = Git.ParseGitUrl(input);

            var expectedText = expected.ValueKind == JsonValueKind.Null ? "null" : DescribeExpectedSource(expected);
            var actualText = Describe(actual);
            if (expectedText != actualText)
            {
                failures.Add(
                    $"parseGitUrl(\"{Quote(input)}\"):\n" +
                    $"      expected {expectedText}\n" +
                    $"      got      {actualText}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static string DescribeExpectedSource(JsonElement source) =>
        "{ type: " + Text(source.GetProperty("type").GetString())
        + ", repo: " + Text(source.GetProperty("repo").GetString())
        + ", host: " + Text(source.GetProperty("host").GetString())
        + ", path: " + Text(source.GetProperty("path").GetString())
        + ", ref: " + Text(source.TryGetProperty("ref", out var reference) ? reference.GetString() : null)
        + ", pinned: " + source.GetProperty("pinned").GetBoolean().ToString().ToLowerInvariant() + " }";

    private static string Describe(GitSource? source) => source is null
        ? "null"
        : "{ type: " + Text(source.Type)
        + ", repo: " + Text(source.Repo)
        + ", host: " + Text(source.Host)
        + ", path: " + Text(source.Path)
        + ", ref: " + Text(source.Ref)
        + ", pinned: " + source.Pinned.ToString().ToLowerInvariant() + " }";
}
