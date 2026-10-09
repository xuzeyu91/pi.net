using System.Text.Json.Nodes;
using Pi.Ai.Types;

namespace Pi.Ai.Models;

/// <summary>
/// <see cref="ModelSpec"/> 与 TS 线格式之间的往返序列化。对应 TS 里"模型对象本身就是 JSON"
/// 这一事实——TS 直接把目录对象写进 <c>models-store.json</c>，C# 侧 <see cref="ModelSpec"/>
/// 是具名记录，需要一个显式映射。
/// </summary>
/// <remarks>
/// 已知差异：
/// <list type="bullet">
/// <item>TS <c>ModelCost</c> 的 <c>tiers</c> 与图片模型的 <c>output</c>、chat 模型的
/// <c>promptCache</c> 在 C# <see cref="ModelSpec"/> 里没有具名字段；它们会经
/// <see cref="ModelSpec.Extra"/> 原样保留并回写，因此往返不丢失（<c>cost.tiers</c> 例外：
/// <c>cost</c> 整体被解析成 <see cref="ModelCostRates"/>，tiers 被丢弃——沿用 Pi.Ai 既有的
/// 成本简化，见 <c>ModelOperations.CalculateCost</c> 的注释）。</item>
/// <item>TS 用 <c>undefined</c> 表示"字段缺失"，C# 用 <c>null</c>；本类只写非 null 字段。</item>
/// </list>
/// </remarks>
public static class ModelSpecJson
{
    /// <summary>把模型条目写成 TS 线格式的 JSON 对象。</summary>
    public static JsonObject ToJsonObject(ModelSpec model)
    {
        var result = new JsonObject
        {
            ["id"] = model.Id,
            ["name"] = model.Name,
            ["api"] = model.Api,
            ["provider"] = model.Provider,
            ["baseUrl"] = model.BaseUrl,
        };

        var input = new JsonArray();
        foreach (var modality in model.Input) input.Add(modality);
        result["input"] = input;

        if (model.Cost is { } cost)
        {
            var costObject = new JsonObject
            {
                ["input"] = cost.Input,
                ["output"] = cost.Output,
            };
            if (cost.CacheRead is { } cacheRead) costObject["cacheRead"] = cacheRead;
            if (cost.CacheWrite is { } cacheWrite) costObject["cacheWrite"] = cacheWrite;
            result["cost"] = costObject;
        }

        // TS：chat 是缺省类型，因此只有非 chat 才写 type。
        if (model.Type != ModelType.Chat) result["type"] = TypeToText(model.Type);

        // TS：reasoning 只存在于 chat 模型上，但 false 也要写出来。
        if (model.Type == ModelType.Chat || model.Reasoning) result["reasoning"] = model.Reasoning;

        // TS：contextWindow/maxTokens 只存在于 chat 与 classifier 模型上。
        if (model.Type != ModelType.Image)
        {
            result["contextWindow"] = model.ContextWindow;
            result["maxTokens"] = model.MaxTokens;
        }
        else
        {
            if (model.ContextWindow != 0) result["contextWindow"] = model.ContextWindow;
            if (model.MaxTokens != 0) result["maxTokens"] = model.MaxTokens;
        }

        if (model.Headers is { } headers)
        {
            var headersObject = new JsonObject();
            foreach (var (name, value) in headers) headersObject[name] = value;
            result["headers"] = headersObject;
        }

        if (model.ThinkingLevelMap is { } levelMap)
        {
            var levelObject = new JsonObject();
            foreach (var (level, mapped) in levelMap.Entries)
            {
                levelObject[level] = mapped is null ? null : JsonValue.Create(mapped);
            }
            result["thinkingLevelMap"] = levelObject;
        }

        if (model.SamplingParams is { } samplingParams) result["samplingParams"] = samplingParams.DeepClone();

        if (model.SamplingParamsByThinkingLevel is { } byLevel)
        {
            var byLevelObject = new JsonObject();
            foreach (var (level, parameters) in byLevel) byLevelObject[level] = parameters.DeepClone();
            result["samplingParamsByThinkingLevel"] = byLevelObject;
        }

        if (model.Compat is { } compat) result["compat"] = compat.DeepClone();
        if (model.InputLimits is { } limits) result["inputLimits"] = WriteInputLimits(limits);

        // 未识别字段（含 TS 的 promptCache / output 等）原样回写。
        if (model.Extra is { } extra)
        {
            foreach (var (key, value) in extra)
            {
                if (!result.ContainsKey(key)) result[key] = value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>解析 TS 线格式的 JSON 对象（复用 <see cref="ModelCatalog.ParseSpec"/>）。</summary>
    public static ModelSpec FromJsonObject(string provider, JsonObject spec)
        => ModelCatalog.ParseSpec(provider, spec);

    /// <summary>类别名（TS 的字符串字面量）。</summary>
    public static string TypeToText(ModelType type) => type switch
    {
        ModelType.Chat => "chat",
        ModelType.Image => "image",
        ModelType.Classifier => "classifier",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown model type"),
    };

    private static JsonObject WriteInputLimits(ModelInputLimits limits)
    {
        var result = new JsonObject();
        if (limits.MaxRequestBytes is { } maxRequestBytes) result["maxRequestBytes"] = maxRequestBytes;
        if (limits.Images is { } images)
        {
            var imagesObject = new JsonObject();
            if (images.Resize is { } resize)
            {
                var resizeObject = new JsonObject();
                if (resize.MaxWidth is { } maxWidth) resizeObject["maxWidth"] = maxWidth;
                if (resize.MaxHeight is { } maxHeight) resizeObject["maxHeight"] = maxHeight;
                if (resize.MaxBytes is { } maxBytes) resizeObject["maxBytes"] = maxBytes;
                if (resize.JpegQuality is { } jpegQuality) resizeObject["jpegQuality"] = jpegQuality;
                imagesObject["resize"] = resizeObject;
            }

            if (images.MaxPerMessage is { } maxPerMessage) imagesObject["maxPerMessage"] = maxPerMessage;
            if (images.MaxPerRequest is { } maxPerRequest) imagesObject["maxPerRequest"] = maxPerRequest;
            result["images"] = imagesObject;
        }

        return result;
    }
}
