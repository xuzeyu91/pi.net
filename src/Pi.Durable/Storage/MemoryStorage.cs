using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Types;

namespace Pi.Durable.Storage;

/// <summary>
/// 绑定到已提交文档化身的可释放只读 Chord 状态。对应 TS
/// <c>MemoryState = AttachedReplicatedState&lt;Readonly&lt;JsonObject&gt; | null&gt;</c>：
/// 值由 base + 累积 delta 经 <see cref="DeltaApply"/> 重放得出；
/// 监听器唯一、绝不内联调用，待发事件有上限（对齐 TS 的 16 条待发上限）。
/// </summary>
public sealed class MemoryState : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Context> _pending = [];
    private Func<Context, Task>? _listener;
    private bool _stopped;
    private bool _disposed;

    internal MemoryState(IReadOnlyDictionary<string, object?>? value)
        => Value = value;

    /// <summary>当前值快照（未物化时为 null）。</summary>
    public IReadOnlyDictionary<string, object?>? Value { get; private set; }

    /// <summary>安装唯一异步监听器；绝不内联调用。对应 TS <c>start(listener)</c>。</summary>
    public void Start(Func<Context, Task> listener)
    {
        List<Context> deliver;
        lock (_gate)
        {
            if (_stopped || _disposed || _listener is not null) return;
            _listener = listener;
            deliver = [.. _pending];
            _pending.Clear();
        }
        // 绝不内联调用：在锁外交付待发事件。
        _ = DeliverAsync(deliver);
    }

    private async Task DeliverAsync(List<Context> deliver)
    {
        foreach (var context in deliver)
            await InvokeAsync(context).ConfigureAwait(false);
    }

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。对应 TS <c>stop()</c>。</summary>
    public Task<WatchEnd> Stop()
    {
        lock (_gate)
        {
            if (_stopped) return Task.FromResult<WatchEnd>(new WatchEnd.Stopped());
            _stopped = true;
            _listener = null;
            _pending.Clear();
        }
        return Task.FromResult<WatchEnd>(new WatchEnd.Stopped());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _listener = null;
            _pending.Clear();
        }
        GC.SuppressFinalize(this);
    }

    internal void Replace(IReadOnlyDictionary<string, object?> value)
        => Value = value;

    internal void ApplyOps(IReadOnlyList<DeltaOp> ops, Context context)
    {
        foreach (var op in ops)
        {
            Value = (IReadOnlyDictionary<string, object?>?)DeltaApply.Apply([op], Value);
            Publish(context);
        }
    }
    private Task InvokeAsync(Context context)
    {
        Func<Context, Task>? listener;
        lock (_gate)
        {
            if (_stopped || _disposed) return Task.CompletedTask;
            listener = _listener;
        }
        return listener?.Invoke(context) ?? Task.CompletedTask;
    }

    /// <summary>把一次变更投递给监听器（无监听器时按上限缓存）。对应 TS 的待发队列。</summary>
    internal void Publish(Context context)
    {
        Func<Context, Task>? listener;
        lock (_gate)
        {
            if (_stopped || _disposed) return;
            listener = _listener;
            if (listener is null)
            {
                if (_pending.Count < 16) _pending.Add(context);
                return;
            }
        }
        _ = listener(context);
    }
}

/// <summary>
/// 单进程存储后端。对应 TS <c>storage/memory.ts</c> 的 <c>MemoryStorage</c>：
/// 所有表按 ID 排序；文档化身按 base + delta 重放；提交原子且发布监听异常不回滚。
/// </summary>
public sealed class MemoryStorage : IStorage
{
    private readonly object _gate = new();
    private readonly List<Func<CommitPublication, Task>> _publications = [];
    private readonly SortedDictionary<long, ConversationRecord> _conversations = new();
    private readonly SortedDictionary<long, EntryRecord> _entries = new();
    private readonly SortedDictionary<long, TaskRecord> _tasks = new();
    private readonly SortedDictionary<long, SubmissionRecord> _submissions = new();
    private readonly SortedDictionary<long, DocumentIncarnation> _documents = new();
    private long _nextSeq = 1;

    /// <inheritdoc />
    public Task<Seq> CommitAsync(
        IReadOnlyList<StorageWrite> writes,
        Func<IReadOnlyList<StorageWrite>, Task>? onCommit = null,
        CancellationToken signal = default)
    {
        lock (_gate)
        {
            var seq = Seq.From(_nextSeq++);
            ApplyWrites(writes, seq);
            var publication = BuildPublication(seq, writes);
            if (onCommit is not null) _ = Task.Run(() => onCommit(writes));
            QueuePublication(publication);
            return Task.FromResult(seq);
        }
    }

    private void ApplyWrites(IReadOnlyList<StorageWrite> writes, Seq seq)
    {
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    _conversations[conversation.Value.Id.Value] = conversation.Value;
                    break;
                case StorageWrite.Entry entry:
                    _entries[entry.Value.Id.Value] = entry.Value;
                    break;
                case StorageWrite.Task task:
                    _tasks[task.Value.Id.Value] = task.Value;
                    break;
                case StorageWrite.Submission submission:
                    _submissions[submission.Value.Id.Value] = submission.Value;
                    break;
                case StorageWrite.DocumentCreateWrite create:
                    CreateDocument(create.Record, create.Content, seq);
                    break;
                case StorageWrite.DocumentCopyWrite copy:
                    CopyDocument(copy.Record, copy.Source);
                    break;
                case StorageWrite.DocumentChange change:
                    ChangeDocument(change.Id, change.Content);
                    break;
                case StorageWrite.DocumentRetire retire:
                    RetireDocument(retire.Id);
                    break;
            }
        }
    }

    private void CreateDocument(DocumentCreate create, DocumentContent.Base content, Seq seq)
    {
        var record = new DocumentRecord
        {
            Id = create.Id,
            Kind = create.Kind,
            Key = create.Key,
            CreatedAt = seq,
            Scope = create.Scope,
            History = create.History,
            Fork = create.Fork,
        };
        _documents[create.Id.Value] = new DocumentIncarnation
        {
            Record = record,
            Version = content.Version,
            State = new MemoryState(content.Value),
        };
    }

    private void CopyDocument(DocumentCreate create, DocumentCopySource source)
    {
        var sourceDocument = Incarnation(source.Id);
        var record = new DocumentRecord
        {
            Id = create.Id,
            Kind = create.Kind,
            Key = create.Key,
            CreatedAt = sourceDocument.Record.CreatedAt,
            Scope = create.Scope,
            History = create.History,
            Fork = create.Fork,
        };
        _documents[create.Id.Value] = new DocumentIncarnation
        {
            Record = record,
            Version = sourceDocument.Version,
            State = new MemoryState(sourceDocument.State.Value),
        };
    }

    private void ChangeDocument(DocumentId id, DocumentContent content)
    {
        var document = Incarnation(id);
        switch (content)
        {
            case DocumentContent.Base baseContent:
                document.Version = baseContent.Version;
                document.State.Replace(baseContent.Value);
                break;
            case DocumentContent.Delta deltaContent:
                document.Version = deltaContent.Version;
                document.State.ApplyOps(deltaContent.Ops, Context.Background);
                break;
        }
    }

    private void RetireDocument(DocumentId id)
    {
        if (_documents.Remove(id.Value, out var document)) document.State.Dispose();
    }

    private DocumentIncarnation Incarnation(DocumentId id)
        => _documents.TryGetValue(id.Value, out var document)
            ? document
            : throw new KeyNotFoundException($"Document incarnation {id.Value} not found");

    private CommitPublication BuildPublication(Seq seq, IReadOnlyList<StorageWrite> writes)
    {
        var changes = new List<CommitChange>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    changes.Add(new CommitChange.ConversationTable(conversation.Value));
                    break;
                case StorageWrite.Entry entry:
                    changes.Add(new CommitChange.EntryTable(entry.Value));
                    break;
                case StorageWrite.Task task:
                    changes.Add(new CommitChange.TaskTable(task.Value));
                    break;
                case StorageWrite.Submission submission:
                    changes.Add(new CommitChange.SubmissionTable(submission.Value));
                    break;
                case StorageWrite.DocumentCreateWrite create:
                    var created = Incarnation(create.Record.Id);
                    changes.Add(new CommitChange.DocumentChanged(new DocumentCommitChange.Document(
                        created.Record, ConversationOf(created.Record), create.Content.Version, create.Content.Value, [])));
                    break;
                case StorageWrite.DocumentCopyWrite copy:
                    var copied = Incarnation(copy.Record.Id);
                    changes.Add(new CommitChange.DocumentCopied(new DocumentCommitChange.Copy(
                        copied.Record, ConversationOf(copied.Record), copy.Source)));
                    break;
                case StorageWrite.DocumentChange change:
                    var changed = Incarnation(change.Id);
                    changes.Add(new CommitChange.DocumentChanged(new DocumentCommitChange.Document(
                        changed.Record, ConversationOf(changed.Record), changed.Version, null, OpsOf(change.Content))));
                    break;
                case StorageWrite.DocumentRetire:
                    // 退役不产生发布变更（化身后续不再有内容）。
                    break;
            }
        }
        return new CommitPublication { Seq = seq, Changes = changes };
    }

    private ConversationId ConversationOf(DocumentRecord record) => record.Scope switch
    {
        DocumentScope.ConversationScope conversation => conversation.ConversationId,
        DocumentScope.TaskScope task => _tasks[task.TaskId.Value].ConversationId,
        _ => DurableIdConstants.RootConversation,
    };

    private static IReadOnlyList<DeltaOp> OpsOf(DocumentContent content)
        => content is DocumentContent.Delta delta ? delta.Ops : [];

    private void QueuePublication(CommitPublication publication)
    {
        Func<CommitPublication, Task>[] listeners;
        lock (_gate) listeners = [.. _publications];
        foreach (var listener in listeners)
            _ = Task.Run(async () =>
            {
                try
                {
                    await listener(publication).ConfigureAwait(false);
                }
                catch
                {
                    // 对齐 TS：发布监听器异常不得回滚已提交批次。
                }
            });
    }

    /// <inheritdoc />
    public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
    {
        lock (_gate) _publications.Add(listener);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync(Func<CommitPublication, Task> listener)
    {
        lock (_gate) _publications.Remove(listener);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        lock (_gate)
        {
            foreach (var document in _documents.Values) document.State.Dispose();
            _documents.Clear();
            _publications.Clear();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id)
        => Task.FromResult(_conversations.TryGetValue(id.Value, out var record) ? record : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationRecord>> ScanConversationsAsync(ConversationQuery? query = null)
    {
        var records = _conversations.Values.AsEnumerable();
        if (query?.OwnerConversationId is { } owner)
            records = records.Where(r => r.Owner?.ConversationId.Value == owner.Value);
        if (query?.OwnerTaskId is { } task)
            records = records.Where(r => r.Owner?.TaskId.Value == task.Value);
        return Task.FromResult<IReadOnlyList<ConversationRecord>>(records.ToList());
    }

    /// <inheritdoc />
    public Task<EntryRecord?> GetEntryAsync(EntryId id)
        => Task.FromResult(_entries.TryGetValue(id.Value, out var record) ? record : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryRecord>> ScanEntriesAsync(EntryQuery query)
    {
        var records = _entries.Values.Where(e => e.ConversationId.Value == query.ConversationId.Value);
        if (query.MinEntryId is { } min) records = records.Where(e => e.Id.Value >= min.Value);
        if (query.MaxEntryId is { } max) records = records.Where(e => e.Id.Value <= max.Value);
        return Task.FromResult<IReadOnlyList<EntryRecord>>(records.ToList());
    }

    /// <inheritdoc />
    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id)
        => Task.FromResult(_tasks.TryGetValue(id.Value, out var record) ? record : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<TaskRecord>> ScanTasksAsync(TaskQuery? query = null)
    {
        var records = _tasks.Values.AsEnumerable();
        if (query?.ConversationId is { } conversation) records = records.Where(t => t.ConversationId.Value == conversation.Value);
        if (query?.Kind is { } kind) records = records.Where(t => t.Kind == kind);
        if (query?.Status is { } status) records = records.Where(t => t.State.Status == status);
        if (query?.AbortRequested is { } abort) records = records.Where(t => t.AbortRequested == abort);
        if (query?.Background is { } background) records = records.Where(t => t.Background == background);
        return Task.FromResult<IReadOnlyList<TaskRecord>>(records.ToList());
    }

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
        => Task.FromResult(_submissions.TryGetValue(id.Value, out var record) ? record : null);

    /// <inheritdoc />
    public Task<IReadOnlyList<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery? query = null)
    {
        var records = _submissions.Values.AsEnumerable();
        if (query?.ConversationId is { } conversation) records = records.Where(s => s.ConversationId.Value == conversation.Value);
        if (query?.Status is { } status) records = records.Where(s => StatusOf(s) == status);
        return Task.FromResult<IReadOnlyList<SubmissionRecord>>(records.ToList());
    }

    private static SubmissionStatus? StatusOf(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => null,
    };

    /// <inheritdoc />
    public Task<Page<StoredDocument>> ScanDocumentsAsync(
        DocumentQuery query, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        var documents = _documents.Values.Where(d => Matches(d, query)).ToList();
        return Task.FromResult(new Page<StoredDocument>
        {
            Items = documents.Select(d => new StoredDocument
            {
                Record = d.Record,
                Version = d.Version,
                Value = d.State.Value ?? new Dictionary<string, object?>(),
                DeltasSinceBase = 0,
            }).ToList(),
            Next = null,
        });
    }

    private static bool Matches(DocumentIncarnation document, DocumentQuery query)
    {
        if (query.Kind is { } kind && document.Record.Kind != kind) return false;
        return query.Scope switch
        {
            DocumentScope.SessionScope => document.Record.Scope is DocumentScope.SessionScope,
            DocumentScope.ConversationScope conversation =>
                document.Record.Scope is DocumentScope.ConversationScope scope
                && scope.ConversationId.Value == conversation.ConversationId.Value,
            DocumentScope.TaskScope task =>
                document.Record.Scope is DocumentScope.TaskScope scope
                && scope.TaskId.Value == task.TaskId.Value,
            _ => false,
        };
    }

    /// <inheritdoc />
    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
    {
        if (!_documents.TryGetValue(id.Value, out var document)) return Task.FromResult<MemoryState?>(null);
        if (listener is not null) document.State.Start(listener);
        return Task.FromResult<MemoryState?>(document.State);
    }

    private sealed class DocumentIncarnation
    {
        public required DocumentRecord Record { get; init; }

        public required MemoryState State { get; init; }

        public int Version { get; set; }
    }
}
