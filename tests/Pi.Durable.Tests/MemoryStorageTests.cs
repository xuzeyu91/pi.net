using System.Text.Json;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 内存存储测试。覆盖 TS <c>storage/memory.test.ts</c> 经
/// <c>registerStorageConformance</c> 注册的全部存储契约（<c>testing/storage-conformance.ts</c>）：
/// 根对话保留 ID、混合批次原子性与回滚、写入/返回记录脱钩、乱序条目索引与游标分页、
/// fork 祖先链可见性与 head marker、任务/提交记录替换与状态扫描、requestId 索引、
/// 文档 base/delta 重放与 as-of 点查询、拷贝独立性与歧义拒绝、地址索引与精确作用域扫描、
/// 生命周期失败原子性、全局 ID 命名空间与耗尽、close 后拒绝；
/// 以及发布监听、附加只读状态与监听器唯一性 / 绝不内联调用。
/// <para>未移植项：TS "keeps indexed string identities lossless"（JS 字符串驻留陷阱，
/// C# string 无语义等价问题）。</para>
/// </summary>
public class MemoryStorageTests
{
    private static readonly ConversationId Conversation = ConversationId.From(1);
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    // ─── 基础：序列分配 / 表排序 / 查询过滤 ───────────────────────────────

    [Fact]
    public async Task CommitAssignsMonotonicSequences()
    {
        var storage = new MemoryStorage();
        var first = await storage.CommitAsync([ConversationWrite(ConversationId.From(1))]);
        var second = await storage.CommitAsync([ConversationWrite(ConversationId.From(2))]);

        Assert.Equal(1L, first.Value);
        Assert.Equal(2L, second.Value);
    }

    [Fact]
    public async Task TablesAreSortedById()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            ConversationWrite(ConversationId.From(5)),
            ConversationWrite(ConversationId.From(2)),
            ConversationWrite(ConversationId.From(9)),
        ]);

        var conversations = await storage.ScanConversationsAsync(null, 10);
        Assert.Equal([2L, 5L, 9L], conversations.Items.Select(c => c.Id.Value));
    }

    [Fact]
    public async Task EntryQueryFiltersByConversationAndBounds()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            ConversationWrite(Conversation),
            EntryWrite(Conversation, 2),
            EntryWrite(Conversation, 3),
            EntryWrite(Conversation, 4),
            EntryWrite(ConversationId.From(2), 5),
        ]);

        var all = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = Conversation }, 10);
        // 条目扫描为最新优先（对应 TS scanEntries 的 newest-first 语义）。
        Assert.Equal([4L, 3L, 2L], all.Items.Select(e => e.Id.Value));

        var bounded = await storage.ScanEntriesAsync(new EntryQuery
        {
            ConversationId = Conversation,
            MinEntryId = EntryId.From(3),
            MaxEntryId = EntryId.From(4),
        }, 10);
        Assert.Equal([4L, 3L], bounded.Items.Select(e => e.Id.Value));
    }

    [Fact]
    public async Task TaskQueryFiltersByKindStatusAndBackground()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            TaskWrite(1, "kind.a", TaskStatus.Pending, background: false),
            TaskWrite(2, "kind.b", TaskStatus.Running, background: true),
            TaskWrite(3, "kind.a", TaskStatus.Terminal, background: false),
        ]);

        var byKind = await storage.ScanTasksAsync(new TaskQuery { Kind = "kind.a" }, 10);
        Assert.Equal([1L, 3L], byKind.Items.Select(t => t.Id.Value));

        var byStatus = await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Running }, 10);
        Assert.Equal([2L], byStatus.Items.Select(t => t.Id.Value));

        var byBackground = await storage.ScanTasksAsync(new TaskQuery { Background = true }, 10);
        Assert.Equal([2L], byBackground.Items.Select(t => t.Id.Value));
    }

    [Fact]
    public async Task SubmissionQueryFiltersByStatus()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.Submission(new SubmissionRecord.InputRecord
            {
                Id = SubmissionId.From(1),
                ConversationId = Conversation,
                Status = SubmissionStatus.Queued,
            }),
            new StorageWrite.Submission(new SubmissionRecord.WriteRecord
            {
                Id = SubmissionId.From(2),
                ConversationId = Conversation,
                Status = SubmissionStatus.Done,
            }),
        ]);

        var queued = await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Queued }, 10);
        var done = await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Done }, 10);

        Assert.Equal([1L], queued.Items.Select(s => s.Id.Value));
        Assert.Equal([2L], done.Items.Select(s => s.Id.Value));
    }

    // ─── 文档：创建拷贝 / delta 重放 / 附加状态 / 发布 ────────────────────

    [Fact]
    public async Task DocumentCreateCopiesAndRetires()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var childId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([ConversationWrite(childId)]);
        var sourceId = await storage.MintIdAsync<DocumentId>();
        var copyId = await storage.MintIdAsync<DocumentId>();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                ConversationDocCreate(sourceId, "doc.conversation", rootId),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1d })),
        ]);

        // 拷贝落在子对话作用域（TS 仅允许对话作用域文档互拷）。
        await storage.CommitAsync([
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(copyId, "doc.conversation", childId),
                new DocumentCopySource { Id = sourceId, At = new DocumentPoint.Current() }),
        ]);

        var documents = await storage.ScanDocumentsAsync(new DocumentQuery
        {
            Scope = new DocumentScope.ConversationScope(childId),
            At = new DocumentPoint.Current(),
        }, 10);
        Assert.Equal([copyId.Value], documents.Items.Select(d => d.Id.Value));
        // 拷贝是独立的 base：改变来源不影响副本。
        await storage.CommitAsync([
            new StorageWrite.DocumentChange(sourceId, new DocumentContent.Delta(1, [SetCount(7d)])),
        ]);
        Assert.Equal(1d, (await storage.GetDocumentAsync(copyId, new DocumentPoint.Current()))!.Value["count"]);
        Assert.Equal(7d, (await storage.GetDocumentAsync(sourceId, new DocumentPoint.Current()))!.Value["count"]);

        await storage.CommitAsync([new StorageWrite.DocumentRetire(copyId)]);
        var afterRetire = await storage.ScanDocumentsAsync(new DocumentQuery
        {
            Scope = new DocumentScope.ConversationScope(childId),
            At = new DocumentPoint.Current(),
        }, 10);
        Assert.Empty(afterRetire.Items);
    }

    [Fact]
    public async Task DocumentDeltaReplaysOps()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(DocumentId.From(1)),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1d })),
        ]);

        var state = await storage.AttachDocumentAsync(DocumentId.From(1));
        Assert.NotNull(state);
        Assert.Equal(1d, state!.Value!["count"]);

        await storage.CommitAsync([
            new StorageWrite.DocumentChange(DocumentId.From(1), new DocumentContent.Delta(1, [SetCount(42d)])),
        ]);

        Assert.Equal(42d, state.Value!["count"]);
    }

    [Fact]
    public async Task AttachReturnsNullForUnknownIncarnation()
    {
        var storage = new MemoryStorage();
        var state = await storage.AttachDocumentAsync(DocumentId.From(999));
        Assert.Null(state);
    }

    [Fact]
    public async Task PublicationsCarrySeqAndChanges()
    {
        var storage = new MemoryStorage();
        var received = new TaskCompletionSource<CommitPublication>();
        await storage.SubscribeAsync(publication =>
        {
            received.TrySetResult(publication);
            return Task.CompletedTask;
        });

        await storage.CommitAsync([ConversationWrite(Conversation)]);

        var publication = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1L, publication.Seq.Value);
        var change = Assert.IsType<CommitChange.ConversationTable>(Assert.Single(publication.Changes));
        Assert.Equal(Conversation, change.Value.Id);
    }

    [Fact]
    public async Task ListenerIsUniqueAndNeverInvokedInline()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(DocumentId.From(1)),
                new DocumentContent.Base(1, new Dictionary<string, object?>())),
        ]);

        var state = await storage.AttachDocumentAsync(DocumentId.From(1));
        Assert.NotNull(state);

        var delivered = new TaskCompletionSource<Context>();
        state!.Start(_ =>
        {
            delivered.TrySetResult(Context.Background);
            return Task.CompletedTask;
        });
        // 第二次安装被忽略（唯一监听器）。
        state.Start(_ => Task.CompletedTask);
        await storage.CommitAsync([
            new StorageWrite.DocumentChange(DocumentId.From(1), new DocumentContent.Delta(1, [SetCount(1d)])),
        ]);

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopIsIdempotentAndStopsFutureCallbacks()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(DocumentId.From(1)),
                new DocumentContent.Base(1, new Dictionary<string, object?>())),
        ]);
        var state = (await storage.AttachDocumentAsync(DocumentId.From(1)))!;

        var first = await state.Stop();
        var second = await state.Stop();
        Assert.IsType<WatchEnd.Stopped>(first);
        Assert.IsType<WatchEnd.Stopped>(second);
    }

    // ─── conformance：根对话保留 ID 与全局命名空间 ────────────────────────

    [Fact]
    public async Task MintIdReservesRootConversationId()
    {
        var storage = new MemoryStorage();
        // ID 1 保留给不可变根对话；mint 从 2 开始。
        Assert.Equal(2L, (await storage.MintIdAsync<ConversationId>()).Value);
        await CreateRoot(storage);
        Assert.Equal(DurableIdConstants.RootConversation, (await storage.GetConversationAsync(DurableIdConstants.RootConversation))!.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync(
            [ConversationWrite(DurableIdConstants.RootConversation)]));
        Assert.Contains("already belongs to conversation", error.Message);
    }

    [Fact]
    public async Task MintIdTracksExplicitIdsAndRejectsExhaustion()
    {
        var storage = new MemoryStorage();
        await CreateRoot(storage);
        await storage.CommitAsync([EntryWrite(DurableIdConstants.RootConversation, 100)]);
        // 显式 ID 把命名空间推到其后。
        Assert.Equal(101L, (await storage.MintIdAsync<EntryId>()).Value);

        // 全局单一命名空间：条目占用的 ID 不能被任务复用。
        var clash = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync(
            [new StorageWrite.Task(PendingTask(TaskId<object?>.From(100), DurableIdConstants.RootConversation))]));
        Assert.Contains("already belongs to entry", clash.Message);

        await storage.CommitAsync([EntryWrite(DurableIdConstants.RootConversation, long.MaxValue, "last-id")]);
        var exhausted = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.MintIdAsync<EntryId>());
        Assert.Contains("ID space is exhausted", exhausted.Message);
        var exhaustedAgain = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.MintIdAsync<EntryId>());
        Assert.Contains("ID space is exhausted", exhaustedAgain.Message);
    }

    // ─── conformance：原子性与回滚 ────────────────────────────────────────

    [Fact]
    public async Task CommitIsAtomicAndRollsBackOnFailure()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var taskId = await storage.MintIdAsync<TaskId<object?>>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var task = PendingTask(taskId, rootId);
        var input = InputSubmission(submissionId, rootId, "request-1", SubmissionStatus.Placed, entryId);
        var initialSeq = await storage.CommitAsync([
            EntryWrite(rootId, entryId.Value, "user", data: new Dictionary<string, object?> { ["text"] = "hello" }),
            new StorageWrite.Task(task),
            new StorageWrite.Submission(input),
        ]);

        var stored = await storage.GetEntryAsync(entryId);
        Assert.NotNull(stored);
        Assert.Equal("hello", ((IReadOnlyDictionary<string, object?>)stored!.Entry.Data!)["text"]);
        Assert.Equal(initialSeq, stored.CommitSeq);
        Assert.Equal(Json(task), Json((await storage.GetTaskAsync(taskId))!));
        Assert.Equal(input.Status, Assert.IsType<SubmissionRecord.InputRecord>(await storage.GetSubmissionAsync(submissionId)).Status);

        var transientEntryId = await storage.MintIdAsync<EntryId>();
        var running = task with { State = new TaskState { Status = TaskStatus.Running, Checkpoint = new Dictionary<string, object?> { ["phase"] = "effect" } } };
        var doneInput = input with { Status = SubmissionStatus.Done, Answer = transientEntryId };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync([
            new StorageWrite.Task(running),
            new StorageWrite.Submission(doneInput),
            EntryWrite(rootId, transientEntryId.Value, "assistant"),
            // 根对话已存在：整批拒绝。
            ConversationWrite(rootId),
        ]));
        Assert.Contains("already belongs to conversation", error.Message);
        Assert.Equal(Json(task), Json((await storage.GetTaskAsync(taskId))!));
        Assert.Equal(SubmissionStatus.Placed, Assert.IsType<SubmissionRecord.InputRecord>(await storage.GetSubmissionAsync(submissionId)).Status);
        Assert.Null(await storage.GetEntryAsync(transientEntryId));
        var afterRollbackSeq = await storage.CommitAsync([
            EntryWrite(rootId, (await storage.MintIdAsync<EntryId>()).Value, "after-rollback"),
        ]);
        Assert.True(afterRollbackSeq.Value > initialSeq.Value);
    }

    [Fact]
    public async Task DetachesRetainedWritesAndReturnedRecords()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var taskId = await storage.MintIdAsync<TaskId<object?>>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var entryData = new Dictionary<string, object?> { ["nested"] = new List<object?> { 1d, 2d } };
        var checkpoint = new Dictionary<string, object?>
        {
            ["phase"] = "ready",
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1d },
        };
        var detail = new Dictionary<string, object?> { ["codes"] = new List<object?> { "initial" } };
        await storage.CommitAsync([
            EntryWrite(rootId, entryId.Value, "note", data: entryData),
            new StorageWrite.Task(PendingTask(taskId, rootId) with
            {
                State = new TaskState { Status = TaskStatus.Pending, Checkpoint = checkpoint },
            }),
            new StorageWrite.Submission(new SubmissionRecord.InputRecord
            {
                Id = submissionId,
                ConversationId = rootId,
                Status = SubmissionStatus.Unanswered,
                Reason = "failed",
                Detail = detail,
            }),
        ]);

        // 写入方Mutation不影响已保留记录。
        ((List<object?>)entryData["nested"]!).Add(3d);
        ((Dictionary<string, object?>)checkpoint["nested"]!)["count"] = 2d;
        ((List<object?>)detail["codes"]!).Add("mutated");
        Assert.Equal(Json(new Dictionary<string, object?> { ["nested"] = new List<object?> { 1d, 2d } }),
            Json((await storage.GetEntryAsync(entryId))!.Entry.Data));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["phase"] = "ready",
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1d },
        }), Json((await storage.GetTaskAsync(taskId))!.State.Checkpoint));
        Assert.Equal(Json(new Dictionary<string, object?> { ["codes"] = new List<object?> { "initial" } }),
            Json(Assert.IsType<SubmissionRecord.InputRecord>(await storage.GetSubmissionAsync(submissionId)).Detail));

        // 读出方Mutation不影响后续读取。
        var readEntry = (await storage.GetEntryAsync(entryId))!.Entry;
        ((List<object?>)((IReadOnlyDictionary<string, object?>)readEntry.Data!)["nested"]!).Add(9d);
        var readTask = await storage.GetTaskAsync(taskId);
        ((Dictionary<string, object?>)((IReadOnlyDictionary<string, object?>)readTask!.State.Checkpoint!)["nested"]!)["count"] = 9d;
        var readInput = Assert.IsType<SubmissionRecord.InputRecord>(await storage.GetSubmissionAsync(submissionId));
        ((List<object?>)((IReadOnlyDictionary<string, object?>)readInput.Detail!)["codes"]!).Add("read mutation");

        Assert.Equal(Json(new Dictionary<string, object?> { ["nested"] = new List<object?> { 1d, 2d } }),
            Json((await storage.GetEntryAsync(entryId))!.Entry.Data));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["phase"] = "ready",
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1d },
        }), Json((await storage.GetTaskAsync(taskId))!.State.Checkpoint));
        Assert.Equal(Json(new Dictionary<string, object?> { ["codes"] = new List<object?> { "initial" } }),
            Json(Assert.IsType<SubmissionRecord.InputRecord>(await storage.GetSubmissionAsync(submissionId)).Detail));
    }

    [Fact]
    public async Task DetachesPrototypeLikeJsonKeys()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var entryId = await storage.MintIdAsync<EntryId>();
        var data = new Dictionary<string, object?>
        {
            ["__proto__"] = new Dictionary<string, object?> { ["polluted"] = false },
            ["constructor"] = new Dictionary<string, object?> { ["label"] = "stored" },
            ["toString"] = "value",
        };
        await storage.CommitAsync([EntryWrite(rootId, entryId.Value, "note", data: data)]);

        ((Dictionary<string, object?>)data["__proto__"]!)["polluted"] = true;
        ((Dictionary<string, object?>)data["constructor"]!)["label"] = "mutated";
        var firstRead = (IReadOnlyDictionary<string, object?>)(await storage.GetEntryAsync(entryId))!.Entry.Data!;
        Assert.True(firstRead.ContainsKey("__proto__"));
        Assert.Equal(Json(new Dictionary<string, object?> { ["polluted"] = false }), Json(firstRead["__proto__"]));
        Assert.Equal(Json(new Dictionary<string, object?> { ["label"] = "stored" }), Json(firstRead["constructor"]));
        Assert.Equal("value", firstRead["toString"]);

        ((Dictionary<string, object?>)firstRead["__proto__"]!)["polluted"] = true;
        var secondRead = (IReadOnlyDictionary<string, object?>)(await storage.GetEntryAsync(entryId))!.Entry.Data!;
        Assert.Equal(Json(new Dictionary<string, object?> { ["polluted"] = false }), Json(secondRead["__proto__"]));
        Assert.Equal(Json(new Dictionary<string, object?> { ["label"] = "stored" }), Json(secondRead["constructor"]));
        Assert.Equal("value", secondRead["toString"]);
    }

    // ─── conformance：条目索引 / 游标 / 分页 ──────────────────────────────

    [Fact]
    public async Task IndexesEntriesCommittedOutOfIdOrder()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        await storage.CommitAsync([
            EntryWrite(rootId, 30),
            EntryWrite(rootId, 10),
            EntryWrite(rootId, 20, "marker", head: EntryId.From(10)),
        ]);

        var page = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 10);
        Assert.Equal([30L, 20L, 10L], page.Items.Select(e => e.Id.Value));
        Assert.Equal(20L, (await storage.FindLatestHeadMarkerAsync(rootId, null))!.Id.Value);
    }

    [Fact]
    public async Task ContinuesEntryCursorBelowLastItemAfterNewerCommit()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var oldestId = await storage.MintIdAsync<EntryId>();
        var middleId = await storage.MintIdAsync<EntryId>();
        var newestId = await storage.MintIdAsync<EntryId>();
        await storage.CommitAsync([
            EntryWrite(rootId, oldestId.Value),
            EntryWrite(rootId, middleId.Value),
            EntryWrite(rootId, newestId.Value),
        ]);

        var first = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 2);
        Assert.Equal([newestId.Value, middleId.Value], first.Items.Select(e => e.Id.Value));
        var appendedId = await storage.MintIdAsync<EntryId>();
        await storage.CommitAsync([EntryWrite(rootId, appendedId.Value)]);
        // 游标固定在其最后一项之下；更新的提交不渗入。
        var second = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 2, first.Next);
        Assert.Equal([oldestId.Value], second.Items.Select(e => e.Id.Value));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task PaginatesConversationsByOpaqueCursor()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var secondId = await storage.MintIdAsync<ConversationId>();
        var thirdId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([
            ConversationWrite(thirdId),
            ConversationWrite(secondId),
        ]);

        var first = await storage.ScanConversationsAsync(null, 2);
        Assert.Equal([rootId.Value, secondId.Value], first.Items.Select(c => c.Id.Value));
        Assert.NotNull(first.Next);
        // 游标是后端持有的 JSON 状态：JSON 往返后原样可用。
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, object?>>(Json(first.Next))!;
        var second = await storage.ScanConversationsAsync(null, 2, roundTripped);
        Assert.Equal([thirdId.Value], second.Items.Select(c => c.Id.Value));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task FiltersAndPagesConversationsByOwnerEdges()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var otherOwnerId = await storage.MintIdAsync<ConversationId>();
        var firstTaskId = await storage.MintIdAsync<TaskId<object?>>();
        var secondTaskId = await storage.MintIdAsync<TaskId<object?>>();
        var firstId = await storage.MintIdAsync<ConversationId>();
        var secondId = await storage.MintIdAsync<ConversationId>();
        var thirdId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([
            ConversationWrite(otherOwnerId),
            ConversationRecordWrite(new ConversationRecord
            {
                Id = firstId,
                Owner = new ConversationOwner { ConversationId = rootId, TaskId = firstTaskId },
            }),
            ConversationRecordWrite(new ConversationRecord
            {
                Id = secondId,
                Owner = new ConversationOwner { ConversationId = rootId, TaskId = secondTaskId },
            }),
            ConversationRecordWrite(new ConversationRecord
            {
                Id = thirdId,
                Owner = new ConversationOwner { ConversationId = otherOwnerId, TaskId = firstTaskId },
            }),
        ]);

        var first = await storage.ScanConversationsAsync(new ConversationQuery { OwnerConversationId = rootId }, 1);
        Assert.Equal([firstId.Value], first.Items.Select(c => c.Id.Value));
        Assert.NotNull(first.Next);
        var second = await storage.ScanConversationsAsync(new ConversationQuery { OwnerConversationId = rootId }, 1, first.Next);
        Assert.Equal([secondId.Value], second.Items.Select(c => c.Id.Value));
        Assert.Null(second.Next);
        var byTask = await storage.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = firstTaskId }, 10);
        Assert.Equal([firstId.Value, thirdId.Value], byTask.Items.Select(c => c.Id.Value));
        var byBoth = await storage.ScanConversationsAsync(
            new ConversationQuery { OwnerConversationId = rootId, OwnerTaskId = firstTaskId }, 10);
        Assert.Equal([firstId.Value], byBoth.Items.Select(c => c.Id.Value));
    }

    // ─── conformance：fork 可见性 / head marker / as-of 读 ────────────────

    [Fact]
    public async Task ScansDeepForkHistoryNewestFirstThroughAncestorCaps()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var rootFirst = await storage.MintIdAsync<EntryId>();
        var rootForkPoint = await storage.MintIdAsync<EntryId>();
        var rootExcludedSameCommit = await storage.MintIdAsync<EntryId>();
        var rootEntriesSeq = await storage.CommitAsync([
            EntryWrite(rootId, rootFirst.Value),
            EntryWrite(rootId, rootForkPoint.Value, "marker", head: rootFirst),
            EntryWrite(rootId, rootExcludedSameCommit.Value),
        ]);
        var childId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([ConversationRecordWrite(new ConversationRecord
        {
            Id = childId,
            Parent = new ConversationParent { ConversationId = rootId, At = rootForkPoint },
        })]);
        var childForkPoint = await storage.MintIdAsync<EntryId>();
        var childExcluded = await storage.MintIdAsync<EntryId>();
        await storage.CommitAsync([
            EntryWrite(childId, childForkPoint.Value, "note"),
            EntryWrite(childId, childExcluded.Value),
        ]);
        var rootExcludedLater = await storage.MintIdAsync<EntryId>();
        await storage.CommitAsync([EntryWrite(rootId, rootExcludedLater.Value)]);
        var grandchildId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([ConversationRecordWrite(new ConversationRecord
        {
            Id = grandchildId,
            Parent = new ConversationParent { ConversationId = childId, At = childForkPoint },
        })]);
        var grandchildHead = await storage.MintIdAsync<EntryId>();
        var grandchildTail = await storage.MintIdAsync<EntryId>();
        var grandchildEntriesSeq = await storage.CommitAsync([
            EntryWrite(grandchildId, grandchildHead.Value, "marker", head: grandchildHead),
            EntryWrite(grandchildId, grandchildTail.Value),
        ]);
        var childExcludedLater = await storage.MintIdAsync<EntryId>();
        await storage.CommitAsync([EntryWrite(childId, childExcludedLater.Value)]);

        // 最新优先扫描穿过每一层祖先含界切点。
        var first = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2);
        Assert.Equal([grandchildTail.Value, grandchildHead.Value], first.Items.Select(e => e.Id.Value));
        var second = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2, first.Next);
        Assert.Equal([childForkPoint.Value, rootForkPoint.Value], second.Items.Select(e => e.Id.Value));
        var third = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2, second.Next);
        Assert.Equal([rootFirst.Value], third.Items.Select(e => e.Id.Value));
        Assert.Null(third.Next);

        // head marker：当前点与历史切点。
        var currentMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, null);
        Assert.Equal(grandchildHead.Value, currentMarker!.Id.Value);
        Assert.Equal(grandchildHead.Value, currentMarker.Head!.Value.Value);
        var historicalMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, childForkPoint);
        Assert.Equal(rootForkPoint.Value, historicalMarker!.Id.Value);
        Assert.Equal(rootFirst.Value, historicalMarker.Head!.Value.Value);
        Assert.Null(await storage.FindLatestHeadMarkerAsync(grandchildId, rootFirst));

        // 活动上下文：从当前 head 起、含界于切点的扫描。
        var activeFirst = await storage.ScanEntriesAsync(
            new EntryQuery { ConversationId = grandchildId, MinEntryId = currentMarker.Head }, 1);
        Assert.Equal([grandchildTail.Value], activeFirst.Items.Select(e => e.Id.Value));
        Assert.NotNull(activeFirst.Next);
        var activeSecond = await storage.ScanEntriesAsync(
            new EntryQuery { ConversationId = grandchildId, MinEntryId = currentMarker.Head }, 1, activeFirst.Next);
        Assert.Equal([grandchildHead.Value], activeSecond.Items.Select(e => e.Id.Value));
        Assert.Null(activeSecond.Next);

        var historical = await storage.ScanEntriesAsync(new EntryQuery
        {
            ConversationId = grandchildId,
            MinEntryId = historicalMarker.Head,
            MaxEntryId = childForkPoint,
        }, 10);
        Assert.Equal([childForkPoint.Value, rootForkPoint.Value, rootFirst.Value], historical.Items.Select(e => e.Id.Value));

        // 精确 ID 读携带提交序列。
        var rootFirstStored = await storage.GetEntryAsync(rootFirst);
        Assert.NotNull(rootFirstStored);
        Assert.Equal(rootEntriesSeq, rootFirstStored!.CommitSeq);
        Assert.Equal(rootEntriesSeq, (await storage.GetEntryAsync(rootForkPoint))!.CommitSeq);
        Assert.Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildHead))!.CommitSeq);
        Assert.Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildTail))!.CommitSeq);
        Assert.Null(await storage.GetEntryAsync(EntryId.From(999_999)));

        // 经请求对话祖先链的可见性。
        Assert.NotNull(await storage.GetEntryAsync(grandchildId, rootFirst));
        Assert.Equal(childId, (await storage.GetEntryAsync(grandchildId, childForkPoint))!.Entry.ConversationId);
        Assert.Equal(grandchildEntriesSeq, (await storage.GetEntryAsync(grandchildId, grandchildTail))!.CommitSeq);
        Assert.Null(await storage.GetEntryAsync(grandchildId, rootExcludedSameCommit));
        Assert.Null(await storage.GetEntryAsync(grandchildId, rootExcludedLater));
        Assert.Null(await storage.GetEntryAsync(grandchildId, childExcluded));
        Assert.Null(await storage.GetEntryAsync(grandchildId, childExcludedLater));
        Assert.Null(await storage.GetEntryAsync(rootId, grandchildHead));
        Assert.Null(await storage.GetEntryAsync(grandchildId, EntryId.From(999_999)));

        var unknown = ConversationId.From(999_999);
        var unknownError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetEntryAsync(unknown, rootFirst));
        Assert.Contains("Unknown conversation", unknownError.Message);
        var unknownScan = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ScanEntriesAsync(new EntryQuery { ConversationId = unknown }, 10));
        Assert.Contains("Unknown conversation", unknownScan.Message);
    }

    // ─── conformance：任务记录替换与状态扫描 ──────────────────────────────

    [Fact]
    public async Task ReplacesCompleteTaskRecordsAndPagesFilteredTaskScans()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<TaskId<object?>>();
        var secondId = await storage.MintIdAsync<TaskId<object?>>();
        var thirdId = await storage.MintIdAsync<TaskId<object?>>();
        var first = PendingTask(firstId, rootId) with
        {
            Memos = new Dictionary<string, object?> { ["winner"] = "first" },
        };
        var second = PendingTask(secondId, rootId) with { Background = true };
        var third = PendingTask(thirdId, rootId) with { AbortRequested = true };
        await storage.CommitAsync([
            new StorageWrite.Task(first),
            new StorageWrite.Task(second),
            new StorageWrite.Task(third),
        ]);

        var running = first with
        {
            State = new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "effect", ["attempt"] = 1d },
            },
            AbortRequested = true,
        };
        await storage.CommitAsync([new StorageWrite.Task(running)]);
        Assert.Equal(Json(running), Json((await storage.GetTaskAsync(firstId))!));

        var terminal = new TaskRecord
        {
            Id = firstId,
            ConversationId = rootId,
            Kind = first.Kind,
            Version = first.Version,
            Input = first.Input,
            State = new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Completed,
                    Result = new Dictionary<string, object?> { ["entryId"] = 99d },
                },
            },
            Background = false,
            AbortRequested = true,
        };
        await storage.CommitAsync([new StorageWrite.Task(terminal)]);
        Assert.Equal(Json(terminal), Json((await storage.GetTaskAsync(firstId))!));

        var pendingPage = await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 1);
        Assert.Equal([secondId.Value], pendingPage.Items.Select(t => t.Id.Value));
        Assert.NotNull(pendingPage.Next);
        var pendingSecond = await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 1, pendingPage.Next);
        Assert.Equal([thirdId.Value], pendingSecond.Items.Select(t => t.Id.Value));
        var terminalScan = await storage.ScanTasksAsync(
            new TaskQuery { Status = TaskStatus.Terminal, AbortRequested = true }, 10);
        Assert.Equal(Json(terminal), Json(Assert.Single(terminalScan.Items)));
        var backgroundScan = await storage.ScanTasksAsync(new TaskQuery { Background = true }, 10);
        Assert.Equal([secondId.Value], backgroundScan.Items.Select(t => t.Id.Value));
    }

    [Fact]
    public async Task ScansWaitingAndCompletingTasksByStatus()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var ownerId = await storage.MintIdAsync<TaskId<object?>>();
        var waitingId = await storage.MintIdAsync<TaskId<object?>>();
        var completingId = await storage.MintIdAsync<TaskId<object?>>();
        var owner = PendingTask(ownerId, rootId);
        var waiting = PendingTask(waitingId, rootId) with
        {
            Owner = ownerId,
            State = new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "next" },
                On = [ownerId],
                Policy = JoinPolicy.AllSettled,
            },
            Memos = new Dictionary<string, object?> { ["kept"] = true },
        };
        var completing = PendingTask(completingId, rootId) with
        {
            Owner = ownerId,
            State = new TaskState
            {
                Status = TaskStatus.Completing,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError { Message = "held" },
                },
            },
        };
        await storage.CommitAsync([
            new StorageWrite.Task(owner),
            new StorageWrite.Task(waiting),
            new StorageWrite.Task(completing),
        ]);
        Assert.Equal(Json(waiting), Json((await storage.GetTaskAsync(waitingId))!));
        Assert.Equal(Json(completing), Json((await storage.GetTaskAsync(completingId))!));

        Assert.Equal(Json(waiting), Json(Assert.Single(
            (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Waiting }, 10)).Items)));
        Assert.Equal(Json(completing), Json(Assert.Single(
            (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Completing }, 10)).Items)));
        Assert.Equal([ownerId.Value],
            (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 10)).Items.Select(t => t.Id.Value));

        var terminal = completing with
        {
            State = new TaskState { Status = TaskStatus.Terminal, Outcome = completing.State.Outcome },
        };
        await storage.CommitAsync([new StorageWrite.Task(terminal)]);
        Assert.Empty((await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Completing }, 10)).Items);
        Assert.Equal(Json(terminal), Json(Assert.Single(
            (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Terminal }, 10)).Items)));
    }

    // ─── conformance：提交记录 / requestId 索引 ───────────────────────────

    [Fact]
    public async Task IndexesRequestIdsPerConversationAndReplacesCompleteSubmissionRecords()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var secondConversationId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([ConversationWrite(secondConversationId)]);
        var firstId = await storage.MintIdAsync<SubmissionId>();
        var secondId = await storage.MintIdAsync<SubmissionId>();
        var otherConversationId = await storage.MintIdAsync<SubmissionId>();
        var first = InputSubmission(firstId, rootId, "same", SubmissionStatus.Queued);
        var second = InputSubmission(secondId, rootId, "other", SubmissionStatus.Queued);
        var otherConversation = InputSubmission(otherConversationId, secondConversationId, "same", SubmissionStatus.Queued);
        await storage.CommitAsync([
            new StorageWrite.Submission(first),
            new StorageWrite.Submission(second),
            new StorageWrite.Submission(otherConversation),
        ]);

        Assert.Equal(firstId, (await storage.GetSubmissionByRequestAsync(rootId, "same"))!.Id);
        Assert.Equal(otherConversationId, (await storage.GetSubmissionByRequestAsync(secondConversationId, "same"))!.Id);

        var placedSecond = second with { Status = SubmissionStatus.Placed, Entry = await storage.MintIdAsync<EntryId>() };
        await storage.CommitAsync([new StorageWrite.Submission(placedSecond)]);
        Assert.Equal(Json(placedSecond), Json((await storage.GetSubmissionAsync(secondId))!));
        Assert.Equal(placedSecond.Id, (await storage.GetSubmissionByRequestAsync(rootId, "other"))!.Id);

        Assert.Equal([firstId.Value, secondId.Value, otherConversationId.Value], await SubmissionIds(storage, new SubmissionQuery()));
        Assert.Equal([firstId.Value, secondId.Value], await SubmissionIds(storage, new SubmissionQuery { ConversationId = rootId }));
        // 状态变化把记录在状态扫描之间移动。
        Assert.Equal([firstId.Value, otherConversationId.Value], await SubmissionIds(storage, new SubmissionQuery { Status = SubmissionStatus.Queued }));
        Assert.Equal([secondId.Value], await SubmissionIds(storage, new SubmissionQuery { Status = SubmissionStatus.Placed }));
        Assert.Equal([otherConversationId.Value], await SubmissionIds(storage,
            new SubmissionQuery { ConversationId = secondConversationId, Status = SubmissionStatus.Queued }));
        Assert.Empty(await SubmissionIds(storage,
            new SubmissionQuery { ConversationId = secondConversationId, Status = SubmissionStatus.Placed }));
        Assert.Equal(Json(placedSecond), Json(Assert.Single(
            (await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Placed }, 10)).Items)));
    }

    [Fact]
    public async Task StoresPassiveWriteSubmissionsWithoutInputOnlyLifecycleStates()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var doneId = await storage.MintIdAsync<SubmissionId>();
        var failedId = await storage.MintIdAsync<SubmissionId>();
        var queuedDone = WriteSubmission(doneId, rootId, "passive-done", SubmissionStatus.Queued);
        var queuedFailed = WriteSubmission(failedId, rootId, "passive-failed", SubmissionStatus.Queued);
        await storage.CommitAsync([
            new StorageWrite.Submission(queuedDone),
            new StorageWrite.Submission(queuedFailed),
        ]);

        var done = queuedDone with { Status = SubmissionStatus.Done, Entry = await storage.MintIdAsync<EntryId>() };
        var unanswered = queuedFailed with
        {
            Status = SubmissionStatus.Unanswered,
            Reason = "closed",
            Detail = new Dictionary<string, object?> { ["retryable"] = false },
        };
        await storage.CommitAsync([
            new StorageWrite.Submission(done),
            new StorageWrite.Submission(unanswered),
        ]);

        Assert.Equal(Json(done), Json((await storage.GetSubmissionAsync(doneId))!));
        Assert.Equal(done.Id, (await storage.GetSubmissionByRequestAsync(rootId, "passive-done"))!.Id);
        Assert.Equal(Json(unanswered), Json((await storage.GetSubmissionAsync(failedId))!));
        Assert.Equal(unanswered.Id, (await storage.GetSubmissionByRequestAsync(rootId, "passive-failed"))!.Id);
    }

    // ─── conformance：文档重放 / as-of 点查询 / 半开化身 ──────────────────

    [Fact]
    public async Task ReconstructsRewindableDocumentsAndPreservesHalfOpenIncarnations()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var record = ConversationDocCreate(firstId, "conversation.notes", rootId);
        var initial = new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1d },
        };
        var createdAt = await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, initial)),
        ]);
        var appended = new List<object?> { "b" };
        var changedAt = await storage.CommitAsync([
            new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(1,
            [
                new DeltaOp.Splice(new Path([Seg.Key("items")]), 1, 0, appended),
                new DeltaOp.Set(new Path([Seg.Key("nested"), Seg.Key("count")]), 2d),
            ])),
        ]);

        // 调用方Mutation已脱钩。
        ((List<object?>)initial["items"]!).Add("caller mutation");
        appended.Add("caller mutation");
        var atCreate = await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(createdAt));
        Assert.NotNull(atCreate);
        Assert.Equal(1, atCreate!.Version);
        Assert.Equal(0L, atCreate.DeltasSinceBase);
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1d },
        }), Json(atCreate.Value));
        var changed = await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(changedAt));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a", "b" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 2d },
        }), Json(changed!.Value));
        Assert.Equal(1L, changed.DeltasSinceBase);
        ((List<object?>)changed.Value["items"]!).Add("read mutation");
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a", "b" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 2d },
        }), Json((await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.Value));

        var checkpointAt = await storage.CommitAsync([
            new StorageWrite.DocumentChange(firstId, new DocumentContent.Base(2, new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "checkpoint" },
                ["nested"] = new Dictionary<string, object?> { ["count"] = 3d },
            })),
        ]);
        var replacedAt = await storage.CommitAsync([
            new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(2,
            [
                new DeltaOp.Replace(new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "replacement" },
                    ["nested"] = new Dictionary<string, object?> { ["count"] = 4d },
                }),
            ])),
        ]);
        var atChangedAgain = await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(changedAt));
        Assert.Equal(1, atChangedAgain!.Version);
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a", "b" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 2d },
        }), Json(atChangedAgain.Value));
        var atCheckpoint = await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(checkpointAt));
        Assert.Equal(2, atCheckpoint!.Version);
        Assert.Equal(0L, atCheckpoint.DeltasSinceBase);
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "checkpoint" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 3d },
        }), Json(atCheckpoint.Value));
        var atReplaced = await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(replacedAt));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "replacement" },
            ["nested"] = new Dictionary<string, object?> { ["count"] = 4d },
        }), Json(atReplaced!.Value));
        Assert.Equal(1L, atReplaced.DeltasSinceBase);
        Assert.Equal(1L, (await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.DeltasSinceBase);

        // 同一提交内：新化身创建 + 旧化身退役 + 旧化身 delta（半开 lifetime）。
        var secondId = await storage.MintIdAsync<DocumentId>();
        var retiredAt = await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(record with { Id = secondId },
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["items"] = new List<object?> { "new" } })),
            new StorageWrite.DocumentRetire(firstId),
            new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(2,
            [
                new DeltaOp.Set(new Path([Seg.Key("retiring")]), true),
            ])),
        ]);
        var address = new DocumentAddress { Kind = record.Kind, Scope = record.Scope };
        Assert.Equal(firstId, (await storage.FindDocumentAsync(address, new DocumentPoint.AtSeq(changedAt)))!.Id);
        var atRetired = await storage.FindDocumentAsync(address, new DocumentPoint.AtSeq(retiredAt));
        Assert.NotNull(atRetired);
        Assert.Equal(secondId, atRetired!.Id);
        Assert.Equal(retiredAt, atRetired.CreatedAt);
        Assert.Equal([firstId.Value], (await storage.ScanDocumentsAsync(
            new DocumentQuery { Scope = record.Scope, At = new DocumentPoint.AtSeq(changedAt) }, 10)).Items.Select(d => d.Id.Value));
        Assert.Equal([secondId.Value], (await storage.ScanDocumentsAsync(
            new DocumentQuery { Scope = record.Scope, At = new DocumentPoint.AtSeq(retiredAt) }, 10)).Items.Select(d => d.Id.Value));
        Assert.Null(await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(retiredAt)));
        Assert.Equal(Json(new Dictionary<string, object?> { ["items"] = new List<object?> { "new" } }),
            Json((await storage.GetDocumentAsync(secondId, new DocumentPoint.Current()))!.Value));
    }

    [Fact]
    public async Task RejectsHistoricalReadsOfCurrentOnlyDocumentsAndVersionRewinds()
    {
        var storage = new MemoryStorage();
        await CreateRoot(storage);
        var id = await storage.MintIdAsync<DocumentId>();
        var record = SessionCreate(id, "session.settings");
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1d })),
        ]);
        await storage.CommitAsync([
            new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1, [SetCount(2d)])),
        ]);
        var migratedAt = await storage.CommitAsync([
            new StorageWrite.DocumentChange(id, new DocumentContent.Base(2, new Dictionary<string, object?> { ["count"] = 3d })),
        ]);
        var current = await storage.GetDocumentAsync(id, new DocumentPoint.Current());
        Assert.Equal(2, current!.Version);
        Assert.Equal(Json(new Dictionary<string, object?> { ["count"] = 3d }), Json(current.Value));

        var historical = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(migratedAt)));
        Assert.Contains("does not retain historical content", historical.Message);

        var rewind = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync([
            new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1, [SetCount(4d)])),
        ]));
        Assert.Contains("version transition requires a base", rewind.Message);
        Assert.Equal(Json(new Dictionary<string, object?> { ["count"] = 3d }),
            Json((await storage.GetDocumentAsync(id, new DocumentPoint.Current()))!.Value));

        await storage.CommitAsync([new StorageWrite.DocumentRetire(id)]);
        Assert.Null(await storage.GetDocumentAsync(id, new DocumentPoint.Current()));
    }

    [Fact]
    public async Task IndexesLogicalAddressesAndExactScopeScansIndependently()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var secondId = await storage.MintIdAsync<DocumentId>();
        var conversationDocId = await storage.MintIdAsync<DocumentId>();
        var taskId = await storage.MintIdAsync<TaskId<object?>>();
        var taskSingletonId = await storage.MintIdAsync<DocumentId>();
        var taskFamilyId = await storage.MintIdAsync<DocumentId>();
        var taskOtherKindId = await storage.MintIdAsync<DocumentId>();
        var createdAt = await storage.CommitAsync([
            new StorageWrite.Task(PendingTask(taskId, rootId)),
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(firstId, "cache", key: "__proto__"),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "first" })),
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(secondId, "cache", key: "constructor"),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "second" })),
            new StorageWrite.DocumentCreateWrite(
                ConversationDocCreate(conversationDocId, "cache", rootId, key: "__proto__") with { History = ConversationHistory.Latest, Fork = ConversationFork.Current },
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "conversation" })),
            new StorageWrite.DocumentCreateWrite(
                TaskCreate(taskSingletonId, "task.cache", taskId),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "singleton" })),
            new StorageWrite.DocumentCreateWrite(
                TaskCreate(taskFamilyId, "task.cache", taskId, key: "member"),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "family" })),
            new StorageWrite.DocumentCreateWrite(
                TaskCreate(taskOtherKindId, "task.other", taskId),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "other" })),
        ]);

        var current = new DocumentPoint.Current();
        Assert.Equal(firstId, (await storage.FindDocumentAsync(
            new DocumentAddress { Kind = "cache", Scope = new DocumentScope.SessionScope(), Key = "__proto__" }, current))!.Id);
        var firstPage = await storage.ScanDocumentsAsync(new DocumentQuery { Scope = new DocumentScope.SessionScope(), At = current }, 1);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.Next);
        var secondPage = await storage.ScanDocumentsAsync(new DocumentQuery { Scope = new DocumentScope.SessionScope(), At = current }, 1, firstPage.Next);
        Assert.Equal([firstId.Value, secondId.Value],
            firstPage.Items.Concat(secondPage.Items).Select(d => d.Id.Value));
        var conversationScope = new DocumentScope.ConversationScope(rootId);
        Assert.Equal([conversationDocId.Value], (await storage.ScanDocumentsAsync(
            new DocumentQuery { Scope = conversationScope, At = current }, 10)).Items.Select(d => d.Id.Value));
        var taskScope = new DocumentScope.TaskScope(taskId);
        Assert.Equal(taskSingletonId, (await storage.FindDocumentAsync(
            new DocumentAddress { Kind = "task.cache", Scope = taskScope }, current))!.Id);
        Assert.Equal(taskFamilyId, (await storage.FindDocumentAsync(
            new DocumentAddress { Kind = "task.cache", Scope = taskScope, Key = "member" }, current))!.Id);
        Assert.Equal([taskSingletonId.Value, taskFamilyId.Value], (await storage.ScanDocumentsAsync(
            new DocumentQuery { Scope = taskScope, At = current, Kind = "task.cache" }, 10)).Items.Select(d => d.Id.Value));

        var historical = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetDocumentAsync(taskSingletonId, new DocumentPoint.AtSeq(createdAt)));
        Assert.Contains("does not retain historical content", historical.Message);
    }

    // ─── conformance：生命周期原子性 / 拷贝独立性 ─────────────────────────

    [Fact]
    public async Task DocumentLifecycleFailuresAreAtomicAndCreatePlusRetireHasEmptyLifetime()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var firstId = await storage.MintIdAsync<DocumentId>();
        var secondId = await storage.MintIdAsync<DocumentId>();
        var record = SessionCreate(firstId, "singleton");
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(record,
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 1d })),
        ]);

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(record with { Id = secondId },
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 2d })),
            new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(1, [])),
        ]));
        Assert.Contains("already has a current incarnation", conflict.Message);
        Assert.Equal(Json(new Dictionary<string, object?> { ["value"] = 1d }),
            Json((await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.Value));
        Assert.Null(await storage.GetDocumentAsync(secondId, new DocumentPoint.Current()));

        // create + retire 同批：空 lifetime，任何点都不可见。
        var emptyId = await storage.MintIdAsync<DocumentId>();
        var emptyAt = await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                ConversationDocCreate(emptyId, "singleton", rootId, key: "empty") with { Fork = ConversationFork.Initial },
                new DocumentContent.Base(1, new Dictionary<string, object?>())),
            new StorageWrite.DocumentRetire(emptyId),
        ]);
        Assert.Null(await storage.GetDocumentAsync(emptyId, new DocumentPoint.Current()));
        Assert.Null(await storage.GetDocumentAsync(emptyId, new DocumentPoint.AtSeq(emptyAt)));
        Assert.Null(await storage.FindDocumentAsync(
            new DocumentAddress
            {
                Kind = "singleton",
                Scope = new DocumentScope.ConversationScope(rootId),
                Key = "empty",
            },
            new DocumentPoint.AtSeq(emptyAt)));
    }

    [Fact]
    public async Task RollsBackRecordTablesAndIndexesWhenDocumentCommandFails()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var taskId = await storage.MintIdAsync<TaskId<object?>>();
        var submissionId = await storage.MintIdAsync<SubmissionId>();
        var documentId = await storage.MintIdAsync<DocumentId>();
        var task = PendingTask(taskId, rootId);
        var submission = InputSubmission(submissionId, rootId, "atomic", SubmissionStatus.Queued);
        var record = SessionCreate(documentId, "atomic");
        var baselineSeq = await storage.CommitAsync([
            new StorageWrite.Task(task),
            new StorageWrite.Submission(submission),
            new StorageWrite.DocumentCreateWrite(record,
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1d })),
        ]);

        var entryId = await storage.MintIdAsync<EntryId>();
        var conflictingDocumentId = await storage.MintIdAsync<DocumentId>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => storage.CommitAsync([
            new StorageWrite.Task(task with
            {
                State = new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = new Dictionary<string, object?> { ["phase"] = "effect" },
                },
            }),
            new StorageWrite.Submission(submission with { Status = SubmissionStatus.Unanswered, Reason = "failed" }),
            EntryWrite(rootId, entryId.Value, "transient"),
            new StorageWrite.DocumentCreateWrite(record with { Id = conflictingDocumentId },
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 2d })),
        ]));
        Assert.Contains("already has a current incarnation", error.Message);

        Assert.Equal(Json(task), Json((await storage.GetTaskAsync(taskId))!));
        Assert.Equal(Json(task), Json(Assert.Single(
            (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 10)).Items)));
        Assert.Equal(submission.Id, (await storage.GetSubmissionByRequestAsync(rootId, "atomic"))!.Id);
        Assert.Null(await storage.GetEntryAsync(entryId));
        Assert.Null(await storage.GetDocumentAsync(conflictingDocumentId, new DocumentPoint.Current()));
        Assert.Equal(documentId, (await storage.FindDocumentAsync(
            new DocumentAddress { Kind = record.Kind, Scope = record.Scope }, new DocumentPoint.Current()))!.Id);
        var afterRollbackSeq = await storage.CommitAsync([
            new StorageWrite.DocumentChange(documentId, new DocumentContent.Delta(1, [SetCount(3d)])),
        ]);
        Assert.True(afterRollbackSeq.Value > baselineSeq.Value);
    }

    [Fact]
    public async Task CopiesDocumentBasesIndependentlyAndRejectsAmbiguousSources()
    {
        var storage = new MemoryStorage();
        var rootId = await CreateRoot(storage);
        var childId = await storage.MintIdAsync<ConversationId>();
        var secondChildId = await storage.MintIdAsync<ConversationId>();
        await storage.CommitAsync([
            ConversationWrite(childId),
            ConversationWrite(secondChildId),
        ]);
        var sourceId = await storage.MintIdAsync<DocumentId>();
        var sourceRecord = ConversationDocCreate(sourceId, "copy.source", rootId);
        var createdAt = await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(sourceRecord, new DocumentContent.Base(2, new Dictionary<string, object?>
            {
                ["count"] = 1d,
                ["rows"] = new List<object?> { new Dictionary<string, object?> { ["value"] = "base" } },
            })),
        ]);
        await storage.CommitAsync([
            new StorageWrite.DocumentChange(sourceId, new DocumentContent.Delta(2,
            [
                new DeltaOp.Set(new Path([Seg.Key("count")]), 2d),
                new DeltaOp.Splice(new Path([Seg.Key("rows")]), 1, 0,
                    [new Dictionary<string, object?> { ["value"] = "current" }]),
            ])),
        ]);

        var historicalCopyId = await storage.MintIdAsync<DocumentId>();
        var currentCopyId = await storage.MintIdAsync<DocumentId>();
        var retiredCopyId = await storage.MintIdAsync<DocumentId>();
        await storage.CommitAsync([
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(historicalCopyId, "copy.source", childId),
                new DocumentCopySource { Id = sourceId, At = new DocumentPoint.AtSeq(createdAt) }),
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(currentCopyId, "copy.source", secondChildId),
                new DocumentCopySource { Id = sourceId, At = new DocumentPoint.Current() }),
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(retiredCopyId, "copy.source", rootId),
                new DocumentCopySource { Id = sourceId, At = new DocumentPoint.Current() }),
            new StorageWrite.DocumentRetire(retiredCopyId),
        ]);
        var current = new DocumentPoint.Current();
        var historicalCopy = await storage.GetDocumentAsync(historicalCopyId, current);
        Assert.Equal(2, historicalCopy!.Version);
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["count"] = 1d,
            ["rows"] = new List<object?> { new Dictionary<string, object?> { ["value"] = "base" } },
        }), Json(historicalCopy.Value));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["count"] = 2d,
            ["rows"] = new List<object?>
            {
                new Dictionary<string, object?> { ["value"] = "base" },
                new Dictionary<string, object?> { ["value"] = "current" },
            },
        }), Json((await storage.GetDocumentAsync(currentCopyId, current))!.Value));
        Assert.Null(await storage.GetDocumentAsync(retiredCopyId, current));

        // 来源后续变更（含退役）不影响已拷贝的 base。
        await storage.CommitAsync([
            new StorageWrite.DocumentChange(sourceId, new DocumentContent.Base(2, new Dictionary<string, object?>
            {
                ["count"] = 99d,
                ["rows"] = new List<object?>(),
            })),
            new StorageWrite.DocumentRetire(sourceId),
        ]);
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["count"] = 2d,
            ["rows"] = new List<object?>
            {
                new Dictionary<string, object?> { ["value"] = "base" },
                new Dictionary<string, object?> { ["value"] = "current" },
            },
        }), Json((await storage.GetDocumentAsync(currentCopyId, current))!.Value));

        // 同批变更来源（copy + retire 来源）被拒绝。
        var conflictId = await storage.MintIdAsync<DocumentId>();
        var conflict = await Assert.ThrowsAsync<StorageRejected>(() => storage.CommitAsync([
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(conflictId, "copy.source", childId),
                new DocumentCopySource { Id = currentCopyId, At = current }),
            new StorageWrite.DocumentRetire(currentCopyId),
        ]));
        Assert.Contains("was rejected", conflict.Message);
        Assert.Null(await storage.GetDocumentAsync(conflictId, current));
        Assert.Equal(Json(new Dictionary<string, object?>
        {
            ["count"] = 2d,
            ["rows"] = new List<object?>
            {
                new Dictionary<string, object?> { ["value"] = "base" },
                new Dictionary<string, object?> { ["value"] = "current" },
            },
        }), Json((await storage.GetDocumentAsync(currentCopyId, current))!.Value));

        // 种类不匹配被拒绝。
        var mismatchId = await storage.MintIdAsync<DocumentId>();
        var mismatch = await Assert.ThrowsAsync<StorageRejected>(() => storage.CommitAsync([
            new StorageWrite.DocumentCopyWrite(
                ConversationDocCreate(mismatchId, "copy.mismatch", childId),
                new DocumentCopySource { Id = currentCopyId, At = current }),
        ]));
        Assert.Contains("was rejected", mismatch.Message);
        Assert.Null(await storage.GetDocumentAsync(mismatchId, current));
    }

    // ─── conformance：close 语义 ──────────────────────────────────────────

    [Fact]
    public async Task CloseIsIdempotentAndRejectsEveryOperationAfterClose()
    {
        var storage = new MemoryStorage();
        await CreateRoot(storage);
        await storage.CloseAsync();
        // close 幂等。
        await storage.CloseAsync();

        var conversation = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.GetConversationAsync(DurableIdConstants.RootConversation));
        Assert.Contains("closed", conversation.Message);
        var commit = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.CommitAsync([]));
        Assert.Contains("closed", commit.Message);
        var mint = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.MintIdAsync<ConversationId>());
        Assert.Contains("closed", mint.Message);
    }

    // ─── 辅助 ─────────────────────────────────────────────────────────────

    private static async Task<ConversationId> CreateRoot(MemoryStorage storage)
    {
        await storage.CommitAsync([ConversationWrite(DurableIdConstants.RootConversation)]);
        return DurableIdConstants.RootConversation;
    }

    private static async Task<List<long>> SubmissionIds(MemoryStorage storage, SubmissionQuery query)
    {
        var found = new List<long>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await storage.ScanSubmissionsAsync(query, 1, cursor);
            found.AddRange(page.Items.Select(s => s.Id.Value));
            cursor = page.Next;
        } while (cursor is not null);
        return found;
    }

    private static StorageWrite.Conversation ConversationWrite(ConversationId id)
        => new(new ConversationRecord { Id = id });

    private static StorageWrite.Conversation ConversationRecordWrite(ConversationRecord record) => new(record);

    private static StorageWrite.Entry EntryWrite(
        ConversationId conversation, long id, string kind = "message", EntryId? head = null, object? data = null)
        => new(new EntryRecord { Id = EntryId.From(id), ConversationId = conversation, Kind = kind, Head = head, Data = data });

    private static StorageWrite.Task TaskWrite(long id, string kind, TaskStatus status, bool background)
        => new(new TaskRecord
        {
            Id = TaskId<object?>.From(id),
            ConversationId = Conversation,
            Kind = kind,
            Version = 1,
            Background = background,
            AbortRequested = false,
            State = new TaskState { Status = status },
        });

    private static TaskRecord PendingTask(TaskId<object?> id, ConversationId conversationId, string phase = "ready")
        => new()
        {
            Id = id,
            ConversationId = conversationId,
            Kind = "test.task",
            Version = 1,
            Input = new Dictionary<string, object?> { ["value"] = id.Value },
            State = new TaskState
            {
                Status = TaskStatus.Pending,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = phase },
            },
            Background = false,
            AbortRequested = false,
        };

    private static SubmissionRecord.InputRecord InputSubmission(
        SubmissionId id, ConversationId conversationId, string requestId, SubmissionStatus status, EntryId? entry = null)
        => new() { Id = id, ConversationId = conversationId, RequestId = requestId, Status = status, Entry = entry };

    private static SubmissionRecord.WriteRecord WriteSubmission(
        SubmissionId id, ConversationId conversationId, string requestId, SubmissionStatus status)
        => new() { Id = id, ConversationId = conversationId, RequestId = requestId, Status = status };

    private static DocumentCreate SessionCreate(DocumentId id, string kind = "doc.session", string? key = null)
        => new() { Id = id, Kind = kind, Scope = new DocumentScope.SessionScope(), Key = key };

    private static DocumentCreate ConversationDocCreate(
        DocumentId id, string kind, ConversationId conversationId, string? key = null)
        => new()
        {
            Id = id,
            Kind = kind,
            Scope = new DocumentScope.ConversationScope(conversationId),
            Key = key,
            History = ConversationHistory.Rewindable,
            Fork = ConversationFork.AsOf,
        };

    private static DocumentCreate TaskCreate(DocumentId id, string kind, TaskId<object?> taskId, string? key = null)
        => new() { Id = id, Kind = kind, Scope = new DocumentScope.TaskScope(taskId), Key = key };

    private static DeltaOp SetCount(double value)
        => new DeltaOp.Set(new Path([Seg.Key("count")]), value);
}
