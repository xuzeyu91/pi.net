using Pi.Ai.Stream;
using Pi.Ai.Utils;
using Pi.Ai.Types;
using Pi.Agent.Types;

namespace Pi.Agent;

/// <summary>
/// 低层 agent 循环。对应 TS <c>agent-loop.ts</c>：
/// 全程使用 AgentMessage（LLM 兼容消息仅在调用边界转换）。
/// 主循环 = 外层（follow-up 续跑）× 内层（工具调用 / steering 消息）。
/// </summary>
public static partial class AgentLoop
{
    /// <summary>
    /// 以新 prompt 消息启动 agent 循环。prompt 会追加进上下文并发出相应事件。
    /// 对应 TS <c>agentLoop()</c>。
    /// </summary>
    public static EventStream<AgentEvent, IReadOnlyList<ChatMessage>> Run(
        IReadOnlyList<ChatMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn streamFn)
    {
        var stream = CreateAgentStream();
        _ = PumpAsync(RunWithPrompts(prompts, context, config, PushSink(stream), signal, streamFn), stream);
        return stream;
    }

    /// <summary>
    /// 从当前上下文继续循环（用于重试；上下文最后一条消息须经 ConvertToLlm 转换为
    /// user 或 toolResult，否则 provider 会拒绝请求）。对应 TS <c>agentLoopContinue()</c>。
    /// </summary>
    public static EventStream<AgentEvent, IReadOnlyList<ChatMessage>> RunContinue(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        StreamFn streamFn)
    {
        ValidateContinueContext(context);
        var stream = CreateAgentStream();
        _ = PumpAsync(RunContinueCore(context, config, PushSink(stream), signal, streamFn), stream);
        return stream;
    }

    /// <summary>异步泵：循环完成则写终值，异常则转入流（对齐 TS 的 void run(...).then(end)）。</summary>
    private static async Task PumpAsync(
        Task<IReadOnlyList<ChatMessage>> run,
        EventStream<AgentEvent, IReadOnlyList<ChatMessage>> stream)
    {
        try
        {
            stream.End(await run.ConfigureAwait(false));
        }
        catch (Exception error)
        {
            stream.Fail(error);
        }
    }

    /// <summary>带 prompt 的异步入口（对应 TS <c>runAgentLoop</c>）。</summary>
    public static async Task<IReadOnlyList<ChatMessage>> RunWithPrompts(
        IReadOnlyList<ChatMessage> prompts,
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn streamFn)
    {
        var initialMessages = DeclareToolChanges(context, prompts);
        var newMessages = new List<ChatMessage>(initialMessages);
        var currentContext = context with
        {
            Messages = [.. context.Messages, .. initialMessages],
        };

        await emit(new AgentEvent.AgentStart());
        await emit(new AgentEvent.TurnStart());
        foreach (var message in initialMessages)
        {
            await emit(new AgentEvent.MessageStart(message));
            await emit(new AgentEvent.MessageEnd(message));
        }

        await RunLoop(currentContext, newMessages, config, signal, emit,
            streamFn ?? DefaultStreamFn.Get());
        return newMessages;
    }

    /// <summary>续跑入口（对应 TS <c>runAgentLoopContinue</c>）。</summary>
    public static async Task<IReadOnlyList<ChatMessage>> RunContinueCore(
        AgentContext context,
        AgentLoopConfig config,
        AgentEventSink emit,
        CancellationToken signal,
        StreamFn streamFn)
    {
        ValidateContinueContext(context);
        var newMessages = new List<ChatMessage>();
        await emit(new AgentEvent.AgentStart());
        await emit(new AgentEvent.TurnStart());
        await RunLoop(context, newMessages, config, signal, emit, streamFn ?? DefaultStreamFn.Get());
        return newMessages;
    }

    private static void ValidateContinueContext(AgentContext context)
    {
        if (context.Messages.Count == 0)
            throw new InvalidOperationException("Cannot continue: no messages in context");
        if (context.Messages[^1] is AssistantMessage)
            throw new InvalidOperationException("Cannot continue from message role: assistant");
    }

    private static EventStream<AgentEvent, IReadOnlyList<ChatMessage>> CreateAgentStream()
        => new();

    /// <summary>把同步 Push 适配为异步事件槽。</summary>
    private static AgentEventSink PushSink(EventStream<AgentEvent, IReadOnlyList<ChatMessage>> stream)
        => @event =>
        {
            stream.Push(@event);
            return Task.CompletedTask;
        };
}
