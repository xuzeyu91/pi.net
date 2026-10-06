using System.Threading.Tasks;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Durable.Env;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;

/// <summary>一次扫描的页大小；与 TS <c>harness.ts</c> 的 <c>SCAN_PAGE_SIZE</c> 一致。</summary>
internal static class HarnessScan
{
    public const int PageSize = 256;
}

/// <summary>
/// 创建对话的目标：根、独立对话，或从既有对话 fork。
/// 对应 TS <c>CreateTarget</c>（判别联合，C# 以抽象 record 承载）。
/// </summary>
public abstract record CreateTarget
{
    /// <summary>保留的根对话（缺省时创建）。</summary>
    public sealed record Root : CreateTarget;

    /// <summary>独立对话。</summary>
    public sealed record Independent(ConversationOwnership Ownership) : CreateTarget;

    /// <summary>在 <paramref name="At"/> 处从父对话 fork。</summary>
    public sealed record Fork(ConversationId ParentId, EntryId At, ConversationOwnership Ownership) : CreateTarget;
}

/// <summary>
/// 便捷选项在创建提交中、创建钩子之后应用。对应 TS <c>CreateOptions</c>。
/// </summary>
public sealed record CreateOptions
{
    public AgentChange? Agent { get; init; }

    public ConversationInit? Init { get; init; }
}

/// <summary>
/// Conversation 句柄使用的 Harness 私有服务。对应 TS <c>ConversationHost</c>。
/// </summary>
internal sealed record ConversationHost(
    HarnessImpl Harness,
    IStorage Storage,
    TaskScheduler Tasks,
    Submissions Submissions,
    ConversationViews Views,
    Func<long> Now,
    Func<CreateTarget, CreateOptions, Context, Task<IConversation>> Create);

/// <summary>
/// 绑定到返回它的 Harness 的无状态对话句柄。按 <see cref="Id"/> 比较。
/// 对应 TS <c>ConversationImpl</c>。
/// </summary>
internal sealed class ConversationImpl : IConversation
{
    private readonly ConversationHost _host;

    public ConversationImpl(ConversationId id, ConversationHost host)
    {
        Id = id;
        _host = host;
    }

    public ConversationId Id { get; }

    public Task<Agent> AgentAsync(Context context) => _host.Harness.ResolveAgentAsync(Id, null, context);

    public Task ConfigureAsync(AgentChange change, Context context)
        => _host.Harness.CommitWithAsync(
            async tx =>
            {
                await AgentDocs.ConfigureAsync(tx, Id, change).ConfigureAwait(false);
                return true;
            }, context);

    public Task<ISubmission> SubmitAsync(SubmissionDraft submission, Context context)
        => _host.Submissions.SubmitAsync(Id, submission, context);

    public Task<TaskId<CompactionResult>> CompactAsync(string? instructions, Context context)
    {
        _host.Tasks.Resume();
        return _host.Harness.CommitWithAsync(
            async tx => TaskId<CompactionResult>.From((await Compaction.CreateCompactionAsync(
                tx, Id, CompactionReason.Manual, null, instructions).ConfigureAwait(false)).Value),
            context);
    }

    public async Task ResetAsync(string? handoff, Context context)
    {
        // TS：handoff 缺省时省略 model 字段；给定则携带一条 timestamp 为宿主时钟的用户消息。
        var entry = new EntryDraft
        {
            Kind = DurableEntries.ResetEntry.Kind,
            HeadIsSelf = true,
            Model = handoff is null
                ? null
                : [new UserMessage([new TextContent(handoff)], _host.Now())],
        };
        await _host.Submissions.SubmitAsync(Id, new SubmissionDraft.Write { Entry = entry }, context)
            .ConfigureAwait(false);
    }

    public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context)
        => _host.Harness.CommitWithAsync(change, context, new TransactionScope { ConversationId = Id });

    public Task<ContextView> ContextAsync(Context context)
        => ConversationContext.ReadContextAsync(_host.Harness, _host.Storage, Id, context);

    public Task<Page<EntryRecord>> EntriesAsync(
        EntryQuery query, int limit, JsonDict? cursor, Context context)
    {
        var bounded = new EntryQuery
        {
            ConversationId = Id,
            MinEntryId = query.MinEntryId,
            MaxEntryId = query.MaxEntryId,
        };
        return _host.Harness.ReadOnLineAsync(
            () => _host.Storage.ScanEntriesAsync(bounded, limit, cursor));
    }

    public Task<IConversation> ForkAsync(EntryId at, ConversationCreateOptions options, Context context)
        => _host.Create(
            new CreateTarget.Fork(Id, at, options.Ownership),
            new CreateOptions { Agent = options.Agent, Init = options.Init },
            context);

    public Task AbortAsync(Context context, ConversationAbortOptions? options = null)
    {
        _host.Tasks.Resume();
        return _host.Tasks.AbortConversationAsync(Id, options?.Background == true, context);
    }

    public Task WaitForIdleAsync(Context context)
    {
        _host.Tasks.Resume();
        return _host.Tasks.WaitForIdleAsync(Id, context);
    }

    public Task<IConversationWatch> WatchAsync(Context context)
        => throw new NotSupportedException(
            "ConversationImpl.watch(): 结构视图 watch 在 P52（view.ts）落地后接入");
}

/// <summary>
/// Session 内核扩展了对话句柄与注册表。对应 TS <c>HarnessImpl</c>。
/// </summary>
public sealed class HarnessImpl : DurableSession, IHarness
{
    private readonly IStorage _storage;
    private readonly HarnessOptions _options;
    private readonly Action<object> _report;
    private readonly ConversationHost _host;
    private readonly TaskScheduler _tasks;
    private readonly Submissions _submissions;
    private readonly TaskGraphView _taskGraph;
    private readonly ConversationViews _views;
    private bool _closed;

    public HarnessImpl(IStorage storage, HarnessOptions options, Context context)
        : base(storage)
    {
        _storage = storage;
        _options = options;
        _report = options.OnReport ?? (_ => { });
        var now = options.Now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var reportException = new Action<Exception>(error => _report(error));
        Settings Settings() => AgentDocs.ResolveSettings(options.Settings);
        // 调度器的 Conversation 委托在运行时才被调用（此时 _submissions/_tasks 已赋值），
        // 但字段初始化顺序使闭包捕获它们会触发可空警告；以本地持有者桥接。
        Submissions? submissionsRef = null;
        TaskScheduler? tasksRef = null;
        _tasks = new TaskScheduler(new TaskSchedulerOptions
        {
            Session = this,
            Storage = storage,
            Registry = options.Registry,
            Models = options.Models,
            Agent = (id, snapshot, callContext) => ResolveAgentAsync(id, snapshot, callContext),
            Settings = Settings,
            Env = (id, callContext) => BuildEnvAsync(id, callContext),
            Now = now,
            Report = reportException,
            SettleOutcome = LiveSettle.SettleSchedulerOutcomeAsync,
            WithdrawInputs = Inbox.WithdrawQueuedInputsAsync,
            Conversation = async (id, binding, callContext) =>
            {
                var record = await ReadOnLineAsync(() => storage.GetConversationAsync(id)).ConfigureAwait(false);
                return record is null
                    ? null
                    : BoundConversations.BoundConversation(id, binding, submissionsRef!, tasksRef!);
            },
            Context = ContextSignals.WithoutAbortSignal(context),
        });
        _submissions = new Submissions(
            this, storage, now,
            () => Settings().SteeringMode,
            () => Settings().FollowUpMode,
            () => _tasks.Resume());
        submissionsRef = _submissions;
        tasksRef = _tasks;
        _taskGraph = new TaskGraphView(this, storage);
        // 视图挂载必须在 ConversationHost 之前创建：其构造函数把自身注册进 Harness→Views 表
        // （对应 TS 的 conversationViews() 惰性检索）。
        _views = new ConversationViews(this, storage);
        _host = new ConversationHost(
            this,
            storage,
            _tasks,
            _submissions,
            _views,
            now,
            (target, createOptions, createContext) => CreateCoreAsync(target, createOptions, createContext));
    }

    /// <summary>
    /// 对照 <paramref name="snapshot"/> 或当前注册表解析一个对话的已提交 <c>pi.agent</c>，以及当前设置。
    /// 对应 TS <c>resolveAgent()</c>。
    /// </summary>
    public async Task<Agent> ResolveAgentAsync(ConversationId id, IRegistrySnapshot? snapshot, Context context)
    {
        var registry = snapshot ?? _options.Registry.Snapshot();
        var state = await SnapshotAsync(AgentDocs.AgentDoc, id, context).ConfigureAwait(false);
        return AgentDocs.ResolveAgent(
            state is null ? null : AgentDocs.StateFromJson(state), registry,
            AgentDocs.ResolveSettings(_options.Settings), _report);
    }

    /// <summary>
    /// 从对话当前的 <c>cwd</c> 构建其环境；无 <c>env</c> 选项时为 null。
    /// 对应 TS <c>buildEnv()</c>。
    /// </summary>
    public async Task<IExecutionEnv?> BuildEnvAsync(ConversationId id, Context context)
    {
        var build = _options.EnvFactory;
        if (build is null) return null;
        var state = await SnapshotAsync(AgentDocs.AgentDoc, id, context).ConfigureAwait(false);
        var cwd = state is null ? null : AgentDocs.StateFromJson(state)?.Cwd;
        return await build(new EnvTarget
        {
            ConversationId = id,
            Cwd = cwd,
            Read = this,
        }, context).ConfigureAwait(false);
    }

    /// <summary>把幸存的 <c>running</c> 任务核回 <c>pending</c>；open 的一部分。对应 TS <c>openTasks()</c>。</summary>
    public Task OpenTasksAsync(Context context) => _tasks.OpenAsync(context);

    public void Resume()
    {
        AssertOpen();
        _tasks.Resume();
    }

    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
        => ReadOnLineAsync(() => _storage.GetTaskAsync(id));

    public Task<HarnessInspection> InspectAsync(Context context)
        => ReadOnLineAsync(async () =>
        {
            var inspection = await _tasks.InspectAsync(_options.Registry.Snapshot()).ConfigureAwait(false);
            async Task<List<SubmissionRecord>> ScanAsync(SubmissionStatus status)
            {
                var page = await HarnessUtil.ScanAllAsync(
                    cursor => _storage.ScanSubmissionsAsync(
                        new SubmissionQuery { Status = status },
                        HarnessScan.PageSize, cursor)).ConfigureAwait(false);
                return [.. page];
            }
            var submissions = new List<SubmissionRecord>();
            submissions.AddRange(await ScanAsync(SubmissionStatus.Queued).ConfigureAwait(false));
            submissions.AddRange(await ScanAsync(SubmissionStatus.Placed).ConfigureAwait(false));
            submissions.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            return new HarnessInspection
            {
                Scheduling = inspection.Scheduling,
                Tasks = inspection.Tasks,
                Submissions = submissions,
            };
        });

    public Task<ISubmission?> SubmissionAsync(SubmissionId id, Context context)
        => _submissions.GetAsync(id, context);

    public Task<string> AbortSubmissionAsync(SubmissionId id, Context context, ConversationId? conversationId = null)
        => _submissions.AbortAsync(id, context, conversationId);

    public Task<string> AbortTaskAsync(TaskId<object?> id, Context context) => _tasks.AbortAsync(id, context);

    public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
    {
        _tasks.Resume();
        return _tasks.WaitForTaskAsync(id, context);
    }

    public Task WaitForIdleAsync(Context context)
    {
        _tasks.Resume();
        return _tasks.WaitForIdleAsync(null, context);
    }

    /// <summary>
    /// 汇总每个对话已提交的 <c>pi.usage</c>。每份文档在其自身位点读取；总计只增。
    /// 对应 TS <c>usage()</c>。
    /// </summary>
    public async Task<UsageState> UsageAsync(Context context)
    {
        var conversations = await ReadOnLineAsync(() => HarnessUtil.ScanAllAsync(
            cursor => _storage.ScanConversationsAsync(null, HarnessScan.PageSize, cursor))).ConfigureAwait(false);
        var total = Usage.InitialUsage();
        foreach (var conversation in conversations)
        {
            var state = await SnapshotAsync(Usage.UsageDoc, conversation.Id, context).ConfigureAwait(false);
            if (state is not null) Usage.AddUsageState(total, state);
        }

        return new UsageState
        {
            Models = ReadBucket(total, Usage.ModelsBucket),
            Tools = ReadBucket(total, Usage.ToolsBucket),
        };
    }

    public Task<CommittedWatch<JsonDict>> WatchTaskGraphAsync(Context context) => _taskGraph.WatchAsync(context);

    /// <summary>任务图的图状态快照（spec §9.5）。对应 TS <c>taskGraph()</c>。</summary>
    public Task<AttachedReplicatedState<JsonDict>> TaskGraphAsync(Context context) => _taskGraph.StateAsync(context);

    public Task<IConversation> RootAsync(Context context, AgentChange? agent = null, ConversationInit? init = null)
        => CreateCoreAsync(
            new CreateTarget.Root(), new CreateOptions { Agent = agent, Init = init }, context);

    public async Task<IConversation?> ConversationAsync(ConversationId id, Context context)
    {
        AssertOpen();
        var record = await ReadOnLineAsync(() => _storage.GetConversationAsync(id)).ConfigureAwait(false);
        return record is null ? null : new ConversationImpl(record.Id, _host);
    }

    public Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, Context context)
        => CreateCoreAsync(
            new CreateTarget.Independent(options.Ownership),
            new CreateOptions { Agent = options.Agent, Init = options.Init },
            context);

    /// <summary>封闭受理并等待任务调用 join（在 <see cref="OnBeforeCloseAsync"/> 中）。对应 TS <c>close()</c>。</summary>
    public new Task CloseAsync(Context context)
    {
        _closed = true;
        return base.CloseAsync(context);
    }

    /// <summary>在受理封闭之后、Storage 关闭之前 join 任务调用；不写任何任务结局。对应 TS <c>beforeClose()</c>。</summary>
    protected override Task OnBeforeCloseAsync() => _tasks.JoinAsync();

    private async Task<IConversation> CreateCoreAsync(CreateTarget target, CreateOptions options, Context context)
    {
        AssertOpen();
        var id = await CommitWithAsync(async tx =>
        {
            if (target is CreateTarget.Root
                && await tx.GetConversationAsync(DurableIdConstants.RootConversation).ConfigureAwait(false)
                    is not null)
            {
                return DurableIdConstants.RootConversation;
            }

            var record = target switch
            {
                CreateTarget.Root => await tx.CreateRootConversationAsync().ConfigureAwait(false),
                CreateTarget.Fork fork => await tx.ForkConversationAsync(
                    fork.ParentId, fork.At, fork.Ownership).ConfigureAwait(false),
                CreateTarget.Independent independent => await tx.CreateConversationAsync(
                    independent.Ownership).ConfigureAwait(false),
                _ => throw new InvalidOperationException("unknown create target"),
            };
            if (options.Agent is not null)
            {
                await AgentDocs.ConfigureAsync(tx, record.Id, options.Agent).ConfigureAwait(false);
            }

            if (options.Init is not null) await options.Init(tx, record.Id).ConfigureAwait(false);
            return record.Id;
        }, context).ConfigureAwait(false);
        return new ConversationImpl(id, _host);
    }

    /// <summary>
    /// 每个创建或 fork 对话的提交中的内建创建钩子：空的 <c>pi.live</c>、<c>pi.inbox</c> 与 <c>pi.usage</c>、
    /// 一个新的 <c>pi.provider</c>、该对话的 <c>pi.agent</c>（见 <c>createAgent()</c>），
    /// 然后 <see cref="HarnessOptions.ConversationCreated"/>。
    /// 对应 TS <c>conversationCreated()</c>。
    /// </summary>
    protected override async Task OnConversationCreatedAsync(Transaction tx, ConversationRecord record)
    {
        await tx.DocAsync(Live.LiveDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(Inbox.InboxDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(Usage.UsageDoc, record.Id).ConfigureAwait(false);
        await tx.DocAsync(Pi.Durable.Harness.Provider.ProviderDoc, record.Id).ConfigureAwait(false);
        await AgentDocs.CreateAgentAsync(tx, record).ConfigureAwait(false);
        if (_options.ConversationCreated is { } hook)
        {
            await hook(tx, record).ConfigureAwait(false);
        }
    }

    private void AssertOpen()
    {
        if (_closed) throw HarnessUtil.ClosedError();
    }

    private static IReadOnlyDictionary<string, Pi.Ai.Types.Usage> ReadBucket(
        Dictionary<string, object?> total, string bucket)
    {
        var result = new Dictionary<string, Pi.Ai.Types.Usage>(StringComparer.Ordinal);
        if (total.TryGetValue(bucket, out var value) && value is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, usage) in map)
            {
                if (usage is IReadOnlyDictionary<string, object?> json)
                {
                    result[key] = Usage.FromJson(json);
                }
            }
        }

        return result;
    }
}

/// <summary>
/// 调度器写入的终局落定（faulted / orphaned）的 Harness 清理。
/// 对应 TS <c>settleSchedulerOutcome()</c>（P56 从 <c>live.ts</c> 迁移，此前标注随 scheduler 阶段接入）。
/// </summary>
internal static class LiveSettle
{
    public static async Task SettleSchedulerOutcomeAsync(
        Transaction tx, TaskRecord record, SchedulerOutcome outcome)
    {
        if (record.Kind == Live.ToolTaskKind)
        {
            var live = await tx.DocAsync(Live.LiveDoc, record.ConversationId).ConfigureAwait(false);
            var slot = Live.ToolSlotOf(live, record.Id);
            if (slot is not null) Live.FinishSlot(live, slot, null);
            return;
        }

        if (record.Kind == Live.CompactionTaskKind)
        {
            var live = await tx.DocAsync(Live.LiveDoc, record.ConversationId).ConfigureAwait(false);
            Live.RemoveCompactionStatus(live, record.Id);
            return;
        }

        if (!Live.RunTaskKinds.Contains(record.Kind)) return;
        var runLive = await tx.DocAsync(Live.LiveDoc, record.ConversationId).ConfigureAwait(false);
        if (Live.RunTaskId(runLive) != record.Id) return;
        await Generation.ConvertPartialAsync(tx, runLive, record.ConversationId).ConfigureAwait(false);
        Live.EndRun(
            tx,
            runLive,
            record.Id,
            outcome.Status == TaskOutcomeStatus.Faulted
                ? new SubmissionSettlement.Unanswered("faulted", outcome.Error?.Message)
                : new SubmissionSettlement.Unanswered(outcome.Reason ?? "unknown"));
    }
}

/// <summary>
/// 调用内绑定的对话句柄，供任务与工具使用。每个操作（及其返回提交的每个操作）先检查调用，
/// 并在其信号下运行，故调用结束后拒绝；已受理的工作保持持久。
/// 对应 TS <c>boundConversation()</c>。
/// </summary>
internal static class BoundConversations
{
    public static IConversationHandle BoundConversation(
        ConversationId id, InvocationBinding binding, Submissions submissions, TaskScheduler tasks)
        => new Handle(id, binding, submissions, tasks);

    private sealed class Handle(
        ConversationId id,
        InvocationBinding binding,
        Submissions submissions,
        TaskScheduler tasks) : IConversationHandle
    {
        public ConversationId Id => id;

        public async Task<ISubmission> SubmitAsync(SubmissionDraft.Input submission, Context context)
        {
            binding.Check();
            var admitted = await submissions.SubmitAsync(
                id, submission, ContextSignals.WithAbortSignal(binding.Signal, context)).ConfigureAwait(false);
            return new BoundSubmission(
                admitted.Id,
                callContext =>
                {
                    binding.Check();
                    return admitted.StatusAsync(ContextSignals.WithAbortSignal(binding.Signal, callContext));
                },
                callContext =>
                {
                    binding.Check();
                    return admitted.WaitAsync(ContextSignals.WithAbortSignal(binding.Signal, callContext));
                },
                callContext =>
                {
                    binding.Check();
                    return admitted.AbortAsync(ContextSignals.WithAbortSignal(binding.Signal, callContext));
                });
        }

        public async Task AbortAsync(Context context, ConversationAbortOptions? options = null)
        {
            binding.Check();
            await tasks.AbortConversationAsync(id, options?.Background == true,
                ContextSignals.WithAbortSignal(binding.Signal, context)).ConfigureAwait(false);
        }

        public async Task WaitForIdleAsync(Context context)
        {
            binding.Check();
            await tasks.WaitForIdleAsync(id, ContextSignals.WithAbortSignal(binding.Signal, context))
                .ConfigureAwait(false);
        }
    }

    private sealed class BoundSubmission(
        SubmissionId id,
        Func<Context, Task<SubmissionRecord>> status,
        Func<Context, Task<SettledSubmissionRecord>> wait,
        Func<Context, Task<string>> abort) : ISubmission
    {
        public SubmissionId Id => id;

        public Task<SubmissionRecord> StatusAsync(Context context) => status(context);

        public Task<SettledSubmissionRecord> WaitAsync(Context context) => wait(context);

        public Task<string> AbortAsync(Context context) => abort(context);
    }
}

/// <summary>
/// 一个 Session 上的持久 agent harness（静态门面）。
/// 对应 TS <c>Harness.open()</c>。
/// </summary>
public static class Harness
{
    /// <summary>
    /// 在存储之上打开一个 Harness。注册表可在 Harness 运行期间持续变化。
    /// 对应 TS <c>Harness.open()</c>。
    /// </summary>
    public static async Task<HarnessImpl> OpenAsync(IStorage storage, HarnessOptions options, Context context)
    {
        context.AbortSignal?.ThrowIfCancellationRequested();
        var snapshot = options.Registry.Snapshot();
        var missing = Registry.BuiltinTasks
            .Where(task => snapshot.Task(task.Name) is null)
            .Select(task => task.Name)
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Registry lacks built-in tasks {string.Join(", ", missing)}; create it with createRegistry()");
        }

        var harness = new HarnessImpl(storage, options, context);
        try
        {
            await harness.OpenTasksAsync(context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 调用方的 context 可能正是 open 失败的原因：不用它 close，并重新抛出 open 错误。
            try
            {
                await harness.CloseAsync(ContextSignals.WithoutAbortSignal(context)).ConfigureAwait(false);
            }
            catch (Exception closeError)
            {
                options.OnReport?.Invoke(closeError);
            }

            throw;
        }

        return harness;
    }

    /// <summary>便捷包装：调用 <see cref="BoundConversations.BoundConversation"/>。</summary>
    internal static IConversationHandle BoundConversation(
        ConversationId id, InvocationBinding binding, Submissions submissions, TaskScheduler tasks)
        => BoundConversations.BoundConversation(id, binding, submissions, tasks);
}
