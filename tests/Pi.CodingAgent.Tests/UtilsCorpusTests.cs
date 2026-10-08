using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the differential corpus captured from the original TypeScript implementations in
/// <c>packages/coding-agent/src/utils</c>. Regenerate with
/// <c>node tools/gen-coding-agent-utils-corpus.mjs</c>.
/// </summary>
public class UtilsCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "utils-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Quote(string value) => value
        .Replace("\u001b", "\\e", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    /// <summary>
    /// The corpus records decoded text as UTF-16 code units, because JS produces lone surrogates for
    /// <c>&amp;#xD800;</c> and <c>System.Text.Json</c> cannot read those back as strings.
    /// </summary>
    private static string? UnitsToString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var unit in element.EnumerateArray())
        {
            builder.Append((char)unit.GetInt32());
        }

        return builder.ToString();
    }

    [Fact]
    public void StripJsonComments_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("stripJsonComments").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = Json.StripJsonComments(input);
            if (actual != expected)
            {
                failures.Add($"input {Quote(input)}: expected {Quote(expected)} got {Quote(actual)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void TextBom_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("text").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var split = vector.GetProperty("split");
            var expectedBom = split.GetProperty("bom").GetString()!;
            var expectedText = split.GetProperty("text").GetString()!;
            var expectedStrip = vector.GetProperty("strip").GetString()!;

            var actual = Text.SplitBom(input);
            if (actual.Bom != expectedBom || actual.Text != expectedText)
            {
                failures.Add(
                    $"splitBom({Quote(input)}): expected ({Quote(expectedBom)}, {Quote(expectedText)}) " +
                    $"got ({Quote(actual.Bom)}, {Quote(actual.Text)})");
            }

            var strip = Text.StripBom(input);
            if (strip != expectedStrip)
            {
                failures.Add($"stripBom({Quote(input)}): expected {Quote(expectedStrip)} got {Quote(strip)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void DecodeHtmlEntity_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("decodeHtmlEntity").EnumerateArray())
        {
            var entity = vector.GetProperty("entity").GetString()!;
            var expected = UnitsToString(vector.GetProperty("expectedUnits"));
            var actual = Html.DecodeHtmlEntity(entity);
            if (actual != expected)
            {
                failures.Add(
                    $"decodeHtmlEntity({Quote(entity)}): expected {Quote(expected ?? "<null>")} " +
                    $"got {Quote(actual ?? "<null>")}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void DecodeHtmlEntityAt_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("decodeHtmlEntityAt").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var index = vector.GetProperty("index").GetInt32();
            var expected = UnitsToString(vector.GetProperty("expectedUnits"));
            var actual = Html.DecodeHtmlEntityAt(input, index);

            if (expected is null)
            {
                if (actual is not null)
                {
                    failures.Add(
                        $"decodeHtmlEntityAt({Quote(input)}, {index}): expected <null> " +
                        $"got ({Quote(actual.Value.Text)}, {actual.Value.Length})");
                }

                continue;
            }

            var expectedLength = vector.GetProperty("expectedLength").GetInt32();
            if (actual is null)
            {
                failures.Add(
                    $"decodeHtmlEntityAt({Quote(input)}, {index}): expected ({Quote(expected)}, {expectedLength}) got <null>");
            }
            else if (actual.Value.Text != expected || actual.Value.Length != expectedLength)
            {
                failures.Add(
                    $"decodeHtmlEntityAt({Quote(input)}, {index}): expected ({Quote(expected)}, {expectedLength}) " +
                    $"got ({Quote(actual.Value.Text)}, {actual.Value.Length})");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void StripAnsi_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("stripAnsi").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = AnsiText.StripAnsi(input);
            if (actual != expected)
            {
                failures.Add($"stripAnsi({Quote(input)}): expected {Quote(expected)} got {Quote(actual)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ParseInt_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("parseInt").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var radix = vector.GetProperty("radix").GetInt32();
            var expectedText = vector.GetProperty("expected").GetString()!;
            var expected = double.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
            var actual = JsRegex.ParseInt(input, radix);

            if (actual is null)
            {
                if (!double.IsNaN(expected))
                {
                    failures.Add($"parseInt({Quote(input)}, {radix}): expected {expectedText} got NaN");
                }

                continue;
            }

            if (double.IsNaN(expected) || actual.Value != expected)
            {
                failures.Add(
                    $"parseInt({Quote(input)}, {radix}): expected {expectedText} got {actual.Value:R}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Chalk_MatchesTypeScriptReference()
    {
        var chalk = Corpus.GetProperty("chalk");

        // The vectors were captured with FORCE_COLOR=1, i.e. level 1.
        Assert.Equal(1, chalk.GetProperty("level").GetInt32());

        var failures = new List<string>();
        var seenStyles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in chalk.GetProperty("vectors").EnumerateArray())
        {
            var style = vector.GetProperty("style").GetString()!;
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            seenStyles.Add(style);

            var codes = Chalk.CodesFor(style);
            Assert.NotNull(codes);

            var actual = Chalk.ApplyWith(input, codes!.Value.Open, codes.Value.Close, enabled: true);
            if (actual != expected)
            {
                failures.Add($"{style}({Quote(input)}): expected {Quote(expected)} got {Quote(actual)}");
            }

            // With colour disabled chalk returns the input untouched.
            var plain = Chalk.ApplyWith(input, codes.Value.Open, codes.Value.Close, enabled: false);
            if (plain != input)
            {
                failures.Add($"{style}({Quote(input)}) disabled: expected {Quote(input)} got {Quote(plain)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(10, seenStyles.Count);
    }

    [Fact]
    public void Deprecation_MatchesTypeScriptReference()
    {
        var section = Corpus.GetProperty("deprecation");
        var messages = section.GetProperty("messages").EnumerateArray().Select(m => m.GetString()!).ToList();
        var expected = section.GetProperty("emitted").EnumerateArray().Select(m => m.GetString()!).ToList();

        var previousEnabled = Chalk.EnabledOverride;
        var previousWriter = Deprecation.ErrorWriterOverride;
        var buffer = new StringWriter();
        try
        {
            Chalk.EnabledOverride = true;
            Deprecation.ErrorWriterOverride = buffer;
            Deprecation.ClearDeprecationWarningsForTests();

            foreach (var message in messages)
            {
                Deprecation.WarnDeprecation(message);
            }

            var actual = buffer.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToList();

            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(Quote(expected[i]), Quote(actual[i]));
            }
        }
        finally
        {
            Deprecation.ClearDeprecationWarningsForTests();
            Deprecation.ErrorWriterOverride = previousWriter;
            Chalk.EnabledOverride = previousEnabled;
        }
    }

    [Fact]
    public void Mime_MatchesTypeScriptReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("mime").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var buffer = vector.GetProperty("bytes").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray();
            var expected = vector.GetProperty("expected").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("expected").GetString();
            var actual = Mime.DetectSupportedImageMimeType(buffer);
            if (actual != expected)
            {
                failures.Add($"{name}: expected {expected ?? "<null>"} got {actual ?? "<null>"}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void CorpusIsComplete()
    {
        Assert.Equal(23, Corpus.GetProperty("stripJsonComments").GetArrayLength());
        Assert.Equal(7, Corpus.GetProperty("text").GetArrayLength());
        Assert.Equal(48, Corpus.GetProperty("decodeHtmlEntity").GetArrayLength());
        Assert.Equal(19, Corpus.GetProperty("decodeHtmlEntityAt").GetArrayLength());
        Assert.Equal(23, Corpus.GetProperty("stripAnsi").GetArrayLength());
        Assert.Equal(110, Corpus.GetProperty("parseInt").GetArrayLength());
        Assert.Equal(4, Corpus.GetProperty("deprecation").GetProperty("messages").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("deprecation").GetProperty("emitted").GetArrayLength());
        Assert.Equal(150, Corpus.GetProperty("chalk").GetProperty("vectors").GetArrayLength());
        Assert.Equal(29, Corpus.GetProperty("mime").GetArrayLength());
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var stripJson = Corpus.GetProperty("stripJsonComments").EnumerateArray()
            .Select(v => v.GetProperty("input").GetString()!).ToList();
        Assert.Contains("{\"a\":\"// not a comment\"}", stripJson);
        Assert.Contains("{\"a\":\"has ,} inside\"}", stripJson);
        Assert.Contains("{\"a\":1,/*block*/}", stripJson);

        var html = Corpus.GetProperty("decodeHtmlEntity").EnumerateArray()
            .Select(v => v.GetProperty("entity").GetString()!).ToList();
        Assert.Contains("#x1F600", html);      // astral code point
        Assert.Contains("#x110000", html);     // just past the maximum
        Assert.Contains("#xzz", html);         // NaN
        Assert.Contains("#+65", html);         // explicit sign
        Assert.Contains("nbsp", html);         // named entity that is not in the table

        var ansi = Corpus.GetProperty("stripAnsi").EnumerateArray()
            .Select(v => v.GetProperty("input").GetString()!).ToList();
        Assert.Contains(ansi, s => s.Contains("\u001b]", StringComparison.Ordinal));  // OSC
        Assert.Contains(ansi, s => s.Contains('\u009B'));                            // 8-bit CSI
        Assert.Contains(ansi, s => s.Contains("\u001b]8;;\u0007", StringComparison.Ordinal)); // BEL terminator

        var parseIntInputs = Corpus.GetProperty("parseInt").EnumerateArray()
            .Select(v => v.GetProperty("input").GetString()!).ToList();
        Assert.Contains(" 42", parseIntInputs);
        Assert.Contains("\uFEFF42", parseIntInputs);
        Assert.Contains("0x1f", parseIntInputs);
        Assert.Contains("3.9", parseIntInputs);

        var chalkVectors = Corpus.GetProperty("chalk").GetProperty("vectors").EnumerateArray().ToList();
        Assert.Contains(chalkVectors, v => v.GetProperty("input").GetString()!.Contains('\n'));
        Assert.Contains(chalkVectors, v => v.GetProperty("input").GetString()!.Contains("\r\n", StringComparison.Ordinal));
        Assert.Contains(chalkVectors, v => v.GetProperty("input").GetString()!.Contains("\u001b[39m", StringComparison.Ordinal));
        Assert.Contains(chalkVectors, v => v.GetProperty("input").GetString()!.Length == 0);
    }
}
