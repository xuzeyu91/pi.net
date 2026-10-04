// ============================================================================
// PORT SKELETON - packages/ai (26.3k lines TS) is the largest package in the
// monorepo. This file establishes the core type surface that Pi.Agent depends
// on; provider implementations are ported per-session on the roadmap below.
//
// Already ported in this session:   (core types only)
// Remaining roadmap order:          types.ts full port -> utils/event-stream ->
//                                   providers/faux -> api/openai-completions ->
//                                   api/anthropic-messages -> model catalog ->
//                                   remaining ~20 providers (lazy pattern).
// ============================================================================

namespace Pi.Ai;

/// <summary>Unified provider API identifier. Mirrors TS <c>Api</c> (string union).</summary>
public static class Api
{
    public const string OpenAiCompletions = "openai-completions";
    public const string OpenAiResponses = "openai-responses";
    public const string AnthropicMessages = "anthropic-messages";
    public const string GoogleGenerativeAi = "google-generative-ai";
    public const string BedrockConverseStream = "bedrock-converse-stream";
}

/// <summary>Token/cost accounting. Mirrors TS <c>Usage</c>.</summary>
public sealed record Usage(
    long Input,
    long Output,
    long CacheRead = 0,
    long CacheWrite = 0,
    long? TotalTokens = null);

/// <summary>Why an assistant turn stopped. Mirrors TS <c>StopReason</c>.</summary>
public enum StopReason
{
    Stop,
    Length,
    ToolUse,
    Error,
    Aborted,
}

/// <summary>Tool arguments schema. Mirrors typebox <c>TSchema</c>; the .NET port uses
/// a JSON-Schema dictionary until the dedicated Schema DSL is ported.</summary>
public sealed record ToolSchema(IReadOnlyDictionary<string, object?> JsonSchema);

/// <summary>A callable tool. Mirrors TS <c>Tool</c>.</summary>
public sealed record AgentTool(
    string Name,
    string Description,
    ToolSchema Parameters);

/// <summary>Reasoning effort levels. Mirrors TS <c>ThinkingLevel</c>.</summary>
public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

/// <summary>A chat model descriptor. Mirrors TS <c>Model&lt;Api&gt;</c>.</summary>
public sealed record Model(
    string Id,
    string Name,
    string Api,
    string Provider,
    string? ProviderId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Stream options shared by all providers. Mirrors TS <c>SimpleStreamOptions</c>.</summary>
public sealed record SimpleStreamOptions(
    string? ApiKey = null,
    string? BaseUrl = null,
    int? MaxTokens = null,
    double? Temperature = null,
    IReadOnlyList<string>? StopSequences = null,
    string? SessionId = null,
    bool? ChainOfThought = null);

// ---------------------------------------------------------------------------
// Message content blocks (discriminated by Type, mirroring the TS unions).
// ---------------------------------------------------------------------------

/// <summary>Text content block. Mirrors TS <c>TextContent</c>.</summary>
public sealed record TextContent(string Text);

/// <summary>Image content block (base64 or URL). Mirrors TS <c>ImageContent</c>.</summary>
public sealed record ImageContent(string Type, string Data, string? MimeType = null);

/// <summary>A tool invocation requested by the assistant. Mirrors TS <c>ToolCall</c>.</summary>
public sealed record ToolCallContent(string Id, string Name, object? Arguments);

/// <summary>Chain-of-thought block. Mirrors TS <c>ThinkingContent</c>.</summary>
public sealed record ThinkingContent(string Thinking, string? Signature = null);

/// <summary>One content item inside a message. Mirrors the TS content union.</summary>
public abstract record MessageContent
{
    private MessageContent() { }

    public sealed record Text(string Body) : MessageContent;

    public sealed record Image(string Type, string Data, string? MimeType) : MessageContent;

    public sealed record ToolCall(string Id, string Name, object? Arguments) : MessageContent;

    public sealed record Thinking(string Body, string? Signature) : MessageContent;
}

/// <summary>Message roles. Mirrors the TS message union discriminators.</summary>
public abstract record ChatMessage
{
    private ChatMessage() { }

    public sealed record System(string Text) : ChatMessage;

    public sealed record User(IReadOnlyList<MessageContent> Content, long Timestamp) : ChatMessage;

    public sealed record Assistant(
        IReadOnlyList<MessageContent> Content,
        string? StopMessage = null,
        StopReason? Stop = null,
        Usage? UsageStats = null,
        long? Timestamp = null) : ChatMessage;

    public sealed record ToolResult(
        string ToolCallId,
        string ToolName,
        IReadOnlyList<MessageContent> Content,
        object? Details = null,
        bool IsError = false) : ChatMessage;
}

/// <summary>
/// Streaming event emitted by a provider. Mirrors TS <c>AssistantMessageEvent</c>
/// (start / text_delta / thinking_delta / toolcall_delta / done / error).
/// </summary>
public abstract record AssistantMessageEvent
{
    private AssistantMessageEvent() { }

    public sealed record Start(ChatMessage.Assistant Initial) : AssistantMessageEvent;

    public sealed record TextDelta(string Delta, long Sequence) : AssistantMessageEvent;

    public sealed record ThinkingDelta(string Delta, long Sequence, string? Signature) : AssistantMessageEvent;

    public sealed record ToolCallDelta(string Index, ChatMessage.Assistant Snapshot) : AssistantMessageEvent;

    public sealed record Done(ChatMessage.Assistant Message) : AssistantMessageEvent;

    public sealed record Error(string ErrorMessage) : AssistantMessageEvent;
}

/// <summary>
/// Protocol event stream. Mirrors TS <c>AssistantMessageEventStream</c>; the .NET
/// port exposes an <see cref="IAsyncEnumerable{T}"/> plus immediate-result access.
/// </summary>
public interface IAssistantMessageEventStream : IAsyncEnumerable<AssistantMessageEvent>
{
    /// <summary>The best-known partial message at call time (updated as events arrive).</summary>
    ChatMessage.Assistant? Partial { get; }

    /// <summary>Completes when the terminal event (done/error/abort) has been consumed.</summary>
    Task<ChatMessage.Assistant> WaitForDoneAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider stream context. Mirrors TS <c>TranscriptContext</c>: the system prompt
/// and tool declarations travel inside the transcript, never as loose fields.
/// </summary>
public sealed record TranscriptContext(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentTool> Tools,
    string? SystemPrompt = null);
