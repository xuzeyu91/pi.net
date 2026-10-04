using Pi.Ai.Types;
using Pi.Agent.Types;

namespace Pi.Agent;

/// <summary>
/// 主循环实现。对应 TS <c>runLoop</c> / <c>streamAssistantResponse</c> /
/// <c>declareToolChanges</c>。
/// </summary>
public static partial class AgentLoop
{
    /// <summary>
    /// 主循环（agentLoop 与 agentLoopContinue 共享）。
    /// 外层循环：agent 本应停止时若有 follow-up 消息则继续；
    /// 内层循环：处理工具调用与 steering 消息，直到模型不再请求工具且队列已清空。
    /// </summary>
    private static async Task RunLoop(
        AgentContext initialContext,
        List<ChatMessage> newMessages,
        AgentLoopConfig initialConfig,
        CancellationToken signal,
        AgentEventSink emit,
        StreamFn streamFunction)
    {
        var currentContext = initialContext;
        var config = initialConfig;
        AgentTurnContext? lastCompletedTurn = null;
        var explicitContinuation = false;
        // 起始处先查一次 steering（用户可能在等待期间已输入）。
        var pendingMessages = new List<ChatMessage>(
            await PollAsync(config.GetSteeringMessages).ConfigureAwait(false));

        while (true)
        {
            var hasMoreToolCalls = true;

            // 内层循环：处理工具调用与 steering 消息。
            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                var preparedMessages = new List<ChatMessage>();
                if (lastCompletedTurn is not null)
                {
                    var nextTurnSnapshot = await (config.PrepareNextTurn?.Invoke(lastCompletedTurn)
                        ?? Task.FromResult<AgentLoopTurnUpdate?>(null)).ConfigureAwait(false);
                    if (nextTurnSnapshot is not null)
                    {
                        currentContext = nextTurnSnapshot.Context ?? currentContext;
                        preparedMessages = [.. (nextTurnSnapshot.Messages ?? [])];
                        config = config with
                        {
                            Model = nextTurnSnapshot.Model ?? config.Model,
                        };
                    }

                    // Prepare 可能长时运行（例如 compaction）。仅当上一次轮询为空时再查一次，
                    // 否则 one-at-a-time 模式会在同一回合投递两条消息。
                    if (pendingMessages.Count == 0)
                        pendingMessages = [.. await PollAsync(config.GetSteeringMessages).ConfigureAwait(false)];
                    await emit(new AgentEvent.TurnStart());
                }

                // 下一次助手响应前，处理 prepared 与排队的消息。
                foreach (var pending in DeclareToolChanges(currentContext, [.. preparedMessages, .. pendingMessages]))
                {
                    await emit(new AgentEvent.MessageStart(pending));
                    await emit(new AgentEvent.MessageEnd(pending));
                    currentContext.Messages.Add(pending);
                    newMessages.Add(pending);
                }
                pendingMessages.Clear();

                var requestUpdate = await (config.PrepareRequest?.Invoke(
                        currentContext, config.Model, ThinkingLevel.Off, signal)
                    ?? Task.FromResult<AgentLoopTurnUpdate?>(null)).ConfigureAwait(false);
                if (requestUpdate is not null)
                {
                    currentContext = requestUpdate.Context ?? currentContext;
                    config = config with { Model = requestUpdate.Model ?? config.Model };
                }

                // 流式获取助手响应。
                var message = await StreamAssistantResponse(
                    currentContext, config, signal, emit, streamFunction).ConfigureAwait(false);
                newMessages.Add(message);

                if (message.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    // 错误与中止是硬退出：不再轮询队列。
                    lastCompletedTurn = new AgentTurnContext(message, [], currentContext, newMessages);
                    if (config.FinishTurn is not null) await config.FinishTurn(lastCompletedTurn, signal).ConfigureAwait(false);
                    await emit(new AgentEvent.TurnEnd(message, []));
                    await emit(new AgentEvent.AgentEnd(newMessages));
                    return;
                }

                // 收集工具调用。
                var toolCalls = message.ToolCalls.ToList();

                var toolResults = new List<ToolResultMessage>();
                hasMoreToolCalls = false;
                if (toolCalls.Count > 0)
                {
                    // "length" 表示输出被 token 上限截断：所有工具调用的参数都可能被
                    // 截断且不可安全执行，全部报错让模型重新发起。
                    var executedToolBatch = message.StopReason == StopReason.Length
                        ? await ToolExecution.FailToolCallsFromTruncatedMessage(toolCalls, emit).ConfigureAwait(false)
                        : await ToolExecution.ExecuteToolCalls(
                            currentContext, message, config, signal, emit).ConfigureAwait(false);
                    toolResults.AddRange(executedToolBatch.Messages);
                    hasMoreToolCalls = !executedToolBatch.Terminate;

                    foreach (var result in toolResults)
                    {
                        currentContext.Messages.Add(result);
                        newMessages.Add(result);
                    }
                }

                lastCompletedTurn = new AgentTurnContext(message, toolResults, currentContext, newMessages);
                var decision = await (config.FinishTurn?.Invoke(lastCompletedTurn, signal)
                    ?? Task.FromResult<AgentTurnDecision?>(null)).ConfigureAwait(false);
                await emit(new AgentEvent.TurnEnd(message, toolResults));

                if (decision is AgentTurnDecision.End)
                {
                    await emit(new AgentEvent.AgentEnd(newMessages));
                    return;
                }

                explicitContinuation = decision is AgentTurnDecision.Continue;
                pendingMessages = [.. await PollAsync(config.GetSteeringMessages).ConfigureAwait(false)];
                if (hasMoreToolCalls || pendingMessages.Count > 0)
                    explicitContinuation = false;
            }

            // agent 本应停止：检查 follow-up 消息。
            var followUpMessages = await PollAsync(config.GetFollowUpMessages).ConfigureAwait(false);
            if (followUpMessages.Count > 0)
            {
                explicitContinuation = false;
                pendingMessages = [.. followUpMessages];
                continue;
            }

            // 没有自然产生的请求：用一次仅上下文的回合兑现 continuation 决策。
            if (explicitContinuation)
            {
                explicitContinuation = false;
                continue;
            }

            break;
        }

        await emit(new AgentEvent.AgentEnd(newMessages));
    }

    /// <summary>钩子安全轮询：委托缺失或抛出时返回空列表（契约：钩子不得抛出）。</summary>
    private static async Task<IReadOnlyList<ChatMessage>> PollAsync(
        Func<Task<IReadOnlyList<ChatMessage>>>? poll)
    {
        try
        {
            if (poll is null) return [];
            return await poll().ConfigureAwait(false) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 向模型声明工具装载差异。
    /// <para><c>context.Tools</c> 是运行时可执行集；transcript 的 system 消息声明模型可调用集。
    /// 每次请求前把两者的差异作为新增/移除写入 system 消息的声明集，使回放后恰好等于
    /// 可执行集。若 pending 中已有 system 消息则视为意图并直接替换声明集，否则在第一条
    /// 非 system 的 pending 消息之前插入新 system 消息。</para>
    /// 对应 TS <c>declareToolChanges</c>。
    /// </summary>
    public static IReadOnlyList<ChatMessage> DeclareToolChanges(
        AgentContext context, IReadOnlyList<ChatMessage> pendingMessages)
    {
        var executable = (context.Tools ?? []).Select(t => t.Definition).ToList();

        // 已提交（transcript）的声明集：最后一条携带工具声明的 system 消息。
        IReadOnlyList<ToolDefinition>? committed = null;
        foreach (var message in context.Messages)
        {
            if (message is SystemMessage { Tools: not null } system)
                committed = system.Tools;
        }

        var declared = committed ?? [];

        var added = executable.Where(t => !declared.Any(d => d.Name == t.Name)).ToList();
        var removed = declared.Where(d => !executable.Any(t => t.Name == d.Name)).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return pendingMessages; // 无差异

        var newDeclaration = executable;

        // pending 中已有的 system 消息视为意图：替换其声明集。
        for (var i = pendingMessages.Count - 1; i >= 0; i--)
        {
            if (pendingMessages[i] is not SystemMessage pendingSystem) continue;
            var replacement = pendingSystem with { Tools = newDeclaration };
            var result = pendingMessages.ToList();
            result[i] = replacement;
            return result;
        }

        // 否则插入新的 system 消息（在第一条非 system 的 pending 之前）。
        var insertIndex = 0;
        while (insertIndex < pendingMessages.Count && pendingMessages[insertIndex] is SystemMessage)
            insertIndex++;

        var changeNote = BuildToolChangeNote(added, removed);
        var declaration = new SystemMessage(
            Content: changeNote.Length > 0 ? changeNote : null,
            Tools: newDeclaration);
        var withInsert = pendingMessages.ToList();
        withInsert.Insert(insertIndex, declaration);
        return withInsert;
    }

    private static string BuildToolChangeNote(
        IReadOnlyList<ToolDefinition> added, IReadOnlyList<ToolDefinition> removed)
    {
        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add("Tools added: " + string.Join(", ", added.Select(t => t.Name)));
        if (removed.Count > 0)
            parts.Add("Tools removed: " + string.Join(", ", removed.Select(t => t.Name)));
        return string.Join("; ", parts);
    }

    /// <summary>取事件携带的部分消息快照（终态事件取最终消息）。</summary>
    private static AssistantMessage? GetPartial(AssistantMessageEvent @event) => @event switch
    {
        AssistantMessageEvent.Start start => start.Partial,
        AssistantMessageEvent.TextStart textStart => textStart.Partial,
        AssistantMessageEvent.TextDelta { Partial: var p } => p,
        AssistantMessageEvent.TextEnd { Partial: var p } => p,
        AssistantMessageEvent.ThinkingDelta { Partial: var p } => p,
        AssistantMessageEvent.ToolCallDelta { Partial: var p } => p,
        AssistantMessageEvent.Done done => done.Message,
        AssistantMessageEvent.Error error => error.Message,
        _ => null,
    };

    /// <summary>
    /// 流式获取一条助手响应。对应 TS <c>streamAssistantResponse</c>：
    /// transformContext → convertToLlm → 解析 API key → streamFn；
    /// 事件逐条转发为 message_start / message_update / message_end。
    /// </summary>
    private static async Task<AssistantMessage> StreamAssistantResponse(
        AgentContext context,
        AgentLoopConfig config,
        CancellationToken signal,
        AgentEventSink emit,
        StreamFn streamFunction)
    {
        // 上下文变换（AgentMessage[] → AgentMessage[]）。
        IReadOnlyList<ChatMessage> messages = context.Messages;
        if (config.TransformContext is not null)
            messages = await config.TransformContext(messages, signal).ConfigureAwait(false);

        // 转换为 LLM 兼容消息（调用边界）。
        var llmMessages = await config.ConvertToLlm(messages).ConfigureAwait(false);
        var tools = context.Tools?.Select(t => t.Definition).ToList();
        var llmContext = new TranscriptContext(llmMessages, tools);

        // 解析 API key（支持会过期的短时 token）。
        var resolvedApiKey = config.GetApiKey is not null
            ? await config.GetApiKey(config.Model.Provider).ConfigureAwait(false)
            : null;
        resolvedApiKey ??= config.StreamOptions?.ApiKey;

        var response = await streamFunction(
            config.Model,
            llmContext,
            config.StreamOptions is null
                ? new SimpleStreamOptions(ApiKey: resolvedApiKey)
                : config.StreamOptions with { ApiKey = resolvedApiKey },
            signal).ConfigureAwait(false);

        var partialMessage = (AssistantMessage?)null;
        var addedPartial = false;

        await foreach (var @event in response.WithCancellation(signal).ConfigureAwait(false))
        {
            switch (@event)
            {
                case AssistantMessageEvent.Start start:
                    partialMessage = start.Partial;
                    context.Messages.Add(partialMessage);
                    addedPartial = true;
                    await emit(new AgentEvent.MessageStart(partialMessage)).ConfigureAwait(false);
                    break;

                case AssistantMessageEvent.Error error:
                {
                    var finalMessage = error.Message;
                    if (addedPartial)
                        context.Messages[^1] = finalMessage;
                    else
                        context.Messages.Add(finalMessage);
                    if (!addedPartial)
                        await emit(new AgentEvent.MessageStart(finalMessage)).ConfigureAwait(false);
                    await emit(new AgentEvent.MessageEnd(finalMessage)).ConfigureAwait(false);
                    return finalMessage;
                }

                case AssistantMessageEvent.Done done:
                {
                    var finalMessage = done.Message;
                    if (addedPartial)
                        context.Messages[^1] = finalMessage;
                    else
                        context.Messages.Add(finalMessage);
                    if (!addedPartial)
                        await emit(new AgentEvent.MessageStart(finalMessage)).ConfigureAwait(false);
                    await emit(new AgentEvent.MessageEnd(finalMessage)).ConfigureAwait(false);
                    return finalMessage;
                }

                default:
                    var snapshot = GetPartial(@event);
                    if (partialMessage is not null && snapshot is not null)
                    {
                        partialMessage = snapshot;
                        context.Messages[^1] = partialMessage;
                        await emit(new AgentEvent.MessageUpdate(@event, partialMessage)).ConfigureAwait(false);
                    }
                    break;
            }
        }

        // 流意外结束（无终态事件）：以当前快照收尾。
        var fallback = partialMessage ?? new AssistantMessage([], StopReason.Error, ErrorMessage: "Stream ended without a terminal event");
        if (addedPartial)
            context.Messages[^1] = fallback;
        else
            context.Messages.Add(fallback);
        await emit(new AgentEvent.MessageEnd(fallback)).ConfigureAwait(false);
        return fallback;
    }
}
