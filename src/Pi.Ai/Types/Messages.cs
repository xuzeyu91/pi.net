using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// 文本内容块，对应 TS <c>TextContent</c>；<c>TextSignature</c> 持 provider 的
/// 文本回执签名（openai-responses 的 TextSignatureV1 JSON）用于同模型重放。
/// </summary>
public sealed record TextContent(string Text) : ContentBlock
{
    [JsonPropertyName("textSignature")]
    public string? TextSignature { get; set; }
}

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

    /// <summary>工具命名空间（openai-responses custom tool）。对应 TS <c>namespace</c>。</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }
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
    /// <summary>流处理中（尚未到达停止原因）。对应 TS <c>pending</c>。</summary>
    Pending,

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

    /// <summary>请求被延后，<c>deferred</c> 句柄可续取最终结果。对应 TS <c>deferred</c>。</summary>
    Deferred,
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
    double? Cost = null,
    long Reasoning = 0)
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
/// 系统消息。对应 TS <c>SystemMessage</c>：transcript 的工具/prompt 增量载体。
/// 首条 system 消息是系统提示；后续的 <c>Content</c> 追加提示、<c>Sections</c>
/// 按名修补（value 为 null 表示删除该段）、<c>ToolsAdded</c>/<c>ToolsRemoved</c>
/// 增量变更工具集。按序回放全部 system 消息得到当前 prompt 与工具。
/// </summary>
public sealed record SystemMessage(
    string? Content = null,
    IReadOnlyDictionary<string, string?>? Sections = null,
    IReadOnlyList<ToolDefinition>? ToolsAdded = null,
    IReadOnlyList<string>? ToolsRemoved = null,
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
    /// <summary>provider 原始停止原因（如 "incomplete.max_output_tokens"）。对应 TS <c>rawStopReason</c>。</summary>
    [JsonPropertyName("rawStopReason")]
    public string? RawStopReason { get; set; }

    /// <summary>provider 报告的实际模型（与请求的 <c>model</c> 不同时）。对应 TS <c>responseModel</c>。</summary>
    [JsonPropertyName("responseModel")]
    public string? ResponseModel { get; set; }

    /// <summary>响应 id（openai-responses 等）。对应 TS <c>responseId</c>。</summary>
    [JsonPropertyName("responseId")]
    public string? ResponseId { get; set; }

    /// <summary>流处理附加的诊断条目（传输降级等）。对应 TS <c>diagnostics</c>。</summary>
    [JsonPropertyName("diagnostics")]
    public List<Utils.AssistantMessageDiagnostic>? Diagnostics { get; set; }

    /// <summary>模型是否自然说完（codex end_turn）。对应 TS <c>endTurn</c>。</summary>
    [JsonPropertyName("endTurn")]
    public bool? EndTurn { get; set; }

    /// <summary>provider 侧实际使用的思考档位（pi-messages 回传）。对应 TS <c>providerThinkingLevel</c>。</summary>
    [JsonPropertyName("providerThinkingLevel")]
    public string? ProviderThinkingLevel { get; set; }

    /// <summary>延后响应句柄（StopReason=Deferred 时携带）。对应 TS <c>deferred</c>。</summary>
    [JsonPropertyName("deferred")]
    public DeferredHandle? Deferred { get; set; }

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
