using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Models;

/// <summary>
/// provider 可选的图片生成能力。对应 TS <c>createProvider</c> 的可选
/// <c>generateImages</c> 成员——支持专属图片模型的 provider 实现。
/// </summary>
public interface IImagesProvider
{
    Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options, CancellationToken cancellationToken);
}

/// <summary>provider 可选的结构化分类能力。对应 TS <c>createProvider</c> 的可选 <c>classify</c> 成员。</summary>
public interface IClassifierProvider
{
    Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options, CancellationToken cancellationToken);
}

/// <summary>
/// Provider 运行时单元：元数据 + 模型列举 + 操作（流式 / 延后 / 图片 / 分类）。
/// 对应 TS <c>Provider</c>（models.ts）。可选成员以默认实现给出，能力接口
/// （<see cref="IImagesProvider"/> / <see cref="IClassifierProvider"/>）承载一次性操作。
/// </summary>
public interface IProvider
{
    string Id { get; }

    string Name { get; }

    string? BaseUrl { get; }

    /// <summary>认证语义。对应 TS <c>Provider.auth</c>（工厂产出的 provider 才有）。</summary>
    ProviderAuth? Auth => null;

    /// <summary>
    /// 当前已知 chat 模型（同步）。抛错的实现被 <see cref="Models"/> 视为无模型
    /// （best-effort 契约，对齐 TS <c>getModels()</c>）。
    /// </summary>
    IReadOnlyList<ModelSpec> GetModels();

    /// <summary>全部类别的已知模型（chat + image + classifier）。对应 TS <c>getAllModels()</c>。</summary>
    IReadOnlyList<ModelSpec> GetAllModels() => GetModels();

    /// <summary>按凭据过滤可用 chat 模型。对应 TS <c>Provider.filterModels</c>。</summary>
    IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential) => models;

    /// <summary>流式：归一化转录分发给 provider。</summary>
    IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null);

    /// <summary>简单流式：文本/图片内容直传。</summary>
    IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null);

    /// <summary>
    /// 续取延后响应。不支持时返回 null（<see cref="Models.StreamDeferred"/> 据此报错）。
    /// 对应 TS 可选 <c>Provider.fetchDeferred</c>。
    /// </summary>
    IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null) => null;

    /// <summary>尽力取消延后响应。对应 TS 可选 <c>Provider.cancelDeferred</c>。</summary>
    Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => throw new ModelsError(ModelsErrorCode.Provider,
            $"Provider {Id} does not support deferred responses");
}

/// <summary>
/// 运行时 provider 集合 + 请求便捷门面。provider 拥有请求行为；
/// <see cref="Models"/> 按 model.provider 分发（requireChatProvider 校验模型归属）。
/// 对应 TS <c>Models</c>（models.ts 1256 行）的编排核心。
/// </summary>
public sealed class Models
{
    private readonly Dictionary<string, IProvider> _providers = [];

    /// <summary>注册（或替换）provider。</summary>
    public void SetProvider(IProvider provider) => _providers[provider.Id] = provider;

    /// <summary>移除 provider。</summary>
    public bool DeleteProvider(string id) => _providers.Remove(id);

    /// <summary>清空全部 provider。</summary>
    public void ClearProviders() => _providers.Clear();

    public IReadOnlyList<IProvider> GetProviders() => _providers.Values.ToList();

    public IProvider? GetProvider(string id) => _providers.GetValueOrDefault(id);

    /// <summary>
    /// 全部 chat 模型（best-effort：抛错的 provider 产出零模型）。
    /// 对应 TS <c>getModels()</c>。
    /// </summary>
    public IReadOnlyList<ModelSpec> GetModels(string? provider = null)
    {
        if (provider is not null)
        {
            return !_providers.TryGetValue(provider, out var entry)
                ? []
                : SafeModels(entry);
        }
        var models = new List<ModelSpec>();
        foreach (var entry in _providers.Values)
        {
            models.AddRange(SafeModels(entry));
        }
        return models;
    }

    /// <summary>按 provider + id 取 chat 模型。对应 TS <c>getModel()</c>。</summary>
    public ModelSpec? GetModel(string provider, string id)
        => SafeModels(_providers.GetValueOrDefault(provider)!)
            .FirstOrDefault(model => model.Id == id)
            ?? (_providers.TryGetValue(provider, out var entry)
                ? SafeModels(entry).FirstOrDefault(model => model.Id == id)
                : null);

    /// <summary>按类别取模型（可选 provider 过滤）。对应 TS <c>getModelsOfType()</c>。</summary>
    public IReadOnlyList<ModelSpec> GetModelsOfType(ModelType type, string? provider = null)
    {
        if (provider is not null)
        {
            return !_providers.TryGetValue(provider, out var entry)
                ? []
                : SafeModels(entry).Where(model => model.Type == type).ToList();
        }
        var models = new List<ModelSpec>();
        foreach (var entry in _providers.Values)
        {
            models.AddRange(SafeModels(entry).Where(model => model.Type == type));
        }
        return models;
    }

    /// <summary>按类别 + provider + id 取模型。对应 TS <c>getModelOfType()</c>。</summary>
    public ModelSpec? GetModelOfType(ModelType type, string provider, string id)
        => GetModelsOfType(type, provider).FirstOrDefault(model => model.Id == id);

    /// <summary>图片生成：按 model.provider 分发给实现 <see cref="IImagesProvider"/> 的 provider。
    /// 任何失败都转为 error 结果（不抛出）。对应 TS <c>Models.generateImages()</c>。</summary>
    public async Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ModelOperations.AssertImageModel(model);
            var provider = RequireProvider(model.Provider);
            if (provider is not IImagesProvider imagesProvider)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support image generation");
            }
            return await imagesProvider.GenerateImagesAsync(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ModelOperations.ImageErrorResult(model, error,
                options?.Signal.IsCancellationRequested == true);
        }
    }

    /// <summary>结构化分类：按 model.provider 分发给实现 <see cref="IClassifierProvider"/> 的 provider。
    /// 任何失败都转为 error 结果（不抛出）。对应 TS <c>Models.classify()</c>。</summary>
    public async Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            ModelOperations.AssertClassifierModel(model);
            var provider = RequireProvider(model.Provider);
            if (provider is not IClassifierProvider classifierProvider)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support classification");
            }
            return await classifierProvider.ClassifyAsync(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return ModelOperations.ClassifierErrorResult(model, error,
                options?.Signal.IsCancellationRequested == true);
        }
    }

    /// <summary>要求 provider 已注册。对应 TS <c>requireProvider</c>。</summary>
    private IProvider RequireProvider(string providerId)
        => _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new ModelsError(ModelsErrorCode.Provider, $"Provider {providerId} is not registered");

    /// <summary>
    /// 简单流式：按 model.provider 分发到所属 provider。
    /// 对应 TS <c>streamSimple()</c>（lazyStream 的延迟语义由调用方迭代触发保持一致）。
    /// </summary>
    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
    {
        var provider = RequireChatProvider(model);
        return provider.StreamSimple(model, context, options);
    }

    /// <summary>简单补全：等待终态并返回最终消息。对应 TS <c>completeSimple()</c>。</summary>
    public async Task<AssistantMessage> CompleteSimpleAsync(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
    {
        var stream = StreamSimple(model, context, options);
        return await stream.WaitForDoneAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全部类别的已知模型（可选 provider 过滤）。对应 TS <c>getAllModels()</c>。</summary>
    public IReadOnlyList<ModelSpec> GetAllModels(string? provider = null)
    {
        if (provider is not null)
        {
            return !_providers.TryGetValue(provider, out var entry) ? [] : SafeAllModels(entry);
        }
        var models = new List<ModelSpec>();
        foreach (var entry in _providers.Values) models.AddRange(SafeAllModels(entry));
        return models;
    }

    /// <summary>
    /// 续取延后响应：同步返回事件流，认证解析与 provider 分派在其后运行
    /// （<see cref="LazyStream.Run"/> 的延迟语义）。对应 TS <c>streamDeferred()</c>。
    /// </summary>
    public IAssistantMessageEventStream StreamDeferred(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null)
        => LazyStream.Run(model, () =>
        {
            var provider = RequireChatProvider(model);
            var stream = provider.StreamDeferred(model, handle, options)
                ?? throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {model.Provider} does not support deferred responses");
            return Task.FromResult(stream);
        });

    /// <summary>续取延后响应并等待终态。对应 TS <c>fetchDeferred()</c>。</summary>
    public async Task<AssistantMessage> FetchDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => await StreamDeferred(model, handle, options).WaitForDoneAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>尽力取消延后响应。对应 TS <c>cancelDeferred()</c>。</summary>
    public async Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
    {
        var provider = RequireChatProvider(model);
        await provider.CancelDeferredAsync(model, handle, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>chat 模型必须归属已注册 provider 且在其目录中。对应 TS <c>requireChatProvider</c>。</summary>
    private IProvider RequireChatProvider(ModelSpec model)
    {
        var provider = _providers.GetValueOrDefault(model.Provider)
            ?? throw new KeyNotFoundException($"Unknown model provider: {model.Provider}");
        if (SafeModels(provider).Any(known => known.Id == model.Id)) return provider;
        throw new KeyNotFoundException(
            $"Provider {model.Provider} does not list model {model.Id}");
    }

    /// <summary>best-effort 取模型（抛错实现产出零模型）。</summary>
    private static IReadOnlyList<ModelSpec> SafeModels(IProvider provider)
    {
        try
        {
            return provider.GetModels();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>best-effort 取全部类别模型（抛错实现产出零模型）。</summary>
    private static IReadOnlyList<ModelSpec> SafeAllModels(IProvider provider)
    {
        try
        {
            return provider.GetAllModels();
        }
        catch
        {
            return [];
        }
    }
}


/// <summary>
/// 思考档位工具。对应 TS <c>models.ts</c> 的 <c>getSupportedThinkingLevels</c> 与
/// <c>clampThinkingLevel</c>：档位顺序 off &lt; minimal &lt; low &lt; medium &lt; high &lt; xhigh &lt; max，
/// clamp 向上找不到就向下找最近可用档位。
/// </summary>
public static class ThinkingLevels
{
    /// <summary>档位全序。对应 TS <c>EXTENDED_THINKING_LEVELS</c>。</summary>
    public static readonly IReadOnlyList<string> ExtendedThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>模型支持的思考档位列表。对应 TS <c>getSupportedThinkingLevels</c>。</summary>
    public static IReadOnlyList<string> GetSupported(ModelSpec model)
    {
        if (!model.Reasoning) return ["off"];

        var levelMap = model.ThinkingLevelMap;
        var supported = new List<string>();
        foreach (var level in ExtendedThinkingLevels)
        {
            // TS：mapped === null → 禁用；xhigh/max 仅在映射存在（非 undefined）时可用；
            // 其余档位缺失视为可用（provider 接受缺省 effort 字符串）。
            if (levelMap is not null && levelMap.Has(level) && levelMap[level] is null) continue;
            if ((level == "xhigh" || level == "max")
                && (levelMap is null || !levelMap.Has(level))) continue;
            supported.Add(level);
        }
        return supported;
    }

    /// <summary>把请求档位收敛到模型支持范围。对应 TS <c>clampThinkingLevel</c>。</summary>
    public static string Clamp(ModelSpec model, string level)
    {
        var available = GetSupported(model);
        if (available.Contains(level)) return level;

        var requestedIndex = IndexOf(ExtendedThinkingLevels, level);
        if (requestedIndex < 0) return available.Count > 0 ? available[0] : "off";

        for (var index = requestedIndex; index < ExtendedThinkingLevels.Count; index++)
        {
            var candidate = ExtendedThinkingLevels[index];
            if (available.Contains(candidate)) return candidate;
        }
        for (var index = requestedIndex - 1; index >= 0; index--)
        {
            var candidate = ExtendedThinkingLevels[index];
            if (available.Contains(candidate)) return candidate;
        }
        return available.Count > 0 ? available[0] : "off";
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] == value) return index;
        }
        return -1;
    }
}
