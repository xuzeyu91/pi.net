using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>The outcome of one locked read/modify cycle. Port of the TS <c>LockResult</c>.</summary>
public sealed record LockResult<T>(T Result, string? Next = null);

/// <summary>
/// Locked read/write access to the auth file. Port of the TS <c>AuthStorageBackend</c>.
/// </summary>
/// <remarks>
/// The TS <c>options</c> parameter is an <c>AuthOperationOptions</c> (<c>{ signal }</c>); .NET passes a
/// <see cref="CancellationToken"/> directly, the same normalization the rest of this solution uses.
/// </remarks>
public interface IAuthStorageBackend
{
    T WithLock<T>(Func<string?, LockResult<T>> fn);

    Task<T> WithLockAsync<T>(
        Func<string?, Task<LockResult<T>>> fn,
        CancellationToken cancellationToken = default);
}

/// <summary>Writes a file with a Unix create mode, keeping the mode off Windows. Port of Node's <c>mode</c> option.</summary>
internal static class ModeFile
{
    /// <summary><c>0o600</c>: owner read/write only.</summary>
    public const int OwnerReadWrite = 0b110_000_000;

    /// <summary><c>0o700</c>: owner read/write/execute only.</summary>
    public const int OwnerAll = 0b111_000_000;

    /// <summary>Write the file, applying <paramref name="unixMode"/> only when the file is created.</summary>
    public static void WriteAllText(string path, string content, int unixMode)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = (UnixFileMode)unixMode,
        };
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>Create a directory tree with a Unix create mode, keeping the mode off Windows.</summary>
    public static void CreateDirectory(string path, int unixMode)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, (UnixFileMode)unixMode);
    }
}

/// <summary>
/// The real <c>auth.json</c> backend: a cross-process lock plus a synchronous and an asynchronous
/// read-modify-write cycle. Port of <c>FileAuthStorageBackend</c>.
/// </summary>
public sealed class FileAuthStorageBackend : IAuthStorageBackend
{
    private readonly string _authPath;

    public FileAuthStorageBackend(string? authPath = null)
    {
        _authPath = Paths.NormalizePath(authPath ?? NodePath.Join(Config.GetAgentDir(), "auth.json"));
    }

    public string AuthPath => _authPath;

    private void EnsureParentDir()
    {
        var directory = NodePath.Dirname(_authPath);
        if (!Directory.Exists(directory)) ModeFile.CreateDirectory(directory, ModeFile.OwnerAll);
    }

    private void EnsureFileExists()
    {
        if (!File.Exists(_authPath)) ModeFile.WriteAllText(_authPath, "{}", ModeFile.OwnerReadWrite);
    }

    public T WithLock<T>(Func<string?, LockResult<T>> fn)
    {
        EnsureParentDir();
        EnsureFileExists();

        using var _ = NodeLock.AcquireSync(_authPath);
        var current = File.Exists(_authPath) ? File.ReadAllText(_authPath) : null;
        var (result, next) = fn(current);
        if (next is not null) ModeFile.WriteAllText(_authPath, next, ModeFile.OwnerReadWrite);
        return result;
    }

    public async Task<T> WithLockAsync<T>(
        Func<string?, Task<LockResult<T>>> fn,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureParentDir();
        EnsureFileExists();

        var lockCompromised = false;
        Exception? lockCompromisedError = null;
        var release = await NodeLock.AcquireAsync(
            _authPath,
            cancellationToken,
            error =>
            {
                lockCompromised = true;
                lockCompromisedError = error;
            }).ConfigureAwait(false);

        void ThrowIfCompromised()
        {
            if (lockCompromised) throw lockCompromisedError ?? new IOException("Auth storage lock was compromised");
        }

        try
        {
            ThrowIfCompromised();
            cancellationToken.ThrowIfCancellationRequested();
            var current = File.Exists(_authPath) ? File.ReadAllText(_authPath) : null;
            var (result, next) = await fn(current).ConfigureAwait(false);
            ThrowIfCompromised();
            cancellationToken.ThrowIfCancellationRequested();
            if (next is not null) ModeFile.WriteAllText(_authPath, next, ModeFile.OwnerReadWrite);
            ThrowIfCompromised();
            return result;
        }
        finally
        {
            // Ignore unlock errors when the lock is compromised.
            try
            {
                await release().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Best-effort unlock.
            }
        }
    }
}

/// <summary>
/// Credential metadata as the read-only store reports it (kept local so the type matches
/// <see cref="ICredentialStore"/> exactly).
/// </summary>
public sealed class ReadOnlyAuthStorage : ICredentialStore
{
    private readonly string _authPath;
    private Dictionary<string, Credential>? _data;

    public ReadOnlyAuthStorage(string? authPath = null)
    {
        _authPath = Paths.NormalizePath(authPath ?? NodePath.Join(Config.GetAgentDir(), "auth.json"));
    }

    private Dictionary<string, Credential> Load()
    {
        if (_data is not null) return _data;

        string text;
        try
        {
            text = File.ReadAllText(_authPath);
        }
        catch (FileNotFoundException)
        {
            _data = new Dictionary<string, Credential>(StringComparer.Ordinal);
            return _data;
        }
        catch (DirectoryNotFoundException)
        {
            _data = new Dictionary<string, Credential>(StringComparer.Ordinal);
            return _data;
        }
        catch (IOException error)
        {
            throw new InvalidOperationException($"Failed to read auth.json: {error.Message}", error);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Text.StripBom(text));
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Failed to read auth.json: {error.Message}", error);
        }

        if (parsed is not JsonObject obj)
        {
            throw new InvalidOperationException("Invalid auth.json: expected an object");
        }

        var data = new Dictionary<string, Credential>(StringComparer.Ordinal);
        foreach (var (providerId, credential) in obj)
        {
            if (credential is not JsonObject credentialObject)
            {
                throw new InvalidOperationException($"Invalid auth.json credential for provider \"{providerId}\"");
            }

            var type = credentialObject["type"] is JsonValue typeValue
                && typeValue.TryGetValue<string>(out var typeText) ? typeText : null;
            if (type == "api_key")
            {
                var validKey = credentialObject["key"] is null
                    || (credentialObject["key"] is JsonValue keyValue && keyValue.TryGetValue<string>(out _));
                var validEnv = credentialObject["env"] is null
                    || (credentialObject["env"] is JsonObject envObject
                        && envObject.All(pair => pair.Value is JsonValue value && value.TryGetValue<string>(out _)));
                if (validKey && validEnv)
                {
                    data[providerId] = JsonSerializer.Deserialize<Credential>(
                        credentialObject.ToJsonString(), AuthJson.Options)!;
                    continue;
                }
            }
            else if (type == "oauth"
                && credentialObject["access"] is JsonValue accessValue && accessValue.TryGetValue<string>(out _)
                && credentialObject["refresh"] is JsonValue refreshValue && refreshValue.TryGetValue<string>(out _)
                && credentialObject["expires"] is JsonValue expiresValue
                && expiresValue.TryGetValue<double>(out var expires) && double.IsFinite(expires))
            {
                data[providerId] = JsonSerializer.Deserialize<Credential>(
                    credentialObject.ToJsonString(), AuthJson.Options)!;
                continue;
            }

            throw new InvalidOperationException($"Invalid auth.json credential for provider \"{providerId}\"");
        }

        _data = data;
        return _data;
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Load().TryGetValue(providerId, out var credential);
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is null) return Task.FromResult<Credential?>(null);
        if (credential is not Credential.ApiKey apiKey || string.IsNullOrEmpty(apiKey.Key)
            || ConfigValueResolver.IsCommandConfigValue(apiKey.Key))
        {
            return Task.FromResult<Credential?>(credential);
        }
        return Task.FromResult<Credential?>(apiKey with
        {
            Key = ConfigValueResolver.ResolveConfigValue(apiKey.Key, apiKey.Env),
        });
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CredentialInfo> credentials = Load()
            .Select(pair => new CredentialInfo(pair.Key, pair.Value.Kind))
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(credentials);
    }

    public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Read-only credential storage cannot modify auth.json");

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Read-only credential storage cannot modify auth.json");
}

/// <summary>An in-process backend that serializes writes. Port of <c>InMemoryAuthStorageBackend</c>.</summary>
public sealed class InMemoryAuthStorageBackend : IAuthStorageBackend
{
    private readonly Lock _lock = new();
    private string? _value;
    private Task _asyncChain = Task.CompletedTask;

    public T WithLock<T>(Func<string?, LockResult<T>> fn)
    {
        lock (_lock)
        {
            var (result, next) = fn(_value);
            if (next is not null) _value = next;
            return result;
        }
    }

    public Task<T> WithLockAsync<T>(
        Func<string?, Task<LockResult<T>>> fn,
        CancellationToken cancellationToken = default)
    {
        Task<T> operation;
        lock (_lock)
        {
            var previous = _asyncChain;
            operation = RunAsync(previous, fn, cancellationToken);
            _asyncChain = operation.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        return cancellationToken.CanBeCanceled
            ? Abort.RaceWithAbortSignalAsync(operation, cancellationToken)
            : operation;
    }

    private async Task<T> RunAsync<T>(
        Task previous,
        Func<string?, Task<LockResult<T>>> fn,
        CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed predecessor must not break the chain.
        }
        cancellationToken.ThrowIfCancellationRequested();
        string? current;
        lock (_lock) current = _value;
        var (result, next) = await fn(current).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (next is not null)
        {
            lock (_lock) _value = next;
        }
        return result;
    }
}

/// <summary>Credential storage backed by <c>auth.json</c>. Port of the TS <c>AuthStorage</c>.</summary>
public sealed class AuthStorage : ICredentialStore
{
    private sealed class AuthFileReload(CancellationTokenSource controller, int readers)
    {
        public CancellationTokenSource Controller { get; } = controller;

        public int Readers { get; set; } = readers;

        public Task<Dictionary<string, Credential>>? Promise { get; set; }
    }

    private sealed class AuthFileReadState
    {
        public Dictionary<string, Credential> Data { get; set; } = new(StringComparer.Ordinal);

        public string? Revision { get; set; }

        public AuthFileReload? Reload { get; set; }
    }

    private static (string AuthPath, AuthFileReadState ReadState)? _sharedAuthFileReadState;

    private readonly IAuthStorageBackend _storage;
    private readonly string? _authPath;
    private readonly AuthFileReadState _readState;
    private readonly Lock _stateLock = new();

    private AuthStorage(IAuthStorageBackend storage, string? authPath = null)
    {
        _storage = storage;
        _authPath = authPath;

        var shared = _sharedAuthFileReadState;
        _readState = authPath is not null && shared?.AuthPath == authPath
            ? shared.Value.ReadState
            : new AuthFileReadState();
        if (authPath is not null && shared is null) _sharedAuthFileReadState = (authPath, _readState);

        if (authPath is not null)
        {
            var revision = Paths.GetFileRevision(authPath);
            if (revision is not null && revision == _readState.Revision) return;
        }
        Reload();
    }

    /// <summary>Create a store backed by the file at <paramref name="authPath"/>.</summary>
    public static AuthStorage Create(string? authPath = null)
    {
        var normalizedAuthPath = Paths.NormalizePath(authPath ?? NodePath.Join(Config.GetAgentDir(), "auth.json"));
        return new AuthStorage(new FileAuthStorageBackend(normalizedAuthPath), normalizedAuthPath);
    }

    public static AuthStorage FromStorage(IAuthStorageBackend storage) => new(storage);

    public static AuthStorage InMemory(IReadOnlyDictionary<string, Credential>? data = null)
    {
        var storage = new InMemoryAuthStorageBackend();
        storage.WithLock(_ => new LockResult<object?>(null, SerializeCredentials(data)));
        return FromStorage(storage);
    }

    private static Dictionary<string, Credential> ParseStorageData(string? content)
        => string.IsNullOrEmpty(content)
            ? new Dictionary<string, Credential>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, Credential>>(
                Text.StripBom(content), AuthJson.Options)
                ?? new Dictionary<string, Credential>(StringComparer.Ordinal);

    private static string SerializeCredentials(IReadOnlyDictionary<string, Credential>? data)
        => JsonSerializer.Serialize(data ?? new Dictionary<string, Credential>(StringComparer.Ordinal), AuthJson.Options);

    private void UpdateReadState(Dictionary<string, Credential> data, string? revision = null)
    {
        lock (_stateLock)
        {
            _readState.Data = data;
            _readState.Revision = revision;
        }
    }

    /// <summary>Reload credentials from storage, keeping the last valid snapshot on failure.</summary>
    public void Reload()
    {
        try
        {
            string? content = null;
            string? revision = null;
            _storage.WithLock(current =>
            {
                content = current;
                revision = _authPath is not null ? Paths.GetFileRevision(_authPath) : null;
                return new LockResult<object?>(null);
            });
            UpdateReadState(ParseStorageData(content), revision);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            // Preserve the last valid in-memory snapshot.
        }
    }

    private async Task<Dictionary<string, Credential>> ReloadFromStorageAsync(CancellationToken cancellationToken)
    {
        return await _storage.WithLockAsync(async content =>
        {
            var currentData = ParseStorageData(content);
            var revision = _authPath is not null ? Paths.GetFileRevision(_authPath) : null;
            UpdateReadState(currentData, revision);
            return new LockResult<Dictionary<string, Credential>>(currentData);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, Credential>> ReadLatestDataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_authPath is null)
        {
            var reload = ReloadFromStorageAsync(cancellationToken);
            if (cancellationToken.CanBeCanceled) return await reload.ConfigureAwait(false);
            try
            {
                return await reload.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            {
                return _readState.Data;
            }
        }

        var revision = Paths.GetFileRevision(_authPath);
        if (revision is not null && revision == _readState.Revision) return _readState.Data;

        AuthFileReload handle;
        lock (_stateLock)
        {
            if (_readState.Reload is null)
            {
                var controller = new CancellationTokenSource();
                var created = new AuthFileReload(controller, 0);
                created.Promise = ReloadFromStorageAsync(controller.Token);
                _readState.Reload = created;
                _ = created.Promise.ContinueWith(
                    _ =>
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_readState.Reload, created)) _readState.Reload = null;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            handle = _readState.Reload!;
            handle.Readers++;
        }

        try
        {
            var promise = handle.Promise!;
            var raced = cancellationToken.CanBeCanceled
                ? Abort.RaceWithAbortSignalAsync(promise, cancellationToken)
                : promise;
            if (cancellationToken.CanBeCanceled) return await raced.ConfigureAwait(false);
            try
            {
                return await raced.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            {
                return _readState.Data;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                handle.Readers--;
                if (handle.Readers == 0 && ReferenceEquals(_readState.Reload, handle))
                {
                    _readState.Reload = null;
                    handle.Controller.Cancel();
                }
            }
        }
    }

    public async Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var data = await ReadLatestDataAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!data.TryGetValue(providerId, out var credential)) return null;
        if (credential is not Credential.ApiKey apiKey) return credential;
        if (apiKey.Key is null) return apiKey;
        return apiKey with { Key = ConfigValueResolver.ResolveConfigValue(apiKey.Key, apiKey.Env) };
    }

    public async Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
        CancellationToken cancellationToken = default)
    {
        var latestData = _readState.Data;
        string? revision = null;
        var result = await _storage.WithLockAsync(async content =>
        {
            var currentData = ParseStorageData(content);
            currentData.TryGetValue(providerId, out var current);
            var next = await fn(current).ConfigureAwait(false);
            if (next is null)
            {
                latestData = currentData;
                revision = _authPath is not null ? Paths.GetFileRevision(_authPath) : null;
                return new LockResult<Credential?>(current);
            }

            var merged = new Dictionary<string, Credential>(currentData, StringComparer.Ordinal)
            {
                [providerId] = next,
            };
            latestData = merged;
            return new LockResult<Credential?>(next, SerializeCredentials(merged));
        }, cancellationToken).ConfigureAwait(false);
        UpdateReadState(latestData, revision);
        return result;
    }

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var latestData = _readState.Data;
        await _storage.WithLockAsync(content =>
        {
            var currentData = ParseStorageData(content);
            currentData.Remove(providerId);
            latestData = currentData;
            return Task.FromResult(new LockResult<object?>(null, SerializeCredentials(currentData)));
        }, cancellationToken).ConfigureAwait(false);
        UpdateReadState(latestData);
    }

    /// <summary>List credential metadata without resolving configured key values.</summary>
    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadLatestDataAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return data.Select(pair => new CredentialInfo(pair.Key, pair.Value.Kind)).ToList();
    }
}

/// <summary>
/// One-off synchronous read of a stored credential from an auth.json file, without instantiating a store
/// or resolving configured key values. Port of <c>readStoredCredential</c>.
/// </summary>
public static class StoredCredential
{
    public static Credential? Read(string providerId, string? authPath = null)
    {
        try
        {
            var path = Paths.NormalizePath(authPath ?? NodePath.Join(Config.GetAgentDir(), "auth.json"));
            var data = JsonSerializer.Deserialize<Dictionary<string, Credential>>(
                Text.StripBom(File.ReadAllText(path)), AuthJson.Options);
            return data is not null && data.TryGetValue(providerId, out var credential) ? credential : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>JSON options for auth.json, matching <c>JSON.stringify(value, null, 2)</c>.</summary>
internal static class AuthJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
