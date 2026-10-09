using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/changelog.ts</c> against <c>git-corpus.json</c>.
/// </summary>
/// <remarks>
/// The corpus is produced by <c>tools/gen-coding-agent-git-corpus.mjs</c>, which drives the original
/// TypeScript in Node. The <c>parseChangelog</c> vectors carry the file <em>content</em> rather than a
/// path, because the original reads from disk; the test writes each one to a temp file first. Nothing in
/// the recorded output depends on the file name.
/// </remarks>
public class ChangelogCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "git-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches://n  " + string.Join("\n  ", failures);

    private static string Quote(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    [Fact]
    public void NormalizeChangelogLinks_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("normalizeChangelogLinks").EnumerateArray())
        {
            var markdown = vector.GetProperty("markdown").GetString()!;
            var version = vector.GetProperty("version");
            var expected = vector.GetProperty("expected").GetString()!;

            var actual = version.ValueKind == JsonValueKind.String
                ? Changelog.NormalizeChangelogLinks(markdown, version.GetString()!)
                : Changelog.NormalizeChangelogLinks(markdown, ReadEntry(version));

            if (actual != expected)
            {
                failures.Add(
                    $"normalizeChangelogLinks(\"{Quote(markdown)}\", {DescribeVersion(version)}):\n" +
                    $"      expected \"{Quote(expected)}\"\n" +
                    $"      got      \"{Quote(actual)}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static ChangelogEntry ReadEntry(JsonElement element) => new(
        element.GetProperty("major").GetInt32(),
        element.GetProperty("minor").GetInt32(),
        element.GetProperty("patch").GetInt32(),
        element.GetProperty("content").GetString()!);

    private static string DescribeVersion(JsonElement version) =>
        version.ValueKind == JsonValueKind.String
            ? $"\"{version.GetString()}\""
            : $"{{ {version.GetProperty("major").GetInt32()}.{version.GetProperty("minor").GetInt32()}.{version.GetProperty("patch").GetInt32()} }}";

    [Fact]
    public void ParseChangelog_MatchesTypeScript()
    {
        var directory = Directory.CreateTempSubdirectory("pi-changelog-");
        try
        {
            var failures = new List<string>();
            foreach (var vector in Corpus.GetProperty("parseChangelog").EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString()!;
                var content = vector.GetProperty("content").GetString()!;
                var path = Path.Combine(directory.FullName, $"{name}.md");
                File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                Compare(failures, name, vector.GetProperty("expected"), Changelog.ParseChangelog(path));
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParseChangelogMissingFile_MatchesTypeScript()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"pi-changelog-missing-{Guid.NewGuid():N}.md");
        var expected = Corpus.GetProperty("parseChangelogMissing").GetProperty("expected");

        var failures = new List<string>();
        Compare(failures, "missing", expected, Changelog.ParseChangelog(missing));
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// Pointing <c>parseChangelog</c> at a directory makes the read throw, which is the catch branch. The
    /// original reports it through <c>console.error</c>; the port writes to
    /// <see cref="Changelog.ErrorWriter"/>, so the message is captured instead of printed.
    /// </summary>
    [Fact]
    public void ParseChangelogUnreadablePath_MatchesTypeScript()
    {
        var directory = Directory.CreateTempSubdirectory("pi-changelog-unreadable-");
        var writer = new StringWriter();
        try
        {
            Changelog.ErrorWriter = writer;
            var expected = Corpus.GetProperty("parseChangelogUnreadable").GetProperty("expected");

            var failures = new List<string>();
            Compare(failures, "unreadable", expected, Changelog.ParseChangelog(directory.FullName));
            Assert.True(failures.Count == 0, Mismatches(failures));

            Assert.Contains("Warning: Could not parse changelog:", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Changelog.ErrorWriter = Console.Error;
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CompareChangelogVersions_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("compareChangelogVersions").EnumerateArray())
        {
            var left = ParseVersion(vector.GetProperty("left").GetString()!);
            var right = ParseVersion(vector.GetProperty("right").GetString()!);
            var expected = vector.GetProperty("expected").GetInt32();

            var actual = Changelog.CompareVersions(left, right);
            if (actual != expected)
            {
                failures.Add($"{vector.GetProperty("left").GetString()} vs {vector.GetProperty("right").GetString()}: expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static ChangelogEntry ParseVersion(string version)
    {
        var parts = version.Split('.');
        return new ChangelogEntry(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), string.Empty);
    }

    [Fact]
    public void GetNewChangelogEntries_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("getNewChangelogEntries").EnumerateArray())
        {
            var lastVersion = vector.GetProperty("lastVersion").GetString()!;
            var entries = vector.GetProperty("entries").EnumerateArray().Select(ReadEntry).ToArray();
            var expected = vector.GetProperty("expected").EnumerateArray().Select(item => item.GetString()!).ToArray();

            var actual = Changelog.GetNewEntries(entries, lastVersion).Select(entry => entry.Content).ToArray();
            if (!expected.SequenceEqual(actual))
            {
                failures.Add(
                    $"getNewEntries(entries, \"{Quote(lastVersion)}\"): " +
                    $"expected [{string.Join(", ", expected.Select(item => $"\"{item}\""))}], " +
                    $"got [{string.Join(", ", actual.Select(item => $"\"{item}\""))}]");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static void Compare(List<string> failures, string name, JsonElement expected, IReadOnlyList<ChangelogEntry> actual)
    {
        var expectedEntries = expected.EnumerateArray().ToArray();
        if (expectedEntries.Length != actual.Count)
        {
            failures.Add($"{name}: expected {expectedEntries.Length} entries, got {actual.Count}");
            return;
        }

        for (var index = 0; index < expectedEntries.Length; index++)
        {
            var want = ReadEntry(expectedEntries[index]);
            var got = actual[index];
            if (want != got)
            {
                failures.Add($"{name}[{index}]: expected {Describe(want)}, got {Describe(got)}");
            }
        }
    }

    private static string Describe(ChangelogEntry entry) =>
        $"{{ {entry.Major}.{entry.Minor}.{entry.Patch} \"{Quote(entry.Content)}\" }}";
}
