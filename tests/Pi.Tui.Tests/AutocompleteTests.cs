using System.Text.Json;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>autocomplete.ts</c> port.
/// </summary>
/// <remarks>
/// <c>autocomplete-corpus.json</c> holds vectors captured by running the original TypeScript
/// implementation (the reference) over:
/// <list type="bullet">
/// <item>the upstream <c>packages/tui/test/autocomplete*.test.ts</c> scenarios,</item>
/// <item>a systematic sweep of prefixes, wrapper characters, CJK separators, quoted paths and
/// cursor positions,</item>
/// <item>the module-private pure helpers (re-exported from a patched copy),</item>
/// <item><c>node:path</c> itself, so the <see cref="NodePath"/> transliteration is pinned,</item>
/// <item>the code points matched by <c>autocompleteSeparatorRegex</c> / <c>cjkPunctuationRegex</c>,</item>
/// <item><c>String.prototype.localeCompare</c> over the labels the provider produces, and</item>
/// <item>the <c>fd</c> fuzzy-search branch, driven through a <c>child_process</c> shim that feeds
/// canned <c>fd</c> output (so it is covered without <c>fd</c> installed).</item>
/// </list>
/// A null <c>expected</c> means the reference returned <c>null</c> / <c>undefined</c>.
/// Regenerate with the harness described in <c>docs/tui-porting-status.md</c>.
/// </remarks>
public class AutocompleteCorpusTests
{
    // ------------------------------------------------------------------
    // Corpus models
    // ------------------------------------------------------------------

    private sealed record PathCase(string Fn, string? Input, List<string>? Parts, string Expected);

    private sealed record CompletionValueOptionsSpec(bool IsDirectory, bool IsAtPrefix, bool IsQuotedPrefix);

    private sealed record HelperCase(
        string Fn,
        string? Input,
        int? Index,
        string? Path,
        CompletionValueOptionsSpec? Options,
        string ExpectedKind,
        JsonElement Expected);

    private sealed record ScoreCase(string Path, string Query, bool IsDirectory, int Expected);

    private sealed record CommandSpec(
        string Kind,
        string? Name,
        string? Value,
        string? Label,
        string? Description,
        string? ArgumentHint,
        string? ArgMode);

    private sealed record ItemSpec(string Value, string Label, string? Description);

    private sealed record SuggestionsSpec(string Prefix, List<ItemSpec> Items);

    private sealed record AppliedSpec(List<string> Lines, int CursorLine, int CursorCol);

    private sealed record ApiCase(
        List<string> Lines,
        int CursorLine,
        int CursorCol,
        bool Force,
        SuggestionsSpec? Expected,
        AppliedSpec? Applied);

    private sealed record ApiScenario(
        string Id,
        List<string> Dirs,
        Dictionary<string, string> Files,
        List<ApiCase> Cases);

    private sealed record SlashCase(
        string CommandSet,
        List<string> Lines,
        int CursorLine,
        int CursorCol,
        bool Force,
        SuggestionsSpec? Expected,
        AppliedSpec? Applied);

    private sealed record TriggerCase(List<string> Lines, int CursorLine, int CursorCol, bool Expected);

    private sealed record FdResponse(int? ExitCode, string? Stdout, bool? Error);

    private sealed record FdCase(
        string Query,
        bool Quoted,
        bool Aborted,
        List<FdResponse> Responses,
        SuggestionsSpec? Expected,
        AppliedSpec? Applied);

    private sealed record FdScenario(string Id, List<string> RootDirs, List<string> BaseDirs, List<FdCase> Cases);

    private sealed record CjkRanges(List<List<int>> Punct, List<List<int>> Separator);

    private sealed record CollateCase(string A, string B, int Sign);

    private sealed record Corpus(
        List<PathCase> Paths,
        List<HelperCase> Helpers,
        List<ScoreCase> Scores,
        Dictionary<string, List<CommandSpec>> Commands,
        List<ApiScenario> Api,
        List<SlashCase> Slash,
        List<TriggerCase> Trigger,
        List<FdScenario> Fd,
        CjkRanges Cjk,
        List<CollateCase> Collate);

    private static Corpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "autocomplete-corpus.json");
        using var stream = File.OpenRead(path);
        var corpus = JsonSerializer.Deserialize<Corpus>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(corpus);
        return corpus;
    }

    private static readonly Lazy<Corpus> Data = new(LoadCorpus);

    /// <summary>
    /// Recreates, once per test run, the fixture trees the reference vectors were captured against.
    /// The corpus only stores relative paths, so the vectors are location independent.
    /// </summary>
    private sealed class Fixtures
    {
        public Fixtures(Corpus corpus)
        {
            Root = Path.Combine(Path.GetTempPath(), "pi-autocomplete-corpus-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            // Slash-command and trigger vectors were captured with an empty base directory.
            Directory.CreateDirectory(Path.Combine(Root, "empty"));

            foreach (var scenario in corpus.Api)
            {
                var basePath = Path.Combine(Root, "api", scenario.Id);
                Directory.CreateDirectory(basePath);
                Materialize(basePath, scenario.Dirs, scenario.Files);
                ApiBasePaths[scenario.Id] = basePath;
            }

            foreach (var scenario in corpus.Fd)
            {
                var root = Path.Combine(Root, "fd", scenario.Id);
                Directory.CreateDirectory(root);
                foreach (var dir in scenario.RootDirs)
                {
                    Directory.CreateDirectory(Path.Combine(root, dir));
                }

                var basePath = Path.Combine(root, "cwd");
                Directory.CreateDirectory(basePath);
                foreach (var dir in scenario.BaseDirs)
                {
                    Directory.CreateDirectory(Path.Combine(basePath, dir));
                }

                FdBasePaths[scenario.Id] = basePath;
            }
        }

        public string Root { get; }

        public Dictionary<string, string> ApiBasePaths { get; } = [];

        public Dictionary<string, string> FdBasePaths { get; } = [];

        private static void Materialize(string basePath, List<string> dirs, Dictionary<string, string> files)
        {
            foreach (var dir in dirs)
            {
                Directory.CreateDirectory(Path.Combine(basePath, dir.Replace('/', Path.DirectorySeparatorChar)));
            }

            foreach (var (relative, contents) in files)
            {
                var full = Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, contents);
            }
        }
    }

    private static readonly Lazy<Fixtures> FixtureData = new(() => new Fixtures(Data.Value));

    // ------------------------------------------------------------------
    // Theory data
    // ------------------------------------------------------------------

    public static TheoryData<int> PathIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Paths.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<string, string, int> HelperIndexes()
    {
        var data = new TheoryData<string, string, int>();
        for (var i = 0; i < Data.Value.Helpers.Count; i++)
        {
            var entry = Data.Value.Helpers[i];
            var label = entry.Input ?? entry.Path ?? "";
            data.Add(entry.Fn, label, i);
        }

        return data;
    }

    public static TheoryData<string, string, bool, int> ScoreIndexes()
    {
        var data = new TheoryData<string, string, bool, int>();
        for (var i = 0; i < Data.Value.Scores.Count; i++)
        {
            var entry = Data.Value.Scores[i];
            data.Add(entry.Path, entry.Query, entry.IsDirectory, i);
        }

        return data;
    }

    public static TheoryData<string, int> ApiIndexes()
    {
        var data = new TheoryData<string, int>();
        foreach (var scenario in Data.Value.Api)
        {
            for (var i = 0; i < scenario.Cases.Count; i++)
            {
                data.Add(scenario.Id, i);
            }
        }

        return data;
    }

    public static TheoryData<string, int> SlashIndexes()
    {
        var data = new TheoryData<string, int>();
        for (var i = 0; i < Data.Value.Slash.Count; i++)
        {
            data.Add(Data.Value.Slash[i].CommandSet, i);
        }

        return data;
    }

    public static TheoryData<int> TriggerIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Trigger.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<string, int> FdIndexes()
    {
        var data = new TheoryData<string, int>();
        foreach (var scenario in Data.Value.Fd)
        {
            for (var i = 0; i < scenario.Cases.Count; i++)
            {
                data.Add(scenario.Id, i);
            }
        }

        return data;
    }

    public static TheoryData<string, string, int> CollateIndexes()
    {
        var data = new TheoryData<string, string, int>();
        for (var i = 0; i < Data.Value.Collate.Count; i++)
        {
            var entry = Data.Value.Collate[i];
            data.Add(entry.A, entry.B, i);
        }

        return data;
    }

    // ------------------------------------------------------------------
    // node:path transliteration
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(PathIndexes))]
    public void NodePathMatchesNodeItself(int index)
    {
        var testCase = Data.Value.Paths[index];

        var actual = testCase.Fn switch
        {
            "dirname" => NodePath.Dirname(testCase.Input!),
            "basename" => NodePath.Basename(testCase.Input!),
            _ => NodePath.Join(testCase.Parts!.ToArray()),
        };

        Assert.Equal(testCase.Expected, actual);
    }

    // ------------------------------------------------------------------
    // Module-private pure helpers
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(HelperIndexes))]
    public void HelperMatchesTheTypeScriptReference(string fn, string input, int index)
    {
        var testCase = Data.Value.Helpers[index];
        Assert.Equal(testCase.Fn, fn);
        Assert.Equal(testCase.Input ?? testCase.Path ?? "", input);

        switch (testCase.Fn)
        {
            case "toDisplayPath":
                Assert.Equal(testCase.Expected.GetString(), CombinedAutocompleteProvider.ToDisplayPath(testCase.Input!));
                break;
            case "escapeRegex":
                Assert.Equal(testCase.Expected.GetString(), CombinedAutocompleteProvider.EscapeRegex(testCase.Input!));
                break;
            case "buildFdPathQuery":
                Assert.Equal(testCase.Expected.GetString(), CombinedAutocompleteProvider.BuildFdPathQuery(testCase.Input!));
                break;
            case "findLastDelimiter":
                Assert.Equal(testCase.Expected.GetInt32(), CombinedAutocompleteProvider.FindLastDelimiter(testCase.Input!));
                break;
            case "stripLeadingWrappers":
                Assert.Equal(testCase.Expected.GetString(), CombinedAutocompleteProvider.StripLeadingWrappers(testCase.Input!));
                break;
            case "findUnclosedQuoteStart":
                AssertIntOrNull(testCase, CombinedAutocompleteProvider.FindUnclosedQuoteStart(testCase.Input!));
                break;
            case "extractQuotedPrefix":
                AssertStringOrNull(testCase, CombinedAutocompleteProvider.ExtractQuotedPrefix(testCase.Input!));
                break;
            case "isTokenStart":
                Assert.Equal(testCase.Expected.GetBoolean(), CombinedAutocompleteProvider.IsTokenStart(testCase.Input!, testCase.Index!.Value));
                break;
            case "parsePathPrefix":
            {
                var (rawPrefix, isAtPrefix, isQuotedPrefix) = CombinedAutocompleteProvider.ParsePathPrefix(testCase.Input!);
                Assert.Equal(testCase.Expected.GetProperty("rawPrefix").GetString(), rawPrefix);
                Assert.Equal(testCase.Expected.GetProperty("isAtPrefix").GetBoolean(), isAtPrefix);
                Assert.Equal(testCase.Expected.GetProperty("isQuotedPrefix").GetBoolean(), isQuotedPrefix);
                break;
            }

            case "buildCompletionValue":
            {
                var options = testCase.Options!;
                var actual = CombinedAutocompleteProvider.BuildCompletionValue(testCase.Path!, options.IsAtPrefix, options.IsQuotedPrefix);
                Assert.Equal(testCase.Expected.GetString(), actual);
                break;
            }

            default:
                Assert.Fail($"unhandled helper '{testCase.Fn}'");
                break;
        }
    }

    private static void AssertIntOrNull(HelperCase testCase, int? actual)
    {
        if (testCase.ExpectedKind == "null")
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(testCase.Expected.GetInt32(), actual);
        }
    }

    private static void AssertStringOrNull(HelperCase testCase, string? actual)
    {
        if (testCase.ExpectedKind == "null")
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(testCase.Expected.GetString(), actual);
        }
    }

    [Theory]
    [MemberData(nameof(ScoreIndexes))]
    public void ScoreEntryMatchesTheTypeScriptReference(string path, string query, bool isDirectory, int index)
    {
        Assert.Equal(Data.Value.Scores[index].Expected, CombinedAutocompleteProvider.ScoreEntry(path, query, isDirectory));
    }

    // ------------------------------------------------------------------
    // Character classification and collation
    // ------------------------------------------------------------------

    [Fact]
    public void SeparatorAndPunctuationTablesMatchTheTypeScriptRegexes()
    {
        Assert.Equal(
            Data.Value.Cjk.Separator.Select(range => (range[0], range[1])).ToList(),
            AutocompleteData.SeparatorRanges.ToList());
        Assert.Equal(
            Data.Value.Cjk.Punct.Select(range => (range[0], range[1])).ToList(),
            AutocompleteData.CjkPunctuationRanges.ToList());
    }

    [Fact]
    public void SeparatorClassificationMatchesAtEveryRangeBoundary()
    {
        foreach (var (start, end) in AutocompleteData.SeparatorRanges)
        {
            if (start > 0)
            {
                Assert.False(CombinedAutocompleteProvider.IsSeparator(start - 1), $"U+{start - 1:X4}");
            }

            Assert.True(CombinedAutocompleteProvider.IsSeparator(start), $"U+{start:X4}");
            Assert.True(CombinedAutocompleteProvider.IsSeparator(end), $"U+{end:X4}");

            if (end < 0x10ffff)
            {
                Assert.False(CombinedAutocompleteProvider.IsSeparator(end + 1), $"U+{end + 1:X4}");
            }
        }
    }

    [Fact]
    public void CjkLettersAndAsciiAreNotSeparators()
    {
        // The point of the CJK punctuation rule: CJK *letters* stay part of words and paths.
        foreach (var rune in "查看文档说明あいうアイウカタカナ한글ㄅ々Ａａ１𠮷abcXYZ0123".EnumerateRunes())
        {
            Assert.False(CombinedAutocompleteProvider.IsSeparator(rune.Value), $"U+{rune.Value:X4}");
        }
    }

    [Theory]
    [MemberData(nameof(CollateIndexes))]
    public void LocaleCompareMatchesNode(string a, string b, int index)
    {
        var expected = Data.Value.Collate[index].Sign;
        var actual = Math.Sign(CombinedAutocompleteProvider.Collate(a, b));
        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------
    // Slash commands
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SlashIndexes))]
    public async Task SlashCommandSuggestionsMatchTheTypeScriptReference(string commandSet, int index)
    {
        var testCase = Data.Value.Slash[index];
        Assert.Equal(testCase.CommandSet, commandSet);
        var provider = new CombinedAutocompleteProvider(
            Path.Combine(FixtureData.Value.Root, "empty"),
            BuildCommands(Data.Value.Commands[testCase.CommandSet]));

        var lines = testCase.Lines.ToArray();
        var result = await provider.GetSuggestionsAsync(
            lines,
            testCase.CursorLine,
            testCase.CursorCol,
            new AutocompleteRequest(CancellationToken.None, testCase.Force));

        AssertSuggestions(testCase.Expected, result);

        if (testCase.Applied is not null)
        {
            Assert.NotNull(result);
            var applied = provider.ApplyCompletion(lines, testCase.CursorLine, testCase.CursorCol, result!.Items[0], result.Prefix);
            AssertApplied(testCase.Applied, applied);
        }
    }

    // ------------------------------------------------------------------
    // Path completion (readdir based)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ApiIndexes))]
    public async Task PathSuggestionsMatchTheTypeScriptReference(string scenarioId, int index)
    {
        var scenario = Data.Value.Api.Single(entry => entry.Id == scenarioId);
        var testCase = scenario.Cases[index];
        var provider = new CombinedAutocompleteProvider(FixtureData.Value.ApiBasePaths[scenarioId]);

        var lines = testCase.Lines.ToArray();
        var result = await provider.GetSuggestionsAsync(
            lines,
            testCase.CursorLine,
            testCase.CursorCol,
            new AutocompleteRequest(CancellationToken.None, testCase.Force));

        AssertSuggestions(testCase.Expected, result);

        if (testCase.Applied is not null)
        {
            Assert.NotNull(result);
            var applied = provider.ApplyCompletion(lines, testCase.CursorLine, testCase.CursorCol, result!.Items[0], result.Prefix);
            AssertApplied(testCase.Applied, applied);
        }
    }

    // ------------------------------------------------------------------
    // Fuzzy search through fd
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(FdIndexes))]
    public async Task FdSuggestionsMatchTheTypeScriptReference(string scenarioId, int index)
    {
        var scenario = Data.Value.Fd.Single(entry => entry.Id == scenarioId);
        var testCase = scenario.Cases[index];
        var provider = new CombinedAutocompleteProvider(FixtureData.Value.FdBasePaths[scenarioId], null, "fd");

        var queue = new Queue<FdResponse>(testCase.Responses);
        CombinedAutocompleteProvider.FdProcessOverride = _ =>
        {
            var response = queue.Count > 0 ? queue.Dequeue() : new FdResponse(0, "", null);
            return response.Error == true ? (-1, "") : (response.ExitCode ?? 0, response.Stdout ?? "");
        };

        try
        {
            var prefix = (testCase.Quoted ? "@\"" : "@") + testCase.Query;
            using var cts = new CancellationTokenSource();
            if (testCase.Aborted)
            {
                await cts.CancelAsync();
            }

            var lines = new[] { prefix };
            var result = await provider.GetSuggestionsAsync(
                lines,
                0,
                prefix.Length,
                new AutocompleteRequest(cts.Token));

            AssertSuggestions(testCase.Expected, result);

            if (testCase.Applied is not null)
            {
                Assert.NotNull(result);
                var applied = provider.ApplyCompletion(lines, 0, prefix.Length, result!.Items[0], result.Prefix);
                AssertApplied(testCase.Applied, applied);
            }
        }
        finally
        {
            CombinedAutocompleteProvider.FdProcessOverride = null;
        }
    }

    // ------------------------------------------------------------------
    // Tab trigger
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TriggerIndexes))]
    public void ShouldTriggerFileCompletionMatchesTheTypeScriptReference(int index)
    {
        var testCase = Data.Value.Trigger[index];
        var provider = new CombinedAutocompleteProvider(FixtureData.Value.Root);
        Assert.Equal(
            testCase.Expected,
            provider.ShouldTriggerFileCompletion(testCase.Lines.ToArray(), testCase.CursorLine, testCase.CursorCol));
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsFullyCovered()
    {
        // Guards against the corpus silently shrinking (e.g. a truncated fixture).
        Assert.True(Data.Value.Paths.Count >= 700, $"paths has only {Data.Value.Paths.Count} cases");
        Assert.True(Data.Value.Helpers.Count >= 550, $"helpers has only {Data.Value.Helpers.Count} cases");
        Assert.True(Data.Value.Scores.Count >= 200, $"scores has only {Data.Value.Scores.Count} cases");
        Assert.True(Data.Value.Api.Sum(scenario => scenario.Cases.Count) >= 1900, "api corpus shrank");
        Assert.True(Data.Value.Slash.Count >= 250, $"slash has only {Data.Value.Slash.Count} cases");
        Assert.True(Data.Value.Fd.Sum(scenario => scenario.Cases.Count) >= 25, "fd corpus shrank");
        Assert.True(Data.Value.Collate.Count >= 1000, $"collate has only {Data.Value.Collate.Count} cases");
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        Assert.Contains(Data.Value.Api.SelectMany(scenario => scenario.Cases), entry => entry.Expected is null);
        Assert.Contains(Data.Value.Api.SelectMany(scenario => scenario.Cases), entry => entry.Expected is not null);
        Assert.Contains(Data.Value.Api.SelectMany(scenario => scenario.Cases), entry => entry.Applied is not null);
        Assert.Contains(Data.Value.Slash, entry => entry.Expected is null);
        Assert.Contains(Data.Value.Slash, entry => entry.Expected is not null);
        Assert.Contains(Data.Value.Fd.SelectMany(scenario => scenario.Cases), entry => entry.Expected is not null);
        Assert.Contains(Data.Value.Fd.SelectMany(scenario => scenario.Cases), entry => entry.Expected is null);
        Assert.Contains(Data.Value.Helpers, entry => entry.ExpectedKind == "null");
        Assert.Contains(Data.Value.Scores, entry => entry.Expected == 0);
        Assert.Contains(Data.Value.Trigger, entry => !entry.Expected);
        Assert.Contains(Data.Value.Paths, entry => entry.Fn == "dirname");
        Assert.Contains(Data.Value.Paths, entry => entry.Fn == "basename");
        Assert.Contains(Data.Value.Paths, entry => entry.Fn == "join");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static List<AutocompleteCommand> BuildCommands(List<CommandSpec> specs)
    {
        var commands = new List<AutocompleteCommand>();
        foreach (var spec in specs)
        {
            if (spec.Kind == "item")
            {
                commands.Add(new ItemCommand
                {
                    Item = new AutocompleteItem
                    {
                        Value = spec.Value!,
                        Label = spec.Label!,
                        Description = spec.Description,
                    },
                });
                continue;
            }

            commands.Add(new SlashCommand
            {
                Name = spec.Name!,
                Description = spec.Description,
                ArgumentHint = spec.ArgumentHint,
                GetArgumentCompletions = spec.ArgMode switch
                {
                    "pair" => prefix => Task.FromResult<IReadOnlyList<AutocompleteItem>?>(
                    [
                        new AutocompleteItem { Value = prefix + "-a", Label = prefix + "-a" },
                        new AutocompleteItem { Value = prefix + "-b", Label = prefix + "-b", Description = "b" },
                    ]),
                    "null" => _ => Task.FromResult<IReadOnlyList<AutocompleteItem>?>(null),
                    "empty" => _ => Task.FromResult<IReadOnlyList<AutocompleteItem>?>([]),
                    "async" => async prefix =>
                    {
                        await Task.Yield();
                        return (IReadOnlyList<AutocompleteItem>?)[new AutocompleteItem { Value = $"async:{prefix}", Label = $"async:{prefix}" }];
                    },
                    _ => null,
                },
            });
        }

        return commands;
    }

    private static void AssertSuggestions(SuggestionsSpec? expected, AutocompleteSuggestions? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Prefix, actual!.Prefix);
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        for (var i = 0; i < expected.Items.Count; i++)
        {
            Assert.Equal(expected.Items[i].Value, actual.Items[i].Value);
            Assert.Equal(expected.Items[i].Label, actual.Items[i].Label);
            Assert.Equal(expected.Items[i].Description, actual.Items[i].Description);
        }
    }

    private static void AssertApplied(AppliedSpec expected, CompletionApplication actual)
    {
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.CursorLine, actual.CursorLine);
        Assert.Equal(expected.CursorCol, actual.CursorCol);
    }
}

/// <summary>
/// Hand-written checks for the JS-vs-.NET string semantics the port depends on (deviation T15).
/// </summary>
public class JsStringTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  abc  ", "abc")]
    [InlineData("\t\n\r\v\f abc \u00a0", "abc")]
    [InlineData("\u3000\ufeffabc\u2028", "abc")]
    // U+0085 (NEL) is whitespace in .NET but NOT in JavaScript.
    [InlineData("\u0085abc", "\u0085abc")]
    public void TrimFollowsTheJavaScriptWhitespaceSet(string input, string expected) =>
        Assert.Equal(expected, JsString.Trim(input));

    [Theory]
    [InlineData("abcdef", 2, null, "cdef")]
    [InlineData("abcdef", -2, null, "ef")]
    [InlineData("abcdef", 0, -2, "abcd")]
    [InlineData("abcdef", 2, 4, "cd")]
    [InlineData("abcdef", -100, 100, "abcdef")]
    [InlineData("abcdef", 4, 2, "")]
    [InlineData("abcdef", 99, null, "")]
    [InlineData("abcdef", -1, -3, "")]
    public void SliceFollowsTheJavaScriptClampingRules(string input, int start, int? end, string expected) =>
        Assert.Equal(expected, JsString.Slice(input, start, end));

    [Fact]
    public void CharAtReturnsTheUndefinedSentinelPastTheEnd() =>
        Assert.Equal('\0', JsString.CharAt("ab", 2));

    [Fact]
    public void CodePointLengthCountsRunesNotCodeUnits() =>
        Assert.Equal(1, JsString.CodePointLength("𠮷"));
}
