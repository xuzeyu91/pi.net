using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai;

/// <summary>注册 faux provider 后返回的操作句柄。对应 TS <c>FauxProviderRegistration</c>。</summary>
public sealed record FauxProviderRegistration
{
    /// <summary>注册的 api id（唯一）。</summary>
    public required string Api { get; init; }

    /// <summary>该 api 下的模型表。</summary>
    public required IReadOnlyList<ModelSpec> Models { get; init; }

    /// <summary>按 id 取模型。</summary>
    public required Func<string, ModelSpec?> GetModel { get; init; }

    /// <summary>可变脚本内核。</summary>
    public required Faux.FauxCore State { get; init; }

    /// <summary>替换脚本。</summary>
    public required Action<IEnumerable<Faux.Response>> SetResponses { get; init; }

    /// <summary>追加脚本。</summary>
    public required Action<IEnumerable<Faux.Response>> AppendResponses { get; init; }

    /// <summary>未回放响应数。</summary>
    public required Func<int> GetPendingResponseCount { get; init; }

    /// <summary>注销该 api（按 sourceId 分组）。</summary>
    public required Action Unregister { get; init; }
}

/// <summary>
/// 兼容旧全局 pi-ai API 表面的临时入口：api-dispatch 的 <c>stream()</c>/<c>complete()</c>
/// （带环境密钥注入）、api 注册表、生成目录读取（<c>getModel</c>/<c>getModels</c>/<c>getProviders</c>）。
/// 对应 TS <c>compat.ts</c>——新代码应使用 <c>CreateModels()</c> 与 provider 工厂。
/// </summary>
public static class Compat
{
    /// <summary>内建 API id（与 TS <c>BUILTIN_APIS</c> 的 10 个内建 API 一致）。</summary>
    public static readonly IReadOnlyList<string> BuiltinApiIds =
    [
        "anthropic-messages",
        "openai-completions",
        "openai-responses",
        "openai-codex-responses",
        "azure-openai-responses",
        "google-generative-ai",
        "google-vertex",
        "mistral-conversations",
        "bedrock-converse-stream",
        "pi-messages",
    ];

    private static readonly Dictionary<string, ApiProvider?> BuiltinApiInstances = new(StringComparer.Ordinal);
    private static readonly Pi.Ai.Models.Models CompatModels = All.BuiltinModels();

    static Compat() => RegisterBuiltInApiProviders();

    /// <summary>
    /// 注册内建 API 实现（不覆盖已有条目：compat 可能晚于测试/扩展加载），
    /// 并快照内建实例用于「模型归属内建 provider」判定。
    /// 对应 TS <c>registerBuiltInApiProviders()</c>。
    /// </summary>
    public static void RegisterBuiltInApiProviders()
    {
        ApiRegistry.RegisterBuiltInApiProviders();
        BuiltinApiInstances.Clear();
        foreach (var api in BuiltinApiIds)
        {
            BuiltinApiInstances[api] = ApiRegistry.GetApiProvider(api);
        }
    }

    /// <summary>重置注册表并重新注册内建实现。对应 TS <c>resetApiProviders()</c>。</summary>
    public static void ResetApiProviders()
    {
        ApiRegistry.ResetApiProviders();
        RegisterBuiltInApiProviders();
    }

    /// <summary>全部内建 provider id。@deprecated 用 <c>All.GetBuiltinProviders()</c> 或 <c>Models.GetProviders()</c>。</summary>
    public static IReadOnlyList<string> GetProviders() => All.GetBuiltinProviders();

    /// <summary>按 provider + id 取内建 chat 模型。@deprecated 用 <c>All.GetBuiltinModel()</c>。</summary>
    public static ModelSpec? GetModel(string provider, string modelId) => All.GetBuiltinModel(provider, modelId);

    /// <summary>按 provider 取全部内建 chat 模型。@deprecated 用 <c>All.GetBuiltinModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetModels(string provider) => All.GetBuiltinModels(provider);

    /// <summary>
    /// 完整选项流式入口。模型归属内建 provider 时走 provider 路径（cloudflare 未解析认证
    /// 时回退到 <c>Models</c>）；否则按 <c>model.api</c> 从 api 注册表解析实现。
    /// 对应 TS <c>compat.stream()</c>。
    /// </summary>
    public static IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
    {
        var builtinProvider = GetBuiltinProviderForModel(model);
        if (builtinProvider is not null)
        {
            if (model.Provider.StartsWith("cloudflare-", StringComparison.Ordinal)
                && !HasResolvedCloudflareAuth(options))
            {
                return CompatModels.StreamSimple(model, context, options);
            }
            return builtinProvider.Stream(model, context, WithEnvApiKey(model, options));
        }
        var provider = ResolveApiProvider(model.Api);
        return provider.StreamSimple(model, new TranscriptContext(context),
            ProviderStreamOptions.FromDictionary(WithEnvApiKey(model, options)), null, CancellationToken.None);
    }

    /// <summary>完整选项补全：等待终态并返回最终消息。对应 TS <c>compat.complete()</c>。</summary>
    public static async Task<AssistantMessage> CompleteAsync(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => await Stream(model, context, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>简单选项流式入口。对应 TS <c>compat.streamSimple()</c>。</summary>
    public static IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
    {
        var builtinProvider = GetBuiltinProviderForModel(model);
        if (builtinProvider is not null)
        {
            if (model.Provider.StartsWith("cloudflare-", StringComparison.Ordinal)
                && !HasResolvedCloudflareAuth(options))
            {
                return CompatModels.StreamSimple(model, context, options);
            }
            return builtinProvider.StreamSimple(model, context, WithEnvApiKey(model, options));
        }
        var provider = ResolveApiProvider(model.Api);
        return provider.StreamSimple(model, new TranscriptContext(context),
            ProviderStreamOptions.FromDictionary(WithEnvApiKey(model, options)), null, CancellationToken.None);
    }

    /// <summary>简单选项补全。对应 TS <c>compat.completeSimple()</c>。</summary>
    public static async Task<AssistantMessage> CompleteSimpleAsync(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => await StreamSimple(model, context, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 注册一个脚本化 faux provider（api id 唯一，避免并发测试互相覆盖）。
    /// 对应 TS <c>compat.registerFauxProvider()</c>。
    /// </summary>
    public static FauxProviderRegistration RegisterFauxProvider()
    {
        var core = new Faux.FauxCore();
        var sourceId = $"faux-provider-{Guid.NewGuid():N}";
        ApiRegistry.RegisterApiProvider(new ApiProvider
        {
            Api = core.Api,
            Stream = (model, _, _, _, cancellationToken) => core.Stream(model, cancellationToken),
            StreamSimple = (model, _, _, _, cancellationToken) => core.Stream(model, cancellationToken),
        }, sourceId);

        var models = new List<ModelSpec> { core.CreateModel() };
        return new FauxProviderRegistration
        {
            Api = core.Api,
            Models = models,
            GetModel = id => models.FirstOrDefault(model => model.Id == id),
            State = core,
            SetResponses = core.SetResponses,
            AppendResponses = core.AppendResponses,
            GetPendingResponseCount = () => core.PendingResponseCount,
            Unregister = () => ApiRegistry.UnregisterApiProviders(sourceId),
        };
    }

    /// <summary>
    /// 内建 provider 归属判定：仅当 <c>model.api</c> 的内建实现未被覆盖、且
    /// <c>model.provider</c> 已注册且其 chat 目录包含该 api 时返回该 provider。
    /// 对应 TS <c>getBuiltinProviderForModel</c>。
    /// </summary>
    private static IProvider? GetBuiltinProviderForModel(ModelSpec model)
    {
        if (!BuiltinApiInstances.TryGetValue(model.Api, out var builtinInstance)
            || !ReferenceEquals(ApiRegistry.GetApiProvider(model.Api), builtinInstance))
        {
            return null;
        }
        var provider = CompatModels.GetProvider(model.Provider);
        return provider is not null && provider.GetModels().Any(candidate => candidate.Api == model.Api)
            ? provider
            : null;
    }

    private static ApiProvider ResolveApiProvider(string api)
        => ApiRegistry.GetApiProvider(api)
            ?? throw new ModelsError(ModelsErrorCode.Provider, $"No API provider registered for api: {api}");

    private static bool HasExplicitApiKey(IReadOnlyDictionary<string, object?>? options)
        => options is not null
            && options.TryGetValue("apiKey", out var apiKey)
            && apiKey is string text
            && text.Trim().Length > 0;

    /// <summary>显式 apiKey 或已解析的 <c>cf-aig-authorization</c> 头都算已认证。对应 TS <c>hasResolvedCloudflareAuth</c>。</summary>
    private static bool HasResolvedCloudflareAuth(IReadOnlyDictionary<string, object?>? options)
    {
        if (HasExplicitApiKey(options)) return true;
        if (options is null || !options.TryGetValue("headers", out var headersValue)) return false;
        return headersValue is IReadOnlyDictionary<string, string?> headers
            && headers.Any(entry => string.Equals(entry.Key, "cf-aig-authorization", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entry.Value));
    }

    /// <summary>
    /// 无显式 apiKey 时从环境注入（<c>&lt;authenticated&gt;</c> 标记不注入）。
    /// 对应 TS <c>withEnvApiKey</c>。
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? WithEnvApiKey(ModelSpec model,
        IReadOnlyDictionary<string, object?>? options)
    {
        if (HasExplicitApiKey(options)) return options;
        var env = options is not null && options.TryGetValue("env", out var envValue)
            ? envValue as IReadOnlyDictionary<string, string>
            : null;
        var apiKey = EnvApiKeys.GetEnvApiKey(model.Provider, env);
        if (string.IsNullOrEmpty(apiKey) || apiKey == EnvApiKeys.AmbientAuthMarker) return options;
        var copy = options is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(options);
        copy["apiKey"] = apiKey;
        return copy;
    }
}
