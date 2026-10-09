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
    /// 按目录费率折算用量成本（写回 <see cref="Usage.Cost"/> 总价）。
    /// 对应 TS <c>calculateCost</c> 的简化版：C# Usage.Cost 是单一总价
    /// （TS 是 input/output/cacheRead/cacheWrite/total 子对象），且暂不支持
    /// 阶梯费率（cost.tiers）与 Anthropic 1h 缓存写加价。
    /// </summary>
    public static Usage CalculateCost(ModelSpec model, Usage usage)
    {
        var rates = model.Cost;
        if (rates is null) return usage with { Cost = 0 };
        return usage with
        {
            Cost =
                rates.Input / 1_000_000 * usage.Input
                + rates.Output / 1_000_000 * usage.Output
                + (rates.CacheRead ?? 0) / 1_000_000 * usage.CacheRead
                + (rates.CacheWrite ?? 0) / 1_000_000 * usage.CacheWrite,
        };
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
