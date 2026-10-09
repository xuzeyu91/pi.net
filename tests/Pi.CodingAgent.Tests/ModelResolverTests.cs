using Pi.Ai;
using Pi.Ai.Auth;
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
/// Behaviour tests for <see cref="ModelResolver"/> (port of TS <c>core/model-resolver.ts</c>) covering what
/// the differential corpus cannot express.
/// </summary>
/// <remarks>
/// The corpus in <see cref="ModelResolverCorpusTests"/> pins the algorithm against the real TypeScript
/// output. What is left is the C#-only surface: that a real <see cref="ModelRuntime"/> satisfies
/// <see cref="IModelResolverRuntime"/>, that the "first available model" fallback behaves (its corpus
/// vectors are omitted because provider order is not reproducible — see that class's remarks), that
/// <see cref="ModelResolutionException"/> carries the uncoloured message, and that
/// <see cref="Glob"/>'s rejection of an oversized pattern propagates the way TS's throw does.
/// </remarks>
public class ModelResolverTests
{
    // ------------------------------------------------------------------ fixtures

    private static ModelSpec Model(
        string id,
        string provider = "solo",
        string? name = null,
        bool reasoning = false,
        ModelType type = ModelType.Chat)
        => new()
        {
            Id = id,
            Name = name ?? id,
            Api = "anthropic-messages",
            Provider = provider,
            BaseUrl = "https://stub.invalid",
            Input = [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            Type = type,
            Reasoning = reasoning,
            ContextWindow = 128_000,
            MaxTokens = 4_096,
        };

    /// <summary>An api-key auth whose outcome the test chooses.</summary>
    private sealed class ToggleApiKeyAuth(bool configured) : IApiKeyAuth
    {
        public string Name => "test-api-key";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(configured
                ? new AuthResult { Auth = new ModelAuth { ApiKey = "test-key" }, Source = "test" }
                : null);
    }

    private sealed class TestProvider : IProvider
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public bool Configured { get; init; }

        public string Name => Id;

        public string? BaseUrl => null;

        public ProviderAuth? Auth => new() { ApiKey = new ToggleApiKeyAuth(Configured) };

        // Unfiltered on purpose: the resolver only ever sees the flat list, and a single-provider runtime
        // keeps the tests free of the cross-provider ordering that ModelRuntime does not guarantee.
        public IReadOnlyList<ModelSpec> GetModels() => Models;

        public IReadOnlyList<ModelSpec> GetAllModels() => Models;

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();
    }

    private static async Task<ModelRuntime> CreateRuntimeAsync(
        string providerId, IReadOnlyList<ModelSpec> models, bool configured = true)
    {
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
        });
        runtime.RegisterNativeProvider(new TestProvider
        {
            Id = providerId,
            Models = models,
            Configured = configured,
        });

        // The registration fires its refresh in the background; the snapshot (and therefore
        // HasConfiguredAuth) is only settled once it completes.
        if (runtime.PendingBackgroundRefresh is { } pending) await pending;
        return runtime;
    }

    /// <summary>A model list that only ever has one provider, so no result depends on provider order.</summary>
    private static List<ModelSpec> Models(params string[] ids)
        => [.. ids.Select(id => Model(id))];

    private static List<ModelSpec> ModelsOf(string provider, params string[] ids)
        => [.. ids.Select(id => Model(id, provider))];

    // ================================================================= the C81 slice

    [Fact]
    public async Task RealRuntimeSatisfiesTheResolverSlice()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));
        IModelResolverRuntime slice = runtime;

        Assert.Same(runtime, slice);
        Assert.Equal("only-model", slice.GetModel("solo", "only-model")?.Id);
        Assert.Null(slice.GetModel("solo", "gone"));
        Assert.True(slice.HasConfiguredAuth("solo"));

        // The catalog is the registered provider *plus* the 42 builtins — which is exactly why
        // IModelResolverRuntime exists: the corpus cannot reproduce a real runtime's catalog (C81).
        var catalog = slice.GetModels().Select(m => $"{m.Provider}/{m.Id}").ToList();
        Assert.Contains("solo/only-model", catalog);
        Assert.True(catalog.Count > 1, $"expected the builtin catalog to be present, got {catalog.Count}");

        foreach (var available in new[] { slice.GetAvailableSnapshot(), await slice.GetAvailableAsync(null) })
        {
            Assert.Contains("solo/only-model", available.Select(m => $"{m.Provider}/{m.Id}"));
        }
    }

    /// <summary>
    /// The interface declares the parameterless <c>GetModels()</c>; the class keeps its optional provider
    /// filter, so an explicit implementation bridges them.
    /// </summary>
    [Fact]
    public async Task RealRuntimeKeepsItsProviderFilter()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        Assert.Single(runtime.GetModels("solo"));
        Assert.Empty(runtime.GetModels("not-a-provider"));
    }

    // ================================================================= the runtime-driven entry points

    [Fact]
    public async Task ResolveCliModel_WorksAgainstARealRuntime()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model", "other-model"));

        var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
        {
            CliProvider = "solo",
            CliModel = "only",
            Runtime = runtime,
        });

        Assert.Equal("solo/only-model", $"{resolved.Model?.Provider}/{resolved.Model?.Id}");
        Assert.Null(resolved.Error);
        Assert.Null(resolved.ThinkingLevel);
    }

    [Fact]
    public async Task ResolveCliModel_ReportsAnUnknownProvider()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
        {
            CliProvider = "nope",
            CliModel = "only-model",
            Runtime = runtime,
        });

        Assert.Null(resolved.Model);
        Assert.Equal(
            "Unknown provider \"nope\". Use --list-models to see available providers/models.",
            resolved.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResolveCliModel_WithoutAModelResolvesNothing(string? cliModel)
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
        {
            CliModel = cliModel,
            Runtime = runtime,
        });

        Assert.Null(resolved.Model);
        Assert.Null(resolved.Error);
        Assert.Null(resolved.Warning);
    }

    [Fact]
    public async Task FindInitialModel_PrefersTheScopedList()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model", "other-model"));

        var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
        {
            ScopedModels = [new ScopedModel { Model = Model("other-model") }],
            Runtime = runtime,
        });

        Assert.Equal("solo/other-model", $"{initial.Model?.Provider}/{initial.Model?.Id}");
        Assert.Equal(ThinkingLevel.Medium, initial.ThinkingLevel);
    }

    [Fact]
    public async Task FindInitialModel_UsesTheSavedDefaultWhenItIsAuthenticated()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
        {
            ScopedModels = [],
            DefaultProvider = "solo",
            DefaultModelId = "only-model",
            DefaultThinkingLevel = ThinkingLevel.Low,
            Runtime = runtime,
        });

        Assert.Equal("solo/only-model", $"{initial.Model?.Provider}/{initial.Model?.Id}");
        Assert.Equal(ThinkingLevel.Low, initial.ThinkingLevel);
    }

    /// <summary>
    /// The "first available model" fallback. Its corpus vectors are omitted because a real runtime registers
    /// providers in <c>HashSet</c> order, but with a single authenticated provider there is no ambiguity.
    /// </summary>
    [Fact]
    public async Task FindInitialModel_FallsBackToTheFirstAvailableModel()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
        {
            ScopedModels = [],
            Runtime = runtime,
        });

        Assert.Equal("solo/only-model", $"{initial.Model?.Provider}/{initial.Model?.Id}");
        Assert.Equal(ThinkingLevel.Medium, initial.ThinkingLevel);
    }

    /// <summary>The same branch with nothing authenticated: the builtin providers have no credentials.</summary>
    [Fact]
    public async Task FindInitialModel_ReturnsNothingWhenNoModelIsAvailable()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"), configured: false);

        var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
        {
            ScopedModels = [],
            DefaultProvider = "solo",
            DefaultModelId = "only-model",
            Runtime = runtime,
        });

        Assert.Null(initial.Model);
        Assert.Equal(ThinkingLevel.Medium, initial.ThinkingLevel);
        Assert.Null(initial.FallbackMessage);
    }

    [Fact]
    public async Task RestoreModelFromSession_WorksAgainstARealRuntime()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));

        var restored = await ModelResolver.RestoreModelFromSessionAsync(
            "solo", "only-model", currentModel: null, shouldPrintMessages: false, runtime);

        Assert.Equal("solo/only-model", $"{restored.Model?.Provider}/{restored.Model?.Id}");
        Assert.Null(restored.FallbackMessage);
    }

    // ================================================================= the exit path

    /// <summary>
    /// TS prints <c>chalk.red(error)</c> and calls <c>process.exit(1)</c>. The port writes the same line and
    /// rejects the returned task with the uncoloured message (difference C78).
    /// </summary>
    [Fact]
    public async Task FindInitialModel_RejectsWithTheUncolouredError()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("only-model"));
        var stderr = new StringWriter();
        var enabled = Chalk.EnabledOverride;
        ModelResolver.ErrorWriterOverride = stderr;
        Chalk.EnabledOverride = true;
        try
        {
            var error = await Assert.ThrowsAsync<ModelResolutionException>(() =>
                ModelResolver.FindInitialModelAsync(new InitialModelOptions
                {
                    CliProvider = "nope",
                    CliModel = "only-model",
                    ScopedModels = [],
                    Runtime = runtime,
                }));

            Assert.Equal(
                "Unknown provider \"nope\". Use --list-models to see available providers/models.",
                error.Message);
            Assert.Equal(
                $"\u001b[31m{error.Message}\u001b[39m\n".Replace("\n", Environment.NewLine),
                stderr.ToString());
        }
        finally
        {
            ModelResolver.ErrorWriterOverride = null;
            Chalk.EnabledOverride = enabled;
        }
    }

    // ================================================================= alias heuristics

    [Fact]
    public void AnAliasBeatsItsDatedVersions()
    {
        var models = Models("x-alpha", "x-alpha-20250929", "x-alpha-20240101");

        var parsed = ModelResolver.ParseModelPattern("alpha", models);

        Assert.Equal("x-alpha", parsed.Model?.Id);
    }

    [Fact]
    public void WithoutAnAliasTheLatestDatedVersionWins()
    {
        var models = Models("y-beta-20240101", "y-beta-20250929", "y-beta-20250101");

        var parsed = ModelResolver.ParseModelPattern("beta", models);

        Assert.Equal("y-beta-20250929", parsed.Model?.Id);
    }

    [Fact]
    public void ALatestSuffixCountsAsAnAlias()
    {
        var models = Models("z-gamma-20250929", "z-gamma-latest");

        var parsed = ModelResolver.ParseModelPattern("gamma", models);

        Assert.Equal("z-gamma-latest", parsed.Model?.Id);
    }

    /// <summary>
    /// The date suffix must be exactly eight digits. TS writes <c>\d</c>, which is <c>[0-9]</c> there but
    /// matches every Unicode digit in .NET, so the port spells the class out (difference C79's neighbour).
    /// </summary>
    [Theory]
    [InlineData("w-delta-2025092", "7 digits")]
    [InlineData("w-delta-202509299", "9 digits")]
    [InlineData("w-delta-\uFF12\uFF10\uFF12\uFF15\uFF10\uFF19\uFF12\uFF19", "fullwidth digits")]
    [InlineData("w-delta-2025-0929", "separated")]
    public void OnlyAnEightDigitSuffixCountsAsADate(string candidate, string because)
    {
        var models = Models(candidate, "w-delta-20250929");

        var parsed = ModelResolver.ParseModelPattern("delta", models);

        Assert.True(parsed.Model?.Id == candidate, $"{because}: expected {candidate}, got {parsed.Model?.Id}");
    }

    // ================================================================= scoping

    /// <summary>
    /// The glob branch matches the canonical <c>provider/modelId</c> form *or* the bare id, so a pattern
    /// need not repeat the provider (the two <c>minimatch</c> calls in the TS source).
    /// </summary>
    [Fact]
    public void GlobPatternsMatchTheCanonicalOrTheBareId()
    {
        var models = ModelsOf("anthropic", "claude-sonnet-4-5");
        models.AddRange(ModelsOf("openrouter", "qwen/qwen3-coder:exacto"));

        Assert.Equal(["anthropic/claude-sonnet-4-5"],
            Keys(ModelResolver.ResolveModelScopeFromModels(["anthropic/*"], models)));
        Assert.Equal(["anthropic/claude-sonnet-4-5"],
            Keys(ModelResolver.ResolveModelScopeFromModels(["*sonnet*"], models)));
        Assert.Equal(["openrouter/qwen/qwen3-coder:exacto"],
            Keys(ModelResolver.ResolveModelScopeFromModels(["qwen3-coder:exacto"], models)));
    }

    [Fact]
    public void GlobMatchingIsCaseInsensitive()
    {
        var models = ModelsOf("anthropic", "claude-sonnet-4-5");

        Assert.Equal(["anthropic/claude-sonnet-4-5"],
            Keys(ModelResolver.ResolveModelScopeFromModels(["*/CLAUDE-*"], models)));
    }

    /// <summary>A pattern that matches nothing yields a <c>no-match</c> diagnostic instead of an entry.</summary>
    [Fact]
    public void AnUnmatchedPatternBecomesADiagnostic()
    {
        var models = Models("x-alpha");

        var result = ModelResolver.ResolveModelScopeFromModels(["nope*"], models);

        Assert.Empty(result.ScopedModels);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ModelScopeDiagnostic.Types.Warning, diagnostic.Type);
        Assert.Equal(ModelScopeDiagnostic.Codes.NoMatch, diagnostic.Code);
        Assert.Equal("No models match pattern \"nope*\"", diagnostic.Message);
        Assert.Equal("nope*", diagnostic.Pattern);
    }

    /// <summary>
    /// The glob branch takes the thinking level off the pattern's <c>:level</c> suffix before matching, and
    /// only when the suffix names a level.
    /// </summary>
    [Fact]
    public void GlobPatternsCarryTheirThinkingLevelSuffix()
    {
        var models = ModelsOf("anthropic", "claude-sonnet-4-5");

        var withLevel = ModelResolver.ResolveModelScopeFromModels(["anthropic/*:high"], models);
        Assert.Equal(ThinkingLevel.High, Assert.Single(withLevel.ScopedModels).ThinkingLevel);
        Assert.Empty(withLevel.Diagnostics);

        // `*:bogus` is not a level, so the suffix stays part of the glob and matches nothing.
        var withBogus = ModelResolver.ResolveModelScopeFromModels(["anthropic/*:bogus"], models);
        Assert.Empty(withBogus.ScopedModels);
        Assert.Single(withBogus.Diagnostics);
    }

    /// <summary>A model reached by two different patterns is added once.</summary>
    [Fact]
    public void ScopedModelsAreDeduplicatedAcrossPatterns()
    {
        var models = Models("x-alpha");

        var result = ModelResolver.ResolveModelScopeFromModels(["x-alpha", "alpha", "x-*"], models);

        Assert.Single(result.ScopedModels);
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// <c>modelsAreEqual</c> compares the type too, so two catalog entries that share an id but differ in
    /// category are both kept.
    /// </summary>
    [Fact]
    public void ModelsOfDifferentTypesAreNotEqual()
    {
        var models = new List<ModelSpec>
        {
            Model("shared-id"),
            Model("shared-id", type: ModelType.Image),
            Model("shared-id", type: ModelType.Classifier),
        };

        // A glob, not a bare id: the bare-id path goes through parseModelPattern's fuzzy match, which picks
        // a single winner before any deduplication happens.
        var result = ModelResolver.ResolveModelScopeFromModels(["shared-*"], models);

        Assert.Equal(3, result.ScopedModels.Count);
    }

    /// <summary>TS lets <c>minimatch</c>'s <c>pattern is too long</c> escape; the port throws the same way.</summary>
    [Fact]
    public void AnOversizedGlobPatternThrows()
    {
        var models = Models("x-alpha");

        var error = Assert.Throws<ArgumentException>(() =>
            ModelResolver.ResolveModelScopeFromModels([new string('*', Glob.MaxPatternLength + 1)], models));

        Assert.Contains("pattern is too long", error.Message, StringComparison.Ordinal);
    }

    // ================================================================= printing & cancellation

    [Fact]
    public async Task ResolveModelScope_PrintsOneYellowLinePerDiagnostic()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("x-alpha"));
        var stderr = new StringWriter();
        var enabled = Chalk.EnabledOverride;
        ModelResolver.ErrorWriterOverride = stderr;
        Chalk.EnabledOverride = true;
        try
        {
            var scoped = await ModelResolver.ResolveModelScopeAsync(["x-alpha", "nope*"], runtime);

            Assert.Single(scoped);
            Assert.Equal(
                "\u001b[33mWarning: No models match pattern \"nope*\"\u001b[39m" + Environment.NewLine,
                stderr.ToString());
        }
        finally
        {
            ModelResolver.ErrorWriterOverride = null;
            Chalk.EnabledOverride = enabled;
        }
    }

    [Fact]
    public async Task ResolveModelScope_PropagatesCancellation()
    {
        var runtime = await CreateRuntimeAsync("solo", Models("x-alpha"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelResolver.ResolveModelScopeAsync(["x-alpha"], runtime, source.Token));
    }

    // ================================================================= helpers

    private static List<string> Keys(ResolveModelScopeResult result)
        => [.. result.ScopedModels.Select(sm => $"{sm.Model.Provider}/{sm.Model.Id}")];
}
