using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Models;

/// <summary>
/// 延后响应句柄：provider 返回 <c>deferred</c> 停止原因时携带的续取令牌。
/// 对应 TS <c>DeferredHandle</c>（types.ts）。
/// </summary>
public sealed record DeferredHandle
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Api { get; init; }

    /// <summary>provider 令牌（响应 id 或批次 id + 行 id）。</summary>
    public required string Id { get; init; }

    /// <summary>句柄失效时刻（epoch 毫秒）。</summary>
    public long? ExpiresAt { get; init; }

    /// <summary>建议的下一次轮询间隔（毫秒）。</summary>
    public long? PollAfterMs { get; init; }

    /// <summary>重建最终助手消息所需的 provider 转换数据。</summary>
    public JsonNode? Data { get; init; }
}

/// <summary>
/// API 实现模块的统一流契约：每个 API 实现导出 <c>stream</c>/<c>streamSimple</c>，
/// 有能力的实现还导出延后响应方法。对应 TS <c>ProviderStreams</c>（types.ts）。
/// </summary>
public sealed record ProviderStreams
{
    public required Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>
        Stream { get; init; }

    public required Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>
        StreamSimple { get; init; }

    public Func<ModelSpec, DeferredHandle, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>?
        FetchDeferred { get; init; }

    public Func<ModelSpec, DeferredHandle, IReadOnlyDictionary<string, object?>?, CancellationToken, Task>?
        CancelDeferred { get; init; }
}

/// <summary>图片生成 API 实现。对应 TS <c>ProviderImages</c>。</summary>
public sealed record ProviderImages
{
    public required Func<ModelSpec, ImagesContext, ImagesOptions?, CancellationToken, Task<AssistantImages>>
        GenerateImages { get; init; }
}

/// <summary>结构化分类 API 实现。对应 TS <c>ProviderClassifier</c>。</summary>
public sealed record ProviderClassifier
{
    public required Func<ModelSpec, ClassifierContext, ClassifierOptions?, CancellationToken, Task<ClassifierResult>>
        Classify { get; init; }
}

/// <summary>
/// <see cref="ProviderStreams"/> 的构造辅助：把各 API 静态入口（返回事件流或 Task）
/// 适配到统一的字典选项签名。对应 TS 的 <c>openAICompletionsApi()</c> 等模块句柄。
/// </summary>
public static class ProviderStreamsFactory
{
    /// <summary>同步返回事件流的 API（openai-responses / anthropic-messages 等）。</summary>
    public static ProviderStreams FromStream(
        Func<ModelSpec, TranscriptContext, SimpleStreamOptions?, IAssistantMessageEventStream> stream)
        => new()
        {
            Stream = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options)),
            StreamSimple = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options)),
        };

    /// <summary>返回 <c>Task&lt;IAssistantMessageEventStream&gt;</c> 的 API（anthropic-messages / openai-completions 的现有形状）。</summary>
    public static ProviderStreams FromAsyncStream(
        Func<ModelSpec, TranscriptContext, SimpleStreamOptions?, Task<IAssistantMessageEventStream>> stream)
        => new()
        {
            Stream = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options))
                .GetAwaiter().GetResult(),
            StreamSimple = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options))
                .GetAwaiter().GetResult(),
        };
}

/// <summary>
/// 通用字典选项 → <see cref="SimpleStreamOptions"/> 桥接。C# 侧 provider 边界沿用
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> 承载 provider 专属选项；
/// 各 API 静态入口需要强类型选项，故在此做字段映射（未知键忽略）。
/// </summary>
public static class ProviderStreamOptions
{
    public static SimpleStreamOptions? FromDictionary(IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null) return null;

        T? Get<T>(string key) where T : class
            => options.TryGetValue(key, out var value) ? value as T : null;

        var result = new SimpleStreamOptions(
            ApiKey: Get<string>("apiKey"),
            BaseUrl: Get<string>("baseUrl"),
            MaxTokens: options.TryGetValue("maxTokens", out var maxTokens) ? maxTokens as int? : null,
            Temperature: options.TryGetValue("temperature", out var temperature) ? temperature as double? : null,
            StopSequences: Get<IReadOnlyList<string>>("stopSequences"),
            SessionId: Get<string>("sessionId"),
            ChainOfThought: options.TryGetValue("chainOfThought", out var chainOfThought) ? chainOfThought as bool? : null)
        {
            Headers = Get<IReadOnlyDictionary<string, string?>>("headers"),
            SamplingParams = Get<JsonObject>("samplingParams"),
            MaxRetries = options.TryGetValue("maxRetries", out var maxRetries) ? maxRetries as int? : null,
            MaxRetryDelayMs = options.TryGetValue("maxRetryDelayMs", out var maxRetryDelayMs) ? maxRetryDelayMs as int? : null,
            TimeoutMs = options.TryGetValue("timeoutMs", out var timeoutMs) ? timeoutMs as int? : null,
            WebsocketConnectTimeoutMs = options.TryGetValue("websocketConnectTimeoutMs", out var websocketTimeout)
                ? websocketTimeout as int?
                : null,
            Transport = Get<string>("transport"),
            CacheRetention = Get<string>("cacheRetention"),
            Metadata = Get<JsonObject>("metadata"),
            Env = Get<IReadOnlyDictionary<string, string>>("env"),
            Signal = options.TryGetValue("signal", out var signal) && signal is CancellationToken token
                ? token
                : default,
            OnPayload = Get<Func<JsonObject, ModelSpec, Task<JsonObject?>>>("onPayload"),
            OnResponse = Get<Func<ProviderResponse, ModelSpec, Task>>("onResponse"),
            OnProviderStreamEvent = Get<Func<JsonObject, ModelSpec, Task>>("onProviderStreamEvent"),
            ToolChoice = options.TryGetValue("toolChoice", out var toolChoice) ? toolChoice as JsonNode : null,
            Reasoning = Get<string>("reasoning"),
            Deferred = options.TryGetValue("deferred", out var deferred) && deferred is true,
            ThinkingBudgets = Get<ThinkingBudgets>("thinkingBudgets"),
        };
        return result;
    }
}

/// <summary>
/// createProvider 的输入部件。对应 TS <c>CreateProviderOptions</c>（models.ts）。
/// </summary>
public sealed record CreateProviderOptions
{
    public required string Id { get; init; }

    /// <summary>显示名；缺省为 <see cref="Id"/>。</summary>
    public string? Name { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>必需——每个 provider 都有认证语义，即便纯环境源或无密钥本地服务器。</summary>
    public ProviderAuth? Auth { get; init; }

    /// <summary>静态基线模型（各类型；无 type 即 chat）。纯动态 provider 为空。</summary>
    public IReadOnlyList<ModelSpec> Models { get; init; } = [];

    /// <summary>单一 chat 实现（服务全部 chat 模型）。与 <see cref="ApiByApi"/> 二选一。</summary>
    public ProviderStreams? Api { get; init; }

    /// <summary>按 <c>model.api</c> 分派的 chat 实现映射（混合 API provider）。</summary>
    public IReadOnlyDictionary<string, ProviderStreams>? ApiByApi { get; init; }

    /// <summary>按 <c>model.api</c> 分派的图片生成实现。</summary>
    public IReadOnlyDictionary<string, ProviderImages>? Images { get; init; }

    /// <summary>按 <c>model.api</c> 分派的分类器实现。</summary>
    public IReadOnlyDictionary<string, ProviderClassifier>? Classifiers { get; init; }

    /// <summary>按凭据的 chat 模型可用性策略。对应 TS <c>filterModels</c>。</summary>
    public Func<IReadOnlyList<ModelSpec>, Credential?, IReadOnlyList<ModelSpec>>? FilterModels { get; init; }
}

/// <summary>
/// 从部件装配 provider。内建 provider 工厂与动态 provider 都经由此处。
/// 对应 TS <c>createProvider</c>（models.ts）：单 <c>api</c> 服务全部 chat 模型；
/// <c>api</c> 映射按 <c>model.api</c> 分派，缺条目产生 error 流；一次性操作映射同理；
/// <c>api</c>/<c>images</c>/<c>classifiers</c> 至少要有一个具体实现（空映射被拒绝）。
/// </summary>
public static class ProviderFactory
{
    public static IProvider Create(CreateProviderOptions input)
    {
        var single = input.Api;
        var byApi = single is null ? input.ApiByApi : null;
        var images = input.Images;
        var classifiers = input.Classifiers;

        var streams = single is not null
            ? [single]
            : (byApi?.Values ?? []).Where(entry => entry is not null).ToList();
        var imageImplementations = (images?.Values ?? []).Where(entry => entry is not null).ToList();
        var classifierImplementations = (classifiers?.Values ?? []).Where(entry => entry is not null).ToList();

        if (streams.Count == 0 && imageImplementations.Count == 0 && classifierImplementations.Count == 0)
        {
            throw new ArgumentException(
                $"Provider {input.Id}: at least one of \"api\", \"images\", or \"classifiers\" is required.");
        }

        return new CreatedProvider(input, single, byApi, images, classifiers, streams);
    }

    /// <summary>工厂产物：基线模型 + 按 api 分派的实现。</summary>
    private sealed class CreatedProvider : IProvider, IImagesProvider, IClassifierProvider
    {
        private readonly CreateProviderOptions _input;
        private readonly ProviderStreams? _single;
        private readonly IReadOnlyDictionary<string, ProviderStreams>? _byApi;
        private readonly IReadOnlyDictionary<string, ProviderImages>? _images;
        private readonly IReadOnlyDictionary<string, ProviderClassifier>? _classifiers;
        private readonly IReadOnlyList<ProviderStreams> _streams;

        public CreatedProvider(
            CreateProviderOptions input,
            ProviderStreams? single,
            IReadOnlyDictionary<string, ProviderStreams>? byApi,
            IReadOnlyDictionary<string, ProviderImages>? images,
            IReadOnlyDictionary<string, ProviderClassifier>? classifiers,
            IReadOnlyList<ProviderStreams> streams)
        {
            _input = input;
            _single = single;
            _byApi = byApi;
            _images = images;
            _classifiers = classifiers;
            _streams = streams;
        }

        public string Id => _input.Id;

        public string Name => _input.Name ?? _input.Id;

        public string? BaseUrl => _input.BaseUrl;

        public ProviderAuth? Auth => _input.Auth;

        public IReadOnlyList<ModelSpec> GetModels()
            => _input.Models.Where(model => model.Type == ModelType.Chat).ToList();

        public IReadOnlyList<ModelSpec> GetAllModels() => _input.Models;

        public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
            => _input.FilterModels?.Invoke(models, credential) ?? models;

        private ProviderStreams? ApiFor(ModelSpec model) => _single ?? _byApi?.GetValueOrDefault(model.Api);

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => Dispatch(model, context, options, streams => streams.Stream);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => Dispatch(model, context, options, streams => streams.StreamSimple);

        private IAssistantMessageEventStream Dispatch(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options,
            Func<ProviderStreams, Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>> select)
        {
            var streams = ApiFor(model);
            if (streams is null)
            {
                return LazyStream.Run(model, () => throw new ModelsError(ModelsErrorCode.Stream,
                    $"Provider {_input.Id} has no API implementation for \"{model.Api}\""));
            }
            return select(streams)(model, new TranscriptContext(context), options);
        }

        /// <summary>仅当任一实现导出延后方法时才暴露 <c>fetchDeferred</c>。对应 TS createProvider。</summary>
        public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            if (!_streams.Any(entry => entry.FetchDeferred is not null)) return null;
            return LazyStream.Run(model, () =>
            {
                var implementation = ApiFor(model);
                if (implementation?.FetchDeferred is null)
                {
                    throw new ModelsError(ModelsErrorCode.Provider,
                        $"Provider {_input.Id} does not support deferred responses for \"{model.Api}\"");
                }
                return Task.FromResult(implementation.FetchDeferred(model, handle, options));
            });
        }

        public async Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
            IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        {
            if (!_streams.Any(entry => entry.CancelDeferred is not null))
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support deferred responses");
            }
            var implementation = ApiFor(model);
            if (implementation?.CancelDeferred is null)
            {
                throw new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} cannot cancel deferred responses for \"{model.Api}\"");
            }
            await implementation.CancelDeferred(model, handle, options, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
        {
            if (_images is null || _images.Count == 0)
            {
                return ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support image generation"));
            }
            if (!_images.TryGetValue(model.Api, out var implementation) || implementation is null)
            {
                return ModelOperations.ImageErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} has no image generation implementation for \"{model.Api}\""));
            }
            return await implementation.GenerateImages(model, context, options, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
            ClassifierOptions? options, CancellationToken cancellationToken)
        {
            if (_classifiers is null || _classifiers.Count == 0)
            {
                return ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} does not support classification"));
            }
            if (!_classifiers.TryGetValue(model.Api, out var implementation) || implementation is null)
            {
                return ModelOperations.ClassifierErrorResult(model, new ModelsError(ModelsErrorCode.Provider,
                    $"Provider {_input.Id} has no classifier implementation for \"{model.Api}\""));
            }
            return await implementation.Classify(model, context, options, cancellationToken).ConfigureAwait(false);
        }
    }
}
