using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Behaviour tests for <see cref="ModelRegistry"/> (port of TS <c>core/model-registry.ts</c>).
/// </summary>
/// <remarks>
/// The class is a pure forwarding facade, so the assertions split in two: that each member reaches the
/// same <see cref="ModelRuntime"/> entry point (including the two convenience wrappers), and that the
/// hand-written parts — the catalog copies and <see cref="ModelRegistry.GetApiKeyAndHeadersAsync"/> — keep
/// the TS control flow. The TS suite drives the facade mostly to exercise the composer underneath; that
/// half is already pinned by <c>CoreProviderComposerTests</c> and <c>CoreModelRuntimeTests</c>.
/// </remarks>
public class CoreModelRegistryTests
{
    // ------------------------------------------------------------------ shared fakes

    private static ModelSpec Model(
        string id,
        string provider = "p",
        string api = "stub-api",
        ModelType type = ModelType.Chat,
        long maxTokens = 100)
        => new()
        {
            Id = id,
            Name = id,
            Api = api,
            Provider = provider,
            BaseUrl = "https://stub.invalid",
            Type = type,
            Input = [ModelInput.Text, ModelInput.Image],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = 1_000,
            MaxTokens = maxTokens,
        };

    private static IAssistantMessageEventStream MarkerStream(ModelSpec model, string marker)
    {
        var stream = new AssistantMessageEventStream();
        stream.End(new AssistantMessage(
            Content: [],
            StopReason: StopReason.Stop,
            Model: marker,
            Api: model.Api,
            Provider: model.Provider));
        return stream;
    }

    private static async Task<string> MarkerOf(IAssistantMessageEventStream stream)
        => (await stream.WaitForDoneAsync()).Model ?? "<none>";

    private sealed class StubApiKeyAuth(
        string? apiKey = "resolved-key",
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        string source = "stub-env") : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth
                {
                    ApiKey = credential?.Key ?? apiKey,
                    BaseUrl = baseUrl,
                    Headers = headers,
                },
                Source = source,
            });
    }

    /// <summary>Auth that reports the provider as unconfigured (TS <c>resolve()</c> returning undefined).</summary>
    private sealed class UnconfiguredApiKeyAuth : IApiKeyAuth
    {
        public string Name => "unconfigured";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(null);
    }

    private sealed class ThrowingApiKeyAuth(string message) : IApiKeyAuth
    {
        public string Name => "throwing";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class StubOAuthAuth : IOAuthAuth
    {
        public string Name => "stub-oauth";

        public bool IsSubscription => true;

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Credential.OAuth("refresh", "access", long.MaxValue));

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = "oauth-access" });
    }

    private sealed class NoopInteraction : IAuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Notify(AuthEvent @event)
        {
        }
    }

    /// <summary>Provider recording what it was dispatched, and able to serve images/classification.</summary>
    private sealed class StubProvider : IProvider, IImagesProvider, IClassifierProvider
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public ProviderAuth? AuthOverride { get; init; }

        public string StreamMarker { get; init; } = "stream";

        public string StreamSimpleMarker { get; init; } = "stream-simple";

        public string Name => Id;

        public string? BaseUrl => null;

        public ProviderAuth? Auth => AuthOverride;

        public IReadOnlyList<ModelSpec> GetModels() => [.. Models.Where(model => model.Type == ModelType.Chat)];

        public IReadOnlyList<ModelSpec> GetAllModels() => Models;

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => MarkerStream(model, StreamMarker);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => MarkerStream(model, StreamSimpleMarker);

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new AssistantImages { Api = model.Api, Provider = model.Provider, Model = "images" });

        public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new ClassifierResult { Api = model.Api, Provider = model.Provider, Model = "classify" });
    }

    /// <summary>A <c>models.json</c> file that outlives the runtime, so <c>refresh()</c> can reload it.</summary>
    private sealed class TempModelsFile : IDisposable
    {
        public TempModelsFile(string json)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"pi-registry-models-{Guid.NewGuid():N}.json");
            Write(json);
        }

        public string Path { get; }

        public void Write(string json) => File.WriteAllText(Path, json);

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover temp file is not a test failure.
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Builds an offline runtime by default: <c>PI_OFFLINE</c> is set so no test reaches the network, and
    /// the builtin remote-catalog providers only restore from their (empty, in-memory) store.
    /// </summary>
    private static Task<ModelRuntime> CreateAsync(
        ICredentialStore? credentials = null, string? modelsPath = null, IModelsStore? modelsStore = null)
        => ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials ?? new InMemoryCredentialStore(),
            ModelsPathDisabled = modelsPath is null,
            ModelsPath = modelsPath,
            ModelsStore = modelsStore ?? new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
        });

    private static async Task SettleAsync(ModelRuntime runtime)
    {
        if (runtime.PendingBackgroundRefresh is { } pending) await pending;
    }

    /// <summary>A runtime with one native provider registered, plus a registry over it.</summary>
    private static async Task<ModelRegistry> WithRegistryAsync(
        StubProvider provider, string? modelsPath = null, ICredentialStore? credentials = null)
    {
        var runtime = await CreateAsync(credentials, modelsPath);
        runtime.RegisterNativeProvider(provider);
        await SettleAsync(runtime);
        return new ModelRegistry(runtime);
    }

    private static ProviderAuth ApiKeyAuth(
        string? apiKey = "resolved-key", string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null)
        => new() { ApiKey = new StubApiKeyAuth(apiKey, baseUrl, headers) };

    private static ProviderAuth UnconfiguredAuth() => new() { ApiKey = new UnconfiguredApiKeyAuth() };

    private static ProviderAuth ThrowingAuth(string message) => new() { ApiKey = new ThrowingApiKeyAuth(message) };

    private static IReadOnlyList<ModelSpec> ForProvider(IReadOnlyList<ModelSpec> models, string provider)
        => [.. models.Where(model => model.Provider == provider)];

    private static ModelSpec RequireModel(ModelRegistry registry, string provider, string id)
        => registry.Find(provider, id) ?? throw new InvalidOperationException($"{provider}/{id} missing");

    // ================================================================= catalog

    [Fact]
    public async Task GetAllReturnsACopyOfTheRuntimeCatalog()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        var first = registry.GetAll();
        var second = registry.GetAll();

        // TS spreads the runtime array into a fresh one; the copy must not be the runtime's own list.
        Assert.NotSame(first, second);
        Assert.Equal("m", Assert.Single(ForProvider(first, "p")).Id);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public async Task GetAvailableReturnsACopyOfTheAvailableSnapshot()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });
        await SettleAsync(runtime);
        var registry = new ModelRegistry(runtime);

        var available = registry.GetAvailable();

        Assert.NotSame(runtime.GetAvailableSnapshot(), available);
        Assert.Equal("m", Assert.Single(ForProvider(available, "p")).Id);
    }

    [Fact]
    public async Task FindForwardsToTheRuntimeCatalog()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        Assert.Equal("m", registry.Find("p", "m")?.Id);
        Assert.Null(registry.Find("p", "nope"));
        Assert.Null(registry.Find("nope", "m"));
    }

    [Fact]
    public async Task FindOfTypeReachesNonChatModels()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m"), Model("i", type: ModelType.Image), Model("c", type: ModelType.Classifier)],
        });

        Assert.Equal("i", registry.FindOfType(ModelType.Image, "p", "i")?.Id);
        Assert.Equal("c", registry.FindOfType(ModelType.Classifier, "p", "c")?.Id);
        // A chat lookup must not fall back to another category.
        Assert.Null(registry.FindOfType(ModelType.Chat, "p", "i"));
    }

    [Fact]
    public async Task GetModelsOfTypeFiltersByTypeAndProvider()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m"), Model("i", type: ModelType.Image), Model("i2", type: ModelType.Image)],
        });

        Assert.Equal("i", Assert.Single(registry.GetModelsOfType(ModelType.Image, "p"), m => m.Id == "i").Id);
        Assert.Equal(2, registry.GetModelsOfType(ModelType.Image, "p").Count);
        Assert.Equal("m", Assert.Single(registry.GetModelsOfType(ModelType.Chat, "p")).Id);
        Assert.Empty(registry.GetModelsOfType(ModelType.Classifier, "p"));
        // Without a provider the search spans the whole catalog (builtins included).
        Assert.Contains(registry.GetModelsOfType(ModelType.Image), model => model.Provider == "p");
    }

    [Fact]
    public async Task GetModelOfTypeMatchesFindOfType()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("i", type: ModelType.Image)],
        });

        Assert.Equal("i", registry.GetModelOfType(ModelType.Image, "p", "i")?.Id);
        Assert.Null(registry.GetModelOfType(ModelType.Chat, "p", "i"));
    }

    [Fact]
    public async Task GetAvailableOfTypeAsyncFiltersByTypeAndCredentials()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m"), Model("i", type: ModelType.Image)],
            AuthOverride = ApiKeyAuth(),
        });

        var images = await registry.GetAvailableOfTypeAsync(ModelType.Image, "p");

        Assert.Equal("i", Assert.Single(images).Id);
        Assert.Empty(await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "p"));
    }

    // ================================================================= auth surface

    [Fact]
    public async Task HasConfiguredAuthFollowsTheProvider()
    {
        var configured = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });
        Assert.True(configured.HasConfiguredAuth(RequireModel(configured, "p", "m")));

        var unconfigured = await WithRegistryAsync(new StubProvider
        {
            Id = "q",
            Models = [Model("m", provider: "q")],
            AuthOverride = UnconfiguredAuth(),
        });
        Assert.False(unconfigured.HasConfiguredAuth(RequireModel(unconfigured, "q", "m")));
    }

    [Fact]
    public async Task GetProviderAuthStatusForwardsToTheRuntime()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });

        Assert.Equal(AuthStatusSource.Environment, registry.GetProviderAuthStatus("p").Source);
        Assert.False(registry.GetProviderAuthStatus("nope").Configured);
    }

    [Fact]
    public async Task GetProviderDisplayNameFallsBackToTheProviderId()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        Assert.Equal("p", registry.GetProviderDisplayName("p"));
        // Unknown providers keep their raw id, matching the TS `?? provider` fallback.
        Assert.Equal("nope", registry.GetProviderDisplayName("nope"));
    }

    [Fact]
    public async Task IsUsingOAuthFollowsTheAvailabilitySnapshot()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth { OAuth = new StubOAuthAuth() },
        });
        await SettleAsync(runtime);
        await runtime.LoginAsync("p", CredentialKind.OAuth, new ProviderAuthInteraction(new NoopInteraction()));
        var registry = new ModelRegistry(runtime);

        Assert.True(registry.IsUsingOAuth(RequireModel(registry, "p", "m")));

        var apiKeyRegistry = await WithRegistryAsync(new StubProvider
        {
            Id = "q",
            Models = [Model("m", provider: "q")],
            AuthOverride = ApiKeyAuth(),
        });
        Assert.False(apiKeyRegistry.IsUsingOAuth(RequireModel(apiKeyRegistry, "q", "m")));
    }

    // ================================================================= getApiKeyAndHeaders

    [Fact]
    public async Task GetApiKeyAndHeadersResolvesConfiguredAuth()
    {
        var headers = new Dictionary<string, string?> { ["X-Auth"] = "1" };
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth("resolved-key", "https://auth.invalid", headers),
        });

        var result = await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m"));

        var ok = Assert.IsType<ResolvedRequestAuth.Ok>(result);
        Assert.Equal("resolved-key", ok.ApiKey);
        Assert.Equal("https://auth.invalid", ok.BaseUrl);
        Assert.Equal("1", ok.Headers?["X-Auth"]);
        Assert.Null(ok.Env);
    }

    [Fact]
    public async Task GetApiKeyAndHeadersDropsAnEmptyBaseUrl()
    {
        // TS spreads the baseUrl key only when the value is truthy, so "" is reported as absent.
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth("resolved-key", baseUrl: ""),
        });

        var ok = Assert.IsType<ResolvedRequestAuth.Ok>(
            await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m")));

        Assert.Equal("resolved-key", ok.ApiKey);
        Assert.Null(ok.BaseUrl);
    }

    [Fact]
    public async Task GetApiKeyAndHeadersFallsBackToCompatibilityHeadersWhenUnconfigured()
    {
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"headers\":{\"X-Provider\":\"p\"},\"models\":[{\"id\":\"m\",\"name\":\"M\","
            + "\"headers\":{\"X-Model\":\"m\"}}]}}}");
        var registry = await WithRegistryAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = UnconfiguredAuth() },
            modelsPath: file.Path);

        var result = await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m"));

        var ok = Assert.IsType<ResolvedRequestAuth.Ok>(result);
        Assert.Null(ok.ApiKey);
        Assert.Equal("p", ok.Headers?["X-Provider"]);
        Assert.Equal("m", ok.Headers?["X-Model"]);
    }

    [Fact]
    public async Task GetApiKeyAndHeadersFailsWhenTheModelRequiresAuthHeader()
    {
        // No resolved auth plus `authHeader: true` cannot be satisfied at all, so it is an error rather
        // than an anonymous request.
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"authHeader\":true,\"models\":[{\"id\":\"m\",\"name\":\"M\"}]}}}");
        var registry = await WithRegistryAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = UnconfiguredAuth() },
            modelsPath: file.Path);

        var result = await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m"));

        var fail = Assert.IsType<ResolvedRequestAuth.Fail>(result);
        Assert.Equal("No API key found for \"p\"", fail.Error);
    }

    [Fact]
    public async Task GetApiKeyAndHeadersMapsTheAuthHeaderCause()
    {
        // The composer throws `authHeader requires a resolved API key`; ModelsError carries it as the
        // inner exception, which the facade translates into the user-facing message.
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"authHeader\":true,\"models\":[{\"id\":\"m\",\"name\":\"M\"}]}}}");
        var registry = await WithRegistryAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth(apiKey: null) },
            modelsPath: file.Path);

        var result = await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m"));

        var fail = Assert.IsType<ResolvedRequestAuth.Fail>(result);
        Assert.Equal("No API key found for \"p\"", fail.Error);
    }

    [Fact]
    public async Task GetApiKeyAndHeadersReportsOtherAuthFailures()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ThrowingAuth("boom"),
        });

        var result = await registry.GetApiKeyAndHeadersAsync(RequireModel(registry, "p", "m"));

        // The cause's own message survives; only the authHeader sentinel is rewritten.
        var fail = Assert.IsType<ResolvedRequestAuth.Fail>(result);
        Assert.Equal("boom", fail.Error);
    }

    [Fact]
    public async Task GetProviderAuthReturnsTheResolution()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth("resolved-key"),
        });

        Assert.Equal("resolved-key", (await registry.GetProviderAuthAsync("p"))?.Auth.ApiKey);
        Assert.Null(await registry.GetProviderAuthAsync("nope"));
    }

    [Fact]
    public async Task GetApiKeyForProviderReturnsTheResolvedKey()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth("resolved-key"),
        });

        Assert.Equal("resolved-key", await registry.GetApiKeyForProviderAsync("p"));
    }

    [Fact]
    public async Task GetApiKeyForProviderSwallowsResolutionFailures()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ThrowingAuth("boom"),
        });

        Assert.Null(await registry.GetApiKeyForProviderAsync("p"));
        Assert.Null(await registry.GetApiKeyForProviderAsync("nope"));
    }

    // ================================================================= registration

    [Fact]
    public async Task RegisterProviderByObjectIsVisibleAndRemovable()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        registry.RegisterProvider(new StubProvider { Id = "q", Models = [Model("m", provider: "q")] });

        Assert.Equal("m", registry.Find("q", "m")?.Id);
        Assert.NotNull(registry.GetRegisteredNativeProvider("q"));
        Assert.Contains("q", registry.GetRegisteredProviderIds());

        registry.UnregisterProvider("q");

        Assert.Null(registry.Find("q", "m"));
        Assert.Null(registry.GetRegisteredNativeProvider("q"));
        Assert.DoesNotContain("q", registry.GetRegisteredProviderIds());
    }

    [Fact]
    public async Task RegisterProviderByNameIsVisibleAndRemovable()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        registry.RegisterProvider("x", new ProviderConfigInput
        {
            BaseUrl = "https://x.invalid",
            Api = "stub-api",
            Models =
            [
                new ProviderChatModelConfig
                {
                    Id = "xm",
                    Name = "XM",
                    Input = [ModelInput.Text],
                    Cost = new ModelCostRates(0, 0, 0, 0),
                    Reasoning = false,
                    ContextWindow = 1_000,
                    MaxTokens = 100,
                },
            ],
        });

        Assert.Equal("XM", registry.Find("x", "xm")?.Name);
        Assert.NotNull(registry.GetRegisteredProviderConfig("x"));
        Assert.Contains("x", registry.GetRegisteredProviderIds());

        registry.UnregisterProvider("x");

        Assert.Null(registry.Find("x", "xm"));
        Assert.Null(registry.GetRegisteredProviderConfig("x"));
    }

    [Fact]
    public async Task RegisterVirtualModelIsVisibleAndRemovable()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        registry.RegisterVirtualModel(new VirtualModelDefinition
        {
            Provider = "v",
            Id = "vm",
            Name = "Virtual",
            Route = _ => Task.FromResult(new ModelRoute
            {
                Model = Model("m"),
                ThinkingLevel = "off",
            }),
        });

        // A provider made of virtual models only is configured immediately (no credentials needed).
        Assert.Equal("vm", registry.Find("v", "vm")?.Id);
        Assert.True(registry.HasConfiguredAuth(RequireModel(registry, "v", "vm")));
        // Virtual models are not extension/native registrations, so they stay out of that list.
        Assert.DoesNotContain("v", registry.GetRegisteredProviderIds());

        registry.UnregisterVirtualModel("v", "vm");

        Assert.Null(registry.Find("v", "vm"));
    }

    [Fact]
    public async Task GetRegisteredProviderIdsListsBothKinds()
    {
        var registry = await WithRegistryAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        registry.RegisterProvider("cfg", new ProviderConfigInput
        {
            BaseUrl = "https://cfg.invalid",
            Api = "stub-api",
        });
        registry.RegisterProvider(new StubProvider { Id = "native", Models = [Model("n", provider: "native")] });

        var ids = registry.GetRegisteredProviderIds();

        Assert.Contains("cfg", ids);
        Assert.Contains("native", ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ================================================================= refresh & errors

    [Fact]
    public async Task RefreshAsyncReloadsModelsJson()
    {
        using var file = new TempModelsFile("{\"providers\":{}}");
        var registry = await WithRegistryAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = UnconfiguredAuth() },
            modelsPath: file.Path);
        Assert.False(registry.GetProviderAuthStatus("cfg").Configured);

        file.Write("{\"providers\":{\"cfg\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"apiKey\":\"cfg-key\"}}}");
        await registry.RefreshAsync();

        Assert.Equal(AuthStatusSource.ModelsJsonKey, registry.GetProviderAuthStatus("cfg").Source);
    }

    [Fact]
    public async Task GetErrorSurfacesCompositionErrors()
    {
        using var file = new TempModelsFile("{\"providers\":{}}");
        var runtime = await CreateAsync(modelsPath: file.Path);
        var registry = new ModelRegistry(runtime);
        Assert.Null(registry.GetError());

        // A provider that names models but no api/baseUrl cannot be composed.
        file.Write("{\"providers\":{\"p\":{\"models\":[{\"id\":\"m\"}]}}}");
        await registry.RefreshAsync();

        var error = registry.GetError();
        Assert.NotNull(error);
        Assert.Contains("Provider \"p\":", error);
    }

    // ================================================================= dispatch

    [Fact]
    public async Task StreamAndStreamSimpleForwardToTheRuntime()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });
        var model = RequireModel(registry, "p", "m");

        Assert.Equal("stream", await MarkerOf(registry.Stream(model, [])));
        Assert.Equal("stream-simple", await MarkerOf(registry.StreamSimple(model, [])));
    }

    [Fact]
    public async Task CompleteAsyncForwardsToTheRuntime()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });

        var message = await registry.CompleteAsync(RequireModel(registry, "p", "m"), []);

        Assert.Equal("stream", message.Model);
    }

    [Fact]
    public async Task ClassifyAndGenerateImagesForwardToTheRuntime()
    {
        var registry = await WithRegistryAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("i", type: ModelType.Image), Model("c", type: ModelType.Classifier)],
            AuthOverride = ApiKeyAuth(),
        });

        // `Find` is chat-only (it mirrors TS `getModel`); non-chat types go through `FindOfType`.
        Assert.Null(registry.Find("p", "i"));

        var images = await registry.GenerateImagesAsync(
            registry.FindOfType(ModelType.Image, "p", "i")!, new ImagesContext { Input = [] });
        var classified = await registry.ClassifyAsync(
            registry.FindOfType(ModelType.Classifier, "p", "c")!,
            new ClassifierContext
            {
                State = new JsonObject(),
                Questions = new Dictionary<string, ClassifierQuestion>(),
            });

        Assert.Equal("images", images.Model);
        Assert.Equal("classify", classified.Model);
    }
}
