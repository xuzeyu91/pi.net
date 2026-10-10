using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// 文本内容块，对应 TS <c>TextContent</c>；<c>TextSignature</c> 持 provider 的
/// 文本回执签名（openai-responses 的 TextSignatureV1 JSON）用于同模型重放。
/// </summary>
public sealed record TextContent(string Text) : ContentBlock
{
    [JsonPropertyName("textSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Redacted { get; set; }
}

/// <summary>助手发起的工具调用块，对应 TS <c>ToolCall</c>。</summary>
public sealed record ToolCallContent(string Id, string Name, object? Arguments) : ContentBlock
{
    /// <summary>Google 系模型的 thought signature（跨模型回放时须剥离）。对应 TS <c>thoughtSignature</c>。</summary>
    [JsonPropertyName("thoughtSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThoughtSignature { get; set; }

    /// <summary>工具命名空间（openai-responses custom tool）。对应 TS <c>namespace</c>。</summary>
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
/// <remarks>
/// TS 的线名是小写驼峰字面量；<see cref="JsonStringEnumMemberNameAttribute"/> 逐个钉死，
/// 以免依赖命名策略（<c>XHigh</c> 这类名字在任何策略下都得不到 TS 的 <c>"xhigh"</c>）。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    /// <summary>流处理中（尚未到达停止原因）。对应 TS <c>pending</c>。</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>自然结束。</summary>
    [JsonStringEnumMemberName("stop")]
    Stop,

    /// <summary>输出被 token 上限截断。</summary>
    [JsonStringEnumMemberName("length")]
    Length,

    /// <summary>请求了工具调用。</summary>
    [JsonStringEnumMemberName("toolUse")]
    ToolUse,

    /// <summary>请求/运行时失败（错误详情见 <c>ErrorMessage</c>）。</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>被用户中止。</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>请求被延后，<c>deferred</c> 句柄可续取最终结果。对应 TS <c>deferred</c>。</summary>
    [JsonStringEnumMemberName("deferred")]
    Deferred,
}

/// <summary>思考/推理档位。对应 TS <c>ThinkingLevel</c>；xhigh/max 仅部分模型家族支持。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("minimal")]
    Minimal,

    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("xhigh")]
    XHigh,

    [JsonStringEnumMemberName("max")]
    Max,
}

/// <summary>
/// 用量成本明细（美元）。对应 TS <c>Usage["cost"]</c>：provider 定价后逐桶写入，
/// <see cref="Total"/> 恒等于四桶之和。TS 侧是可变的 <c>usage.cost</c> 对象，端口用
/// 不可变记录 + <c>with</c> 表达（provider 重建 <see cref="Usage"/>）。
/// </summary>
public sealed record UsageCost(
    [property: JsonPropertyName("input")] double Input = 0,
    [property: JsonPropertyName("output")] double Output = 0,
    [property: JsonPropertyName("cacheRead")] double CacheRead = 0,
    [property: JsonPropertyName("cacheWrite")] double CacheWrite = 0,
    [property: JsonPropertyName("total")] double Total = 0)
{
    /// <summary>全零成本：provider 初始化用量时的起点（TS 的 <c>{ input: 0, …, total: 0 }</c>）。</summary>
    public static UsageCost Zero { get; } = new();

    /// <summary>按倍率缩放四桶与总价（服务档位加价）。对应 TS 侧对 <c>cost.total</c> 的乘法。</summary>
    public UsageCost Scale(double multiplier)
        => new(Input * multiplier, Output * multiplier, CacheRead * multiplier,
            CacheWrite * multiplier, Total * multiplier);

    /// <summary>按四桶重算 <see cref="Total"/>。对应 TS <c>calculateCost</c> 的末行。</summary>
    public UsageCost WithRecomputedTotal() => this with { Total = Input + Output + CacheRead + CacheWrite };
}

/// <summary>
/// 用量统计。对应 TS <c>Usage</c>。
/// </summary>
/// <remarks>
/// <see cref="TotalTokens"/> 是**存储字段**而非计算属性：TS 各 provider 定义不一致
/// （anthropic/google 用全桶之和，bedrock 用 input+output，mistral 用 wire 的 total_tokens，
/// openai 用全桶之和），故端口不再用 <c>Input + Output</c> 推断。需要「无上报则回退」的调用点
/// 应显式写 <c>usage.TotalTokens != 0 ? usage.TotalTokens : usage.Input + usage.Output +
/// usage.CacheRead + usage.CacheWrite</c>（对应 TS <c>utils/estimate.ts</c> 的 <c>||</c> 回退）。
/// </remarks>
public sealed record Usage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0)
{
    /// <summary>1h 保留期的缓存写子集；仅 Anthropic 上报。对应 TS <c>cacheWrite1h?</c>。</summary>
    [JsonPropertyName("cacheWrite1h")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWrite1h { get; init; }

    /// <summary>
    /// 推理/思维 token，<see cref="Output"/> 的子集；provider 未上报时为 null。
    /// 对应 TS <c>reasoning?</c>。
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Reasoning { get; init; }

    /// <summary>总 token（provider 上报值）。对应 TS <c>totalTokens</c>。</summary>
    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    /// <summary>成本明细。对应 TS <c>cost</c>。</summary>
    [JsonPropertyName("cost")]
    public UsageCost Cost { get; init; } = UsageCost.Zero;
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
    // protected（而非 private protected）：coding-agent 的 4 个自定义 role
    // （bashExecution / custom / branchSummary / compactionSummary）在 Pi.CodingAgent 中派生，
    // 对应 TS 侧 pi-agent-core 的 CustomAgentMessages declaration merging。
    protected ChatMessage() { }
}

/// <summary>
/// 系统消息。对应 TS <c>SystemMessage</c>：transcript 的工具/prompt 增量载体。
/// 首条 system 消息是系统提示；后续的 <c>Content</c> 追加提示、<c>Sections</c>
/// 按名修补（value 为 null 表示删除该段）、<c>ToolsAdded</c>/<c>ToolsRemoved</c>
/// 增量变更工具集。按序回放全部 system 消息得到当前 prompt 与工具。
/// </summary>
public sealed record SystemMessage(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string?>? Sections = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ToolDefinition>? ToolsAdded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ToolsRemoved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage;

/// <summary>用户消息。对应 TS <c>UserMessage</c>。</summary>
public sealed record UserMessage(IReadOnlyList<ContentBlock> Content, long Timestamp) : ChatMessage;

/// <summary>助手消息。对应 TS <c>AssistantMessage</c>。</summary>
/// <remarks>
/// 可空字段一律「null 即省略」，对齐 TS 里 <c>undefined</c> 不参与 JSON 序列化的行为。
/// </remarks>
public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,
    StopReason StopReason = Types.StopReason.Stop,
    [property: JsonPropertyName("errorMessage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Usage? UsageStats = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Api = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ThinkingLevel? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage
{
    /// <summary>provider 原始停止原因（如 "incomplete.max_output_tokens"）。对应 TS <c>rawStopReason</c>。</summary>
    [JsonPropertyName("rawStopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawStopReason { get; set; }

    /// <summary>provider 报告的实际模型（与请求的 <c>model</c> 不同时）。对应 TS <c>responseModel</c>。</summary>
    [JsonPropertyName("responseModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseModel { get; set; }

    /// <summary>响应 id（openai-responses 等）。对应 TS <c>responseId</c>。</summary>
    [JsonPropertyName("responseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; set; }

    /// <summary>流处理附加的诊断条目（传输降级等）。对应 TS <c>diagnostics</c>。</summary>
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Utils.AssistantMessageDiagnostic>? Diagnostics { get; set; }

    /// <summary>模型是否自然说完（codex end_turn）。对应 TS <c>endTurn</c>。</summary>
    [JsonPropertyName("endTurn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EndTurn { get; set; }

    /// <summary>provider 侧实际使用的思考档位（pi-messages 回传）。对应 TS <c>providerThinkingLevel</c>。</summary>
    [JsonPropertyName("providerThinkingLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderThinkingLevel { get; set; }

    /// <summary>延后响应句柄（StopReason=Deferred 时携带）。对应 TS <c>deferred</c>。</summary>
    [JsonPropertyName("deferred")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeferredHandle? Deferred { get; set; }

    /// <summary>从 <c>timestamp</c> 到响应结束的毫秒数（单调时钟）。对应 TS <c>durationMs</c>。</summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage
{
    /// <summary>
    /// 工具执行自身的用量（不计入主 LLM 上下文账）。对应 TS <c>usage?</c>。
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? UsageStats { get; set; }

    /// <summary>工具执行耗时（毫秒，单调时钟）。对应 TS <c>durationMs</c>。</summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }
}

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
