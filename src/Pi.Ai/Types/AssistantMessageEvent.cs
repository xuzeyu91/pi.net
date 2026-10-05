using Pi.Ai.Models;
using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// Provider 流式事件判别基类。对应 TS <c>AssistantMessageEvent</c> 联合类型。
/// 每个事件携带 <c>Partial</c> 快照：当前累积的部分助手消息（record 不可变，每次都是新快照）。
/// </summary>
public abstract record AssistantMessageEvent
{
    private AssistantMessageEvent() { }

    /// <summary>流开始（携带首个部分消息快照）。对应 TS <c>start</c>。</summary>
    public sealed record Start(AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>文本块开始。对应 TS <c>text_start</c>。</summary>
    public sealed record TextStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>文本增量。对应 TS <c>text_delta</c>。</summary>
    public sealed record TextDelta(int ContentIndex, string Delta, long Sequence, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>文本块结束（携带最终文本）。对应 TS <c>text_end</c>。</summary>
    public sealed record TextEnd(int ContentIndex, string Content, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>思维块开始。对应 TS <c>thinking_start</c>。</summary>
    public sealed record ThinkingStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>思维链增量。对应 TS <c>thinking_delta</c>。</summary>
    public sealed record ThinkingDelta(int ContentIndex, string Delta, long Sequence, string? Signature, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>思维块结束（携带最终内容）。对应 TS <c>thinking_end</c>。</summary>
    public sealed record ThinkingEnd(int ContentIndex, string Content, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>工具调用块开始。对应 TS <c>toolcall_start</c>。</summary>
    public sealed record ToolCallStart(int ContentIndex, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>工具调用参数增量。<c>Index</c> 为内容块序号。对应 TS <c>toolcall_delta</c>。</summary>
    public sealed record ToolCallDelta(int ContentIndex, int Index, string Delta, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>工具调用块结束（携带最终调用）。对应 TS <c>toolcall_end</c>。</summary>
    public sealed record ToolCallEnd(int ContentIndex, ToolCallContent ToolCall, AssistantMessage Partial) : AssistantMessageEvent;

    /// <summary>
    /// 终态：流正常结束。<c>Message</c> 为最终完整消息（含停止原因与用量）。
    /// 对应 TS <c>done</c>。
    /// </summary>
    public sealed record Done(StopReason Reason, AssistantMessage Message) : AssistantMessageEvent;

    /// <summary>终态：失败。错误编码在消息内。对应 TS <c>error</c>。</summary>
    public sealed record Error(StopReason Reason, string ErrorMessage, AssistantMessage Message) : AssistantMessageEvent;

    /// <summary>流是否已到达终态（done / error）。</summary>
    [JsonIgnore]
    public bool IsTerminal => this is Done or Error;

}

/// <summary>
/// 工具参数 JSON Schema。对应 typebox <c>TSchema</c> 在 C# 侧的承载：
/// 直接持有 JSON Schema 字典，保证与工具声明 wire 兼容。
/// </summary>
public sealed record ToolSchema(IReadOnlyDictionary<string, object?> JsonSchema);

/// <summary>AI 层工具声明（仅有元数据；可执行工具见 Pi.Agent 的 <c>AgentTool</c>）。</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    ToolSchema Parameters)
{
    /// <summary>约束采样配置（wire 形状：{type:"grammar",variants} | {type:"json_schema",strict}）。对应 TS <c>tool.constrainedSampling</c>。</summary>
    [JsonPropertyName("constrainedSampling")]
    public System.Text.Json.Nodes.JsonObject? ConstrainedSampling { get; set; }
}

/// <summary>模型描述。对应 TS <c>Model&lt;Api&gt;</c>。</summary>
public sealed record Model(
    string Id,
    string Name,
    string Api,
    string Provider,
    string? Reasoning = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>
/// 各 provider 共享的流式选项（简单入口）。对应 TS <c>SimpleStreamOptions</c>：
/// 位置参数保留旧构造兼容，完整字段经 init 属性补齐（headers/samplingParams/
/// maxRetries/cacheRetention/sessionId/transport/onResponse/onProviderStreamEvent 等）。
/// </summary>
public sealed record SimpleStreamOptions(
    string? ApiKey = null,
    string? BaseUrl = null,
    int? MaxTokens = null,
    double? Temperature = null,
    IReadOnlyList<string>? StopSequences = null,
    string? SessionId = null,
    bool? ChainOfThought = null)
{
    /// <summary>附加请求头（调用方值覆盖默认）。</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>任意采样参数（原样并入请求体；OpenAI 兼容适配器生效）。对应 TS <c>samplingParams</c>。</summary>
    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>WebSocket 连接握手超时（多传输 provider）。</summary>
    public int? WebsocketConnectTimeoutMs { get; init; }

    /// <summary>首选传输（auto/sse/websocket/websocket-cached）。</summary>
    public string? Transport { get; init; }

    /// <summary>提示缓存保留偏好（short/long/none；缺省 short）。</summary>
    public string? CacheRetention { get; init; }

    /// <summary>请求元数据（provider 自取所需字段）。</summary>
    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }

    /// <summary>作用域环境变量覆盖。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>请求取消令牌。对应 TS <c>signal</c>。</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>请求体替换钩子（返回 null 保持原体）。对应 TS <c>onPayload</c>。</summary>
    public Func<System.Text.Json.Nodes.JsonObject, ModelSpec, Task<System.Text.Json.Nodes.JsonObject?>>? OnPayload { get; init; }

    /// <summary>HTTP 响应到达回调。对应 TS <c>onResponse</c>。</summary>
    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    /// <summary>provider 流事件观察回调（Pi 归一化前）。对应 TS <c>onProviderStreamEvent</c>。</summary>
    public Func<System.Text.Json.Nodes.JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }

    /// <summary>provider 无关的工具选择。对应 TS <c>toolChoice</c>。</summary>
    public System.Text.Json.Nodes.JsonNode? ToolChoice { get; init; }

    /// <summary>推理档位（off/minimal/low/medium/high/xhigh/max）。对应 TS <c>reasoning</c>。</summary>
    public string? Reasoning { get; init; }

    /// <summary>请求延后句柄。对应 TS <c>deferred</c>（P31 落地，先占位）。</summary>
    public bool Deferred { get; init; }

    /// <summary>token 型 provider 的思考预算覆盖。对应 TS <c>thinkingBudgets</c>。</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}

/// <summary>思考档位 token 预算。对应 TS <c>ThinkingBudgets</c>。</summary>
public sealed record ThinkingBudgets(
    long? Minimal = null,
    long? Low = null,
    long? Medium = null,
    long? High = null);

/// <summary>
/// Provider 流上下文。对应 TS <c>TranscriptContext</c>：system prompt 与工具声明
/// 由 transcript 的 system 消息承载，不作为独立字段传入。
/// </summary>
public sealed record TranscriptContext(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null);

/// <summary>
/// 流式函数。对应 TS <c>StreamFn</c>（agent-loop 的 LLM 调用边界）。
/// 契约：不得抛出异常或返回失败任务——错误必须编码为终态事件
/// （StopReason=Error/Aborted 的 <c>Done</c>/<c>Error</c> 事件）。
/// </summary>
public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);

/// <summary>
/// 助手消息事件流的只读契约。对应 TS <c>AssistantMessageEventStream</c>：
/// 迭代事件流、随时可取当前部分消息快照、等待终态消息。
/// </summary>
public interface IAssistantMessageEventStream : IAsyncEnumerable<AssistantMessageEvent>
{
    /// <summary>调用时刻的已知部分消息快照（流开始前为 null）。</summary>
    AssistantMessage? Partial { get; }

    /// <summary>等待终态（done / error）并返回最终消息。</summary>
    Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default);
}

