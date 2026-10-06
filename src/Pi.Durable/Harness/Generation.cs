using Pi.Ai.Models;
using Pi.Durable.Env;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;
using Path = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>pi.generation 任务的检查点（JSON 字典承载，与 TS 形状一致）。</summary>
internal abstract record GenerationCheckpoint
{
    public abstract JsonDict ToJson();

    public static GenerationCheckpoint FromJson(JsonDict json) => json["phase"] switch
    {
        "prepare" => new Prepare(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted) && compacted is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted))
                : null,
            json.TryGetValue("overflow", out var overflow) ? overflow as string : null),
        "request" => new Request(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted2) && compacted2 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted2))
                : null,
            new ModelRef((string)((JsonDict)json["model"]!)["provider"]!, (string)((JsonDict)json["model"]!)["modelId"]!),
            ThinkingLevelOf((string)json["thinkingLevel"]!),
            json.TryGetValue("streamOptions", out var stream) && stream is not null
                ? JsonTrees.Deserialize<ConversationStreamOptions>(stream)
                : new ConversationStreamOptions(),
            EntryId.From(Convert.ToInt64(json["cutoff"]!))),
        "retry" => new RetryPhase(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted3) && compacted3 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted3))
                : null,
            Convert.ToInt64(json["until"]!)),
        "poll" => new Poll(
            (int)Convert.ToInt64(json["attempt"]!),
            json.TryGetValue("compacted", out var compacted4) && compacted4 is not null
                ? TaskId<object?>.From(Convert.ToInt64(compacted4))
                : null,
            new ModelRef((string)((JsonDict)json["model"]!)["provider"]!, (string)((JsonDict)json["model"]!)["modelId"]!),
            EntryId.From(Convert.ToInt64(json["cutoff"]!)),
            JsonTrees.Deserialize<DeferredHandle>(json["handle"]!),
            Convert.ToInt64(json["pollAt"]!)),
        "tools" => new Tools(
            EntryId.From(Convert.ToInt64(json["assistant"]!)),
            json.TryGetValue("tools", out var tools) && tools is IReadOnlyList<object?> toolList
                ? toolList.Select(value => TaskId<object?>.From(Convert.ToInt64(value))).ToList()
                : [],
            json.TryGetValue("pending", out var pending) && pending is IReadOnlyList<object?> pendingList
                ? pendingList.OfType<string>().ToList()
                : []),
        var other => throw new InvalidOperationException($"Unknown pi.generation checkpoint phase {other}"),
    };

    protected static JsonDict ModelRefJson(ModelRef model)
        => new Dictionary<string, object?> { ["provider"] = model.Provider, ["modelId"] = model.ModelId };

    protected static string ThinkingLevelJson(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static ThinkingLevel ThinkingLevelOf(string level) => level switch
    {
        "off" => ThinkingLevel.Off,
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "xhigh" => ThinkingLevel.XHigh,
        "max" => ThinkingLevel.Max,
        var other => throw new InvalidOperationException($"Unknown thinking level {other}"),
    };

    public sealed record Prepare(int Attempt, TaskId<object?>? Compacted, string? Overflow) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?> { ["phase"] = "prepare", ["attempt"] = (long)Attempt };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            if (Overflow is not null) json["overflow"] = Overflow;
            return json;
        }
    }

    public sealed record Request(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, ThinkingLevel ThinkingLevel,
        ConversationStreamOptions StreamOptions, EntryId Cutoff) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "request",
                ["attempt"] = (long)Attempt,
                ["model"] = ModelRefJson(Model),
                ["thinkingLevel"] = ThinkingLevelJson(ThinkingLevel),
                ["streamOptions"] = JsonTrees.ToTree(StreamOptions),
                ["cutoff"] = Cutoff.Value,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            return json;
        }
    }

    public sealed record RetryPhase(int Attempt, TaskId<object?>? Compacted, long Until) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "retry", ["attempt"] = (long)Attempt, ["until"] = Until,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            return json;
        }
    }

    public sealed record Poll(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, EntryId Cutoff,
        DeferredHandle Handle, long PollAt) : GenerationCheckpoint
    {
        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>
            {
                ["phase"] = "poll",
                ["attempt"] = (long)Attempt,
                ["model"] = ModelRefJson(Model),
                ["cutoff"] = Cutoff.Value,
                ["handle"] = JsonTrees.ToTree(Handle),
                ["pollAt"] = PollAt,
            };
            if (Compacted is { } compacted) json["compacted"] = compacted.Value;
            return json;
        }
    }

    /// <summary>等待本轮的工具任务；生成拥有它们（spec §8.5）。</summary>
    public sealed record Tools(EntryId Assistant, IReadOnlyList<TaskId<object?>> ToolsIds, IReadOnlyList<string> Pending)
        : GenerationCheckpoint
    {
        public override JsonDict ToJson() => new Dictionary<string, object?>
        {
            ["phase"] = "tools",
            ["assistant"] = Assistant.Value,
            ["tools"] = ToolsIds.Select(id => (object?)id.Value).ToList(),
            ["pending"] = Pending.ToList(),
        };
    }
}

/// <summary>
/// 内建生成任务：准备位置系统提示与工具负载，请求或轮询模型，重试，并分类响应。
/// 运行的输入在 <c>pi.live.run</c>。对应 TS <c>harness/generation.ts</c> 的 <c>GenerationTask</c> 全量。
/// </summary>
public static partial class Generation
{
    public const string TaskName = "pi.generation";

    private const long DefaultPollAfterMs = 5000;

    /// <summary>擦除形状的内建任务定义（输入 / checkpoint / 结果均为 JSON 字典）。</summary>
    public static readonly DurableTask<JsonDict, JsonDict, JsonDict> Instance = DurableTasks.DefineTask(
        new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = TaskName,
            Version = 1,
            Initial = _ => new GenerationCheckpoint.Prepare(1, null, null).ToJson(),
            Phases = new Dictionary<string, object?>
            {
                ["prepare"] = (TaskPhaseHandler)PreparePhaseAsync,
                ["request"] = (TaskPhaseHandler)RequestPhaseAsync,
                ["retry"] = (TaskPhaseHandler)RetryPhaseAsync,
                ["poll"] = (TaskPhaseHandler)PollPhaseAsync,
                ["tools"] = (TaskPhaseHandler)ToolsPhaseAsync,
            },
            Abort = (TaskPhaseHandler)AbortAsync,
        });

    // ─── 阶段 ───────────────────────────────────────────────────────────────

    /// <summary>渲染系统提示与工具负载并追加所需的位置 <c>pi.system</c> 条目，随后进入 request。</summary>
    private static async Task PreparePhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var conversationId = runtime.ConversationId;
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var settings = runtime.Settings;
        var modelReference = agent.Model;
        var resolved = modelReference is null
            ? null
            : runtime.Models.GetModel(modelReference.Provider, modelReference.ModelId);
        if (modelReference is null || resolved is null)
        {
            await FailNoModelAsync(runtime, modelReference, context).ConfigureAwait(false);
            return;
        }

        var checkpoint = (GenerationCheckpoint.Prepare)ReadCheckpoint(task);
        if (checkpoint.Compacted is { } compacted && checkpoint.Overflow is { } overflow)
        {
            var outcome = (await runtime.OutcomesAsync([compacted], context).ConfigureAwait(false))[0];
            var entryId = outcome is { Status: TaskOutcomeStatus.Completed, Result: JsonDict result }
                && result.TryGetValue("entryId", out var value) && value is not null
                    ? Convert.ToInt64(value)
                    : default(long?);
            if (outcome.Status != TaskOutcomeStatus.Completed || entryId is null)
            {
                await FailModelErrorAsync(runtime, overflow, context).ConfigureAwait(false);
                return;
            }
        }

        var view = await runtime.ContextAsync(conversationId, context).ConfigureAwait(false);
        var shown = Prompt.ReplaySections(view.Messages);
        var report = new Action<Exception>(error => runtime.Report(error));
        IExecutionEnv? env = null;
        try
        {
            env = await runtime.EnvAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (context.AbortSignal is { IsCancellationRequested: true }) throw;
            report(error);
        }

        var input = new PromptInput
        {
            ConversationId = conversationId,
            Agent = agent,
            Env = env,
            Shown = shown,
            Read = runtime,
        };
        var desired = await Prompt.RenderSections(agent.Sections, input, shown, report, context).ConfigureAwait(false);
        var declarations = agent.Tools.Select(Prompt.DeclarationOf).ToList();
        var entries = Prompt.PlanSystemEntries(view, desired, declarations, runtime.Now());
        var threshold = checkpoint.Compacted is null
            ? ThresholdCompaction(view, entries, resolved.ContextWindow, settings.Compaction)
            : null;
        if (threshold == "blocking")
        {
            // 先压缩再重新准备；transcript 在压缩追加之前不变。
            await runtime.CommitAsync(async (tx, _) =>
            {
                var child = await Compaction.CreateCompactionAsync(
                    tx, conversationId, CompactionReason.Threshold, runtime.TaskId).ConfigureAwait(false);
                return new TaskState
                {
                    Status = TaskStatus.Waiting,
                    Checkpoint = new GenerationCheckpoint.Prepare(checkpoint.Attempt, child, null).ToJson(),
                    On = [child],
                    Policy = JoinPolicy.AllSettled,
                };
            }, context).ConfigureAwait(false);
            return;
        }

        await runtime.CommitAsync(async (tx, _) =>
        {
            EntryId? cutoff = (await tx.ScanEntriesAsync(
                new EntryQuery { ConversationId = conversationId }, 1).ConfigureAwait(false))
                .Items.FirstOrDefault()?.Id;
            foreach (var entry in entries)
            {
                cutoff = (await tx.AppendEntryAsync(
                    DurableEntries.SystemEntry, conversationId,
                    new TypedEntryDraft<object?> { Model = entry.Model, Edits = entry.Edits }).ConfigureAwait(false)).Id;
            }

            if (cutoff is null)
            {
                throw new InvalidOperationException($"Conversation {conversationId.Value} has no entries to send");
            }

            // 在本提交里检查，使准备期间受理的压缩也计入。
            if (threshold == "background")
            {
                var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                if (!live.Draft.ContainsKey("compactions"))
                {
                    await Compaction.CreateCompactionAsync(tx, conversationId, CompactionReason.Threshold)
                        .ConfigureAwait(false);
                }
            }

            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new GenerationCheckpoint.Request(
                    checkpoint.Attempt, checkpoint.Compacted, modelReference, agent.ThinkingLevel,
                    settings.Stream, cutoff.Value).ToJson(),
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task RequestPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Request)ReadCheckpoint(task);
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            Live.SetGeneration(live, new Dictionary<string, object?> { ["attempt"] = (long)checkpoint.Attempt });
            return null;
        }, context).ConfigureAwait(false);
        var model = runtime.Models.GetModel(checkpoint.Model.Provider, checkpoint.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, checkpoint.Model, context).ConfigureAwait(false);
            return;
        }

        var view = await runtime.ContextAsync(conversationId, context, checkpoint.Cutoff).ConfigureAwait(false);
        var messages = view.Messages;
        await runtime.Hooks.EachAsync("beforeRequest", async handler =>
        {
            if (handler is not IGenerationHooks hooks) return;
            var replaced = await hooks.BeforeRequest(messages, runtime, context).ConfigureAwait(false);
            if (replaced is { Used: true, Messages: { } replacedMessages }) messages = replacedMessages;
        }).ConfigureAwait(false);
        var options = await Provider.RequestOptionsAsync(
            runtime, checkpoint.StreamOptions, checkpoint.ThinkingLevel).ConfigureAwait(false);
        var message = await StreamResponseAsync(runtime, model, messages, options, checkpoint.Attempt, context)
            .ConfigureAwait(false);
        var request = new RequestInfo(
            checkpoint.Attempt, checkpoint.Compacted, checkpoint.Model, checkpoint.Cutoff, view.Messages);
        await ClassifyAsync(runtime, request, message, context).ConfigureAwait(false);
    }

    private static async Task RetryPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.RetryPhase)ReadCheckpoint(task);
        await runtime.SleepAsync(checkpoint.Until, context).ConfigureAwait(false);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.SetGeneration(live, new Dictionary<string, object?> { ["attempt"] = (long)(checkpoint.Attempt + 1) });
            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new GenerationCheckpoint.Prepare(checkpoint.Attempt + 1, checkpoint.Compacted, null).ToJson(),
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task PollPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Poll)ReadCheckpoint(task);
        var model = runtime.Models.GetModel(checkpoint.Model.Provider, checkpoint.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, checkpoint.Model, context).ConfigureAwait(false);
            return;
        }

        await runtime.SleepAsync(checkpoint.PollAt, context).ConfigureAwait(false);
        var message = await runtime.Models.FetchDeferredAsync(model, checkpoint.Handle, null, runtime.Signal)
            .ConfigureAwait(false);
        var request = new RequestInfo(
            checkpoint.Attempt, checkpoint.Compacted, checkpoint.Model, checkpoint.Cutoff, null, checkpoint.PollAt);
        await ClassifyAsync(runtime, request, message, context).ConfigureAwait(false);
    }

    private static async Task ToolsPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = (GenerationCheckpoint.Tools)ReadCheckpoint(task);
        var next = checkpoint.Pending.FirstOrDefault();
        if (next is null)
        {
            await FinishToolRoundAsync(runtime, checkpoint.Assistant, checkpoint.ToolsIds, context).ConfigureAwait(false);
            return;
        }

        // 顺序轮：启动下一个调用并等待它。
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var taskId = await CreateToolTaskAsync(tx, runtime, checkpoint.Assistant, next).ConfigureAwait(false);
            var slot = Live.ToolSlotsOf(live).FirstOrDefault(
                candidate => candidate.CallId == next && candidate.TaskId is null);
            if (slot is not null) Live.ReplaceSlot(live, slot with { TaskId = taskId });
            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new GenerationCheckpoint.Tools(
                    checkpoint.Assistant,
                    [.. checkpoint.ToolsIds, taskId],
                    checkpoint.Pending.Skip(1).ToList()).ToJson(),
                On = [taskId],
                Policy = JoinPolicy.AllSettled,
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task AbortAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = ReadCheckpoint(task);
        if (checkpoint is GenerationCheckpoint.Poll poll)
        {
            var model = runtime.Models.GetModel(poll.Model.Provider, poll.Model.ModelId);
            if (model is not null)
            {
                try
                {
                    await runtime.Models.CancelDeferredAsync(model, poll.Handle, null, runtime.Signal)
                        .ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    runtime.Report(error);
                }
            }
        }

        var conversationId = runtime.ConversationId;
        // 在本轮的工具任务都终态之后运行；从未启动的调用得到 aborted 结果（spec §8.5）。
        var unstarted = checkpoint is GenerationCheckpoint.Tools tools
            ? await ReadCallsAsync(runtime, tools.Assistant, tools.Pending, context).ConfigureAwait(false)
            : Array.Empty<ToolCallContent>();
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await ConvertPartialAsync(tx, live, conversationId).ConfigureAwait(false);
            foreach (var call in unstarted)
            {
                var result = ToolTask.HarnessError("aborted", $"Tool {call.Name} was aborted");
                await ToolTask.AppendToolResultAsync(tx, conversationId, call, result, runtime.Now())
                    .ConfigureAwait(false);
            }

            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("aborted"));
            return new TaskState
            {
                Status = TaskStatus.Terminal, Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 分类与响应 ─────────────────────────────────────────────────────────

    private readonly record struct RequestInfo(
        int Attempt, TaskId<object?>? Compacted, ModelRef Model, EntryId Cutoff,
        IReadOnlyList<ChatMessage>? Messages, long? PollAt = null);

    /// <summary>assistant 条目中 <paramref name="callIds"/> 的调用，按给定顺序。对应 TS <c>readCalls()</c>。</summary>
    private static async Task<IReadOnlyList<ToolCallContent>> ReadCallsAsync(
        ITaskRuntime runtime, EntryId assistant, IReadOnlyList<string> callIds, Context context)
    {
        var entry = await runtime.EntryAsync(DurableEntries.AssistantEntry, assistant, context).ConfigureAwait(false);
        var message = entry is { Model.Count: > 0 } typed ? typed.Model[0] : null;
        var calls = message is AssistantMessage assistantMessage
            ? assistantMessage.Content.OfType<ToolCallContent>().ToList()
            : [];
        return [.. callIds.SelectMany(id => calls.Where(call => call.Id == id).Take(1))];
    }

    /// <summary>调用 <paramref name="callId"/> 的工具任务，由生成拥有。对应 TS <c>createToolTask()</c>。</summary>
    private static async Task<TaskId<object?>> CreateToolTaskAsync(
        Transaction tx, ITaskRuntime runtime, EntryId assistant, string callId)
    {
        var taskId = await tx.CreateTaskAsync(
            ToolTask.Instance,
            new Dictionary<string, object?> { ["assistant"] = assistant.Value, ["callId"] = callId },
            new TaskOptions
            {
                Ownership = new TaskOwnership.TaskOwner(runtime.TaskId),
                ConversationId = runtime.ConversationId,
            }).ConfigureAwait(false);
        return TaskId<object?>.From(taskId.Value);
    }

    /// <summary>
    /// 准备在请求前启动哪种阈值压缩（spec §8.3）：超过 <c>contextWindow - reserveTokens</c> 为 blocking，
    /// 超过后台阈值为 background，且仅当范围选择找到切点。调用方只在没有列出压缩时才启动后台压缩。
    /// 对应 TS <c>thresholdCompaction()</c>。
    /// </summary>
    internal static string? ThresholdCompaction(
        ContextView view, IReadOnlyList<TypedEntryDraft<object?>> planned, long contextWindow, CompactionPolicy policy)
    {
        if (!policy.Enabled || contextWindow <= 0) return null;
        var tokens = Compaction.EstimateContext(
            view, planned.SelectMany(entry => entry.Model ?? []).ToList());
        var blocking = contextWindow - policy.ReserveTokens;
        var background = blocking - policy.BackgroundTokens;
        var over = tokens > blocking ? "blocking"
            : policy.BackgroundTokens > 0 && tokens > background ? "background"
            : null;
        if (over is null || Compaction.SelectCut(view, policy.KeepRecentTokens) is null) return null;
        return over;
    }

    /// <summary>把运行的输入以 <c>model_error</c> 结算为 unanswered 并以 <paramref name="text"/> 失败。</summary>
    private static async Task FailModelErrorAsync(ITaskRuntime runtime, string text, Context context)
    {
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("model_error", text));
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = text, Detail = new Dictionary<string, object?> { ["reason"] = "model_error" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>把运行的输入以 <c>no_model</c> 结算为 unanswered 并失败。</summary>
    private static async Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, Context context)
    {
        var message = reference is null
            ? "No model is configured"
            : $"Model {reference.Provider}/{reference.ModelId} is not available";
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("no_model"));
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = message, Detail = new Dictionary<string, object?> { ["reason"] = "no_model" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 把被打断、中止、出错或孤立的尝试留下的已提交 partial 追加为 aborted 的 assistant 条目；
    /// 调用方替换或移除 <c>generation</c>。对应 TS <c>convertPartial()</c>。
    /// </summary>
    internal static async Task ConvertPartialAsync(Transaction tx, TxDocChange live, ConversationId conversationId)
    {
        var generation = Live.GenerationOf(live);
        if (generation is null || !generation.TryGetValue("message", out var partial) || partial is null) return;
        var message = JsonTrees.Deserialize<AssistantMessage>(partial) with { StopReason = StopReason.Aborted };
        await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
    }

    /// <summary>追加 provider 结果并在同一提交里把它的用量记入 <c>pi.usage</c>。</summary>
    internal static async Task<TypedEntry<object?>> AppendAssistantAsync(
        Transaction tx, ConversationId conversationId, AssistantMessage message)
    {
        await Usage.RecordUsageAsync(
            tx, conversationId, "models", $"{message.Provider}/{message.Model}", message.UsageStats ?? new Pi.Ai.Types.Usage(0, 0))
            .ConfigureAwait(false);
        return await tx.AppendEntryAsync(
            DurableEntries.AssistantEntry, conversationId,
            new TypedEntryDraft<object?> { Model = [message] }).ConfigureAwait(false);
    }

    /// <summary>单个流事件的 partial 快照（Done / Error 没有）。</summary>
    private static AssistantMessage? PartialOf(AssistantMessageEvent evt) => evt switch
    {
        AssistantMessageEvent.Start e => e.Partial,
        AssistantMessageEvent.TextStart e => e.Partial,
        AssistantMessageEvent.TextDelta e => e.Partial,
        AssistantMessageEvent.TextEnd e => e.Partial,
        AssistantMessageEvent.ThinkingStart e => e.Partial,
        AssistantMessageEvent.ThinkingDelta e => e.Partial,
        AssistantMessageEvent.ThinkingEnd e => e.Partial,
        AssistantMessageEvent.ToolCallStart e => e.Partial,
        AssistantMessageEvent.ToolCallDelta e => e.Partial,
        AssistantMessageEvent.ToolCallEnd e => e.Partial,
        _ => null,
    };

    /// <summary>
    /// 流式完成一次请求并返回终态消息。partial 以至多每 <c>progress.partialIntervalMs</c>（缺省 100ms）一次
    /// 尾随提交的方式落盘（同一时刻至多一个提交在途）；finally 停止节流并等待在途提交，
    /// 因此不会有过期 partial 落在结果之后。对应 TS <c>streamResponse()</c>。
    /// </summary>
    private static async Task<AssistantMessage> StreamResponseAsync(
        ITaskRuntime runtime, ModelSpec model, IReadOnlyList<ChatMessage> messages,
        Dictionary<string, object?> options, int attempt, Context context)
    {
        var throttle = new PartialThrottle(runtime, attempt, context);
        var stream = runtime.Models.StreamSimple(model, messages, options);
        try
        {
            await foreach (var evt in stream.WithCancellation(runtime.Signal).ConfigureAwait(false))
            {
                // 没有内容的 partial（如 pi-ai 的开流 start 事件）什么也不显示；deferred 响应不会
                // 越过它，因此不会留下 partial。
                var partial = PartialOf(evt);
                if (evt is AssistantMessageEvent.Done or AssistantMessageEvent.Error
                    || partial is not { Content.Count: > 0 })
                {
                    continue;
                }

                throttle.Post(partial);
            }

            return await stream.WaitForDoneAsync(runtime.Signal).ConfigureAwait(false);
        }
        finally
        {
            await throttle.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>partial 的节流提交器（TS streamResponse 闭包状态的等价物）。</summary>
    private sealed class PartialThrottle(ITaskRuntime runtime, int attempt, Context context)
    {
        private readonly object _gate = new();
        private AssistantMessage? _pending;
        private Task? _inFlight;
        private bool _timerScheduled;
        private bool _stopped;

        public void Post(AssistantMessage partial)
        {
            lock (_gate)
            {
                _pending = partial;
                if (!_timerScheduled && _inFlight is null && !_stopped)
                {
                    _timerScheduled = true;
                    _ = TimerLoop();
                }
            }
        }

        private async Task TimerLoop()
        {
            var interval = runtime.Settings.Progress.PartialIntervalMs;
            try
            {
                await Task.Delay(interval).ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // 已停止；忽略。
            }
        }

        private async Task FlushAsync()
        {
            AssistantMessage? partial;
            lock (_gate)
            {
                _timerScheduled = false;
                partial = _pending;
                _pending = null;
                if (partial is null || _stopped) return;
            }

            // 同步拷贝：provider 持续修改其 partial。
            var message = JsonTrees.Deserialize<AssistantMessage>(JsonTrees.ToTree(partial));
            var commit = runtime.CommitAsync(async (tx, _) =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                var generation = Live.GenerationOf(live);
                if (generation is null)
                {
                    Live.SetGeneration(live, new Dictionary<string, object?>
                    {
                        ["attempt"] = (long)attempt,
                        ["message"] = JsonTrees.ToTree(message),
                    });
                }
                else
                {
                    // TS 逐叶 assignJson("message", …) 的整值等价：message 只由本生成写入。
                    live.Set(
                        Path.Root.Append(Seg.Key("generation")).Append(Seg.Key("message")),
                        JsonTrees.ToTree(message));
                }

                return null;
            }, context);
            lock (_gate) _inFlight = commit;
            try
            {
                await commit.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 中止标记或关闭之后的拒绝是预期的；已提交状态保持一致。
                if (!runtime.Signal.IsCancellationRequested) runtime.Report(error);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight = null;
                    if (_pending is not null && !_stopped && !_timerScheduled)
                    {
                        _timerScheduled = true;
                        _ = TimerLoop();
                    }
                }
            }
        }

        /// <summary>停止节流并等待在途提交；其后不再有 partial 落盘。</summary>
        public async Task StopAsync()
        {
            Task? inFlight;
            lock (_gate)
            {
                _stopped = true;
                inFlight = _inFlight;
            }

            if (inFlight is not null) await inFlight.ConfigureAwait(false);
        }
    }

    /// <summary>在也清除 partial 的同一提交里分类一个终态 provider 消息。对应 TS <c>classify()</c>。</summary>
    private static async Task ClassifyAsync(
        ITaskRuntime runtime, RequestInfo request, AssistantMessage message, Context context)
    {
        // 中止标记或关闭：中止调用或重开的运行处理已提交状态。
        runtime.Signal.ThrowIfCancellationRequested();
        var conversationId = runtime.ConversationId;
        if (message.StopReason == StopReason.Deferred && message.Deferred is { } handle)
        {
            var pollAt = Math.Max(
                runtime.Now() + (handle.PollAfterMs ?? DefaultPollAfterMs),
                request.PollAt is { } previous ? previous + 1 : long.MinValue);
            await runtime.CommitAsync(async (tx, _) =>
            {
                var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)request.Attempt,
                    ["deferred"] = new Dictionary<string, object?> { ["pollAt"] = pollAt },
                });
                return new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = new GenerationCheckpoint.Poll(
                        request.Attempt, request.Compacted, request.Model, request.Cutoff, handle, pollAt).ToJson(),
                };
            }, context).ConfigureAwait(false);
            return;
        }

        await runtime.Hooks.EachAsync("afterResponse", async handler =>
        {
            if (handler is IGenerationHooks hooks)
            {
                await hooks.AfterResponse(message, runtime, context).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        var calls = message.Content.OfType<ToolCallContent>().ToList();
        if (message.StopReason == StopReason.ToolUse && calls.Count > 0)
        {
            await StartToolRoundAsync(runtime, request, message, calls, context).ConfigureAwait(false);
            return;
        }

        if (message.StopReason is StopReason.Stop or StopReason.Length or StopReason.ToolUse)
        {
            await AnswerAsync(runtime, message, context).ConfigureAwait(false);
            return;
        }

        // 重试与压缩策略决定下一次尝试，因此现在读取而非在准备时固定。
        var settings = runtime.Settings;
        var overflow = message.StopReason == StopReason.Error && Overflow.IsContextOverflow(message);
        if (overflow && request.Compacted is null && settings.Compaction.Enabled)
        {
            var policy = settings.Compaction;
            var view = await runtime.ContextAsync(conversationId, context, request.Cutoff).ConfigureAwait(false);
            if (Compaction.SelectCut(view, policy.KeepRecentTokens) is not null)
            {
                var text = message.ErrorMessage ?? "Context overflow";
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
                    await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
                    Live.DeleteGeneration(live);
                    var child = await Compaction.CreateCompactionAsync(
                        tx, conversationId, CompactionReason.Overflow, runtime.TaskId).ConfigureAwait(false);
                    return new TaskState
                    {
                        Status = TaskStatus.Waiting,
                        Checkpoint = new GenerationCheckpoint.Prepare(request.Attempt, child, text).ToJson(),
                        On = [child],
                        Policy = JoinPolicy.AllSettled,
                    };
                }, context).ConfigureAwait(false);
                return;
            }
        }

        var retryPolicy = settings.Retry;
        // 溢出永不重试：只有压缩能让下一次请求放得下。
        var retry = message.StopReason == StopReason.Error
            && !overflow
            && Retry.IsRetryableAssistantError(message)
            && retryPolicy.Enabled
            && request.Attempt <= retryPolicy.MaxRetries;
        var until = retry
            ? runtime.Now() + Retry.RetryDelayMs(Compaction.ToPiRetryPolicy(retryPolicy), request.Attempt)
            : 0L;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            if (retry)
            {
                Live.SetGeneration(live, new Dictionary<string, object?>
                {
                    ["attempt"] = (long)request.Attempt,
                    ["retry"] = new Dictionary<string, object?>
                    {
                        ["at"] = until, ["error"] = message.ErrorMessage ?? "",
                    },
                });
                return new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = new GenerationCheckpoint.RetryPhase(request.Attempt, request.Compacted, until).ToJson(),
                };
            }

            var failure = message.ErrorMessage
                ?? $"Model response ended with stop reason {message.StopReason.ToString().ToLowerInvariant()}";
            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("model_error", failure));
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = failure, Detail = new Dictionary<string, object?> { ["reason"] = "model_error" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 最终答案；final 边界放置排队项（spec §6）。首个 onYield 续延追加用户消息并把运行交给后继生成——
    /// 仅当边界没有选中用户项且没有重置。否则运行的输入结算 done，被选中的用户项启动下一个运行。
    /// 对应 TS <c>answer()</c>。
    /// </summary>
    private static async Task AnswerAsync(ITaskRuntime runtime, AssistantMessage message, Context context)
    {
        IReadOnlyList<ContentBlock>? continuation = null;
        await runtime.Hooks.EachAsync("onYield", async handler =>
        {
            if (continuation is not null || handler is not IGenerationHooks hooks) return;
            var decision = await hooks.OnYield(message, runtime, context).ConfigureAwait(false);
            if (decision is { ContinueRun: true, Continue: { Count: > 0 } decided }) continuation = decided;
        }).ConfigureAwait(false);
        var conversationId = runtime.ConversationId;
        await runtime.CommitAsync(async (tx, _) =>
        {
            // 队列模式在 Session 线上读取，即边界决定之时。
            var settings = runtime.Settings;
            var boundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var result = new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = entry.Id.Value },
                },
            };
            var applied = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, runtime.Now())
                .ConfigureAwait(false);
            if (continuation is not null && applied.Users.Count == 0 && !applied.Reset)
            {
                await tx.AppendEntryAsync(
                    DurableEntries.UserEntry, conversationId,
                    new TypedEntryDraft<object?> { Model = [new UserMessage(continuation, runtime.Now())] })
                    .ConfigureAwait(false);
                var successor = await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false);
                Live.HandOver(live, runtime.TaskId, successor);
                Live.DeleteGeneration(live);
                return result;
            }

            Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Done(entry.Id));
            if (applied.Users.Count > 0)
            {
                await StartRunAsync(tx, conversationId, live, applied.Users).ConfigureAwait(false);
            }

            return result;
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 在一个提交里追加 tool-calling 答案并启动其工具轮（spec §8.3）。对请求未提供的工具的调用在此得到
    /// tool_unavailable 结果；其他每个调用得到生成拥有的工具任务——顺序轮只有第一个现在启动。生成随后
    /// 在其 tools 阶段等待它们，持有运行。对应 TS <c>startToolRound()</c>。
    /// </summary>
    private static async Task StartToolRoundAsync(
        ITaskRuntime runtime, RequestInfo request, AssistantMessage message,
        IReadOnlyList<ToolCallContent> calls, Context context)
    {
        var conversationId = runtime.ConversationId;
        var messages = request.Messages
            ?? (await runtime.ContextAsync(conversationId, context, request.Cutoff).ConfigureAwait(false)).Messages;
        var offered = Transcript.GetCurrentTools(messages)
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        // 轮启动时读取；工具在其工具任务解析它时被解析。
        var agentTools = (await runtime.AgentAsync(context).ConfigureAwait(false)).Tools;
        var sequential = runtime.Settings.ToolExecution == ToolExecutionMode.Sequential
            || calls.Any(call => offered.Contains(call.Name)
                && agentTools.FirstOrDefault(tool => tool.Name == call.Name)?.ExecutionMode
                    == ToolExecutionMode.Sequential);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var entry = await AppendAssistantAsync(tx, conversationId, message).ConfigureAwait(false);
            var slots = new List<ToolSlot>();
            var toolTasks = new List<TaskId<object?>>();
            var pending = new List<string>();
            foreach (var call in calls)
            {
                if (!offered.Contains(call.Name))
                {
                    var unavailable = ToolTask.HarnessError("tool_unavailable", $"Tool {call.Name} is not available");
                    var result = await ToolTask.AppendToolResultAsync(
                        tx, conversationId, call, unavailable, runtime.Now()).ConfigureAwait(false);
                    slots.Add(new ToolSlot { CallId = call.Id, Name = call.Name, Status = "done", Entry = result.Id });
                    continue;
                }

                if (sequential && toolTasks.Count > 0)
                {
                    pending.Add(call.Id);
                    slots.Add(new ToolSlot { CallId = call.Id, Name = call.Name, Status = "pending" });
                    continue;
                }

                var taskId = await CreateToolTaskAsync(tx, runtime, entry.Id, call.Id).ConfigureAwait(false);
                toolTasks.Add(taskId);
                slots.Add(new ToolSlot
                {
                    CallId = call.Id, Name = call.Name, TaskId = taskId, Status = "pending",
                });
            }

            Live.DeleteGeneration(live);
            Live.SetTools(live, slots);
            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new GenerationCheckpoint.Tools(entry.Id, toolTasks, pending).ToJson(),
                On = toolTasks,
                Policy = JoinPolicy.AllSettled,
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 本轮的工具都终态了：应用它们的控制，并在 final 边界（terminate、handoff 或排队重置）结束运行，
    /// 或在 postTools 边界把它交给下一个生成（spec §8.5）。对应 TS <c>finishToolRound()</c>。
    /// </summary>
    private static async Task FinishToolRoundAsync(
        ITaskRuntime runtime, EntryId assistant, IReadOnlyList<TaskId<object?>> tools, Context context)
    {
        var conversationId = runtime.ConversationId;
        var outcomes = await runtime.OutcomesAsync(tools, context).ConfigureAwait(false);
        var controls = new List<ToolControl?>();
        var controlByTask = new Dictionary<TaskId<object?>, ToolControl?>();
        for (var index = 0; index < tools.Count; index++)
        {
            var outcome = outcomes[index];
            ToolControl? control = null;
            if (outcome is { Status: TaskOutcomeStatus.Completed, Result: JsonDict result })
            {
                control = ToolTaskResult.FromJson(result).Control;
            }

            controls.Add(control);
            controlByTask[tools[index]] = control;
        }

        var snapshot = await runtime.SnapshotAsync(Live.LiveDoc, conversationId, context).ConfigureAwait(false);
        var slots = snapshot is not null
            && snapshot.TryGetValue("tools", out var toolsValue)
            && toolsValue is IReadOnlyList<object?> list
            ? list.OfType<JsonDict>().Select(ToolSlot.FromJson).ToList()
            : [];
        var results = slots.Where(slot => slot.Entry is not null).Select(slot => slot.Entry!.Value).ToList();
        await runtime.Hooks.EachAsync("afterTools", async handler =>
        {
            if (handler is IGenerationHooks hooks)
            {
                await hooks.AfterTools(assistant, results, runtime, context).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        // 轮的每个调用，包括没有任务就得到回答的调用，都必须请求终止。
        var terminate = slots.Count > 0 && slots.All(slot => slot.TaskId is { } slotTask
            && controlByTask.TryGetValue(slotTask, out var control) && control?.Terminate == true);
        var added = controls.Where(control => control?.AddTools is not null)
            .SelectMany(control => control!.AddTools!).ToList();
        // 调用顺序的最后一个 handoff 胜出。
        var handoff = controls.LastOrDefault(control => control?.Handoff is not null)?.Handoff;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var settings = runtime.Settings;
            var boundary = await Inbox.PrepareBoundaryAsync(
                tx, conversationId, settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            if (added.Count > 0)
            {
                await AgentDocs.AddToolsAsync(tx, conversationId, added).ConfigureAwait(false);
            }

            var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
            var now = runtime.Now();
            if (terminate || handoff is not null)
            {
                if (handoff is not null)
                {
                    var resetEntry = await tx.AppendEntryAsync(
                        DurableEntries.ResetEntry, conversationId,
                        new TypedEntryDraft<object?>
                        {
                            HeadIsSelf = true, Model = [new UserMessage([new TextContent(handoff)], now)],
                        }).ConfigureAwait(false);
                    boundary.Head = resetEntry.Id;
                }

                var final = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.Final, now).ConfigureAwait(false);
                Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Done(assistant));
                if (final.Users.Count > 0)
                {
                    await StartRunAsync(tx, conversationId, live, final.Users).ConfigureAwait(false);
                }
            }
            else
            {
                var postTools = await Inbox.ApplyBoundaryAsync(tx, boundary, BoundaryAt.PostTools, now)
                    .ConfigureAwait(false);
                if (postTools.Reset)
                {
                    // 排队的重置在答案之前切掉了运行的上下文。
                    Live.EndRun(tx, live, runtime.TaskId, new SubmissionSettlement.Unanswered("reset"));
                    if (postTools.Users.Count > 0)
                    {
                        await StartRunAsync(tx, conversationId, live, postTools.Users).ConfigureAwait(false);
                    }
                }
                else
                {
                    Live.DeleteTools(live);
                    if (Live.RunTaskId(live) == runtime.TaskId)
                    {
                        Live.PushRunInputs(live, postTools.Users);
                    }

                    var successor = await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false);
                    Live.HandOver(live, runtime.TaskId, successor);
                }
            }

            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = assistant.Value },
                },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 运行启动与移交 ─────────────────────────────────────────────────────

    /// <summary>为 <paramref name="inputs"/> 启动运行，放置输入提交：新生成持有 <c>pi.live.run</c>。对应 TS <c>startRun()</c>。</summary>
    public static async Task StartRunAsync(
        Transaction tx, ConversationId conversationId, TxDocChange live, IReadOnlyList<SubmissionId> inputs)
    {
        Live.SetRun(live, await CreateGenerationAsync(tx, conversationId).ConfigureAwait(false), inputs);
    }

    /// <summary>对话拥有的生成。对应 TS <c>createGeneration()</c>。</summary>
    internal static async Task<TaskId<object?>> CreateGenerationAsync(Transaction tx, ConversationId conversationId)
    {
        var taskId = await tx.CreateTaskAsync(
            Instance,
            new Dictionary<string, object?>(),
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conversationId })
            .ConfigureAwait(false);
        return TaskId<object?>.From(taskId.Value);
    }

    // ─── 检查点 ─────────────────────────────────────────────────────────────

    internal static GenerationCheckpoint ReadCheckpoint(TaskRecord task)
        => task.State.Checkpoint is JsonDict json
            ? GenerationCheckpoint.FromJson(json)
            : throw new InvalidOperationException("pi.generation requires a JSON checkpoint");
}
