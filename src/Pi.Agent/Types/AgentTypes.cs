using System.Text.Json.Serialization;
using Pi.Ai.Types;

namespace Pi.Agent.Types;

/// <summary>
/// Agent 事件判别基类。对应 TS <c>AgentEvent</c> 联合类型（agent / turn / message /
/// tool execution 四组生命周期事件）。<c>agent_end</c> 是一次运行的最后一个事件。
/// </summary>
public abstract record AgentEvent
{
    private AgentEvent() { }

    // ---- Agent 生命周期 ----

    public sealed record AgentStart : AgentEvent;

    /// <summary>运行结束。<c>Messages</c> 为本次运行新增的全部消息。</summary>
    public sealed record AgentEnd(IReadOnlyList<ChatMessage> Messages) : AgentEvent;

    // ---- Turn 生命周期（一个 turn = 一次助手回复 + 其工具调用/结果） ----

    public sealed record TurnStart : AgentEvent;

    public sealed record TurnEnd(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults) : AgentEvent;

    // ---- 消息生命周期（system / user / assistant / toolResult 均触发） ----

    public sealed record MessageStart(ChatMessage Message) : AgentEvent;

    /// <summary>仅流式过程中的助手消息触发；携带原始助手事件与消息快照。</summary>
    public sealed record MessageUpdate(AssistantMessageEvent AssistantMessageEvent, AssistantMessage Message) : AgentEvent;

    public sealed record MessageEnd(ChatMessage Message) : AgentEvent;

    // ---- 工具执行生命周期 ----

    public sealed record ToolExecutionStart(string ToolCallId, string ToolName, object? Args) : AgentEvent;

    public sealed record ToolExecutionUpdate(string ToolCallId, string ToolName, object? Args, object? PartialResult) : AgentEvent;

    public sealed record ToolExecutionEnd(string ToolCallId, string ToolName, object? Result, bool IsError) : AgentEvent;
}

/// <summary>事件槽：AgentLoop 的每一事件都经过它发出。对应 TS <c>AgentEventSink</c>。</summary>
public delegate Task AgentEventSink(AgentEvent @event);

/// <summary>
/// 到达队列排空点时注入的排队用户消息数量。对应 TS <c>QueueMode</c>。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueMode>))]
public enum QueueMode
{
    /// <summary>在该排空点注入全部排队消息。</summary>
    All,

    /// <summary>每次只注入最旧的一条，其余留给后续排空点。</summary>
    OneAtATime,
}

/// <summary>工具执行结果。对应 TS <c>AgentToolResult</c>。</summary>
public sealed record AgentToolResult(
    IReadOnlyList<ContentBlock> Content,
    object? Details = null,
    object? StructuredContent = null,
    Usage? UsageStats = null,
    bool IsError = false,
    bool Terminate = false)
{
    /// <summary>构造错误结果（模型看到文本，UI 保留 details）。</summary>
    public static AgentToolResult FromError(string text, object? details = null)
        => new([new TextContent(text)], Details: details, IsError: true);
}

/// <summary>工具调用经钩子处理后的最终结果。对应 TS <c>AgentToolCallOutcome</c>。</summary>
public sealed record AgentToolCallOutcome(
    ToolCallContent ToolCall,
    AgentToolResult Result,
    bool IsError);

/// <summary>工具执行时的流式部分结果回调。对应 TS <c>AgentToolUpdateCallback</c>。</summary>
public delegate Task AgentToolUpdateCallback(AgentToolResult partialResult);

/// <summary>
/// Agent 运行时可执行工具。对应 TS <c>AgentTool&lt;TParameters&gt;</c>（继承 ai 包的
/// <c>Tool</c> 声明）。<c>Execute</c> 失败应抛异常或返回 <c>IsError = true</c> 的结果。
/// </summary>
public sealed record AgentTool(
    string Name,
    string Description,
    ToolSchema Parameters,
    string Label,
    Func<string, object?, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> Execute,
    ToolExecutionMode? ExecutionMode = null,
    Func<object?, object?>? PrepareArguments = null)
{
    /// <summary>构造可被 ai 包声明使用的视图。</summary>
    public ToolDefinition Definition => new(Name, Description, Parameters);
}

/// <summary>工具调用钩子上下文。对应 TS <c>BeforeToolCallContext</c> / <c>AfterToolCallContext</c>。</summary>
public sealed record BeforeToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCallContent ToolCall,
    object? Args,
    AgentContext Context);

public sealed record AfterToolCallContext(
    AssistantMessage AssistantMessage,
    ToolCallContent ToolCall,
    object? Args,
    AgentToolResult Result,
    bool IsError,
    AgentContext Context);

/// <summary><c>beforeToolCall</c> 的返回值。对应 TS <c>BeforeToolCallResult</c>：
/// Block=true 阻止执行并发出错误工具结果；Terminate 参与批提前终止规则。</summary>
public sealed record BeforeToolCallResult(bool? Block = null, string? Reason = null, bool? Terminate = null);

/// <summary><c>afterToolCall</c> 的部分覆盖。逐字段合并，无深合并。对应 TS <c>AfterToolCallResult</c>。</summary>
public sealed record AfterToolCallResult(
    IReadOnlyList<ContentBlock>? Content = null,
    object? Details = null,
    object? StructuredContent = null,
    bool? IsError = null,
    Usage? UsageStats = null,
    bool? Terminate = null);

/// <summary>完成的回合快照（finishTurn / prepareNextTurn 的入参）。对应 TS <c>AgentTurnContext</c>。</summary>
public sealed record AgentTurnContext(
    AssistantMessage Message,
    IReadOnlyList<ToolResultMessage> ToolResults,
    AgentContext Context,
    IReadOnlyList<ChatMessage> NewMessages);

/// <summary>finishTurn 的调度决策。对应 TS <c>AgentTurnDecision</c>；null 保持正常调度。</summary>
public abstract record AgentTurnDecision
{
    private AgentTurnDecision() { }

    public sealed record Continue : AgentTurnDecision;

    public sealed record End : AgentTurnDecision;
}

/// <summary>下一次 provider 请求前的运行时状态替换。对应 TS <c>AgentLoopTurnUpdate</c>。</summary>
public sealed record AgentLoopTurnUpdate(
    AgentContext? Context = null,
    IReadOnlyList<ChatMessage>? Messages = null,
    Model? Model = null,
    ThinkingLevel? ThinkingLevel = null);

/// <summary>
/// Agent 循环配置。对应 TS <c>AgentLoopConfig</c>：所有委托钩子契约均为
/// "不得抛出异常"，失败时应返回安全回退值。
/// </summary>
public sealed record AgentLoopConfig
{
    /// <summary>本次运行的模型。</summary>
    public required Model Model { get; init; }

    /// <summary>每次 LLM 调用前把 AgentMessage 列表转换为 LLM 兼容消息（不可抛出）。</summary>
    public required Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>> ConvertToLlm { get; init; }

    /// <summary>转换前的上下文变换（裁剪/注入），可空。</summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext { get; init; }

    /// <summary>按 provider 动态解析 API key（支持短时 OAuth token），可空。</summary>
    public Func<string, Task<string?>>? GetApiKey { get; init; }

    /// <summary>回合完成后的调度钩子（返回 Continue/End），可空。</summary>
    public Func<AgentTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurn { get; init; }

    /// <summary>每次 provider 请求前的状态替换钩子，可空。</summary>
    public Func<AgentContext, Model, ThinkingLevel, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareRequest { get; init; }

    /// <summary>turn_end 之后、下一回合开始前的准备钩子，可空。</summary>
    public Func<AgentTurnContext, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    /// <summary>取插入运行中途的 steering 消息（不可抛出；无消息返回空数组）。</summary>
    public Func<Task<IReadOnlyList<ChatMessage>>>? GetSteeringMessages { get; init; }

    /// <summary>取 agent 本应停止时的 follow-up 消息（不可抛出；无消息返回空数组）。</summary>
    public Func<Task<IReadOnlyList<ChatMessage>>>? GetFollowUpMessages { get; init; }

    /// <summary>工具执行模式；默认 Parallel（与 TS 默认一致）。</summary>
    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

    /// <summary>工具调用前钩子（参数验证后调用；Block=true 阻止执行）。</summary>
    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

    /// <summary>工具执行后、事件发出前的覆盖钩子（逐字段合并）。</summary>
    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; init; }

    /// <summary>基础流式选项（API key 等由循环内动态覆盖）。</summary>
    public SimpleStreamOptions? StreamOptions { get; init; }
}

/// <summary>工具执行模式。对应 TS <c>ToolExecutionMode</c>；默认 parallel。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolExecutionMode>))]
public enum ToolExecutionMode
{
    /// <summary>逐个准备、执行、终结。</summary>
    Sequential,

    /// <summary>顺序预检后并行执行允许的工具；end 事件按完成序、消息按源序。</summary>
    Parallel,
}

/// <summary>循环可见的上下文快照。对应 TS <c>AgentContext</c>。</summary>
public sealed record AgentContext
{
    /// <summary>模型可见的对话 transcript（可变列表，循环内原地追加）。</summary>
    public required List<ChatMessage> Messages { get; set; }

    /// <summary>本次运行可执行的工具集。</summary>
    public IReadOnlyList<AgentTool>? Tools { get; set; }
}
