using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// harness 系列测试的共享脚手架。对应 TS 的 <c>task-support.ts</c> 与 <c>chat-support.ts</c>：
/// 延迟门闩、轮询等待、任务打开与断言辅助，供多份 harness-*.test.ts 移植共用。
/// </summary>
internal static class HarnessTestSupport
{
    internal static readonly Context Ctx = Context.Background;

    /// <summary>供测试判定的延迟门闩；对应 TS <c>deferred()</c>。</summary>
    internal sealed class Deferred<T>
    {
        private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _source.Task;

        public void Resolve(T value) => _source.TrySetResult(value);

        public void Reject(Exception error) => _source.TrySetException(error);

        public void Resolve() => _source.TrySetResult(default!);
    }

    internal static Deferred<T> NewDeferred<T>() => new();

    /// <summary>冲刷待定微任务与一个宏任务轮次。对应 TS <c>flush()</c>（<c>setTimeout(0)</c>）。</summary>
    internal static async Task FlushAsync()
    {
        await Task.Yield();
        await Task.Delay(1).ConfigureAwait(false);
    }

    /// <summary>按次数冲刷宏任务轮次，直到 <paramref name="check"/> 成立。对应 TS <c>eventually()</c>。</summary>
    internal static async Task EventuallyAsync(Func<Task<bool>> check)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await check().ConfigureAwait(false)) return;
            await Task.Yield();
        }

        throw new TimeoutException("Condition was not reached");
    }

    internal static async Task EventuallyAsync(Func<bool> check)
        => await EventuallyAsync(() => Task.FromResult(check())).ConfigureAwait(false);

    /// <summary>实时轮询 <paramref name="check"/> 直到成立。对应 TS <c>waitFor()</c>。</summary>
    internal static async Task WaitForAsync(Func<Task<bool>> check, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await check().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    internal static async Task WaitForAsync(Func<bool> check, int timeoutMs = 5000)
        => await WaitForAsync(() => Task.FromResult(check()), timeoutMs).ConfigureAwait(false);

    /// <summary>下一个完成任务的持久化状态。对应 TS <c>completed()</c>。</summary>
    internal static TaskState Completed(object? result) => new()
    {
        Status = TaskStatus.Terminal,
        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = result },
    };

    /// <summary>下一个中止任务的持久化状态。对应 TS <c>abortedWith()</c>。</summary>
    internal static TaskState AbortedWith(string reason) => new()
    {
        Status = TaskStatus.Terminal,
        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted, Reason = reason },
    };

    /// <summary>登记 fake 任务的扩展。对应 TS <c>defineExtension({ name, tasks })</c>。</summary>
    internal sealed class TaskExtension(string name, AnyDurableTask[] tasks) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    /// <summary>打开装有所述任务定义的 Harness。对应 TS <c>openTasks()</c>。</summary>
    internal static async Task<(HarnessImpl Harness, IRegistry Registry, List<Exception> Reports)> OpenTasksAsync(
        IStorage storage, IReadOnlyList<AnyDurableTask> tasks, Func<long>? now = null)
    {
        var registry = Registry.CreateRegistry();
        if (tasks.Count > 0) registry.Install(new TaskExtension("tasks", [.. tasks]));
        var reports = new List<Exception>();
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = new Models(),
            Registry = registry,
            OnReport = report => reports.Add(report as Exception ?? new InvalidOperationException(report?.ToString())),
            Now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        }, Ctx).ConfigureAwait(false);
        return (harness, registry, reports);
    }

    /// <summary>由阶段处理器数组构造一个无输入/状态/结果约束的 fake 任务定义。对应 TS <c>task()</c>。</summary>
    internal static DurableTask<JsonDict, JsonDict, JsonDict> DefineTask(
        string name, int version, (string Name, TaskPhaseHandler Handler)[] phases, TaskPhaseHandler? abort = null,
        Action? migrate = null)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = name,
                Version = version,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = phases[0].Name },
                Phases = phases.ToDictionary(pair => pair.Name, pair => (object?)pair.Handler),
                Abort = abort,
                // 擦除迁移委托以 ErasedMigrate 形状承载；仅计数/抛出，不改变输入与 checkpoint。
                Migrate = migrate is null
                    ? null
                    : new ErasedMigrate((input, checkpoint, fromVersion) =>
                    {
                        migrate();
                        return (input, checkpoint);
                    }),
            },
        };

    /// <summary>
    /// 以单个 phase="run" 的裸定义创建任务、指定其存储版本（用于制造迁移场景）并返回 ID。
    /// 对应 TS <c>tx.createTask(task(name, version), null, …)</c>。
    /// </summary>
    internal static async Task<TaskId<JsonDict>> CreateRawTaskAsync(
        ITx tx, string name, int version, string phase, ConversationId conversationId)
    {
        var definition = DefineTask(name, version, [(phase, (_, _, _) => Task.CompletedTask)]);
        return await tx.CreateTaskAsync(definition, null, new TaskOptions
        {
            Ownership = new TaskOwnership.ConversationOwner(),
            ConversationId = conversationId,
        }).ConfigureAwait(false);
    }

    /// <summary>一个总是指向下一阶段的状态提交处理器。</summary>
    internal static TaskPhaseHandler AdvanceTo(string phase)
        => (task, runtime, context) => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = phase },
            }),
            context);

    /// <summary>一个完成任务的状态提交处理器。</summary>
    internal static TaskPhaseHandler CompleteWith(object? result)
        => (task, runtime, context) => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(Completed(result)),
            context);

    /// <summary>关闭 Harness，收尾不抛。</summary>
    internal static async Task CloseQuietlyAsync(IHarness harness)
    {
        try
        {
            await harness.CloseAsync(Ctx).ConfigureAwait(false);
        }
        catch
        {
            // 测试收尾不抛。
        }
    }

    /// <summary>
    /// 跨 close/reopen 幸存的 models 与 registry（如同宿主进程自己的对象）。
    /// 对应 TS <c>ChatSetup</c>（chat-support.ts）。
    /// </summary>
    internal sealed class ChatSetup
    {
        public required FakeProvider Provider { get; init; }

        public required Models Models { get; init; }

        public required IRegistry Registry { get; init; }

        public required List<object> Reports { get; init; }

        /// <summary>活的 Harness 设置；测试在决策之间赋值以改变它们。</summary>
        public required HarnessSettings Settings { get; init; }

        /// <summary>宿主时钟；默认单调递增的固定值。</summary>
        public Func<long> Now { get; set; } = () => 1;
    }

    /// <summary>按给定的应答构造一个 chat 夹具。对应 TS <c>chatSetup()</c>。</summary>
    internal static ChatSetup NewChatSetup(AssistantMessage response, bool block = false)
        => NewChatSetup(new FakeProvider("faux", _ => response, block));

    internal static ChatSetup NewChatSetup(FakeProvider provider)
    {
        var models = new Models();
        models.SetProvider(provider);
        return new ChatSetup
        {
            Provider = provider,
            Models = models,
            Registry = Registry.CreateRegistry(),
            Reports = [],
            Settings = new HarnessSettings
            {
                Extensions = [],
                Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            },
        };
    }

    /// <summary>在 <paramref name="storage"/> 上打开 Harness 并返回其 root。对应 TS <c>openChat()</c>。</summary>
    internal static async Task<(HarnessImpl Harness, IConversation Root)> OpenChatAsync(
        IStorage storage, ChatSetup setup)
    {
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models,
            Registry = setup.Registry,
            Settings = setup.Settings,
            Now = () => setup.Now(),
            OnReport = error => setup.Reports.Add(error),
        }, Ctx).ConfigureAwait(false);
        var root = await harness.RootAsync(
            Ctx, new AgentChange { Model = new ModelRef(setup.Provider.Id, setup.Provider.ModelId) })
            .ConfigureAwait(false);
        return (harness, root);
    }

    /// <summary>一个对话的全部原始条目，最旧在前。对应 TS <c>allEntries()</c>。</summary>
    internal static async Task<IReadOnlyList<EntryRecord>> AllEntriesAsync(
        IConversation conversation, Context? context = null)
    {
        var page = await conversation.EntriesAsync(
            new EntryQuery { ConversationId = conversation.Id }, 1000, null, context ?? Ctx)
            .ConfigureAwait(false);
        return [.. page.Items.Reverse()];
    }

    /// <summary>等待 <paramref name="gate"/> 或取消（取消时抛出）。对应 TS <c>Promise.race([gate, aborted(signal)])</c>。</summary>
    internal static async Task RaceAsync(Task gate, CancellationToken signal)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => aborted.TrySetCanceled());
        var completed = await Task.WhenAny(gate, aborted.Task).ConfigureAwait(false);
        if (completed == aborted.Task) signal.ThrowIfCancellationRequested();
        await gate.ConfigureAwait(false);
    }

    /// <summary>一个 assistant 应答消息（provider 为 <c>faux</c>）。对应 TS <c>fauxAssistantMessage(text)</c>。</summary>
    internal static AssistantMessage AssistantReply(string text) => AssistantReply("faux", text);

    internal static AssistantMessage AssistantReply(string providerId, string text)
        => new([new TextContent(text)])
        {
            StopReason = StopReason.Stop,
            Model = $"{providerId}-1",
            Provider = providerId,
            UsageStats = new Pi.Ai.Types.Usage(5, 7),
        };
}

/// <summary>
/// 记录提交批次并对指定读取加门闩的存储装饰器。对应 TS <c>ControlledStorage</c>
/// （session-support.ts 的子集：commits 记录 + submission 读门闩）。
/// </summary>
internal sealed class ControlledHarnessStorage(IStorage inner) : IStorage
{
    /// <summary>已受理的提交批次（借用视图）。</summary>
    public readonly List<IReadOnlyList<StorageWrite>> AdmittedCommits = [];

    /// <summary>为值断言而解耦的提交批次。</summary>
    public readonly List<IReadOnlyList<StorageWrite>> Commits = [];

    private HarnessTestSupport.Deferred<Unit>? _submissionGate;
    private HarnessTestSupport.Deferred<Unit>? _commitGate;
    private HarnessTestSupport.Deferred<Unit>? _commitEntered;

    /// <summary>让下一次 <c>GetSubmissionAsync</c> 阻塞，直到释放；返回进入信号与释放动作。</summary>
    public (Task Entered, Action Release) HoldSubmissionReads()
    {
        var gate = new HarnessTestSupport.Deferred<Unit>();
        _submissionGate = gate;
        var entered = new HarnessTestSupport.Deferred<Unit>();
        EnteredSignal = entered;
        return (entered.Task, () => gate.Resolve(default));
    }

    /// <summary>让下一次 <c>CommitAsync</c> 阻塞，直到释放；返回进入信号与释放动作。对应 TS <c>holdCommits()</c>。</summary>
    public (Task Entered, Action Release) HoldCommits()
    {
        var gate = new HarnessTestSupport.Deferred<Unit>();
        _commitGate = gate;
        var entered = new HarnessTestSupport.Deferred<Unit>();
        _commitEntered = entered;
        return (entered.Task, () => gate.Resolve(default));
    }

    private HarnessTestSupport.Deferred<Unit>? EnteredSignal { get; set; }

    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
    {
        AdmittedCommits.Add(writes);
        Commits.Add([.. writes]);
        if (_commitGate is { } gate)
        {
            _commitGate = null;
            _commitEntered?.Resolve(default);
            await gate.Task.ConfigureAwait(false);
        }

        return await inner.CommitAsync(writes, signal).ConfigureAwait(false);
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct => inner.MintIdAsync<TId>();

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id) => inner.GetConversationAsync(id);

    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => inner.ScanConversationsAsync(query, limit, cursor);

    public Task<StoredEntry?> GetEntryAsync(EntryId id) => inner.GetEntryAsync(id);

    public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
        => inner.GetEntryAsync(conversationId, id);

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
        => inner.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId);

    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => inner.ScanEntriesAsync(query, limit, cursor);

    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id) => inner.GetTaskAsync(id);

    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => inner.ScanTasksAsync(query, limit, cursor);

    public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
    {
        if (_submissionGate is { } gate)
        {
            _submissionGate = null;
            EnteredSignal?.Resolve(default);
            await gate.Task.ConfigureAwait(false);
        }

        return await inner.GetSubmissionAsync(id).ConfigureAwait(false);
    }

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => inner.ScanSubmissionsAsync(query, limit, cursor);

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
        => inner.GetSubmissionByRequestAsync(conversationId, requestId);

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
        => inner.FindDocumentAsync(address, at);

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
        => inner.GetDocumentAsync(id, at);

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => inner.ScanDocumentsAsync(query, limit, cursor);

    public Task CloseAsync() => inner.CloseAsync();

    public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
        => inner.SubscribeAsync(listener, signal);

    public Task UnsubscribeAsync(Func<CommitPublication, Task> listener) => inner.UnsubscribeAsync(listener);

    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
        => inner.AttachDocumentAsync(id, listener);
}

/// <summary>无载荷的占位值类型。</summary>
internal readonly record struct Unit;
