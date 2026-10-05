using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>文本内容块，对应 TS <c>TextContent</c>。</summary>
public sealed record TextContent(string Text) : ContentBlock;

/// <summary>图片内容块（base64 数据），对应 TS <c>ImageContent</c>。</summary>
public sealed record ImageContent(string Data, string? MimeType = null) : ContentBlock;

/// <summary>思维链内容块，对应 TS <c>ThinkingContent</c>；<c>Signature</c> 为 provider 签名，
/// <c>Redacted</c> 标记加密不可回放的思维块（anthropic redacted thinking）。</summary>
public sealed record ThinkingContent(string Thinking, string? Signature = null) : ContentBlock
{
    /// <summary>加密思维块：内容不透明，仅同模型可回放。对应 TS <c>redacted</c>。</summary>
    [JsonPropertyName("redacted")]
    public bool? Redacted { get; set; }
}

/// <summary>助手发起的工具调用块，对应 TS <c>ToolCall</c>。</summary>
public sealed record ToolCallContent(string Id, string Name, object? Arguments) : ContentBlock
{
    /// <summary>Google 系模型的 thought signature（跨模型回放时须剥离）。对应 TS <c>thoughtSignature</c>。</summary>
    [JsonPropertyName("thoughtSignature")]
    public string? ThoughtSignature { get; set; }
}

/// <summary>
/// 内容块判别基类。对应 TS <c>TextContent | ImageContent | ThinkingContent | ToolCall</c> 联合类型，
/// 以 <c>type</c> 字段作为 JSON 序列化的判别符（wire 兼容）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
public abstract record ContentBlock
{
    private protected ContentBlock() { }
}

/// <summary>助手回合停止原因。对应 TS <c>StopReason</c> 字符串联合。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    /// <summary>自然结束。</summary>
    Stop,

    /// <summary>输出被 token 上限截断。</summary>
    Length,

    /// <summary>请求了工具调用。</summary>
    ToolUse,

    /// <summary>请求/运行时失败（错误详情见 <c>ErrorMessage</c>）。</summary>
    Error,

    /// <summary>被用户中止。</summary>
    Aborted,
}

/// <summary>思考/推理档位。对应 TS <c>ThinkingLevel</c>；xhigh/max 仅部分模型家族支持。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>用量统计。对应 TS <c>Usage</c>。</summary>
public sealed record Usage(
    long Input,
    long Output,
    long CacheRead = 0,
    long CacheWrite = 0,
    double? Cost = null)
{
    /// <summary>输入 + 输出 token 合计（不含缓存读写）。</summary>
    [JsonIgnore]
    public long TotalTokens => Input + Output;
}

/// <summary>
/// 聊天消息判别基类。对应 TS <c>Message = SystemMessage | UserMessage | AssistantMessage |
/// ToolResultMessage</c>，以 <c>role</c> 字段判别。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record ChatMessage
{
    private protected ChatMessage() { }
}

/// <summary>
/// 系统消息。对应 TS <c>SystemMessage</c>：承载 system prompt 与工具声明回放，
/// <c>Sections</c> 非空时与 <c>Content</c> 按顺序拼接；
/// <c>Tools</c> 声明"模型当前可调用的工具集"（由 agent 循环维护增量）。
/// </summary>
public sealed record SystemMessage(
    string? Content = null,
    IReadOnlyList<string>? Sections = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    long? Timestamp = null) : ChatMessage;

/// <summary>用户消息。对应 TS <c>UserMessage</c>。</summary>
public sealed record UserMessage(IReadOnlyList<ContentBlock> Content, long Timestamp) : ChatMessage;

/// <summary>助手消息。对应 TS <c>AssistantMessage</c>。</summary>
public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,
    StopReason StopReason = Types.StopReason.Stop,
    string? ErrorMessage = null,
    Usage? UsageStats = null,
    string? Model = null,
    string? Api = null,
    string? Provider = null,
    ThinkingLevel? ThinkingLevel = null,
    long? Timestamp = null) : ChatMessage
{
    /// <summary>本条消息中的全部工具调用块（按出现顺序）。</summary>
    [JsonIgnore]
    public IEnumerable<ToolCallContent> ToolCalls =>
        Content.OfType<ToolCallContent>();
}

/// <summary>工具结果消息。对应 TS <c>ToolResultMessage</c>。</summary>
public sealed record ToolResultMessage(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<ContentBlock> Content,
    bool IsError = false,
    object? Details = null,
    long? Timestamp = null) : ChatMessage;

/// <summary>静态构造辅助（对齐 TS 侧消息工厂的易用性）。</summary>
public static class Messages
{
    public static UserMessage User(params ContentBlock[] content)
        => new(content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static UserMessage UserText(string text)
        => User(new TextContent(text));

    public static AssistantMessage AssistantText(string text, StopReason stopReason = Types.StopReason.Stop)
        => new([new TextContent(text)], stopReason,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ToolResultMessage ToolResult(
        string toolCallId, string toolName, string text, bool isError = false)
        => new(toolCallId, toolName, [new TextContent(text)], isError,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
