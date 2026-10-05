using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// <see cref="ProviderStreams"/> 的构造辅助：把各 API 静态入口（同步返回事件流或
/// 返回 Task）适配到统一的字典选项签名。对应 TS 的 <c>api/lazy.ts</c> 中
/// <c>lazyApi(load, capabilities)</c> 所包装的模块句柄形状。
/// </summary>
public static class ProviderStreamsFactory
{
    /// <summary>同步返回事件流的 API（openai-responses / google-generative-ai 等）。</summary>
    public static ProviderStreams FromStream(
        Func<ModelSpec, TranscriptContext, SimpleStreamOptions?, IAssistantMessageEventStream> stream)
        => new()
        {
            Stream = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options)),
            StreamSimple = (model, context, options) => stream(model, context, ProviderStreamOptions.FromDictionary(options)),
        };

    /// <summary>返回 <c>Task&lt;IAssistantMessageEventStream&gt;</c> 的 API（anthropic-messages / openai-completions）。</summary>
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

        return new SimpleStreamOptions(
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
    }
}

/// <summary>
/// 内建 API 实现句柄。对应 TS 各 <c>api/&lt;name&gt;.lazy.ts</c> 导出的单例
/// （<c>anthropicMessagesApi()</c> 等）：TS 用动态 import 隔离 Node-only 模块，
/// C# 直接引用静态入口。
/// </summary>
public static class LazyApis
{
    public static ProviderStreams AnthropicMessagesApi()
        => ProviderStreamsFactory.FromAsyncStream((model, context, options) =>
            AnthropicMessages.StreamSimple(ToRuntime(model), context, options));

    public static ProviderStreams OpenAiCompletionsApi()
        => ProviderStreamsFactory.FromAsyncStream((model, context, options) =>
            OpenAiCompletions.StreamSimple(ToRuntime(model), context, options));

    public static ProviderStreams OpenAiResponsesApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            OpenAiResponses.StreamSimple(model, context, options));

    public static ProviderStreams OpenAiCodexResponsesApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            OpenAiCodexResponses.StreamSimple(model, context, options));

    public static ProviderStreams AzureOpenAiResponsesApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            AzureOpenAiResponses.StreamSimple(model, context, options));

    public static ProviderStreams GoogleGenerativeAiApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            GoogleGenerativeAi.StreamSimple(model, context, options));

    public static ProviderStreams GoogleVertexApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            GoogleVertex.StreamSimple(model, context, options));

    public static ProviderStreams MistralConversationsApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            MistralConversations.StreamSimple(model, context, options));

    public static ProviderStreams BedrockConverseStreamApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            BedrockConverseStream.StreamSimple(model, context, options));

    public static ProviderStreams PiMessagesApi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            PiMessages.StreamSimple(model, context, options));

    /// <summary>OpenRouter 图片生成 API。对应 TS <c>openrouterImagesApi()</c>。</summary>
    public static ProviderImages OpenRouterImagesApi() => new()
    {
        GenerateImages = (model, context, options, cancellationToken) =>
            OpenRouterImages.GenerateImages(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>TypeSafe System One 分类 API。对应 TS <c>typesafeSystemOneApi()</c>。</summary>
    public static ProviderClassifier TypesafeSystemOneApi() => new()
    {
        Classify = (model, context, options, cancellationToken) =>
            TypesafeSystemOne.ClassifyAsync(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>Cloudflare Workers AI System One 分类 API。对应 TS <c>cloudflareWorkersAISystemOneApi()</c>。</summary>
    public static ProviderClassifier CloudflareWorkersAiSystemOneApi() => new()
    {
        Classify = (model, context, options, cancellationToken) =>
            CloudflareWorkersAiSystemOne.ClassifyAsync(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>把 <see cref="ModelSpec"/> 投影为 anthropic/openai-completions 消费的运行时模型描述。</summary>
    private static Model ToRuntime(ModelSpec model)
        => new(model.Id, model.Name, model.Api, model.Provider);
}
