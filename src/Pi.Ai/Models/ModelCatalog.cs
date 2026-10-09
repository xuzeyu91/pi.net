using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;

namespace Pi.Ai.Models;

/// <summary>模型类别。对应 TS <c>ModelType</c>（chat 为缺省类型）。</summary>
public enum ModelType
{
    Chat,
    Image,
    Classifier,
}

/// <summary>成本费率（美元/百万 token）。对应 TS <c>ModelCostRates</c>。</summary>
public sealed record ModelCostRates(double Input, double Output, double? CacheRead = null, double? CacheWrite = null);

/// <summary>输入模态。对应 TS <c>input: ("text" | "image")[]</c>。</summary>
public static class ModelInput
{
    public const string Text = "text";
    public const string Image = "image";
}

/// <summary>
/// 思考档位 → provider effort 字符串映射（null 表示该档位禁用）。
/// 对应 TS <c>ThinkingLevelMap = Partial&lt;Record&lt;ModelThinkingLevel, string | null&gt;&gt;</c>。
/// </summary>
public sealed record ThinkingLevelMap
{
    // 保留原始条目以区分 TS 的「显式 null（禁用）」与「缺失（undefined）」。
    private readonly Dictionary<string, string?> _levels = new();

    /// <summary>按档位名取映射值（缺失或 null 均返回 null）。</summary>
    public string? this[string level]
        => _levels.TryGetValue(level, out var value) ? value : null;

    /// <summary>档位是否在映射表中显式存在（含 null 值）。</summary>
    public bool Has(string level) => _levels.ContainsKey(level);

    /// <summary>显式存在的档位条目（含 null 值），按插入顺序——序列化用。</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> Entries => _levels.ToList();

    public static ThinkingLevelMap FromJsonObject(JsonObject obj)
    {
        var map = new ThinkingLevelMap();
        foreach (var (key, value) in obj)
        {
            map._levels[key] = value is JsonValue { } primitive
                ? (primitive.TryGetValue<string>(out var text) ? text
                    : primitive.TryGetValue<bool>(out var flag) && !flag ? null : null)
                : null;
        }
        return map;
    }
}

/// <summary>
/// 模型规格：目录里的完整模型条目（含未识别扩展字段保留在 <see cref="Extra"/>）。
/// 对应 TS <c>BaseModel</c> + <c>Model</c>/<c>ImageModel</c>/<c>ClassifierModel</c> 的公共形状。
/// </summary>
public sealed record ModelSpec
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>协议名（openai-completions / anthropic-messages / …）。对应 TS <c>Api</c>。</summary>
    public required string Api { get; init; }

    public required string Provider { get; init; }

    public required string BaseUrl { get; init; }

    /// <summary>输入模态（text/image）。</summary>
    public IReadOnlyList<string> Input { get; init; } = [ModelInput.Text];

    public ModelCostRates? Cost { get; init; }

    /// <summary>模型类别；chat 为缺省（TS 约定：无 type 字段即 chat）。</summary>
    public ModelType Type { get; init; } = ModelType.Chat;

    /// <summary>是否支持推理（chat 专用）。</summary>
    public bool Reasoning { get; init; }

    public long ContextWindow { get; init; }

    public long MaxTokens { get; init; }

    /// <summary>模型级默认请求头。对应 TS <c>Model.headers</c>。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>思考档位映射。对应 TS <c>thinkingLevelMap</c>。</summary>
    public ThinkingLevelMap? ThinkingLevelMap { get; init; }

    /// <summary>模型级采样参数（原样并入请求）。对应 TS <c>samplingParams</c>。</summary>
    public JsonObject? SamplingParams { get; init; }

    /// <summary>按思考档位的采样参数覆盖。对应 TS <c>samplingParamsByThinkingLevel</c>。</summary>
    public IReadOnlyDictionary<string, JsonObject>? SamplingParamsByThinkingLevel { get; init; }

    /// <summary>API 兼容设置（原样保留；各 API 用扩展方法取强类型视图）。对应 TS <c>compat</c>。</summary>
    public JsonObject? Compat { get; init; }

    /// <summary>provider 输入限制与缓存安全预处理元数据。对应 TS <c>BaseModel.inputLimits</c>。</summary>
    public ModelInputLimits? InputLimits { get; init; }

    /// <summary>未识别字段原样保留（前向兼容）。</summary>
    public JsonObject? Extra { get; init; }
}

/// <summary>
/// 单一 provider 的模型目录：从 groups JSON（{api: {"type:id": spec}}）flatten 为
/// 按类别索引的查询表。对应 TS <c>model-catalog.ts</c> 的 <c>flattenChatModelCatalog</c> 等。
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, ModelSpec> _chat = [];
    private readonly Dictionary<string, ModelSpec> _image = [];
    private readonly Dictionary<string, ModelSpec> _classifier = [];

    public string Provider { get; }

    private ModelCatalog(string provider) => Provider = provider;

    /// <summary>provider 的目录键（与 models.generated.ts 的键一致）。</summary>
    public IReadOnlyList<string> KnownProviders => _knownProviders;

    private static readonly List<string> _knownProviders =
    [
        "amazon-bedrock", "ant-ling", "anthropic", "azure-openai-responses", "baseten",
        "cerebras", "cloudflare-ai-gateway", "cloudflare-workers-ai", "deepseek", "fireworks",
        "github-copilot", "google", "google-vertex", "groq", "huggingface",
        "kimi-coding", "meta", "minimax", "minimax-cn", "mistral",
        "moonshotai", "moonshotai-cn", "nvidia", "openai", "openai-codex",
        "opencode", "opencode-go", "openrouter", "qwen-token-plan", "qwen-token-plan-cn",
        "qwen-token-plan-individual", "radius", "together", "typesafe", "vercel-ai-gateway",
        "xai", "xiaomi", "xiaomi-token-plan-ams", "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp",
        "zai", "zai-coding-cn",
    ];

    /// <summary>解析 groups JSON：{api: {"type:id": spec}} → flatten 为按 id 索引的类别表。</summary>
    public static ModelCatalog Load(string provider, JsonObject groups)
    {
        var catalog = new ModelCatalog(provider);
        foreach (var (_, apiGroup) in groups)
        {
            if (apiGroup is not JsonObject models) continue;
            foreach (var (_, rawSpec) in models)
            {
                if (rawSpec is not JsonObject spec) continue;
                var model = ParseSpec(provider, spec);
                var bucket = model.Type switch
                {
                    ModelType.Chat => catalog._chat,
                    ModelType.Image => catalog._image,
                    ModelType.Classifier => catalog._classifier,
                    _ => throw new ArgumentException($"unknown model type: {model.Type}"),
                };
                bucket[model.Id] = model;
            }
        }
        return catalog;
    }

    /// <summary>从程序集嵌入资源加载（资源名形如 <c>Pi.Ai.ModelData.openai.json</c>）。</summary>
    public static ModelCatalog LoadFromResource(string provider)
    {
        var assembly = typeof(ModelCatalog).Assembly;
        var resourceName = $"Pi.Ai.ModelData.{provider}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded model catalog not found: {resourceName}");
        using var document = JsonDocument.Parse(stream);
        return Load(provider, document.RootElement.Deserialize<JsonObject>()
            ?? throw new ArgumentException("Invalid model catalog JSON"));
    }

    /// <summary>
    /// 解析单个模型条目（<c>ModelSpecJson.FromJsonObject</c> 复用此入口，保证读路径只有一份实现）。
    /// </summary>
    public static ModelSpec ParseSpec(string provider, JsonObject spec)
    {
        string? GetString(string key)
            => spec[key] is JsonValue { } value && value.TryGetValue<string>(out var text) ? text : null;

        long GetLong(string key)
            => spec[key] is JsonValue { } number && number.TryGetValue<long>(out var parsed) ? parsed : 0;

        var typeText = GetString("type") ?? "chat";
        var type = typeText switch
        {
            "chat" => ModelType.Chat,
            "image" => ModelType.Image,
            "classifier" => ModelType.Classifier,
            _ => throw new ArgumentException($"unknown model type: {typeText}"),
        };

        var input = new List<string>();
        if (spec["input"] is JsonArray inputArray)
        {
            input.AddRange(inputArray
                .OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var text) ? text : "")
                .Where(text => text.Length > 0));
        }

        ModelCostRates? cost = null;
        if (spec["cost"] is JsonObject costObject)
        {
            double Rate(string key)
                => costObject[key] is JsonValue { } value && value.TryGetValue<double>(out var parsed) ? parsed : 0;
            cost = new ModelCostRates(Rate("input"), Rate("output"),
                costObject["cache_read"] is JsonValue { } cr && cr.TryGetValue<double>(out var cacheRead)
                    ? cacheRead : null,
                costObject["cache_write"] is JsonValue { } cw && cw.TryGetValue<double>(out var cacheWrite)
                    ? cacheWrite : null);
        }

        var consumed = new HashSet<string>
        {
            "id", "name", "api", "provider", "baseUrl", "input", "cost", "type", "reasoning",
            "contextWindow", "maxTokens", "headers", "thinkingLevelMap", "samplingParams",
            "samplingParamsByThinkingLevel", "compat", "inputLimits",
        };
        var extra = new JsonObject();
        foreach (var (key, value) in spec)
        {
            if (!consumed.Contains(key)) extra[key] = value?.DeepClone();
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if (spec["headers"] is JsonObject headersObject)
        {
            var map = new Dictionary<string, string>();
            foreach (var (key, value) in headersObject)
            {
                if (value is JsonValue { } headerValue && headerValue.TryGetValue<string>(out var headerText))
                {
                    map[key] = headerText;
                }
            }
            headers = map;
        }

        ThinkingLevelMap? thinkingLevelMap = spec["thinkingLevelMap"] is JsonObject levelMapObject
            ? ThinkingLevelMap.FromJsonObject(levelMapObject)
            : null;

        JsonObject? samplingParams = spec["samplingParams"] as JsonObject;

        IReadOnlyDictionary<string, JsonObject>? samplingByLevel = null;
        if (spec["samplingParamsByThinkingLevel"] is JsonObject byLevelObject)
        {
            var map = new Dictionary<string, JsonObject>();
            foreach (var (key, value) in byLevelObject)
            {
                if (value is JsonObject levelParams) map[key] = levelParams;
            }
            samplingByLevel = map;
        }

        ModelInputLimits? inputLimits = spec["inputLimits"] is JsonObject limitsObject
            ? ParseInputLimits(limitsObject)
            : null;

        return new ModelSpec
        {
            Id = GetString("id") ?? throw new ArgumentException("model spec requires id"),
            Name = GetString("name") ?? "",
            Api = GetString("api") ?? "",
            Provider = provider,
            BaseUrl = GetString("baseUrl") ?? "",
            Input = input,
            Cost = cost,
            Headers = headers,
            ThinkingLevelMap = thinkingLevelMap,
            SamplingParams = samplingParams,
            SamplingParamsByThinkingLevel = samplingByLevel,
            Compat = spec["compat"] as JsonObject,
            Type = type,
            Reasoning = spec["reasoning"] is JsonValue { } reasoning && reasoning.TryGetValue<bool>(out var flag) && flag,
            ContextWindow = GetLong("contextWindow"),
            MaxTokens = GetLong("maxTokens"),
            InputLimits = inputLimits,
            Extra = extra.Count > 0 ? extra : null,
        };
    }

    /// <summary>解析 <c>inputLimits</c> 子树（resize/maxPerMessage/maxPerRequest/maxRequestBytes）。</summary>
    public static ModelInputLimits ParseInputLimits(JsonObject limits)
    {
        static long? GetLongValue(JsonObject owner, string key)
            => owner[key] is JsonValue { } number && number.TryGetValue<long>(out var parsed) ? parsed : null;

        static int? GetIntValue(JsonObject owner, string key)
            => owner[key] is JsonValue { } number && number.TryGetValue<int>(out var parsed) ? parsed : null;

        ModelImageInputLimits? images = null;
        if (limits["images"] is JsonObject imagesObject)
        {
            ModelImageResizeOptions? resize = null;
            if (imagesObject["resize"] is JsonObject resizeObject)
            {
                resize = new ModelImageResizeOptions
                {
                    MaxWidth = GetIntValue(resizeObject, "maxWidth"),
                    MaxHeight = GetIntValue(resizeObject, "maxHeight"),
                    MaxBytes = GetLongValue(resizeObject, "maxBytes"),
                    JpegQuality = GetIntValue(resizeObject, "jpegQuality"),
                };
            }

            images = new ModelImageInputLimits
            {
                Resize = resize,
                MaxPerMessage = GetIntValue(imagesObject, "maxPerMessage"),
                MaxPerRequest = GetIntValue(imagesObject, "maxPerRequest"),
            };
        }

        return new ModelInputLimits
        {
            MaxRequestBytes = GetLongValue(limits, "maxRequestBytes"),
            Images = images,
        };
    }

    /// <summary>chat 模型（按模型 id 索引）。对应 TS <c>ChatModelCatalog</c>。</summary>
    public IReadOnlyDictionary<string, ModelSpec> ChatModels => _chat;

    /// <summary>image 模型。对应 TS <c>ImageModelCatalog</c>。</summary>
    public IReadOnlyDictionary<string, ModelSpec> ImageModels => _image;

    /// <summary>classifier 模型。对应 TS <c>ClassifierModelCatalog</c>。</summary>
    public IReadOnlyDictionary<string, ModelSpec> ClassifierModels => _classifier;

    /// <summary>按 id 查任意类别模型。</summary>
    public ModelSpec? Find(string modelId)
        => _chat.GetValueOrDefault(modelId)
            ?? _image.GetValueOrDefault(modelId)
            ?? _classifier.GetValueOrDefault(modelId);
}
