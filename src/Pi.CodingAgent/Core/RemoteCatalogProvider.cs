using System.Globalization;
using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Adds a persisted <c>pi.dev</c> catalog overlay to a static built-in provider.
/// Port of <c>core/remote-catalog-provider.ts</c> (<c>withRemoteCatalog</c>).
/// </summary>
/// <remarks>
/// <para>
/// TS builds the wrapper by spreading the inner provider and overriding three members, so every other
/// member (including the optional <c>generateImages</c> / <c>classify</c> capabilities) is carried over
/// unchanged. C# has no object spread, so this wrapper forwards each member explicitly and implements
/// <see cref="IImagesProvider"/> / <see cref="IClassifierProvider"/> itself: when the inner provider does
/// not expose the capability the forwarded call throws the same
/// <c>Provider … does not support …</c> error the inner provider would have produced, which keeps
/// <see cref="Models.GenerateImagesAsync"/> / <see cref="Models.ClassifyAsync"/> on their existing paths.
/// </para>
/// <para>
/// A malformed catalog and a failed catalog request raise <see cref="InvalidOperationException"/> rather
/// than <see cref="ModelsError"/>, matching TS (which throws a plain <c>Error</c>): the refresh driver
/// then wraps it as a <c>model_source</c> failure attributed to this provider.
/// </para>
/// </remarks>
public sealed class RemoteCatalogProvider : IProvider, IImagesProvider, IClassifierProvider
{
    public const string DefaultCatalogBaseUrl = "https://pi.dev";

    private const int RemoteCatalogAttemptTimeoutMs = 4_000;

    /// <summary>How long a fetched catalog is considered fresh before it is revalidated.</summary>
    public const long RemoteCatalogRefreshIntervalMs = 4L * 60 * 60 * 1000;

    /// <summary>
    /// Model types this client can consume. Sent as <c>?types=</c> so the catalog server returns the
    /// full-type shard instead of the chat-only one served to clients that predate model types. A server
    /// that ignores the parameter still returns the chat-only shard, which this client handles unchanged.
    /// </summary>
    public static readonly IReadOnlyList<ModelType> RemoteCatalogModelTypes =
        [ModelType.Chat, ModelType.Image, ModelType.Classifier];

    private readonly IProvider _inner;
    private readonly string _catalogBaseUrl;
    private readonly long? _localGeneratedAt;
    private IReadOnlyList<ModelSpec> _dynamicModels = [];

    public RemoteCatalogProvider(IProvider inner, string? catalogBaseUrl = null, long? localGeneratedAt = null)
    {
        _inner = inner;
        _catalogBaseUrl = catalogBaseUrl ?? DefaultCatalogBaseUrl;
        _localGeneratedAt = localGeneratedAt;
    }

    public string Id => _inner.Id;

    public string Name => _inner.Name;

    public string? BaseUrl => _inner.BaseUrl;

    public ProviderAuth? Auth => _inner.Auth;

    public bool SupportsFetchDeferred => _inner.SupportsFetchDeferred;

    public bool SupportsCancelDeferred => _inner.SupportsCancelDeferred;

    public IReadOnlyList<ModelSpec> GetModels()
        => MergeModels(_inner.GetModels(), _dynamicModels.Where(model => model.Type == ModelType.Chat).ToList());

    public IReadOnlyList<ModelSpec> GetAllModels() => MergeModels(_inner.GetAllModels(), _dynamicModels);

    public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
        => _inner.FilterModels(models, credential);

    public IReadOnlyList<ModelSpec>? FilterAllModels(IReadOnlyList<ModelSpec> models, Credential? credential)
        => _inner.FilterAllModels(models, credential);

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.Stream(model, context, options);

    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.StreamSimple(model, context, options);

    public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.StreamDeferred(model, handle, options);

    public Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => _inner.CancelDeferredAsync(model, handle, options, cancellationToken);

    public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options, CancellationToken cancellationToken)
        => _inner is IImagesProvider images
            ? images.GenerateImagesAsync(model, context, options, cancellationToken)
            : throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support image generation");

    public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options, CancellationToken cancellationToken)
        => _inner is IClassifierProvider classifier
            ? classifier.ClassifyAsync(model, context, options, cancellationToken)
            : throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support classification");

    /// <summary>The overlay refresh implementation installed as <see cref="IProvider.RefreshModels"/>.</summary>
    public Func<RefreshModelsContext, Task>? RefreshModels => RefreshAsync;

    private async Task RefreshAsync(RefreshModelsContext context)
    {
        var stored = context.Stored;
        var restored = RemoteModels(stored, _localGeneratedAt)
            .Where(model => model.Provider == Id)
            .ToList();

        if (!await context.Publish(new ModelsPublication { Update = () => _dynamicModels = restored })
                .ConfigureAwait(false))
        {
            return;
        }

        if (!context.AllowNetwork || context.Signal.IsCancellationRequested) return;
        if (context.Force != true && IsFresh(stored)) return;

        // Only revalidate when a cached body backs the validator, so a 304 can never leave the overlay empty.
        var validator = stored is { Models.Count: > 0 } ? stored.Etag : null;
        var types = string.Join(",", RemoteCatalogModelTypes.Select(ModelSpecJson.TypeToText));
        var relative = $"/api/models/providers/{JsUri.EncodeUriComponent(Id)}"
            + $"?types={JsUri.EncodeUriComponent(types)}";
        var url = new Uri(new Uri(_catalogBaseUrl), relative).AbsoluteUri;

        using var response = await ManagementHttp.FetchWithRetryAsync(
            url,
            request =>
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent",
                    Pi.CodingAgent.Utils.PiUserAgent.GetPiUserAgent(Config.Version));
                if (validator is not null)
                {
                    request.Headers.TryAddWithoutValidation("if-none-match", validator);
                }
            },
            new FetchRetryOptions { AttemptTimeoutMs = RemoteCatalogAttemptTimeoutMs },
            context.Signal).ConfigureAwait(false);

        if (context.Signal.IsCancellationRequested) return;
        var checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var status = (int)response.StatusCode;

        // Unchanged: _dynamicModels already holds the stored overlay, so only the freshness window moves.
        if (status == 304 && stored is not null)
        {
            await context.Publish(new ModelsPublication { Persist = stored with { CheckedAt = checkedAt } })
                .ConfigureAwait(false);
            return;
        }

        if (status is 404 or 501)
        {
            await context.Publish(new ModelsPublication
            {
                Persist = (stored ?? EmptyEntry()) with
                {
                    CheckedAt = checkedAt,
                    LastModified = 0,
                    Etag = null,
                },
            }).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Transient failure: the cached body and its validator stay valid, so keep the etag and let the
            // next refresh revalidate instead of downloading the catalog.
            await context.Publish(new ModelsPublication
            {
                Persist = (stored ?? EmptyEntry()) with { CheckedAt = checkedAt },
            }).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Model catalog request failed for {Id}: {status}");
        }

        var body = await response.Content.ReadAsStringAsync(context.Signal).ConfigureAwait(false);
        var refreshed = ParseCatalog(Id, JsonNode.Parse(body));
        var lastModified = ParseHttpDate(ReadHeader(response, "last-modified"));
        if (context.Signal.IsCancellationRequested) return;

        var entry = new ModelsStoreEntry
        {
            Models = refreshed,
            CheckedAt = checkedAt,
            LastModified = lastModified ?? 0,
            Etag = ReadHeader(response, "etag"),
        };
        var published = RemoteModels(entry, _localGeneratedAt);
        await context.Publish(new ModelsPublication
        {
            Persist = entry,
            Update = () => _dynamicModels = published,
        }).ConfigureAwait(false);
    }

    private static ModelsStoreEntry EmptyEntry() => new() { Models = [] };

    /// <summary>Whether the stored catalog was checked within the freshness window.</summary>
    private static bool IsFresh(ModelsStoreEntry? stored)
        => stored is { CheckedAt: { } checkedAt, LastModified: not null }
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - checkedAt < RemoteCatalogRefreshIntervalMs;

    /// <summary>Merge a baseline and a dynamic catalog: a later entry wins, keeping first-seen order.</summary>
    private static IReadOnlyList<ModelSpec> MergeModels(
        IReadOnlyList<ModelSpec> baseline, IReadOnlyList<ModelSpec> dynamic)
    {
        var merged = new List<ModelSpec>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var model in baseline.Concat(dynamic))
        {
            var key = $"{ModelSpecJson.TypeToText(model.Type)}\0{model.Id}";
            if (index.TryGetValue(key, out var existing)) merged[existing] = model;
            else
            {
                index[key] = merged.Count;
                merged.Add(model);
            }
        }
        return merged;
    }

    /// <summary>Parse a catalog body: an array, an object with a <c>models</c> array, or an object map.</summary>
    private static IReadOnlyList<ModelSpec> ParseCatalog(string providerId, JsonNode? value)
    {
        var entries = value switch
        {
            JsonArray array => array,
            JsonObject obj when obj["models"] is JsonArray models => models,
            // Object.values(value): an object map keyed by model id.
            JsonObject obj => ObjectValues(obj),
            _ => null,
        };
        if (entries is null)
        {
            throw new InvalidOperationException($"Invalid model catalog for provider \"{providerId}\"");
        }

        var parsed = new List<ModelSpec>();
        foreach (var item in entries)
        {
            if (item is not JsonObject entry || !entry.ContainsKey("id")) continue;
            if (!IsSupportedModelType(entry)) continue;
            parsed.Add(ModelSpecJson.FromJsonObject(providerId, entry));
        }
        return parsed;
    }

    private static JsonArray ObjectValues(JsonObject source)
    {
        var array = new JsonArray();
        foreach (var (_, entry) in source) array.Add(entry?.DeepClone());
        return array;
    }

    /// <summary>
    /// A model type is supported when <c>type</c> is absent or one of <see cref="RemoteCatalogModelTypes"/>.
    /// The absence check mirrors TS: a server predating model types omits <c>type</c> entirely, while an
    /// explicit non-string value is rejected.
    /// </summary>
    private static bool IsSupportedModelType(JsonObject model)
    {
        if (!model.ContainsKey("type")) return true;
        if (model["type"] is not JsonValue value) return false;
        if (!value.TryGetValue<string>(out var type)) return false;
        return RemoteCatalogModelTypes.Any(candidate => ModelSpecJson.TypeToText(candidate) == type);
    }

    /// <summary>The stored overlay, gated on being newer than the locally generated catalog.</summary>
    private static IReadOnlyList<ModelSpec> RemoteModels(ModelsStoreEntry? entry, long? localGeneratedAt)
    {
        if (entry is null) return [];
        if (localGeneratedAt is { } generatedAt
            && (entry.LastModified is null || entry.LastModified <= generatedAt))
        {
            return [];
        }
        return entry.Models;
    }

    /// <summary>Parse an HTTP date header; an unparseable value becomes null (TS <c>Number.isNaN</c> → 0).</summary>
    private static long? ParseHttpDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;

    /// <summary>
    /// Read a response header the way TS's <c>response.headers.get(name)</c> does. .NET splits the header
    /// collections, and routes entity headers such as <c>Last-Modified</c> to
    /// <see cref="HttpContentHeaders"/> rather than <see cref="HttpResponseHeaders"/>, so both are searched.
    /// </summary>
    private static string? ReadHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return response.Content.Headers.TryGetValues(name, out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }
}
