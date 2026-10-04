// ============================================================================
// PORT SKELETON - packages/agent (2.5k lines TS: agent-loop, agent, proxy,
// stream-fn, types). The full port of agent-loop.ts (940 lines) lands in the
// next session; the type surface below mirrors packages/agent/src/types.ts.
// ============================================================================

using Pi.Ai;

namespace Pi.Agent;

/// <summary>
/// Stream function used by the agent loop; <c>Models.streamSimple</c> satisfies this shape.
/// Mirrors TS <c>StreamFn</c>. Contract: must not throw for request/model/runtime
/// failures - encode them in the returned stream with stopReason "error"/"aborted".
/// </summary>
public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model,
    TranscriptContext context,
    SimpleStreamOptions? options,
    CancellationToken cancellationToken);

/// <summary>How tool calls from one assistant message execute. Mirrors TS <c>ToolExecutionMode</c>.</summary>
public enum ToolExecutionMode
{
    /// <summary>Each tool call is prepared, executed, and finalized before the next one starts.</summary>
    Sequential,

    /// <summary>Tool calls are prepared sequentially, then allowed tools execute concurrently.</summary>
    Parallel,
}

/// <summary>How many queued user messages drain at each drain point. Mirrors TS <c>QueueMode</c>.</summary>
public enum QueueMode
{
    All,
    OneAtATime,
}

/// <summary>Decision returned from <c>BeforeToolCall</c>. Mirrors TS <c>BeforeToolCallResult</c>.</summary>
public sealed record BeforeToolCallResult(bool? Block = null, string? Reason = null, bool? Terminate = null);

/// <summary>Partial override returned from <c>AfterToolCall</c>. Mirrors TS <c>AfterToolCallResult</c>
/// (field-by-field merge; no deep merge for content/details/usage).</summary>
public sealed record AfterToolCallResult(
    IReadOnlyList<MessageContent>? Content = null,
    object? Details = null,
    bool? IsError = null,
    Usage? UsageStats = null,
    bool? Terminate = null);

/// <summary>Replacement runtime state before another provider request. Mirrors TS <c>AgentLoopTurnUpdate</c>.</summary>
public sealed record AgentLoopTurnUpdate(
    TranscriptContext? Context = null,
    IReadOnlyList<ChatMessage>? Messages = null,
    Model? Model = null,
    ThinkingLevel? ThinkingLevel = null);

/// <summary>Loop configuration. Mirrors TS <c>AgentLoopConfig</c>.</summary>
public sealed record AgentLoopConfig(
    Model Model,
    Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>> ConvertToLlm,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext = null,
    ToolExecutionMode ToolExecutionMode = ToolExecutionMode.Sequential,
    QueueMode QueueMode = QueueMode.All,
    SimpleStreamOptions? StreamOptions = null);

/// <summary>
/// The agent loop state machine. Mirrors TS <c>AgentLoop</c>; implementation lands
/// next session with its full event surface (agent_start / turn_start /
/// message_start / message_update / message_end / tool_execution_* / turn_end /
/// agent_end) and queue/steering semantics.
/// </summary>
public sealed class AgentLoop(AgentLoopConfig config, StreamFn streamFn)
{
    public AgentLoopConfig Config { get; } = config;

    public StreamFn StreamFn { get; } = streamFn;

    /// <summary>Runs the loop until the model stops requesting tools and the queue is drained.</summary>
    public Task<IReadOnlyList<ChatMessage>> PromptAsync(string message, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Port of packages/agent/src/agent-loop.ts - scheduled next session.");
}
