using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Behaviour tests for <see cref="ModelRuntime"/> (port of TS <c>core/model-runtime.ts</c>).
/// </summary>
/// <remarks>
/// The TS class exports no helpers, so there is no reference suite to mirror. These assertions pin the
/// observable rules instead: the catalog/registration surface, the auth-resolution boundary
/// (<c>prepareRequest</c>), the availability snapshot's generation bookkeeping, the credential-operation
/// queue, the virtual-model routing path, and the error reporting.
/// </remarks>
public class CoreModelRuntimeTests
{
    // ------------------------------------------------------------------ shared fakes

    private static ModelSpec Model(
        string id,
        string provider = "p",
        string api = "stub-api",
        ModelType type = ModelType.Chat,
        long maxTokens = 100,
        bool reasoning = false)
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
            Reasoning = reasoning,
        };

    /// <summary>A stream that already ended with a message whose <c>model</c> is the marker.</summary>
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

    private static DeferredHandle Deferred()
        => new()
        {
            Provider = "p",
            ModelId = "m",
            Api = "stub-api",
            Id = "handle",
        };

    private sealed class StubApiKeyAuth(
        string? apiKey = "resolved-key",
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        string source = "stub-env") : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public int ResolveCount { get; private set; }

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return Task.FromResult<AuthResult?>(new AuthResult
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
    }

    /// <summary>Auth that reports the provider as unconfigured (TS <c>resolve()</c> returning undefined).</summary>
    private sealed class UnconfiguredApiKeyAuth : IApiKeyAuth
    {
        public string Name => "unconfigured";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(null);
    }

    private sealed class StubOAuthAuth : IOAuthAuth
    {
        public string Name => "stub-oauth";

        public bool IsSubscription { get; init; } = true;

        public Func<ProviderAuthInteraction, LoginOptions?, CancellationToken, Task<Credential.OAuth>>? OnLogin
        {
            get;
            init;
        }

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => OnLogin is null
                ? throw new NotSupportedException()
                : OnLogin(interaction, options, cancellationToken);

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

    /// <summary>Provider without image/classifier capability, recording what it was dispatched.</summary>
    private class StubProvider : IProvider
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public string? NameOverride { get; init; }

        public ProviderAuth? AuthOverride { get; init; }

        public Func<RefreshModelsContext, Task>? Refresh { get; init; }

        public string StreamMarker { get; init; } = "stream";

        public string StreamSimpleMarker { get; init; } = "stream-simple";

        public ModelSpec? LastModel { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastOptions { get; private set; }

        public string Name => NameOverride ?? Id;

        public string? BaseUrl => null;

        public ProviderAuth? Auth => AuthOverride;

        public IReadOnlyList<ModelSpec> GetModels() => [.. Models.Where(model => model.Type == ModelType.Chat)];

        public IReadOnlyList<ModelSpec> GetAllModels() => Models;

        public Func<RefreshModelsContext, Task>? RefreshModels => Refresh;

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            LastModel = model;
            LastOptions = options;
            return MarkerStream(model, StreamMarker);
        }

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            LastModel = model;
            LastOptions = options;
            return MarkerStream(model, StreamSimpleMarker);
        }
    }

    /// <summary>Provider that also serves images and classification, recording the boundary options.</summary>
    private sealed class ImagingProvider : StubProvider, IImagesProvider, IClassifierProvider
    {
        public ImagesOptions? LastImagesOptions { get; private set; }

        public ClassifierOptions? LastClassifierOptions { get; private set; }

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
        {
            LastImagesOptions = options;
            return Task.FromResult(new AssistantImages
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = "images",
            });
        }

        public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
        {
            LastClassifierOptions = options;
            return Task.FromResult(new ClassifierResult
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = "classify",
            });
        }
    }

    private sealed class RecordingRefresh
    {
        public List<bool> AllowNetwork { get; } = [];

        public Func<RefreshModelsContext, Task>? Fail { get; init; }

        public Task Record(RefreshModelsContext context)
        {
            AllowNetwork.Add(context.AllowNetwork);
            return Fail is null ? Task.CompletedTask : Fail(context);
        }
    }

    /// <summary>A <c>models.json</c> file that outlives the runtime, so <c>refresh()</c> can reload it.</summary>
    private sealed class TempModelsFile : IDisposable
    {
        public TempModelsFile(string json)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"pi-runtime-models-{Guid.NewGuid():N}.json");
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
        ICredentialStore? credentials = null,
        string? modelsPath = null,
        Func<string, string?>? env = null,
        IModelsStore? modelsStore = null)
        => ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials ?? new InMemoryCredentialStore(),
            ModelsPathDisabled = modelsPath is null,
            ModelsPath = modelsPath,
            ModelsStore = modelsStore ?? new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = env ?? (name => name == "PI_OFFLINE" ? "1" : null),
        });

    private static async Task SettleAsync(ModelRuntime runtime)
    {
        if (runtime.PendingBackgroundRefresh is { } pending) await pending;
    }

    private static async Task<ModelRuntime> WithProviderAsync(
        StubProvider provider,
        ICredentialStore? credentials = null,
        string? modelsPath = null,
        Func<string, string?>? env = null)
    {
        var runtime = await CreateAsync(credentials, modelsPath, env);
        runtime.RegisterNativeProvider(provider);
        await SettleAsync(runtime);
        return runtime;
    }

    private static ProviderAuth ApiKeyAuth(
        string? apiKey = "resolved-key",
        string? baseUrl = null,
        IReadOnlyDictionary<string, string?>? headers = null,
        string source = "stub-env")
        => new() { ApiKey = new StubApiKeyAuth(apiKey, baseUrl, headers, source) };

    private static Task<AssistantImages> GenerateImages(ModelRuntime runtime, ModelSpec model)
        => runtime.GenerateImagesAsync(model, new ImagesContext { Input = [] });

    private static Task<ClassifierResult> Classify(ModelRuntime runtime, ModelSpec model)
        => runtime.ClassifyAsync(model, new ClassifierContext
        {
            State = new JsonObject(),
            Questions = new Dictionary<string, ClassifierQuestion>(),
        });

    private static AssistantMessage Assistant(
        string provider, string model, StopReason stopReason = StopReason.Stop, string? thinkingLevel = null)
        => new(
            Content: [],
            StopReason: stopReason,
            Model: model,
            Provider: provider,
            ThinkingLevel: Pi.CodingAgent.Core.ThinkingLevels.Parse(thinkingLevel));

    // ================================================================= creation & catalog

    [Fact]
    public async Task CreateComposesTheBuiltinCatalogWithoutRefreshing()
    {
        var runtime = await CreateAsync();

        Assert.Equal(42, runtime.GetProviders().Count);
        Assert.NotNull(runtime.GetProvider("anthropic"));
        Assert.NotNull(runtime.GetProvider("radius"));
        Assert.Null(runtime.GetError());
        Assert.Empty(runtime.GetAvailableSnapshot());
        Assert.Empty(runtime.GetRegisteredProviderIds());
    }

    [Fact]
    public async Task CreateLoadsModelsJsonFromTheGivenPath()
    {
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"baseUrl\":\"https://cfg.invalid\",\"apiKey\":\"cfg-key\"}}}");

        var runtime = await CreateAsync(modelsPath: file.Path);

        Assert.Null(runtime.GetError());
        Assert.Equal(AuthStatusSource.ModelsJsonKey, runtime.GetProviderAuthStatus("p").Source);
    }

    [Fact]
    public async Task CreateWithoutModelsPathHasNoModelsJson()
    {
        // A stale config must not leak in through the agent dir when modelsPath is disabled.
        var runtime = await CreateAsync();

        Assert.Null(runtime.GetError());
        Assert.False(runtime.GetProviderAuthStatus("definitely-not-a-provider").Configured);
    }

    [Fact]
    public async Task CreateHonoursPiOfflineForTheDefaultNetworkPolicy()
    {
        var onlineRecording = new RecordingRefresh();
        var online = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
            Refresh = onlineRecording.Record,
        }, env: _ => null);
        onlineRecording.AllowNetwork.Clear();
        await online.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });
        Assert.Equal(new[] { false, true }, onlineRecording.AllowNetwork);

        var offlineRecording = new RecordingRefresh();
        var offline = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
            Refresh = offlineRecording.Record,
        });
        offlineRecording.AllowNetwork.Clear();
        await offline.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });

        // Presence of PI_OFFLINE (not truthiness) disables the default network policy.
        Assert.Equal(new[] { false }, offlineRecording.AllowNetwork);
    }

    // ================================================================= registration

    [Fact]
    public async Task RegisterNativeProviderRejectsAnEmptyId()
    {
        var runtime = await CreateAsync();

        var error = Assert.Throws<InvalidOperationException>(
            () => runtime.RegisterNativeProvider(new StubProvider { Id = "  ", Models = [] }));
        Assert.Equal("Provider id must not be empty.", error.Message);
    }

    [Fact]
    public async Task RegisterNativeProviderPublishesItInTheCatalog()
    {
        var runtime = await WithProviderAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        Assert.NotNull(runtime.GetProvider("p"));
        Assert.Equal("m", Assert.Single(runtime.GetModels("p")).Id);
        Assert.Equal("p", runtime.GetRegisteredNativeProvider("p")?.Id);
        Assert.Equal(new[] { "p" }, runtime.GetRegisteredProviderIds());
    }

    [Fact]
    public async Task RegisterProviderMergesDefinedValuesAndPreservesUndefinedOnes()
    {
        var runtime = await CreateAsync();
        runtime.RegisterProvider("p", new ProviderConfigInput { Name = "First", ApiKey = "k1", Api = "api" });
        await SettleAsync(runtime);

        runtime.RegisterProvider("p", new ProviderConfigInput { BaseUrl = "https://second.invalid" });
        await SettleAsync(runtime);

        var effective = runtime.GetRegisteredProviderConfig("p");
        Assert.NotNull(effective);
        Assert.Equal("First", effective.Name);
        Assert.Equal("k1", effective.ApiKey);
        Assert.Equal("api", effective.Api);
        Assert.Equal("https://second.invalid", effective.BaseUrl);
        Assert.Equal("First", runtime.GetProvider("p")?.Name);
    }

    [Fact]
    public async Task RegisterProviderRejectsABrokenRegistrationWithoutTouchingTheStoredConfig()
    {
        var runtime = await CreateAsync();
        runtime.RegisterProvider("p", new ProviderConfigInput { Name = "Kept", ApiKey = "k1" });
        await SettleAsync(runtime);

        // streamSimple requires an api, and validation must run before the stored config changes.
        var error = Assert.Throws<InvalidOperationException>(() => runtime.RegisterProvider("p",
            new ProviderConfigInput
            {
                StreamSimple = (_, _, _) => MarkerStream(Model("m"), "never"),
            }));
        Assert.Contains("\"api\" is required when registering streamSimple.", error.Message);

        var effective = runtime.GetRegisteredProviderConfig("p");
        Assert.NotNull(effective);
        Assert.Equal("Kept", effective.Name);
        Assert.Null(effective.StreamSimple);
    }

    [Fact]
    public async Task RegisteringAnExtensionDropsTheNativeRegistration()
    {
        var runtime = await WithProviderAsync(new StubProvider { Id = "p", Models = [Model("m")] });
        Assert.NotNull(runtime.GetRegisteredNativeProvider("p"));

        runtime.RegisterProvider("p", new ProviderConfigInput { Name = "Extension" });
        await SettleAsync(runtime);

        Assert.Null(runtime.GetRegisteredNativeProvider("p"));
        Assert.Equal("Extension", runtime.GetRegisteredProviderConfig("p")?.Name);
        Assert.Equal(new[] { "p" }, runtime.GetRegisteredProviderIds());
    }

    [Fact]
    public async Task RegisteringANativeProviderDropsTheExtensionRegistration()
    {
        var runtime = await CreateAsync();
        runtime.RegisterProvider("p", new ProviderConfigInput { Name = "Extension" });
        await SettleAsync(runtime);

        runtime.RegisterNativeProvider(new StubProvider { Id = "p", Models = [Model("m")] });
        await SettleAsync(runtime);

        Assert.Null(runtime.GetRegisteredProviderConfig("p"));
        Assert.Equal(new[] { "p" }, runtime.GetRegisteredProviderIds());
    }

    [Fact]
    public async Task UnregisterProviderRemovesItFromTheCatalog()
    {
        var runtime = await WithProviderAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        runtime.UnregisterProvider("p");
        await SettleAsync(runtime);

        Assert.Null(runtime.GetProvider("p"));
        Assert.Empty(runtime.GetRegisteredProviderIds());
    }

    // ================================================================= virtual models

    private static VirtualModelDefinition Virtual(
        string providerId, string id, Func<ModelRouteRequest, Task<ModelRoute>> route, string? level = "medium")
        => new()
        {
            Provider = providerId,
            Id = id,
            Name = id,
            ThinkingLevels = level is null ? null : ["off", level],
            Route = route,
        };

    [Fact]
    public async Task RegisterVirtualModelRejectsEmptyIds()
    {
        var runtime = await CreateAsync();

        var error = Assert.Throws<InvalidOperationException>(() => runtime.RegisterVirtualModel(
            Virtual(" ", "id", _ => Task.FromResult(new ModelRoute { Model = Model("m"), ThinkingLevel = "off" }))));
        Assert.Equal("Virtual model provider and id must not be empty.", error.Message);
    }

    [Fact]
    public async Task RegisterVirtualModelRejectsAConflictWithAPhysicalModel()
    {
        var runtime = await WithProviderAsync(new StubProvider { Id = "p", Models = [Model("m")] });

        var error = Assert.Throws<InvalidOperationException>(() => runtime.RegisterVirtualModel(
            Virtual("p", "m", _ => Task.FromResult(new ModelRoute { Model = Model("m"), ThinkingLevel = "off" }))));
        Assert.Equal("Virtual model p/m conflicts with a physical model.", error.Message);
    }

    [Fact]
    public async Task RegisterVirtualModelPublishesItAndMarksAKeylessProviderConfigured()
    {
        var runtime = await CreateAsync();
        runtime.RegisterVirtualModel(Virtual("v", "vm",
            _ => Task.FromResult(new ModelRoute { Model = Model("m"), ThinkingLevel = "off" })));
        await SettleAsync(runtime);

        var virtualModel = Assert.Single(runtime.GetModels("v"));
        Assert.Equal("vm", virtualModel.Id);
        Assert.True(VirtualModels.IsVirtualModel(virtualModel));
        Assert.True(runtime.HasConfiguredAuth("v"));
        Assert.Null(runtime.GetPhysicalModel("v", "vm"));
        Assert.Equal("vm", Assert.Single(runtime.GetAvailableSnapshot()).Id);
    }

    [Fact]
    public async Task UnregisterVirtualModelRemovesItFromTheCatalog()
    {
        var runtime = await CreateAsync();
        runtime.RegisterVirtualModel(Virtual("v", "vm",
            _ => Task.FromResult(new ModelRoute { Model = Model("m"), ThinkingLevel = "off" })));
        await SettleAsync(runtime);

        runtime.UnregisterVirtualModel("v", "vm");
        await SettleAsync(runtime);

        Assert.Empty(runtime.GetModels("v"));
        Assert.Empty(runtime.GetAvailableSnapshot());
    }

    [Fact]
    public async Task UnregisterVirtualModelIgnoresUnknownIds()
    {
        var runtime = await CreateAsync();
        runtime.UnregisterVirtualModel("v", "vm");

        Assert.Empty(runtime.GetRegisteredProviderIds());
    }

    // ================================================================= provider auth status

    [Fact]
    public async Task ProviderAuthStatusReportsARuntimeKeyFirst()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored")));
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() }, credentials);
        await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });
        Assert.Equal(AuthStatusSource.Stored, runtime.GetProviderAuthStatus("p").Source);

        await runtime.SetRuntimeApiKeyAsync("p", "runtime-key");

        var status = runtime.GetProviderAuthStatus("p");
        Assert.True(status.Configured);
        Assert.Equal(AuthStatusSource.Runtime, status.Source);
    }

    [Fact]
    public async Task ProviderAuthStatusReportsStoredCredentials()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored")));
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() }, credentials);
        await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });

        var status = runtime.GetProviderAuthStatus("p");
        Assert.True(status.Configured);
        Assert.Equal(AuthStatusSource.Stored, status.Source);
    }

    [Fact]
    public async Task ProviderAuthStatusReportsAModelsJsonKeyBeforeTheAvailabilityCheck()
    {
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"baseUrl\":\"https://cfg.invalid\",\"apiKey\":\"cfg-key\"}}}");
        var runtime = await CreateAsync(modelsPath: file.Path);

        var status = runtime.GetProviderAuthStatus("p");
        Assert.True(status.Configured);
        Assert.Equal(AuthStatusSource.ModelsJsonKey, status.Source);
    }

    [Fact]
    public async Task ProviderAuthStatusReportsAnEnvironmentCheckLast()
    {
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth(source: "ANTHROPIC_API_KEY") });
        await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });

        var status = runtime.GetProviderAuthStatus("p");
        Assert.True(status.Configured);
        Assert.Equal(AuthStatusSource.Environment, status.Source);
        Assert.Equal("ANTHROPIC_API_KEY", status.Label);
    }

    [Fact]
    public async Task ProviderAuthStatusReportsNotConfiguredForAnUnknownProvider()
    {
        var runtime = await CreateAsync();

        var status = runtime.GetProviderAuthStatus("nobody");
        Assert.False(status.Configured);
        Assert.Null(status.Source);
    }

    [Fact]
    public async Task IsUsingOAuthAndSubscriptionFollowTheAvailabilitySnapshot()
    {
        var credentials = new InMemoryCredentialStore();
        await credentials.ModifyAsync("p",
            _ => Task.FromResult<Credential?>(new Credential.OAuth("refresh", "access", long.MaxValue)));
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth { OAuth = new StubOAuthAuth() },
        }, credentials);

        // The registration's background refresh already ran one availability pass.
        Assert.True(runtime.IsUsingOAuth("p"));
        Assert.True(runtime.IsUsingSubscription("p"));

        await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });
        Assert.True(runtime.IsUsingOAuth("p"));
    }

    [Fact]
    public async Task IsUsingSubscriptionIsFalseForAnApiKeyCredential()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth
            {
                ApiKey = new StubApiKeyAuth(),
                OAuth = new StubOAuthAuth(),
            },
        });

        Assert.False(runtime.IsUsingOAuth("p"));
        Assert.False(runtime.IsUsingSubscription("p"));
    }

    // ================================================================= auth & headers

    [Fact]
    public async Task GetAuthForAProviderReturnsTheResolvedAuth()
    {
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth("from-auth") });

        var auth = await runtime.GetAuthAsync("p");

        Assert.NotNull(auth);
        Assert.Equal("from-auth", auth.Auth.ApiKey);
        Assert.Equal("stub-env", auth.Source);
    }

    [Fact]
    public async Task GetAuthForAModelMergesConfiguredModelHeaders()
    {
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"models\":[{\"id\":\"m\",\"name\":\"M\",\"headers\":{\"X-Model\":\"1\"}}]}}}");
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth(headers: new Dictionary<string, string?>
            {
                ["X-Auth"] = "a",
            }) },
            modelsPath: file.Path);

        var model = runtime.GetModel("p", "m");
        Assert.NotNull(model);
        var auth = await runtime.GetAuthAsync(model);

        Assert.NotNull(auth);
        Assert.Equal("a", auth.Auth.Headers?["X-Auth"]);
        Assert.Equal("1", auth.Auth.Headers?["X-Model"]);
    }

    [Fact]
    public async Task StreamSimpleMergesAuthAndCallerHeadersCaseInsensitively()
    {
        var provider = new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(headers: new Dictionary<string, string?>
            {
                ["X-Shared"] = "from-auth",
                ["X-Only-Auth"] = "a",
            }),
        };
        var runtime = await WithProviderAsync(provider);

        await runtime.StreamSimple(Model("m"), [], new Dictionary<string, object?>
        {
            ["headers"] = new Dictionary<string, string?> { ["x-shared"] = "from-caller" },
        }).WaitForDoneAsync();

        var headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(provider.LastOptions!["headers"]);
        Assert.Equal("from-caller", headers["x-shared"]);
        Assert.False(headers.ContainsKey("X-Shared"));
        Assert.Equal("a", headers["X-Only-Auth"]);
    }

    [Fact]
    public async Task StreamSimpleAppliesTransformHeadersAndStripsTheOption()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);
        IReadOnlyDictionary<string, string?>? seen = null;

        await runtime.StreamSimple(Model("m"), [], new Dictionary<string, object?>
        {
            ["transformHeaders"] = (Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>)(
                headers =>
                {
                    seen = headers;
                    return Task.FromResult<IReadOnlyDictionary<string, string?>>(
                        new Dictionary<string, string?> { ["X-Transformed"] = "1" });
                }),
        }).WaitForDoneAsync();

        Assert.NotNull(seen);
        Assert.Equal("resolved-key", provider.LastOptions!["apiKey"]);
        Assert.False(provider.LastOptions!.ContainsKey(Models.TransformHeadersOptionKey));
        var headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(provider.LastOptions["headers"]);
        Assert.Equal("1", headers["X-Transformed"]);
    }

    [Fact]
    public async Task StreamSimpleOverridesTheRequestModelBaseUrlFromTheAuthResolution()
    {
        var provider = new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(baseUrl: "https://per-credential.invalid/v1"),
        };
        var runtime = await WithProviderAsync(provider);

        await runtime.StreamSimple(Model("m"), []).WaitForDoneAsync();

        Assert.Equal("https://per-credential.invalid/v1", provider.LastModel?.BaseUrl);
    }

    [Fact]
    public async Task StreamSimpleKeepsTheCallerApiKeyAheadOfTheResolution()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);

        await runtime.StreamSimple(Model("m"), [], new Dictionary<string, object?> { ["apiKey"] = "caller-key" })
            .WaitForDoneAsync();

        Assert.Equal("caller-key", provider.LastOptions!["apiKey"]);
    }

    // ================================================================= request dispatch

    [Fact]
    public async Task StreamDispatchesThroughTheProviderStream()
    {
        var provider = new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
            StreamMarker = "full",
            StreamSimpleMarker = "simple",
        };
        var runtime = await WithProviderAsync(provider);

        Assert.Equal("full", await MarkerOf(runtime.Stream(Model("m"), [])));
        Assert.Equal("simple", await MarkerOf(runtime.StreamSimple(Model("m"), [])));
        Assert.Equal("full", (await runtime.CompleteAsync(Model("m"), [])).Model);
        Assert.Equal("simple", (await runtime.CompleteSimpleAsync(Model("m"), [])).Model);
    }

    [Fact]
    public async Task StreamReportsAnUnknownProviderAsAnErrorTerminal()
    {
        var runtime = await CreateAsync();

        var done = await runtime.Stream(Model("m", provider: "ghost"), []).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Unknown provider: ghost", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamReportsAnUnconfiguredProviderAsAnErrorTerminal()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth { ApiKey = new UnconfiguredApiKeyAuth() },
        });

        var done = await runtime.StreamSimple(Model("m"), []).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Provider is not configured: p", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamDeferredRequiresTheProviderCapability()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);

        var done = await runtime.StreamDeferred(Model("m"), Deferred()).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Provider p does not support deferred responses", done.ErrorMessage);
    }

    [Fact]
    public async Task CancelDeferredRequiresTheProviderCapability()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);

        var error = await Assert.ThrowsAsync<ModelsError>(
            () => runtime.CancelDeferredAsync(Model("m"), Deferred()));

        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Equal("Provider p does not support deferred responses", error.Message);
    }

    [Fact]
    public async Task GenerateImagesUsesTheAuthBoundaryAndAppliesTransformHeaders()
    {
        var provider = new ImagingProvider
        {
            Id = "p",
            Models = [Model("i", type: ModelType.Image), Model("c", type: ModelType.Classifier)],
            AuthOverride = ApiKeyAuth(headers: new Dictionary<string, string?> { ["X-Auth"] = "a" }),
        };
        var runtime = await WithProviderAsync(provider);

        var images = await runtime.GenerateImagesAsync(
            Model("i", type: ModelType.Image),
            new ImagesContext { Input = [] },
            new ModelsImagesOptions
            {
                ApiKey = "images-key",
                Headers = new Dictionary<string, string?> { ["X-Caller"] = "c" },
                TransformHeaders = headers => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?>(headers) { ["X-Transformed"] = "1" }),
            });

        Assert.Equal("images", images.Model);
        Assert.NotNull(provider.LastImagesOptions);
        Assert.Equal("images-key", provider.LastImagesOptions.ApiKey);
        Assert.Equal("a", provider.LastImagesOptions.Headers?["X-Auth"]);
        Assert.Equal("c", provider.LastImagesOptions.Headers?["X-Caller"]);
        Assert.Equal("1", provider.LastImagesOptions.Headers?["X-Transformed"]);
    }

    [Fact]
    public async Task GenerateImagesReportsUnsupportedProvidersAsAnErrorResult()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("i", type: ModelType.Image)],
            AuthOverride = ApiKeyAuth(),
        });

        var images = await GenerateImages(runtime, Model("i", type: ModelType.Image));

        Assert.Equal(ImagesStopReason.Error, images.StopReason);
        Assert.Equal("Provider p does not support image generation", images.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyReportsUnsupportedProvidersAsAnErrorResult()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("c", type: ModelType.Classifier)],
            AuthOverride = ApiKeyAuth(),
        });

        var result = await Classify(runtime, Model("c", type: ModelType.Classifier));

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal("Provider p does not support classification", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassifyDispatchesThroughTheAuthBoundary()
    {
        var provider = new ImagingProvider
        {
            Id = "p",
            Models = [Model("c", type: ModelType.Classifier)],
            AuthOverride = ApiKeyAuth(),
        };
        var runtime = await WithProviderAsync(provider);

        var result = await Classify(runtime, Model("c", type: ModelType.Classifier));

        Assert.Equal("classify", result.Model);
        Assert.Equal("resolved-key", provider.LastClassifierOptions?.ApiKey);
    }

    [Fact]
    public async Task GenerateImagesReportsANonImageModelAsAnErrorResult()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
        });

        var images = await GenerateImages(runtime, Model("m"));

        Assert.Equal(ImagesStopReason.Error, images.StopReason);
        Assert.Contains("is not an image model", images.ErrorMessage);
    }

    // ================================================================= virtual routing

    private static async Task<(ModelRuntime Runtime, StubProvider Physical, ModelSpec Virtual)>
        WithRoutedVirtualModelAsync(bool sameProvider, bool credentialed = true)
    {
        var physicalModel = Model("m", maxTokens: 100, reasoning: true);
        var provider = new StubProvider
        {
            Id = "p",
            Models = [physicalModel],
            AuthOverride = credentialed
                ? ApiKeyAuth(headers: new Dictionary<string, string?> { ["X-Auth"] = "a" })
                : new ProviderAuth { ApiKey = new UnconfiguredApiKeyAuth() },
        };
        var runtime = await WithProviderAsync(provider);
        if (credentialed) await runtime.SetRuntimeApiKeyAsync("p", "physical-key");

        // A virtual model under the physical provider id exercises the same-provider credential path.
        var providerId = sameProvider ? "p" : "v";
        runtime.RegisterVirtualModel(Virtual(providerId, "vm",
            _ => Task.FromResult(new ModelRoute { Model = physicalModel, ThinkingLevel = "medium" })));
        await SettleAsync(runtime);

        var virtualModel = runtime.GetModel(providerId, "vm");
        Assert.NotNull(virtualModel);
        Assert.True(VirtualModels.IsVirtualModel(virtualModel));
        return (runtime, provider, virtualModel);
    }

    [Fact]
    public async Task StreamSimpleRoutesAVirtualModelAndClampsMaxTokens()
    {
        var (runtime, provider, virtualModel) = await WithRoutedVirtualModelAsync(sameProvider: false);

        var done = await runtime.StreamSimple(
            virtualModel, [], new Dictionary<string, object?> { ["maxTokens"] = 500L })
            .WaitForDoneAsync();

        Assert.Equal("stream-simple", done.Model);
        Assert.Equal(100L, provider.LastOptions!["maxTokens"]);
        Assert.Equal("medium", provider.LastOptions["reasoning"]);
    }

    [Fact]
    public async Task StreamSimpleKeepsTheCallerBudgetWhenTheRoutedModelHasNoLimit()
    {
        var physicalModel = Model("m", maxTokens: 0);
        var provider = new StubProvider
        {
            Id = "p",
            Models = [physicalModel],
            AuthOverride = ApiKeyAuth(),
        };
        var runtime = await WithProviderAsync(provider);
        await runtime.SetRuntimeApiKeyAsync("p", "physical-key");
        runtime.RegisterVirtualModel(Virtual("v", "vm",
            _ => Task.FromResult(new ModelRoute { Model = physicalModel, ThinkingLevel = "off" })));
        await SettleAsync(runtime);
        var virtualModel = runtime.GetModel("v", "vm");
        Assert.NotNull(virtualModel);

        await runtime.StreamSimple(virtualModel, [], new Dictionary<string, object?> { ["maxTokens"] = 500L })
            .WaitForDoneAsync();

        Assert.Equal(500L, provider.LastOptions!["maxTokens"]);
        Assert.Null(provider.LastOptions["reasoning"]);
    }

    [Fact]
    public async Task StreamSimpleDropsCallerCredentialsWhenRoutingToAnotherProvider()
    {
        var (runtime, provider, virtualModel) = await WithRoutedVirtualModelAsync(sameProvider: false);

        await runtime.StreamSimple(virtualModel, [], new Dictionary<string, object?>
        {
            ["apiKey"] = "caller-key",
            ["headers"] = new Dictionary<string, string?> { ["X-Caller"] = "1" },
            ["env"] = new Dictionary<string, string> { ["CALLER"] = "1" },
        }).WaitForDoneAsync();

        // The caller's credentials were resolved for the virtual model's provider, so they are not sent to
        // the other vendor: only the routed provider's own auth headers survive.
        Assert.Equal("physical-key", provider.LastOptions!["apiKey"]);
        var headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(provider.LastOptions["headers"]);
        Assert.Equal("a", headers["X-Auth"]);
        Assert.False(headers.ContainsKey("X-Caller"));

        // The key is always set (TS spreads it explicitly), but the caller's env did not survive either.
        Assert.True(provider.LastOptions.ContainsKey("env"));
        Assert.Null(provider.LastOptions["env"]);
    }

    [Fact]
    public async Task StreamSimplePassesCallerCredentialsThroughWhenTheProviderMatches()
    {
        var (runtime, provider, virtualModel) = await WithRoutedVirtualModelAsync(sameProvider: true);

        await runtime.StreamSimple(virtualModel, [], new Dictionary<string, object?>
        {
            ["apiKey"] = "caller-key",
            ["headers"] = new Dictionary<string, string?> { ["X-Caller"] = "1" },
        }).WaitForDoneAsync();

        Assert.Equal("caller-key", provider.LastOptions!["apiKey"]);
        var headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(provider.LastOptions["headers"]);
        Assert.Equal("1", headers["X-Caller"]);
    }

    [Fact]
    public async Task StreamSimpleReportsAnUnregisteredVirtualModel()
    {
        var runtime = await CreateAsync();

        var done = await runtime.StreamSimple(Model("vm", provider: "v", api: VirtualModels.Api), [])
            .WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Virtual model v/vm is not registered.", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamSimpleReportsARouteToANonPhysicalModel()
    {
        var runtime = await CreateAsync();
        runtime.RegisterVirtualModel(Virtual("v", "vm",
            _ => Task.FromResult(new ModelRoute { Model = Model("other", provider: "other"), ThinkingLevel = "off" })));
        await SettleAsync(runtime);
        var virtualModel = runtime.GetModel("v", "vm");
        Assert.NotNull(virtualModel);

        var done = await runtime.StreamSimple(virtualModel, []).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Virtual model v/vm routed to other/other, which is not a physical model.", done.ErrorMessage);
    }

    [Fact]
    public async Task StreamSimpleReportsARouteWithoutCredentials()
    {
        var (runtime, _, virtualModel) = await WithRoutedVirtualModelAsync(sameProvider: false, credentialed: false);

        var done = await runtime.StreamSimple(virtualModel, []).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, done.StopReason);
        Assert.Equal("Virtual model v/vm routed to p/m, which has no credentials.", done.ErrorMessage);
    }

    [Fact]
    public async Task ResolveModelReportsThePreviousPhysicalResponse()
    {
        var physicalModel = Model("m", reasoning: true);
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [physicalModel],
            AuthOverride = ApiKeyAuth(),
        });
        await runtime.SetRuntimeApiKeyAsync("p", "k");

        ModelRouteRequest? captured = null;
        runtime.RegisterVirtualModel(Virtual("v", "vm", request =>
        {
            captured = request;
            return Task.FromResult(new ModelRoute { Model = physicalModel, ThinkingLevel = "high" });
        }));
        await SettleAsync(runtime);
        var virtualModel = runtime.GetModel("v", "vm");
        Assert.NotNull(virtualModel);

        var route = await runtime.ResolveModelAsync(virtualModel, [
            Assistant("p", "m"),
            Assistant("p", "m", StopReason.Error),
            Assistant("p", "m", StopReason.Aborted),
        ], new ResolveModelOptions
        {
            Reason = VirtualModels.RouteReasons.User,
            ThinkingLevel = "medium",
        });

        Assert.NotNull(captured);
        Assert.NotNull(captured.Previous);
        Assert.Equal("m", captured.Previous.Model.Id);
        Assert.Equal(VirtualModels.RouteReasons.User, captured.Reason);
        Assert.Equal("high", route.ThinkingLevel);
        Assert.Equal("m", route.Model.Id);
    }

    [Fact]
    public async Task ResolveModelReportsNoPreviousWhenTheConversationHasNone()
    {
        var physicalModel = Model("m", reasoning: true);
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [physicalModel],
            AuthOverride = ApiKeyAuth(),
        });
        await runtime.SetRuntimeApiKeyAsync("p", "k");

        ModelRouteRequest? captured = null;
        runtime.RegisterVirtualModel(Virtual("v", "vm", request =>
        {
            captured = request;
            return Task.FromResult(new ModelRoute { Model = physicalModel, ThinkingLevel = "medium" });
        }));
        await SettleAsync(runtime);
        var virtualModel = runtime.GetModel("v", "vm");
        Assert.NotNull(virtualModel);

        await runtime.ResolveModelAsync(virtualModel, [Assistant("p", "m", StopReason.Error)],
            new ResolveModelOptions
            {
                Reason = VirtualModels.RouteReasons.Retry,
                ThinkingLevel = "medium",
                Failed = Assistant("p", "m", StopReason.Error, "low"),
            });

        Assert.NotNull(captured);
        Assert.Null(captured.Previous);
        Assert.NotNull(captured.Failed);
        Assert.Equal("low", captured.Failed.ThinkingLevel);
    }

    // ================================================================= refresh & errors

    [Fact]
    public async Task RefreshCollectsPerProviderErrors()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
            Refresh = _ => throw new InvalidOperationException("boom"),
        });
        await SettleAsync(runtime);

        var result = await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });

        Assert.False(result.Aborted);
        Assert.True(result.Errors.ContainsKey("p"));
        Assert.Contains("boom", result.Errors["p"].Message);
    }

    [Fact]
    public async Task RefreshReportsAbortedForACancelledSignal()
    {
        var runtime = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await runtime.RefreshAsync(new ModelsRefreshOptions
        {
            Providers = ["p"],
            Signal = cancelled.Token,
        });

        Assert.True(result.Aborted);
    }

    [Fact]
    public async Task RefreshReloadsModelsJsonAndReportsCompositionErrors()
    {
        using var file = new TempModelsFile("{\"providers\":{}}");
        var runtime = await CreateAsync(modelsPath: file.Path);
        Assert.Null(runtime.GetError());

        // A provider that names models but no api/baseUrl cannot be composed.
        file.Write("{\"providers\":{\"p\":{\"models\":[{\"id\":\"m\"}]}}}");
        await runtime.RefreshAsync();

        var error = runtime.GetError();
        Assert.NotNull(error);
        Assert.Contains("Provider \"p\":", error);
        Assert.Contains("no \"api\" specified", error);
    }

    [Fact]
    public async Task RefreshDropsAStaleCompositionErrorAfterTheConfigIsFixed()
    {
        using var file = new TempModelsFile("{\"providers\":{\"p\":{\"models\":[{\"id\":\"m\"}]}}}");
        var runtime = await CreateAsync(modelsPath: file.Path);
        await runtime.RefreshAsync();
        Assert.NotNull(runtime.GetError());

        file.Write("{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\","
            + "\"models\":[{\"id\":\"m\",\"name\":\"M\"}]}}}");
        await runtime.RefreshAsync();

        Assert.Null(runtime.GetError());
        Assert.Equal("M", runtime.GetModel("p", "m")?.Name);
    }

    [Fact]
    public async Task GetErrorJoinsTheConfigErrorWithTheCompositionAndAvailabilityErrors()
    {
        using var file = new TempModelsFile("{\"providers\":\"nope\"}");
        var runtime = await CreateAsync(modelsPath: file.Path);

        var error = runtime.GetError();

        Assert.NotNull(error);
        Assert.Contains("Invalid models.json schema", error);
    }

    [Fact]
    public async Task SetRuntimeApiKeyUpdatesTheSnapshot()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);

        await runtime.SetRuntimeApiKeyAsync("p", "runtime-key");

        Assert.True(runtime.HasConfiguredAuth("p"));
        Assert.Equal("m", Assert.Single(runtime.GetAvailableSnapshot()).Id);
        Assert.Equal(AuthStatusSource.Runtime, runtime.GetProviderAuthStatus("p").Source);
    }

    [Fact]
    public async Task RemoveRuntimeApiKeyDropsTheOverride()
    {
        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);
        await runtime.SetRuntimeApiKeyAsync("p", "runtime-key");
        Assert.True(runtime.HasConfiguredAuth("p"));

        await runtime.RemoveRuntimeApiKeyAsync("p");

        // The provider's own ambient auth still resolves, so it stays configured via the check.
        Assert.Equal(AuthStatusSource.Environment, runtime.GetProviderAuthStatus("p").Source);
        Assert.Empty(await runtime.ListCredentialsAsync());
    }

    [Fact]
    public async Task CredentialSynchronizationFailureIsWrapped()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = ApiKeyAuth(),
            Refresh = _ => throw new InvalidOperationException("sync-boom"),
        });
        await SettleAsync(runtime);

        var error = await Assert.ThrowsAsync<CredentialSynchronizationError>(
            () => runtime.SetRuntimeApiKeyAsync("p", "runtime-key"));

        Assert.Equal("p", error.ProviderId);
        Assert.Equal(CredentialSynchronizationOperation.SetRuntimeApiKey, error.Operation);
        Assert.Equal("runtime-key", Assert.IsType<Credential.ApiKey>(error.Credential).Key);
        Assert.Contains("sync-boom", error.InnerException?.Message);
        Assert.Equal(
            "Credential setRuntimeApiKey committed for p, but local synchronization failed",
            error.Message);
    }

    [Fact]
    public async Task CredentialOperationsAreSerializedPerProvider()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth
            {
                OAuth = new StubOAuthAuth
                {
                    OnLogin = async (_, _, _) =>
                    {
                        var current = Interlocked.Increment(ref concurrent);
                        maxConcurrent = Math.Max(maxConcurrent, current);
                        await Task.Delay(30);
                        Interlocked.Decrement(ref concurrent);
                        return new Credential.OAuth("refresh", "access", long.MaxValue);
                    },
                },
            },
        });
        await SettleAsync(runtime);

        var interaction = new ProviderAuthInteraction(new NoopInteraction());
        await Task.WhenAll(
            runtime.LoginAsync("p", CredentialKind.OAuth, interaction),
            runtime.LoginAsync("p", CredentialKind.OAuth, interaction));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task LoginPersistsTheCredentialAndMarksTheProviderConfigured()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth
            {
                OAuth = new StubOAuthAuth
                {
                    OnLogin = (_, _, _) => Task.FromResult(new Credential.OAuth("refresh", "access", long.MaxValue)),
                },
            },
        });
        await SettleAsync(runtime);

        var credential = await runtime.LoginAsync(
            "p", CredentialKind.OAuth, new ProviderAuthInteraction(new NoopInteraction()));

        Assert.Equal("access", Assert.IsType<Credential.OAuth>(credential).Access);
        Assert.True(runtime.IsUsingOAuth("p"));
        Assert.True(runtime.HasConfiguredAuth("p"));
        var listed = Assert.Single(await runtime.ListCredentialsAsync());
        Assert.Equal("p", listed.ProviderId);
    }

    [Fact]
    public async Task LogoutRemovesTheStoredCredential()
    {
        var runtime = await CreateAsync();
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = "p",
            Models = [Model("m")],
            AuthOverride = new ProviderAuth
            {
                OAuth = new StubOAuthAuth
                {
                    OnLogin = (_, _, _) => Task.FromResult(new Credential.OAuth("refresh", "access", long.MaxValue)),
                },
            },
        });
        await SettleAsync(runtime);
        await runtime.LoginAsync("p", CredentialKind.OAuth, new ProviderAuthInteraction(new NoopInteraction()));

        await runtime.LogoutAsync("p");

        Assert.Empty(await runtime.ListCredentialsAsync());
        Assert.False(runtime.IsUsingOAuth("p"));
    }

    // ================================================================= misc accessors

    [Fact]
    public async Task GetCompatibilityRequestConfigReadsModelsJson()
    {
        using var file = new TempModelsFile(
            "{\"providers\":{\"p\":{\"api\":\"stub-api\",\"baseUrl\":\"https://cfg.invalid\",\"authHeader\":true,"
            + "\"headers\":{\"X-Provider\":\"p\"},\"models\":[{\"id\":\"m\",\"name\":\"M\","
            + "\"headers\":{\"X-Model\":\"m\"}}]}}}");
        var runtime = await WithProviderAsync(
            new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() },
            modelsPath: file.Path);

        var model = runtime.GetModel("p", "m");
        Assert.NotNull(model);
        var config = runtime.GetCompatibilityRequestConfig(model);

        Assert.True(config.AuthHeader);
        Assert.Equal("p", config.Headers?["X-Provider"]);
        Assert.Equal("m", config.Headers?["X-Model"]);
    }

    [Fact]
    public async Task ModelTypeAccessorsFilterByType()
    {
        var runtime = await WithProviderAsync(new StubProvider
        {
            Id = "p",
            Models = [Model("m"), Model("i", type: ModelType.Image), Model("c", type: ModelType.Classifier)],
            AuthOverride = ApiKeyAuth(),
        });

        Assert.Equal("m", Assert.Single(runtime.GetModels("p")).Id);
        Assert.Equal("i", Assert.Single(runtime.GetModelsOfType(ModelType.Image, "p")).Id);
        Assert.Equal("c", runtime.GetModelOfType(ModelType.Classifier, "p", "c")?.Id);
        Assert.Equal(3, runtime.GetAllModels("p").Count);
    }

    [Fact]
    public async Task AvailableSnapshotFollowsTheConfiguredProviders()
    {
        // An unconfigured provider keeps its models out of the available snapshot.
        var unconfigured = await WithProviderAsync(new StubProvider
        {
            Id = "q",
            Models = [Model("m", provider: "q")],
            AuthOverride = new ProviderAuth { ApiKey = new UnconfiguredApiKeyAuth() },
        });
        Assert.Empty(unconfigured.GetAvailableSnapshot());

        var provider = new StubProvider { Id = "p", Models = [Model("m")], AuthOverride = ApiKeyAuth() };
        var runtime = await WithProviderAsync(provider);

        await runtime.RefreshAsync(new ModelsRefreshOptions { Providers = ["p"] });

        Assert.Equal("m", Assert.Single(runtime.GetAvailableSnapshot()).Id);
        Assert.Equal("m", Assert.Single(await runtime.GetAvailableAsync("p")).Id);
        Assert.Equal("m", Assert.Single(await runtime.GetAvailableAsync()).Id);
        Assert.Equal("m", Assert.Single(await runtime.GetAvailableOfTypeAsync(ModelType.Chat, "p")).Id);
        Assert.Equal("m", Assert.Single(await runtime.GetAllAvailableAsync("p")).Id);
        Assert.NotNull(await runtime.CheckAuthAsync("p"));
    }
}
