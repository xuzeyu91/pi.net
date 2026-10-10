using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 模型类型守卫与错误结果辅助。对应 TS <c>utils/model-operations.ts</c>。
/// </summary>
public static class ModelOperations
{
    /// <summary>模型的类别（C# ModelSpec 恒有 Type，chat 为缺省）。对应 TS <c>getModelType</c>。</summary>
    public static ModelType GetModelType(ModelSpec model) => model.Type;

    public static bool IsModelType(ModelSpec model, ModelType type) => model.Type == type;

    /// <summary>
    /// 两个模型是否同一实体：类别、id、provider 三项全等。任一侧为 <c>null</c> 即 false。
    /// 对应 TS <c>modelsAreEqual</c>（<c>models.ts</c>）。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>if (!a || !b) return false</c> 用真值判断，但 <c>Model</c> 是对象、永不为假值，
    /// 故「null/undefined」与「假值」在这里是同一集合。id / provider 是 JS 的 <c>===</c>
    /// 字符串比较，对应序号比较。
    /// </remarks>
    public static bool ModelsAreEqual(ModelSpec? a, ModelSpec? b)
        => a is not null
            && b is not null
            && a.Type == b.Type
            && string.Equals(a.Id, b.Id, StringComparison.Ordinal)
            && string.Equals(a.Provider, b.Provider, StringComparison.Ordinal);

    public static void AssertChatModel(ModelSpec model)
    {
        if (model.Type != ModelType.Chat)
        {
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Model {model.Provider}/{model.Id} is not a chat model");
        }
    }

    public static void AssertImageModel(ModelSpec model)
    {
        if (model.Type != ModelType.Image)
        {
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Model {model.Provider}/{model.Id} is not an image model");
        }
    }

    public static void AssertClassifierModel(ModelSpec model)
    {
        if (model.Type != ModelType.Classifier)
        {
            throw new ModelsError(ModelsErrorCode.Provider,
                $"Model {model.Provider}/{model.Id} is not a classifier model");
        }
    }

    /// <summary>
    /// 按目录费率把用量折算为成本明细（写回 <see cref="Usage.Cost"/> 四桶与总价）。
    /// 对应 TS <c>calculateCost</c>。
    /// </summary>
    /// <remarks>
    /// 与 TS 一致的两处细节：① Anthropic 的 1h 缓存写按 2 倍基础输入价计费
    /// （<c>rates.cacheWrite * 短写 + rates.input * 2 * 长写</c>）；② 总价由四桶求和得出。
    /// 未移植：请求级阶梯费率 <c>cost.tiers</c>（<see cref="ModelCostRates"/> 尚无该字段，
    /// 目录解析与回写同样省略——见 <c>ModelSpecJson</c> 的说明）。
    /// </remarks>
    public static Usage CalculateCost(ModelSpec model, Usage usage)
    {
        var rates = model.Cost;
        if (rates is null) return usage with { Cost = UsageCost.Zero };

        var longWrite = usage.CacheWrite1h ?? 0;
        var shortWrite = usage.CacheWrite - longWrite;
        var cost = new UsageCost(
            Input: rates.Input / 1_000_000 * usage.Input,
            Output: rates.Output / 1_000_000 * usage.Output,
            CacheRead: (rates.CacheRead ?? 0) / 1_000_000 * usage.CacheRead,
            CacheWrite: ((rates.CacheWrite ?? 0) * shortWrite + rates.Input * 2 * longWrite) / 1_000_000);
        return usage with { Cost = cost.WithRecomputedTotal() };
    }

    public static AssistantImages ImageErrorResult(ModelSpec model, Exception error, bool aborted = false)
        => new()
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = [],
            StopReason = aborted ? ImagesStopReason.Aborted : ImagesStopReason.Error,
            ErrorMessage = error.Message,
        };

    public static ClassifierResult ClassifierErrorResult(ModelSpec model, Exception error, bool aborted = false)
        => new()
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Answers = new Dictionary<string, ClassifierAnswer>(),
            StopReason = aborted ? ClassifierStopReason.Aborted : ClassifierStopReason.Error,
            ErrorMessage = error.Message,
        };
}
