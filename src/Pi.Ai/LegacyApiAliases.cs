using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai;

/// <summary>
/// 旧版全局别名（deprecated）。对应 TS <c>legacy-api-aliases.ts</c>：
/// 兼容旧入口命名的薄转发，新代码应使用各 API 静态类的 Stream/StreamSimple。
/// </summary>
public static class LegacyApiAliases
{
    /// <summary>@deprecated 用 <see cref="OpenAiResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamOpenAIResponses(
        ModelSpec model, TranscriptContext context, OpenAiResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleOpenAIResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="AzureOpenAiResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamAzureOpenAIResponses(
        ModelSpec model, TranscriptContext context, AzureOpenAiResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => AzureOpenAiResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="AzureOpenAiResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleAzureOpenAIResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => AzureOpenAiResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiCodexResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamOpenAICodexResponses(
        ModelSpec model, TranscriptContext context, OpenAiCodexResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiCodexResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiCodexResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleOpenAICodexResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiCodexResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="MistralConversations.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamMistral(
        ModelSpec model, TranscriptContext context, MistralOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => MistralConversations.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="MistralConversations.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleMistral(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => MistralConversations.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="PiMessages.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamPiMessages(
        ModelSpec model, TranscriptContext context, PiMessagesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => PiMessages.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="PiMessages.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimplePiMessages(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => PiMessages.StreamSimple(model, context, options, client, ct);
}
