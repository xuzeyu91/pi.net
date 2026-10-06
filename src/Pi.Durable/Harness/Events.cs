using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 实验性：一个对话的 agent 事件流（spec §9.4），形状对齐 coding agent 的会话事件。
/// 对应 TS <c>harness/events.ts</c>（判别联合在 C# 以 abstract record + 密封嵌套 record 承载）。
/// </summary>
public abstract record AgentEvent
{
    private protected AgentEvent() { }

    /// <summary>wire 判别符（与 TS <c>type</c> 字段一致）。</summary>
    public abstract string Type { get; }

    /// <summary>挂载时的快照事件。对应 TS <c>SnapshotEvent</c>。</summary>
    public sealed record Snapshot : AgentEvent
    {
        public override string Type => "snapshot";

        public required IReadOnlyList<EntryRecord> Entries { get; init; }

        public SnapshotRun? Run { get; init; }

        /// <summary>当前生成尝试：进行中的 partial、重试退避或延迟轮询。</summary>
        public SnapshotGeneration? Generation { get; init; }

        public required IReadOnlyList<ToolSlot> Tools { get; init; }

        /// <summary><c>pi.live.compactions</c>：活跃压缩及其尝试与重试退避。</summary>
        public required IReadOnlyList<CompactionStatus> Compactions { get; init; }

        public required IReadOnlyList<QueuedItem> Inbox { get; init; }

        /// <summary><c>pi.agent</c>；缺失时为 <c>{}</c>。</summary>
        public required IReadOnlyDictionary<string, object?> Agent { get; init; }

        public required IReadOnlyDictionary<string, object?> Usage { get; init; }
    }

    public sealed record SnapshotRun(IReadOnlyList<SubmissionId> Inputs);

    public sealed record SnapshotGeneration
    {
        public required int Attempt { get; init; }

        public AssistantMessage? Message { get; init; }

        public RetryState? Retry { get; init; }

        public DeferredState? Deferred { get; init; }

        public sealed record RetryState(long At, string Error);

        public sealed record DeferredState(long PollAt);
    }

    public sealed record RunStart : AgentEvent
    {
        public override string Type => "run_start";

        public required IReadOnlyList<SubmissionId> Inputs { get; init; }
    }

    public sealed record RunEnd : AgentEvent
    {
        public override string Type => "run_end";

        public required IReadOnlyList<SubmissionId> Inputs { get; init; }
    }

    public sealed record TurnStart : AgentEvent
    {
        public override string Type => "turn_start";
    }

    public sealed record TurnEnd : AgentEvent
    {
        public override string Type => "turn_end";
    }

    public sealed record MessageStart : AgentEvent
    {
        public override string Type => "message_start";

        public required ChatMessage Message { get; init; }
    }

    /// <summary><c>usage</c> 是 partial 的当前用量，如 coding agent 的 JSON 模式。</summary>
    public sealed record MessageUpdate : AgentEvent
    {
        public override string Type => "message_update";

        public required Pi.Ai.Types.Usage Usage { get; init; }

        public required IReadOnlyList<MessageChange> Changes { get; init; }
    }

    public sealed record MessageEnd : AgentEvent
    {
        public override string Type => "message_end";

        public required EntryRecord Entry { get; init; }
    }

    public sealed record ToolExecutionStart : AgentEvent
    {
        public override string Type => "tool_execution_start";

        public required string ToolCallId { get; init; }

        public required string ToolName { get; init; }

        public required IReadOnlyDictionary<string, object?> Args { get; init; }
    }

    public sealed record ToolExecutionUpdate : AgentEvent
    {
        public override string Type => "tool_execution_update";

        public required string ToolCallId { get; init; }

        public required string ToolName { get; init; }

        /// <summary>先裁剪头部再追加保留窗口，或整体替换。</summary>
        public ToolOutputChange? Output { get; init; }

        public object? Details { get; init; }

        public IReadOnlyList<ToolDiagnostic>? Diagnostics { get; init; }
    }

    /// <summary>输出变化：裁剪 + 追加，或整体替换。对应 TS <c>{ trimStart?, append? } | { set }</c>。</summary>
    public sealed record ToolOutputChange
    {
        public long? TrimStart { get; init; }

        public string? Append { get; init; }

        /// <summary>整体替换时为设置值（此时 <see cref="TrimStart"/>/<see cref="Append"/> 无意义）。</summary>
        public string? Set { get; init; }

        public static ToolOutputChange Trim(long trimStart, string? append)
            => new() { TrimStart = trimStart, Append = append };

        public static ToolOutputChange Replace(string set) => new() { Set = set };
    }

    /// <summary><c>entry</c> 在工具任务 faulted 或 orphaned 时缺省。</summary>
    public sealed record ToolExecutionEnd : AgentEvent
    {
        public override string Type => "tool_execution_end";

        public required string ToolCallId { get; init; }

        public required string ToolName { get; init; }

        public EntryRecord? Entry { get; init; }
    }

    public sealed record InboxUpdate : AgentEvent
    {
        public override string Type => "inbox_update";

        public required IReadOnlyList<QueuedItem> Items { get; init; }
    }

    public sealed record Submission : AgentEvent
    {
        public override string Type => "submission";

        public required SubmissionRecord Record { get; init; }
    }

    public sealed record AutoRetryStart : AgentEvent
    {
        public override string Type => "auto_retry_start";

        public required int Attempt { get; init; }

        public required long At { get; init; }

        public required string ErrorMessage { get; init; }
    }

    public sealed record AutoRetryEnd : AgentEvent
    {
        public override string Type => "auto_retry_end";

        public required int Attempt { get; init; }
    }

    public sealed record DeferredPoll : AgentEvent
    {
        public override string Type => "deferred_poll";

        public required long PollAt { get; init; }
    }

    public sealed record EntryAppended : AgentEvent
    {
        public override string Type => "entry_appended";

        public required EntryRecord Entry { get; init; }
    }

    public sealed record AgentChanged : AgentEvent
    {
        public override string Type => "agent_changed";

        public required IReadOnlyDictionary<string, object?> Agent { get; init; }
    }

    public sealed record UsageChanged : AgentEvent
    {
        public override string Type => "usage_changed";

        public required IReadOnlyDictionary<string, object?> Usage { get; init; }
    }

    public sealed record TaskFailed : AgentEvent
    {
        public override string Type => "task_failed";

        public required TaskId<object?> TaskId { get; init; }

        public required string Kind { get; init; }

        public required string Message { get; init; }
    }

    public sealed record CompactionStart : AgentEvent
    {
        public override string Type => "compaction_start";

        public required TaskId<object?> TaskId { get; init; }

        public required CompactionReason Reason { get; init; }

        public required bool Blocking { get; init; }
    }

    /// <summary>任务的回执说明它是否产出了摘要；摘要条目有其自己的事件。</summary>
    public sealed record CompactionEnd : AgentEvent
    {
        public override string Type => "compaction_end";

        public required TaskId<object?> TaskId { get; init; }

        public required CompactionReason Reason { get; init; }
    }
}

/// <summary>队列中的一项（提交 ID 与模式）。对应 TS <c>QueuedItem</c>。</summary>
public sealed record QueuedItem(SubmissionId Id, string Mode);

/// <summary>一条相对进行中助手消息的变更。对应 TS <c>MessageChange</c>。</summary>
public abstract record MessageChange
{
    private protected MessageChange() { }

    public abstract string Type { get; }

    /// <summary>一个内容块开始（text / thinking / toolcall）。</summary>
    public sealed record Start : MessageChange
    {
        public override string Type => Kind;

        /// <summary>"text_start" / "thinking_start" / "toolcall_start"。</summary>
        public required string Kind { get; init; }

        public required int ContentIndex { get; init; }

        public required ContentBlock ContentBlock { get; init; }
    }

    /// <summary>文本或思考的增量。</summary>
    public sealed record Delta : MessageChange
    {
        public override string Type => Kind;

        /// <summary>"text_delta" / "thinking_delta"。</summary>
        public required string Kind { get; init; }

        public required int ContentIndex { get; init; }

        public required string Value { get; init; }
    }

    /// <summary>工具调用参数的增量。</summary>
    public sealed record ToolCallDelta : MessageChange
    {
        public override string Type => "toolcall_delta";

        public required int ContentIndex { get; init; }

        public required IReadOnlyList<object> PathSegments { get; init; }

        public required string Value { get; init; }
    }

    /// <summary>整个内容块（发送前已含其后变更）。</summary>
    public sealed record Block : MessageChange
    {
        public override string Type => "block";

        public required int ContentIndex { get; init; }

        public required ContentBlock Value { get; init; }
    }

    /// <summary>整条消息。</summary>
    public sealed record Whole : MessageChange
    {
        public override string Type => "message";

        public required AssistantMessage Value { get; init; }
    }
}

/// <summary>
/// 一个对话事件批次的序列化流，每提交一批。对应 TS <c>AgentEventStream</c>。
/// </summary>
public sealed class AgentEventStream
{
    private readonly CommittedWatch<IReadOnlyList<AgentEvent>> _watch;

    internal AgentEventStream(CommittedWatch<IReadOnlyList<AgentEvent>> watch, AgentEvent.Snapshot snapshot)
        => (_watch, Snapshot) = (watch, snapshot);

    /// <summary>挂载时的 <c>snapshot</c> 事件。</summary>
    public AgentEvent.Snapshot Snapshot { get; }

    /// <summary>安装监听器（每批一次）。</summary>
    public void Start(Func<IReadOnlyList<AgentEvent>, Context, Task> listener)
        => _watch.Start((events, _, context) => listener(events, context));

    /// <summary>停止并返回终态。</summary>
    public Task<WatchEnd> StopAsync() => _watch.Stop();

    /// <summary>流终止时落定。</summary>
    public Task<WatchEnd> Closed => _watch.Closed;
}

/// <summary>事件适配器（spec §9.4）。对应 TS <c>harness/events.ts</c> 的自由函数。</summary>
public static class Events
{
    private static readonly Path PartialPath = new([Seg.Key("docs"), Seg.Key("pi.live"), Seg.Key("generation"), Seg.Key("message")]);

    /// <summary>视图读到的已类型化部分。对应 TS <c>Parts</c>。</summary>
    private sealed record Parts(
        IReadOnlyDictionary<string, object?> Live,
        IReadOnlyDictionary<string, object?>? Inbox,
        IReadOnlyDictionary<string, object?>? Agent,
        IReadOnlyDictionary<string, object?>? Usage);

    private static Parts PartsOf(ConversationView view)
    {
        var docs = view.Docs;
        return new Parts(
            docs.TryGetValue("pi.live", out var live) ? live : new Dictionary<string, object?>(),
            docs.TryGetValue("pi.inbox", out var inbox) ? inbox : null,
            docs.TryGetValue("pi.agent", out var agent) ? agent : null,
            docs.TryGetValue("pi.usage", out var usage) ? usage : null);
    }

    private static AgentEvent.Snapshot SnapshotOf(ConversationView view)
    {
        var parts = PartsOf(view);
        var live = parts.Live;

        AgentEvent.SnapshotRun? run = null;
        if (live.TryGetValue("run", out var runValue) && runValue is IReadOnlyDictionary<string, object?> runJson
            && runJson.TryGetValue("inputs", out var inputsValue) && inputsValue is IReadOnlyList<object?> inputs)
        {
            run = new AgentEvent.SnapshotRun([.. inputs.Select(input => SubmissionId.From(Convert.ToInt64(input)))]);
        }

        AgentEvent.SnapshotGeneration? generation = null;
        if (live.TryGetValue("generation", out var generationValue)
            && generationValue is IReadOnlyDictionary<string, object?> generationJson)
        {
            generation = GenerationOf(generationJson);
        }

        var tools = ToolSlots(live);
        var compactions = CompactionStatuses(live);

        return new AgentEvent.Snapshot
        {
            Entries = view.Entries,
            Run = run,
            Generation = generation,
            Tools = tools,
            Compactions = compactions,
            Inbox = Queued(parts.Inbox),
            Agent = parts.Agent ?? AgentDocs.InitialAgent(),
            Usage = parts.Usage ?? Usage.InitialUsage(),
        };
    }

    private static AgentEvent.SnapshotGeneration GenerationOf(IReadOnlyDictionary<string, object?> json)
    {
        AgentEvent.SnapshotGeneration.RetryState? retry = null;
        if (json.TryGetValue("retry", out var retryValue) && retryValue is IReadOnlyDictionary<string, object?> retryJson)
        {
            retry = new AgentEvent.SnapshotGeneration.RetryState(
                Convert.ToInt64(retryJson["at"]!),
                retryJson.TryGetValue("error", out var error) ? error as string ?? "" : "");
        }

        AgentEvent.SnapshotGeneration.DeferredState? deferred = null;
        if (json.TryGetValue("deferred", out var deferredValue)
            && deferredValue is IReadOnlyDictionary<string, object?> deferredJson
            && deferredJson.TryGetValue("pollAt", out var pollAt))
        {
            deferred = new AgentEvent.SnapshotGeneration.DeferredState(Convert.ToInt64(pollAt));
        }

        return new AgentEvent.SnapshotGeneration
        {
            Attempt = json.TryGetValue("attempt", out var attempt) ? Convert.ToInt32(attempt) : 0,
            Message = json.TryGetValue("message", out var message)
                ? JsonTrees.ToAssistantMessage(message as IReadOnlyDictionary<string, object?>)
                : null,
            Retry = retry,
            Deferred = deferred,
        };
    }

    private static IReadOnlyList<ToolSlot> ToolSlots(IReadOnlyDictionary<string, object?> live)
        => live.TryGetValue("tools", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(ToolSlot.FromJson)]
            : [];

    private static IReadOnlyList<CompactionStatus> CompactionStatuses(IReadOnlyDictionary<string, object?> live)
        => live.TryGetValue("compactions", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(CompactionStatus.FromJson)]
            : [];

    private static IReadOnlyList<QueuedItem> Queued(IReadOnlyDictionary<string, object?>? inbox)
    {
        if (inbox is null || !inbox.TryGetValue("items", out var value) || value is not IReadOnlyList<object?> list)
            return [];
        var items = new List<QueuedItem>();
        foreach (var item in list.OfType<IReadOnlyDictionary<string, object?>>())
        {
            items.Add(new QueuedItem(
                SubmissionId.From(Convert.ToInt64(item["id"]!)),
                item.TryGetValue("mode", out var mode) ? mode as string ?? "" : ""));
        }

        return items;
    }

    /// <summary>
    /// 实验性：附加到一个对话的 agent 事件（spec §9.4）。快照与后续提交的注册在 Session 线上原子捕获；
    /// 溢出时以一份快照替换未投递的批次。对应 TS <c>watchEvents</c>。
    /// </summary>
    public static async Task<AgentEventStream> WatchEventsAsync(
        DurableSession harness, ConversationId conversationId, Context context)
    {
        CommittedWatch<IReadOnlyList<AgentEvent>>? watch = null;
        AgentEvent.Snapshot? snapshot = null;
        await ConversationViews.For(harness).AttachAsync(conversationId, async (initial, release) =>
        {
            var observer = await EventsObserver.CreateAsync(harness, conversationId, initial, release)
                .ConfigureAwait(false);
            watch = observer.Watch;
            snapshot = observer.Snapshot;
            return (ViewObserver)observer;
        }, context).ConfigureAwait(false);

        // 与 watch 一样，获取上下文决定流的生命周期。
        var signal = context.AbortSignal;
        if (signal?.IsCancellationRequested == true)
        {
            watch!.Cancel();
            throw new OperationCanceledException(signal.GetValueOrDefault());
        }

        if (signal is { } watchSignal) watch!.ObserveCancellation(watchSignal);
        return new AgentEventStream(watch!, snapshot!);
    }

    /// <summary>在挂载上创建事件批次 watch 的观察者。对应 TS <c>watchEvents</c> 的内联观察者。</summary>
    private sealed class EventsObserver : ViewObserver
    {
        private readonly ConversationId _conversationId;
        private readonly HashSet<TaskId<object?>> _held;
        private ConversationView _current;

        private EventsObserver(
            ConversationId conversationId,
            ConversationView initial,
            CommittedWatch<IReadOnlyList<AgentEvent>> watch,
            AgentEvent.Snapshot snapshot,
            HashSet<TaskId<object?>> held)
        {
            _conversationId = conversationId;
            _current = initial;
            Watch = watch;
            Snapshot = snapshot;
            _held = held;
        }

        public CommittedWatch<IReadOnlyList<AgentEvent>> Watch { get; }

        public AgentEvent.Snapshot Snapshot { get; }

        /// <summary>
        /// 在 Session 线上构建观察者：读取持留结局的生成（随快照），并建立批次 watch。
        /// 对应 TS <c>attach</c> 回调体。
        /// </summary>
        public static async Task<EventsObserver> CreateAsync(
            DurableSession harness, ConversationId conversationId, ConversationView initial, Action release)
        {
            // 已经结束其轮次的持留结局生成，随快照在线上读取。
            var completing = await HarnessUtil.ScanAllAsync(cursor => harness.ScanTasksOnLineAsync(
                new TaskQuery
                {
                    ConversationId = conversationId,
                    Kind = "pi.generation",
                    Status = DurableTaskStatus.Completing,
                },
                100,
                cursor)).ConfigureAwait(false);
            var held = new HashSet<TaskId<object?>>(completing.Select(record => record.Id));
            var snapshot = SnapshotOf(initial);
            // 批次是 watch 的值；溢出投递最新视图的一份快照。
            EventsObserver? self = null;
            var watch = new CommittedWatch<IReadOnlyList<AgentEvent>>(
                [], release, () => [SnapshotOf(self!._current)]);
            var observer = new EventsObserver(conversationId, initial, watch, snapshot, held);
            self = observer;
            return observer;
        }

        public override void Publication(
            ConversationView before, ConversationView after, IReadOnlyList<DeltaOp> ops,
            CommitPublication publication, Context context)
        {
            _current = after;
            var events = Translate(_conversationId, before, after, ops, publication, _held);
            if (events.Count > 0) Watch.Advance(events, [], context);
        }

        public override void CloseSession() => Watch.CloseSession();
    }

    /// <summary>结果条目 <c>callId</c> 在 <paramref name="entries"/> 中。对应 TS <c>resultOf</c>。</summary>
    private static EntryRecord? ResultOf(IReadOnlyList<EntryRecord> entries, string callId)
        => entries.FirstOrDefault(entry =>
            entry.Model?.Count > 0 && entry.Model[0] is ToolResultMessage message && message.ToolCallId == callId);

    /// <summary>一次发布造成的全部事件，按 spec §9.4 的顺序。对应 TS <c>translate</c>。</summary>
    private static List<AgentEvent> Translate(
        ConversationId conversationId,
        ConversationView before,
        ConversationView after,
        IReadOnlyList<DeltaOp> viewOps,
        CommitPublication publication,
        HashSet<TaskId<object?>> held)
    {
        var entries = new List<EntryRecord>();
        var tasks = new Dictionary<TaskId<object?>, TaskRecord>();
        var submissions = new List<SubmissionRecord>();
        foreach (var change in publication.Changes)
        {
            if (change is CommitChange.EntryTable entryChange && entryChange.Value.ConversationId == conversationId)
                entries.Add(entryChange.Value);
            if (change is CommitChange.TaskTable taskChange && taskChange.Value.ConversationId == conversationId)
                tasks[taskChange.Value.Id] = taskChange.Value;
            if (change is CommitChange.SubmissionTable submissionChange
                && submissionChange.Value.ConversationId == conversationId)
            {
                submissions.Add(submissionChange.Value);
            }
        }

        if (viewOps.Count == 0 && entries.Count == 0 && tasks.Count == 0 && submissions.Count == 0) return [];
        // 条目按 ID 序追加；提交记录按提交首次触及它们的顺序发布。
        submissions.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
        var was = PartsOf(before);
        var now = PartsOf(after);
        var events = new List<AgentEvent>();

        // 进展：工具开始、进行中消息、工具更新、重试与延迟状态。
        var slotsBefore = new Dictionary<string, ToolSlot>(StringComparer.Ordinal);
        foreach (var slot in ToolSlots(was.Live)) slotsBefore[slot.CallId] = slot;
        var slots = ToolSlots(now.Live);
        foreach (var slot in slots)
        {
            if (slot.Status != "running") continue;
            if (slotsBefore.TryGetValue(slot.CallId, out var previous) && previous.Status == "running") continue;
            IReadOnlyDictionary<string, object?> args = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (slot.TaskId is { } taskId && tasks.TryGetValue(taskId, out var taskRecord)
                && taskRecord.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
                && checkpoint.TryGetValue("arguments", out var arguments)
                && arguments is IReadOnlyDictionary<string, object?> argumentsJson)
            {
                args = argumentsJson;
            }

            events.Add(new AgentEvent.ToolExecutionStart
            {
                ToolCallId = slot.CallId,
                ToolName = slot.Name,
                Args = args,
            });
        }

        var partialBefore = PartialOf(was.Live);
        var partial = PartialOf(now.Live);
        if (partial is not null && partialBefore is null)
        {
            events.Add(new AgentEvent.MessageStart { Message = partial });
        }
        else if (partial is not null && !ReferenceEquals(partial, partialBefore))
        {
            events.Add(new AgentEvent.MessageUpdate
            {
                Usage = partial.UsageStats ?? new Pi.Ai.Types.Usage(0, 0),
                Changes = MessageChanges(viewOps, partial),
            });
        }

        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            slotsBefore.TryGetValue(slot.CallId, out var previous);
            if (slot.Status != "running" || previous?.Status != "running") continue;
            var update = ToolUpdate(viewOps, index, slot, previous);
            if (update is null) continue;
            events.Add(new AgentEvent.ToolExecutionUpdate
            {
                ToolCallId = slot.CallId,
                ToolName = slot.Name,
                Output = update.Output,
                Details = update.Details,
                Diagnostics = update.Diagnostics,
            });
        }

        var generationJson = GenerationJson(now.Live);
        var generationBeforeJson = GenerationJson(was.Live);
        var retry = RetryOf(generationJson);
        var retryBefore = RetryOf(generationBeforeJson);
        if (retry is not null && retryBefore is null)
        {
            events.Add(new AgentEvent.AutoRetryStart
            {
                Attempt = AttemptOf(generationJson),
                At = retry.At,
                ErrorMessage = retry.Error,
            });
        }

        if (retryBefore is not null && retry is null)
        {
            events.Add(new AgentEvent.AutoRetryEnd { Attempt = AttemptOf(generationBeforeJson) });
        }

        var deferredPollAt = DeferredPollAtOf(generationJson);
        if (deferredPollAt is { } pollAt && pollAt != DeferredPollAtOf(generationBeforeJson))
        {
            events.Add(new AgentEvent.DeferredPoll { PollAt = pollAt });
        }

        // 本提交结束的工具：变为 done 的槽、创建即 done 的槽（未提供的调用）、或因运行结束而消失的未完成槽。
        // 消失的 done 槽更早结束。
        var toolEnds = new List<AgentEvent.ToolExecutionEnd>();
        void EndTool(string callId, string name, long? entryId)
        {
            var entry = entryId is { } id ? entries.FirstOrDefault(candidate => candidate.Id.Value == id) : null;
            toolEnds.Add(new AgentEvent.ToolExecutionEnd
            {
                ToolCallId = callId,
                ToolName = name,
                Entry = entry,
            });
        }

        foreach (var previous in slotsBefore.Values)
        {
            if (previous.Status == "done") continue;
            var slot = slots.FirstOrDefault(candidate => candidate.CallId == previous.CallId);
            if (slot?.Status == "done") EndTool(previous.CallId, previous.Name, slot.Entry?.Value);
            // 运行在本次提交结束的槽可能已随之追加其结果，如未开始的调用。
            else if (slot is null) EndTool(previous.CallId, previous.Name, ResultOf(entries, previous.CallId)?.Id.Value);
        }

        foreach (var slot in slots)
        {
            if (slot.Status == "done" && !slotsBefore.ContainsKey(slot.CallId))
                EndTool(slot.CallId, slot.Name, slot.Entry?.Value);
        }

        // 条目按追加序；工具结束紧接其结果消息，如 coding agent。
        var assistantAppended = false;
        foreach (var entry in entries)
        {
            events.AddRange(toolEnds.Where(end => ReferenceEquals(end.Entry, entry)));
            if (entry.Model is not { Count: > 0 } model)
            {
                events.Add(new AgentEvent.EntryAppended { Entry = entry });
                continue;
            }

            var message = model[0];
            // 流式答案已随其首个 partial 开始。
            var streamed = message is AssistantMessage && partialBefore is not null && !assistantAppended;
            if (message is AssistantMessage) assistantAppended = true;
            if (!streamed) events.Add(new AgentEvent.MessageStart { Message = message });
            events.Add(new AgentEvent.MessageEnd { Entry = entry });
        }

        // 无结果条目的结束：faulted 或 orphaned 的工具，或运行结束者。
        events.AddRange(toolEnds.Where(end => end.Entry is null));

        // 压缩结束、任务失败，然后轮次与运行结束。
        var compactionsBefore = CompactionStatuses(was.Live);
        var compactions = CompactionStatuses(now.Live);
        foreach (var previous in compactionsBefore)
        {
            if (!compactions.Any(status => status.TaskId == previous.TaskId))
            {
                events.Add(new AgentEvent.CompactionEnd { TaskId = previous.TaskId, Reason = previous.Reason });
            }
        }

        // 生成的轮次在其结局被提交时结束：在 completing 持留处或终态，先到者为准，
        // 因此持留处创建的后继在其之后开始。
        var turnEnded = false;
        foreach (var task in tasks.Values)
        {
            var status = task.State.Status;
            if (task.Kind == "pi.generation" && status == DurableTaskStatus.Completing && !held.Contains(task.Id))
            {
                held.Add(task.Id);
                turnEnded = true;
            }

            if (status != DurableTaskStatus.Terminal) continue;
            if (task.Kind == "pi.generation" && !held.Remove(task.Id)) turnEnded = true;
            var outcome = task.State.Outcome;
            if (outcome is null) continue;
            if (outcome.Status is TaskOutcomeStatus.Faulted or TaskOutcomeStatus.Orphaned)
            {
                var message = outcome.Status == TaskOutcomeStatus.Faulted
                    ? outcome.Error?.Message ?? ""
                    : outcome.Reason ?? "";
                events.Add(new AgentEvent.TaskFailed
                {
                    TaskId = task.Id,
                    Kind = task.Kind,
                    Message = message,
                });
            }
        }

        if (turnEnded) events.Add(new AgentEvent.TurnEnd());
        var run = RunInputs(now.Live);
        var runBefore = RunInputs(was.Live);
        var runChanged = run.Inputs.FirstOrDefault() != runBefore.Inputs.FirstOrDefault();
        if (runBefore.Present && runChanged)
        {
            events.Add(new AgentEvent.RunEnd { Inputs = runBefore.Inputs });
        }

        // 提交、文档状态，然后开始者。
        foreach (var record in submissions) events.Add(new AgentEvent.Submission { Record = record });
        if (!SameValue(now.Inbox, was.Inbox))
        {
            events.Add(new AgentEvent.InboxUpdate { Items = Queued(now.Inbox) });
        }

        // 退役文档读作其初值，如快照。
        if (!SameValue(now.Agent, was.Agent))
        {
            events.Add(new AgentEvent.AgentChanged { Agent = now.Agent ?? AgentDocs.InitialAgent() });
        }

        if (!SameValue(now.Usage, was.Usage))
        {
            events.Add(new AgentEvent.UsageChanged { Usage = now.Usage ?? Usage.InitialUsage() });
        }

        foreach (var status in compactions)
        {
            if (!compactionsBefore.Any(previous => previous.TaskId == status.TaskId))
            {
                events.Add(new AgentEvent.CompactionStart
                {
                    TaskId = status.TaskId,
                    Reason = status.Reason,
                    Blocking = status.Blocking,
                });
            }
        }

        if (run.Present && runChanged)
        {
            events.Add(new AgentEvent.RunStart { Inputs = run.Inputs });
        }

        if (run.Present && run.TaskId != runBefore.TaskId
            && run.TaskId is { } runTaskId && tasks.TryGetValue(runTaskId, out var runTask)
            && runTask.Kind == "pi.generation")
        {
            events.Add(new AgentEvent.TurnStart());
        }

        return events;
    }

    private sealed record RunView(bool Present, TaskId<object?>? TaskId, IReadOnlyList<SubmissionId> Inputs);

    private static RunView RunInputs(IReadOnlyDictionary<string, object?> live)
    {
        if (!live.TryGetValue("run", out var runValue) || runValue is not IReadOnlyDictionary<string, object?> run)
            return new RunView(false, null, []);
        var taskId = run.TryGetValue("taskId", out var id) && id is not null
            ? TaskId<object?>.From(Convert.ToInt64(id))
            : (TaskId<object?>?)null;
        IReadOnlyList<SubmissionId> inputs = run.TryGetValue("inputs", out var inputsValue)
            && inputsValue is IReadOnlyList<object?> list
            ? [.. list.Select(input => SubmissionId.From(Convert.ToInt64(input)))]
            : [];
        return new RunView(true, taskId, inputs);
    }

    private static bool SameValue(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is IReadOnlyDictionary<string, object?> a && right is IReadOnlyDictionary<string, object?> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var other) || !SameValue(value, other)) return false;
            }

            return true;
        }

        return Equals(left, right);
    }

    private static AssistantMessage? PartialOf(IReadOnlyDictionary<string, object?> live)
    {
        var generationJson = GenerationJson(live);
        if (generationJson is null || !generationJson.TryGetValue("message", out var message)) return null;
        return JsonTrees.ToAssistantMessage(message as IReadOnlyDictionary<string, object?>);
    }

    private static IReadOnlyDictionary<string, object?>? GenerationJson(IReadOnlyDictionary<string, object?> live)
        => live.TryGetValue("generation", out var value) && value is IReadOnlyDictionary<string, object?> generation
            ? generation
            : null;

    private static int AttemptOf(IReadOnlyDictionary<string, object?>? generation)
        => generation is not null && generation.TryGetValue("attempt", out var attempt) ? Convert.ToInt32(attempt) : 0;

    private static AgentEvent.SnapshotGeneration.RetryState? RetryOf(IReadOnlyDictionary<string, object?>? generation)
    {
        if (generation is null || !generation.TryGetValue("retry", out var retry)
            || retry is not IReadOnlyDictionary<string, object?> retryJson) return null;
        return new AgentEvent.SnapshotGeneration.RetryState(
            Convert.ToInt64(retryJson["at"]!),
            retryJson.TryGetValue("error", out var error) ? error as string ?? "" : "");
    }

    private static long? DeferredPollAtOf(IReadOnlyDictionary<string, object?>? generation)
    {
        if (generation is null || !generation.TryGetValue("deferred", out var deferred)
            || deferred is not IReadOnlyDictionary<string, object?> deferredJson
            || !deferredJson.TryGetValue("pollAt", out var pollAt)) return null;
        return Convert.ToInt64(pollAt);
    }

    /// <summary>把视图操作翻译成进行中消息的变更（spec §9.4）。对应 TS <c>messageChanges</c>。</summary>
    private static List<MessageChange> MessageChanges(IReadOnlyList<DeltaOp> viewOps, AssistantMessage message)
    {
        var changes = new List<MessageChange>();
        // 整体发送的块已含本批次其对它的所有后续变更。
        var whole = new HashSet<int>();
        foreach (var op in viewOps)
        {
            // 视图操作从不替换根。
            var path = OpPath(op);
            if (!StartsWith(path, PartialPath))
            {
                // 整条消息或 generation 被替换。
                if (StartsWith(PartialPath, path)) return [new MessageChange.Whole { Value = message }];
                continue;
            }

            var rest = path.Segments.Skip(PartialPath.Segments.Count).ToList();
            if (rest.Count > 0 && IsKey(rest[0], "usage")) continue;
            if (rest.Count == 0 || !IsKey(rest[0], "content"))
                return [new MessageChange.Whole { Value = message }];
            if (rest.Count == 1)
            {
                if (op is not DeltaOp.Splice splice || splice.Index != 0)
                    return [new MessageChange.Whole { Value = message }];
                var blocks = splice.Items.OfType<ContentBlock>().ToList();
                for (var offset = 0; offset < blocks.Count; offset++)
                {
                    var block = blocks[offset];
                    var kind = block switch
                    {
                        TextContent => "text_start",
                        ThinkingContent => "thinking_start",
                        _ => "toolcall_start",
                    };
                    changes.Add(new MessageChange.Start
                    {
                        Kind = kind,
                        ContentIndex = (int)splice.Path.Segments.Count + offset,
                        ContentBlock = block,
                    });
                }

                continue;
            }

            if (rest[1].Value is not long contentIndexLong) continue;
            var contentIndex = (int)contentIndexLong;
            var field = rest.Count > 2 && rest[2].Value is string fieldKey ? fieldKey : null;
            if (whole.Contains(contentIndex)) continue;
            if (op is DeltaOp.Append append && rest.Count == 3 && (field == "text" || field == "thinking"))
            {
                changes.Add(new MessageChange.Delta
                {
                    Kind = field == "text" ? "text_delta" : "thinking_delta",
                    ContentIndex = contentIndex,
                    Value = append.Text,
                });
            }
            else if (op is DeltaOp.Append toolAppend && field == "arguments")
            {
                changes.Add(new MessageChange.ToolCallDelta
                {
                    ContentIndex = contentIndex,
                    PathSegments = [.. rest.Skip(3).Select(SegmentToObject)],
                    Value = toolAppend.Text,
                });
            }
            else
            {
                whole.Add(contentIndex);
                changes.Add(new MessageChange.Block
                {
                    ContentIndex = contentIndex,
                    Value = message.Content[contentIndex],
                });
            }
        }

        return changes;
    }

    private static object SegmentToObject(Seg segment) => segment.Value;

    /// <summary>段是否为该名称的对象键。</summary>
    private static bool IsKey(Seg segment, string name) => segment.Value is string key && key == name;

    /// <summary>运行中槽的输出、details、诊断变更。对应 TS <c>toolUpdate</c>。</summary>
    private static ToolUpdateResult? ToolUpdate(
        IReadOnlyList<DeltaOp> viewOps, int index, ToolSlot slot, ToolSlot previous)
    {
        var outputPath = new Path([
            Seg.Key("docs"), Seg.Key("pi.live"), Seg.Key("tools"), Seg.Index(index), Seg.Key("output"),
        ]);
        long trimStart = 0;
        var append = "";
        var set = false;
        foreach (var op in viewOps)
        {
            if (!StartsWith(OpPath(op), outputPath)) continue;
            switch (op)
            {
                case DeltaOp.Truncate truncate:
                    trimStart += truncate.Length;
                    break;
                case DeltaOp.Append appendOp:
                    append += appendOp.Text;
                    break;
                default:
                    set = true;
                    break;
            }
        }

        AgentEvent.ToolOutputChange? output = null;
        if (set || (slot.Output != previous.Output && trimStart == 0 && append == ""))
        {
            output = AgentEvent.ToolOutputChange.Replace(slot.Output ?? "");
        }
        else if (trimStart > 0 || append != "")
        {
            output = AgentEvent.ToolOutputChange.Trim(trimStart, append == "" ? null : append);
        }

        // 安全重跑清除运行中槽的进展：移除的 details 发送 null，移除的 diagnostics 发送 []。
        var detailsChanged = !Equals(slot.Details, previous.Details);
        var diagnosticsChanged = !SameValue(slot.Diagnostics, previous.Diagnostics);
        if (output is null && !detailsChanged && !diagnosticsChanged) return null;
        return new ToolUpdateResult(
            output,
            detailsChanged ? slot.Details : null,
            diagnosticsChanged ? slot.Diagnostics ?? [] : null);
    }

    private sealed record ToolUpdateResult(
        AgentEvent.ToolOutputChange? Output, object? Details, IReadOnlyList<ToolDiagnostic>? Diagnostics);

    private static Path OpPath(DeltaOp op) => op switch
    {
        DeltaOp.Replace => Path.Root,
        DeltaOp.Set set => set.Path,
        DeltaOp.Delete delete => delete.Path,
        DeltaOp.Append append => append.Path,
        DeltaOp.Truncate truncate => truncate.Path,
        DeltaOp.Splice splice => splice.Path,
        DeltaOp.Move move => move.Path,
        _ => throw new InvalidOperationException("unknown op"),
    };

    private static bool StartsWith(Path path, Path prefix)
    {
        if (prefix.Segments.Count > path.Segments.Count) return false;
        for (var index = 0; index < prefix.Segments.Count; index++)
        {
            if (!SameSegment(prefix.Segments[index], path.Segments[index])) return false;
        }

        return true;
    }

    private static bool SameSegment(Seg left, Seg right) => Equals(left.Value, right.Value);
}
