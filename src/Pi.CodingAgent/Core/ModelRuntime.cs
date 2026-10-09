using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Options for <see cref="ModelRuntime.CreateAsync"/>. Port of the TS
/// <c>CreateModelRuntimeOptions</c> (core/model-runtime.ts).
/// </summary>
public sealed record CreateModelRuntimeOptions
{
    /// <summary>Credential storage. Defaults to the file at <see cref="AuthPath"/>.</summary>
    public ICredentialStore? Credentials { get; init; }

    public string? AuthPath { get; init; }

    /// <summary>Path to <c>models.json</c>; unset uses the agent dir. See <see cref="ModelsPathDisabled"/>.</summary>
    public string? ModelsPath { get; init; }

    /// <summary>
    /// TS <c>modelsPath: null</c> — run with no <c>models.json</c> at all. Takes precedence over
    /// <see cref="ModelsPath"/> and also disables the file-backed models store (the store default is
    /// derived from the models path).
    /// </summary>
    public bool ModelsPathDisabled { get; init; }

    public IModelsStore? ModelsStore { get; init; }

    public string? ModelsStorePath { get; init; }

    /// <summary>Allow <c>CreateAsync</c> to refresh model catalogs over the network. Defaults to false.</summary>
    public bool? AllowModelNetwork { get; init; }

    /// <summary>Timeout for the create-time network model refresh.</summary>
    public int? ModelRefreshTimeoutMs { get; init; }

    public string? CatalogBaseUrl { get; init; }

    /// <summary>Optional caller cancellation for initial cache restoration and availability checks.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Skip initial catalog and availability refresh. Static models remain available.</summary>
    public bool? RefreshOnCreate { get; init; }

    /// <summary>
    /// Environment lookup seam (tests). Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// Used for <c>PI_OFFLINE</c>, which TS tests with <c>process.env.PI_OFFLINE === undefined</c>.
    /// </summary>
    public Func<string, string?>? Env { get; init; }
}

/// <summary>Per-request auth overrides. Port of the TS <c>ModelRuntimeAuthOverrides</c>.</summary>
public sealed record ModelRuntimeAuthOverrides
{
    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Require this much remaining OAuth-token validity; defaults to five minutes.</summary>
    public long? MinOAuthValidityMs { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>The credential mutation an out-of-sync snapshot is attributed to.</summary>
public enum CredentialSynchronizationOperation
{
    Login,
    Logout,
    SetRuntimeApiKey,
    RemoveRuntimeApiKey,
}

/// <summary>TS spelling of <see cref="CredentialSynchronizationOperation"/>, used in the error message.</summary>
internal static class CredentialSynchronizationOperations
{
    public static string Text(CredentialSynchronizationOperation operation) => operation switch
    {
        CredentialSynchronizationOperation.Login => "login",
        CredentialSynchronizationOperation.Logout => "logout",
        CredentialSynchronizationOperation.SetRuntimeApiKey => "setRuntimeApiKey",
        CredentialSynchronizationOperation.RemoveRuntimeApiKey => "removeRuntimeApiKey",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}

/// <summary>
/// Credentials changed successfully, but the local model/auth snapshot could not be synchronized.
/// Port of the TS <c>CredentialSynchronizationError</c>.
/// </summary>
public sealed class CredentialSynchronizationError(
    string providerId,
    CredentialSynchronizationOperation operation,
    Credential? credential,
    Exception cause)
    : Exception(
        $"Credential {CredentialSynchronizationOperations.Text(operation)} committed for {providerId},"
        + " but local synchronization failed",
        cause)
{
    public string ProviderId { get; } = providerId;

    public CredentialSynchronizationOperation Operation { get; } = operation;

    public Credential? Credential { get; } = credential;
}

/// <summary>Router inputs for one <c>resolveModel</c> call. Port of the TS inline options object.</summary>
public sealed record ResolveModelOptions
{
    /// <summary>One of <see cref="VirtualModels.RouteReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>The selected thinking level. Its meaning is up to the router.</summary>
    public required string ThinkingLevel { get; init; }

    public CancellationToken Signal { get; init; }

    /// <summary>The failed response for a retry; <c>messages</c> no longer contains it.</summary>
    public AssistantMessage? Failed { get; init; }

    /// <summary>Router state the caller stored on the session branch.</summary>
    public JsonNode? State { get; init; }
}

/// <summary>
/// Cached view of the runtime's models and provider auth. TS keeps this as a private
/// <c>ModelRuntimeSnapshot</c>; C# mirrors that (internal, never handed out as a whole).
/// </summary>
/// <remarks>
/// TS's <c>auth</c> map holds an entry for every provider, some with an <c>undefined</c> value.
/// Lookups cannot tell that apart from a missing key, so C# stores only the providers that actually
/// resolved (difference C58).
/// </remarks>
internal sealed record ModelRuntimeSnapshot
{
    public required IReadOnlyList<ModelSpec> All { get; init; }

    public required IReadOnlyList<ModelSpec> Available { get; init; }

    public required HashSet<string> ConfiguredProviders { get; init; }

    public required HashSet<string> StoredProviders { get; init; }

    public required Dictionary<string, AuthCheck> Auth { get; init; }
}

/// <summary>
/// Configured pi-ai model collection used by the coding agent and SDK consumers.
/// Port of <c>core/model-runtime.ts</c> (1,032 lines).
/// </summary>
/// <remarks>
/// <para>
/// TS declares <c>class ModelRuntime implements Models</c>, i.e. it satisfies the public <c>Models</c>
/// interface while wrapping a <c>MutableModels</c> instance. C# has no separate interface — <c>Models</c>
/// is a sealed class that plays both roles — so this class <em>composes</em> a <c>Models</c> instance and
/// re-exposes the same surface (difference C57).
/// </para>
/// <para>
/// Differences recorded against the TS original:
/// <list type="bullet">
/// <item>C57：组合而非继承 <c>Models</c>（见上）。</item>
/// <item>C58：<c>snapshot.auth</c> 只保存真正解析成功的 provider（TS 保存全部、未配置者值为
/// <c>undefined</c>；两种形状的查询结果一致）。</item>
/// <item>C59：<c>modelsPath</c> 的「未设置 / 显式 null」两态由 <see cref="CreateModelRuntimeOptions.ModelsPathDisabled"/>
/// 承载（C# 的 <c>string?</c> 只有一个空值）。</item>
/// <item>C60：<c>refresh()</c> 里对「旧版发布包返回 undefined」的兜底不需要——C# 的
/// <see cref="Models.RefreshAsync"/> 恒返回结果对象。</item>
/// <item>C61：图片/分类请求同样解析认证并应用 <c>transformHeaders</c>（与 TS 一致），但这与 C# 既有
/// <see cref="Models.GenerateImagesAsync"/>/<see cref="Models.ClassifyAsync"/> 的「不解析认证」行为不同——
/// 本类是图片/分类的认证边界，<c>Models</c> 那一层保持原样。</item>
/// <item>C81（记在 <c>model-resolver.ts</c> 名下）：实现 <see cref="IModelResolverRuntime"/>，即
/// <c>model-resolver.ts</c> 实际读到的五个成员的具名切片。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModelRuntime : IModelResolverRuntime
{
    private sealed record RegisteredVirtualModel(ModelSpec Model, Func<ModelRouteRequest, Task<ModelRoute>> Route);

    private sealed record PreparedChatRequest(IProvider Provider, ModelSpec Model, Dictionary<string, object?> Options);

    private sealed record PreparedImagesRequest(IProvider Provider, ModelSpec Model, ImagesOptions Options);

    private sealed record PreparedClassifierRequest(IProvider Provider, ModelSpec Model, ClassifierOptions Options);

    private readonly Models _models;
    private readonly RuntimeCredentials _credentials;
    private readonly Dictionary<string, IProvider> _defaultBuiltins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IProvider> _builtins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IProvider> _nativeExtensionProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderConfigInput> _extensionProviders = new(StringComparer.Ordinal);

    /// <summary>Virtual models by provider id, then model id.</summary>
    private readonly Dictionary<string, Dictionary<string, RegisteredVirtualModel>> _virtualModels =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _compositionErrors = new(StringComparer.Ordinal);
    private readonly string? _modelsPath;
    private readonly bool _modelNetworkEnabled;
    private ModelConfig _config;

    private ModelRuntimeSnapshot _snapshot = new()
    {
        All = [],
        Available = [],
        ConfiguredProviders = [],
        StoredProviders = [],
        Auth = [],
    };

    private int _availabilityRefreshSeq;
    private int _availabilityErrorSeq;
    private readonly Dictionary<string, int> _providerAvailabilitySeq = new(StringComparer.Ordinal);
    private string? _availabilityError;
    private readonly Dictionary<string, Task> _credentialOperations = new(StringComparer.Ordinal);

    private ModelRuntime(
        RuntimeCredentials credentials,
        ModelConfig config,
        string? modelsPath,
        IModelsStore modelsStore,
        IReadOnlyList<IProvider> providers,
        bool modelNetworkEnabled)
    {
        _credentials = credentials;
        _config = config;
        _modelsPath = modelsPath;
        _modelNetworkEnabled = modelNetworkEnabled;
        foreach (var provider in providers) _defaultBuiltins[provider.Id] = provider;
        foreach (var (providerId, provider) in _defaultBuiltins) _builtins[providerId] = provider;
        _models = new Models(new CreateModelsOptions { Credentials = credentials, ModelsStore = modelsStore });
        RebuildProviders();
    }

    public static async Task<ModelRuntime> CreateAsync(CreateModelRuntimeOptions? options = null)
    {
        options ??= new CreateModelRuntimeOptions();
        var env = options.Env ?? Environment.GetEnvironmentVariable;
        var credentials = new RuntimeCredentials(options.Credentials ?? AuthStorage.Create(options.AuthPath));
        var modelsPath = options.ModelsPathDisabled
            ? null
            : options.ModelsPath ?? NodePath.Join(Config.GetAgentDir(), "models.json");
        var config = await ModelConfig.LoadAsync(modelsPath).ConfigureAwait(false);
        var modelsStore = options.ModelsStore
            ?? (modelsPath is not null
                ? new FileModelsStore(
                    options.ModelsStorePath ?? NodePath.Join(NodePath.Dirname(modelsPath), "models-store.json"))
                : new InMemoryCodingAgentModelsStore());
        var builtinModelDataGeneratedAt = All.GetBuiltinModelDataGeneratedAt();
        var providers = All.BuiltinProviders()
            .Select(provider => provider.Id == "radius"
                ? provider
                : (IProvider)new RemoteCatalogProvider(provider, options.CatalogBaseUrl, builtinModelDataGeneratedAt))
            .ToList();

        // TS: `process.env.PI_OFFLINE === undefined` — presence, not truthiness.
        var runtime = new ModelRuntime(
            credentials, config, modelsPath, modelsStore, providers, env("PI_OFFLINE") is null);
        runtime.ConfigureRadiusProviders();
        runtime.RebuildProviders();

        var refreshFromNetwork = runtime._modelNetworkEnabled && options.AllowModelNetwork == true;
        CancellationTokenSource? timeoutSource = null;
        if (refreshFromNetwork && options.ModelRefreshTimeoutMs is { } modelRefreshTimeoutMs)
        {
            timeoutSource = new CancellationTokenSource();
            timeoutSource.CancelAfter(modelRefreshTimeoutMs);
        }

        using var signal = AbortSignals.Combine(options.Signal, timeoutSource?.Token ?? default);
        try
        {
            if (options.RefreshOnCreate != false)
            {
                await runtime.RefreshAsync(new ModelsRefreshOptions
                {
                    AllowNetwork = refreshFromNetwork,
                    Signal = signal.Token,
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            timeoutSource?.Dispose();
        }

        return runtime;
    }

    // ================================================================= provider composition

    private void ConfigureRadiusProviders()
    {
        _builtins.Clear();
        foreach (var (providerId, provider) in _defaultBuiltins) _builtins[providerId] = provider;
        foreach (var providerId in _config.GetProviderIds())
        {
            var config = _config.GetProvider(providerId);
            if (config?.OAuth != "radius" || string.IsNullOrEmpty(config.BaseUrl)) continue;
            _builtins[providerId] = Pi.Ai.Providers.Radius.Provider(new RadiusProviderOptions
            {
                Id = providerId,
                Name = config.Name ?? providerId,
                // TS: `baseUrl.replace(/\/v1\/?$/u, "")` — `$` without the `m` flag matches only the very end.
                Gateway = Regex.Replace(config.BaseUrl, @"/v1/?\z", ""),
            });
        }
    }

    private HashSet<string> ProviderIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var providerId in _builtins.Keys) ids.Add(providerId);
        foreach (var providerId in _nativeExtensionProviders.Keys) ids.Add(providerId);
        foreach (var providerId in _config.GetProviderIds()) ids.Add(providerId);
        foreach (var providerId in _extensionProviders.Keys) ids.Add(providerId);
        foreach (var providerId in _virtualModels.Keys) ids.Add(providerId);
        return ids;
    }

    /// <summary>
    /// Provider without virtual models, or null when only virtual models define it.
    /// Corresponds to TS <c>recomposeProvider</c>.
    /// </summary>
    private IProvider? RecomposeProvider(string providerId)
    {
        var provider = ComposeProvider(providerId);
        var virtualModels = _virtualModels.TryGetValue(providerId, out var entries)
            ? entries.Values.Select(entry => entry.Model).ToList()
            : [];
        if (virtualModels.Count > 0) _models.SetProvider(VirtualModels.WithVirtualModels(providerId, provider, virtualModels));
        else if (provider is not null) _models.SetProvider(provider);
        else _models.DeleteProvider(providerId);
        return provider;
    }

    /// <summary>The provider without virtual models, or null when nothing defines it.</summary>
    private IProvider? ComposeProvider(string providerId)
    {
        var baseProvider = _nativeExtensionProviders.GetValueOrDefault(providerId)
            ?? _builtins.GetValueOrDefault(providerId);
        var extension = _extensionProviders.GetValueOrDefault(providerId);
        if (_config.GetProvider(providerId) is null && extension is null)
        {
            // No overlays: use the builtin untouched so its auth/login/stream behavior is exact.
            _compositionErrors.Remove(providerId);
            return baseProvider;
        }

        try
        {
            var provider = ProviderComposer.ComposeModelProvider(providerId, baseProvider, _config, extension);
            _compositionErrors.Remove(providerId);
            return provider;
        }
        catch (Exception error)
        {
            _compositionErrors[providerId] = error.Message;
            return baseProvider;
        }
    }

    private void RebuildProviders()
    {
        _models.ClearProviders();
        _compositionErrors.Clear();
        foreach (var providerId in ProviderIds()) RecomposeProvider(providerId);
        UpdateModelSnapshot();
    }

    private void UpdateModelSnapshot()
    {
        var all = _models.GetModels();
        _snapshot = _snapshot with
        {
            All = all,
            Available = all.Where(model => _snapshot.ConfiguredProviders.Contains(model.Provider)).ToList(),
        };
    }

    // ================================================================= availability

    private async Task RunAvailabilityRefreshAsync(int seq, int errorSeq, CancellationToken signal)
    {
        var providers = _models.GetProviders();
        var availableTask = _models.GetAvailableAsync(null, signal);
        var checksTask = Task.WhenAll(providers.Select(async provider =>
            (ProviderId: provider.Id, Check: await _models.CheckAuthAsync(provider.Id, signal).ConfigureAwait(false))));
        var credentialsTask = _credentials.ListAsync(signal);
        await Task.WhenAll(availableTask, checksTask, credentialsTask).ConfigureAwait(false);

        var available = await availableTask.ConfigureAwait(false);
        var checks = await checksTask.ConfigureAwait(false);
        var credentials = await credentialsTask.ConfigureAwait(false);
        if (seq != _availabilityRefreshSeq) return;

        var auth = new Dictionary<string, AuthCheck>(StringComparer.Ordinal);
        var configuredProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (providerId, check) in checks)
        {
            if (check is null) continue;
            auth[providerId] = check;
            configuredProviders.Add(providerId);
        }

        _snapshot = new ModelRuntimeSnapshot
        {
            All = _models.GetModels(),
            Available = available,
            ConfiguredProviders = configuredProviders,
            StoredProviders = credentials.Select(entry => entry.ProviderId).ToHashSet(StringComparer.Ordinal),
            Auth = auth,
        };
        if (errorSeq == _availabilityErrorSeq) _availabilityError = null;
    }

    private async Task QueueAvailabilityRefreshAsync(CancellationToken signal)
    {
        var seq = ++_availabilityRefreshSeq;
        foreach (var providerId in _providerAvailabilitySeq.Keys.ToList())
        {
            _providerAvailabilitySeq[providerId]++;
        }

        var errorSeq = ++_availabilityErrorSeq;
        try
        {
            await RunAvailabilityRefreshAsync(seq, errorSeq, signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (errorSeq == _availabilityErrorSeq && !signal.IsCancellationRequested)
            {
                _availabilityError = error.Message;
            }

            throw;
        }
    }

    private async Task RefreshProviderAvailabilityAsync(string providerId, CancellationToken signal)
    {
        // Invalidate any full availability pass that started before this credential change.
        _availabilityRefreshSeq++;
        var providerSeq = _providerAvailabilitySeq.GetValueOrDefault(providerId) + 1;
        _providerAvailabilitySeq[providerId] = providerSeq;
        var errorSeq = ++_availabilityErrorSeq;
        try
        {
            var availableTask = _models.GetAvailableAsync(providerId, signal);
            var authTask = _models.CheckAuthAsync(providerId, signal);
            var credentialTask = _credentials.ReadAsync(providerId, signal);
            await Task.WhenAll(availableTask, authTask, credentialTask).ConfigureAwait(false);

            var available = await availableTask.ConfigureAwait(false);
            var auth = await authTask.ConfigureAwait(false);
            var credential = await credentialTask.ConfigureAwait(false);
            signal.ThrowIfCancellationRequested();
            if (_providerAvailabilitySeq.GetValueOrDefault(providerId) != providerSeq) return;

            var configuredProviders = new HashSet<string>(_snapshot.ConfiguredProviders, StringComparer.Ordinal);
            var storedProviders = new HashSet<string>(_snapshot.StoredProviders, StringComparer.Ordinal);
            var authByProvider = CopyAuth(_snapshot.Auth);
            if (auth is not null)
            {
                configuredProviders.Add(providerId);
                authByProvider[providerId] = auth;
            }
            else
            {
                configuredProviders.Remove(providerId);
                authByProvider.Remove(providerId);
            }

            if (credential is not null) storedProviders.Add(providerId);
            else storedProviders.Remove(providerId);

            var all = _models.GetModels();
            var availableById = new Dictionary<string, ModelSpec>(StringComparer.Ordinal);
            foreach (var model in _snapshot.Available.Where(model => model.Provider != providerId).Concat(available))
            {
                availableById[$"{model.Provider}\0{model.Id}"] = model;
            }

            _snapshot = new ModelRuntimeSnapshot
            {
                All = all,
                Available = all.Where(model => availableById.ContainsKey($"{model.Provider}\0{model.Id}")).ToList(),
                ConfiguredProviders = configuredProviders,
                StoredProviders = storedProviders,
                Auth = authByProvider,
            };
            if (errorSeq == _availabilityErrorSeq) _availabilityError = null;
        }
        catch (Exception error)
        {
            if (_providerAvailabilitySeq.GetValueOrDefault(providerId) == providerSeq
                && errorSeq == _availabilityErrorSeq
                && !signal.IsCancellationRequested)
            {
                _availabilityError = error.Message;
            }

            throw;
        }
    }

    // ================================================================= catalog accessors

    public IReadOnlyList<IProvider> GetProviders() => _models.GetProviders();

    public IProvider? GetProvider(string providerId) => _models.GetProvider(providerId);

    public IReadOnlyList<ModelSpec> GetModels(string? providerId = null) => _models.GetModels(providerId);

    /// <summary>
    /// The resolver only ever asks for the whole catalog, so <see cref="IModelResolverRuntime"/> declares the
    /// parameterless form while this class's accessor keeps its optional provider filter.
    /// </summary>
    IReadOnlyList<ModelSpec> IModelResolverRuntime.GetModels() => GetModels();

    public ModelSpec? GetModel(string providerId, string modelId) => _models.GetModel(providerId, modelId);

    public IReadOnlyList<ModelSpec> GetModelsOfType(ModelType type, string? providerId = null)
        => _models.GetModelsOfType(type, providerId);

    public ModelSpec? GetModelOfType(ModelType type, string providerId, string modelId)
        => _models.GetModelOfType(type, providerId, modelId);

    public IReadOnlyList<ModelSpec> GetAllModels(string? providerId = null) => _models.GetAllModels(providerId);

    public Task<IReadOnlyList<ModelSpec>> GetAvailableOfTypeAsync(
        ModelType type, string? providerId = null, CancellationToken signal = default)
        => _models.GetAvailableOfTypeAsync(type, providerId, signal);

    public Task<IReadOnlyList<ModelSpec>> GetAllAvailableAsync(string? providerId = null, CancellationToken signal = default)
        => _models.GetAllAvailableAsync(providerId, signal);

    public Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken signal = default)
        => _models.CheckAuthAsync(providerId, signal);

    public async Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(string? providerId = null, CancellationToken signal = default)
    {
        // TS: `if (providerId)` — an empty id falls through to the queued full pass.
        if (!string.IsNullOrEmpty(providerId))
        {
            var errorSeq = ++_availabilityErrorSeq;
            try
            {
                var available = await _models.GetAvailableAsync(providerId, signal).ConfigureAwait(false);
                if (errorSeq == _availabilityErrorSeq) _availabilityError = null;
                return available;
            }
            catch (Exception error)
            {
                if (errorSeq == _availabilityErrorSeq && !signal.IsCancellationRequested)
                {
                    _availabilityError = error.Message;
                }

                throw;
            }
        }

        await QueueAvailabilityRefreshAsync(signal).ConfigureAwait(false);
        return _snapshot.Available;
    }

    public IReadOnlyList<ModelSpec> GetAvailableSnapshot() => _snapshot.Available;

    public string? GetError()
    {
        var errors = new List<string>();
        if (_config.Error is { } configError) errors.Add(configError);
        foreach (var (providerId, error) in _compositionErrors)
        {
            errors.Add($"Provider \"{providerId}\": {error}");
        }

        if (_availabilityError is { } availabilityError) errors.Add($"Availability refresh: {availabilityError}");
        return errors.Count > 0 ? string.Join("\n\n", errors) : null;
    }

    public ProviderConfigInput? GetRegisteredProviderConfig(string providerId)
        => _extensionProviders.GetValueOrDefault(providerId);

    public IReadOnlyList<string> GetRegisteredProviderIds()
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var providerId in _extensionProviders.Keys)
        {
            if (seen.Add(providerId)) ids.Add(providerId);
        }

        foreach (var providerId in _nativeExtensionProviders.Keys)
        {
            if (seen.Add(providerId)) ids.Add(providerId);
        }

        return ids;
    }

    public IProvider? GetRegisteredNativeProvider(string providerId)
        => _nativeExtensionProviders.GetValueOrDefault(providerId);

    /// <summary>Compatibility fallback for <c>ModelRegistry</c> when provider auth is unconfigured.</summary>
    public ProviderComposer.CompatibilityRequestConfig GetCompatibilityRequestConfig(ModelSpec model)
        => ProviderComposer.ResolveCompatibilityRequestConfig(
            model,
            _config.GetProvider(model.Provider),
            _extensionProviders.GetValueOrDefault(model.Provider));

    public bool IsUsingOAuth(string providerId)
        => _snapshot.Auth.TryGetValue(providerId, out var check) && check.Type == CredentialKind.OAuth;

    public bool IsUsingSubscription(string providerId)
        => IsUsingOAuth(providerId) && _models.GetProvider(providerId)?.Auth?.OAuth?.IsSubscription == true;

    public bool HasConfiguredAuth(string providerId) => _snapshot.ConfiguredProviders.Contains(providerId);

    // ================================================================= auth

    public Task<AuthResult?> GetAuthAsync(string providerId, ModelRuntimeAuthOverrides? overrides = null)
        => _models.GetAuthAsync(providerId, ToModelsOverrides(overrides));

    public async Task<AuthResult?> GetAuthAsync(ModelSpec model, ModelRuntimeAuthOverrides? overrides = null)
    {
        var resolution = await GetAuthAsync(model.Provider, overrides).ConfigureAwait(false);
        if (resolution is null) return null;
        var configuredHeaders = ProviderComposer.ResolveConfiguredModelHeaders(
            model,
            _config.GetProvider(model.Provider),
            _extensionProviders.GetValueOrDefault(model.Provider),
            MergeEnv(resolution.Env, overrides?.Env));
        return resolution with
        {
            Auth = resolution.Auth with
            {
                Headers = MergeConfiguredHeaders(resolution.Auth.Headers, configuredHeaders),
            },
        };
    }

    public Task SetRuntimeApiKeyAsync(string providerId, string apiKey, CancellationToken signal = default)
        => EnqueueCredentialOperationAsync(providerId, signal, async () =>
        {
            _credentials.SetRuntimeApiKey(providerId, apiKey);
            await SynchronizeCredentialStateAsync(
                providerId,
                CredentialSynchronizationOperation.SetRuntimeApiKey,
                new Credential.ApiKey(apiKey),
                signal).ConfigureAwait(false);
            return true;
        });

    public Task RemoveRuntimeApiKeyAsync(string providerId, CancellationToken signal = default)
        => EnqueueCredentialOperationAsync(providerId, signal, async () =>
        {
            _credentials.RemoveRuntimeApiKey(providerId);
            await SynchronizeCredentialStateAsync(
                providerId, CredentialSynchronizationOperation.RemoveRuntimeApiKey, null, signal)
                .ConfigureAwait(false);
            return true;
        });

    public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken signal = default)
        => _credentials.ListAsync(signal);

    public AuthStatus GetProviderAuthStatus(string providerId)
    {
        if (_credentials.HasRuntimeApiKey(providerId))
        {
            return new AuthStatus { Configured = true, Source = AuthStatusSource.Runtime };
        }

        if (_snapshot.StoredProviders.Contains(providerId))
        {
            return new AuthStatus { Configured = true, Source = AuthStatusSource.Stored };
        }

        var configured = ProviderComposer.ConfiguredRequestAuthStatus(
            _config.GetProvider(providerId), _extensionProviders.GetValueOrDefault(providerId));
        if (configured is not null) return configured;

        return _snapshot.Auth.TryGetValue(providerId, out var check)
            ? new AuthStatus { Configured = true, Source = AuthStatusSource.Environment, Label = check.Source }
            : new AuthStatus { Configured = false };
    }

    private static ModelsAuthOverrides? ToModelsOverrides(ModelRuntimeAuthOverrides? overrides)
        => overrides is null
            ? null
            : new ModelsAuthOverrides
            {
                ApiKey = overrides.ApiKey,
                Env = overrides.Env,
                MinOAuthValidityMs = overrides.MinOAuthValidityMs,
                Signal = overrides.Signal,
            };

    // ================================================================= credential operations

    /// <summary>
    /// Serializes credential mutations per provider. The returned task stops waiting on abort
    /// <em>before</em> the task starts; once started, the mutation runs to completion.
    /// </summary>
    private Task<T> EnqueueCredentialOperationAsync<T>(string providerId, CancellationToken signal, Func<Task<T>> task)
    {
        var previous = _credentialOperations.GetValueOrDefault(providerId) ?? Task.CompletedTask;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = RunAsync();
        var tail = operation.ContinueWith(
            static settled => _ = settled.Exception,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        _credentialOperations[providerId] = tail;
        _ = tail.ContinueWith(
            _ =>
            {
                if (_credentialOperations.GetValueOrDefault(providerId) == tail) _credentialOperations.Remove(providerId);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return AwaitStartedThenOperationAsync();

        async Task<T> RunAsync()
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // A failed predecessor must not poison this operation.
            }

            signal.ThrowIfCancellationRequested();
            started.TrySetResult(true);
            return await task().ConfigureAwait(false);
        }

        async Task<T> AwaitStartedThenOperationAsync()
        {
            await AbortSignals.RaceWithAsync(started.Task, signal).ConfigureAwait(false);
            return await operation.ConfigureAwait(false);
        }
    }

    private async Task SynchronizeCredentialStateAsync(
        string providerId,
        CredentialSynchronizationOperation operation,
        Credential? credential,
        CancellationToken signal)
    {
        try
        {
            signal.ThrowIfCancellationRequested();
            RecomposeProvider(providerId);
            if (_compositionErrors.TryGetValue(providerId, out var compositionError))
            {
                throw new InvalidOperationException(compositionError);
            }

            var result = await _models.RefreshAsync(new ModelsRefreshOptions
            {
                AllowNetwork = false,
                Providers = [providerId],
                Signal = signal,
            }).ConfigureAwait(false);
            if (result.Aborted) signal.ThrowIfCancellationRequested();
            if (result.Errors.TryGetValue(providerId, out var refreshError)) throw refreshError;
            UpdateModelSnapshot();
            await RefreshProviderAvailabilityAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (Exception cause)
        {
            throw new CredentialSynchronizationError(providerId, operation, credential, cause);
        }
    }

    public Task<Credential> LoginAsync(
        string providerId, CredentialKind type, ProviderAuthInteraction interaction, LoginOptions? options = null)
    {
        var signal = interaction.Signal;
        return EnqueueCredentialOperationAsync(providerId, signal, async () =>
        {
            var credential = await _models.LoginAsync(providerId, type, interaction, interaction, options)
                .ConfigureAwait(false);
            await SynchronizeCredentialStateAsync(
                providerId, CredentialSynchronizationOperation.Login, credential, signal).ConfigureAwait(false);
            return credential;
        });
    }

    public Task LogoutAsync(string providerId, CancellationToken signal = default)
        => EnqueueCredentialOperationAsync(providerId, signal, async () =>
        {
            await _models.LogoutAsync(providerId, signal).ConfigureAwait(false);
            await SynchronizeCredentialStateAsync(
                providerId, CredentialSynchronizationOperation.Logout, null, signal).ConfigureAwait(false);
            return true;
        });

    // ================================================================= requests

    private async Task<PreparedChatRequest> PrepareChatRequestAsync(
        ModelSpec model, IReadOnlyDictionary<string, object?>? options)
    {
        var provider = _models.GetProvider(model.Provider)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");
        var resolution = await GetAuthAsync(model, new ModelRuntimeAuthOverrides
        {
            ApiKey = ReadOption<string>(options, "apiKey"),
            Env = ReadOption<IReadOnlyDictionary<string, string>>(options, "env"),
            Signal = ReadSignal(options),
        }).ConfigureAwait(false);
        if (resolution is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        }

        var requestOptions = options is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(options, StringComparer.Ordinal);
        var transform = ReadOption<Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>>(
            options, Models.TransformHeadersOptionKey);
        requestOptions.Remove(Models.TransformHeadersOptionKey);

        IReadOnlyDictionary<string, string?>? headers = MergeHeaders(
            resolution.Auth.Headers, ReadOption<IReadOnlyDictionary<string, string?>>(options, "headers"));
        if (transform is not null)
        {
            headers = await transform(headers ?? new Dictionary<string, string?>()).ConfigureAwait(false);
        }

        var requestModel = resolution.Auth.BaseUrl is null ? model : model with { BaseUrl = resolution.Auth.BaseUrl };
        requestOptions["apiKey"] = ReadOption<string>(options, "apiKey") ?? resolution.Auth.ApiKey;
        requestOptions["headers"] = headers;
        requestOptions["env"] = MergeEnv(resolution.Env, ReadOption<IReadOnlyDictionary<string, string>>(options, "env"));
        return new PreparedChatRequest(provider, requestModel, requestOptions);
    }

    private async Task<PreparedImagesRequest> PrepareImagesRequestAsync(ModelSpec model, ModelsImagesOptions? options)
    {
        var provider = _models.GetProvider(model.Provider)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");
        var resolution = await GetAuthAsync(model, new ModelRuntimeAuthOverrides
        {
            ApiKey = options?.ApiKey,
            Env = options?.Env,
            Signal = options?.Signal ?? default,
        }).ConfigureAwait(false);
        if (resolution is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        }

        IReadOnlyDictionary<string, string?>? headers = MergeHeaders(resolution.Auth.Headers, options?.Headers);
        if (options?.TransformHeaders is { } transform)
        {
            headers = await transform(headers ?? new Dictionary<string, string?>()).ConfigureAwait(false);
        }

        var requestModel = resolution.Auth.BaseUrl is null ? model : model with { BaseUrl = resolution.Auth.BaseUrl };
        return new PreparedImagesRequest(provider, requestModel, new ImagesOptions
        {
            Signal = options?.Signal ?? default,
            ApiKey = options?.ApiKey ?? resolution.Auth.ApiKey,
            Headers = headers,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            Metadata = options?.Metadata,
            Env = MergeEnv(resolution.Env, options?.Env),
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
        });
    }

    private async Task<PreparedClassifierRequest> PrepareClassifierRequestAsync(
        ModelSpec model, ModelsClassifierOptions? options)
    {
        var provider = _models.GetProvider(model.Provider)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");
        var resolution = await GetAuthAsync(model, new ModelRuntimeAuthOverrides
        {
            ApiKey = options?.ApiKey,
            Env = options?.Env,
            Signal = options?.Signal ?? default,
        }).ConfigureAwait(false);
        if (resolution is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        }

        IReadOnlyDictionary<string, string?>? headers = MergeHeaders(resolution.Auth.Headers, options?.Headers);
        if (options?.TransformHeaders is { } transform)
        {
            headers = await transform(headers ?? new Dictionary<string, string?>()).ConfigureAwait(false);
        }

        var requestModel = resolution.Auth.BaseUrl is null ? model : model with { BaseUrl = resolution.Auth.BaseUrl };
        return new PreparedClassifierRequest(provider, requestModel, new ClassifierOptions
        {
            Signal = options?.Signal ?? default,
            ApiKey = options?.ApiKey ?? resolution.Auth.ApiKey,
            Headers = headers,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            Temperature = options?.Temperature,
            Env = MergeEnv(resolution.Env, options?.Env),
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
        });
    }

    public IAssistantMessageEventStream Stream(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => LazyStream.Run(model, async () =>
        {
            ModelOperations.AssertChatModel(model);
            var prepared = await PrepareChatRequestAsync(model, options).ConfigureAwait(false);
            return prepared.Provider.Stream(prepared.Model, context, prepared.Options);
        });

    public async Task<AssistantMessage> CompleteAsync(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
        => await Stream(model, context, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    public IAssistantMessageEventStream StreamSimple(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
    {
        if (VirtualModels.IsVirtualModel(model))
        {
            // Requests outside the agent loop are routed here. Callers sized them before routing, so
            // cap the output budget to the routed model.
            return LazyStream.Run(model, async () =>
            {
                var route = await ResolveModelAsync(model, context, new ResolveModelOptions
                {
                    Reason = VirtualModels.RouteReasons.Direct,
                    ThinkingLevel = ReadOption<string>(options, "reasoning") ?? "off",
                    Signal = ReadSignal(options),
                }).ConfigureAwait(false);

                var limit = route.Model.MaxTokens;
                var requested = ReadMaxTokens(options);
                var maxTokens = requested is { } requestedTokens && limit > 0
                    ? Math.Min(requestedTokens, limit)
                    : requested;
                var reasoning = route.ThinkingLevel == "off" ? null : route.ThinkingLevel;

                // Caller credentials were resolved for the virtual model's provider. Another provider
                // resolves its own, so they are not sent to the wrong vendor.
                var requestOptions = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (options is not null)
                {
                    foreach (var (key, value) in options)
                    {
                        if (key is "apiKey" or "headers" or "env") continue;
                        requestOptions[key] = value;
                    }
                }

                if (route.Model.Provider == model.Provider)
                {
                    requestOptions["apiKey"] = ReadOption<string>(options, "apiKey");
                    requestOptions["headers"] = ReadOption<IReadOnlyDictionary<string, string?>>(options, "headers");
                    requestOptions["env"] = ReadOption<IReadOnlyDictionary<string, string>>(options, "env");
                }

                if (maxTokens is { } budget) requestOptions["maxTokens"] = budget;
                requestOptions["reasoning"] = reasoning;
                return StreamSimple(route.Model, context, requestOptions);
            });
        }

        return LazyStream.Run(model, async () =>
        {
            ModelOperations.AssertChatModel(model);
            var prepared = await PrepareChatRequestAsync(model, options).ConfigureAwait(false);
            return prepared.Provider.StreamSimple(prepared.Model, context, prepared.Options);
        });
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
        => await StreamSimple(model, context, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    public IAssistantMessageEventStream StreamDeferred(
        ModelSpec model, DeferredHandle handle, IReadOnlyDictionary<string, object?>? options = null)
        => LazyStream.Run(model, async () =>
        {
            ModelOperations.AssertChatModel(model);
            var prepared = await PrepareChatRequestAsync(model, options).ConfigureAwait(false);
            if (!prepared.Provider.SupportsFetchDeferred)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            }

            return prepared.Provider.StreamDeferred(prepared.Model, handle, prepared.Options)
                ?? throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
        });

    public async Task<AssistantMessage> FetchDeferredAsync(
        ModelSpec model, DeferredHandle handle, IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
        => await StreamDeferred(model, handle, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    public async Task CancelDeferredAsync(
        ModelSpec model, DeferredHandle handle, IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
    {
        ModelOperations.AssertChatModel(model);
        var prepared = await PrepareChatRequestAsync(model, options).ConfigureAwait(false);
        if (!prepared.Provider.SupportsCancelDeferred)
        {
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support deferred responses");
        }

        await prepared.Provider.CancelDeferredAsync(prepared.Model, handle, prepared.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AssistantImages> GenerateImagesAsync(
        ModelSpec model, ImagesContext context, ModelsImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ModelOperations.AssertImageModel(model);
            var prepared = await PrepareImagesRequestAsync(model, options).ConfigureAwait(false);
            if (prepared.Provider is not IImagesProvider images)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support image generation");
            }

            return await images.GenerateImagesAsync(prepared.Model, context, prepared.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ModelOperations.ImageErrorResult(model, error, options?.Signal.IsCancellationRequested == true);
        }
    }

    public async Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ModelsClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ModelOperations.AssertClassifierModel(model);
            var prepared = await PrepareClassifierRequestAsync(model, options).ConfigureAwait(false);
            if (prepared.Provider is not IClassifierProvider classifier)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support classification");
            }

            return await classifier.ClassifyAsync(prepared.Model, context, prepared.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ModelOperations.ClassifierErrorResult(model, error, options?.Signal.IsCancellationRequested == true);
        }
    }

    // ================================================================= refresh

    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        _config = await ModelConfig.LoadAsync(_modelsPath).ConfigureAwait(false);
        ConfigureRadiusProviders();
        if (options.Providers is not null)
        {
            foreach (var providerId in new HashSet<string>(options.Providers, StringComparer.Ordinal))
            {
                RecomposeProvider(providerId);
            }

            UpdateModelSnapshot();
        }
        else
        {
            RebuildProviders();
        }

        // C# `Models.RefreshAsync` always returns a result, so the TS fallback for pre-ModelsStore
        // published builds is not needed (difference C60).
        var result = await _models.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = options.AllowNetwork ?? _modelNetworkEnabled,
            Providers = options.Providers,
            Force = options.Force,
            Signal = options.Signal,
        }).ConfigureAwait(false);

        var errors = new Dictionary<string, Exception>(StringComparer.Ordinal);
        foreach (var (providerId, error) in result.Errors) errors[providerId] = error;
        UpdateModelSnapshot();

        if (options.Providers is not null)
        {
            await Task.WhenAll(new HashSet<string>(options.Providers, StringComparer.Ordinal).Select(async providerId =>
            {
                try
                {
                    await RefreshProviderAvailabilityAsync(providerId, options.Signal).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (!options.Signal.IsCancellationRequested) errors[providerId] = error;
                }
            })).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await QueueAvailabilityRefreshAsync(options.Signal).ConfigureAwait(false);
            }
            catch
            {
                // Availability errors are recorded by the latest pass; refreshed models remain usable.
            }
        }

        return new ModelsRefreshResult(result.Aborted || options.Signal.IsCancellationRequested, errors);
    }

    // ================================================================= registration

    public void RegisterNativeProvider(IProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new InvalidOperationException("Provider id must not be empty.");
        _extensionProviders.Remove(provider.Id);
        _nativeExtensionProviders[provider.Id] = provider;
        RecomposeProvider(provider.Id);
        UpdateModelSnapshot();
        var auth = provider.Auth;
        MarkProvisionallyConfigured(
            provider.Id,
            ProviderComposer.ConfiguredRequestAuthStatus(_config.GetProvider(provider.Id), null),
            auth?.OAuth is not null && auth.ApiKey is null ? CredentialKind.OAuth : CredentialKind.ApiKey);
        FireAndForgetRefresh();
    }

    /// <summary>
    /// Marks a newly registered provider as configured when it has a stored credential or a configured API
    /// key. Availability checks run asynchronously, and callers such as initial model selection read the
    /// snapshot before they finish. The next availability pass replaces this entry.
    /// </summary>
    private void MarkProvisionallyConfigured(string providerId, AuthStatus? configuredStatus, CredentialKind type)
    {
        if (!_snapshot.StoredProviders.Contains(providerId) && configuredStatus?.Configured != true) return;
        var configuredProviders = new HashSet<string>(_snapshot.ConfiguredProviders, StringComparer.Ordinal)
        {
            providerId,
        };
        var auth = CopyAuth(_snapshot.Auth);

        // Never clobber a real check result.
        if (!auth.ContainsKey(providerId))
        {
            auth[providerId] = new AuthCheck { Type = type, Source = "configured provider" };
        }

        _snapshot = _snapshot with
        {
            Auth = auth,
            ConfiguredProviders = configuredProviders,
            Available = _snapshot.All.Where(model => configuredProviders.Contains(model.Provider)).ToList(),
        };
    }

    public void RegisterProvider(string providerId, ProviderConfigInput config)
    {
        // Validate the incoming registration on its own, like the legacy registry:
        // a broken re-registration must throw without touching the stored config.
        ProviderComposer.ValidateExtensionProvider(
            providerId, _builtins.GetValueOrDefault(providerId), _config.GetProvider(providerId), config);
        _nativeExtensionProviders.Remove(providerId);

        // Re-registration merges defined values over the previous registration and preserves
        // undefined ones, matching the legacy ModelRegistry contract.
        var previous = _extensionProviders.GetValueOrDefault(providerId);
        var effective = new ProviderConfigInput
        {
            Name = config.Name ?? previous?.Name,
            BaseUrl = config.BaseUrl ?? previous?.BaseUrl,
            ApiKey = config.ApiKey ?? previous?.ApiKey,
            Api = config.Api ?? previous?.Api,
            StreamSimple = config.StreamSimple ?? previous?.StreamSimple,
            Images = config.Images ?? previous?.Images,
            Classifiers = config.Classifiers ?? previous?.Classifiers,
            Headers = config.Headers ?? previous?.Headers,
            AuthHeader = config.AuthHeader ?? previous?.AuthHeader,
            OAuth = config.OAuth ?? previous?.OAuth,
            Models = config.Models ?? previous?.Models,
            RefreshModels = config.RefreshModels ?? previous?.RefreshModels,
        };
        _extensionProviders[providerId] = effective;
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        MarkProvisionallyConfigured(
            providerId,
            ProviderComposer.ConfiguredRequestAuthStatus(_config.GetProvider(providerId), effective),
            effective.OAuth is not null && effective.ApiKey is null ? CredentialKind.OAuth : CredentialKind.ApiKey);
        FireAndForgetRefresh();
    }

    public void UnregisterProvider(string providerId)
    {
        _extensionProviders.Remove(providerId);
        _nativeExtensionProviders.Remove(providerId);
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        FireAndForgetRefresh();
    }

    /// <summary>
    /// Registers a virtual model under <c>definition.provider</c>, which may also list physical models or
    /// several virtual models. Re-registering the same provider and id replaces the virtual model.
    /// Throws when the id belongs to a physical model of that provider.
    /// </summary>
    public void RegisterVirtualModel(VirtualModelDefinition definition)
    {
        var providerId = definition.Provider;
        var id = definition.Id;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("Virtual model provider and id must not be empty.");
        }

        var existing = _models.GetModel(providerId, id);
        if (existing is not null && !VirtualModels.IsVirtualModel(existing))
        {
            throw new InvalidOperationException($"Virtual model {providerId}/{id} conflicts with a physical model.");
        }

        if (!_virtualModels.TryGetValue(providerId, out var models))
        {
            models = new Dictionary<string, RegisteredVirtualModel>(StringComparer.Ordinal);
            _virtualModels[providerId] = models;
        }

        models[id] = new RegisteredVirtualModel(VirtualModels.CreateVirtualModel(definition), definition.Route);
        if (RecomposeProvider(providerId) is null && !_snapshot.ConfiguredProviders.Contains(providerId))
        {
            // A provider of only virtual models needs no credentials. Mark it configured now: session
            // restore checks auth before the refresh below lands.
            var auth = CopyAuth(_snapshot.Auth);
            auth[providerId] = new AuthCheck { Type = CredentialKind.ApiKey, Source = "virtual" };
            _snapshot = _snapshot with
            {
                Auth = auth,
                ConfiguredProviders = new HashSet<string>(_snapshot.ConfiguredProviders, StringComparer.Ordinal)
                {
                    providerId,
                },
            };
        }

        UpdateModelSnapshot();
        FireAndForgetRefresh();
    }

    public void UnregisterVirtualModel(string providerId, string id)
    {
        if (!_virtualModels.TryGetValue(providerId, out var models) || !models.Remove(id)) return;
        if (models.Count == 0) _virtualModels.Remove(providerId);
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        FireAndForgetRefresh();
    }

    private void FireAndForgetRefresh()
    {
        var refresh = RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
        PendingBackgroundRefresh = refresh.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>
    /// The refresh a registration call started in the background. TS fires and forgets it
    /// (<c>void this.refresh({ allowNetwork: false })</c>); this port keeps the handle so tests can await a
    /// settled runtime instead of racing the pass. Not part of the TS surface.
    /// </summary>
    internal Task? PendingBackgroundRefresh { get; private set; }

    // ================================================================= virtual routing

    /// <summary>
    /// Asks a virtual model's router for the model and thinking level of one request. The router must return
    /// a physical catalog model whose provider has credentials; the thinking level is clamped to that model.
    /// Throws when routing fails.
    /// </summary>
    public async Task<ModelRoute> ResolveModelAsync(
        ModelSpec model, IReadOnlyList<ChatMessage> messages, ResolveModelOptions options)
    {
        var name = $"Virtual model {model.Provider}/{model.Id}";
        var virtualModel = _virtualModels.TryGetValue(model.Provider, out var entries)
            && entries.TryGetValue(model.Id, out var entry)
                ? entry
                : null;
        if (virtualModel is null) throw new InvalidOperationException($"{name} is not registered.");

        var failed = options.Failed;
        var latest = VirtualModels.FindLatestResponse(messages);
        var previousModel = latest is not null ? GetPhysicalModel(latest.Provider ?? "", latest.Model ?? "") : null;

        // A failed routing attempt names the virtual model; there is no physical request to report.
        var failedModel = failed is not null ? GetPhysicalModel(failed.Provider ?? "", failed.Model ?? "") : null;
        var route = await virtualModel.Route(new ModelRouteRequest
        {
            Model = model,
            ThinkingLevel = options.ThinkingLevel,
            Reason = options.Reason,
            Previous = previousModel is not null && latest is not null
                ? new ModelRoutePrevious(previousModel, ThinkingLevelText(latest.ThinkingLevel))
                : null,
            Failed = failedModel is not null && failed is not null
                ? new ModelRouteFailure(failedModel, ThinkingLevelText(failed.ThinkingLevel), failed)
                : null,
            State = options.State,
            Messages = messages,
            Signal = options.Signal,
        }).ConfigureAwait(false);

        var target = GetPhysicalModel(route.Model.Provider, route.Model.Id);
        var routed = $"{name} routed to {route.Model.Provider}/{route.Model.Id}";
        if (target is null) throw new InvalidOperationException($"{routed}, which is not a physical model.");
        if (!HasConfiguredAuth(target.Provider))
        {
            throw new InvalidOperationException($"{routed}, which has no credentials.");
        }

        return new ModelRoute
        {
            Model = target,
            ThinkingLevel = Pi.Ai.Models.ThinkingLevels.Clamp(target, route.ThinkingLevel),
            State = route.State,
        };
    }

    /// <summary>A catalog chat model that is not virtual.</summary>
    public ModelSpec? GetPhysicalModel(string providerId, string modelId)
    {
        var model = _models.GetModel(providerId, modelId);
        return model is not null && !VirtualModels.IsVirtualModel(model) ? model : null;
    }

    // ================================================================= helpers

    /// <summary>TS <c>thinkingLevel</c> strings, i.e. the wire spelling rather than the enum name.</summary>
    private static string? ThinkingLevelText(ThinkingLevel? level)
        => level is { } value ? ThinkingLevels.ToText(value) : null;

    private static Dictionary<string, AuthCheck> CopyAuth(IReadOnlyDictionary<string, AuthCheck> source)
    {
        var copy = new Dictionary<string, AuthCheck>(StringComparer.Ordinal);
        foreach (var (providerId, check) in source) copy[providerId] = check;
        return copy;
    }

    /// <summary>
    /// Merges request headers: an override replaces same-named base headers case-insensitively and keeps
    /// the override's spelling. Corresponds to the TS <c>mergeHeaders</c>.
    /// </summary>
    private static Dictionary<string, string?>? MergeHeaders(
        IReadOnlyDictionary<string, string?>? baseHeaders,
        IReadOnlyDictionary<string, string?>? overrideHeaders)
    {
        if (baseHeaders is null && overrideHeaders is null) return null;
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (baseHeaders is not null)
        {
            foreach (var (name, value) in baseHeaders) merged[name] = value;
        }

        if (overrideHeaders is not null)
        {
            foreach (var (name, value) in overrideHeaders)
            {
                foreach (var existing in merged.Keys.ToList())
                {
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) merged.Remove(existing);
                }

                merged[name] = value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Overload for non-nullable override values. C# erases nullable annotations from generic type
    /// arguments, so this cannot be an overload of <see cref="MergeHeaders(IReadOnlyDictionary{string, string?}?, IReadOnlyDictionary{string, string?}?)"/>.
    /// </summary>
    private static Dictionary<string, string?>? MergeConfiguredHeaders(
        IReadOnlyDictionary<string, string?>? baseHeaders,
        IReadOnlyDictionary<string, string>? overrideHeaders)
        => MergeHeaders(
            baseHeaders,
            overrideHeaders?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));

    private static Dictionary<string, string>? MergeEnv(
        IReadOnlyDictionary<string, string>? baseEnv, IReadOnlyDictionary<string, string>? overrideEnv)
    {
        if (baseEnv is null && overrideEnv is null) return null;
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseEnv is not null)
        {
            foreach (var (name, value) in baseEnv) merged[name] = value;
        }

        if (overrideEnv is not null)
        {
            foreach (var (name, value) in overrideEnv) merged[name] = value;
        }

        return merged;
    }

    private static T? ReadOption<T>(IReadOnlyDictionary<string, object?>? options, string key)
        where T : class
        => options is not null && options.TryGetValue(key, out var value) ? value as T : null;

    private static CancellationToken ReadSignal(IReadOnlyDictionary<string, object?>? options)
        => options is not null && options.TryGetValue("signal", out var value) && value is CancellationToken token
            ? token
            : default;

    /// <summary>
    /// Reads the numeric <c>maxTokens</c> option. Callers in this repository store it as <c>long</c>
    /// (<c>Pi.Durable/Harness/Provider.cs</c>); ints and doubles are accepted so an extension that writes
    /// the JS-shaped number still works.
    /// </summary>
    private static long? ReadMaxTokens(IReadOnlyDictionary<string, object?>? options)
        => options is not null && options.TryGetValue("maxTokens", out var value)
            ? value switch
            {
                long number => number,
                int number => number,
                double number => (long)number,
                _ => null,
            }
            : null;
}
