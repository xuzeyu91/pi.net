using System.Text.Json;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

// `Pi.Ai.Models.ThinkingLevels` (clamping) and `Pi.CodingAgent.Core.ThinkingLevels` (text conversion)
// would otherwise both be in scope.
using ThinkingLevels = Pi.CodingAgent.Core.ThinkingLevels;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the differential corpus captured from <c>core/model-resolver.ts</c>. Regenerate with
/// <c>FORCE_COLOR=1 node tools/gen-coding-agent-model-resolver-corpus.mjs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The generator stages the <em>real</em> TypeScript source and rewrites only its import header, so these
/// vectors are the reference implementation's own output. Every entry point is covered:
/// <c>defaultModelPerProvider</c>, <c>findExactModelReferenceMatch</c>, <c>parseModelPattern</c> (both the
/// scope and the strict CLI mode), <c>resolveModelScopeFromModels</c>, <c>resolveCliModel</c>,
/// <c>findInitialModel</c>, <c>restoreModelFromSession</c>, and <c>resolveModelScope</c>.
/// </para>
/// <para>
/// The runtime-driven vectors go through <see cref="FixtureRuntime"/>, a duck object mirroring the one the
/// generator passes to the TS functions. That is exactly what <see cref="IModelResolverRuntime"/> exists for
/// (difference C81): a real <see cref="ModelRuntime"/> always also carries the 42 builtin providers, whose
/// catalog is neither stable across releases nor reproducible in the TS harness.
/// </para>
/// <para>
/// No vector depends on the order of models <em>across</em> providers: <c>ModelRuntime.ProviderIds()</c>
/// returns a <c>HashSet</c>, and .NET randomizes string hashing per process, so such vectors would be flaky.
/// The "first available model" fallback is covered by <see cref="ModelResolverTests"/> instead.
/// </para>
/// </remarks>
public class ModelResolverCorpusTests
{
    /// <summary>Keeps a runaway mismatch from burying the signal in the test log.</summary>
    private const int MaxReportedFailures = 25;

    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "model-resolver-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonElement Section(string name) => Corpus.GetProperty(name);

    // ------------------------------------------------------------------ fixtures

    /// <summary>Builds the model list a vector's <c>set</c> names.</summary>
    private static List<ModelSpec> ModelSet(string name)
        => [.. Section("modelSets").GetProperty(name).EnumerateArray().Select(ReadModel)];

    private static ModelSpec ReadModel(JsonElement element)
    {
        var cost = new ModelCostRates(0, 0, 0, 0);
        var input = element.GetProperty("input").EnumerateArray().Select(v => v.GetString()!).ToList();
        var type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString()! : "chat";
        return new ModelSpec
        {
            Id = element.GetProperty("id").GetString()!,
            Name = element.GetProperty("name").GetString()!,
            Api = element.GetProperty("api").GetString()!,
            Provider = element.GetProperty("provider").GetString()!,
            BaseUrl = element.GetProperty("baseUrl").GetString()!,
            Input = input,
            Cost = cost,
            Type = type switch
            {
                "image" => ModelType.Image,
                "classifier" => ModelType.Classifier,
                _ => ModelType.Chat,
            },
            Reasoning = element.GetProperty("reasoning").GetBoolean(),
            ContextWindow = element.GetProperty("contextWindow").GetInt64(),
            MaxTokens = element.GetProperty("maxTokens").GetInt64(),
        };
    }

    private static HashSet<string> AuthSet(string name)
        => [.. Section("authSets").GetProperty(name).EnumerateArray().Select(v => v.GetString()!)];

    /// <summary>The duck runtime the generator handed to the TS functions.</summary>
    private sealed class FixtureRuntime(List<ModelSpec> models, HashSet<string> authed) : IModelResolverRuntime
    {
        public IReadOnlyList<ModelSpec> GetModels() => models;

        public ModelSpec? GetModel(string providerId, string modelId)
            => models.FirstOrDefault(m => m.Provider == providerId && m.Id == modelId);

        public bool HasConfiguredAuth(string providerId) => authed.Contains(providerId);

        public IReadOnlyList<ModelSpec> GetAvailableSnapshot()
            => [.. models.Where(m => authed.Contains(m.Provider))];

        public Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(string? providerId, CancellationToken signal = default)
            => Task.FromResult(GetAvailableSnapshot());
    }

    private static FixtureRuntime RuntimeFor(string set, string authed)
        => new(ModelSet(set), AuthSet(authed));

    // ------------------------------------------------------------------ shape helpers

    /// <summary>
    /// Renders a resolved model the same way the generator's <c>modelDetail</c> does. Only the fields a
    /// spread could plausibly drop are compared — the resolver's <c>with</c> expressions have to carry the
    /// rest of the base model through untouched.
    /// </summary>
    private static string Describe(ModelSpec? model)
        => model is null
            ? "<null>"
            : $"{model.Provider}/{model.Id} name={model.Name} type={TypeText(model.Type)}"
                + $" reasoning={model.Reasoning.ToString().ToLowerInvariant()}"
                + $" input={string.Join("+", model.Input)} ctx={model.ContextWindow} max={model.MaxTokens}";

    /// <summary>The wire spelling of a model type; the enum names are C#-only.</summary>
    private static string TypeText(ModelType type) => type switch
    {
        ModelType.Image => "image",
        ModelType.Classifier => "classifier",
        _ => "chat",
    };

    private static string Describe(JsonElement element)
        => element.ValueKind == JsonValueKind.Null
            ? "<null>"
            : $"{element.GetProperty("ref").GetString()} name={element.GetProperty("name").GetString()}"
                + $" type={element.GetProperty("type").GetString()}"
                + $" reasoning={element.GetProperty("reasoning").GetBoolean().ToString().ToLowerInvariant()}"
                + $" input={element.GetProperty("input").GetString()}"
                + $" ctx={element.GetProperty("contextWindow").GetInt64()}"
                + $" max={element.GetProperty("maxTokens").GetInt64()}";

    private static ThinkingLevel? ReadLevel(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : ThinkingLevels.Parse(element.GetString()!);

    private static string LevelText(ThinkingLevel? level)
        => level is { } value ? ThinkingLevels.ToText(value) : "<null>";

    private static string ReadLevelText(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? "<null>" : element.GetString()!;

    private static string ReadText(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? "<null>" : element.GetString()!;

    private static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

    private static string Join(JsonElement array) =>
        string.Join("\n", array.EnumerateArray().Select(v => v.GetString()));

    // ------------------------------------------------------------------ vectors

    [Fact]
    public void CorpusIsFromTheExpectedSource()
    {
        Assert.Equal("packages/coding-agent/src/core/model-resolver.ts", Corpus.GetProperty("source").GetString());
        Assert.Equal("10.2.6", Corpus.GetProperty("minimatchVersion").GetString());
    }

    /// <summary>
    /// The table is walked in order by both <c>findInitialModel</c> and <c>restoreModelFromSession</c>, so
    /// membership and order are both part of the contract.
    /// </summary>
    [Fact]
    public void DefaultModelPerProvider_MatchesReference()
    {
        var expected = Section("defaultModelPerProvider").EnumerateArray()
            .Select(entry => (Provider: entry[0].GetString()!, ModelId: entry[1].GetString()!))
            .ToList();
        var actual = ModelResolver.DefaultModelPerProvider
            .Select(pair => (Provider: pair.Key, ModelId: pair.Value))
            .ToList();

        Assert.Equal(expected, actual);
        foreach (var (provider, modelId) in expected)
        {
            Assert.Equal(modelId, ModelResolver.GetDefaultModelId(provider));
            Assert.True(ModelResolver.HasDefaultModelProvider(provider));
        }

        Assert.False(ModelResolver.HasDefaultModelProvider("not-a-provider"));
        Assert.Null(ModelResolver.GetDefaultModelId("not-a-provider"));
    }

    [Fact]
    public void FindExactModelReferenceMatch_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("exactMatches").EnumerateArray())
        {
            var models = ModelSet(vector.GetProperty("set").GetString()!);
            var reference = vector.GetProperty("reference").GetString()!;
            var expected = vector.GetProperty("index").GetInt32();
            var actual = ModelResolver.FindExactModelReferenceMatch(reference, models);
            var actualIndex = actual is null ? -1 : models.IndexOf(actual);
            Check(failures, $"findExact({Quote(reference)}, {vector.GetProperty("set").GetString()})",
                expected, actualIndex);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ParseModelPattern_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("parsePatterns").EnumerateArray())
        {
            var models = ModelSet(vector.GetProperty("set").GetString()!);
            var pattern = vector.GetProperty("pattern").GetString()!;
            var strict = vector.GetProperty("strict").GetBoolean();
            var label = $"parseModelPattern({Quote(pattern)}, {vector.GetProperty("set").GetString()}"
                + $", allowInvalidThinkingLevelFallback: {!strict})";

            var actual = ModelResolver.ParseModelPattern(pattern, models, allowInvalidThinkingLevelFallback: !strict);
            Check(failures, $"{label} model", Describe(vector.GetProperty("model")), Describe(actual.Model));
            Check(failures, $"{label} thinkingLevel", ReadLevelText(vector.GetProperty("thinkingLevel")),
                LevelText(actual.ThinkingLevel));
            Check(failures, $"{label} warning", ReadText(vector.GetProperty("warning")), actual.Warning ?? "<null>");
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ResolveModelScopeFromModels_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("scopes").EnumerateArray())
        {
            var models = ModelSet(vector.GetProperty("set").GetString()!);
            var patterns = vector.GetProperty("patterns").EnumerateArray().Select(v => v.GetString()!).ToList();
            var label = $"resolveModelScopeFromModels({Join(patterns.Select(Quote))},"
                + $" {vector.GetProperty("set").GetString()})";

            var actual = ModelResolver.ResolveModelScopeFromModels(patterns, models);
            var actualScoped = actual.ScopedModels
                .Select(sm => $"{sm.Model.Provider}/{sm.Model.Id}|{LevelText(sm.ThinkingLevel)}")
                .ToList();
            var expectedScoped = vector.GetProperty("scopedModels").EnumerateArray()
                .Select(entry => $"{entry[0].GetString()}|{ReadLevelText(entry[1])}")
                .ToList();
            Check(failures, $"{label} scopedModels", Join(expectedScoped), Join(actualScoped));

            var actualDiagnostics = actual.Diagnostics
                .Select(d => $"{d.Type}|{d.Code}|{d.Message}|{d.Pattern}")
                .ToList();
            var expectedDiagnostics = vector.GetProperty("diagnostics").EnumerateArray()
                .Select(entry => $"{entry[0].GetString()}|{entry[1].GetString()}|{entry[2].GetString()}"
                    + $"|{entry[3].GetString()}")
                .ToList();
            Check(failures, $"{label} diagnostics", Join(expectedDiagnostics), Join(actualDiagnostics));
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ResolveCliModel_MatchesReference()
    {
        var failures = new List<string>();
        var runtimes = new Dictionary<string, FixtureRuntime>(StringComparer.Ordinal);
        foreach (var vector in Section("cliModels").EnumerateArray())
        {
            var set = vector.GetProperty("set").GetString()!;
            var authed = vector.GetProperty("authed").GetString()!;
            var key = $"{set}|{authed}";
            if (!runtimes.TryGetValue(key, out var runtime)) runtimes[key] = runtime = RuntimeFor(set, authed);

            var cliModel = ReadText(vector.GetProperty("cliModel"));
            var label = $"resolveCliModel(provider: {ReadText(vector.GetProperty("cliProvider"))},"
                + $" model: {Quote(cliModel)}, thinking: {ReadText(vector.GetProperty("cliThinking"))}"
                + $", set: {set}, authed: {authed})";

            var actual = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
            {
                CliProvider = vector.GetProperty("cliProvider").ValueKind == JsonValueKind.Null
                    ? null
                    : vector.GetProperty("cliProvider").GetString(),
                CliModel = vector.GetProperty("cliModel").ValueKind == JsonValueKind.Null
                    ? null
                    : cliModel,
                CliThinking = ReadLevel(vector.GetProperty("cliThinking")),
                Runtime = runtime,
            });

            Check(failures, $"{label} model", Describe(vector.GetProperty("model")), Describe(actual.Model));
            Check(failures, $"{label} thinkingLevel", ReadLevelText(vector.GetProperty("thinkingLevel")),
                LevelText(actual.ThinkingLevel));
            Check(failures, $"{label} warning", ReadText(vector.GetProperty("warning")), actual.Warning ?? "<null>");
            Check(failures, $"{label} error", ReadText(vector.GetProperty("error")), actual.Error ?? "<null>");
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public async Task FindInitialModel_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("initialModels").EnumerateArray())
        {
            var runtime = RuntimeFor(vector.GetProperty("set").GetString()!, vector.GetProperty("authed").GetString()!);
            var scoped = ReadScopedModels(vector.GetProperty("scoped"));
            var label = $"findInitialModel(set: {vector.GetProperty("set").GetString()},"
                + $" authed: {vector.GetProperty("authed").GetString()}, scoped: {scoped.Count},"
                + $" continuing: {vector.GetProperty("isContinuing").GetBoolean()},"
                + $" cli: {ReadText(vector.GetProperty("cliProvider"))}/{ReadText(vector.GetProperty("cliModel"))})";

            var options = new InitialModelOptions
            {
                CliProvider = Nullable(vector.GetProperty("cliProvider")),
                CliModel = Nullable(vector.GetProperty("cliModel")),
                ScopedModels = scoped,
                IsContinuing = vector.GetProperty("isContinuing").GetBoolean(),
                DefaultProvider = Nullable(vector.GetProperty("defaultProvider")),
                DefaultModelId = Nullable(vector.GetProperty("defaultModelId")),
                DefaultThinkingLevel = ReadLevel(vector.GetProperty("defaultThinkingLevel")),
                ModelThinkingLevels = ReadThinkingLevels(vector.GetProperty("modelThinkingLevels")),
                Runtime = runtime,
            };

            var (stdout, stderr, result, failure) = await CaptureAsync(async () =>
            {
                var value = await ModelResolver.FindInitialModelAsync(options);
                return (Describe(value.Model), LevelText(value.ThinkingLevel), value.FallbackMessage ?? "<null>");
            });

            Check(failures, $"{label} stdout", Join(vector.GetProperty("stdout")), Join(stdout));
            Check(failures, $"{label} stderr", Join(vector.GetProperty("stderr")), Join(stderr));
            Check(failures, $"{label} failure", ReadText(vector.GetProperty("failure")), failure);
            if (vector.GetProperty("failure").ValueKind != JsonValueKind.Null) continue;

            Check(failures, $"{label} model", Describe(vector.GetProperty("model")), result.Item1);
            Check(failures, $"{label} thinkingLevel", ReadLevelText(vector.GetProperty("thinkingLevel")),
                result.Item2);
            Check(failures, $"{label} fallbackMessage", ReadText(vector.GetProperty("fallbackMessage")),
                result.Item3);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public async Task RestoreModelFromSession_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("restoredModels").EnumerateArray())
        {
            var set = vector.GetProperty("set").GetString()!;
            var authed = vector.GetProperty("authed").GetString()!;
            var runtime = RuntimeFor(set, authed);
            var savedProvider = vector.GetProperty("savedProvider").GetString()!;
            var savedModelId = vector.GetProperty("savedModelId").GetString()!;
            var currentKey = ReadText(vector.GetProperty("currentModel"));
            var current = currentKey == "<null>"
                ? null
                : ModelSet(set).FirstOrDefault(
                    m => $"{m.Provider}/{m.Id}" == currentKey);
            var label = $"restoreModelFromSession({savedProvider}/{savedModelId}, set: {set}, authed: {authed},"
                + $" current: {currentKey}, print: {vector.GetProperty("shouldPrintMessages").GetBoolean()})";

            var (stdout, stderr, result, failure) = await CaptureAsync(async () =>
            {
                var value = await ModelResolver.RestoreModelFromSessionAsync(
                    savedProvider, savedModelId, current,
                    vector.GetProperty("shouldPrintMessages").GetBoolean(), runtime);
                return (Describe(value.Model), value.FallbackMessage ?? "<null>");
            });

            Check(failures, $"{label} stdout", Join(vector.GetProperty("stdout")), Join(stdout));
            Check(failures, $"{label} stderr", Join(vector.GetProperty("stderr")), Join(stderr));
            Check(failures, $"{label} failure", ReadText(vector.GetProperty("failure")), failure);
            if (vector.GetProperty("failure").ValueKind != JsonValueKind.Null) continue;

            Check(failures, $"{label} model", Describe(vector.GetProperty("model")), result.Item1);
            Check(failures, $"{label} fallbackMessage", ReadText(vector.GetProperty("fallbackMessage")),
                result.Item2);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// <c>resolveModelScope</c> is the printing wrapper; its own contract is "same scoping, plus one yellow
    /// line per diagnostic on stderr".
    /// </summary>
    [Fact]
    public async Task ResolveModelScope_MatchesReference()
    {
        var failures = new List<string>();
        foreach (var vector in Section("scopeWarnings").EnumerateArray())
        {
            var runtime = RuntimeFor(vector.GetProperty("set").GetString()!, vector.GetProperty("authed").GetString()!);
            var patterns = vector.GetProperty("patterns").EnumerateArray().Select(v => v.GetString()!).ToList();
            var label = $"resolveModelScope({Join(patterns.Select(Quote))}, {vector.GetProperty("set").GetString()})";

            var (_, stderr, result, failure) = await CaptureAsync(async () =>
                (await ModelResolver.ResolveModelScopeAsync(patterns, runtime))
                    .Select(sm => $"{sm.Model.Provider}/{sm.Model.Id}|{LevelText(sm.ThinkingLevel)}")
                    .ToList());

            Check(failures, $"{label} stderr", Join(vector.GetProperty("stderr")), Join(stderr));
            Check(failures, $"{label} failure", ReadText(vector.GetProperty("failure")), failure);

            var expected = vector.GetProperty("scopedModels").EnumerateArray()
                .Select(entry => $"{entry[0].GetString()}|{ReadLevelText(entry[1])}")
                .ToList();
            Check(failures, $"{label} scopedModels", Join(expected), Join(result!));
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    // ------------------------------------------------------------------ reading

    private static string? Nullable(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    private static List<ScopedModel> ReadScopedModels(JsonElement array)
    {
        var models = ModelSet("all");
        return [.. array.EnumerateArray().Select(entry =>
        {
            var provider = entry[0].GetString()!;
            var id = entry[1].GetString()!;
            return new ScopedModel
            {
                Model = models.First(m => m.Provider == provider && m.Id == id),
                ThinkingLevel = ReadLevel(entry[2]),
            };
        })];
    }

    private static IReadOnlyDictionary<string, ThinkingLevel>? ReadThinkingLevels(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var levels = new Dictionary<string, ThinkingLevel>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            levels[property.Name] = ThinkingLevels.Parse(property.Value.GetString()!)!.Value;
        }

        return levels;
    }

    /// <summary>
    /// Substitutes the writers <see cref="ModelResolver"/> prints through and reports the outcome the way the
    /// generator recorded it: <c>null</c>, or the <c>exit(N)</c> the TS <c>process.exit</c> would have taken.
    /// </summary>
    private static async Task<(List<string> Stdout, List<string> Stderr, T? Result, string Failure)> CaptureAsync<T>(
        Func<Task<T>> run)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var enabled = Chalk.EnabledOverride;
        ModelResolver.OutWriterOverride = stdout;
        ModelResolver.ErrorWriterOverride = stderr;
        Chalk.EnabledOverride = true;
        try
        {
            var result = await run();
            return ([.. Lines(stdout)], [.. Lines(stderr)], result, "<null>");
        }
        catch (ModelResolutionException)
        {
            // TS takes `process.exit(1)` here; the corpus records that as `exit(1)`. The message itself is
            // asserted through the stderr line above.
            return ([.. Lines(stdout)], [.. Lines(stderr)], default, "exit(1)");
        }
        finally
        {
            ModelResolver.OutWriterOverride = null;
            ModelResolver.ErrorWriterOverride = null;
            Chalk.EnabledOverride = enabled;
        }
    }

    /// <summary>
    /// Splits captured output into lines. The port prints through <see cref="TextWriter.WriteLine()"/>, which
    /// uses <see cref="Environment.NewLine"/> (CRLF on Windows) where TS's <c>console.log</c> always writes
    /// LF, so the carriage returns are normalised away (the established TextWriter convention, C3).
    /// </summary>
    private static IEnumerable<string> Lines(StringWriter writer)
    {
        var text = writer.ToString();
        if (text.Length == 0) return [];
        return text.TrimEnd('\n').Split('\n').Select(line => line.TrimEnd('\r'));
    }

    // ------------------------------------------------------------------ assertions

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

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Mismatches(List<string> failures)
        => $"{failures.Count} mismatch(es):\n" + string.Join("\n", failures);
}
