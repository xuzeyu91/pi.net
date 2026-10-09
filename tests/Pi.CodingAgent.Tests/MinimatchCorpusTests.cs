using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the differential corpus captured from <c>minimatch@10.2.6</c>. Regenerate with
/// <c>node tools/gen-coding-agent-minimatch-corpus.mjs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is the real package's own output, so it pins the whole pipeline rather than a summary of it:
/// <c>globSet</c> (brace expansion plus dedupe), <c>globParts</c> (the preprocessing rewrites), <c>set</c>
/// (the compiled path portions, including the exact regular-expression source and the reconstructed glob
/// text), the per-path <c>minimatch()</c> and <c>Minimatch.match()</c> answers, the <c>makeRe()</c>
/// answers, and <c>hasMagic()</c>.
/// </para>
/// <para>
/// Most option sets pin <c>platform</c> explicitly so the Windows-only roots (UNC and drive letters) are
/// covered on any host. The remaining sets follow the host, which is why <see cref="HostPlatform"/> is
/// asserted against the recorded generator platform.
/// </para>
/// </remarks>
public class MinimatchCorpusTests
{
    /// <summary>Keeps a runaway mismatch from burying the signal in the test log.</summary>
    private const int MaxReportedFailures = 25;

    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "minimatch-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Cases => Corpus.GetProperty("cases").EnumerateArray();

    private static string[] Paths =>
        [.. Corpus.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!)];

    /// <summary>The option-set name, which is what makes a failing pattern identifiable.</summary>
    private static string Label(JsonElement vector) => vector.GetProperty("optionSet").GetString()!;

    /// <summary>
    /// The corpus stores per-path answers as one character per path rather than a JSON boolean array, so
    /// that 5,673 cases x 46 paths do not dominate the file.
    /// </summary>
    private static string ReadBits(JsonElement vector, string name)
    {
        var value = vector.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    }

    [Fact]
    public void CorpusIsFromTheExpectedVersion()
    {
        Assert.Equal("minimatch", Corpus.GetProperty("package").GetString());
        Assert.Equal("10.2.6", Corpus.GetProperty("version").GetString());
    }

    [Fact]
    public void HostPlatform_MatchesCorpus()
    {
        Assert.Equal(Corpus.GetProperty("hostPlatform").GetString(), ProcessInfo.Platform);
        Assert.Equal(Corpus.GetProperty("sep").GetString(), Glob.Sep);
    }

    [Fact]
    public void MinimatchFunction_MatchesReference()
    {
        var paths = Paths;
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var options = ReadOptions(vector.GetProperty("options"));
            var expected = ReadBits(vector, "match");
            for (var i = 0; i < paths.Length; i++)
            {
                var actual = Glob.Match(paths[i], pattern, options);
                if (actual != (expected[i] == '1'))
                {
                    Add(failures, $"minimatch(\"{paths[i]}\", \"{pattern}\", {Label(vector)}): " +
                        $"expected {expected[i] == '1'}, got {actual}");
                }
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void MinimatchClassMatch_MatchesReference()
    {
        var paths = Paths;
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var options = ReadOptions(vector.GetProperty("options"));
            var matcher = new Minimatch(pattern, options);
            var expected = ReadBits(vector, "matchDirect");
            for (var i = 0; i < paths.Length; i++)
            {
                var actual = matcher.Match(paths[i]);
                if (actual != (expected[i] == '1'))
                {
                    Add(failures, $"Minimatch(\"{pattern}\", {Label(vector)}).match(\"{paths[i]}\"): " +
                        $"expected {expected[i] == '1'}, got {actual}");
                }
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void MakeRe_MatchesReference()
    {
        var paths = Paths;
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var label = Label(vector);

            var available = vector.GetProperty("reAvailable");
            if (available.ValueKind == JsonValueKind.String) continue; // the reference itself threw

            var expectedAvailable = available.ValueKind == JsonValueKind.True;
            var regex = new Minimatch(pattern, ReadOptions(vector.GetProperty("options"))).MakeRe();
            if ((regex is not null) != expectedAvailable)
            {
                Add(failures, $"Minimatch(\"{pattern}\", {label}).makeRe(): expected " +
                    $"{(expectedAvailable ? "a regex" : "no match"),-10} got {(regex is null ? "no match" : "a regex")}");
                continue;
            }

            if (regex is null) continue;

            var expected = ReadBits(vector, "re");
            for (var i = 0; i < paths.Length; i++)
            {
                var actual = regex.IsMatch(paths[i]);
                if (actual != (expected[i] == '1'))
                {
                    Add(failures, $"Minimatch(\"{pattern}\", {label}).makeRe().test(\"{paths[i]}\"): " +
                        $"expected {expected[i] == '1'}, got {actual}");
                }
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void HasMagic_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var expected = vector.GetProperty("hasMagic");
            if (expected.ValueKind == JsonValueKind.String) continue;

            var actual = new Minimatch(pattern, ReadOptions(vector.GetProperty("options"))).HasMagic();
            if (actual != expected.GetBoolean())
            {
                Add(failures, $"Minimatch(\"{pattern}\", {Label(vector)}).hasMagic(): " +
                    $"expected {expected.GetBoolean()}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void BraceExpand_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var expected = vector.GetProperty("braceExpand");
            if (expected.ValueKind == JsonValueKind.String) continue;

            var want = expected.EnumerateArray().Select(v => v.GetString()!).ToArray();
            var got = Glob.BraceExpand(pattern, ReadOptions(vector.GetProperty("options")));
            if (!want.SequenceEqual(got))
            {
                Add(failures, $"braceExpand(\"{pattern}\", {Label(vector)}): " +
                    $"expected [{Join(want)}], got [{Join(got)}]");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ParseState_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Cases)
        {
            var pattern = vector.GetProperty("pattern").GetString()!;
            var label = Label(vector);
            var matcher = new Minimatch(pattern, ReadOptions(vector.GetProperty("options")));

            Check(failures, $"(\"{pattern}\", {label}).pattern",
                vector.GetProperty("strippedPattern").GetString()!, matcher.Pattern);
            Check(failures, $"(\"{pattern}\", {label}).negate",
                vector.GetProperty("negate").GetBoolean(), matcher.Negate);
            Check(failures, $"(\"{pattern}\", {label}).comment",
                vector.GetProperty("comment").GetBoolean(), matcher.Comment);
            Check(failures, $"(\"{pattern}\", {label}).empty",
                vector.GetProperty("empty").GetBoolean(), matcher.Empty);
            Check(failures, $"(\"{pattern}\", {label}).nocase",
                vector.GetProperty("nocase").GetBoolean(), matcher.NoCase);
            Check(failures, $"(\"{pattern}\", {label}).isWindows",
                vector.GetProperty("isWindows").GetBoolean(), matcher.IsWindows);
            Check(failures, $"(\"{pattern}\", {label}).windowsNoMagicRoot",
                vector.GetProperty("windowsNoMagicRoot").GetBoolean(), matcher.WindowsNoMagicRoot);
            Check(failures, $"(\"{pattern}\", {label}).windowsPathsNoEscape",
                vector.GetProperty("windowsPathsNoEscape").GetBoolean(), matcher.WindowsPathsNoEscape);
            Check(failures, $"(\"{pattern}\", {label}).maxGlobstarRecursion",
                vector.GetProperty("maxGlobstarRecursion").GetInt32(), matcher.MaxGlobstarRecursion);

            var wantGlobSet = vector.GetProperty("globSet").EnumerateArray().Select(v => v.GetString()!).ToArray();
            if (!wantGlobSet.SequenceEqual(matcher.GlobSet))
            {
                Add(failures, $"(\"{pattern}\", {label}).globSet: expected [{Join(wantGlobSet)}], " +
                    $"got [{Join(matcher.GlobSet)}]");
            }

            CheckGlobParts(failures, vector, matcher, pattern, label);
            CheckSet(failures, vector, matcher, pattern, label);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void EscapeAndUnescape_MatchReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("escape").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var options = ReadOptions(vector.GetProperty("options"));
            var label = vector.GetProperty("optionSet").GetString()!;

            var wantEscape = vector.GetProperty("escape").GetString()!;
            var gotEscape = Glob.Escape(input, options);
            if (wantEscape != gotEscape)
            {
                Add(failures, $"escape(\"{input}\", {label}): expected \"{wantEscape}\", got \"{gotEscape}\"");
            }

            var wantUnescape = vector.GetProperty("unescape").GetString()!;
            var gotUnescape = Glob.Unescape(input, options);
            if (wantUnescape != gotUnescape)
            {
                Add(failures, $"unescape(\"{input}\", {label}): expected \"{wantUnescape}\", got \"{gotUnescape}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void MatchList_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("matchList").EnumerateArray())
        {
            var list = vector.GetProperty("list").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var pattern = vector.GetProperty("pattern").GetString()!;
            var options = ReadOptions(vector.GetProperty("options"));
            var want = vector.GetProperty("result").EnumerateArray().Select(v => v.GetString()!).ToArray();

            var got = Glob.MatchList(list, pattern, options);
            if (!want.SequenceEqual(got))
            {
                Add(failures, $"match([{Join(list)}], \"{pattern}\", " +
                    $"{vector.GetProperty("optionSet").GetString()}): expected [{Join(want)}], got [{Join(got)}]");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static void CheckGlobParts(
        List<string> failures, JsonElement vector, Minimatch matcher, string pattern, string label)
    {
        var want = vector.GetProperty("globParts").EnumerateArray()
            .Select(part => part.EnumerateArray().Select(v => v.GetString()!).ToArray()).ToArray();
        var got = matcher.GlobParts.Select(part => part.ToArray()).ToArray();

        if (want.Length != got.Length)
        {
            Add(failures, $"(\"{pattern}\", {label}).globParts: expected {want.Length} entries, got {got.Length}");
            return;
        }

        for (var i = 0; i < want.Length; i++)
        {
            if (!want[i].SequenceEqual(got[i]))
            {
                Add(failures, $"(\"{pattern}\", {label}).globParts[{i}]: " +
                    $"expected [{Join(want[i])}], got [{Join(got[i])}]");
            }
        }
    }

    private static void CheckSet(
        List<string> failures, JsonElement vector, Minimatch matcher, string pattern, string label)
    {
        var want = vector.GetProperty("set").EnumerateArray().ToArray();
        var got = matcher.Set;

        if (want.Length != got.Count)
        {
            Add(failures, $"(\"{pattern}\", {label}).set: expected {want.Length} patterns, got {got.Count}");
            return;
        }

        for (var i = 0; i < want.Length; i++)
        {
            var wantParts = want[i].EnumerateArray().ToArray();
            var gotParts = got[i];
            if (wantParts.Length != gotParts.Count)
            {
                Add(failures, $"(\"{pattern}\", {label}).set[{i}]: " +
                    $"expected {wantParts.Length} parts, got {gotParts.Count}");
                continue;
            }

            for (var j = 0; j < wantParts.Length; j++)
            {
                var expected = DescribePart(wantParts[j]);
                var actual = DescribePart(gotParts[j]);
                if (expected != actual)
                {
                    Add(failures, $"(\"{pattern}\", {label}).set[{i}][{j}]: expected {expected}, got {actual}");
                }
            }
        }
    }

    /// <summary>
    /// The JS <c>string | RegExp | GLOBSTAR</c> union is a discriminated record here, so render both sides
    /// as one comparable line. The regex case also checks the reconstructed glob text, which is what
    /// <c>fillNegs</c> and <c>flatten</c> rewrite.
    /// </summary>
    private static string DescribePart(JsonElement part) => part.GetProperty("t").GetString() switch
    {
        "s" => $"str({Quote(part.GetProperty("v").GetString()!)})",
        "g" => "globstar",
        _ => $"re({Quote(part.GetProperty("v").GetString()!)}, glob={Quote(part.GetProperty("glob").GetString()!)})",
    };

    private static string DescribePart(MatchPart part) => part switch
    {
        MatchLiteralPart literal => $"str({Quote(literal.Value)})",
        MatchGlobstarPart => "globstar",
        MatchRegexPart regex => $"re({Quote(regex.Src)}, glob={Quote(regex.Glob ?? "")})",
        _ => throw new InvalidOperationException("unknown part"),
    };

    private static GlobOptions ReadOptions(JsonElement options)
    {
        var result = new GlobOptions();
        foreach (var property in options.EnumerateObject())
        {
            result = property.Name switch
            {
                "nocase" => result with { NoCase = property.Value.GetBoolean() },
                "nocaseMagicOnly" => result with { NoCaseMagicOnly = property.Value.GetBoolean() },
                "noglobstar" => result with { NoGlobStar = property.Value.GetBoolean() },
                "noext" => result with { NoExt = property.Value.GetBoolean() },
                "nobrace" => result with { NoBrace = property.Value.GetBoolean() },
                "nonegate" => result with { NoNegate = property.Value.GetBoolean() },
                "nocomment" => result with { NoComment = property.Value.GetBoolean() },
                "matchBase" => result with { MatchBase = property.Value.GetBoolean() },
                "partial" => result with { Partial = property.Value.GetBoolean() },
                "flipNegate" => result with { FlipNegate = property.Value.GetBoolean() },
                "magicalBraces" => result with { MagicalBraces = property.Value.GetBoolean() },
                "preserveMultipleSlashes" => result with { PreserveMultipleSlashes = property.Value.GetBoolean() },
                "windowsPathsNoEscape" => result with { WindowsPathsNoEscape = property.Value.GetBoolean() },
                "dot" => result with { Dot = property.Value.GetBoolean() },
                "nonull" => result with { Nonull = property.Value.GetBoolean() },
                "optimizationLevel" => result with { OptimizationLevel = property.Value.GetInt32() },
                "maxGlobstarRecursion" => result with { MaxGlobstarRecursion = property.Value.GetInt32() },
                "maxExtglobRecursion" => result with { MaxExtglobRecursion = property.Value.GetInt32() },
                "braceExpandMax" => result with { BraceExpandMax = property.Value.GetInt32() },
                "platform" => result with { Platform = property.Value.GetString() },
                "allowWindowsEscape" => result with { AllowWindowsEscape = property.Value.GetBoolean() },
                _ => throw new InvalidOperationException($"unmapped option: {property.Name}"),
            };
        }

        return result;
    }

    private static void Check<T>(List<string> failures, string label, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Add(failures, $"{label}: expected {expected}, got {actual}");
        }
    }

    private static void Add(List<string> failures, string message)
    {
        if (failures.Count < MaxReportedFailures) failures.Add(message);
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values.Select(Quote));

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Mismatches(List<string> failures)
    {
        if (failures.Count == 0) return "";
        var text = new StringBuilder($"{failures.Count} mismatch(es):");
        foreach (var failure in failures) text.Append("\n  ").Append(failure);
        if (failures.Count == MaxReportedFailures) text.Append("\n  … (truncated)");
        return text.ToString();
    }
}
