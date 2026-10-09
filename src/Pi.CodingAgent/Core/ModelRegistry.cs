using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Port of the TS <c>ResolvedRequestAuth</c> discriminated union: the request credentials resolved for
/// one model, or the message explaining why none could be.
/// </summary>
public abstract record ResolvedRequestAuth
{
    /// <summary>The TS <c>{ ok: true; apiKey?; headers?; baseUrl?; env? }</c>.</summary>
    /// <remarks>
    /// TS omits the <c>baseUrl</c> key entirely when the resolved value is falsy; C# carries <c>null</c>
    /// instead. All four members are absent-by-default in TS, so <c>null</c> is the faithful shape.
    /// </remarks>
    public sealed record Ok(
        string? ApiKey = null,
        IReadOnlyDictionary<string, string?>? Headers = null,
        string? BaseUrl = null,
        IReadOnlyDictionary<string, string>? Env = null) : ResolvedRequestAuth;

    /// <summary>The TS <c>{ ok: false; error }</c>.</summary>
    public sealed record Fail(string Error) : ResolvedRequestAuth;
}

/// <summary>
/// Synchronous compatibility facade exposed to extensions. Coding-agent internals use
/// <see cref="ModelRuntime"/> directly. Port of <c>core/model-registry.ts</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member forwards to <see cref="ModelRuntime"/>; this class adds no state and no behaviour of its
/// own. It exists because extensions were written against a synchronous catalog facade while the runtime
/// moved to the richer asynchronous surface.
/// </para>
/// <para>
/// Differences recorded against the TS original:
/// <list type="bullet">
/// <item>C62：TS 的 <c>ModelTypeMap[TType]</c> / <c>Api</c> 泛型参数在 C# 折叠为 <see cref="ModelSpec"/> /
/// <see cref="ModelType"/>（C# 的目录是单一具名记录，没有按类别分的类型族）。</item>
/// <item>C63：TS 的 <c>registerProvider(name)</c>（缺 config）在运行期抛
/// <c>Provider config is required when registering by name</c>；C# 拆成两个重载后该错误变为编译期约束，
/// 守卫不再可达。</item>
/// <item>C64：<c>ResolvedRequestAuth</c> 由两条成员联合改为 <see cref="ResolvedRequestAuth.Ok"/> /
/// <see cref="ResolvedRequestAuth.Fail"/> 两个子记录（本仓判别联合的既有惯例，见 <c>ProcessImageResult</c>）。</item>
/// <item>C65：<c>AuthOperationOptions</c>（<c>{ signal }</c>）一律展开为末尾的
/// <see cref="CancellationToken"/> 形参；<c>stream</c> / <c>complete</c> 额外多出可选 token
/// （TS 把它们放在选项里）。</item>
/// </list>
/// </para>
/// <para>
/// TS 在本模块末尾 re-export 了 <c>ProviderConfigInput</c> 与 <c>clearApiKeyCache</c>；C# 没有 re-export
/// 机制，调用方直接使用 <see cref="ProviderConfigInput"/> 与 <c>ProviderComposer.ClearApiKeyCache</c>。
/// </para>
/// </remarks>
public sealed class ModelRegistry(ModelRuntime runtime)
{
    private readonly ModelRuntime _runtime = runtime;

    /// <summary>Reload models.json asynchronously. Await before making synchronous registry reads.</summary>
    public Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
        => _runtime.RefreshAsync(options);

    public string? GetError() => _runtime.GetError();

    /// <summary>Every known chat model, in catalog order.</summary>
    public IReadOnlyList<ModelSpec> GetAll() => [.. _runtime.GetModels()];

    /// <summary>Chat models whose provider currently has working credentials.</summary>
    public IReadOnlyList<ModelSpec> GetAvailable() => [.. _runtime.GetAvailableSnapshot()];

    public ModelSpec? Find(string provider, string modelId) => _runtime.GetModel(provider, modelId);

    /// <summary>Find a model of a non-chat type, e.g. <c>FindOfType(ModelType.Classifier, "typesafe", "jev-latest")</c>.</summary>
    public ModelSpec? FindOfType(ModelType type, string provider, string modelId)
        => _runtime.GetModelOfType(type, provider, modelId);

    public bool HasConfiguredAuth(ModelSpec model) => _runtime.HasConfiguredAuth(model.Provider);

    /// <summary>
    /// Request-time credentials for one model. Never throws: failures come back as
    /// <see cref="ResolvedRequestAuth.Fail"/>.
    /// </summary>
    public async Task<ResolvedRequestAuth> GetApiKeyAndHeadersAsync(ModelSpec model)
    {
        try
        {
            var resolution = await _runtime.GetAuthAsync(model).ConfigureAwait(false);
            if (resolution is null)
            {
                // Unconfigured provider: the model's own static headers still apply, unless the model
                // insists on an Authorization header it can no longer build.
                var compatibility = _runtime.GetCompatibilityRequestConfig(model);
                return compatibility.AuthHeader
                    ? new ResolvedRequestAuth.Fail($"No API key found for \"{model.Provider}\"")
                    : new ResolvedRequestAuth.Ok(Headers: compatibility.Headers);
            }

            return new ResolvedRequestAuth.Ok(
                ApiKey: resolution.Auth.ApiKey,
                Headers: resolution.Auth.Headers,
                // TS spreads the key only when `baseUrl` is truthy, so an empty string counts as absent.
                BaseUrl: string.IsNullOrEmpty(resolution.Auth.BaseUrl) ? null : resolution.Auth.BaseUrl,
                Env: resolution.Env);
        }
        catch (Exception error)
        {
            // TS reads `error.cause`; ModelsError carries the cause as InnerException. When the cause is
            // absent it falls back to the error's own message — in C# only an Exception can be thrown, so
            // the TS `String(error)` arm has no counterpart.
            var message = error.InnerException?.Message ?? error.Message;
            return new ResolvedRequestAuth.Fail(
                message == "authHeader requires a resolved API key"
                    ? $"No API key found for \"{model.Provider}\""
                    : message);
        }
    }

    public AuthStatus GetProviderAuthStatus(string provider) => _runtime.GetProviderAuthStatus(provider);

    public IProvider? GetProvider(string provider) => _runtime.GetProvider(provider);

    /// <summary>Stream through the configured provider with request-time authentication.</summary>
    public IAssistantMessageEventStream Stream(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => _runtime.Stream(model, context, options);

    /// <summary>Stream with provider-neutral options and request-time authentication.</summary>
    public IAssistantMessageEventStream StreamSimple(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => _runtime.StreamSimple(model, context, options);

    public Task<AssistantMessage> CompleteAsync(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null,
        CancellationToken cancellationToken = default)
        => _runtime.CompleteAsync(model, context, options, cancellationToken);

    /// <summary>Every known model of a type (chat, image, classifier), optionally for one provider.</summary>
    public IReadOnlyList<ModelSpec> GetModelsOfType(ModelType type, string? provider = null)
        => _runtime.GetModelsOfType(type, provider);

    /// <summary>Models of a type whose provider has working credentials.</summary>
    public Task<IReadOnlyList<ModelSpec>> GetAvailableOfTypeAsync(
        ModelType type, string? provider = null, CancellationToken signal = default)
        => _runtime.GetAvailableOfTypeAsync(type, provider, signal);

    public ModelSpec? GetModelOfType(ModelType type, string provider, string modelId)
        => _runtime.GetModelOfType(type, provider, modelId);

    /// <summary>Classify structured state with request-time authentication. Never rejects.</summary>
    public Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ModelsClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runtime.ClassifyAsync(model, context, options, cancellationToken);

    /// <summary>Generate images with request-time authentication. Never rejects.</summary>
    public Task<AssistantImages> GenerateImagesAsync(
        ModelSpec model, ImagesContext context, ModelsImagesOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runtime.GenerateImagesAsync(model, context, options, cancellationToken);

    public string GetProviderDisplayName(string provider) => _runtime.GetProvider(provider)?.Name ?? provider;

    public Task<AuthResult?> GetProviderAuthAsync(string provider, ModelRuntimeAuthOverrides? overrides = null)
        => _runtime.GetAuthAsync(provider, overrides);

    /// <summary>
    /// The resolved API key for a provider, or <c>null</c> when it cannot be resolved. Mirrors the TS
    /// bare <c>catch {}</c>: every failure — including cancellation — is reported as "no key".
    /// </summary>
    public async Task<string?> GetApiKeyForProviderAsync(string provider)
    {
        try
        {
            return (await _runtime.GetAuthAsync(provider).ConfigureAwait(false))?.Auth.ApiKey;
        }
        catch
        {
            return null;
        }
    }

    public bool IsUsingOAuth(ModelSpec model) => _runtime.IsUsingOAuth(model.Provider);

    /// <summary>Register a native provider object (TS <c>registerProvider(provider)</c>).</summary>
    public void RegisterProvider(IProvider provider) => _runtime.RegisterNativeProvider(provider);

    /// <summary>Register a provider by id and config (TS <c>registerProvider(name, config)</c>).</summary>
    public void RegisterProvider(string providerName, ProviderConfigInput config)
        => _runtime.RegisterProvider(providerName, config);

    public void UnregisterProvider(string providerName) => _runtime.UnregisterProvider(providerName);

    public void RegisterVirtualModel(VirtualModelDefinition definition)
        => _runtime.RegisterVirtualModel(definition);

    public void UnregisterVirtualModel(string providerName, string id)
        => _runtime.UnregisterVirtualModel(providerName, id);

    public ProviderConfigInput? GetRegisteredProviderConfig(string providerName)
        => _runtime.GetRegisteredProviderConfig(providerName);

    public IProvider? GetRegisteredNativeProvider(string providerName)
        => _runtime.GetRegisteredNativeProvider(providerName);

    public IReadOnlyList<string> GetRegisteredProviderIds() => _runtime.GetRegisteredProviderIds();
}
