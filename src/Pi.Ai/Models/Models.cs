using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Models;

/// <summary>
/// Provider 运行时单元：元数据 + 模型列举 + 操作（流式）。对应 TS <c>Provider</c>
/// （models.ts）的核心成员；auth/fetchDeferred/images/classify 在后续阶段接入。
/// </summary>
public interface IProvider
{
    string Id { get; }

    string Name { get; }

    string? BaseUrl { get; }

    /// <summary>
    /// 当前已知 chat 模型（同步）。抛错的实现被 <see cref="Models"/> 视为无模型
    /// （best-effort 契约，对齐 TS <c>getModels()</c>）。
    /// </summary>
    IReadOnlyList<ModelSpec> GetModels();

    /// <summary>流式：归一化转录分发给 provider。</summary>
    IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null);

    /// <summary>简单流式：文本/图片内容直传。</summary>
    IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null);
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
}
