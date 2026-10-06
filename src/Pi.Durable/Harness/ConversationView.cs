using System.Runtime.CompilerServices;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;

/// <summary>
/// 一个对话的活动转录与内建文档的结构挂载（spec §9.3）。对应 TS <c>ConversationView</c>。
/// C# 的 <c>entries</c> 是类型化 <see cref="EntryRecord"/>（TS 的发布 ops 仍原样投递给观察者；
/// C# 的 <see cref="DeltaApply"/> 无法对含 C# 记录的数组执行 splice，观察者应以投递值为准，
/// 见差异记录）。
/// </summary>
public sealed record ConversationView
{
    public required ConversationRecord Conversation { get; init; }

    /// <summary>原始活动条目，如 <c>ContextView.entries</c>：head 标记，然后其 head 之后的非 head 条目。</summary>
    public required IReadOnlyList<EntryRecord> Entries { get; init; }

    /// <summary>按 kind 的内建对话文档；缺失的文档缺失。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Docs { get; init; }
}

/// <summary>接收挂载的每个下一修订与 Session 的关闭。对应 TS <c>ViewObserver</c>。</summary>
public abstract class ViewObserver
{
    public virtual void Advance(ConversationView value, IReadOnlyList<DeltaOp> ops, Context context)
    {
    }

    /// <summary>每次发布（挂载取用之后）；<c>ops</c> 是挂载的（可能为空）。</summary>
    public virtual void Publication(
        ConversationView before, ConversationView after, IReadOnlyList<DeltaOp> ops,
        CommitPublication publication, Context context)
    {
    }

    public abstract void CloseSession();
}

/// <summary>
/// 一个 Harness 的对话视图挂载：每个对话至多一个，由其第一个观察者在 Session 线上构建，
/// 最后一个离开时丢弃；从 Session 的提交发布（持久）推进。对应 TS <c>view.ts</c> 全量。
/// </summary>
public sealed class ConversationViews
{
    private static readonly ConditionalWeakTable<DurableSession, ConversationViews> ViewsTable = new();

    /// <summary>一个 Harness 的视图挂载，供建于其上的适配器使用。对应 TS <c>conversationViews()</c>。</summary>
    public static ConversationViews For(DurableSession harness)
        => ViewsTable.TryGetValue(harness, out var views)
            ? views
            : throw new InvalidOperationException("Not a Harness");

    /// <summary>内建挂载文档（无键控的对话文档令牌）。对应 TS <c>MOUNTED</c>。</summary>
    private static readonly IReadOnlyList<DocToken<Dictionary<string, object?>>> Mounted =
    [
        AgentDocs.AgentDoc,
        Live.LiveDoc,
        Inbox.InboxDoc,
        Provider.ProviderDoc,
        Usage.UsageDoc,
    ];

    private static readonly IReadOnlySet<string> MountedKinds =
        new HashSet<string>(Mounted.Select(token => token.Definition.Kind), StringComparer.Ordinal);

    private static readonly Path EntriesPath = new([Seg.Key("entries")]);
    private static readonly Path DocsPath = new([Seg.Key("docs")]);

    private readonly DurableSession _session;
    private readonly IStorage _storage;
    private readonly object _gate = new();
    private readonly Dictionary<ConversationId, Mount> _mounts = [];
    private bool _closed;

    /// <summary>一个对话的挂载：当前修订、它展示的文档化身与观察者。</summary>
    private sealed class Mount
    {
        public ConversationView Value = null!;

        /// <summary>按 kind 的挂载化身与定义版本；另一个化身或版本整体设置。</summary>
        public readonly Dictionary<string, MountedDoc> Docs = new(StringComparer.Ordinal);

        public readonly HashSet<object> Observers = [];
    }

    private sealed record MountedDoc(DocumentId Id, int Version, IReadOnlyDictionary<string, object?> Value);

    public ConversationViews(DurableSession session, IStorage storage)
    {
        _session = session;
        _storage = storage;
        ViewsTable.Remove(session);
        ViewsTable.Add(session, this);
        _session.SubscribeCommits((publication, context) =>
        {
            lock (_gate)
            {
                foreach (var (id, mount) in _mounts) Advance(id, mount, publication, context);
            }
        });
        _session.SubscribeClose(() =>
        {
            List<object[]> observers;
            lock (_gate)
            {
                _closed = true;
                observers = [.. _mounts.Values.Select(mount => mount.Observers.ToArray())];
                _mounts.Clear();
            }

            foreach (var group in observers)
            {
                foreach (var observer in group) CloseObserver(observer);
            }
        });
    }

    /// <summary>视图的可释放只读 Chord 状态。对应 TS <c>state()</c>。</summary>
    public async Task<AttachedReplicatedState<ConversationView>> StateAsync(ConversationId id, Context context)
    {
        var (observer, detach) = await AttachAsync(
            id,
            (value, release) => new CommittedStateSource<ConversationView>(value, release),
            context).ConfigureAwait(false);
        try
        {
            return ReplicatedStateAttachments.AttachReplicatedStateSource(observer);
        }
        catch (Exception)
        {
            detach();
            throw;
        }
    }

    /// <summary>视图的序列化精确帧 watch；取消 <paramref name="context"/> 使其停止。对应 TS <c>watch()</c>。</summary>
    public async Task<CommittedWatch<ConversationView>> WatchAsync(ConversationId id, Context context)
    {
        var (observer, _) = await AttachAsync(
            id,
            (value, release) => new CommittedWatch<ConversationView>(value, release),
            context).ConfigureAwait(false);
        var signal = context.AbortSignal;
        if (signal?.IsCancellationRequested == true)
        {
            observer.Cancel();
            throw new OperationCanceledException(signal.GetValueOrDefault());
        }

        if (signal is { } watchSignal) observer.ObserveCancellation(watchSignal);
        return observer;
    }

    /// <summary>
    /// 注册一个从当前修订创建的观察者，原子地在 Session 线上：它看到之后的每次发布而非更早的。
    /// <paramref name="create"/> 可读已提交 Storage（仍在线上）；<c>release</c> 丢弃它——
    /// 以及挂载与其最后一个观察者。对应 TS <c>attach()</c>。
    /// </summary>
    public Task<(T Observer, Action Detach)> AttachAsync<T>(
        ConversationId id, Func<ConversationView, Action, T> create, Context context)
        where T : notnull
        => AttachCoreAsync(id, async (value, release) => create(value, release), context);

    /// <summary>异步创建回调的重载（回调仍运行在 Session 线上，可读已提交 Storage）。</summary>
    public Task<(T Observer, Action Detach)> AttachAsync<T>(
        ConversationId id, Func<ConversationView, Action, Task<T>> create, Context context)
        where T : notnull
        => AttachCoreAsync(id, create, context);

    private Task<(T Observer, Action Detach)> AttachCoreAsync<T>(
        ConversationId id, Func<ConversationView, Action, Task<T>> create, Context context)
        where T : notnull
        => _session.ReadOnLineAsync(async () =>
        {
            Mount? mount;
            lock (_gate) _mounts.TryGetValue(id, out mount);
            if (mount is null)
            {
                var built = await BuildAsync(id, context).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_mounts.TryGetValue(id, out mount)) _mounts[id] = mount = built;
                }
            }

            var (observer, detach) = await AttachToAsync(mount, create).ConfigureAwait(false);
            // 挂载水合期间 close 或取消可能已开始；此后不再注册。
            lock (_gate)
            {
                if (_closed) throw HarnessUtil.ClosedError();
            }

            context.AbortSignal?.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _mounts[id] = mount;
                mount.Observers.Add(observer);
            }

            return (observer, detach);
        });

    private (T Observer, Action Detach) AttachTo<T>(Mount mount, Func<ConversationView, Action, T> create)
        where T : notnull
    {
        T? observer = default;
        Action detach = () =>
        {
            lock (mount.Observers)
            {
                mount.Observers.Remove(observer!);
                if (mount.Observers.Count == 0) RemoveIfCurrent(mount);
            }
        };
        observer = create(mount.Value, detach);
        return (observer, detach);
    }

    private async Task<(T Observer, Action Detach)> AttachToAsync<T>(
        Mount mount, Func<ConversationView, Action, Task<T>> create)
        where T : notnull
    {
        T? observer = default;
        Action detach = () =>
        {
            lock (mount.Observers)
            {
                mount.Observers.Remove(observer!);
                if (mount.Observers.Count == 0) RemoveIfCurrent(mount);
            }
        };
        observer = await create(mount.Value, detach).ConfigureAwait(false);
        return (observer, detach);
    }

    private void RemoveIfCurrent(Mount mount)
    {
        lock (_gate)
        {
            foreach (var (id, current) in _mounts)
            {
                if (!ReferenceEquals(current, mount)) continue;
                _mounts.Remove(id);
                return;
            }
        }
    }

    private async Task<Mount> BuildAsync(ConversationId id, Context context)
    {
        var conversation = await _storage.GetConversationAsync(id).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {id.Value} does not exist");
        var bounds = await ConversationContext.CaptureContextBoundsAsync(_storage, id, context).ConfigureAwait(false);
        var entries = await ConversationContext.ActiveEntriesAsync(_storage, id, bounds, context).ConfigureAwait(false);
        var docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        var mount = new Mount();
        foreach (var token in Mounted)
        {
            var loaded = await _session.ConversationDocumentOnLineAsync(token, id, context).ConfigureAwait(false);
            if (loaded?.Value is not { } value) continue;
            var kind = token.Definition.Kind;
            docs[kind] = new Dictionary<string, object?>(value);
            mount.Docs[kind] = new MountedDoc(loaded.Record.Id, loaded.Version, docs[kind]);
        }

        mount.Value = new ConversationView
        {
            Conversation = conversation,
            Entries = entries,
            Docs = docs,
        };
        return mount;
    }

    /// <summary>从一次发布派生挂载的操作，应用它们，并把修订交给每个观察者。对应 TS <c>advance()</c>。</summary>
    private static void Advance(ConversationId id, Mount mount, CommitPublication publication, Context context)
    {
        var docOps = new List<DeltaOp>();   // 以 docs 为根的观察者视角操作
        var relOps = new List<DeltaOp>();   // 以 docs 值为根的应用操作
        var entryOps = new List<DeltaOp>();
        var entries = new List<EntryRecord>(mount.Value.Entries);
        var entriesChanged = false;
        var docsChanged = false;

        // 条目写入按 ID 序发布。
        foreach (var change in publication.Changes)
        {
            if (change is CommitChange.EntryTable entryChange && entryChange.Value.ConversationId == id)
            {
                var entry = entryChange.Value;
                if (entry.Head is null)
                {
                    entryOps.Add(new DeltaOp.Splice(EntriesPath, entries.Count, 0, [entry]));
                    entries.Add(entry);
                    entriesChanged = true;
                    continue;
                }

                // head 标记保留其 head 之后的非 head 条目（总是后缀），并放到最前。
                var target = entry.Head.Value;
                var kept = entries.FindIndex(
                    candidate => candidate.Head is null && candidate.Id.Value >= target.Value);
                if (kept < 0) kept = entries.Count;
                entryOps.Add(new DeltaOp.Splice(EntriesPath, 0, kept, [entry]));
                entries = [entry, .. entries.Skip(kept)];
                entriesChanged = true;
                continue;
            }

            if (change is not CommitChange.DocumentChanged documentChange) continue;
            if (documentChange.Change is not DocumentCommitChange.Document document) continue;
            var kind = document.Record.Kind;
            if (!MountedKinds.Contains(kind) || document.Record.Key is not null) continue;
            if (document.ConversationId is not { } conversationId || conversationId != id) continue;

            var prefix = DocsPath.Append(Seg.Key(kind));
            var relativePrefix = new Path([Seg.Key(kind)]);
            var mounted = mount.Docs.TryGetValue(kind, out var found) ? found : null;
            if (document.Value is null)
            {
                if (mounted is null || mounted.Id != document.Record.Id) continue;
                mount.Docs.Remove(kind);
                docsChanged = true;
                docOps.Add(new DeltaOp.Delete(prefix));
                relOps.Add(new DeltaOp.Delete(relativePrefix));
                continue;
            }

            if (mounted is not null && mounted.Id == document.Record.Id && mounted.Version == document.Version)
            {
                docsChanged = true;
                foreach (var op in document.Ops)
                {
                    docOps.Add(Prefixed(op, prefix));
                    relOps.Add(Prefixed(op, relativePrefix));
                }
            }
            else
            {
                mount.Docs[kind] = new MountedDoc(document.Record.Id, document.Version ?? 0, document.Value);
                docsChanged = true;
                docOps.Add(new DeltaOp.Set(prefix, document.Value));
                relOps.Add(new DeltaOp.Set(relativePrefix, document.Value));
            }
        }

        var before = mount.Value;
        var ops = new List<DeltaOp>(docOps.Count + entryOps.Count);
        ops.AddRange(docOps);
        ops.AddRange(entryOps);
        var frameContext = ContextSignals.WithoutAbortSignal(context);
        if (ops.Count > 0)
        {
            var value = before;
            if (docsChanged)
            {
                var docsRoot = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (name, doc) in mount.Docs) docsRoot[name] = doc.Value;
                var applied = (Dictionary<string, object?>)DeltaApply.Apply(relOps, docsRoot)!;
                var docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
                foreach (var (name, docValue) in applied) docs[name] = (IReadOnlyDictionary<string, object?>)docValue!;
                value = value with { Docs = docs };
            }

            if (entriesChanged) value = value with { Entries = entries };
            mount.Value = value;
            object[] observers;
            lock (mount.Observers) observers = [.. mount.Observers];
            foreach (var observer in observers) DispatchAdvance(observer, mount.Value, ops, frameContext);
        }

        object[] publicationObservers;
        lock (mount.Observers) publicationObservers = [.. mount.Observers];
        foreach (var observer in publicationObservers)
        {
            DispatchPublication(observer, before, mount.Value, ops, publication, frameContext);
        }
    }

    /// <summary><c>op</c> 移到 <c>prefix</c> 之下；根替换变成对前缀的设置。对应 TS <c>prefixed()</c>。</summary>
    private static DeltaOp Prefixed(DeltaOp op, Path prefix)
    {
        Path At(Path path) => new([.. prefix.Segments, .. path.Segments]);
        return op switch
        {
            DeltaOp.Replace replace => new DeltaOp.Set(prefix, replace.Value),
            DeltaOp.Splice splice => new DeltaOp.Splice(At(splice.Path), splice.Index, splice.Remove, splice.Items),
            DeltaOp.Move move => new DeltaOp.Move(At(move.Path), move.Permutation),
            DeltaOp.Set set => new DeltaOp.Set(At(set.Path), set.Value),
            DeltaOp.Delete delete => new DeltaOp.Delete(At(delete.Path)),
            DeltaOp.Append append => new DeltaOp.Append(At(append.Path), append.Text),
            DeltaOp.Truncate truncate => new DeltaOp.Truncate(At(truncate.Path), truncate.Length),
            _ => throw new InvalidOperationException("unknown op"),
        };
    }

    private static void DispatchAdvance(
        object observer, ConversationView value, IReadOnlyList<DeltaOp> ops, Context context)
    {
        switch (observer)
        {
            case CommittedWatch<ConversationView> watch:
                watch.Advance(value, ops, context);
                break;
            case CommittedStateSource<ConversationView> source:
                source.Advance(value, ops, context);
                break;
            case ViewObserver viewObserver:
                viewObserver.Advance(value, ops, context);
                break;
        }
    }

    private static void DispatchPublication(
        object observer, ConversationView before, ConversationView after, IReadOnlyList<DeltaOp> ops,
        CommitPublication publication, Context context)
    {
        // CommittedWatch / CommittedStateSource 没有 publication 回调（TS 的可选方法缺省）。
        if (observer is ViewObserver viewObserver)
        {
            viewObserver.Publication(before, after, ops, publication, context);
        }
    }

    private static void CloseObserver(object observer)
    {
        switch (observer)
        {
            case CommittedWatch<ConversationView> watch:
                watch.CloseSession();
                break;
            case CommittedStateSource<ConversationView> source:
                source.CloseSession();
                break;
            case ViewObserver viewObserver:
                viewObserver.CloseSession();
                break;
        }
    }
}
