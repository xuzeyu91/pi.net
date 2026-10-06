using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Session;

/// <summary>暂存的提交变更：结算，或把排队提交放置到其条目。对应 TS <c>SubmissionChange</c>。</summary>
internal abstract record SubmissionChange
{
    private SubmissionChange() { }

    /// <summary>放置（输入 → placed，写入 → done）。</summary>
    public sealed record Placed(EntryId Entry) : SubmissionChange;

    /// <summary>结算为 done。</summary>
    public sealed record Done(EntryId Answer) : SubmissionChange;

    /// <summary>结算为 unanswered。</summary>
    public sealed record Unanswered(string Reason, object? Detail) : SubmissionChange;
}

/// <summary>
/// 应用一次变更后的完整记录。放置把排队的输入变 placed、排队的写入变 done；
/// 只有已放置的输入可被应答；已结算的记录保持不变。
/// 对应 TS <c>applySubmissionChange</c>。
/// </summary>
internal static class SubmissionChanges
{
    public static SubmissionRecord Apply(SubmissionRecord current, SubmissionChange change)
    {
        if (IsSettled(current)) return current;
        switch (change)
        {
            case SubmissionChange.Placed placed:
            {
                if (StatusOf(current) != SubmissionStatus.Queued)
                    throw new InvalidOperationException($"Submission {current.Id.Value} is not queued");
                var status = current.Type == "input" ? SubmissionStatus.Placed : SubmissionStatus.Done;
                return WithStatus(current, status, entry: placed.Entry);
            }
            case SubmissionChange.Done done:
            {
                if (StatusOf(current) != SubmissionStatus.Placed)
                    throw new InvalidOperationException($"Submission {current.Id.Value} is not a placed input");
                return WithStatus(current, SubmissionStatus.Done, answer: done.Answer);
            }
            case SubmissionChange.Unanswered unanswered:
            {
                // 排队与已放置记录不带 answer / reason / detail；unanswered 的输入保留其条目。
                return WithStatus(
                    current, SubmissionStatus.Unanswered, reason: unanswered.Reason, detail: unanswered.Detail);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private static bool IsSettled(SubmissionRecord record)
        => record is SubmissionRecord.InputRecord { Status: SubmissionStatus.Done or SubmissionStatus.Unanswered }
            or SubmissionRecord.WriteRecord { Status: SubmissionStatus.Done or SubmissionStatus.Unanswered };

    private static SubmissionStatus StatusOf(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => throw new InvalidOperationException("Unknown submission record kind"),
    };

    private static SubmissionRecord WithStatus(
        SubmissionRecord record,
        SubmissionStatus status,
        EntryId? entry = null,
        EntryId? answer = null,
        string? reason = null,
        object? detail = null)
        => record switch
        {
            SubmissionRecord.InputRecord input => input with
            {
                Status = status,
                Entry = entry ?? input.Entry,
                Answer = answer ?? input.Answer,
                Reason = reason ?? input.Reason,
                Detail = detail ?? input.Detail,
            },
            SubmissionRecord.WriteRecord write => write with
            {
                Status = status,
                Entry = entry ?? write.Entry,
                Reason = reason ?? write.Reason,
                Detail = detail ?? write.Detail,
            },
            _ => throw new InvalidOperationException("Unknown submission record kind"),
        };
}

/// <summary>Session 跟踪器缓存拥有的一个已提交文档化身。对应 TS <c>LoadedDocument</c>。</summary>
public sealed class LoadedDocument
{
    public required string AddressId { get; init; }

    public required DocumentRecord Record { get; init; }

    /// <summary>持久化定义版本；被跟踪值仅在内存迁移时更旧。</summary>
    public int StoredVersion { get; set; }

    /// <summary>被跟踪值形状对应的定义版本；以其他版本访问会从 Storage 重载。</summary>
    public required int ValueVersion { get; init; }

    /// <summary>最新 base 之后已存储的 delta 数；采纳时推进，下一次谓词调用无需读取。</summary>
    public long DeltasSinceBase { get; set; }

    public required Tracker<IReadOnlyDictionary<string, object?>> Tracker { get; init; }
}

/// <summary>事务在持有变更线期间使用的 Session 服务。对应 TS <c>TransactionHost</c>。</summary>
public interface ITransactionHost
{
    IStorage Storage { get; }

    /// <summary>不加载直接返回缓存的当前化身。</summary>
    LoadedDocument? Cached(string addressId);

    /// <summary>返回缓存的当前化身，必要时冷加载并迁移。</summary>
    Task<LoadedDocument?> LoadAsync(
        DurableDocuments.AnyDocDefinition definition,
        string addressId,
        DocumentAddress address,
        Context context);

    /// <summary>安装新提交的化身。</summary>
    void Install(LoadedDocument document);

    /// <summary>退役化身若仍是其地址上的缓存占据者则移除。</summary>
    void Evict(string addressId, DocumentId recordId);

    /// <summary>把每个新建或 fork 对话的内置写暂存进其创建事务。</summary>
    Task ConversationCreatedAsync(Transaction tx, ConversationRecord record);
}

/// <summary>本事务触及的每个任务的已提交与候选状态。对应 TS <c>TransactionTask</c>。</summary>
internal sealed class TransactionTask
{
    public Task<TaskRecord?>? CommittedRead { get; set; }

    public StagedTaskWrite? Write { get; set; }

    public ConversationId? PublicationConversationId { get; set; }
}

/// <summary>任务的暂存写（create = 新建，replace = 整体替换）。</summary>
internal sealed record StagedTaskWrite(string Kind, TaskRecord Record);

/// <summary>一个暂存文档化身的存储/缓存来源。对应 TS <c>DocumentTarget</c>。</summary>
internal abstract record DocumentTarget
{
    private DocumentTarget() { }

    public sealed record Loaded(LoadedDocument Document) : DocumentTarget;

    public sealed record Created(
        DocumentCreate Record, int Version, Tracker<IReadOnlyDictionary<string, object?>> Tracker) : DocumentTarget;

    public sealed record ForkCopy(DocumentCreate Record, DocumentCopySource Source) : DocumentTarget;

    public sealed record RetireOnly(DocumentRecord Record) : DocumentTarget;
}

/// <summary>本事务取得、创建或退役的一个文档化身。对应 TS <c>DocumentEntry</c>。</summary>
internal sealed class DocumentEntry
{
    public required string AddressId { get; init; }

    public required DocumentAddress Address { get; init; }

    /// <summary>无定义 fork 拷贝与终态任务扫描发现的退役条目缺省。</summary>
    public DurableDocuments.AnyDocDefinition? Definition { get; set; }

    /// <summary>公开取得的备忘；仅元数据退役缺省。</summary>
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change>? DraftPromise { get; set; }

    /// <summary>取得或退役查找命中受影响化身之后设置。</summary>
    public DocumentTarget? Target { get; set; }

    public Tracker<IReadOnlyDictionary<string, object?>>.Change? Change { get; set; }

    public Tracker<IReadOnlyDictionary<string, object?>>.Prepared? Prepared { get; set; }

    public bool RetireOnCommit { get; set; }
}

/// <summary>文档计划的 prepared 变更信息。对应 TS <c>DocumentPlan["change"]</c>。</summary>
internal sealed class PlannedChange
{
    public required Tracker<IReadOnlyDictionary<string, object?>> Tracker { get; init; }

    public required Tracker<IReadOnlyDictionary<string, object?>>.Prepared Prepared { get; init; }

    public required int Version { get; init; }

    /// <summary>本变更更新的缓存化身；创建时缺省。</summary>
    public LoadedDocument? Loaded { get; init; }

    public DurableDocuments.AnyDocDefinition? Definition { get; init; }
}

/// <summary>
/// 一个暂存化身写入与发布的内容，在 Storage 受理前一次性决定，采纳只负责应用。
/// 对应 TS <c>DocumentPlan</c>（<c>record</c> 为已提交化身的 <c>DocumentRecord</c>，
/// 或新化身的 <c>DocumentCreate</c>）。
/// </summary>
internal sealed class DocumentPlan
{
    public required string AddressId { get; init; }

    /// <summary>新化身的创建记录（与 <see cref="CommittedRecord"/> 互斥）。</summary>
    public DocumentCreate? CreateRecord { get; set; }

    /// <summary>已提交化身（与 <see cref="CreateRecord"/> 互斥）。</summary>
    public DocumentRecord? CommittedRecord { get; set; }

    public bool Retire { get; set; }

    /// <summary>创建、拷贝或变更内容；仅退役时不写。</summary>
    public StorageWrite? Content { get; set; }

    /// <summary>被跟踪化身的 prepared 变更；fork 拷贝与仅退役条目缺省。</summary>
    public PlannedChange? Change { get; set; }

    /// <summary>Storage 受理前解析，采纳因此保持同步。</summary>
    public ConversationId? ConversationId { get; set; }
}

/// <summary>
/// 一次 Session 提交回调的事务。对应 TS <c>session/transaction.ts</c> 的 <c>Transaction</c>。
/// 每个异步操作都被跟踪，使回调结算能够拒绝并排空未完成的工作；Session 只调用一个结算方法，
/// 然后在 Storage 成功后丢弃或采纳 prepared 变更。
/// </summary>
public sealed class Transaction : ITx
{
    private const int InternalScanPageSize = 256;

    private readonly ITransactionHost _host;
    private readonly Context _context;
    private readonly TransactionScope _scope;
    private readonly HashSet<Task> _pendingOperations = [];
    private bool _sealed;
    private bool _hasTableWrite;

    /// <summary>原子批次；对话与条目写立即暂存，任务与文档写稍后装配。</summary>
    private readonly List<StorageWrite> _writes = [];
    private readonly HashSet<ConversationId> _createdConversationIds = [];
    private readonly HashSet<ConversationId> _forkSourceConversationIds = [];
    private readonly HashSet<DocumentId> _forkSourceDocumentIds = [];

    /// <summary>公开读、候选写或文档属主查找触及的每个任务。</summary>
    private readonly Dictionary<TaskId<object?>, TransactionTask> _tasksById = [];

    /// <summary>本事务创建的提交，按 ID。</summary>
    private readonly Dictionary<SubmissionId, SubmissionRecord> _submissions = [];

    /// <summary>暂存顺序的提交结算与放置；装配期间按最新候选记录解析。</summary>
    private readonly List<(SubmissionId Id, SubmissionChange Change)> _submissionChanges = [];

    /// <summary>装配期间构建的每个暂存化身的写与发布计划。</summary>
    private readonly List<DocumentPlan> _plans = [];

    /// <summary>暂存顺序的每个文档取得或退役标记。</summary>
    private readonly List<DocumentEntry> _documents = [];

    /// <summary>每个逻辑地址上的最新事务内化身或退役标记。</summary>
    private readonly Dictionary<string, DocumentEntry> _latestDocumentByAddress = [];

    internal Transaction(ITransactionHost host, Context context, TransactionScope? scope)
    {
        _host = host;
        _context = context;
        _scope = scope ?? new TransactionScope();
    }

    // ─── 表读 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id)
        => ReadAsync("conversation", () => _host.Storage.GetConversationAsync(id));

    /// <inheritdoc />
    public Task<EntryRecord?> GetEntryAsync(EntryId id)
        => ReadAsync("entry", async () => (await _host.Storage.GetEntryAsync(id).ConfigureAwait(false))?.Entry);

    /// <inheritdoc />
    public Task<TypedEntry<TData>?> GetEntryAsync<TData>(Entry<TData> token, EntryId id)
        => ReadAsync("entry", async () =>
        {
            var entry = (await _host.Storage.GetEntryAsync(id).ConfigureAwait(false))?.Entry;
            if (entry is null || entry.Kind != token.Kind) return null;
            return new TypedEntry<TData>
            {
                Id = entry.Id,
                ConversationId = entry.ConversationId,
                Kind = entry.Kind,
                Model = entry.Model,
                Data = (TData?)entry.Data,
                Head = entry.Head,
                Edits = entry.Edits,
                ByTaskId = entry.ByTaskId,
            };
        });

    /// <inheritdoc />
    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id) => ReadAsync("task", () => CommittedTaskAsync(id));

    /// <inheritdoc />
    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => ReadAsync("scanConversations", () => _host.Storage.ScanConversationsAsync(query, limit, cursor));

    /// <inheritdoc />
    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => ReadAsync("scanEntries", () => _host.Storage.ScanEntriesAsync(query, limit, cursor));

    /// <inheritdoc />
    public Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId)
        => ReadAsync("latestHeadMarker", () => _host.Storage.FindLatestHeadMarkerAsync(conversationId, null));

    /// <inheritdoc />
    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => ReadAsync("scanTasks", () => _host.Storage.ScanTasksAsync(query, limit, cursor));

    /// <summary>内部：已提交提交记录。</summary>
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
        => ReadAsync("submission", () => _host.Storage.GetSubmissionAsync(id));

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
        => ReadAsync(
            "submissionByRequest", () => _host.Storage.GetSubmissionByRequestAsync(conversationId, requestId));

    // ─── 表写 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<ConversationRecord> CreateConversationAsync(ConversationOwnership ownership)
        => WriteAsync(() => StageConversationAsync(null, ownership, reservedId: null));

    /// <summary>内部：保留根身份的最终形态引导路径。</summary>
    public Task<ConversationRecord> CreateRootConversationAsync()
        => WriteAsync(() => StageConversationAsync(
            null, new ConversationOwnership.Ownerless(), DurableIdConstants.RootConversation));

    /// <inheritdoc />
    public Task<ConversationRecord> ForkConversationAsync(
        ConversationId parentConversationId, EntryId at, ConversationOwnership ownership)
        => WriteAsync(() => StageConversationAsync(
            new ConversationParent { ConversationId = parentConversationId, At = at }, ownership, reservedId: null));

    private async Task<ConversationRecord> StageConversationAsync(
        ConversationParent? parent, ConversationOwnership ownership, ConversationId? reservedId)
    {
        var ownerTaskId = ownership is ConversationOwnership.TaskOwned taskOwned ? taskOwned.TaskId : (TaskId<object?>?)null;
        var id = reservedId ?? await _host.Storage.MintIdAsync<ConversationId>().ConfigureAwait(false);
        AssertOpen();
        ConversationOwner? owner = null;
        if (ownerTaskId is not null)
        {
            var task = await CurrentTaskAsync(ownerTaskId.Value).ConfigureAwait(false);
            AssertOpen();
            if (task is null)
                throw new InvalidOperationException($"Conversation owner task {ownerTaskId.Value} does not exist");
            owner = new ConversationOwner { ConversationId = task.ConversationId, TaskId = ownerTaskId.Value };
        }
        var record = new ConversationRecord
        {
            Id = id,
            Parent = parent,
            Owner = owner,
        };
        var copies = parent is null
            ? []
            : await Forks.PrepareForkDocumentCopiesAsync(
                _host.Storage, parent.ConversationId, parent.At, id, _context).ConfigureAwait(false);
        AssertOpen();
        foreach (var copy in copies)
        {
            _forkSourceDocumentIds.Add(copy.Source.Id);
            var address = AddressOfCreate(copy.Record);
            var entry = new DocumentEntry
            {
                AddressId = DurableDocuments.AddressId(address),
                Address = address,
                Target = new DocumentTarget.ForkCopy(copy.Record, copy.Source),
                RetireOnCommit = false,
            };
            _documents.Add(entry);
            _latestDocumentByAddress[entry.AddressId] = entry;
        }
        if (parent is not null) _forkSourceConversationIds.Add(parent.ConversationId);
        _createdConversationIds.Add(id);
        _writes.Add(new StorageWrite.Conversation(record));
        await _host.ConversationCreatedAsync(this, record).ConfigureAwait(false);
        AssertOpen();
        return record;
    }

    /// <inheritdoc />
    public Task<EntryRecord> AppendEntryAsync(ConversationId conversationId, EntryDraft value)
        => WriteAsync(() => AppendEntryCoreAsync(conversationId, value));

    /// <inheritdoc />
    public Task<TypedEntry<TData>> AppendEntryAsync<TData>(
        Entry<TData> token, ConversationId conversationId, TypedEntryDraft<TData> value)
        => WriteAsync(async () =>
        {
            var record = await AppendEntryCoreAsync(conversationId, new EntryDraft
            {
                Kind = token.Kind,
                Model = value.Model,
                Data = value.Data,
                Head = value.Head,
                HeadIsSelf = value.HeadIsSelf,
                Edits = value.Edits,
            }).ConfigureAwait(false);
            return new TypedEntry<TData>
            {
                Id = record.Id,
                ConversationId = record.ConversationId,
                Kind = record.Kind,
                Model = record.Model,
                Data = (TData?)record.Data,
                Head = record.Head,
                Edits = record.Edits,
                ByTaskId = record.ByTaskId,
            };
        });

    private async Task<EntryRecord> AppendEntryCoreAsync(ConversationId conversationId, EntryDraft value)
    {
        await RequireConversationAsync(conversationId).ConfigureAwait(false);
        AssertOpen();
        var id = await _host.Storage.MintIdAsync<EntryId>().ConfigureAwait(false);
        AssertOpen();
        // 对应 TS：head === "self" 时替换为新分配 ID；未定义的 head / byTaskId 由 copy 省略。
        var record = new EntryRecord
        {
            Id = id,
            ConversationId = conversationId,
            Kind = value.Kind,
            Model = value.Model,
            Data = value.Data,
            Head = value.HeadIsSelf ? id : value.Head,
            Edits = value.Edits,
            ByTaskId = _scope.TaskId,
        };
        _writes.Add(new StorageWrite.Entry(record));
        return record;
    }

    /// <inheritdoc />
    public Task<TaskId<TResult>> CreateTaskAsync<TInput, TState, TResult>(
        DurableTask<TInput, TState, TResult> task, TInput? input, TaskOptions options)
        => WriteAsync(async () =>
        {
            var ownership = options.Ownership;
            TaskRecord? owner = null;
            if (ownership is TaskOwnership.TaskOwner taskOwner)
            {
                // 装配期间会对照属主的最终候选再次校验。
                owner = await CurrentTaskAsync(taskOwner.TaskId).ConfigureAwait(false);
                AssertOpen();
                if (owner is null)
                    throw new InvalidOperationException($"Task owner {taskOwner.TaskId.Value} does not exist");
                if (options.Background) throw new ArgumentException("A child task cannot be background");
                if (options.ConversationId is not null && options.ConversationId != owner.ConversationId)
                {
                    throw new InvalidOperationException(
                        $"A child task lives in its owner's conversation {owner.ConversationId.Value}");
                }
            }
            var conversationId = owner?.ConversationId ?? options.ConversationId ?? _scope.ConversationId;
            if (conversationId is null)
                throw new ArgumentException("Tx.createTask() requires options.conversationId");
            await RequireConversationAsync(conversationId.Value).ConfigureAwait(false);
            AssertOpen();
            var definition = task.Definition;
            var checkpoint = definition.Initial(input);
            // mint 统一用擦除键（TS 的 TaskId 品牌参数仅类型层面）。
            var minted = await _host.Storage.MintIdAsync<TaskId<object?>>().ConfigureAwait(false);
            var id = TaskId<TResult>.From(minted.Value);
            AssertOpen();
            var record = new TaskRecord
            {
                Id = TaskId<object?>.From(id.Value),
                ConversationId = conversationId.Value,
                Kind = definition.Name,
                Version = definition.Version,
                Input = input,
                Owner = owner?.Id,
                Background = options.Background,
                AbortRequested = false,
                State = new TaskState { Status = Types.TaskStatus.Pending, Checkpoint = checkpoint },
            };
            _tasksById[record.Id] = new TransactionTask { Write = new StagedTaskWrite("create", record) };
            return id;
        });

    /// <inheritdoc />
    public Task<SubmissionRecord> CreateSubmissionAsync(SubmissionCreate create)
        => WriteAsync(async () =>
        {
            await RequireConversationAsync(create.ConversationId).ConfigureAwait(false);
            AssertOpen();
            var id = await _host.Storage.MintIdAsync<SubmissionId>().ConfigureAwait(false);
            AssertOpen();
            var record = create switch
            {
                SubmissionCreate.InputCreate input => (SubmissionRecord)new SubmissionRecord.InputRecord
                {
                    Id = id,
                    ConversationId = input.ConversationId,
                    RequestId = input.RequestId,
                    Status = input.Status,
                    Entry = input.Entry,
                    Answer = input.Answer,
                    Reason = input.Reason,
                    Detail = input.Detail,
                },
                SubmissionCreate.WriteCreate write => new SubmissionRecord.WriteRecord
                {
                    Id = id,
                    ConversationId = write.ConversationId,
                    RequestId = write.RequestId,
                    Status = write.Status,
                    Entry = write.Entry,
                    Reason = write.Reason,
                    Detail = write.Detail,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(create)),
            };
            _submissions[id] = record;
            return record;
        });

    /// <inheritdoc />
    public void SettleSubmission(SubmissionId id, SubmissionSettlement settlement)
    {
        AssertOpen();
        _hasTableWrite = true;
        _submissionChanges.Add((id, settlement switch
        {
            SubmissionSettlement.Done done => new SubmissionChange.Done(done.Answer),
            SubmissionSettlement.Unanswered unanswered => new SubmissionChange.Unanswered(
                unanswered.Reason, unanswered.Detail),
            _ => throw new ArgumentOutOfRangeException(nameof(settlement)),
        }));
    }

    /// <inheritdoc />
    public void PlaceSubmission(SubmissionId id, EntryId entry)
    {
        AssertOpen();
        _hasTableWrite = true;
        _submissionChanges.Add((id, new SubmissionChange.Placed(entry)));
    }

    /// <summary>内部：完全替换一个任务记录。任务通过其运行时变更自身状态。</summary>
    public void SetTask(TaskRecord value)
    {
        AssertOpen();
        _hasTableWrite = true;
        var task = TaskEntry(value.Id);
        var candidate = task.Write?.Record;
        if (candidate?.State.Status == Types.TaskStatus.Terminal)
        {
            throw new InvalidOperationException($"Task {value.Id.Value} already has a terminal candidate");
        }
        if (candidate is not null && candidate.ConversationId != value.ConversationId)
        {
            throw new InvalidOperationException($"Task {value.Id.Value} cannot change conversations");
        }
        // 对应 TS copyJson：C# 记录不可变且字段已是 JSON 安全值，直接持有引用。
        task.Write = new StagedTaskWrite(task.Write?.Kind == "create" ? "create" : "replace", value);
    }

    /// <summary>内部：本事务迄今创建或替换的任务候选记录。</summary>
    public IReadOnlyList<TaskRecord> StagedTasks()
    {
        var records = new List<TaskRecord>();
        foreach (var task in _tasksById.Values)
        {
            if (task.Write is not null) records.Add(task.Write.Record);
        }
        return records;
    }

    /// <summary>内部：本事务迄今创建或 fork 的对话。</summary>
    public IReadOnlyList<ConversationRecord> StagedConversations()
    {
        var records = new List<ConversationRecord>();
        foreach (var write in _writes)
        {
            if (write is StorageWrite.Conversation conversation) records.Add(conversation.Value);
        }
        return records;
    }

    // ─── 文档 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(DocToken<T> token)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), []);

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(
        DocToken<T> token, ConversationId conversationId)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value]);

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(
        DocToken<T> token, TaskId<object?> taskId)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value]);

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, TSeed? seed)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), [key, seed]);

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, TSeed? seed)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value, key, seed]);

    /// <inheritdoc />
    public Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, TSeed? seed)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value, key, seed]);

    private Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocCoreAsync(
        DurableDocuments.AnyDocDefinition definition, object?[] args)
    {
        try
        {
            AssertOpen();
            var resolved = DurableDocuments.ResolveAddress(definition, args);
            AssertTaskDocumentsOpen(resolved);
            _latestDocumentByAddress.TryGetValue(resolved.Id, out var latest);
            if (latest is not null && !latest.RetireOnCommit)
            {
                if (latest.DraftPromise is not null) return latest.DraftPromise;
                if (latest.Target is DocumentTarget.ForkCopy forkCopy)
                {
                    latest.DraftPromise = Track(AcquireForkCopyAsync(latest, definition, forkCopy));
                    return latest.DraftPromise;
                }
            }
            // 对应 TS：family 时 seed = copyJson(args[resolved.nextArgument])。
            var seed = definition.Family ? Json.CopyJson(args[resolved.NextArgument]) : null;
            var docEntry = new DocumentEntry
            {
                AddressId = resolved.Id,
                Address = resolved.Address,
                Definition = definition,
                RetireOnCommit = false,
            };
            _documents.Add(docEntry);
            _latestDocumentByAddress[docEntry.AddressId] = docEntry;
            // 在 await 之前捕获退役标记，使未完成的旧取得与其替换保持可区分。
            docEntry.DraftPromise = Track(AcquireAsync(docEntry, seed, latest?.RetireOnCommit == true));
            return docEntry.DraftPromise;
        }
        catch (Exception error)
        {
            return Task.FromException<Tracker<IReadOnlyDictionary<string, object?>>.Change>(error);
        }
    }

    /// <inheritdoc />
    public Task RetireDocAsync<T>(DocToken<T> token)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), []);

    /// <inheritdoc />
    public Task RetireDocAsync<T>(DocToken<T> token, ConversationId conversationId)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value]);

    /// <inheritdoc />
    public Task RetireDocAsync<T>(DocToken<T> token, TaskId<object?> taskId)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value]);

    /// <inheritdoc />
    public Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, string key)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), [key]);

    /// <inheritdoc />
    public Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value, key]);

    /// <inheritdoc />
    public Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key)
        where T : class, IReadOnlyDictionary<string, object?>
        => RetireDocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value, key]);

    private Task RetireDocCoreAsync(DurableDocuments.AnyDocDefinition definition, object?[] args)
    {
        try
        {
            AssertOpen();
            var resolved = DurableDocuments.ResolveAddress(definition, args);
            if (_latestDocumentByAddress.TryGetValue(resolved.Id, out var latest))
            {
                if (latest.RetireOnCommit) return Task.CompletedTask;
                if (latest.Target is DocumentTarget.ForkCopy forkCopy)
                {
                    DurableDocuments.CheckRecordScope(definition, CreateRecordOf(forkCopy.Record));
                    latest.RetireOnCommit = true;
                    return Task.CompletedTask;
                }
                if (latest.DraftPromise is not null)
                {
                    // 取得草稿的退役在退役前持久化其最终内容。
                    latest.RetireOnCommit = true;
                    return Track(latest.DraftPromise);
                }
            }
            var entry = new DocumentEntry
            {
                AddressId = resolved.Id,
                Address = resolved.Address,
                Definition = definition,
                RetireOnCommit = true,
            };
            _documents.Add(entry);
            _latestDocumentByAddress[entry.AddressId] = entry;
            return Track(FindRetirementAsync(entry));
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private async Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> AcquireAsync(
        DocumentEntry entry, object? seed, bool skipLoad)
    {
        var definition = entry.Definition!;
        var loaded = skipLoad
            ? null
            : await _host.LoadAsync(definition, entry.AddressId, entry.Address, _context).ConfigureAwait(false);
        AssertOpen();
        if (loaded is not null)
        {
            DurableDocuments.CheckRecordScope(definition, loaded.Record);
            DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
            entry.Target = new DocumentTarget.Loaded(loaded);
            entry.Change = loaded.Tracker.BeginChange();
            return entry.Change;
        }
        var scope = entry.Address.Scope;
        if (scope is DocumentScope.ConversationScope conversation)
        {
            await RequireConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        }
        if (scope is DocumentScope.TaskScope taskScope)
        {
            var task = await CurrentTaskAsync(taskScope.TaskId).ConfigureAwait(false);
            if (task is null) throw new InvalidOperationException($"Task {taskScope.TaskId.Value} does not exist");
            if (task.State.Status == Types.TaskStatus.Terminal)
                throw new InvalidOperationException($"Task {taskScope.TaskId.Value} is terminal");
        }
        AssertOpen();
        // 对应 TS：copyJson(family ? definition.initial(seed) : definition.initial())。
        var value = Json.CopyJson(definition.Initial(seed))
            as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException("Document value must be a JSON object");
        var id = await _host.Storage.MintIdAsync<DocumentId>().ConfigureAwait(false);
        AssertOpen();
        var tracker = new Tracker<IReadOnlyDictionary<string, object?>>(value);
        entry.Target = new DocumentTarget.Created(
            DurableDocuments.DocumentCreate(definition, entry.Address, id), definition.Version, tracker);
        entry.Change = tracker.BeginChange();
        return entry.Change;
    }

    private async Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> AcquireForkCopyAsync(
        DocumentEntry entry,
        DurableDocuments.AnyDocDefinition definition,
        DocumentTarget.ForkCopy target)
    {
        var stored = await _host.Storage.GetDocumentAsync(target.Source.Id, target.Source.At).ConfigureAwait(false);
        AssertOpen();
        if (stored is null)
        {
            throw new InvalidOperationException(
                $"Fork source document {target.Source.Id.Value} cannot be read");
        }
        if (stored.Record.Scope is not DocumentScope.ConversationScope
            || stored.Record.Kind != target.Record.Kind
            || stored.Record.Key != target.Record.Key
            || stored.Record.History != target.Record.History
            || stored.Record.Fork != target.Record.Fork)
        {
            throw new InvalidOperationException(
                $"Fork source document {target.Source.Id.Value} does not match the copied record");
        }
        var value = DurableDocuments.MaterializeDocumentValue(
            definition, CreateRecordOf(target.Record), stored.Version, stored.Value);
        var tracker = new Tracker<IReadOnlyDictionary<string, object?>>(value);
        entry.Definition = definition;
        entry.Target = new DocumentTarget.Created(target.Record, definition.Version, tracker);
        entry.Change = tracker.BeginChange();
        return entry.Change;
    }

    private async Task FindRetirementAsync(DocumentEntry entry)
    {
        var record = _host.Cached(entry.AddressId)?.Record
            ?? await _host.Storage.FindDocumentAsync(entry.Address, new DocumentPoint.Current())
                .ConfigureAwait(false);
        AssertOpen();
        if (record is null) return;
        DurableDocuments.CheckRecordScope(entry.Definition!, record);
        entry.Target = new DocumentTarget.RetireOnly(record);
    }

    // ─── 结算 ───────────────────────────────────────────────────────────────

    /// <summary>回调失败后封闭：放弃每个变更并观察每个未完成操作。</summary>
    public async Task SettleFailureAsync()
    {
        _sealed = true;
        AbortChanges();
        await DrainPendingOperationsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 回调成功后封闭、物化每个未决变更并装配原子批次。任何失败都会在 Storage 受理前放弃全部变更。
    /// </summary>
    public async Task<IReadOnlyList<StorageWrite>> SettleSuccessAsync()
    {
        _sealed = true;
        if (_pendingOperations.Count > 0)
        {
            AbortChanges();
            await DrainPendingOperationsAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "Session commit callback settled before its pending Tx operations");
        }
        try
        {
            // 同步物化每个未决变更；这使每个草稿作废。
            foreach (var document in _documents)
            {
                if (document.Change is not null) document.Prepared = document.Change.Prepare();
            }
            return await AssembleAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            AbortChanges();
            throw;
        }
    }

    /// <summary>Storage 失败后或无需写入时放弃每个 prepared 变更。</summary>
    public void Discard() => AbortChanges();

    /// <summary>Storage 成功后按指针交换采纳每个 prepared 变更，并描述发布。对应 TS <c>adopt</c>。</summary>
    public List<DocumentCommitChange> Adopt(Seq seq)
    {
        var publications = new List<DocumentCommitChange>();
        foreach (var plan in _plans)
        {
            var committed = plan.CommittedRecord is not null;
            var record = committed
                ? plan.CommittedRecord!
                : RecordFromCreate(plan.CreateRecord!, seq);
            if (plan.Retire) record = record with { RetiredAt = seq };
            var change = plan.Change;
            if (change is not null)
            {
                // 新化身除非同批退役即采纳；已加载化身仅在变更过时采纳。
                if (change.Loaded is null ? !plan.Retire : change.Prepared.Ops.Count > 0)
                    change.Tracker.Adopt(change.Prepared);
                else
                    change.Prepared.Abort();
                if (change.Loaded is not null)
                {
                    if (change.Loaded.StoredVersion < change.Version) change.Loaded.StoredVersion = change.Version;
                    if (plan.Content is StorageWrite.DocumentChange documentChange)
                    {
                        if (documentChange.Content is DocumentContent.Base) change.Loaded.DeltasSinceBase = 0;
                        else change.Loaded.DeltasSinceBase++;
                    }
                }
                else if (!plan.Retire)
                {
                    _host.Install(new LoadedDocument
                    {
                        AddressId = plan.AddressId,
                        Record = record,
                        StoredVersion = change.Version,
                        ValueVersion = change.Version,
                        DeltasSinceBase = 0,
                        Tracker = change.Tracker,
                    });
                }
            }
            var conversationId = plan.ConversationId;
            if (plan.Retire)
            {
                if (committed) _host.Evict(plan.AddressId, record.Id);
                publications.Add(new DocumentCommitChange.Document(
                    record, conversationId, null, null, Array.Empty<DeltaOp>()));
            }
            else if (plan.Content is StorageWrite.DocumentCopyWrite copy)
            {
                publications.Add(new DocumentCommitChange.Copy(
                    record, conversationId!.Value, copy.Source));
            }
            else if (change is not null && Publishes(plan))
            {
                var ops = change.Loaded is null ? Array.Empty<DeltaOp>() : change.Prepared.Ops;
                publications.Add(new DocumentCommitChange.Document(
                    record, conversationId, change.Version, (IReadOnlyDictionary<string, object?>?)change.Prepared.Value, ops));
            }
        }
        return publications;
    }

    private async Task<List<StorageWrite>> AssembleAsync()
    {
        var storage = _host.Storage;
        foreach (var document in _documents)
        {
            var plan = PlanDocument(document);
            if (plan is not null) _plans.Add(plan);
        }
        RejectForkSourceWrites(_plans);
        await ValidateOwnersAsync().ConfigureAwait(false);
        foreach (var (id, task) in _tasksById)
        {
            if (task.Write?.Kind != "replace") continue;
            var committed = await CommittedTaskAsync(id).ConfigureAwait(false);
            if (committed is null) throw new InvalidOperationException($"Task {id.Value} does not exist");
            if (committed.State.Status == Types.TaskStatus.Terminal)
                throw new InvalidOperationException($"Task {id.Value} is already terminal");
            if (committed.ConversationId != task.Write.Record.ConversationId)
            {
                throw new InvalidOperationException($"Task {id.Value} cannot change conversations");
            }
        }

        // 终态结算退役每个任务文档，包括本事务创建的。
        var terminalTaskIds = new HashSet<TaskId<object?>>();
        foreach (var task in _tasksById.Values)
        {
            if (task.Write?.Record.State.Status == Types.TaskStatus.Terminal)
                terminalTaskIds.Add(task.Write.Record.Id);
        }
        if (terminalTaskIds.Count > 0)
        {
            var retiring = new HashSet<DocumentId>();
            foreach (var plan in _plans)
            {
                var (id, scope) = PlanRecord(plan);
                if (scope is not DocumentScope.TaskScope taskScope || !terminalTaskIds.Contains(taskScope.TaskId))
                    continue;
                plan.Retire = true;
                retiring.Add(id);
            }
            foreach (var taskId in terminalTaskIds)
            {
                if (_tasksById.GetValueOrDefault(taskId)?.Write?.Kind == "create") continue;
                IReadOnlyDictionary<string, object?>? cursor = null;
                do
                {
                    var page = await storage.ScanDocumentsAsync(
                        new DocumentQuery
                        {
                            Scope = new DocumentScope.TaskScope(taskId),
                            At = new DocumentPoint.Current(),
                        },
                        InternalScanPageSize,
                        cursor).ConfigureAwait(false);
                    foreach (var record in page.Items)
                    {
                        if (retiring.Contains(record.Id)) continue;
                        _plans.Add(new DocumentPlan
                        {
                            AddressId = DurableDocuments.AddressId(AddressOfRecord(record)),
                            CommittedRecord = record,
                            Retire = true,
                        });
                        retiring.Add(record.Id);
                    }
                    cursor = page.Next;
                }
                while (cursor is not null);
            }
        }

        // 在 Storage 受理前解析发布归属，使采纳保持同步。
        foreach (var plan in _plans)
        {
            if (!Publishes(plan)) continue;
            var scope = PlanRecord(plan).Scope;
            if (scope is DocumentScope.ConversationScope conversationScope)
                plan.ConversationId = conversationScope.ConversationId;
            if (scope is not DocumentScope.TaskScope taskScope2) continue;
            var task = TaskEntry(taskScope2.TaskId);
            if (task.PublicationConversationId is null)
            {
                var current = await CurrentTaskAsync(taskScope2.TaskId).ConfigureAwait(false);
                if (current is not null) task.PublicationConversationId = current.ConversationId;
            }
            plan.ConversationId = task.PublicationConversationId;
        }

        foreach (var (id, change) in _submissionChanges)
        {
            var current = _submissions.GetValueOrDefault(id)
                ?? await storage.GetSubmissionAsync(id).ConfigureAwait(false);
            if (current is null) throw new InvalidOperationException($"Submission {id.Value} does not exist");
            var next = SubmissionChanges.Apply(current, change);
            if (!ReferenceEquals(next, current)) _submissions[id] = next;
        }

        var writes = _writes;
        foreach (var value in _submissions.Values) writes.Add(new StorageWrite.Submission(value));
        foreach (var task in _tasksById.Values)
        {
            if (task.Write is not null) writes.Add(new StorageWrite.Task(task.Write.Record));
        }
        foreach (var plan in _plans)
        {
            var change = plan.Change;
            // checkpoint 谓词最后运行，在全部校验之后。
            if (plan.Content is StorageWrite.DocumentChange documentChange
                && documentChange.Content is DocumentContent.Delta
                && change?.Loaded is not null)
            {
                var info = new CheckpointInfo { DeltasSinceBase = change.Loaded.DeltasSinceBase };
                if (change.Definition?.CheckpointWhen?.Invoke(
                        (IReadOnlyDictionary<string, object?>)change.Prepared.Value!, change.Prepared.Ops, info) == true)
                {
                    plan.Content = new StorageWrite.DocumentChange(
                        documentChange.Id,
                        new DocumentContent.Base(change.Version, (IReadOnlyDictionary<string, object?>)change.Prepared.Value!));
                }
            }
            if (plan.Content is not null) writes.Add(plan.Content);
            if (plan.Retire) writes.Add(new StorageWrite.DocumentRetire(PlanRecord(plan).Id));
        }
        return writes;
    }

    // ─── 助手 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 新的有主工作需要活着的属主，按属主的最终候选判定：非 completing、terminal 或 abort 标记。
    /// 因此任务不能在结束自己的同一提交里创建有主工作（spec §5.5）。
    /// </summary>
    private async Task ValidateOwnersAsync()
    {
        var owners = new List<(string What, TaskId<object?> TaskId)>();
        foreach (var write in _writes)
        {
            if (write is StorageWrite.Conversation { Value.Owner: { } owner })
                owners.Add(("Conversation owner task", owner.TaskId));
        }
        foreach (var task in _tasksById.Values)
        {
            var record = task.Write?.Kind == "create" ? task.Write.Record : null;
            if (record?.Owner is { } ownerId) owners.Add(("Task owner", ownerId));
        }
        foreach (var (what, taskId) in owners)
        {
            var task = await CurrentTaskAsync(taskId).ConfigureAwait(false);
            if (task is null) throw new InvalidOperationException($"{what} {taskId.Value} does not exist");
            if (task.State.Status is Types.TaskStatus.Terminal or Types.TaskStatus.Completing)
            {
                throw new InvalidOperationException($"{what} {taskId.Value} is {task.State.Status}");
            }
            if (task.AbortRequested) throw new InvalidOperationException($"{what} {taskId.Value} is abort-marked");
        }
    }

    private void RejectForkSourceWrites(IReadOnlyList<DocumentPlan> plans)
    {
        foreach (var plan in plans)
        {
            if (plan.Content is null && !plan.Retire) continue;
            var (id, scope) = PlanRecord(plan);
            if (_forkSourceDocumentIds.Contains(id))
            {
                throw new InvalidOperationException(
                    $"Cannot change fork source document {id.Value} in the fork transaction");
            }
            var fork = plan.CreateRecord?.Fork ?? plan.CommittedRecord?.Fork;
            if (scope is DocumentScope.ConversationScope conversationScope
                && _forkSourceConversationIds.Contains(conversationScope.ConversationId)
                && fork == ConversationFork.Current)
            {
                throw new InvalidOperationException(
                    $"Cannot fork conversation {conversationScope.ConversationId.Value} while changing its current-policy documents");
            }
        }
    }

    private void AbortChanges()
    {
        foreach (var document in _documents) document.Change?.Abort();
    }

    private void AssertOpen()
    {
        if (_sealed) throw new InvalidOperationException("Transaction has settled");
    }

    private void AssertTaskDocumentsOpen(DurableDocuments.ResolvedAddress resolved)
    {
        if (resolved.Address.Scope is DocumentScope.TaskScope taskScope
            && _tasksById.GetValueOrDefault(taskScope.TaskId)?.Write?.Record.State.Status == Types.TaskStatus.Terminal)
        {
            throw new InvalidOperationException($"Task {taskScope.TaskId.Value} is terminal");
        }
    }

    /// <summary>注册一个操作，使回调结算能够拒绝并排空它。</summary>
    private Task<T> Track<T>(Task<T> operation)
    {
        _pendingOperations.Add(operation);
        _ = operation.ContinueWith(
            static (_, state) =>
            {
                var set = (HashSet<Task>)state!;
                foreach (var pending in set) if (pending.IsCompleted) set.Remove(pending);
            },
            _pendingOperations,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    private Task Track(Task operation)
    {
        _pendingOperations.Add(operation);
        _ = operation.ContinueWith(
            static (_, state) =>
            {
                var set = (HashSet<Task>)state!;
                foreach (var pending in set) if (pending.IsCompleted) set.Remove(pending);
            },
            _pendingOperations,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    private async Task DrainPendingOperationsAsync()
    {
        foreach (var operation in _pendingOperations.ToArray())
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch
            {
                // 对应 Promise.allSettled：观察但不传播。
            }
        }
    }

    private Task<T> ReadAsync<T>(string method, Func<Task<T>> read)
    {
        try
        {
            AssertOpen();
            if (_hasTableWrite) throw new ReadAfterWrite(method);
            return Track(read());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private Task<T> WriteAsync<T>(Func<Task<T>> write)
    {
        try
        {
            AssertOpen();
            _hasTableWrite = true;
            return Track(write());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private async Task RequireConversationAsync(ConversationId id)
    {
        if (_createdConversationIds.Contains(id)) return;
        if (await _host.Storage.GetConversationAsync(id).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"Conversation {id.Value} does not exist");
        }
    }

    private TransactionTask TaskEntry(TaskId<object?> id)
    {
        if (!_tasksById.TryGetValue(id, out var task))
        {
            task = new TransactionTask();
            _tasksById[id] = task;
        }
        return task;
    }

    /// <summary>最新候选任务记录，回落到已提交状态；不是调用方的表读。</summary>
    private Task<TaskRecord?> CurrentTaskAsync(TaskId<object?> id)
        => _tasksById.GetValueOrDefault(id)?.Write?.Record is { } candidate
            ? Task.FromResult<TaskRecord?>(candidate)
            : CommittedTaskAsync(id);

    private Task<TaskRecord?> CommittedTaskAsync(TaskId<object?> id)
    {
        var task = TaskEntry(id);
        task.CommittedRead ??= _host.Storage.GetTaskAsync(id);
        return task.CommittedRead;
    }

    // ─── 计划构建 ───────────────────────────────────────────────────────────

    /// <summary>一个暂存文档的计划：其记录、内容写与 prepared 变更；退役稍后决定。对应 TS <c>planDocument</c>。</summary>
    private static DocumentPlan? PlanDocument(DocumentEntry document)
    {
        var target = document.Target;
        if (target is null) return null;
        switch (target)
        {
            case DocumentTarget.Created created:
            {
                var prepared = document.Prepared!;
                var content = new DocumentContent.Base(created.Version, (IReadOnlyDictionary<string, object?>)prepared.Value!);
                return new DocumentPlan
                {
                    AddressId = document.AddressId,
                    Retire = document.RetireOnCommit,
                    CreateRecord = created.Record,
                    Content = new StorageWrite.DocumentCreateWrite(created.Record, content),
                    Change = new PlannedChange
                    {
                        Tracker = created.Tracker,
                        Prepared = prepared,
                        Version = created.Version,
                    },
                };
            }
            case DocumentTarget.ForkCopy forkCopy:
                return new DocumentPlan
                {
                    AddressId = document.AddressId,
                    Retire = document.RetireOnCommit,
                    CreateRecord = forkCopy.Record,
                    Content = new StorageWrite.DocumentCopyWrite(forkCopy.Record, forkCopy.Source),
                };
            case DocumentTarget.RetireOnly retireOnly:
                return new DocumentPlan
                {
                    AddressId = document.AddressId,
                    Retire = document.RetireOnCommit,
                    CommittedRecord = retireOnly.Record,
                };
            case DocumentTarget.Loaded loadedTarget:
            {
                var loaded = loadedTarget.Document;
                var definition = document.Definition!;
                var prepared = document.Prepared!;
                var version = definition.Version;
                var id = loaded.Record.Id;
                // 版本变更即使没有操作也存 base；否则只有变更存 delta。
                var content = loaded.StoredVersion < version
                    ? new StorageWrite.DocumentChange(id, new DocumentContent.Base(version, (IReadOnlyDictionary<string, object?>)prepared.Value!))
                    : prepared.Ops.Count > 0
                        ? new StorageWrite.DocumentChange(id, new DocumentContent.Delta(version, prepared.Ops))
                        : null;
                return new DocumentPlan
                {
                    AddressId = document.AddressId,
                    Retire = document.RetireOnCommit,
                    CommittedRecord = loaded.Record,
                    Content = content,
                    Change = new PlannedChange
                    {
                        Tracker = loaded.Tracker,
                        Prepared = prepared,
                        Version = version,
                        Loaded = loaded,
                        Definition = definition,
                    },
                };
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }

    /// <summary>
    /// 采纳是否发布该计划：每个创建、拷贝与退役，以及写内容的已加载化身
    /// （含仅迁移的 base，使旧形状的观察者收到新值）。对应 TS <c>publishes</c>。
    /// </summary>
    private static bool Publishes(DocumentPlan plan)
        => plan.Retire || plan.Change?.Loaded is null || plan.Content is not null;

    /// <summary>计划记录的 ID 与作用域（两种记录形状的统一视图）。</summary>
    private static (DocumentId Id, DocumentScope Scope) PlanRecord(DocumentPlan plan)
        => plan.CommittedRecord is { } record
            ? (record.Id, record.Scope)
            : (plan.CreateRecord!.Id, plan.CreateRecord.Scope);

    private static DocumentRecord RecordFromCreate(DocumentCreate create, Seq seq) => new()
    {
        Id = create.Id,
        Kind = create.Kind,
        Key = create.Key,
        Scope = create.Scope,
        History = create.History,
        Fork = create.Fork,
        CreatedAt = seq,
    };

    /// <summary>把 DocumentCreate 抬升为未提交的 DocumentRecord（scope / version 校验用）。</summary>
    private static DocumentRecord CreateRecordOf(DocumentCreate create) => new()
    {
        Id = create.Id,
        Kind = create.Kind,
        Key = create.Key,
        Scope = create.Scope,
        History = create.History,
        Fork = create.Fork,
        CreatedAt = default,
    };

    /// <summary>从创建记录推导逻辑地址。</summary>
    private static DocumentAddress AddressOfCreate(DocumentCreate create) => new()
    {
        Kind = create.Kind,
        Key = create.Key,
        Scope = create.Scope,
    };

    /// <summary>从记录推导逻辑地址。</summary>
    private static DocumentAddress AddressOfRecord(DocumentRecord record) => new()
    {
        Kind = record.Kind,
        Key = record.Key,
        Scope = record.Scope,
    };
}
