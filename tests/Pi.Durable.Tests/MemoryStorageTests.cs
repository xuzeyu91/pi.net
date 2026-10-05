using Pi.Chord.Context;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 内存存储测试。覆盖 TS <c>storage/memory.test.ts</c> 的核心契约：
/// 提交原子性与序列分配、表按 ID 排序、文档 base/delta 重放与退役、
/// 发布监听、附加只读状态与监听器唯一性 / 绝不内联调用。
/// </summary>
public class MemoryStorageTests
{
    private static readonly ConversationId Conversation = ConversationId.From(1);

    private static Pi.Chord.Delta.DeltaOp SetCount(double value)
        => new Pi.Chord.Delta.DeltaOp.Set(
            new Pi.Chord.Delta.Path([Pi.Chord.Delta.Seg.Key("count")]), value);

    [Fact]
    public async Task CommitAssignsMonotonicSequences()
    {
        var storage = new MemoryStorage();
        var first = await storage.CommitAsync([ConversationWrite(Conversation)]);
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

        var conversations = await storage.ScanConversationsAsync();
        Assert.Equal([2L, 5L, 9L], conversations.Select(c => c.Id.Value));
    }

    [Fact]
    public async Task EntryQueryFiltersByConversationAndBounds()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            EntryWrite(Conversation, 1),
            EntryWrite(Conversation, 2),
            EntryWrite(Conversation, 3),
            EntryWrite(ConversationId.From(2), 4),
        ]);

        var all = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = Conversation });
        Assert.Equal([1L, 2L, 3L], all.Select(e => e.Id.Value));

        var bounded = await storage.ScanEntriesAsync(new EntryQuery
        {
            ConversationId = Conversation,
            MinEntryId = EntryId.From(2),
            MaxEntryId = EntryId.From(3),
        });
        Assert.Equal([2L, 3L], bounded.Select(e => e.Id.Value));
    }

    [Fact]
    public async Task TaskQueryFiltersByKindStatusAndBackground()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            TaskWrite(1, "kind.a", Pi.Durable.Types.TaskStatus.Pending, background: false),
            TaskWrite(2, "kind.b", Pi.Durable.Types.TaskStatus.Running, background: true),
            TaskWrite(3, "kind.a", Pi.Durable.Types.TaskStatus.Terminal, background: false),
        ]);

        var byKind = await storage.ScanTasksAsync(new TaskQuery { Kind = "kind.a" });
        Assert.Equal([1L, 3L], byKind.Select(t => t.Id.Value));

        var byStatus = await storage.ScanTasksAsync(new TaskQuery { Status = Pi.Durable.Types.TaskStatus.Running });
        Assert.Equal([2L], byStatus.Select(t => t.Id.Value));

        var byBackground = await storage.ScanTasksAsync(new TaskQuery { Background = true });
        Assert.Equal([2L], byBackground.Select(t => t.Id.Value));
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

        var queued = await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Queued });
        var done = await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Done });

        Assert.Equal([1L], queued.Select(s => s.Id.Value));
        Assert.Equal([2L], done.Select(s => s.Id.Value));
    }

    [Fact]
    public async Task DocumentCreateCopiesAndRetires()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(DocumentId.From(1)),
                new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1d })),
        ]);

        await storage.CommitAsync([
            new StorageWrite.DocumentCopyWrite(
                SessionCreate(DocumentId.From(2)),
                new DocumentCopySource { Id = DocumentId.From(1), At = new DocumentPoint.Current() }),
        ]);

        var documents = await storage.ScanDocumentsAsync(new DocumentQuery
        {
            Scope = new DocumentScope.SessionScope(),
            At = new DocumentPoint.Current(),
        });
        Assert.Equal(2, documents.Items.Count);
        Assert.All(documents.Items, d => Assert.Equal(1d, d.Value["count"]));

        await storage.CommitAsync([new StorageWrite.DocumentRetire(DocumentId.From(2))]);
        var afterRetire = await storage.ScanDocumentsAsync(new DocumentQuery
        {
            Scope = new DocumentScope.SessionScope(),
            At = new DocumentPoint.Current(),
        });
        Assert.Single(afterRetire.Items);
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

    [Fact]
    public async Task CloseReleasesDocuments()
    {
        var storage = new MemoryStorage();
        await storage.CommitAsync([
            new StorageWrite.DocumentCreateWrite(
                SessionCreate(DocumentId.From(1)),
                new DocumentContent.Base(1, new Dictionary<string, object?>())),
        ]);
        await storage.CloseAsync();

        var documents = await storage.ScanDocumentsAsync(new DocumentQuery
        {
            Scope = new DocumentScope.SessionScope(),
            At = new DocumentPoint.Current(),
        });
        Assert.Empty(documents.Items);
    }

    private static StorageWrite.Conversation ConversationWrite(ConversationId id)
        => new(new ConversationRecord { Id = id });

    private static StorageWrite.Entry EntryWrite(ConversationId conversation, long id)
        => new(new EntryRecord { Id = EntryId.From(id), ConversationId = conversation, Kind = "test" });

    private static StorageWrite.Task TaskWrite(long id, string kind, Types.TaskStatus status, bool background)
        => new(new Types.TaskRecord
        {
            Id = TaskId<object?>.From(id),
            ConversationId = Conversation,
            Kind = kind,
            Version = 1,
            Background = background,
            AbortRequested = false,
            State = new Types.TaskState { Status = status },
        });

    private static DocumentCreate SessionCreate(DocumentId id)
        => new()
        {
            Id = id,
            Kind = "doc.session",
            Scope = new DocumentScope.SessionScope(),
        };
}
