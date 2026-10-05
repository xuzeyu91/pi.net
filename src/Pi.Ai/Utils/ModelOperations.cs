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
