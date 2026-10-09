using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Models;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Persistent model catalogs keyed by provider id. Port of the TS <c>ModelsStore</c>.</summary>
/// <remarks>
/// The TS store speaks plain JSON objects; <see cref="IModelsStore"/> speaks <see cref="ModelsStoreEntry"/>.
/// The file-backed implementation below keeps the parsed JSON as the source of truth so that entries this
/// build does not understand survive a write of a sibling provider (matching TS, which parses and
/// re-serializes the whole file).
/// </remarks>
public static class ModelsStoreJson
{
    /// <summary>Two-space indented JSON with JS-compatible escaping (<c>JSON.stringify(value, null, 2)</c>).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serialize one entry the way TS persists it (<c>{ models, lastModified?, checkedAt?, etag? }</c>).</summary>
    public static JsonObject ToJsonObject(ModelsStoreEntry entry)
    {
        var models = new JsonArray();
        foreach (var model in entry.Models) models.Add(ModelSpecJson.ToJsonObject(model));

        var result = new JsonObject { ["models"] = models };
        if (entry.LastModified is { } lastModified) result["lastModified"] = lastModified;
        if (entry.CheckedAt is { } checkedAt) result["checkedAt"] = checkedAt;
        if (entry.Etag is { } etag) result["etag"] = etag;
        return result;
    }

    /// <summary>
    /// Parse one entry. Models whose <c>type</c> this build does not know are dropped, which TS only
    /// does later in <c>createModels</c> (difference C37) — no shipped provider emits unknown types.
    /// </summary>
    public static ModelsStoreEntry? FromJsonObject(string providerId, JsonObject entry)
    {
        var models = new List<ModelSpec>();
        if (entry["models"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject spec) continue;
                try
                {
                    models.Add(ModelSpecJson.FromJsonObject(providerId, spec));
                }
                catch (ArgumentException)
                {
                    // Unknown model type: dropped, as in TS `hasKnownModelType`.
                }
            }
        }

        return new ModelsStoreEntry
        {
            Models = models,
            LastModified = ReadLong(entry, "lastModified"),
            CheckedAt = ReadLong(entry, "checkedAt"),
            Etag = entry["etag"] is JsonValue etag && etag.TryGetValue<string>(out var etagText) ? etagText : null,
        };
    }

    private static long? ReadLong(JsonObject owner, string key)
        => owner[key] is JsonValue value && value.TryGetValue<long>(out var parsed) ? parsed : null;
}

/// <summary>
/// In-memory model store owned by the coding agent. Port of the TS <c>InMemoryCodingAgentModelsStore</c>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InMemoryModelsStore"/> in Pi.Ai on purpose: the TS package defines its own
/// class here, and callers depend on this one's identity when picking a default store.
/// </remarks>
public sealed class InMemoryCodingAgentModelsStore : IModelsStore
{
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public Task<ModelsStoreEntry?> ReadAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        if (!_entries.TryGetValue(providerId, out var json)) return Task.FromResult<ModelsStoreEntry?>(null);
        var node = JsonNode.Parse(json) as JsonObject;
        return Task.FromResult(node is null ? null : ModelsStoreJson.FromJsonObject(providerId, node));
    }

    public Task WriteAsync(string providerId, ModelsStoreEntry entry,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        // Round-trip through JSON so readers cannot observe later mutation, matching `structuredClone`.
        _entries[providerId] = ModelsStoreJson.ToJsonObject(entry).ToJsonString(ModelsStoreJson.Options);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfAborted(options, cancellationToken);
        _entries.Remove(providerId);
        return Task.CompletedTask;
    }

    private static void ThrowIfAborted(ModelsStoreOperationOptions? options, CancellationToken cancellationToken)
    {
        if (options?.Signal.CanBeCanceled == true) options.Signal.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Locked JSON-backed storage for dynamically refreshed provider catalogs.
/// Port of the TS <c>FileModelsStore</c>.
/// </summary>
public sealed class FileModelsStore : IModelsStore
{
    private sealed class ModelsFileReload(CancellationTokenSource controller, Task<Dictionary<string, JsonObject>> promise)
    {
        public CancellationTokenSource Controller { get; } = controller;

        public Task<Dictionary<string, JsonObject>> Promise { get; } = promise;

        public int Readers { get; set; }
    }

    private sealed class ModelsFileReadState
    {
        public Dictionary<string, JsonObject> Data { get; set; } = new(StringComparer.Ordinal);

        public string? Revision { get; set; }

        public ModelsFileReload? Reload { get; set; }
    }

    // Optimize the common path without retaining an unbounded set of custom paths.
    private static readonly Lock SharedLock = new();
    private static string? _sharedPath;
    private static ModelsFileReadState? _sharedReadState;

    private readonly IAuthStorageBackend _storage;
    private readonly string _path;
    private readonly ModelsFileReadState _readState;

    public FileModelsStore(string? path = null)
    {
        _path = Paths.NormalizePath(path ?? NodePath.Join(Config.GetAgentDir(), "models-store.json"));
        _storage = new FileAuthStorageBackend(_path);
        lock (SharedLock)
        {
            if (_sharedPath == _path && _sharedReadState is not null)
            {
                _readState = _sharedReadState;
            }
            else
            {
                _readState = new ModelsFileReadState();
                if (_sharedPath is null)
                {
                    _sharedPath = _path;
                    _sharedReadState = _readState;
                }
            }
        }
    }

    /// <summary>Absolute path of the backing file.</summary>
    public string Path => _path;

    private static Dictionary<string, JsonObject> Parse(string? content)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content)) return result;
        if (JsonNode.Parse(Text.StripBom(content)) is not JsonObject root) return result;
        foreach (var (providerId, value) in root)
        {
            if (value is JsonObject entry) result[providerId] = entry;
        }
        return result;
    }

    private void UpdateReadState(Dictionary<string, JsonObject> data, string? revision = null)
    {
        _readState.Data = data;
        _readState.Revision = revision;
    }

    private Task<Dictionary<string, JsonObject>> ReloadFromStorageAsync(CancellationToken cancellationToken)
        => _storage.WithLockAsync<Dictionary<string, JsonObject>>(
            content =>
            {
                var data = Parse(content);
                UpdateReadState(data, Paths.GetFileRevision(_path));
                return Task.FromResult(new LockResult<Dictionary<string, JsonObject>>(data));
            },
            cancellationToken);

    private async Task<Dictionary<string, JsonObject>> ReadLatestAsync(
        ModelsStoreOperationOptions? options, CancellationToken cancellationToken)
    {
        var signal = options?.Signal ?? default;
        if (signal.CanBeCanceled) signal.ThrowIfCancellationRequested();

        var revision = Paths.GetFileRevision(_path);
        if (revision is not null && revision == _readState.Revision) return _readState.Data;

        ModelsFileReload reload;
        lock (SharedLock)
        {
            if (_readState.Reload is null)
            {
                var controller = new CancellationTokenSource();
                var created = new ModelsFileReload(controller, ReloadFromStorageAsync(controller.Token));
                _readState.Reload = created;
                _ = created.Promise.ContinueWith(
                    _ =>
                    {
                        lock (SharedLock)
                        {
                            if (ReferenceEquals(_readState.Reload, created)) _readState.Reload = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            reload = _readState.Reload!;
            reload.Readers++;
        }

        try
        {
            var raced = signal.CanBeCanceled
                ? Abort.RaceWithAbortSignalAsync(reload.Promise, signal)
                : reload.Promise;
            return await raced.ConfigureAwait(false);
        }
        finally
        {
            lock (SharedLock)
            {
                reload.Readers--;
                if (reload.Readers == 0 && ReferenceEquals(_readState.Reload, reload))
                {
                    _readState.Reload = null;
                    reload.Controller.Cancel();
                }
            }
        }
    }

    public async Task<ModelsStoreEntry?> ReadAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = await ReadLatestAsync(options, cancellationToken).ConfigureAwait(false);
        if (options?.Signal.CanBeCanceled == true) options.Signal.ThrowIfCancellationRequested();
        if (!data.TryGetValue(providerId, out var entry)) return null;
        return ModelsStoreJson.FromJsonObject(providerId, (JsonObject)entry.DeepClone());
    }

    public async Task WriteAsync(string providerId, ModelsStoreEntry entry,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonObject>? latest = null;
        await _storage.WithLockAsync<string?>(
            content =>
            {
                var current = Parse(content);
                current[providerId] = ModelsStoreJson.ToJsonObject(entry);
                latest = current;
                return Task.FromResult(new LockResult<string?>(null, Serialize(current)));
            },
            MergeSignals(options, cancellationToken)).ConfigureAwait(false);
        if (latest is not null) UpdateReadState(latest);
    }

    public async Task DeleteAsync(string providerId,
        ModelsStoreOperationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Dictionary<string, JsonObject>? latest = null;
        await _storage.WithLockAsync<string?>(
            content =>
            {
                var current = Parse(content);
                current.Remove(providerId);
                latest = current;
                return Task.FromResult(new LockResult<string?>(null, Serialize(current)));
            },
            MergeSignals(options, cancellationToken)).ConfigureAwait(false);
        if (latest is not null) UpdateReadState(latest);
    }

    /// <summary>
    /// TS passes one <c>AbortSignal</c>; the port has an options signal and an ambient token, so the
    /// stronger of the two is used (linked when both are cancellable).
    /// </summary>
    private static CancellationToken MergeSignals(ModelsStoreOperationOptions? options, CancellationToken cancellationToken)
    {
        var signal = options?.Signal ?? default;
        if (!signal.CanBeCanceled) return cancellationToken;
        if (!cancellationToken.CanBeCanceled) return signal;
        return CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken).Token;
    }

    private static string Serialize(Dictionary<string, JsonObject> data)
    {
        var root = new JsonObject();
        foreach (var (providerId, entry) in data) root[providerId] = entry;
        return root.ToJsonString(ModelsStoreJson.Options);
    }
}
