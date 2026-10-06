using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Session;

/// <summary>在一个存储后端上打开 Session 内核。对应 TS <c>createSession</c>。</summary>
public static class DurableSessions
{
    /// <inheritdoc cref="CreateSession" />
    public static DurableSession Create(IStorage storage) => new(storage);

    /// <summary>在一个存储后端上打开 Session 内核。</summary>
    public static DurableSession CreateSession(IStorage storage) => new(storage);
}

/// <summary>
/// Session 内核：一条变更线、已加载文档跟踪器缓存与已提交发布。
/// 对应 TS <c>session/session.ts</c> 的 <c>SessionImpl</c>。
/// 只有已提交状态可观察；每个提交回调、准备、Storage 结算、采纳与发布入队都在持有变更线时运行；
/// 监听器稍后运行。Harness 派生并覆写 <see cref="OnConversationCreatedAsync"/> /
/// <see cref="OnBeforeCloseAsync"/>（对应 TS 的 protected 钩子）。
/// </summary>
public class DurableSession : ISession
{
    private readonly IStorage _storage;
    private readonly Dictionary<string, LoadedDocument> _documents = [];
    private readonly HashSet<Action<CommitPublication, Context>> _commitListeners = [];
    private readonly HashSet<Action> _closeListeners = [];
    private readonly ITransactionHost _host;
    private Task _tail = Task.CompletedTask;
    private Task? _closing;
    private Exception? _poison;

    public DurableSession(IStorage storage)
    {
        _storage = storage;
        _host = new SessionHost(this);
    }

    /// <inheritdoc />
    public Task<TResult> CommitAsync<TResult>(Func<ITx, Task<TResult>> change, Context context)
        => CommitWithAsync(tx => change(tx), context, scope: null);

    /// <summary>无返回值回调的重载（对应 TS 回调隐式返回 undefined）。</summary>
    public Task CommitAsync(Func<ITx, Task> change, Context context)
        => CommitWithAsync<object?>(
            async tx =>
            {
                await change(tx).ConfigureAwait(false);
                return null;
            },
            context,
            scope: null);

    /// <summary>
    /// 内部提交：暴露具体事务与其内部操作（保留 ID 根引导与任务替换）。
    /// <paramref name="scope"/> 设置缺省 <c>tx.createTask()</c> 对话与追加条目归属的任务。
    /// 对应 TS <c>commitWith</c>。
    /// </summary>
    public Task<TResult> CommitWithAsync<TResult>(
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope = null)
    {
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<TResult>(error);
        }
        return Enqueue(() => RunCommitAsync(change, context, scope));
    }

    /// <summary>
    /// 内部：在变更线上运行一个只读作业，使多重读取派生观察同一个已提交状态。
    /// 对应 TS <c>readOnLine</c>。
    /// </summary>
    public Task<T> ReadOnLineAsync<T>(Func<Task<T>> job)
    {
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
        return Enqueue(async () =>
        {
            AssertHealthy();
            return await job().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 内部：在变更线上分页扫描任务。供已在变更线上运行的作业使用（见 <see cref="ReadOnLineAsync"/>）。
    /// 对应 TS <c>storage.scanTasks</c>（线上读取）。
    /// </summary>
    public Task<Page<TaskRecord>> ScanTasksOnLineAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => _storage.ScanTasksAsync(query, limit, cursor);

    /// <summary>
    /// 内部：一个对话文档的当前化身与值，供已在变更线上运行的作业使用（见 <see cref="ReadOnLineAsync"/>）。
    /// 缺失文档为 undefined。对应 TS <c>conversationDocumentOnLine</c>。
    /// </summary>
    public async Task<ConversationDocumentSnapshot?> ConversationDocumentOnLineAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        var definition = DurableDocuments.Erase(token.Definition);
        var resolved = DurableDocuments.ResolveAddress(definition, [conversationId.Value]);
        var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
            .ConfigureAwait(false);
        if (loaded is null) return null;
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return new ConversationDocumentSnapshot(
            loaded.Record, loaded.ValueVersion, loaded.Tracker.Value);
    }

    /// <summary>一个对话文档在变更线上的当前化身与值。对应 TS 返回形状。</summary>
    public sealed record ConversationDocumentSnapshot(
        DocumentRecord Record, int Version, IReadOnlyDictionary<string, object?>? Value);

    // ─── 快照 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [taskId.Value], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [key], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [taskId.Value, key], context).ConfigureAwait(false);

    private async Task<object?> SnapshotCoreAsync(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        _documents.TryGetValue(resolved.Id, out var cached);
        var loaded = cached?.ValueVersion == definition.Version
            ? cached
            : await Enqueue(async () =>
            {
                AssertHealthy();
                return await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (loaded is null) return null;
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return loaded.Tracker.Value;
    }

    // ─── 文档状态 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(DurableDocuments.Erase(token.Definition), [], context);

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(DurableDocuments.Erase(token.Definition), [conversationId.Value], context);

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(DurableDocuments.Erase(token.Definition), [taskId.Value], context);

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(DurableDocuments.Erase(token.Definition), [key], context);

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key], context);

    /// <inheritdoc />
    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => DocumentStateCoreAsync<T>(
            DurableDocuments.Erase(token.Definition), [taskId.Value, key], context);

    private async Task<DocumentState<T>?> DocumentStateCoreAsync<T>(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        var state = await Enqueue(async () =>
        {
            AssertHealthy();
            var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                .ConfigureAwait(false);
            if (loaded is null) return null;
            var (observer, detach) = AttachDocument(
                definition,
                loaded,
                (value, release) => new CommittedStateSource<IReadOnlyDictionary<string, object?>?>(
                    value, release));
            try
            {
                var attached = ReplicatedStateAttachments.AttachReplicatedStateSource(observer);
                return new DocumentState<T>(attached);
            }
            catch (Exception)
            {
                detach();
                throw;
            }
        }).ConfigureAwait(false);
        return state;
    }

    // ─── Watch ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [conversationId.Value], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [taskId.Value], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [key], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [conversationId.Value, key], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [taskId.Value, key], context);

    private async Task<IDocumentWatch<T>?> WatchDocCoreAsync<T>(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        var signal = context.AbortSignal;
        var cancelled = signal?.IsCancellationRequested ?? false;
        using var registration = signal?.Register(() => cancelled = true);
        var watch = await Enqueue(async () =>
        {
            AssertHealthy();
            if (cancelled) throw CancellationError(signal!.Value);
            var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                .ConfigureAwait(false);
            if (cancelled) throw CancellationError(signal!.Value);
            if (loaded is null) return null;
            var inner = AttachDocument(
                definition,
                loaded,
                (value, release) => new CommittedWatch<IReadOnlyDictionary<string, object?>?>(
                    value, release)).Observer;
            return new DocumentWatch<T>(inner);
        }).ConfigureAwait(false);
        if (watch is null) return null;
        if (cancelled)
        {
            watch.Cancel();
            throw CancellationError(signal!.Value);
        }
        if (signal is { } watchSignal) watch.ObserveCancellation(watchSignal);
        return watch;
    }

    // ─── 历史快照 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => SnapshotAsOfCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value, at.Value], context);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => SnapshotAsOfCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key, at.Value], context);

    private async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfCoreAsync(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        if (resolved.Address.Scope is not DocumentScope.ConversationScope addressScope)
        {
            throw new ArgumentException("Session.snapshotAsOf() requires a conversation document");
        }
        var conversationId = addressScope.ConversationId;
        var atValue = args[resolved.NextArgument];
        if (atValue is not long at || at is < -9007199254740991 or > 9007199254740991)
        {
            throw new ArgumentException("Session.snapshotAsOf() requires an entry ID");
        }
        var entryAt = EntryId.From(at);
        return await Enqueue(async () =>
        {
            AssertHealthy();
            var storedEntry = await _storage.GetEntryAsync(conversationId, entryAt).ConfigureAwait(false);
            if (storedEntry is null)
            {
                throw new InvalidOperationException(
                    $"Entry {entryAt.Value} is not visible from conversation {conversationId.Value}");
            }
            var address = resolved.Address with
            {
                Scope = new DocumentScope.ConversationScope(storedEntry.Entry.ConversationId),
            };
            var record = await _storage.FindDocumentAsync(
                address, new DocumentPoint.AtSeq(storedEntry.CommitSeq)).ConfigureAwait(false);
            if (record is null) return null;
            var stored = await _storage.GetDocumentAsync(record.Id, new DocumentPoint.AtSeq(storedEntry.CommitSeq))
                .ConfigureAwait(false);
            if (stored is null)
            {
                throw new InvalidOperationException(
                    $"Historical document {record.Id.Value} ({record.Kind}) cannot be read");
            }
            return DurableDocuments.MaterializeDocument(definition, stored);
        }).ConfigureAwait(false);
    }

    // ─── 生命周期 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CloseAsync(Context context)
    {
        if (_closing is null)
        {
            var cleanup = ContextSignals.WithoutAbortSignal(context);
            // 先封闭受理，再停止观察者；已受理工作在 Storage 关闭之前结算。
            _closing = Task.Run(() => OnBeforeCloseAsync())
                .ContinueWith(_ => Enqueue<object?>(async () =>
                {
                    _commitListeners.Clear();
                    _documents.Clear();
                    await _storage.CloseAsync().ConfigureAwait(false);
                    return null;
                }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap();
            var listeners = _closeListeners.ToArray();
            _closeListeners.Clear();
            foreach (var listener in listeners) listener();
        }
        return ContextSignals.AwaitWithContext(_closing, context);
    }

    /// <summary>
    /// 在每个创建或 fork 对话的事务里、对话记录暂存之后运行。普通 Session 不暂存；
    /// Harness 暂存其内置文档。对应 TS protected <c>conversationCreated</c>。
    /// </summary>
    protected virtual Task OnConversationCreatedAsync(Transaction tx, ConversationRecord record)
        => Task.CompletedTask;

    /// <summary>在 close 封闭受理之后、变更线关闭 Storage 之前运行；不得拒绝。对应 TS protected <c>beforeClose</c>。</summary>
    protected virtual Task OnBeforeCloseAsync() => Task.CompletedTask;

    /// <summary>
    /// 注册同步的采纳后监听器。不得抛出、阻塞或调用 Session 操作。
    /// 对应 TS <c>subscribeCommits</c>；返回退订 <see cref="IDisposable"/>。
    /// </summary>
    public IDisposable SubscribeCommits(Action<CommitPublication, Context> listener)
    {
        AssertUsable();
        _commitListeners.Add(listener);
        return new Unsubscription(() => _commitListeners.Remove(listener));
    }

    /// <summary>注册在 close 开始时同步调用的监听器。不得抛出、阻塞或调用 Session 操作。</summary>
    public IDisposable SubscribeClose(Action listener)
    {
        AssertUsable();
        _closeListeners.Add(listener);
        return new Unsubscription(() => _closeListeners.Remove(listener));
    }

    /// <summary>在变更线上丢弃每个已加载跟踪器；之后的访问从 Storage 冷加载。</summary>
    public Task UnloadDocumentsAsync()
        => Enqueue<object?>(async () =>
        {
            _documents.Clear();
            return null;
        });

    // ─── 提交执行 ───────────────────────────────────────────────────────────

    private async Task<TResult> RunCommitAsync<TResult>(
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope)
    {
        AssertHealthy();
        if (context.AbortSignal is { IsCancellationRequested: true } signal)
            throw new OperationCanceledException(signal);
        var tx = new Transaction(_host, context, scope);
        TResult result;
        try
        {
            result = await change(tx).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await tx.SettleFailureAsync().ConfigureAwait(false);
            throw;
        }
        var writes = await tx.SettleSuccessAsync().ConfigureAwait(false);
        if (writes.Count == 0)
        {
            tx.Discard();
            return result;
        }
        Seq seq;
        try
        {
            // 一旦受理，调用方取消不会中断 Storage 结算。
            seq = await _storage.CommitAsync(writes, ContextSignals.WithoutAbortSignal(context).AbortSignal ?? default)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            tx.Discard();
            // 回调错误从不进入此分支；只有 StorageRejected 能保证无批次效果被提交。
            if (error is not StorageRejected) _poison = error;
            throw;
        }
        List<DocumentCommitChange> documents;
        try
        {
            documents = tx.Adopt(seq);
        }
        catch (Exception error)
        {
            // Storage 已提交；采纳失败使内存落后于持久状态。
            _poison = error;
            throw;
        }
        Publish(seq, writes, documents, context);
        return result;
    }

    private void Publish(
        Seq seq,
        IReadOnlyList<StorageWrite> writes,
        IReadOnlyList<DocumentCommitChange> documents,
        Context context)
    {
        if (_commitListeners.Count == 0) return;
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
            }
        }
        foreach (var document in documents)
        {
            changes.Add(document switch
            {
                DocumentCommitChange.Document changed => new CommitChange.DocumentChanged(changed),
                DocumentCommitChange.Copy copy => new CommitChange.DocumentCopied(copy),
                _ => throw new ArgumentOutOfRangeException(nameof(document)),
            });
        }
        var publication = new CommitPublication { Seq = seq, Changes = changes };
        foreach (var listener in _commitListeners.ToArray()) listener(publication, context);
    }

    /// <summary>
    /// 把观察者附着到一个已提交化身：校验定义，然后转发本化身的已提交变更并关闭。
    /// <c>detach</c> 同时移除两个订阅。对应 TS <c>#attachDocument</c>。
    /// </summary>
    private (TObserver Observer, Action Detach) AttachDocument<TObserver>(
        DurableDocuments.AnyDocDefinition definition,
        LoadedDocument loaded,
        Func<IReadOnlyDictionary<string, object?>?, Action, TObserver> create)
        where TObserver : class
    {
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        IDisposable unsubscribeCommit = new Unsubscription(() => { });
        IDisposable unsubscribeClose = new Unsubscription(() => { });
        Action detach = () =>
        {
            unsubscribeCommit.Dispose();
            unsubscribeClose.Dispose();
        };
        var observer = create(loaded.Tracker.Value, detach);
        var observed = new ObservedVersion { Version = loaded.ValueVersion };
        unsubscribeCommit = SubscribeCommits((publication, context) =>
        {
            foreach (var change in publication.Changes)
            {
                if (change is not CommitChange.DocumentChanged document
                    || document.Change.Record.Id != loaded.Record.Id)
                {
                    continue;
                }
                // 文档状态的帧不带调用方取消；watch 观察自己的取消。
                var frameContext = observer is CommittedStateSource<IReadOnlyDictionary<string, object?>?>
                    ? ContextSignals.WithoutAbortSignal(context)
                    : context;
                var ops = ObservedOperations(observed, document.Change);
                // 仅迁移的 base 对新版本的观察者没有任何改变。
                if (ops.Count == 0) continue;
                switch (observer)
                {
                    case CommittedStateSource<IReadOnlyDictionary<string, object?>?> source:
                        source.Advance(document.Change.Value, ops, frameContext);
                        break;
                    case CommittedWatch<IReadOnlyDictionary<string, object?>?> watch:
                        watch.Advance(document.Change.Value, ops, frameContext);
                        break;
                }
            }
        });
        unsubscribeClose = SubscribeClose(() =>
        {
            switch (observer)
            {
                case CommittedStateSource<IReadOnlyDictionary<string, object?>?> source:
                    source.CloseSession();
                    break;
                case CommittedWatch<IReadOnlyDictionary<string, object?>?> watch:
                    watch.CloseSession();
                    break;
            }
        });
        return (observer, detach);
    }

    private sealed class ObservedVersion
    {
        public int Version;
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(
        DurableDocuments.AnyDocDefinition definition, string addressId, DocumentAddress address, Context context)
    {
        _documents.TryGetValue(addressId, out var cached);
        // 跟踪器只服务于其值被物化时的版本的令牌；其他版本从 Storage 重载。
        if (cached?.ValueVersion == definition.Version) return cached;
        if (cached is not null) _documents.Remove(addressId);
        var record = await _storage.FindDocumentAsync(address, new DocumentPoint.Current())
            .ConfigureAwait(false);
        if (record is null) return null;
        var stored = await _storage.GetDocumentAsync(record.Id, new DocumentPoint.Current())
            .ConfigureAwait(false);
        if (stored is null)
        {
            throw new InvalidOperationException(
                $"Current document {record.Id.Value} ({record.Kind}) cannot be read");
        }
        var value = DurableDocuments.MaterializeDocument(definition, stored);
        var loaded = new LoadedDocument
        {
            AddressId = addressId,
            Record = stored.Record,
            StoredVersion = stored.Version,
            ValueVersion = definition.Version,
            DeltasSinceBase = stored.DeltasSinceBase,
            Tracker = new Tracker<IReadOnlyDictionary<string, object?>>(value),
        };
        _documents[addressId] = loaded;
        return loaded;
    }

    private Task<T> Enqueue<T>(Func<Task<T>> job)
    {
        var run = _tail.ContinueWith(
            _ => job(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
        _tail = run.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return run;
    }

    private void AssertUsable()
    {
        if (_closing is not null) throw new InvalidOperationException("Session is closed");
        AssertHealthy();
    }

    private void AssertHealthy()
    {
        if (_poison is not null)
        {
            throw new InvalidOperationException(
                "Session is poisoned by a failed commit after storage admission; reopen it", _poison);
        }
    }

    /// <summary>
    /// 观察者为一次已提交变更应用的操作。以其他定义版本水化的观察者持有不同形状的值，
    /// 因此它收到新值作为根替换而非该形状的操作。对应 TS <c>observedOperations</c>。
    /// </summary>
    private static IReadOnlyList<DeltaOp> ObservedOperations(
        ObservedVersion observed, DocumentCommitChange.Document change)
    {
        if (change.Value is null) return CommittedStateSource<object?>.RetirementOperations;
        if (change.Version == observed.Version) return change.Ops;
        observed.Version = change.Version!.Value;
        return [new DeltaOp.Replace(change.Value)];
    }

    private static OperationCanceledException CancellationError(CancellationToken signal)
        => new(signal);

    /// <summary>事务主机的默认实现，桥接到 Session 私有状态。</summary>
    private sealed class SessionHost(DurableSession session) : ITransactionHost
    {
        public IStorage Storage => session._storage;

        public LoadedDocument? Cached(string addressId)
            => session._documents.GetValueOrDefault(addressId);

        public Task<LoadedDocument?> LoadAsync(
            DurableDocuments.AnyDocDefinition definition,
            string addressId,
            DocumentAddress address,
            Context context)
            => session.LoadDocumentAsync(definition, addressId, address, context);

        public void Install(LoadedDocument document) => session._documents[document.AddressId] = document;

        public void Evict(string addressId, DocumentId recordId)
        {
            if (session._documents.GetValueOrDefault(addressId)?.Record.Id == recordId)
                session._documents.Remove(addressId);
        }

        public Task ConversationCreatedAsync(Transaction tx, ConversationRecord record)
            => session.OnConversationCreatedAsync(tx, record);
    }

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
