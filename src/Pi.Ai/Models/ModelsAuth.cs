using Pi.Ai.Auth;
using Pi.Ai.Utils;

namespace Pi.Ai.Models;

/// <summary>构造 <see cref="Models"/> 的选项。对应 TS <c>CreateModelsOptions</c>。</summary>
public sealed record CreateModelsOptions
{
    public ICredentialStore? Credentials { get; init; }

    public IModelsStore? ModelsStore { get; init; }

    public IAuthContext? AuthContext { get; init; }
}

/// <summary>请求级认证覆盖项。对应 TS <c>AuthResolutionOverrides</c> 的 <c>Models</c> 用法。</summary>
public sealed record ModelsAuthOverrides
{
    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>
/// <see cref="Models"/> 的凭据、目录刷新与可用性层。对应 TS <c>ModelsImpl</c>（models.ts）
/// 的认证编排部分：provider 拥有请求行为，<see cref="Models"/> 解析认证并按 model.provider 分派。
/// </summary>
/// <remarks>
/// 已知差异：
/// <list type="bullet">
/// <item>C43：TS 的 <c>Provider.auth</c> 是必填成员，因此不存在「无认证语义」的 provider。
/// C# <see cref="IProvider.Auth"/> 是默认接口成员（null），<see cref="ApplyAuthAsync"/> 把 null
/// 视为无认证语义并跳过解析——这让测试替身与 faux provider 不必构造认证，
/// 而所有真实 provider 都设有 <c>Auth</c>，走与 TS 完全一致的路径。</item>
/// <item>C44：TS 的 <c>AbortSignal</c> 取消以 <c>signal.reason</c> 呈现，C# 以
/// <see cref="OperationCanceledException"/> 呈现。</item>
/// <item>C45：请求变换 <c>transformHeaders</c> 以选项字典的 <c>"transformHeaders"</c> 键承载
/// （TS 是 <c>ModelsRequestTransforms</c> 的具名成员），与本解决方案其余流选项的字典约定一致。</item>
/// </list>
/// </remarks>
public sealed partial class Models
{
    /// <summary>Options key carrying the request-level header transform (difference C45).</summary>
    public const string TransformHeadersOptionKey = "transformHeaders";

    private readonly ICredentialStore _credentials;
    private readonly IModelsStore _modelsStore;
    private readonly IAuthContext _authContext;
    private readonly Dictionary<string, int> _refreshGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _refreshControllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _publicationChains = new(StringComparer.Ordinal);

    public Models() : this(null)
    {
    }

    public Models(CreateModelsOptions? options)
    {
        _credentials = options?.Credentials ?? new InMemoryCredentialStore();
        _modelsStore = options?.ModelsStore ?? new InMemoryModelsStore();
        _authContext = options?.AuthContext ?? DefaultAuthContext.Instance;
    }

    /// <summary>凭据存储（catalog 刷新与登录/登出共用）。</summary>
    public ICredentialStore Credentials => _credentials;

    /// <summary>模型目录存储。</summary>
    public IModelsStore Store => _modelsStore;

    // ------------------------------------------------------------------ refresh

    /// <summary>使 provider 的进行中刷新失效，并中止其刷新控制器。</summary>
    private int SupersedeProviderRefresh(string providerId)
    {
        var generation = _refreshGenerations.GetValueOrDefault(providerId) + 1;
        _refreshGenerations[providerId] = generation;
        if (_refreshControllers.Remove(providerId, out var previous)) previous.Cancel();
        return generation;
    }

    private (int Generation, CancellationTokenSource Controller) BeginProviderRefresh(string providerId)
    {
        var generation = SupersedeProviderRefresh(providerId);
        var controller = new CancellationTokenSource();
        _refreshControllers[providerId] = controller;
        return (generation, controller);
    }

    /// <summary>
    /// 带代次校验的目录发布：同一 provider 的发布串行化，且被更新的刷新取代后不再落盘。
    /// 对应 TS <c>publishProviderModels</c>。
    /// </summary>
    private async Task<bool> PublishProviderModelsAsync(
        string providerId, int generation, CancellationToken signal, ModelsPublication publication)
    {
        var previous = _publicationChains.GetValueOrDefault(providerId) ?? Task.CompletedTask;
        var queued = PublishAsync(previous);
        var tail = queued.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        _publicationChains[providerId] = tail;
        _ = tail.ContinueWith(
            _ =>
            {
                if (_publicationChains.GetValueOrDefault(providerId) == tail) _publicationChains.Remove(providerId);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return await AbortSignals.RaceWithAsync(queued, signal).ConfigureAwait(false);

        async Task<bool> PublishAsync(Task previousTask)
        {
            try
            {
                await previousTask.ConfigureAwait(false);
            }
            catch
            {
                // 前一个发布链的失败不影响本代次。
            }

            if (signal.IsCancellationRequested || _refreshGenerations.GetValueOrDefault(providerId) != generation)
            {
                return false;
            }

            if (publication.PersistDeleted)
            {
                await _modelsStore.DeleteAsync(providerId, null, signal).ConfigureAwait(false);
            }
            else if (publication.Persist is { } entry)
            {
                await _modelsStore.WriteAsync(providerId, CloneEntry(entry), null, signal).ConfigureAwait(false);
            }

            if (signal.IsCancellationRequested || _refreshGenerations.GetValueOrDefault(providerId) != generation)
            {
                return false;
            }

            publication.Update?.Invoke();
            return true;
        }
    }

    /// <summary>发布前深拷贝目录条目，避免 provider 事后改动已落盘的数据（对齐 TS <c>structuredClone</c>）。</summary>
    private static ModelsStoreEntry CloneEntry(ModelsStoreEntry entry)
        => new()
        {
            Models = entry.Models.Select(model => model with { Input = [.. model.Input] }).ToList(),
            LastModified = entry.LastModified,
            CheckedAt = entry.CheckedAt,
            Etag = entry.Etag,
        };

    /// <summary>一次 provider 刷新阶段：读存储 → 恢复缓存 → （允许时）联网抓取。</summary>
    private async Task RunProviderRefreshPhaseAsync(
        IProvider provider,
        Func<RefreshModelsContext, Task> refreshModels,
        Credential? credential,
        bool allowNetwork,
        bool? force,
        int generation,
        CancellationToken signal)
    {
        var stored = await _modelsStore.ReadAsync(provider.Id, null, signal).ConfigureAwait(false);
        await refreshModels(new RefreshModelsContext
        {
            Credential = credential,
            Stored = stored is null ? null : WithKnownModelTypes(CloneEntry(stored)),
            Publish = publication => PublishProviderModelsAsync(provider.Id, generation, signal, publication),
            AllowNetwork = allowNetwork,
            Force = allowNetwork ? force : null,
            Signal = signal,
        }).ConfigureAwait(false);
    }

    /// <summary>丢弃本版本不认识的模型类别（对应 TS <c>withKnownModelTypes</c>）。</summary>
    private static ModelsStoreEntry WithKnownModelTypes(ModelsStoreEntry entry)
        => entry with { Models = entry.Models.Where(HasKnownModelType).ToList() };

    private static bool HasKnownModelType(ModelSpec model)
        => model.Type is ModelType.Chat or ModelType.Image or ModelType.Classifier;

    /// <summary>
    /// 并发刷新选定的已配置动态 provider（省略 <c>providers</c> 即全部）。provider 错误与取消都不抛出；
    /// 静态、未知与未配置 provider 被跳过。对应 TS <c>Models.refresh()</c>。
    /// </summary>
    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        var allowNetwork = options.AllowNetwork ?? true;
        var callerSignal = AbortSignals.Combine(options.Signal).Token;
        var errors = new Dictionary<string, Exception>(StringComparer.Ordinal);
        if (callerSignal.IsCancellationRequested) return new ModelsRefreshResult(true, errors);

        var selected = options.Providers is null ? null : new HashSet<string>(options.Providers, StringComparer.Ordinal);
        var refreshable = _providers.Values
            .Where(provider => provider.RefreshModels is not null
                && (selected is null || selected.Contains(provider.Id)))
            .ToList();

        var refresh = Task.WhenAll(refreshable.Select(async provider =>
        {
            var refreshModels = provider.RefreshModels!;
            var (generation, controller) = BeginProviderRefresh(provider.Id);
            using var linked = AbortSignals.Combine(callerSignal, controller.Token);
            var signal = linked.Token;
            var operation = RefreshOneAsync();

            async Task RefreshOneAsync()
            {
                Credential? storedCredential = null;
                Exception? credentialError = null;
                try
                {
                    storedCredential = await ReadCredentialAsync(provider.Id, signal).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    credentialError = error;
                }

                // 在认证解析或联网之前先恢复缓存的 provider 状态。
                await RunProviderRefreshPhaseAsync(
                    provider, refreshModels, storedCredential, false, null, generation, signal).ConfigureAwait(false);
                if (credentialError is not null) throw credentialError;
                if (!allowNetwork || signal.IsCancellationRequested) return;

                var credential = await ResolveRefreshCredentialAsync(provider, storedCredential, signal)
                    .ConfigureAwait(false);
                if (credential is null) return;
                await RunProviderRefreshPhaseAsync(
                    provider, refreshModels, credential, true, options.Force, generation, signal).ConfigureAwait(false);
            }

            try
            {
                await AbortSignals.RaceWithAsync(operation, signal).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!signal.IsCancellationRequested)
                {
                    errors[provider.Id] = error is ModelsError
                        ? error
                        : new ModelsError(ModelsErrorCode.ModelSource,
                            $"Model refresh failed for {provider.Id}", error);
                }
            }
            finally
            {
                if (_refreshControllers.GetValueOrDefault(provider.Id) == controller)
                {
                    _refreshControllers.Remove(provider.Id);
                }

                controller.Dispose();
            }
        }));

        try
        {
            await AbortSignals.RaceWithAsync(refresh, callerSignal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!callerSignal.IsCancellationRequested) throw;
            _ = error;
        }

        return new ModelsRefreshResult(callerSignal.IsCancellationRequested, errors);
    }

    /// <summary>刷新阶段使用的生效凭据：OAuth 过期则锁内刷新，api-key 走 provider 解析。对应 TS <c>resolveRefreshCredential</c>。</summary>
    private async Task<Credential?> ResolveRefreshCredentialAsync(
        IProvider provider, Credential? stored, CancellationToken signal)
    {
        if (stored is Credential.OAuth storedOAuth)
        {
            var oauth = provider.Auth?.OAuth;
            if (oauth is null) return null;
            if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < storedOAuth.Expires) return storedOAuth;
            if (signal.IsCancellationRequested) return null;
            // 已开始的刷新不受取消或后续刷新取代影响，轮换后的 refresh token 一定被持久化。
            return await AuthResolve.RefreshStoredOAuthCredentialAsync(
                _credentials, provider.Id, oauth,
                current => DateTimeOffset.Now.ToUnixTimeMilliseconds() >= current.Expires,
                signal).ConfigureAwait(false);
        }

        var apiKey = provider.Auth?.ApiKey;
        if (apiKey is null) return null;
        var credential = stored as Credential.ApiKey;
        var result = await apiKey.ResolveAsync(credential, _authContext, signal).ConfigureAwait(false);
        return result is null ? null : new Credential.ApiKey(result.Auth.ApiKey, result.Env);
    }

    private async Task<Credential?> ReadCredentialAsync(string providerId, CancellationToken signal)
    {
        try
        {
            return await _credentials.ReadAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store read failed for {providerId}", error);
        }
    }

    // ------------------------------------------------------------------ availability

    private async Task<AuthCheck?> CheckProviderAuthAsync(
        IProvider provider, Credential? credential, CancellationToken signal)
    {
        if (credential is Credential.OAuth)
        {
            return provider.Auth?.OAuth is not null ? new AuthCheck { Source = "OAuth", Type = CredentialKind.OAuth } : null;
        }

        var apiKey = provider.Auth?.ApiKey;
        if (apiKey is null) return null;

        var result = await AuthResolve.ResolveProviderAuthAsync(
            provider.Id,
            provider.Auth!,
            _credentials,
            _authContext,
            new AuthResolutionOverrides { Signal = signal },
            signal).ConfigureAwait(false);
        return result is null ? null : new AuthCheck { Source = result.Source, Type = CredentialKind.ApiKey };
    }

    /// <summary>检查 provider 是否有完整认证配置（不刷新 OAuth）。对应 TS <c>checkAuth()</c>。</summary>
    public async Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken signal = default)
    {
        var check = CheckAsync();
        return await AbortSignals.RaceWithAsync(check, signal).ConfigureAwait(false);

        async Task<AuthCheck?> CheckAsync()
        {
            signal.ThrowIfCancellationRequested();
            var provider = _providers.GetValueOrDefault(providerId);
            if (provider is null) return null;
            return await CheckProviderAuthAsync(
                provider, await ReadCredentialAsync(providerId, signal).ConfigureAwait(false), signal)
                .ConfigureAwait(false);
        }
    }

    private async Task<List<(IProvider Provider, Credential? Credential, AuthCheck? Auth)>> GetAuthenticatedProvidersAsync(
        string? providerId, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var providers = providerId is not null
            ? (_providers.GetValueOrDefault(providerId) is { } single ? new List<IProvider> { single } : [])
            : _providers.Values.ToList();

        var checks = new List<(IProvider, Credential?, AuthCheck?)>();
        foreach (var provider in providers)
        {
            var credential = await ReadCredentialAsync(provider.Id, signal).ConfigureAwait(false);
            var auth = await CheckProviderAuthAsync(provider, credential, signal).ConfigureAwait(false);
            checks.Add((provider, credential, auth));
        }

        return checks.Where(entry => entry.Item3 is not null).ToList();
    }

    /// <summary>返回 provider 认证完整的 chat 模型。对应 TS <c>getAvailable()</c>。</summary>
    public async Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(
        string? providerId = null, CancellationToken signal = default)
    {
        var available = AvailableAsync();
        return await AbortSignals.RaceWithAsync(available, signal).ConfigureAwait(false);

        async Task<IReadOnlyList<ModelSpec>> AvailableAsync()
        {
            var providers = await GetAuthenticatedProvidersAsync(providerId, signal).ConfigureAwait(false);
            var models = new List<ModelSpec>();
            foreach (var (provider, credential, _) in providers)
            {
                var listed = SafeModels(provider);
                models.AddRange(provider.FilterModels(listed, credential));
            }

            return models;
        }
    }

    /// <summary>返回某一类别中 provider 认证完整的模型。对应 TS <c>getAvailableOfType()</c>。</summary>
    public async Task<IReadOnlyList<ModelSpec>> GetAvailableOfTypeAsync(
        ModelType type, string? providerId = null, CancellationToken signal = default)
    {
        var all = await GetAllAvailableAsync(providerId, signal).ConfigureAwait(false);
        return all.Where(model => model.Type == type).ToList();
    }

    /// <summary>返回全部类别中 provider 认证完整的模型。对应 TS <c>getAllAvailable()</c>。</summary>
    public async Task<IReadOnlyList<ModelSpec>> GetAllAvailableAsync(
        string? providerId = null, CancellationToken signal = default)
    {
        var available = AvailableAsync();
        return await AbortSignals.RaceWithAsync(available, signal).ConfigureAwait(false);

        async Task<IReadOnlyList<ModelSpec>> AvailableAsync()
        {
            var providers = await GetAuthenticatedProvidersAsync(providerId, signal).ConfigureAwait(false);
            var models = new List<ModelSpec>();
            foreach (var (provider, credential, _) in providers)
            {
                var listed = SafeAllModels(provider);
                if (provider.FilterAllModels(listed, credential) is { } filtered)
                {
                    models.AddRange(filtered);
                    continue;
                }

                if (provider.FilterModels(SafeModels(provider), credential) is not { } availableChat)
                {
                    models.AddRange(listed);
                    continue;
                }

                // 未实现 filterAllModels 时，chat 模型按 filterModels 的结果过滤，其余类别保留。
                var availableChatIds = availableChat.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
                models.AddRange(listed.Where(model =>
                    model.Type != ModelType.Chat || availableChatIds.Contains(model.Id)));
            }

            return models;
        }
    }

    // ------------------------------------------------------------------ auth

    /// <summary>
    /// 解析 provider 作用域认证（按 provider id 或按模型）。未知或未配置 provider 返回 null。
    /// 对应 TS <c>getAuth()</c>。
    /// </summary>
    public async Task<AuthResult?> GetAuthAsync(
        string providerId, ModelsAuthOverrides? overrides = null, CancellationToken signal = default)
    {
        var effectiveSignal = overrides?.Signal.CanBeCanceled == true ? overrides.Signal : signal;
        var provider = _providers.GetValueOrDefault(providerId);
        if (provider is null) return null;
        return await AuthResolve.ResolveProviderAuthAsync(
            providerId,
            provider.Auth ?? new ProviderAuth(),
            _credentials,
            _authContext,
            new AuthResolutionOverrides
            {
                ApiKey = overrides?.ApiKey,
                Env = overrides?.Env,
                Signal = effectiveSignal,
            },
            effectiveSignal).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析模型请求认证：provider 认证再叠加模型自带的静态请求头。
    /// 对应 TS <c>getAuth(model)</c>。
    /// </summary>
    public async Task<AuthResult?> GetAuthAsync(
        ModelSpec model, ModelsAuthOverrides? overrides = null, CancellationToken signal = default)
    {
        var result = await GetAuthAsync(model.Provider, overrides, signal).ConfigureAwait(false);
        if (result is null || model.Headers is not { Count: > 0 } modelHeaders) return result;

        // ModelSpec.Headers 的值不可空，而认证头允许 null 值：显式协变（C# 可空注解不参与泛型协变）。
        var modelOverrides = modelHeaders.ToDictionary(
            pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        return result with { Auth = result.Auth with { Headers = MergeHeaders(result.Auth.Headers, modelOverrides) } };
    }

    /// <summary>运行 provider 自有的登录流程并持久化其凭据。对应 TS <c>login()</c>。</summary>
    public async Task<Credential> LoginAsync(
        string providerId,
        CredentialKind type,
        IAuthInteraction interaction,
        ProviderAuthInteraction providerInteraction,
        LoginOptions? options = null)
    {
        var signal = providerInteraction.Signal;
        signal.ThrowIfCancellationRequested();
        var provider = _providers.GetValueOrDefault(providerId)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {providerId}");
        var oauthMethod = provider.Auth?.OAuth;
        var apiKeyMethod = provider.Auth?.ApiKey;
        if (type == CredentialKind.OAuth ? oauthMethod is null : apiKeyMethod is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"{provider.Name} does not support {type} login");
        }

        var loginOperation = type == CredentialKind.OAuth
            ? LoginOAuthAsync(oauthMethod!)
            : LoginApiKeyAsync(apiKeyMethod!);
        var credential = await AbortSignals.RaceWithAsync(loginOperation, signal).ConfigureAwait(false);

        // 凭据落盘一旦开始就不再被取消，否则用户会看到「已登录但没存下来」。
        var mutationStarted = false;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = _credentials.ModifyAsync(providerId, _ =>
        {
            mutationStarted = true;
            started.TrySetResult(true);
            return Task.FromResult<Credential?>(credential);
        }, signal);
        _ = mutation.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        try
        {
            await WaitForMutationStartAsync().ConfigureAwait(false);
            await mutation.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            signal.ThrowIfCancellationRequested();
            _ = error;
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store modify failed for {providerId}", error);
        }

        return credential;

        async Task<Credential> LoginOAuthAsync(IOAuthAuth oauth)
            => await oauth.LoginAsync(providerInteraction, options, signal).ConfigureAwait(false);

        async Task<Credential> LoginApiKeyAsync(IApiKeyAuth apiKey)
            => await apiKey.LoginAsync(providerInteraction, signal).ConfigureAwait(false);

        async Task WaitForMutationStartAsync()
        {
            var aborted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = signal.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), aborted);

            var winner = await Task.WhenAny(started.Task, aborted.Task, mutation).ConfigureAwait(false);
            if (ReferenceEquals(winner, mutation))
            {
                await mutation.ConfigureAwait(false);
                return;
            }

            if (!mutationStarted)
            {
                signal.ThrowIfCancellationRequested();
                throw new OperationCanceledException(signal);
            }

            await mutation.ConfigureAwait(false);
        }
    }

    /// <summary>移除 provider 的存储凭据。对应 TS <c>logout()</c>。</summary>
    public async Task LogoutAsync(string providerId, CancellationToken signal = default)
    {
        signal.ThrowIfCancellationRequested();
        try
        {
            await _credentials.DeleteAsync(providerId, signal).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            signal.ThrowIfCancellationRequested();
            throw new ModelsError(ModelsErrorCode.Auth,
                $"Credential store delete failed for {providerId}", error);
        }
    }

    // ------------------------------------------------------------------ request auth

    /// <summary>
    /// 合并基础请求头与覆盖项：同名（不区分大小写）的覆盖项替换基础项，且沿用覆盖项的大小写。
    /// 对应 TS <c>mergeHeaders</c>。
    /// </summary>
    internal static IReadOnlyDictionary<string, string?>? MergeHeaders(
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

    private static IReadOnlyDictionary<string, string>? MergeEnv(
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

    /// <summary>
    /// 解析请求认证并把它并入请求选项：显式请求选项逐字段优先，<c>Models</c> 专属的
    /// <c>transformHeaders</c> 最后执行。对应 TS <c>applyAuth</c>。
    /// </summary>
    internal async Task<(ModelSpec RequestModel, IReadOnlyDictionary<string, object?>? RequestOptions)> ApplyAuthAsync(
        ModelSpec model, IReadOnlyDictionary<string, object?>? options, CancellationToken signal)
    {
        if (!_providers.ContainsKey(model.Provider))
        {
            throw new ModelsError(ModelsErrorCode.Provider, $"Unknown provider: {model.Provider}");
        }

        // 无认证语义的 provider（C# 默认接口成员，差异 C43）不需要解析。
        if (_providers[model.Provider].Auth is null) return (model, options);

        var resolution = await GetAuthAsync(model, new ModelsAuthOverrides
        {
            ApiKey = ReadOption<string>(options, "apiKey"),
            Env = ReadOption<IReadOnlyDictionary<string, string>>(options, "env"),
            Signal = signal,
        }, signal).ConfigureAwait(false);
        if (resolution is null)
        {
            throw new ModelsError(ModelsErrorCode.Auth, $"Provider is not configured: {model.Provider}");
        }

        var auth = resolution.Auth;
        var apiKey = ReadOption<string>(options, "apiKey") ?? auth.ApiKey;
        var headers = MergeHeaders(auth.Headers, ReadOption<IReadOnlyDictionary<string, string?>>(options, "headers"));
        if (ReadOption<Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>>(
                options, TransformHeadersOptionKey) is { } transform)
        {
            headers = await transform(headers ?? new Dictionary<string, string?>()).ConfigureAwait(false);
        }

        var requestEnv = MergeEnv(resolution.Env, ReadOption<IReadOnlyDictionary<string, string>>(options, "env"));
        var requestModel = auth.BaseUrl is null ? model : model with { BaseUrl = auth.BaseUrl };

        var requestOptions = options is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(options, StringComparer.Ordinal);
        requestOptions.Remove(TransformHeadersOptionKey);
        requestOptions["apiKey"] = apiKey;
        requestOptions["headers"] = headers;
        if (requestEnv is not null) requestOptions["env"] = requestEnv;

        return (requestModel, requestOptions);
    }

    private static T? ReadOption<T>(IReadOnlyDictionary<string, object?>? options, string key)
        where T : class
        => options is not null && options.TryGetValue(key, out var value) ? value as T : null;
}
