using System.Text.Json.Nodes;
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
/// Behaviour tests for <see cref="ProviderComposer"/> (port of TS <c>core/provider-composer.ts</c>).
/// </summary>
/// <remarks>
/// The TS module exports no helpers, so there is no reference test suite to mirror; these assertions pin
/// the observable rules of <c>composeModelProvider</c> instead — the five-layer stacking order, the
/// stream dispatch order, when <c>refreshModels</c> exists at all, "validate before publish", and the
/// auth/header composition.
/// </remarks>
public class CoreProviderComposerTests
{
    // ------------------------------------------------------------------ shared fakes

    private static ModelSpec Model(
        string id, string api = "api", ModelType type = ModelType.Chat, string provider = "p")
        => new()
        {
            Id = id,
            Name = id,
            Api = api,
            Provider = provider,
            BaseUrl = "https://built-in.invalid",
            Type = type,
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

    private sealed class StubApiKeyAuth(string name, string? resolvedKey = "ambient") : IApiKeyAuth
    {
        public string Name => name;

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential?.Key ?? resolvedKey },
                Source = name,
            });
    }

    private sealed class StubOAuthAuth : IOAuthAuth
    {
        public string Name => "stub-oauth";

        public bool IsSubscription => true;

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = "oauth-key" });
    }

    /// <summary>Base provider without image/classifier capability.</summary>
    private class StubProvider : IProvider
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public string? NameOverride { get; init; }

        public string? BaseUrlOverride { get; init; }

        public ProviderAuth? AuthOverride { get; init; }

        public Func<RefreshModelsContext, Task>? Refresh { get; init; }

        public string StreamMarker { get; init; } = "base-stream";

        public string StreamSimpleMarker { get; init; } = "base-stream-simple";

        public string Name => NameOverride ?? Id;

        public string? BaseUrl => BaseUrlOverride;

        public ProviderAuth? Auth => AuthOverride;

        public IReadOnlyList<ModelSpec> GetModels() => [.. Models.Where(model => model.Type == ModelType.Chat)];

        public IReadOnlyList<ModelSpec> GetAllModels() => Models;

        public Func<RefreshModelsContext, Task>? RefreshModels => Refresh;

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => MarkerStream(model, StreamMarker);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => MarkerStream(model, StreamSimpleMarker);
    }

    /// <summary>Base provider that also serves images and classification.</summary>
    private sealed class ImagingProvider : StubProvider, IImagesProvider, IClassifierProvider
    {
        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new AssistantImages
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = "base-images",
            });

        public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new ClassifierResult
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = "base-classify",
            });
    }

    private sealed class PublicationRecorder
    {
        public List<ModelsPublication> Publications { get; } = [];

        public Task<bool> Publish(ModelsPublication publication)
        {
            Publications.Add(publication);
            publication.Update?.Invoke();
            return Task.FromResult(true);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Load a <c>models.json</c> snapshot from an inline document.</summary>
    private static async Task<ModelConfig> LoadConfigAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-models-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var config = await ModelConfig.LoadAsync(path);
            Assert.Null(config.Error);
            return config;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task<ModelConfig> EmptyConfigAsync() => LoadConfigAsync("{\"providers\":{}}");

    private static ProviderChatModelConfig ChatConfig(
        string id,
        string? api = "ext-api",
        string? baseUrl = "https://extension.invalid",
        string name = "ext",
        IReadOnlyDictionary<string, string>? headers = null)
        => new()
        {
            Id = id,
            Name = name,
            Api = api,
            BaseUrl = baseUrl,
            Input = [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            Reasoning = false,
            ContextWindow = 1000,
            MaxTokens = 100,
            Headers = headers,
        };

    private static ProviderImageModelConfig ImageConfig(string id, string? api = "ext-images")
        => new()
        {
            Id = id,
            Name = id,
            Api = api,
            BaseUrl = "https://extension.invalid",
            Input = [ModelInput.Text],
            Output = [ModelInput.Text, ModelInput.Image],
            Cost = new ModelCostRates(0, 0, 0, 0),
        };

    private static IProvider Compose(
        string providerId, IProvider? baseProvider, ModelConfig config, ProviderConfigInput? extension)
        => ProviderComposer.ComposeModelProvider(providerId, baseProvider, config, extension);

    private static StubProvider BaseWithKey(string id, params ModelSpec[] models) => new()
    {
        Id = id,
        Models = models,
        AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Base API key") },
    };

    private static ProviderConfigInput Refreshable(
        IReadOnlyList<ProviderModelConfig> models,
        Func<RefreshModelsContext, Task<IReadOnlyList<ProviderModelConfig>>> refresh)
        => new() { Models = models, RefreshModels = refresh };

    /// <summary>Image generation lives on <see cref="IImagesProvider"/>, not on <see cref="IProvider"/>.</summary>
    private static Task<AssistantImages> GenerateImages(IProvider provider, ModelSpec model)
        => ((IImagesProvider)provider).GenerateImagesAsync(
            model, new ImagesContext { Input = [] }, null, CancellationToken.None);

    private static Task<ClassifierResult> Classify(IProvider provider, ModelSpec model)
        => ((IClassifierProvider)provider).ClassifyAsync(
            model,
            new ClassifierContext
            {
                State = new JsonObject(),
                Questions = new Dictionary<string, ClassifierQuestion>(),
            },
            null,
            CancellationToken.None);

    // ============================================================ catalog stacking order

    [Fact]
    public async Task ModelsJsonOverlaysBaseUrlAndCompatOnChatModelsOnly()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("i", type: ModelType.Image));
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"baseUrl\":\"https://cfg.invalid\",\"compat\":{\"supportsStore\":true}}}}");

        var models = Compose("p", baseProvider, config, null).GetAllModels();

        var chat = models.Single(model => model.Id == "a");
        Assert.Equal("https://cfg.invalid", chat.BaseUrl);
        Assert.True(chat.Compat?["supportsStore"]?.GetValue<bool>());

        // Non-chat models only get the baseUrl; compat is chat-only.
        var image = models.Single(model => model.Id == "i");
        Assert.Equal("https://cfg.invalid", image.BaseUrl);
        Assert.Null(image.Compat);
    }

    [Fact]
    public async Task ModelsJsonUpsertsCustomModelsInPlaceAndAppendsNewOnes()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("b"));
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"models\":[{\"id\":\"b\",\"name\":\"B2\"},{\"id\":\"c\"}]}}}");

        var models = Compose("p", baseProvider, config, null).GetModels();

        // `b` is replaced where it already sat; `c` is appended.
        Assert.Equal(new[] { "a", "b", "c" }, models.Select(model => model.Id));
        Assert.Equal("B2", models[1].Name);
        // `c` borrows api/baseUrl from the base catalog (id miss → openai-completions miss → first).
        Assert.Equal("api", models[2].Api);
        Assert.Equal("https://built-in.invalid", models[2].BaseUrl);
    }

    [Fact]
    public async Task ExtensionModelsReplaceTheWholeCatalogAndBorrowDefaultsById()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("b"));
        var extension = new ProviderConfigInput { Models = [ChatConfig("a", name: "A2")] };

        var models = Compose("p", baseProvider, await EmptyConfigAsync(), extension).GetModels();

        Assert.Equal(new[] { "a" }, models.Select(model => model.Id));
        Assert.Equal("A2", models[0].Name);
        Assert.Equal("ext-api", models[0].Api);
    }

    [Fact]
    public async Task ExtensionModelWithoutApiOrBaseUrlBorrowsTheBaseModelWithTheSameId()
    {
        var baseProvider = BaseWithKey("p", Model("a"));
        var extension = new ProviderConfigInput { Models = [ChatConfig("a", api: null, baseUrl: null)] };

        var models = Compose("p", baseProvider, await EmptyConfigAsync(), extension).GetModels();

        Assert.Equal("api", models[0].Api);
        Assert.Equal("https://built-in.invalid", models[0].BaseUrl);
    }

    [Fact]
    public async Task ExtensionWithoutModelsOnlyRewritesTheBaseUrl()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("i", type: ModelType.Image));
        var extension = new ProviderConfigInput { BaseUrl = "https://ext.invalid" };

        var models = Compose("p", baseProvider, await EmptyConfigAsync(), extension).GetAllModels();

        Assert.All(models, model => Assert.Equal("https://ext.invalid", model.BaseUrl));
    }

    [Fact]
    public async Task TopLevelModelOverridesApplyLastAndOnlyToChatModels()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("i", type: ModelType.Image));
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"modelOverrides\":{\"a\":{\"name\":\"final\",\"contextWindow\":777}}}}}");

        var models = Compose("p", baseProvider, config, null).GetAllModels();

        var chat = models.Single(model => model.Id == "a");
        Assert.Equal("final", chat.Name);
        Assert.Equal(777L, chat.ContextWindow);

        var image = models.Single(model => model.Id == "i");
        Assert.Equal("i", image.Name);
    }

    [Fact]
    public async Task OAuthModifyModelsIsChatOnlyAndRunsBeforeTopLevelOverrides()
    {
        var baseProvider = BaseWithKey("p", Model("a"), Model("i", type: ModelType.Image));
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"modelOverrides\":{\"a\":{\"contextWindow\":777}}}}}");
        var extension = new ProviderConfigInput
        {
            OAuth = new ExtensionOAuthConfig
            {
                Name = "ext-oauth",
                Login = (_, _, _) => throw new NotSupportedException(),
                RefreshToken = (_, _) => throw new NotSupportedException(),
                GetApiKey = _ => "oauth-key",
                ModifyModels = (chat, _) => [.. chat.Select(model => model with { Name = $"mod-{model.Id}" })],
            },
        };

        var composed = Compose("p", baseProvider, config, extension);

        // Nothing is modified yet: the hook only runs once an OAuth credential has been published.
        Assert.Equal(new[] { "a", "i" }, composed.GetAllModels().Select(model => model.Id));

        Assert.NotNull(composed.RefreshModels);
        var recorder = new PublicationRecorder();
        await composed.RefreshModels!(new RefreshModelsContext
        {
            Credential = new Credential.OAuth("refresh", "access", 0),
            Publish = recorder.Publish,
            AllowNetwork = true,
            Signal = CancellationToken.None,
        });

        var models = composed.GetAllModels();
        // Chat is rewritten and moved to the front; the image model passes through untouched.
        Assert.Equal(new[] { "a", "i" }, models.Select(model => model.Id));
        Assert.Equal("mod-a", models[0].Name);
        Assert.Equal("i", models[1].Name);
        // The top-level override still wins over the hook, and applies after it.
        Assert.Equal(777L, models[0].ContextWindow);
    }

    // ============================================================ stream dispatch order

    [Fact]
    public async Task StreamSimpleIsRoutedToTheExtensionOnlyForItsOwnApi()
    {
        var baseProvider = BaseWithKey("p", Model("a", api: "base-api"));
        var extension = new ProviderConfigInput
        {
            Api = "ext-api",
            ApiKey = "configured",
            StreamSimple = (model, _, _) => MarkerStream(model, "extension"),
        };

        var composed = Compose("p", baseProvider, await EmptyConfigAsync(), extension);

        Assert.Equal("extension", await MarkerOf(composed.StreamSimple(Model("a", api: "ext-api"), [], null)));
        Assert.Equal("base-stream-simple", await MarkerOf(composed.StreamSimple(Model("a", api: "base-api"), [], null)));
        Assert.Equal("base-stream", await MarkerOf(composed.Stream(Model("a", api: "base-api"), [], null)));
    }

    [Fact]
    public async Task StreamWithoutARegisteredApiProducesAnErrorStream()
    {
        var extension = new ProviderConfigInput
        {
            ApiKey = "configured",
            Models = [ChatConfig("m", api: "no-such-api", baseUrl: "https://x.invalid")],
        };
        var composed = Compose("p", null, await EmptyConfigAsync(), extension);
        var model = composed.GetModels().Single();

        var message = await composed.StreamSimple(model, [], null).WaitForDoneAsync();

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Equal("No API provider registered for api: no-such-api", message.ErrorMessage);
    }

    [Fact]
    public async Task ApiRegistryFallbackIsUsedWhenTheBaseDoesNotServeTheApi()
    {
        var apiId = $"test-api-{Guid.NewGuid():N}";
        var sourceId = $"test-source-{Guid.NewGuid():N}";
        ApiRegistry.RegisterApiProvider(new ApiProvider
        {
            Api = apiId,
            Stream = (model, _, _, _, _) => MarkerStream(model, "registry-stream"),
            StreamSimple = (model, _, _, _, _) => MarkerStream(model, "registry-stream-simple"),
        }, sourceId);

        try
        {
            var extension = new ProviderConfigInput
            {
                ApiKey = "configured",
                Models = [ChatConfig("m", api: apiId, baseUrl: "https://x.invalid")],
            };
            var composed = Compose("p", null, await EmptyConfigAsync(), extension);
            var model = composed.GetModels().Single();

            // Difference C52: both paths go through StreamSimple, as Compat.Stream does, because the C#
            // registry contract cannot carry the provider-boundary option dictionary as JSON.
            Assert.Equal("registry-stream-simple", await MarkerOf(composed.StreamSimple(model, [], null)));
            Assert.Equal("registry-stream-simple", await MarkerOf(composed.Stream(model, [], null)));
        }
        finally
        {
            ApiRegistry.UnregisterApiProviders(sourceId);
        }
    }

    // ============================================================ refreshModels presence

    [Fact]
    public async Task RefreshModelsExistsOnlyWhenSomeLayerCanRefresh()
    {
        var config = await EmptyConfigAsync();
        var plain = BaseWithKey("p", Model("a"));
        var refreshing = new StubProvider
        {
            Id = "p",
            Models = [Model("a")],
            AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Base API key") },
            Refresh = _ => Task.CompletedTask,
        };

        Assert.Null(Compose("p", plain, config, null).RefreshModels);
        Assert.NotNull(Compose("p", refreshing, config, null).RefreshModels);
        Assert.NotNull(Compose("p", plain, config, Refreshable([], _ =>
            Task.FromResult<IReadOnlyList<ProviderModelConfig>>([]))).RefreshModels);
        Assert.NotNull(Compose("p", plain, config, new ProviderConfigInput
        {
            OAuth = new ExtensionOAuthConfig
            {
                Name = "ext-oauth",
                Login = (_, _, _) => throw new NotSupportedException(),
                RefreshToken = (_, _) => throw new NotSupportedException(),
                GetApiKey = _ => "oauth-key",
                ModifyModels = (chat, _) => chat,
            },
        }).RefreshModels);
    }

    [Fact]
    public async Task RefreshValidatesTheNewListBeforePublishingIt()
    {
        var baseProvider = BaseWithKey("p");
        var extension = Refreshable(
            [ChatConfig("a")],
            // No api and no defaults anywhere: the synchronous list must be rejected.
            _ => Task.FromResult<IReadOnlyList<ProviderModelConfig>>([ChatConfig("broken", api: null, baseUrl: null)]));
        var composed = Compose("p", baseProvider, await EmptyConfigAsync(), extension);
        Assert.Equal(new[] { "a" }, composed.GetModels().Select(model => model.Id));

        var recorder = new PublicationRecorder();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composed.RefreshModels!(new RefreshModelsContext
            {
                Publish = recorder.Publish,
                AllowNetwork = true,
                Signal = CancellationToken.None,
            }));

        Assert.Contains("no \"api\" specified", error.Message, StringComparison.Ordinal);
        // The old catalog is intact: the failure happened before the new list was adopted.
        Assert.Equal(new[] { "a" }, composed.GetModels().Select(model => model.Id));
    }

    [Fact]
    public async Task RefreshAdoptsTheNewListOnlyAfterValidationSucceeds()
    {
        var extension = Refreshable(
            [ChatConfig("a")],
            _ => Task.FromResult<IReadOnlyList<ProviderModelConfig>>([ChatConfig("b", name: "B")]));
        var composed = Compose("p", BaseWithKey("p", Model("a")), await EmptyConfigAsync(), extension);

        var recorder = new PublicationRecorder();
        await composed.RefreshModels!(new RefreshModelsContext
        {
            Publish = recorder.Publish,
            AllowNetwork = true,
            Signal = CancellationToken.None,
        });

        Assert.Single(recorder.Publications);
        Assert.Equal(new[] { "b" }, composed.GetModels().Select(model => model.Id));
    }

    [Fact]
    public async Task RefreshReturnsBeforePublishingWhenTheSignalIsAlreadyCancelled()
    {
        var extension = Refreshable([ChatConfig("a")],
            _ => Task.FromResult<IReadOnlyList<ProviderModelConfig>>([ChatConfig("b")]));
        var composed = Compose("p", BaseWithKey("p", Model("a")), await EmptyConfigAsync(), extension);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var recorder = new PublicationRecorder();
        await composed.RefreshModels!(new RefreshModelsContext
        {
            Publish = recorder.Publish,
            AllowNetwork = true,
            Signal = cancelled.Token,
        });

        Assert.Empty(recorder.Publications);
        Assert.Equal(new[] { "a" }, composed.GetModels().Select(model => model.Id));
    }

    // ============================================================ images / classification

    [Fact]
    public async Task ImagesPreferTheExtensionThenTheBaseThenReportAnError()
    {
        var extension = new ProviderConfigInput
        {
            ApiKey = "configured",
            Images = new Dictionary<string, ProviderImages>
            {
                ["ext-api"] = new ProviderImages
                {
                    GenerateImages = (model, _, _, _) => Task.FromResult(new AssistantImages
                    {
                        Api = model.Api,
                        Provider = model.Provider,
                        Model = "extension-images",
                    }),
                },
            },
        };
        var imaging = new ImagingProvider
        {
            Id = "p",
            Models = [Model("m", api: "base-api")],
            AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Base API key") },
        };
        var composed = Compose("p", imaging, await EmptyConfigAsync(), extension);

        // Extension implementation for its own api.
        Assert.Equal("extension-images", (await GenerateImages(composed, Model("m", api: "ext-api"))).Model);

        // No extension entry for the api → the base implementation.
        Assert.Equal("base-images", (await GenerateImages(composed, Model("m", api: "base-api"))).Model);

        // Extension declares images, but not for this api, and the base has none.
        var withoutBase = Compose("p", BaseWithKey("p", Model("m", api: "other-api")),
            await EmptyConfigAsync(), extension);
        var missing = await GenerateImages(withoutBase, Model("m", api: "other-api"));
        Assert.Equal(ImagesStopReason.Error, missing.StopReason);
        Assert.Equal("Provider p has no image implementation for \"other-api\"", missing.ErrorMessage);
    }

    [Fact]
    public async Task ImagesWithoutAnyImplementationReportTheModelsLayerMessage()
    {
        var composed = Compose("p", BaseWithKey("p", Model("m")), await EmptyConfigAsync(), null);

        var result = await GenerateImages(composed, Model("m"));

        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Equal("Provider p does not support image generation", result.ErrorMessage);
    }

    [Fact]
    public async Task ClassificationFollowsTheSamePreferenceOrder()
    {
        var extension = new ProviderConfigInput
        {
            ApiKey = "configured",
            Classifiers = new Dictionary<string, ProviderClassifier>
            {
                ["ext-api"] = new ProviderClassifier
                {
                    Classify = (model, _, _, _) => Task.FromResult(new ClassifierResult
                    {
                        Api = model.Api,
                        Provider = model.Provider,
                        Model = "extension-classify",
                    }),
                },
            },
        };
        var imaging = new ImagingProvider
        {
            Id = "p",
            Models = [Model("m", api: "base-api")],
            AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Base API key") },
        };
        var composed = Compose("p", imaging, await EmptyConfigAsync(), extension);

        Assert.Equal("extension-classify", (await Classify(composed, Model("m", api: "ext-api"))).Model);
        Assert.Equal("base-classify", (await Classify(composed, Model("m", api: "base-api"))).Model);

        var plain = Compose("p", BaseWithKey("p", Model("m")), await EmptyConfigAsync(), null);
        var missing = await Classify(plain, Model("m"));
        Assert.Equal(ClassifierStopReason.Error, missing.StopReason);
        Assert.Equal("Provider p does not support classification", missing.ErrorMessage);
    }

    // ============================================================ auth composition

    [Fact]
    public async Task ComposeWithoutAnyAuthExposesAnApiKeyMethodThatReportsNotConfigured()
    {
        // TS fabricates an api-key method whenever the provider is not OAuth-only, even with no key
        // anywhere — the "no authentication method configured" guard is unreachable in practice.
        var composed = Compose("p", null, await EmptyConfigAsync(), null);

        var auth = composed.Auth;
        Assert.NotNull(auth?.ApiKey);
        Assert.Equal("API key", auth!.ApiKey!.Name);
        Assert.True(auth.ApiKey.HasLogin);
        Assert.Null(auth.OAuth);
        Assert.Null(await auth.ApiKey.ResolveAsync(null, DefaultAuthContext.Instance));
    }

    [Fact]
    public async Task ApiKeyAuthInheritsTheBaseNameAndLoginMethod()
    {
        var composed = Compose("p", BaseWithKey("p", Model("a")), await EmptyConfigAsync(), null);

        var auth = composed.Auth;
        Assert.NotNull(auth?.ApiKey);
        Assert.Equal("Base API key", auth!.ApiKey!.Name);
        Assert.True(auth.ApiKey.HasLogin);
        Assert.Null(auth.OAuth);
    }

    [Fact]
    public async Task OAuthOnlyProvidersDoNotGetAFabricatedApiKeyLogin()
    {
        var baseProvider = new StubProvider
        {
            Id = "p",
            Models = [Model("a")],
            AuthOverride = new ProviderAuth { OAuth = new StubOAuthAuth() },
        };

        var composed = Compose("p", baseProvider, await EmptyConfigAsync(), null);

        Assert.Null(composed.Auth?.ApiKey);
        Assert.NotNull(composed.Auth?.OAuth);
        Assert.Equal("stub-oauth", composed.Auth!.OAuth!.Name);
    }

    [Fact]
    public async Task ConfiguredApiKeyResolvesThroughTheEnvironmentAndAppliesConfiguredHeaders()
    {
        // OAuth-only base: the configured key is resolved by the composed api-key auth itself, so the
        // reported source is the configured one rather than an inherited provider's.
        var baseProvider = new StubProvider
        {
            Id = "p",
            Models = [Model("a")],
            AuthOverride = new ProviderAuth { OAuth = new StubOAuthAuth() },
        };
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"apiKey\":\"$TEST_API_KEY\",\"headers\":{\"X-A\":\"$A_VAR\"}}}}");
        var composed = Compose("p", baseProvider, config, null);

        var ctx = new OverlayEnvAuthContext(DefaultAuthContext.Instance,
            new Dictionary<string, string> { ["TEST_API_KEY"] = "secret", ["A_VAR"] = "aval" });
        var result = await composed.Auth!.ApiKey!.ResolveAsync(null, ctx);

        Assert.NotNull(result);
        Assert.Equal("secret", result!.Auth.ApiKey);
        Assert.Equal("configured API key", result.Source);
        Assert.Equal("aval", result.Auth.Headers?["X-A"]);
    }

    [Fact]
    public async Task AuthHeaderRequiresAResolvedApiKey()
    {
        var baseProvider = new StubProvider
        {
            Id = "p",
            Models = [Model("a")],
            // Resolves successfully but without a key: the shape `authHeader` cannot express.
            AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Ambient only", resolvedKey: null) },
        };
        var composed = Compose("p", baseProvider, await EmptyConfigAsync(),
            new ProviderConfigInput { AuthHeader = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            composed.Auth!.ApiKey!.ResolveAsync(null, DefaultAuthContext.Instance));
        Assert.Equal("authHeader requires a resolved API key", error.Message);
    }

    [Fact]
    public async Task AuthHeaderAddsTheBearerTokenToTheResolvedAuth()
    {
        var extension = new ProviderConfigInput { AuthHeader = true, ApiKey = "configured" };
        var composed = Compose("p", BaseWithKey("p", Model("a")), await EmptyConfigAsync(), extension);

        var result = await composed.Auth!.ApiKey!.ResolveAsync(null, DefaultAuthContext.Instance);

        Assert.Equal("Bearer configured", result!.Auth.Headers?["Authorization"]);
    }

    // ============================================================ header + status helpers

    [Fact]
    public async Task ResolveConfiguredModelHeadersMergesOverridesDefinitionAndExtension()
    {
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{"
            + "\"modelOverrides\":{\"a\":{\"headers\":{\"X-Override\":\"1\",\"X-Shared\":\"override\"}}},"
            + "\"models\":[{\"id\":\"a\",\"headers\":{\"X-Def\":\"2\",\"X-Shared\":\"definition\"}}]}}}");
        var extension = new ProviderConfigInput
        {
            Models = [ChatConfig("a", headers: new Dictionary<string, string>
            {
                ["X-Ext"] = "3",
                ["X-Shared"] = "extension",
            })],
        };

        var headers = ProviderComposer.ResolveConfiguredModelHeaders(
            Model("a"), config.GetProvider("p"), extension);

        Assert.NotNull(headers);
        Assert.Equal("1", headers!["X-Override"]);
        Assert.Equal("2", headers["X-Def"]);
        Assert.Equal("3", headers["X-Ext"]);
        // Extension declarations win over models.json definitions, which win over overrides.
        Assert.Equal("extension", headers["X-Shared"]);
    }

    [Fact]
    public void ResolveConfiguredModelHeadersIsNullWhenNothingIsConfigured()
        => Assert.Null(ProviderComposer.ResolveConfiguredModelHeaders(Model("a"), null, null));

    [Fact]
    public async Task ResolveCompatibilityRequestConfigLetsConfiguredHeadersWin()
    {
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"headers\":{\"X-C\":\"c\",\"X-Shared\":\"configured\"},\"authHeader\":true}}}");
        var model = Model("a") with
        {
            Headers = new Dictionary<string, string> { ["X-M"] = "m", ["X-Shared"] = "model" },
        };

        var result = ProviderComposer.ResolveCompatibilityRequestConfig(model, config.GetProvider("p"), null);

        Assert.True(result.AuthHeader);
        Assert.Equal("m", result.Headers?["X-M"]);
        Assert.Equal("c", result.Headers?["X-C"]);
        Assert.Equal("configured", result.Headers?["X-Shared"]);
    }

    [Fact]
    public void ResolveCompatibilityRequestConfigIsNullWhenNeitherSideHasHeaders()
    {
        var result = ProviderComposer.ResolveCompatibilityRequestConfig(Model("a"), null, null);

        Assert.Null(result.Headers);
        Assert.False(result.AuthHeader);
    }

    [Fact]
    public async Task ConfiguredRequestAuthStatusReportsEverySource()
    {
        Assert.Null(ProviderComposer.ConfiguredRequestAuthStatus(null, null));

        var command = await LoadConfigAsync("{\"providers\":{\"p\":{\"apiKey\":\"!echo key\"}}}");
        var commandStatus = ProviderComposer.ConfiguredRequestAuthStatus(command.GetProvider("p"), null);
        Assert.True(commandStatus!.Configured);
        Assert.Equal(AuthStatusSource.ModelsJsonCommand, commandStatus.Source);

        Environment.SetEnvironmentVariable("PI_UNSET_TEST_VAR", null);
        var unset = await LoadConfigAsync("{\"providers\":{\"p\":{\"apiKey\":\"$PI_UNSET_TEST_VAR\"}}}");
        var unsetStatus = ProviderComposer.ConfiguredRequestAuthStatus(unset.GetProvider("p"), null);
        Assert.False(unsetStatus!.Configured);
        Assert.Null(unsetStatus.Source);

        var configured = await LoadConfigAsync("{\"providers\":{\"p\":{\"apiKey\":\"$PI_SET_TEST_VAR\"}}}");
        // `isConfigValueConfigured` falls back to the process environment.
        Environment.SetEnvironmentVariable("PI_SET_TEST_VAR", "value");
        try
        {
            var envStatus = ProviderComposer.ConfiguredRequestAuthStatus(configured.GetProvider("p"), null);
            Assert.True(envStatus!.Configured);
            Assert.Equal(AuthStatusSource.Environment, envStatus.Source);
            Assert.Equal("PI_SET_TEST_VAR", envStatus.Label);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_SET_TEST_VAR", null);
        }

        var literal = await LoadConfigAsync("{\"providers\":{\"p\":{\"apiKey\":\"sk-literal\"}}}");
        var literalStatus = ProviderComposer.ConfiguredRequestAuthStatus(literal.GetProvider("p"), null);
        Assert.True(literalStatus!.Configured);
        Assert.Equal(AuthStatusSource.ModelsJsonKey, literalStatus.Source);

        var fallbackStatus = ProviderComposer.ConfiguredRequestAuthStatus(
            literal.GetProvider("p"), new ProviderConfigInput { ApiKey = "sk-extension" });
        Assert.Equal(AuthStatusSource.Fallback, fallbackStatus!.Source);
    }

    // ============================================================ validation

    [Fact]
    public void ValidateExtensionProviderRequiresApiWhenStreamSimpleIsRegistered()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProviderComposer.ValidateExtensionProvider(
            "p", null, null, new ProviderConfigInput { StreamSimple = (model, _, _) => MarkerStream(model, "x") }));

        Assert.Equal("Provider p: \"api\" is required when registering streamSimple.", error.Message);
    }

    [Fact]
    public async Task ValidateExtensionProviderRunsTheWholeStack()
    {
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{\"models\":[{\"id\":\"m\",\"contextWindow\":0}]}}}");

        var error = Assert.Throws<InvalidOperationException>(() => ProviderComposer.ValidateExtensionProvider(
            "p", BaseWithKey("p", Model("m")), config.GetProvider("p"), new ProviderConfigInput
            {
                Models = [ChatConfig("m")],
            }));

        Assert.Contains("invalid contextWindow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsJsonRejectsOauthWithoutBaseUrl()
    {
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{\"oauth\":\"radius\"}}}");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Compose("p", BaseWithKey("p", Model("a")), config, null));

        Assert.Equal("Provider p: \"baseUrl\" is required when \"oauth\" is set.", error.Message);
    }

    [Fact]
    public async Task ModelsJsonRejectsAnEntryThatSpecifiesNothing()
    {
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{}}}");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Compose("p", BaseWithKey("p", Model("a")), config, null));

        Assert.Equal(
            "Provider p: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".",
            error.Message);
    }

    [Fact]
    public async Task ModelsJsonModelWithoutApiReportsTheProviderLevelHint()
    {
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{\"apiKey\":\"sk-x\",\"models\":[{\"id\":\"m\"}]}}}");

        var error = Assert.Throws<InvalidOperationException>(() => Compose("p", null, config, null));

        Assert.Equal("Provider p, model m: no \"api\" specified. Set at provider or model level.", error.Message);
    }

    [Fact]
    public async Task ModelsJsonModelWithoutBaseUrlIsRejected()
    {
        var config = await LoadConfigAsync(
            "{\"providers\":{\"p\":{\"apiKey\":\"sk-x\",\"api\":\"x\",\"models\":[{\"id\":\"m\"}]}}}");

        var error = Assert.Throws<InvalidOperationException>(() => Compose("p", null, config, null));

        Assert.Equal("Provider p: \"baseUrl\" is required when defining custom models.", error.Message);
    }

    [Fact]
    public async Task ModelsJsonModelWithoutPositiveMaxTokensIsRejected()
    {
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{\"models\":[{\"id\":\"m\",\"maxTokens\":-1}]}}}");

        var error = Assert.Throws<InvalidOperationException>(() =>
            Compose("p", BaseWithKey("p", Model("m")), config, null));

        Assert.Equal("Provider p, model m: invalid maxTokens", error.Message);
    }

    [Fact]
    public async Task ExtensionModelWithoutApiReportsTheModelLevelHint()
    {
        var config = await EmptyConfigAsync();
        var error = Assert.Throws<InvalidOperationException>(() => Compose(
            "p",
            null,
            config,
            new ProviderConfigInput { Models = [ChatConfig("m", api: null, baseUrl: "https://x.invalid")] }));

        Assert.Equal(
            "Provider p, model m: no \"api\" specified. Set it at model level or provider level.", error.Message);
    }

    [Fact]
    public async Task ExtensionImageModelWithoutApiOmitsTheProviderLevelHint()
    {
        var config = await EmptyConfigAsync();
        var error = Assert.Throws<InvalidOperationException>(() => Compose(
            "p", null, config, new ProviderConfigInput { Models = [ImageConfig("m", api: null)] }));

        Assert.Equal("Provider p, model m: no \"api\" specified. Set it at model level.", error.Message);
    }

    [Fact]
    public async Task ExtensionImageModelsCarryTheirOutputModalities()
    {
        var extension = new ProviderConfigInput { Models = [ImageConfig("img")] };

        var models = Compose("p", null, await EmptyConfigAsync(), extension).GetAllModels();

        var image = models.Single();
        Assert.Equal(ModelType.Image, image.Type);
        Assert.Equal("ext-images", image.Api);
        var output = Assert.IsType<JsonArray>(image.Extra?["output"]);
        Assert.Equal(new[] { "text", "image" }, output.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task ProviderNameFallsBackThroughConfigAndBase()
    {
        var baseProvider = new StubProvider
        {
            Id = "p",
            Models = [Model("a")],
            NameOverride = "Base name",
            AuthOverride = new ProviderAuth { ApiKey = new StubApiKeyAuth("Base API key") },
        };
        var config = await LoadConfigAsync("{\"providers\":{\"p\":{\"baseUrl\":\"https://cfg.invalid\"}}}");

        Assert.Equal("Base name", Compose("p", baseProvider, await EmptyConfigAsync(), null).Name);
        Assert.Equal("Base name", Compose("p", baseProvider, config, null).Name);
        Assert.Equal("Extension name", Compose("p", baseProvider, config,
            new ProviderConfigInput { Name = "Extension name" }).Name);
        Assert.Equal("p", Compose("p", null, await EmptyConfigAsync(),
            new ProviderConfigInput { ApiKey = "k" }).Name);
    }
}
