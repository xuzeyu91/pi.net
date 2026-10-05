using System.Text.Json;
using Pi.Chord;
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
/// 所有表按 ID 排序；文档化身按 base + delta 重放（rewindable 保留修订历史，
/// current-only 只留最新 base）；提交原子且发布监听异常不回滚。
/// 读取与暂存写入均深拷贝，对齐序列化后端的脱钩所有权边界。
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
    private readonly Dictionary<long, string> _recordTypes = [];
    private readonly Dictionary<long, List<long>> _entryIdsByConversation = [];
    private readonly Dictionary<long, List<long>> _headEntryIdsByConversation = [];
    private readonly Dictionary<long, Seq> _entryCommitSeqs = [];
    private readonly Dictionary<(long ConversationId, string RequestId), long> _submissionIdsByRequest = [];
    private readonly Dictionary<string, AddressIndex> _documentAddresses = [];
    private readonly Dictionary<string, List<long>> _documentIdsByScope = [];
    private long _nextId = 2; // 1 保留给根对话（ROOT_CONVERSATION_ID）。
    private long _nextSeq = 1;
    private bool _closed;

    // ─── 提交 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
    {
        lock (_gate)
        {
            AssertOpen();
            var seq = Seq.From(_nextSeq);
            var detached = CloneWrites(writes);
            var resolved = ResolveDocumentCopies(detached);
            CheckGlobalIds(resolved);
            var actions = PrepareDocumentActions(resolved);
            CheckDocumentActions(actions);
            ApplyPreparedCommit(resolved, actions, seq);
            var publication = BuildPublication(seq, resolved);
            QueuePublication(publication);
            return Task.FromResult(seq);
        }
    }

    private static IReadOnlyList<StorageWrite> CloneWrites(IReadOnlyList<StorageWrite> writes)
    {
        var cloned = new List<StorageWrite>(writes.Count);
        foreach (var write in writes)
        {
            cloned.Add(write switch
            {
                StorageWrite.Conversation conversation => write,
                StorageWrite.Entry entry => new StorageWrite.Entry(CloneEntry(entry.Value)),
                StorageWrite.Task task => new StorageWrite.Task(CloneTask(task.Value)),
                StorageWrite.Submission submission => new StorageWrite.Submission(CloneSubmission(submission.Value)),
                StorageWrite.DocumentCreateWrite create => new StorageWrite.DocumentCreateWrite(
                    create.Record, CloneBase(create.Content)),
                StorageWrite.DocumentCopyWrite copy => write,
                StorageWrite.DocumentChange change => new StorageWrite.DocumentChange(
                    change.Id, CloneContent(change.Content)),
                StorageWrite.DocumentRetire retire => write,
                _ => write,
            });
        }
        return cloned;
    }

    private void ApplyPreparedCommit(
        IReadOnlyList<StorageWrite> writes,
        IReadOnlyList<(long Id, DocumentAction Action)> actions,
        Seq seq)
    {
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    _recordTypes[conversation.Value.Id.Value] = "conversation";
                    _conversations[conversation.Value.Id.Value] = conversation.Value;
                    TouchId(conversation.Value.Id.Value);
                    break;
                case StorageWrite.Entry entry:
                    _recordTypes[entry.Value.Id.Value] = "entry";
                    _entries[entry.Value.Id.Value] = entry.Value;
                    _entryCommitSeqs[entry.Value.Id.Value] = seq;
                    InsertSorted(EntryIdsOf(entry.Value.ConversationId.Value), entry.Value.Id.Value);
                    if (entry.Value.Head is { } head)
                        InsertSorted(HeadEntryIdsOf(entry.Value.ConversationId.Value), entry.Value.Id.Value);
                    TouchId(entry.Value.Id.Value);
                    break;
                case StorageWrite.Task task:
                    _recordTypes[task.Value.Id.Value] = "task";
                    _tasks[task.Value.Id.Value] = task.Value;
                    TouchId(task.Value.Id.Value);
                    break;
                case StorageWrite.Submission submission:
                    ApplySubmissionWrite(submission.Value);
                    break;
                case StorageWrite.DocumentCreateWrite:
                case StorageWrite.DocumentCopyWrite:
                case StorageWrite.DocumentChange:
                case StorageWrite.DocumentRetire:
                    // 文档动作统一在 ApplyDocumentActions 落地。
                    break;
            }
        }
        ApplyDocumentActions(actions, seq);
        _nextSeq = seq.Value + 1;
    }

    private void ApplySubmissionWrite(SubmissionRecord record)
    {
        _recordTypes[record.Id.Value] = "submission";
        if (_submissions.TryGetValue(record.Id.Value, out var previous) && previous.RequestId is { } oldRequest)
        {
            var oldKey = (previous.ConversationId.Value, oldRequest);
            if (_submissionIdsByRequest.TryGetValue(oldKey, out var mapped) && mapped == record.Id.Value)
                _submissionIdsByRequest.Remove(oldKey);
        }
        _submissions[record.Id.Value] = record;
        if (record.RequestId is { } request)
            _submissionIdsByRequest[(record.ConversationId.Value, request)] = record.Id.Value;
        TouchId(record.Id.Value);
    }

    private List<long> EntryIdsOf(long conversationId)
    {
        if (!_entryIdsByConversation.TryGetValue(conversationId, out var ids))
        {
            ids = [];
            _entryIdsByConversation[conversationId] = ids;
        }
        return ids;
    }

    private List<long> HeadEntryIdsOf(long conversationId)
    {
        if (!_headEntryIdsByConversation.TryGetValue(conversationId, out var ids))
        {
            ids = [];
            _headEntryIdsByConversation[conversationId] = ids;
        }
        return ids;
    }

    private void TouchId(long id)
    {
        if (id >= _nextId) _nextId = id + 1;
    }

    // ─── ID 铸造 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TId> MintIdAsync<TId>() where TId : struct
    {
        lock (_gate)
        {
            AssertOpen();
            // 对应 TS：!Number.isSafeInteger(nextId) 时拒绝（C# 以 long 回绕到负值表达耗尽）。
            if (_nextId < 0) throw new InvalidOperationException("ID space is exhausted");
            var id = _nextId++;
            if (typeof(TId) == typeof(ConversationId)) return Task.FromResult((TId)(object)ConversationId.From(id));
            if (typeof(TId) == typeof(EntryId)) return Task.FromResult((TId)(object)EntryId.From(id));
            if (typeof(TId) == typeof(TaskId<object?>)) return Task.FromResult((TId)(object)TaskId<object?>.From(id));
            if (typeof(TId) == typeof(SubmissionId)) return Task.FromResult((TId)(object)SubmissionId.From(id));
            if (typeof(TId) == typeof(DocumentId)) return Task.FromResult((TId)(object)DocumentId.From(id));
            throw new NotSupportedException($"MemoryStorage cannot mint IDs of type {typeof(TId).Name}");
        }
    }

    // ─── 对话 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id)
    {
        lock (_gate)
        {
            AssertOpen();
            return Task.FromResult(
                _conversations.TryGetValue(id.Value, out var record) ? CloneConversation(record) : null);
        }
    }

    /// <inheritdoc />
    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        lock (_gate)
        {
            AssertOpen();
            var after = CursorAfter(cursor);
            var values = new List<ConversationRecord>();
            foreach (var record in _conversations.Values)
            {
                if (after is { } afterId && record.Id.Value <= afterId) continue;
                if (query?.OwnerConversationId is { } ownerConversation
                    && record.Owner?.ConversationId.Value != ownerConversation.Value) continue;
                if (query?.OwnerTaskId is { } ownerTask
                    && record.Owner?.TaskId.Value != ownerTask.Value) continue;
                values.Add(CloneConversation(record));
                if (values.Count > limit) break;
            }
            return Task.FromResult(Page(values, limit));
        }
    }

    // ─── 条目 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(EntryId id)
    {
        lock (_gate)
        {
            AssertOpen();
            return Task.FromResult(LookupEntry(id.Value));
        }
    }

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
    {
        lock (_gate)
        {
            AssertOpen();
            // 与 TS 一致：仅当条目经该对话祖先链可见时返回。
            var visible = VisibleEntries(conversationId.Value, id.Value, id.Value).FirstOrDefault();
            if (visible is null || visible.Id.Value != id.Value) return Task.FromResult<StoredEntry?>(null);
            return Task.FromResult(LookupEntry(id.Value));
        }
    }

    private StoredEntry? LookupEntry(long id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return null;
        return new StoredEntry { Entry = CloneEntry(entry), CommitSeq = _entryCommitSeqs[id] };
    }

    /// <inheritdoc />
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
    {
        lock (_gate)
        {
            AssertOpen();
            if (!_conversations.ContainsKey(conversationId.Value))
                throw new InvalidOperationException($"Unknown conversation: {conversationId.Value}");
            var currentId = conversationId.Value;
            var upperEntryId = atOrBeforeEntryId?.Value ?? long.MaxValue;
            while (true)
            {
                var ids = _headEntryIdsByConversation.TryGetValue(currentId, out var headIds) ? headIds : [];
                var index = UpperBound(ids, upperEntryId) - 1;
                if (index >= 0)
                {
                    // headEntryIds 中的条目必带 head；TS 以交叉类型表达，C# 直接返回记录。
                    return Task.FromResult<EntryRecord?>(CloneEntry(_entries[ids[index]]));
                }
                var conversation = _conversations[currentId];
                if (conversation.Parent is not { } parent) return Task.FromResult<EntryRecord?>(null);
                upperEntryId = Math.Min(upperEntryId, parent.At.Value);
                currentId = parent.ConversationId.Value;
            }
        }
    }

    /// <inheritdoc />
    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        lock (_gate)
        {
            AssertOpen();
            var after = CursorAfter(cursor);
            var maxEntryId = after is { } afterId
                ? Math.Min(query.MaxEntryId?.Value ?? long.MaxValue, afterId - 1)
                : query.MaxEntryId?.Value ?? long.MaxValue;
            var minEntryId = query.MinEntryId?.Value ?? long.MinValue;
            var values = new List<EntryRecord>();
            foreach (var entry in VisibleEntries(query.ConversationId.Value, minEntryId, maxEntryId))
            {
                values.Add(CloneEntry(entry));
                if (values.Count > limit) break;
            }
            return Task.FromResult(Page(values, limit));
        }
    }

    /// <summary>
    /// 沿对话祖先链由新到旧产出可见条目。对应 TS <c>visibleEntries</c>：
    /// 每代对话取不超过上界的条目，父代上界收紧到 <c>parent.at</c>。
    /// </summary>
    private IEnumerable<EntryRecord> VisibleEntries(long conversationId, long minEntryId, long maxEntryId)
    {
        if (!_conversations.ContainsKey(conversationId))
            throw new InvalidOperationException($"Unknown conversation: {conversationId}");
        var currentId = conversationId;
        var upperEntryId = maxEntryId;
        while (true)
        {
            var ids = _entryIdsByConversation.TryGetValue(currentId, out var entryIds) ? entryIds : [];
            for (var index = UpperBound(ids, upperEntryId) - 1; index >= 0; index--)
            {
                var id = ids[index];
                if (id < minEntryId) break;
                yield return _entries[id];
            }
            var conversation = _conversations[currentId];
            if (conversation.Parent is not { } parent) break;
            upperEntryId = Math.Min(upperEntryId, parent.At.Value);
            if (upperEntryId < minEntryId) break;
            currentId = parent.ConversationId.Value;
        }
    }

    // ─── 任务 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id)
    {
        lock (_gate)
        {
            AssertOpen();
            return Task.FromResult(_tasks.TryGetValue(id.Value, out var record) ? CloneTask(record) : null);
        }
    }

    /// <inheritdoc />
    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        lock (_gate)
        {
            AssertOpen();
            var after = CursorAfter(cursor);
            var values = new List<TaskRecord>();
            foreach (var record in _tasks.Values)
            {
                if (after is { } afterId && record.Id.Value <= afterId) continue;
                if (query?.ConversationId is { } conversation && record.ConversationId.Value != conversation.Value) continue;
                if (query?.Kind is { } kind && record.Kind != kind) continue;
                if (query?.Status is { } status && record.State.Status != status) continue;
                if (query?.AbortRequested is { } abort && record.AbortRequested != abort) continue;
                if (query?.Background is { } background && record.Background != background) continue;
                values.Add(CloneTask(record));
                if (values.Count > limit) break;
            }
            return Task.FromResult(Page(values, limit));
        }
    }

    // ─── 提交记录 ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
    {
        lock (_gate)
        {
            AssertOpen();
            return Task.FromResult(
                _submissions.TryGetValue(id.Value, out var record) ? CloneSubmission(record) : null);
        }
    }

    /// <inheritdoc />
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        lock (_gate)
        {
            AssertOpen();
            var after = CursorAfter(cursor);
            var values = new List<SubmissionRecord>();
            foreach (var record in _submissions.Values)
            {
                if (after is { } afterId && record.Id.Value <= afterId) continue;
                if (query?.ConversationId is { } conversation && record.ConversationId.Value != conversation.Value) continue;
                if (query?.Status is { } status && StatusOf(record) != status) continue;
                values.Add(CloneSubmission(record));
                if (values.Count > limit) break;
            }
            return Task.FromResult(Page(values, limit));
        }
    }

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
    {
        lock (_gate)
        {
            AssertOpen();
            if (!_submissionIdsByRequest.TryGetValue((conversationId.Value, requestId), out var id))
                return Task.FromResult<SubmissionRecord?>(null);
            return Task.FromResult(
                _submissions.TryGetValue(id, out var record) ? CloneSubmission(record) : null);
        }
    }

    // ─── 文档 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
    {
        lock (_gate)
        {
            AssertOpen();
            var index = _documentAddresses.GetValueOrDefault(AddressKey(address));
            if (index is null) return Task.FromResult<DocumentRecord?>(null);
            if (at is DocumentPoint.Current)
            {
                return Task.FromResult(
                    index.CurrentId is { } current ? CloneDocumentRecord(_documents[current].Record) : null);
            }
            var atSeq = ((DocumentPoint.AtSeq)at).Seq;
            foreach (var id in index.Ids)
            {
                var record = _documents[id].Record;
                if (IsAliveAt(record, at, atSeq)) return Task.FromResult<DocumentRecord?>(CloneDocumentRecord(record));
            }
            return Task.FromResult<DocumentRecord?>(null);
        }
    }

    /// <inheritdoc />
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
    {
        lock (_gate)
        {
            AssertOpen();
            var stored = MaterializeDocument(id, at);
            return Task.FromResult(stored is null ? null : new StoredDocument
            {
                Record = CloneDocumentRecord(stored.Record),
                Version = stored.Version,
                Value = (IReadOnlyDictionary<string, object?>)Json.DeepClone(stored.Value)!,
                DeltasSinceBase = stored.DeltasSinceBase,
            });
        }
    }

    /// <inheritdoc />
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
    {
        lock (_gate)
        {
            AssertOpen();
            var ids = _documentIdsByScope.GetValueOrDefault(ScopeKey(query.Scope)) ?? [];
            var after = CursorAfter(cursor);
            var atSeq = query.At is DocumentPoint.AtSeq atPoint ? atPoint.Seq : (Seq?)null;
            var values = new List<DocumentRecord>();
            foreach (var id in ids)
            {
                if (after is { } afterId && id <= afterId) continue;
                var record = _documents[id].Record;
                if (query.Kind is { } kind && record.Kind != kind) continue;
                if (!IsAliveAt(record, query.At, atSeq)) continue;
                values.Add(CloneDocumentRecord(record));
                if (values.Count > limit) break;
            }
            return Task.FromResult(Page(values, limit));
        }
    }

    /// <summary>
    /// 按 ID 与选择点物化一个化身：找不到返回 null；current-only 文档请求历史点抛错；
    /// 值由所选 base 起重放后续 delta 得出，<see cref="StoredDocument.DeltasSinceBase"/>
    /// 即重放的 delta 数。
    /// </summary>
    private StoredDocument? MaterializeDocument(DocumentId id, DocumentPoint at)
    {
        if (!_documents.TryGetValue(id.Value, out var stored)) return null;
        var atSeq = at is DocumentPoint.AtSeq atPoint ? atPoint.Seq : (Seq?)null;
        if (atSeq is { } historical && IsCurrentOnly(stored.Record))
            throw new InvalidOperationException($"Document {id.Value} does not retain historical content");
        if (!IsAliveAt(stored.Record, at, atSeq)) return null;
        var revisions = atSeq is { } point
            ? stored.Revisions.Where(revision => revision.Seq.Value <= point.Value).ToList()
            : stored.Revisions;
        var baseIndex = revisions.Count - 1;
        while (baseIndex >= 0 && revisions[baseIndex].Content is not DocumentContent.Base) baseIndex--;
        if (baseIndex < 0 || revisions[baseIndex].Content is not DocumentContent.Base baseContent)
            throw new InvalidOperationException($"Document {id.Value} is missing a required base");
        var value = baseContent.Value;
        for (var index = baseIndex + 1; index < revisions.Count; index++)
        {
            if (revisions[index].Content is not DocumentContent.Delta delta)
                throw new InvalidOperationException(
                    $"Document {id.Value} crosses a stored version boundary without a base");
            if (delta.Version != baseContent.Version)
                throw new InvalidOperationException(
                    $"Document {id.Value} crosses a stored version boundary without a base");
            value = (IReadOnlyDictionary<string, object?>?)DeltaApply.Apply(delta.Ops, value)
                ?? throw new InvalidOperationException($"Document {id.Value} delta application failed");
        }
        return new StoredDocument
        {
            Record = stored.Record,
            Version = baseContent.Version,
            Value = value,
            DeltasSinceBase = revisions.Count - baseIndex - 1,
        };
    }

    // ─── 关闭与发布 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CloseAsync()
    {
        lock (_gate)
        {
            // 对齐 TS：close 幂等（重复 close 不拒绝），只封闭存储。
            if (_closed) return Task.CompletedTask;
            _closed = true;
            // close 只封闭存储（不清空表）；之后所有操作拒绝。
            foreach (var document in _documents.Values) document.State?.Dispose();
        }
        return Task.CompletedTask;
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
    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
    {
        lock (_gate)
        {
            AssertOpen();
            if (!_documents.TryGetValue(id.Value, out var document) || document.State is not { } state)
                return Task.FromResult<MemoryState?>(null);
            if (listener is not null) state.Start(listener);
            return Task.FromResult<MemoryState?>(state);
        }
    }

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
                    var created = _documents[create.Record.Id.Value];
                    changes.Add(new CommitChange.DocumentChanged(new DocumentCommitChange.Document(
                        created.Record, ConversationOf(created.Record), create.Content.Version, create.Content.Value, [])));
                    break;
                case StorageWrite.DocumentCopyWrite copy:
                    var copied = _documents[copy.Record.Id.Value];
                    changes.Add(new CommitChange.DocumentCopied(new DocumentCommitChange.Copy(
                        copied.Record, ConversationOf(copied.Record), copy.Source)));
                    break;
                case StorageWrite.DocumentChange change:
                    var changed = _documents[change.Id.Value];
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

    // ─── 文档动作（验证 + 落地） ─────────────────────────────────────────────

    /// <summary>
    /// 把 <c>document.copy</c> 解析为 <c>document.create</c>（内容取来源化身在所选点的物化值）。
    /// 对应 TS <c>resolveDocumentCopies</c>；失败包成 <see cref="StorageRejected"/>（存储无效果前拒绝）。
    /// </summary>
    private IReadOnlyList<StorageWrite> ResolveDocumentCopies(IReadOnlyList<StorageWrite> writes)
    {
        if (!writes.Any(write => write is StorageWrite.DocumentCopyWrite)) return writes;
        var changedDocumentIds = new HashSet<long>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.DocumentCreateWrite create:
                    changedDocumentIds.Add(create.Record.Id.Value);
                    break;
                case StorageWrite.DocumentCopyWrite copy:
                    changedDocumentIds.Add(copy.Record.Id.Value);
                    break;
                case StorageWrite.DocumentChange change:
                    changedDocumentIds.Add(change.Id.Value);
                    break;
                case StorageWrite.DocumentRetire retire:
                    changedDocumentIds.Add(retire.Id.Value);
                    break;
            }
        }
        var resolved = new List<StorageWrite>(writes.Count);
        foreach (var write in writes)
        {
            if (write is not StorageWrite.DocumentCopyWrite copyWrite)
            {
                resolved.Add(write);
                continue;
            }
            try
            {
                if (changedDocumentIds.Contains(copyWrite.Source.Id.Value))
                    throw new InvalidOperationException(
                        $"Fork source document {copyWrite.Source.Id.Value} is changed in the copy batch");
                var stored = MaterializeDocument(copyWrite.Source.Id, copyWrite.Source.At);
                if (stored is null)
                    throw new InvalidOperationException(
                        $"Fork source document {copyWrite.Source.Id.Value} cannot be read");
                var sourceRecord = stored.Record;
                if (sourceRecord.Scope is not DocumentScope.ConversationScope
                    || copyWrite.Record.Scope is not DocumentScope.ConversationScope
                    || sourceRecord.Kind != copyWrite.Record.Kind
                    || sourceRecord.Key != copyWrite.Record.Key
                    || sourceRecord.History != copyWrite.Record.History
                    || sourceRecord.Fork != copyWrite.Record.Fork)
                {
                    throw new InvalidOperationException(
                        $"Fork source document {copyWrite.Source.Id.Value} does not match the copied record");
                }
                resolved.Add(new StorageWrite.DocumentCreateWrite(
                    copyWrite.Record,
                    new DocumentContent.Base(stored.Version, stored.Value)));
            }
            catch (Exception error) when (error is not StorageRejected)
            {
                throw new StorageRejected($"Document copy {copyWrite.Record.Id.Value} was rejected", error);
            }
        }
        return resolved;
    }

    /// <summary>校验批次的全局 ID 所有权（同批次与既有记录均不可冲突）。对应 TS <c>checkGlobalIds</c>。</summary>
    private void CheckGlobalIds(IReadOnlyList<StorageWrite> writes)
    {
        var claimed = new Dictionary<long, string>();
        foreach (var write in writes)
        {
            string table;
            long id;
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    table = "conversation";
                    id = conversation.Value.Id.Value;
                    break;
                case StorageWrite.Entry entry:
                    table = "entry";
                    id = entry.Value.Id.Value;
                    break;
                case StorageWrite.Task task:
                    table = "task";
                    id = task.Value.Id.Value;
                    break;
                case StorageWrite.Submission submission:
                    table = "submission";
                    id = submission.Value.Id.Value;
                    break;
                case StorageWrite.DocumentCreateWrite create:
                    table = "document";
                    id = create.Record.Id.Value;
                    break;
                case StorageWrite.DocumentChange:
                case StorageWrite.DocumentRetire:
                    continue;
                default:
                    continue;
            }
            var existing = _recordTypes.GetValueOrDefault(id);
            var earlier = claimed.GetValueOrDefault(id);
            if (table is "conversation" or "entry" or "document")
            {
                if (existing is not null) throw new InvalidOperationException($"ID {id} already belongs to {existing}");
                if (earlier is not null) throw new InvalidOperationException($"ID {id} is written more than once");
            }
            else
            {
                if (existing is not null && existing != table)
                    throw new InvalidOperationException($"ID {id} already belongs to {existing}");
                if (earlier is not null && earlier != table)
                    throw new InvalidOperationException($"ID {id} is written as two record types");
            }
            claimed[id] = table;
        }
    }

    /// <summary>一个文档化身在本批次中的动作聚合。对应 TS <c>DocumentAction</c>。</summary>
    private sealed class DocumentAction
    {
        public DocumentCreate? Create { get; set; }

        public DocumentContent? Content { get; set; }

        public bool Retire { get; set; }
    }

    private List<(long Id, DocumentAction Action)> PrepareDocumentActions(IReadOnlyList<StorageWrite> writes)
    {
        var actions = new Dictionary<long, DocumentAction>();
        foreach (var write in writes)
        {
            long id;
            switch (write)
            {
                case StorageWrite.DocumentCreateWrite create:
                    id = create.Record.Id.Value;
                    var createAction = ActionFor(actions, id);
                    if (createAction.Create is not null || createAction.Content is not null)
                        throw new InvalidOperationException($"Document {id} has more than one content command");
                    createAction.Create = create.Record;
                    createAction.Content = create.Content;
                    break;
                case StorageWrite.DocumentChange change:
                    id = change.Id.Value;
                    var changeAction = ActionFor(actions, id);
                    if (changeAction.Content is not null)
                        throw new InvalidOperationException($"Document {id} has more than one content command");
                    changeAction.Content = change.Content;
                    break;
                case StorageWrite.DocumentRetire retire:
                    id = retire.Id.Value;
                    var retireAction = ActionFor(actions, id);
                    if (retireAction.Retire)
                        throw new InvalidOperationException($"Document {id} is retired more than once");
                    retireAction.Retire = true;
                    break;
            }
        }
        return [.. actions.Select(pair => (pair.Key, pair.Value))];
    }

    private static DocumentAction ActionFor(Dictionary<long, DocumentAction> actions, long id)
    {
        if (!actions.TryGetValue(id, out var action))
        {
            action = new DocumentAction();
            actions[id] = action;
        }
        return action;
    }

    private void CheckDocumentActions(IReadOnlyList<(long Id, DocumentAction Action)> actions)
    {
        var liveCounts = new Dictionary<string, int>();
        foreach (var (id, action) in actions)
        {
            var existing = _documents.GetValueOrDefault(id);
            if (action.Create is null && existing is null)
                throw new InvalidOperationException($"Unknown document: {id}");
            if (action.Create is not null && existing is not null)
                throw new InvalidOperationException($"Document {id} already exists");
            if (existing?.Record.RetiredAt is not null)
                throw new InvalidOperationException($"Document {id} is retired");
            var previous = existing?.Revisions.Count > 0 ? existing.Revisions[^1] : null;
            if (action.Content is DocumentContent.Delta delta)
            {
                if (previous is null) throw new InvalidOperationException($"Document {id} delta has no base");
                // 对应 TS checkDocumentActions：只比对上一修订的版本号——
                // delta 之后可紧跟同版本 delta，不要求上一修订本身是 base。
                var previousVersion = previous.Content switch
                {
                    DocumentContent.Base baseContent => baseContent.Version,
                    DocumentContent.Delta deltaContent => deltaContent.Version,
                    _ => 0,
                };
                if (previousVersion != delta.Version)
                    throw new InvalidOperationException($"Document {id} version transition requires a base");
            }

            var key = action.Create is not null
                ? RecordAddressKey(action.Create)
                : RecordAddressKey(existing!.Record);
            var currentId = _documentAddresses.GetValueOrDefault(key)?.CurrentId;
            var live = liveCounts.TryGetValue(key, out var counted)
                ? counted
                : currentId is null ? 0 : 1;
            if (action.Retire && currentId == id) live--;
            if (action.Create is not null && !action.Retire) live++;
            liveCounts[key] = live;
        }

        foreach (var live in liveCounts.Values)
        {
            if (live > 1)
                throw new InvalidOperationException("Document address already has a current incarnation");
        }
    }

    private void ApplyDocumentActions(IReadOnlyList<(long Id, DocumentAction Action)> actions, Seq seq)
    {
        foreach (var (id, action) in actions)
        {
            if (action.Create is { } create)
            {
                var record = new DocumentRecord
                {
                    Id = create.Id,
                    Kind = create.Kind,
                    Key = create.Key,
                    CreatedAt = seq,
                    RetiredAt = action.Retire ? seq : null,
                    Scope = create.Scope,
                    History = create.History,
                    Fork = create.Fork,
                };
                var value = action.Content is DocumentContent.Base baseContent
                    ? baseContent.Value
                    : throw new InvalidOperationException($"Document {id} create requires a base");
                var incarnation = new DocumentIncarnation
                {
                    Record = record,
                    Revisions = [new DocumentRevision { Content = action.Content, Seq = seq }],
                    Version = action.Content is DocumentContent.Base created ? created.Version : 0,
                    State = new MemoryState(value),
                };
                _documents[id] = incarnation;
                _recordTypes[id] = "document";

                var key = RecordAddressKey(record);
                if (!_documentAddresses.TryGetValue(key, out var address))
                {
                    address = new AddressIndex();
                    _documentAddresses[key] = address;
                }
                InsertSorted(address.Ids, id);

                var scopeKey = ScopeKey(record.Scope);
                if (!_documentIdsByScope.TryGetValue(scopeKey, out var scopeIds))
                {
                    scopeIds = [];
                    _documentIdsByScope[scopeKey] = scopeIds;
                }
                InsertSorted(scopeIds, id);
                TouchId(id);
                if (!action.Retire) address.CurrentId = id;
                continue;
            }

            ApplyDocumentChange(id, action, seq);
        }
    }

    private void ApplyDocumentChange(long id, DocumentAction action, Seq seq)
    {
        var incarnation = _documents[id];
        if (action.Content is { } content)
        {
            var revision = new DocumentRevision { Content = content, Seq = seq };
            if (content is DocumentContent.Base baseContent)
            {
                incarnation.Version = baseContent.Version;
                if (IsCurrentOnly(incarnation.Record))
                {
                    incarnation.Revisions.Clear();
                    incarnation.Revisions.Add(revision);
                    incarnation.State?.Replace(baseContent.Value);
                }
                else
                {
                    incarnation.Revisions.Add(revision);
                    incarnation.State?.Replace(baseContent.Value);
                }
            }
            else if (content is DocumentContent.Delta deltaContent)
            {
                incarnation.Version = deltaContent.Version;
                incarnation.Revisions.Add(revision);
                incarnation.State?.ApplyOps(deltaContent.Ops, Context.Background);
            }
        }
        if (action.Retire)
        {
            incarnation.Record = incarnation.Record with { RetiredAt = seq };
            if (IsCurrentOnly(incarnation.Record)) incarnation.Revisions.Clear();
            incarnation.State?.Dispose();
            incarnation.State = null;
            var key = RecordAddressKey(incarnation.Record);
            if (_documentAddresses.TryGetValue(key, out var address) && address.CurrentId == id)
                address.CurrentId = null;
        }
    }

    // ─── 索引键与工具 ────────────────────────────────────────────────────────

    private sealed class AddressIndex
    {
        public List<long> Ids { get; } = [];

        public long? CurrentId { get; set; }
    }

    private sealed class DocumentIncarnation
    {
        public required DocumentRecord Record { get; set; }

        public required List<DocumentRevision> Revisions { get; init; }

        /// <summary>最近修订的存储定义版本（发布变更用）。</summary>
        public int Version { get; set; }

        public MemoryState? State { get; set; }
    }

    private sealed record DocumentRevision
    {
        public required DocumentContent Content { get; init; }

        public required Seq Seq { get; init; }
    }

    private static string ScopeKey(DocumentScope scope) => scope switch
    {
        DocumentScope.SessionScope => "session",
        DocumentScope.ConversationScope conversation => $"conversation:{conversation.ConversationId.Value}",
        DocumentScope.TaskScope task => $"task:{task.TaskId.Value}",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static string AddressKey(DocumentAddress address)
    {
        var keyPart = address.Key is null ? "singleton" : $"family:{address.Key}";
        return $"{address.Kind}|{ScopeKey(address.Scope)}|{keyPart}";
    }

    private static string RecordAddressKey(DocumentRecord record)
        => AddressKey(new DocumentAddress { Kind = record.Kind, Scope = record.Scope, Key = record.Key });

    private static string RecordAddressKey(DocumentCreate create)
        => AddressKey(new DocumentAddress { Kind = create.Kind, Scope = create.Scope, Key = create.Key });

    private static bool IsAliveAt(DocumentRecord record, DocumentPoint at, Seq? atSeq) => at switch
    {
        DocumentPoint.Current => record.RetiredAt is null,
        DocumentPoint.AtSeq => record.CreatedAt.Value <= atSeq!.Value
            && (record.RetiredAt is null || atSeq!.Value < record.RetiredAt.Value),
        _ => false,
    };

    private static bool IsCurrentOnly(DocumentRecord record)
        => record.Scope is not DocumentScope.ConversationScope || record.History == Types.ConversationHistory.Latest;

    private static long? CursorAfter(IReadOnlyDictionary<string, object?>? cursor)
    {
        if (cursor is null) return null;
        if (!cursor.TryGetValue("after", out var after) || after is null)
            throw new InvalidOperationException("Invalid storage cursor");
        return after switch
        {
            double number when number == Math.Floor(number) => (long)number,
            long number => number,
            // 游标是后端持有的 JSON 状态；调用方可能经 JSON 往返后回传（TS conformance 有此用例）。
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed) => parsed,
            _ => throw new InvalidOperationException("Invalid storage cursor"),
        };
    }

    private static Page<T> Page<T>(IReadOnlyList<T> values, int limit)
    {
        var items = values.Take(limit).ToList();
        if (values.Count <= limit) return new Page<T> { Items = items };
        var lastId = LastIdOf(items[^1]);
        return new Page<T>
        {
            Items = items,
            Next = new Dictionary<string, object?> { ["after"] = (double)lastId },
        };
    }

    private static long LastIdOf<T>(T value) => value switch
    {
        ConversationRecord conversation => conversation.Id.Value,
        EntryRecord entry => entry.Id.Value,
        TaskRecord task => task.Id.Value,
        SubmissionRecord submission => submission.Id.Value,
        DocumentRecord document => document.Id.Value,
        _ => throw new InvalidOperationException($"Unsupported page item type {typeof(T).Name}"),
    };

    private static void InsertSorted(List<long> ids, long id)
    {
        if (ids.Count == 0 || ids[^1] < id)
        {
            ids.Add(id);
            return;
        }
        ids.Insert(LowerBound(ids, id), id);
    }

    private static int LowerBound(List<long> ids, long target)
    {
        var low = 0;
        var high = ids.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (ids[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(List<long> ids, long target)
    {
        var low = 0;
        var high = ids.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (ids[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static SubmissionStatus? StatusOf(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => null,
    };

    private void AssertOpen()
    {
        if (_closed) throw new InvalidOperationException("MemoryStorage is closed");
    }

    // ─── 脱钩拷贝 ────────────────────────────────────────────────────────────

    private static ConversationRecord CloneConversation(ConversationRecord record) => record;

    private static DocumentRecord CloneDocumentRecord(DocumentRecord record) => record;

    private static EntryRecord CloneEntry(EntryRecord record) => record with
    {
        Model = CloneMessages(record.Model),
        Data = Json.DeepClone(record.Data),
        Edits = CloneEdits(record.Edits),
    };

    private static TaskRecord CloneTask(TaskRecord record) => record with
    {
        Input = Json.DeepClone(record.Input),
        State = CloneTaskState(record.State),
        Memos = CloneObject(record.Memos),
    };

    private static TaskState CloneTaskState(TaskState state) => state with
    {
        Checkpoint = Json.DeepClone(state.Checkpoint),
        Outcome = state.Outcome is null
            ? null
            : state.Outcome with
            {
                Result = Json.DeepClone(state.Outcome.Result),
                Error = state.Outcome.Error is null
                    ? null
                    : state.Outcome.Error with { Detail = Json.DeepClone(state.Outcome.Error.Detail) },
            },
    };

    private static SubmissionRecord CloneSubmission(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input with { Detail = Json.DeepClone(input.Detail) },
        SubmissionRecord.WriteRecord write => write with { Detail = Json.DeepClone(write.Detail) },
        _ => record,
    };

    private static IReadOnlyList<Pi.Ai.Types.ChatMessage>? CloneMessages(
        IReadOnlyList<Pi.Ai.Types.ChatMessage>? messages)
    {
        if (messages is null) return null;
        // ChatMessage 是判别 record 层次（非普通 JSON 容器）；以 JSON 往返产生脱钩副本，
        // 对齐 TS copyJson 对消息对象的结构化拷贝语义。
        var cloned = new List<Pi.Ai.Types.ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            cloned.Add(System.Text.Json.JsonSerializer.Deserialize<Pi.Ai.Types.ChatMessage>(
                System.Text.Json.JsonSerializer.Serialize(message))
                ?? throw new InvalidOperationException("Chat message clone failed"));
        }
        return cloned;
    }

    private static IReadOnlyList<ContextEdit>? CloneEdits(IReadOnlyList<ContextEdit>? edits)
    {
        if (edits is null) return null;
        var cloned = new List<ContextEdit>(edits.Count);
        foreach (var edit in edits)
        {
            cloned.Add(edit switch
            {
                ContextEdit.Omit omit => omit,
                ContextEdit.Replace replace => replace with { Messages = CloneMessages(replace.Messages)! },
                _ => edit,
            });
        }
        return cloned;
    }

    private static IReadOnlyDictionary<string, object?>? CloneObject(IReadOnlyDictionary<string, object?>? value)
        => value is null ? null : (IReadOnlyDictionary<string, object?>?)Json.DeepClone(value);

    private static DocumentContent CloneContent(DocumentContent content) => content switch
    {
        DocumentContent.Base baseContent => CloneBase(baseContent),
        DocumentContent.Delta deltaContent => new DocumentContent.Delta(
            deltaContent.Version, CloneOps(deltaContent.Ops)),
        _ => content,
    };

    private static DocumentContent.Base CloneBase(DocumentContent.Base baseContent)
        => new(baseContent.Version, (IReadOnlyDictionary<string, object?>)Json.DeepClone(baseContent.Value)!);

    private static IReadOnlyList<DeltaOp> CloneOps(IReadOnlyList<DeltaOp> ops)
    {
        var cloned = new List<DeltaOp>(ops.Count);
        foreach (var op in ops)
        {
            cloned.Add(op switch
            {
                DeltaOp.Replace replace => new DeltaOp.Replace(Json.DeepClone(replace.Value)),
                DeltaOp.Set set => new DeltaOp.Set(set.Path, Json.DeepClone(set.Value)),
                DeltaOp.Delete delete => delete,
                DeltaOp.Append append => append,
                DeltaOp.Truncate truncate => truncate,
                DeltaOp.Splice splice => new DeltaOp.Splice(
                    splice.Path, splice.Index, splice.Remove, CloneItems(splice.Items)),
                DeltaOp.Move move => move,
                _ => op,
            });
        }
        return cloned;
    }

    private static IReadOnlyList<object?> CloneItems(IReadOnlyList<object?> items)
    {
        var cloned = new List<object?>(items.Count);
        foreach (var item in items) cloned.Add(Json.DeepClone(item));
        return cloned;
    }
}
