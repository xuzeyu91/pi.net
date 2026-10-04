using Pi.Ai.Types;
using Pi.Agent.Types;

namespace Pi.Agent;

/// <summary>
/// 工具批执行。对应 TS <c>executeToolCalls / executeToolCallsSequential /
/// executeToolCallsParallel / prepareToolCall / finalizeExecutedToolCall</c>。
/// <para>语义要点：预检（参数处理 + beforeToolCall）始终顺序执行；并行模式允许
/// 通过预检的工具并发执行——<c>tool_execution_end</c> 按完成序发出，而
/// tool-result 消息事件按助手消息中的源顺序发出；批提前终止仅在批内全部
/// 终结结果的 <c>Terminate</c> 均为 true 时生效。</para>
/// </summary>
public static partial class ToolExecution
{
    public sealed record ExecutedToolCallBatch(List<ToolResultMessage> Messages, bool Terminate);

    /// <summary>按配置的模式分发执行。</summary>
    public static async Task<ExecutedToolCallBatch> ExecuteToolCalls(
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var toolCalls = assistantMessage.ToolCalls.ToList();
        var hasSequentialToolCall = toolCalls.Any(tc =>
            (context.Tools ?? []).Any(t => t.Name == tc.Name && t.ExecutionMode == ToolExecutionMode.Sequential));

        if (config.ToolExecution == ToolExecutionMode.Sequential || hasSequentialToolCall)
            return await ExecuteSequential(context, assistantMessage, toolCalls, config, signal, emit).ConfigureAwait(false);
        return await ExecuteParallel(context, assistantMessage, toolCalls, config, signal, emit).ConfigureAwait(false);
    }

    /// <summary>
    /// 输出被 token 上限截断的助手消息中的全部工具调用一律报错：
    /// 流式参数经 JSON 抢救解析后可能"看似完整实则截断"，不可执行。
    /// 对应 TS <c>failToolCallsFromTruncatedMessage</c>。
    /// </summary>
    public static async Task<ExecutedToolCallBatch> FailToolCallsFromTruncatedMessage(
        IReadOnlyList<ToolCallContent> toolCalls, AgentEventSink emit)
    {
        var messages = new List<ToolResultMessage>();
        foreach (var toolCall in toolCalls)
        {
            await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
            var outcome = new AgentToolCallOutcome(toolCall, AgentToolResult.FromError(
                $"Tool call \"{toolCall.Name}\" was not executed: the response hit the output token limit, "
                + "so its arguments may be truncated. Re-issue the tool call with complete arguments."), true);
            await emit(new AgentEvent.ToolExecutionEnd(toolCall.Id, toolCall.Name, outcome.Result, true)).ConfigureAwait(false);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, Terminate: false);
    }

    // ---------- 顺序执行 ----------

    private static async Task<ExecutedToolCallBatch> ExecuteSequential(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var messages = new List<ToolResultMessage>();
        var finalized = new List<AgentToolCallOutcome>();
        foreach (var toolCall in toolCalls)
        {
            var outcome = await ExecuteOne(context, assistantMessage, toolCall, config, signal, emit).ConfigureAwait(false);
            finalized.Add(outcome);
            // 顺序模式：每个调用终结后立即发出 end 事件。
            await emit(new AgentEvent.ToolExecutionEnd(
                outcome.ToolCall.Id, outcome.ToolCall.Name, outcome.Result, outcome.IsError)).ConfigureAwait(false);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminateBatch(finalized));
    }

    /// <summary>顺序执行单个调用：预检 → 执行 → 终结（start 事件在预检前发出）。</summary>
    private static async Task<AgentToolCallOutcome> ExecuteOne(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCallContent toolCall,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
        var prepared = await PrepareToolCall(context, assistantMessage, toolCall, config, signal).ConfigureAwait(false);
        return prepared switch
        {
            PreparedCall.Blocked blocked => new AgentToolCallOutcome(toolCall, blocked.Result, blocked.Result.IsError),
            PreparedCall.Ready ready => await ExecuteAndFinalize(ready, context, assistantMessage, config, signal, emit).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown prepared call"),
        };
    }

    // ---------- 并行执行 ----------

    private static async Task<ExecutedToolCallBatch> ExecuteParallel(
        AgentContext context,
        AssistantMessage assistantMessage,
        IReadOnlyList<ToolCallContent> toolCalls,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        // 阶段一：顺序预检（发出 start 事件；参数处理 + beforeToolCall 阻断）。
        var prepared = new List<PreparedCall>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            await emit(new AgentEvent.ToolExecutionStart(toolCall.Id, toolCall.Name, toolCall.Arguments)).ConfigureAwait(false);
            prepared.Add(await PrepareToolCall(context, assistantMessage, toolCall, config, signal).ConfigureAwait(false));
        }

        // 阶段二：并行执行通过预检的调用（blocked 结果直接作为产出）。
        var tasks = new List<Task<(int Index, AgentToolCallOutcome Outcome)>>();
        for (var index = 0; index < prepared.Count; index++)
        {
            var currentIndex = index;
            var current = prepared[index];
            tasks.Add(Task.Run(async () =>
            {
                var outcome = current switch
                {
                    PreparedCall.Blocked blocked => new AgentToolCallOutcome(
                        blocked.ToolCall, blocked.Result, blocked.Result.IsError),
                    PreparedCall.Ready ready => await ExecuteAndFinalize(
                        ready, context, assistantMessage, config, signal, emit).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Unknown prepared call"),
                };
                return (currentIndex, outcome);
            }, CancellationToken.None));
        }

        var completed = new Dictionary<int, AgentToolCallOutcome>();
        await foreach (var finished in Task.WhenEach(tasks).ConfigureAwait(false))
        {
            var (index, outcome) = finished.Result;
            completed[index] = outcome;
            // end 事件按完成序发出（对齐 TS 的完成顺序语义）。
            await emit(new AgentEvent.ToolExecutionEnd(
                outcome.ToolCall.Id, outcome.ToolCall.Name, outcome.Result, outcome.IsError)).ConfigureAwait(false);
        }

        // 阶段三：tool-result 消息按助手消息源顺序合成并发出。
        var messages = new List<ToolResultMessage>();
        var finalized = new List<AgentToolCallOutcome>();
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var outcome = completed[index];
            finalized.Add(outcome);
            var message = CreateToolResultMessage(outcome);
            await emit(new AgentEvent.MessageStart(message)).ConfigureAwait(false);
            await emit(new AgentEvent.MessageEnd(message)).ConfigureAwait(false);
            messages.Add(message);
        }
        return new ExecutedToolCallBatch(messages, ShouldTerminateBatch(finalized));
    }

    // ---------- 预检结果 ----------

    /// <summary>预检结果的判别联合：可执行 / 被阻断。</summary>
    private abstract record PreparedCall
    {
        private PreparedCall() { }

        public sealed record Ready(ToolCallContent ToolCall, AgentTool Tool, object? Args) : PreparedCall;

        public sealed record Blocked(ToolCallContent ToolCall, AgentToolResult Result, bool Terminate) : PreparedCall;
    }

    /// <summary>
    /// 预检：查找工具 → prepareArguments 垫片 → beforeToolCall（可阻断）。
    /// 找不到工具 / 参数处理失败 / 阻断决策都会产出错误结果（跳过执行）。
    /// </summary>
    private static async Task<PreparedCall> PrepareToolCall(
        AgentContext context,
        AssistantMessage assistantMessage,
        ToolCallContent toolCall,
        AgentLoopConfig config,
        CancellationToken signal)
    {
        var tool = (context.Tools ?? []).FirstOrDefault(t => t.Name == toolCall.Name);
        if (tool is null)
            return new PreparedCall.Blocked(toolCall, AgentToolResult.FromError($"Tool not found: {toolCall.Name}"), false);

        // 参数兼容垫片；基础 JSON Schema 结构校验随 ai 包 provider 移植补全。
        var args = toolCall.Arguments;
        try
        {
            if (tool.PrepareArguments is not null)
                args = tool.PrepareArguments(args);
        }
        catch (Exception error)
        {
            return new PreparedCall.Blocked(
                toolCall,
                AgentToolResult.FromError($"Invalid arguments for tool {toolCall.Name}: {error.Message}"),
                false);
        }

        if (config.BeforeToolCall is not null)
        {
            var decision = await config.BeforeToolCall(
                new BeforeToolCallContext(assistantMessage, toolCall, args, context), signal).ConfigureAwait(false);
            if (decision?.Block == true)
            {
                var reason = decision.Reason ?? $"Tool call \"{toolCall.Name}\" was blocked.";
                return new PreparedCall.Blocked(
                    toolCall, AgentToolResult.FromError(reason), decision.Terminate == true);
            }
        }

        return new PreparedCall.Ready(toolCall, tool, args);
    }

    // ---------- 执行 / 终结 ----------

    private static async Task<AgentToolCallOutcome> ExecuteAndFinalize(
        PreparedCall.Ready prepared,
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit)
    {
        var executed = await ExecutePrepared(prepared, signal, emit).ConfigureAwait(false);
        return await Finalize(executed, prepared, context, assistantMessage, config, signal).ConfigureAwait(false);
    }

    /// <summary>执行已预检的工具调用（onUpdate 转发为 tool_execution_update 事件）。</summary>
    private static async Task<AgentToolResult> ExecutePrepared(
        PreparedCall.Ready prepared,
        CancellationToken signal,
        AgentEventSink emit)
    {
        try
        {
            // onUpdate 转发为 tool_execution_update 事件（对齐 TS 的 emitToolExecutionUpdate）。
            AgentToolUpdateCallback? onUpdate = partial =>
                emit(new AgentEvent.ToolExecutionUpdate(
                    prepared.ToolCall.Id, prepared.ToolCall.Name, prepared.Args, partial));
            return await prepared.Tool.Execute(prepared.ToolCall.Id, prepared.Args, signal, onUpdate).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 中止沿取消路径传播（由上层运行决定退出方式）。
            throw;
        }
        catch (Exception error)
        {
            // 抛出的错误视为失败结果（模型可见文本，UI 可读异常）。
            return AgentToolResult.FromError(error.Message);
        }
    }

    /// <summary>应用 afterToolCall 覆盖并产出最终结果。对应 TS <c>finalizeExecutedToolCall</c>。</summary>
    private static async Task<AgentToolCallOutcome> Finalize(
        AgentToolResult executed,
        PreparedCall.Ready prepared,
        AgentContext context,
        AssistantMessage assistantMessage,
        AgentLoopConfig config,
        CancellationToken signal)
    {
        var result = executed;
        var isError = executed.IsError;

        if (config.AfterToolCall is not null)
        {
            var patch = await config.AfterToolCall(
                new AfterToolCallContext(assistantMessage, prepared.ToolCall, prepared.Args, executed, isError, context),
                signal).ConfigureAwait(false);
            if (patch is not null)
            {
                // 逐字段覆盖：提供即替换，省略即保留原值；无深合并。
                result = executed with
                {
                    Content = patch.Content ?? executed.Content,
                    Details = patch.Details ?? executed.Details,
                    UsageStats = patch.UsageStats ?? executed.UsageStats,
                    IsError = patch.IsError ?? isError,
                    Terminate = patch.Terminate ?? executed.Terminate,
                };
                if (patch.StructuredContent is not null)
                    result = result with { StructuredContent = patch.StructuredContent };
                // content 被替换而未提供 structuredContent 时，结构化内容失效（可能不再匹配）。
                else if (patch.Content is not null)
                    result = result with { StructuredContent = null };
                isError = result.IsError;
            }
        }

        return new AgentToolCallOutcome(prepared.ToolCall, result, isError);
    }

    /// <summary>批提前终止：所有终结结果均要求终止才生效。</summary>
    private static bool ShouldTerminateBatch(IReadOnlyList<AgentToolCallOutcome> finalized)
        => finalized.Count > 0 && finalized.All(o => o.Result.Terminate);

    /// <summary>把工具结果合成为 toolResult 消息（源顺序）。</summary>
    private static ToolResultMessage CreateToolResultMessage(AgentToolCallOutcome outcome)
        => new(
            outcome.ToolCall.Id,
            outcome.ToolCall.Name,
            outcome.Result.Content,
            outcome.IsError,
            outcome.Result.Details,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
