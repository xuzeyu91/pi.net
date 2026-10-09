using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Behaviour tests for the 4b runtime helpers: <see cref="RuntimeCredentials"/> and
/// <see cref="RemoteCatalogProvider"/>. Both are ports of TS modules that carry observable ordering and
/// caching rules, so the assertions mirror the TS semantics rather than any C#-specific shortcut.
/// </summary>
public class CoreRuntimeTests
{
    // ------------------------------------------------------------------ shared fakes

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, Credential> _entries = new(StringComparer.Ordinal);

        public void Seed(string providerId, Credential credential) => _entries[providerId] = credential;

        public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.GetValueOrDefault(providerId));

        public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CredentialInfo>>(
                [.. _entries.Select(entry => new CredentialInfo(entry.Key, entry.Value.Kind))]);

        public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        {
            _entries.Remove(providerId);
            return Task.CompletedTask;
        }
    }

    private static ModelSpec Model(string id, string provider = "p", ModelType type = ModelType.Chat)
        => new()
        {
            Id = id,
            Name = id,
            Api = "api",
            Provider = provider,
            BaseUrl = "https://example.invalid",
            Type = type,
        };

    /// <summary>Minimal provider: a fixed catalog plus the image/classifier capability switches.</summary>
    private sealed class StubProvider(
        IReadOnlyList<ModelSpec> models, bool images = false, bool classifier = false)
        : IProvider, IImagesProvider, IClassifierProvider
    {
        public string Id => "p";

        public string Name => "p";

        public string? BaseUrl => null;

        public IReadOnlyList<ModelSpec> GetModels() => models.Where(m => m.Type == ModelType.Chat).ToList();

        public IReadOnlyList<ModelSpec> GetAllModels() => models;

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
            => images
                ? Task.FromResult(new AssistantImages
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                })
                : throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support image generation");

        public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
            => classifier
                ? Task.FromResult(new ClassifierResult
                {
                    Api = model.Api,
                    Provider = model.Provider,
                    Model = model.Id,
                })
                : throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support classification");
    }

    /// <summary>Records every publication and optionally reports the generation as superseded.</summary>
    private sealed class PublicationRecorder
    {
        public List<ModelsPublication> Publications { get; } = [];

        public bool Accepted { get; set; } = true;

        public Task<bool> Publish(ModelsPublication publication)
        {
            Publications.Add(publication);
            // The driver only runs `update` on the accepted path (models.ts publishProviderModels).
            if (Accepted) publication.Update?.Invoke();
            return Task.FromResult(Accepted);
        }

        public ModelsStoreEntry? LastPersist => Publications.LastOrDefault()?.Persist;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? body = null,
        (string Name, string Value)[]? headers = null)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        foreach (var (name, value) in headers ?? [])
        {
            // Model the real stack: entity headers such as Last-Modified only fit HttpContentHeaders,
            // while HttpResponseHeaders rejects them.
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        return response;
    }

    private static RefreshModelsContext Context(PublicationRecorder recorder, ModelsStoreEntry? stored = null,
        bool allowNetwork = false, bool? force = null)
        => new()
        {
            Stored = stored,
            Publish = recorder.Publish,
            AllowNetwork = allowNetwork,
            Force = force,
            Signal = CancellationToken.None,
        };

    // ------------------------------------------------------------------ RuntimeCredentials

    [Fact]
    public async Task RuntimeApiKeyOverridesTheStoredCredential()
    {
        var store = new MemoryCredentialStore();
        store.Seed("p", new Credential.ApiKey("stored"));
        var credentials = new RuntimeCredentials(store);

        Assert.Equal("stored", Assert.IsType<Credential.ApiKey>(await credentials.ReadAsync("p")).Key);

        credentials.SetRuntimeApiKey("p", "runtime");
        Assert.True(credentials.HasRuntimeApiKey("p"));
        Assert.Equal("runtime", Assert.IsType<Credential.ApiKey>(await credentials.ReadAsync("p")).Key);

        credentials.RemoveRuntimeApiKey("p");
        Assert.False(credentials.HasRuntimeApiKey("p"));
        Assert.Equal("stored", Assert.IsType<Credential.ApiKey>(await credentials.ReadAsync("p")).Key);
    }

    [Fact]
    public async Task RuntimeApiKeyIsListedInPlaceAndNewProvidersAppend()
    {
        var store = new MemoryCredentialStore();
        store.Seed("a", new Credential.OAuth("r", "access", 0));
        store.Seed("b", new Credential.ApiKey("stored-b"));
        var credentials = new RuntimeCredentials(store);
        credentials.SetRuntimeApiKey("b", "runtime-b");
        credentials.SetRuntimeApiKey("c", "runtime-c");

        var listed = await credentials.ListAsync();

        // Stored order first, `b` replaced in place (still index 1), `c` appended.
        Assert.Equal(new[] { "a", "b", "c" }, listed.Select(entry => entry.ProviderId));
        Assert.Equal(CredentialKind.OAuth, listed[0].Type);
        Assert.Equal(CredentialKind.ApiKey, listed[1].Type);
        Assert.Equal(CredentialKind.ApiKey, listed[2].Type);
    }

    [Fact]
    public async Task DeleteDropsBothTheStoredCredentialAndTheOverride()
    {
        var store = new MemoryCredentialStore();
        store.Seed("p", new Credential.ApiKey("stored"));
        var credentials = new RuntimeCredentials(store);
        credentials.SetRuntimeApiKey("p", "runtime");

        await credentials.DeleteAsync("p");

        Assert.False(credentials.HasRuntimeApiKey("p"));
        Assert.Null(await credentials.ReadAsync("p"));
    }

    [Fact]
    public async Task ReadsAndDeletesObserveAnAlreadyCancelledSignal()
    {
        var credentials = new RuntimeCredentials(new MemoryCredentialStore());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credentials.ReadAsync("p", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credentials.ListAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credentials.DeleteAsync("p", cancelled.Token));
    }

    // ------------------------------------------------------------------ RemoteCatalogProvider

    private static ModelsStoreEntry StoredEntry(params ModelSpec[] models)
        => new() { Models = models, LastModified = 1_000, CheckedAt = 1_000, Etag = "\"v1\"" };

    [Fact]
    public void OverlayIsEmptyWithoutAStoredEntry()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));

        Assert.Equal(new[] { "builtin" }, provider.GetModels().Select(model => model.Id));
    }

    [Fact]
    public async Task OverlayAddsStoredModelsAndMergesByTypeAndId()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var recorder = new PublicationRecorder();

        // A restore-only refresh (no network) seeds the overlay from the stored catalog.
        await provider.RefreshModels!(Context(recorder, StoredEntry(
            Model("dynamic", type: ModelType.Image),
            Model("builtin"), // same type+id as the baseline → replaces it, keeping its position
            Model("chatty"))));

        // getModels() is chat-only; getAllModels() keeps every type. Merge keeps first-seen order, so the
        // image entry lands right after the baseline it was listed behind.
        Assert.Equal(new[] { "builtin", "chatty" }, provider.GetModels().Select(model => model.Id));
        Assert.Equal(new[] { "builtin", "dynamic", "chatty" },
            provider.GetAllModels().Select(model => model.Id));
    }

    [Fact]
    public async Task OverlayIsDroppedWhenTheStoredCatalogIsNotNewerThanTheLocalOne()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]), localGeneratedAt: 1_000);
        var recorder = new PublicationRecorder();

        // lastModified == localGeneratedAt → not newer.
        await provider.RefreshModels!(Context(recorder, StoredEntry(Model("dynamic"))));

        Assert.Equal(new[] { "builtin" }, provider.GetModels().Select(model => model.Id));
    }

    [Fact]
    public async Task ARejectedPublicationLeavesTheOverlayUntouched()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var recorder = new PublicationRecorder { Accepted = false };

        await provider.RefreshModels!(Context(recorder, StoredEntry(Model("dynamic"))));

        Assert.Equal(new[] { "builtin" }, provider.GetModels().Select(model => model.Id));
    }

    [Fact]
    public async Task AStoredCatalogInsideTheFreshnessWindowSkipsTheRequest()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "[]"));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            await provider.RefreshModels!(Context(recorder, new ModelsStoreEntry
            {
                Models = [Model("dynamic")],
                LastModified = 1_000,
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Etag = "\"v1\"",
            }, allowNetwork: true));

            Assert.Empty(handler.Requests);
            Assert.Single(recorder.Publications); // only the offline restore publish
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task AForcedRefreshSendsTheTypeShardAndTheValidatorAndPersistsTheCatalog()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        const string body = """
            {"models":[{"id":"remote-a","name":"Remote A","type":"chat"},
                       {"id":"remote-img","name":"Remote Img","type":"image"},
                       {"id":"remote-video","name":"Remote Video","type":"video"},
                       {"name":"no id"}]}
            """;
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, body,
            [("etag", "\"v2\""), ("last-modified", "Wed, 21 Oct 2015 07:28:00 GMT")]));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            await provider.RefreshModels!(Context(recorder, StoredEntry(Model("dynamic")),
                allowNetwork: true, force: true));

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://pi.dev/api/models/providers/p?types=chat%2Cimage%2Cclassifier",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("\"v1\"", request.Headers.GetValues("if-none-match").Single());

            // The video entry is dropped; the entry without an id is dropped.
            Assert.Equal(new[] { "builtin", "remote-a" }, provider.GetModels().Select(model => model.Id));
            Assert.Equal(new[] { "builtin", "remote-a", "remote-img" },
                provider.GetAllModels().Select(model => model.Id));

            var persisted = recorder.LastPersist!;
            Assert.Equal("\"v2\"", persisted.Etag);
            Assert.Equal(1_445_412_480_000L, persisted.LastModified); // 2015-10-21T07:28:00Z
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task ANotModifiedResponseOnlyMovesTheFreshnessWindow()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var stored = StoredEntry(Model("dynamic"));
        var handler = new StubHandler(_ => Response((HttpStatusCode)304));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            await provider.RefreshModels!(Context(recorder, stored, allowNetwork: true, force: true));

            var persisted = recorder.LastPersist!;
            Assert.Equal("\"v1\"", persisted.Etag);
            Assert.Equal(1_000, persisted.LastModified);
            Assert.True(persisted.CheckedAt > stored.CheckedAt);
            // The overlay still comes from the stored entry.
            Assert.Equal(new[] { "builtin", "dynamic" }, provider.GetModels().Select(model => model.Id));
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task ANotFoundResponseClearsTheValidatorAndDropsTheStoredOverlay()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var handler = new StubHandler(_ => Response(HttpStatusCode.NotFound));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            await provider.RefreshModels!(Context(recorder, StoredEntry(Model("dynamic")),
                allowNetwork: true, force: true));

            var persisted = recorder.LastPersist!;
            Assert.Null(persisted.Etag);
            Assert.Equal(0, persisted.LastModified);
            // TS publishes no `update` on this branch, so the in-memory overlay survives this run while the
            // zeroed lastModified keeps it out of the next process's restore.
            Assert.Equal(new[] { "builtin", "dynamic" }, provider.GetModels().Select(model => model.Id));
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task ATransientFailureKeepsTheValidatorAndRaisesAPlainError()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("builtin")]));
        var handler = new StubHandler(_ => Response(HttpStatusCode.Forbidden));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.RefreshModels!(Context(recorder, StoredEntry(Model("dynamic")),
                    allowNetwork: true, force: true)));

            Assert.Equal("Model catalog request failed for p: 403", error.Message);
            Assert.Equal("\"v1\"", recorder.LastPersist!.Etag); // the validator survives
            // A plain error, so the refresh driver wraps it as a model_source failure.
            Assert.IsNotType<ModelsError>(error);
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Theory]
    [InlineData("""[{"id":"a"}]""")]
    [InlineData("""{"models":[{"id":"a"}]}""")]
    [InlineData("""{"a":{"id":"a"}}""")]
    public async Task CatalogBodiesAreAcceptedInAllThreeShapes(string body)
    {
        var provider = new RemoteCatalogProvider(new StubProvider([]));
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, body));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            await provider.RefreshModels!(Context(recorder, allowNetwork: true, force: true));

            Assert.Equal(new[] { "a" }, provider.GetModels().Select(model => model.Id));
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task AMalformedCatalogRaisesAPlainError()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([]));
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "\"just a string\""));
        ManagementHttp.HandlerOverride = handler;
        try
        {
            var recorder = new PublicationRecorder();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.RefreshModels!(Context(recorder, allowNetwork: true, force: true)));

            Assert.Equal("Invalid model catalog for provider \"p\"", error.Message);
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }
    }

    [Fact]
    public async Task CapabilitiesAreForwardedOnlyWhenTheInnerProviderHasThem()
    {
        var withImages = new RemoteCatalogProvider(new StubProvider([Model("m")], images: true));
        var image = await withImages.GenerateImagesAsync(Model("m"), new ImagesContext { Input = [] },
            null, CancellationToken.None);
        Assert.Equal("m", image.Model);

        var withoutImages = new RemoteCatalogProvider(new StubProvider([Model("m")]));
        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            withoutImages.GenerateImagesAsync(Model("m"), new ImagesContext { Input = [] },
                null, CancellationToken.None));
        Assert.Equal(ModelsErrorCode.Provider, error.Code);
        Assert.Contains("does not support image generation", error.Message);

        var withClassifier = new RemoteCatalogProvider(new StubProvider([Model("m")], classifier: true));
        var classified = await withClassifier.ClassifyAsync(Model("m"), new ClassifierContext
        {
            State = new JsonObject(),
            Questions = new Dictionary<string, ClassifierQuestion>(),
        }, null, CancellationToken.None);
        Assert.Equal("m", classified.Model);
    }

    [Fact]
    public void IdentityAndCatalogAccessorsForwardToTheInnerProvider()
    {
        var provider = new RemoteCatalogProvider(new StubProvider([Model("m")]));

        Assert.Equal("p", provider.Id);
        Assert.Equal("p", provider.Name);
        Assert.Null(provider.BaseUrl);
        Assert.Null(provider.Auth);
        Assert.False(provider.SupportsFetchDeferred);
        Assert.False(provider.SupportsCancelDeferred);
        Assert.Null(provider.FilterAllModels(provider.GetAllModels(), null));
    }
}
