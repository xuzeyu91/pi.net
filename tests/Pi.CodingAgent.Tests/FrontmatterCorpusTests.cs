using System.Collections;
using System.Globalization;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/frontmatter.ts</c> against <c>frontmatter-corpus.json</c>.
/// </summary>
/// <remarks>
/// The corpus records the field view as a recursive shape (<c>kind</c> plus <c>text</c>, <c>items</c>
/// or <c>entries</c>) rather than as JSON text, so the comparison does not depend on both sides
/// agreeing about JSON formatting. Where the two YAML implementations genuinely disagree the corpus
/// pins the divergence — see C25 in <c>docs/coding-agent-porting-status.md</c>.
/// </remarks>
public class FrontmatterCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "frontmatter-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches:\n  " + string.Join("\n  ", failures);

    [Fact]
    public void Parse_MatchesTypeScript()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("cases").EnumerateArray())
        {
            count++;
            var label = vector.GetProperty("label").GetString()!;
            var content = vector.GetProperty("content").GetString()!;
            var threw = vector.GetProperty("threw");

            ParsedFrontmatter? parsed = null;
            string? error = null;
            try
            {
                parsed = Frontmatter.Parse(content);
            }
            catch (Exception caught) when (caught is not OperationCanceledException)
            {
                error = caught.Message;
            }

            if (threw.ValueKind != JsonValueKind.Null)
            {
                if (error is null)
                {
                    failures.Add($"{label}: expected a parse failure, got {DescribeFields(parsed!.Frontmatter)}");
                }

                continue;
            }

            if (error is not null)
            {
                failures.Add($"{label}: unexpected parse failure: {error}");
                continue;
            }

            Check(failures, $"{label} body", vector.GetProperty("body").GetString()!, parsed!.Body);

            var expectedFields = vector.GetProperty("fields").EnumerateArray().ToArray();
            Check(failures, $"{label} field count", expectedFields.Length, parsed.Frontmatter.Count);

            var actualFields = parsed.Frontmatter.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            foreach (var expected in expectedFields)
            {
                var key = expected.GetProperty("key").GetString()!;
                if (!actualFields.TryGetValue(key, out var value))
                {
                    failures.Add($"{label} field {key}: missing");
                    continue;
                }

                CompareValue(failures, $"{label} field {key}", expected, value);
            }
        }

        Assert.True(count > 0, "no frontmatter vectors were replayed");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void Strip_MatchesTypeScript()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("cases").EnumerateArray())
        {
            count++;
            var label = vector.GetProperty("label").GetString()!;
            var content = vector.GetProperty("content").GetString()!;
            var stripThrew = vector.GetProperty("stripThrew");

            string? stripped = null;
            string? error = null;
            try
            {
                stripped = Frontmatter.Strip(content);
            }
            catch (Exception caught) when (caught is not OperationCanceledException)
            {
                error = caught.Message;
            }

            if (stripThrew.ValueKind != JsonValueKind.Null)
            {
                if (error is null)
                {
                    failures.Add($"{label}: expected stripFrontmatter to fail, got {Quote(stripped!)}");
                }

                continue;
            }

            if (error is not null)
            {
                failures.Add($"{label}: unexpected stripFrontmatter failure: {error}");
                continue;
            }

            Check(failures, label, vector.GetProperty("stripped").GetString()!, stripped!);
        }

        Assert.True(count > 0, "no frontmatter vectors were replayed");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// The accessors exist to make the consumers' <c>typeof x === "string"</c> / <c>=== "boolean"</c>
    /// guards explicit. Deriving the expectation from the corpus keeps that claim honest: a field is a
    /// string here exactly when the original saw a string.
    /// </summary>
    [Fact]
    public void Accessors_MatchTypeScriptTypeGuards()
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var vector in Corpus.GetProperty("cases").EnumerateArray())
        {
            if (vector.GetProperty("threw").ValueKind != JsonValueKind.Null)
            {
                continue;
            }

            var label = vector.GetProperty("label").GetString()!;
            var parsed = Frontmatter.Parse(vector.GetProperty("content").GetString()!);

            foreach (var expected in vector.GetProperty("fields").EnumerateArray())
            {
                count++;
                var key = expected.GetProperty("key").GetString()!;
                var kind = expected.GetProperty("kind").GetString()!;

                var text = kind == "string" ? expected.GetProperty("text").GetString() : null;
                Check(failures, $"{label} GetString({key})", text, Frontmatter.GetString(parsed.Frontmatter, key));

                bool? flag = kind == "boolean" ? expected.GetProperty("text").GetString() == "true" : null;
                Check(failures, $"{label} GetBool({key})", flag, Frontmatter.GetBool(parsed.Frontmatter, key));
            }
        }

        Assert.True(count > 0, "no fields were checked");
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static void CompareValue(List<string> failures, string path, JsonElement expected, object? actual)
    {
        var kind = expected.GetProperty("kind").GetString()!;

        switch (kind)
        {
            case "null":
                if (actual is not null)
                {
                    failures.Add($"{path}: expected null, got {Describe(actual)}");
                }

                return;

            case "string":
                Check(failures, path, expected.GetProperty("text").GetString(), actual as string);
                return;

            case "boolean":
                if (actual is not bool flag)
                {
                    failures.Add($"{path}: expected a boolean, got {Describe(actual)}");
                    return;
                }

                Check(failures, path, expected.GetProperty("text").GetString(), flag ? "true" : "false");
                return;

            case "number":
                Check(failures, path, expected.GetProperty("text").GetString(), JsNumberText(actual));
                return;

            case "array":
                if (actual is not IList list)
                {
                    failures.Add($"{path}: expected a sequence, got {Describe(actual)}");
                    return;
                }

                var items = expected.GetProperty("items").EnumerateArray().ToArray();
                Check(failures, $"{path} length", items.Length, list.Count);
                for (var index = 0; index < Math.Min(items.Length, list.Count); index++)
                {
                    CompareValue(failures, $"{path}[{index}]", items[index], list[index]);
                }

                return;

            case "object":
                if (actual is not IDictionary<object, object> mapping)
                {
                    failures.Add($"{path}: expected a mapping, got {Describe(actual)}");
                    return;
                }

                var entries = expected.GetProperty("entries").EnumerateArray().ToArray();
                Check(failures, $"{path} entry count", entries.Length, mapping.Count);
                foreach (var entry in entries)
                {
                    var key = entry.GetProperty("key").GetString()!;
                    if (!mapping.TryGetValue(key, out var value))
                    {
                        failures.Add($"{path}.{key}: missing");
                        continue;
                    }

                    CompareValue(failures, $"{path}.{key}", entry.GetProperty("value"), value);
                }

                return;

            default:
                failures.Add($"{path}: unhandled corpus kind {kind}");
                return;
        }
    }

    /// <summary>JS <c>String(number)</c>, which is what the corpus recorded.</summary>
    private static string JsNumberText(object? value) => value switch
    {
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        uint number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        short number => number.ToString(CultureInfo.InvariantCulture),
        byte number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => JsDoubleText(number),
        float number => JsDoubleText(number),
        _ => value?.ToString() ?? "null",
    };

    private static string JsDoubleText(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        // `R` drops the trailing ".0" that JS `String()` also drops for integral doubles.
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"string {Quote(text)}",
        bool flag => $"boolean {flag}",
        IList list => $"sequence of {list.Count}",
        IDictionary<object, object> mapping => $"mapping of {mapping.Count}",
        _ => $"{value.GetType().Name} {value}",
    };

    private static string DescribeFields(IReadOnlyDictionary<string, object?> fields) =>
        "{" + string.Join(", ", fields.Select(entry => $"{entry.Key}={Describe(entry.Value)}")) + "}";

    private static string Quote(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void Check(List<string> failures, string label, string? expected, string? actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {Quote(expected ?? "null")}, got {Quote(actual ?? "null")}");
        }
    }

    private static void Check(List<string> failures, string label, int expected, int actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void Check(List<string> failures, string label, bool? expected, bool? actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
        }
    }
}
