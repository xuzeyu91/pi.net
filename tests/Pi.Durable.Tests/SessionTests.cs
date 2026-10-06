using System.Text.Json;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// Session 内核测试。覆盖 TS <c>session-tables</c> / <c>session-documents</c> /
/// <c>session-watches</c> / <c>session-forks</c> / <c>session-states</c> 的核心行为：
/// 首次表写后的表读拒绝、对话创建与条目追加、文档初值创建与提交持久化、草稿复用、
/// 族种子初始化、退役、终态任务的文档级联退役、watch 的精确帧投递与退休 / 取消 /
/// 关闭终态、fork 的文档拷贝与 fork 源写拒绝、as-of 历史快照、提交发布、
/// storage 失败毒化与 close 后拒绝。
/// </summary>
public class SessionTests
{
    private static readonly Context Ctx = Context.Background;
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    // ─── 测试夹具 ───────────────────────────────────────────────────────────

    /// <summary>Session 内核 + 受控存储 + 全部提交发布。对应 TS <c>openTestSession</c>。</summary>
    private sealed record TestSession
    {
        public required ControlledStorage Storage { get; init; }

        public required DurableSession Session { get; init; }

        public required List<CommitPublication> Publications { get; init; }
    }

    private static TestSession OpenTestSession()
    {
        var storage = new ControlledStorage(new MemoryStorage());
        var session = new DurableSession(storage);
        var publications = new List<CommitPublication>();
        session.SubscribeCommits((publication, _) => publications.Add(publication));
        return new TestSession { Storage = storage, Session = session, Publications = publications };
    }

    /// <summary>提交失败注入装饰器（MemoryStorage 非 virtual，用组合注入）。</summary>
    private sealed class ControlledStorage(IStorage inner) : IStorage
    {
        public readonly List<IReadOnlyList<StorageWrite>> AdmittedCommits = [];
        private Exception? _commitFailure;

        public void FailNextCommit(Exception error) => _commitFailure = error;

        public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
        {
            AdmittedCommits.Add(writes);
            var failure = _commitFailure;
            if (failure is not null)
            {
                _commitFailure = null;
                throw failure;
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

        public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id) => inner.GetSubmissionAsync(id);

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

    // ─── 定义 ───────────────────────────────────────────────────────────────

    /// <summary>进度文档（任务级单例）。对应 TS <c>ProgressDoc</c>。</summary>
    private static readonly DocToken<Dictionary<string, object?>> ProgressDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "test.progress",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["lines"] = new List<object?>() },
            Semantics = new DocumentSemantics.TaskScope(),
        });

    /// <summary>笔记文档（对话级 rewindable，fork: asOf）。对应 TS <c>NotesDoc</c>。</summary>
    private static readonly DocToken<Dictionary<string, object?>> NotesDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "test.notes",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["text"] = "" },
            Semantics = new DocumentSemantics.RewindableConversationScope(ConversationFork.AsOf),
        });

    /// <summary>当前策略文档（对话级 latest，fork: current）。对应 TS 分叉测试的 CurrentDoc。</summary>
    private static readonly DocToken<Dictionary<string, object?>> CurrentDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "test.current",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["n"] = 0L },
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Current),
        });

    /// <summary>会话级族文档。对应 TS 的 StepDoc 家族用法。</summary>
    private static readonly DocFamilyToken<Dictionary<string, object?>, object?> StepDoc =
        DurableDocuments.DefineDocFamily(new DocFamilyDefinition<Dictionary<string, object?>, object?>
        {
            Kind = "test.step",
            Version = 1,
            Initial = seed => new Dictionary<string, object?> { ["seed"] = seed, ["n"] = 0L },
            Semantics = new DocumentSemantics.SessionScope(),
        });

    /// <summary>工作任务。对应 TS <c>WorkTask</c>。</summary>
    private static readonly DurableTask<Dictionary<string, object?>, Dictionary<string, object?>, bool> WorkTask =
        DurableTasks.DefineTask(new TaskDefinition<Dictionary<string, object?>, Dictionary<string, object?>, bool>
        {
            Name = "test.work",
            Version = 1,
            Initial = input => new Dictionary<string, object?> { ["phase"] = "start" },
        });

    private static Task<ConversationId> CreateConversationAsync(DurableSession session)
        => session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless()).ConfigureAwait(false))
                .Id,
            Ctx);

    /// <summary>轮询直至条件成立（异步投递近似 TS 微任务时序）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> Clone(IReadOnlyDictionary<string, object?> value)
        => new(value);

    // ─── 表读 / 表写 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AllowsTableReadsOnlyBeforeFirstTableWrite()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(async tx =>
        {
            Assert.NotNull(await tx.GetConversationAsync(conversationId));
            var conversations = await tx.ScanConversationsAsync(new ConversationQuery(), 1);
            Assert.Single(conversations.Items);
            var tasks = await tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 10);
            Assert.Empty(tasks.Items);
            var entries = await tx.ScanEntriesAsync(new EntryQuery { ConversationId = conversationId }, 10);
            Assert.Empty(entries.Items);
            await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note" });
            await Assert.ThrowsAsync<ReadAfterWrite>(() => tx.GetConversationAsync(conversationId));
            await Assert.ThrowsAsync<ReadAfterWrite>(() =>
                tx.GetTaskAsync(TaskId<object?>.From(1)));
            await Assert.ThrowsAsync<ReadAfterWrite>(() =>
                tx.ScanConversationsAsync(new ConversationQuery(), 10));
            // 文档访问在表写之后仍然可用。
            (await tx.DocAsync(NotesDoc, conversationId)).Set(
                Path.Root.Append(Seg.Key("text")), "after write");
        }, Ctx);
        var snapshot = await test.Session.SnapshotAsync(NotesDoc, conversationId, Ctx);
        Assert.Equal("""{"text":"after write"}""", Json(snapshot));
    }

    [Fact]
    public async Task AppendEntryStampsKindHeadAndScopeTask()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var record = await test.Session.CommitAsync(async tx =>
        {
            var taskId = await tx.CreateTaskAsync(
                WorkTask,
                new Dictionary<string, object?> { ["path"] = "a" },
                new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = conversationId,
                });
            var entry = await tx.AppendEntryAsync(
                new Entry<bool> { Kind = "flag" },
                conversationId,
                new TypedEntryDraft<bool> { Data = true, HeadIsSelf = true });
            Assert.Equal("flag", entry.Kind);
            Assert.Equal(entry.Id, entry.Head);
            // byTaskId 取自事务 scope（TS 的 scope.taskId），不是本事务创建的任务。
            Assert.Null(entry.ByTaskId);
            return entry;
        }, Ctx);
        Assert.Equal(conversationId, record.ConversationId);
    }

    [Fact]
    public async Task ReadAfterWriteMessageNamesTheMethod()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var error = await Assert.ThrowsAsync<ReadAfterWrite>(() => test.Session.CommitAsync(async tx =>
        {
            await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note" });
            await tx.GetTaskAsync(TaskId<object?>.From(1));
            return 0;
        }, Ctx));
        Assert.Contains("Tx.task() cannot read tables after the first table write", error.Message);
    }

    // ─── 文档 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DocCreatesWithInitialValueAndPersistsOnCommit()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            Assert.Equal("""{"text":""}""", Json(change.Draft));
        }, Ctx);
        Assert.Equal("""{"text":""}""", Json(await test.Session.SnapshotAsync(NotesDoc, conversationId, Ctx)));
    }

    [Fact]
    public async Task DocMutationPublishesAndPersists()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "hello");
        }, Ctx);
        Assert.Equal("""{"text":"hello"}""", Json(await test.Session.SnapshotAsync(NotesDoc, conversationId, Ctx)));
        // 第二次提交只做变更（加载化身，发布 delta ops）。
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "two");
        }, Ctx);
        var documentChanges = test.Publications
            .SelectMany(p => p.Changes)
            .OfType<CommitChange.DocumentChanged>()
            .ToList();
        Assert.Equal(2, documentChanges.Count);
        // 创建化身发布 base 值且 ops 为空（观察者从快照水合）；变更化身发布 ops。
        Assert.Empty(documentChanges[0].Change.Ops);
        Assert.Equal("""{"text":"hello"}""", Json(documentChanges[0].Change.Value));
        Assert.Single(documentChanges[1].Change.Ops);
        Assert.Equal("""{"text":"two"}""", Json(documentChanges[1].Change.Value));
    }

    [Fact]
    public async Task DocReusesTheSameDraftWithinOneTransaction()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var createdId = await test.Session.CommitAsync(async tx =>
        {
            var first = await tx.DocAsync(NotesDoc, conversationId);
            var second = await tx.DocAsync(NotesDoc, conversationId);
            Assert.Same(first, second);
            return 0L;
        }, Ctx);
        Assert.Equal(0L, createdId);
    }

    [Fact]
    public async Task FamilyMemberInitializesFromSeed()
    {
        var test = OpenTestSession();
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(StepDoc, "a", "seed-a");
            change.Set(Path.Root.Append(Seg.Key("n")), 1L);
        }, Ctx);
        Assert.Equal(
            """{"seed":"seed-a","n":1}""",
            Json(await test.Session.SnapshotAsync(StepDoc, "a", Ctx)));
        // 未触达的成员不存在。
        Assert.Null(await test.Session.SnapshotAsync(StepDoc, "b", Ctx));
    }

    [Fact]
    public async Task RetireDocRemovesTheSnapshotAndEmitsRetirement()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "doomed");
        }, Ctx);
        await test.Session.CommitAsync(tx => tx.RetireDocAsync(NotesDoc, conversationId), Ctx);
        Assert.Null(await test.Session.SnapshotAsync(NotesDoc, conversationId, Ctx));
        var retirements = test.Publications
            .SelectMany(p => p.Changes)
            .OfType<CommitChange.DocumentChanged>()
            .Where(c => c.Change.Value is null)
            .ToList();
        Assert.Single(retirements);
    }

    [Fact]
    public async Task TerminalTaskRetiresItsTaskDocuments()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var taskId = await test.Session.CommitAsync(async tx =>
        {
            var id = await tx.CreateTaskAsync(
                WorkTask,
                new Dictionary<string, object?> { ["path"] = "a" },
                new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conversationId });
            var change = await tx.DocAsync(ProgressDoc, TaskId<object?>.From(id.Value));
            change.Splice(Path.Root.Append(Seg.Key("lines")), 0, 0, new object?[] { "started" });
            return id;
        }, Ctx);
        var taskKey = TaskId<object?>.From(taskId.Value);
        Assert.Equal(
            """{"lines":["started"]}""",
            Json(await test.Session.SnapshotAsync(ProgressDoc, taskKey, Ctx)));

        var record = await test.Session.CommitAsync(tx => tx.GetTaskAsync(taskKey), Ctx);
        Assert.NotNull(record);
        var terminal = record! with
        {
            State = new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = true },
            },
        };
        await test.Session.CommitAsync(tx =>
        {
            ((Transaction)tx).SetTask(terminal);
            return Task.CompletedTask;
        }, Ctx);
        // 终态结算退役该任务的全部文档（包括本事务创建的）。
        Assert.Null(await test.Session.SnapshotAsync(ProgressDoc, taskKey, Ctx));
    }

    [Fact]
    public async Task SnapshotAsOfReadsHistory()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var entryId = await test.Session.CommitAsync(async tx =>
        {
            await tx.DocAsync(NotesDoc, conversationId);
            return (await tx.AppendEntryAsync(conversationId, new EntryDraft { Kind = "note" })).Id;
        }, Ctx);
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "updated");
        }, Ctx);
        Assert.Equal(
            """{"text":"updated"}""",
            Json(await test.Session.SnapshotAsync(NotesDoc, conversationId, Ctx)));
        Assert.Equal(
            """{"text":""}""",
            Json(await test.Session.SnapshotAsOfAsync(NotesDoc, conversationId, entryId, Ctx)));
    }

    // ─── Fork ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForkCopiesAsOfAndCurrentDocuments()
    {
        var test = OpenTestSession();
        var parentId = await CreateConversationAsync(test.Session);
        var entryId = await test.Session.CommitAsync(async tx =>
        {
            var notes = await tx.DocAsync(NotesDoc, parentId);
            notes.Set(Path.Root.Append(Seg.Key("text")), "fork point");
            return (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "note" })).Id;
        }, Ctx);
        await test.Session.CommitAsync(async tx =>
        {
            var current = await tx.DocAsync(CurrentDoc, parentId);
            current.Set(Path.Root.Append(Seg.Key("n")), 5L);
        }, Ctx);
        var childId = await test.Session.CommitAsync(
            async tx => (await tx.ForkConversationAsync(
                parentId, entryId, new ConversationOwnership.Ownerless())).Id,
            Ctx);
        // asOf 策略文档取切点状态。
        Assert.Equal(
            """{"text":"fork point"}""",
            Json(await test.Session.SnapshotAsync(NotesDoc, childId, Ctx)));
        // current 策略文档取当前状态。
        Assert.Equal(
            """{"n":5}""",
            Json(await test.Session.SnapshotAsync(CurrentDoc, childId, Ctx)));
    }

    [Fact]
    public async Task ForkRejectsChangingCurrentPolicyDocumentsOfTheSource()
    {
        var test = OpenTestSession();
        var parentId = await CreateConversationAsync(test.Session);
        var entryId = await test.Session.CommitAsync(async tx =>
        {
            await tx.DocAsync(CurrentDoc, parentId);
            return (await tx.AppendEntryAsync(parentId, new EntryDraft { Kind = "note" })).Id;
        }, Ctx);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Session.CommitAsync(async tx =>
        {
            await tx.ForkConversationAsync(parentId, entryId, new ConversationOwnership.Ownerless());
            var change = await tx.DocAsync(CurrentDoc, parentId);
            change.Set(Path.Root.Append(Seg.Key("n")), 1L);
            return 0;
        }, Ctx));
        Assert.Contains("Cannot change fork source document", error.Message);
    }

    // ─── Watch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WatchDeliversCommittedUpdates()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(tx => tx.DocAsync(NotesDoc, conversationId), Ctx);
        var watch = await test.Session.WatchDocAsync(NotesDoc, conversationId, Ctx);
        Assert.NotNull(watch);
        var updates = new List<string>();
        object? gate = new();
        watch.Start((value, _, _) =>
        {
            lock (gate)
            {
                updates.Add(Json(value));
            }
            return Task.CompletedTask;
        });
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "one");
        }, Ctx);
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "two");
        }, Ctx);
        await WaitUntilAsync(() =>
        {
            lock (gate) return updates.Count == 2;
        });
        lock (gate)
        {
            // watch 无水合帧：只投递附着之后的已提交变更。
            Assert.Equal(["""{"text":"one"}""", """{"text":"two"}"""], updates);
        }
        var end = await watch.Stop();
        Assert.IsType<WatchEnd.Stopped>(end);
    }

    [Fact]
    public async Task WatchEndsRetiredWhenDocumentRetires()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(tx => tx.DocAsync(NotesDoc, conversationId), Ctx);
        var watch = await test.Session.WatchDocAsync(NotesDoc, conversationId, Ctx);
        Assert.NotNull(watch);
        watch.Start((_, _, _) => Task.CompletedTask);
        await test.Session.CommitAsync(tx => tx.RetireDocAsync(NotesDoc, conversationId), Ctx);
        var end = await watch.Closed;
        Assert.IsType<WatchEnd.Retired>(end);
        Assert.Null(watch.Value);
    }

    [Fact]
    public async Task WatchCancelAndSessionCloseEndIt()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(tx => tx.DocAsync(NotesDoc, conversationId), Ctx);
        var watch = await test.Session.WatchDocAsync(NotesDoc, conversationId, Ctx);
        Assert.NotNull(watch);
        watch.Start((_, _, _) => Task.CompletedTask);
        ((DocumentWatch<Dictionary<string, object?>>)watch).Cancel();
        Assert.IsType<WatchEnd.Cancelled>(await watch.Closed);

        var watch2 = await test.Session.WatchDocAsync(NotesDoc, conversationId, Ctx);
        Assert.NotNull(watch2);
        watch2.Start((_, _, _) => Task.CompletedTask);
        await test.Session.CloseAsync(Ctx);
        Assert.IsType<WatchEnd.SessionClosed>(await watch2.Closed);
    }

    [Fact]
    public async Task WatchReportsListenerError()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        await test.Session.CommitAsync(tx => tx.DocAsync(NotesDoc, conversationId), Ctx);
        var watch = await test.Session.WatchDocAsync(NotesDoc, conversationId, Ctx);
        Assert.NotNull(watch);
        watch.Start((_, _, _) => throw new InvalidOperationException("listener boom"));
        await test.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(NotesDoc, conversationId);
            change.Set(Path.Root.Append(Seg.Key("text")), "boom");
        }, Ctx);
        var end = await watch.Closed;
        var listenerError = Assert.IsType<WatchEnd.ListenerError>(end);
        Assert.Contains("listener boom", listenerError.Error.Message);
    }

    // ─── 生命周期 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StorageFailurePoisonsTheSession()
    {
        var test = OpenTestSession();
        test.Storage.FailNextCommit(new InvalidOperationException("disk on fire"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Session.CommitAsync(tx => tx.CreateConversationAsync(new ConversationOwnership.Ownerless()), Ctx));
        // 已受理失败后的提交拒绝：Session 被毒化。
        var poisoned = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Session.CommitAsync(tx => tx.CreateConversationAsync(new ConversationOwnership.Ownerless()), Ctx));
        Assert.Contains("Session is poisoned", poisoned.Message);
    }

    [Fact]
    public async Task CloseRejectsFurtherCommits()
    {
        var test = OpenTestSession();
        await test.Session.CloseAsync(Ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Session.CommitAsync(tx => tx.CreateConversationAsync(new ConversationOwnership.Ownerless()), Ctx));
    }

    [Fact]
    public async Task CommitWithoutWritesDoesNotPublish()
    {
        var test = OpenTestSession();
        var conversationId = await CreateConversationAsync(test.Session);
        var before = test.Publications.Count;
        var count = await test.Session.CommitAsync(tx =>
        {
            Assert.NotNull(tx);
            return Task.FromResult(7);
        }, Ctx);
        Assert.Equal(7, count);
        // 无写入的提交不发布。
        Assert.Equal(before, test.Publications.Count);
        Assert.True(conversationId.Value > 0);
    }
}
