using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// P57：提交受理（harness-submissions.test.ts）。
/// <para>覆盖 <see cref="Submissions"/> 的受理生命周期：空闲写入即结算、忙碌输入的放置与
/// <c>whenBusy: reject</c> 拒绝、请求 ID 去重、abort 结果与按对话查找、仅取消等待、
/// close 拒绝待定等待、跨 reopen 的耐久再获取与结算、提交或等待即启用调度、
/// 在事务中以当前记录结算、以及类型化条目的追加读取。</para>
/// </summary>
public class HarnessSubmissionsTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── 1. 空闲写入 ────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendsAnIdleWriteAndSettlesItDoneWithoutATurn()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused")));
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "note", Data = new Dictionary<string, object?> { ["text"] = "x" } } },
                Ctx);
            var settled = await submission.WaitAsync(Ctx);

            var record = Assert.IsType<SubmissionRecord.WriteRecord>(settled.Record);
            Assert.Equal(submission.Id, record.Id);
            Assert.Equal(root.Id, record.ConversationId);
            Assert.Equal("write", record.Type);
            Assert.Equal(SubmissionStatus.Done, record.Status);
            Assert.NotNull(record.Entry);

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var only = Assert.Single(entries);
            Assert.Equal(record.Entry!.Value, only.Id);
            Assert.Equal(root.Id, only.ConversationId);
            Assert.Equal("note", only.Kind);

            // 空闲写入不启动运行。
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.NotNull(live);
            Assert.False(live!.ContainsKey("run"));
            var tasks = await harness.CommitWithAsync(
                tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 10), Ctx);
            Assert.Empty(tasks.Items);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 2. 忙碌输入的放置与拒绝 ────────────────────────────────────────────

    [Fact]
    public async Task PlacesIdleInputAndRejectsBusyInputWithWhenBusyRejectWithoutWriting()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        setup.Now = () => 42;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await provider.Reached;

            var record = Assert.IsType<SubmissionRecord.InputRecord>(await submission.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Placed, record.Status);
            Assert.NotNull(record.Entry);
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(record.Entry!.Value), Ctx);
            Assert.NotNull(entry);
            var user = Assert.IsType<UserMessage>(Assert.Single(entry!.Model!));
            Assert.Equal(42, user.Timestamp);
            Assert.Equal("hi", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);

            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.NotNull(live);
            var run = Assert.IsAssignableFrom<JsonDict>(live!["run"]);
            Assert.Equal(new object?[] { submission.Id.Value }, (IReadOnlyList<object?>)run["inputs"]!);
            var taskId = TaskId<object?>.From(Convert.ToInt64(run["taskId"]));
            Assert.Equal("pi.generation", (await harness.GetTaskAsync(taskId, Ctx))!.Kind);

            // whenBusy: reject 不写任何提交。
            var busy = await Assert.ThrowsAsync<ConversationBusy>(() => root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("again")], WhenBusy = "reject" }, Ctx));
            Assert.Equal(root.Id, busy.ConversationId);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 3. 请求 ID 去重 ────────────────────────────────────────────────────

    [Fact]
    public async Task DeduplicatesRequestIdsPerConversationBeforeAnyWrite()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            storage, HarnessTestSupport.NewChatSetup(provider));
        try
        {
            var first = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")], RequestId = "r1" }, Ctx);
            await provider.Reached;

            // 去重在忙碌检查之前进行：同型同 ID 复用既有提交。
            var again = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("different")], RequestId = "r1" }, Ctx);
            Assert.Equal(first.Id, again.Id);

            // 同 ID 不同类型拒绝。
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => root.SubmitAsync(
                new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "note" }, RequestId = "r1" }, Ctx));
            Assert.Contains("Request r1 already identifies a submission of type input", error.Message);

            var status = Assert.IsType<SubmissionRecord.InputRecord>(await first.StatusAsync(Ctx));
            Assert.Equal("r1", status.RequestId);
            Assert.Equal(SubmissionStatus.Placed, status.Status);

            // 去重是对话作用域的。
            var other = await harness.CreateConversationAsync(
                new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
            var write = await other.SubmitAsync(
                new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "note" }, RequestId = "r1" }, Ctx);
            Assert.NotEqual(first.Id, write.Id);
            var writeAgain = await other.SubmitAsync(
                new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "note" }, RequestId = "r1" }, Ctx);
            Assert.Equal(write.Id, writeAgain.Id);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 4. abort 结果与按对话查找 ──────────────────────────────────────────

    [Fact]
    public async Task ReportsAbortResultsAndLooksSubmissionsUpByConversation()
    {
        var release = HarnessTestSupport.NewDeferred<Unit>();
        var provider = new FakeProvider("faux", _ =>
        {
            release.Task.GetAwaiter().GetResult();
            return HarnessTestSupport.AssistantReply("answer");
        });
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(provider));
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            Assert.Equal("already_placed", await submission.AbortAsync(Ctx));
            Assert.Equal("already_placed", await harness.AbortSubmissionAsync(submission.Id, Ctx, root.Id));

            var other = await harness.CreateConversationAsync(
                new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
            Assert.Equal("not_found", await harness.AbortSubmissionAsync(submission.Id, Ctx, other.Id));
            Assert.Equal("not_found", await harness.AbortSubmissionAsync(SubmissionId.From(999_999), Ctx));
            Assert.Null(await harness.SubmissionAsync(SubmissionId.From(999_999), Ctx));

            release.Resolve(default);
            await submission.WaitAsync(Ctx);
            Assert.Equal("settled", await submission.AbortAsync(Ctx));
            Assert.Equal("settled", await harness.AbortSubmissionAsync(submission.Id, Ctx));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 5. 仅取消等待；close 拒绝待定等待 ──────────────────────────────────

    [Fact]
    public async Task CancelsOnlyAWaitAndRejectsPendingWaitsOnClose()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(provider));
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);

        using var source = new CancellationTokenSource();
        var cancelledContext = Pi.Chord.Context.ContextSignals.WithAbortSignal(source.Token, Ctx);
        var cancelled = submission.WaitAsync(cancelledContext);
        var pending = submission.WaitAsync(Ctx);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var status = Assert.IsType<SubmissionRecord.InputRecord>(await submission.StatusAsync(Ctx));
        Assert.Equal(SubmissionStatus.Placed, status.Status);

        await harness.CloseAsync(Ctx);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Contains("Harness is closed", error.Message);
    }

    // ─── 6. 跨 close 起点的等待被拒绝 ───────────────────────────────────────

    [Fact]
    public async Task RejectsAWaitWhoseSubmissionReadSpansTheStartOfClose()
    {
        var entered = HarnessTestSupport.NewDeferred<Unit>();
        var release = HarnessTestSupport.NewDeferred<Unit>();
        var storage = new HeldReadsStorage(new MemoryStorage());
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            storage, HarnessTestSupport.NewChatSetup(provider));
        var submission = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);

        // 装载门闩：下一次提交读取进入时阻塞，跨越 close 的起点。
        storage.ArmGate(entered, release);
        var waiting = submission.WaitAsync(Ctx);
        await entered.Task;

        var closing = harness.CloseAsync(Ctx);
        release.Resolve(default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Contains("Harness is closed", error.Message);
        await closing;
    }

    // ─── 7. 跨 reopen 的耐久再获取与结算 ────────────────────────────────────

    [Fact]
    public async Task ReacquiresASubmissionAfterReopenAndSettlesItDurably()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("after reopen"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = new MemoryStorage();
        var opened = await HarnessTestSupport.OpenChatAsync(first, setup);
        var id = (await opened.Root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")], RequestId = "print" }, Ctx)).Id;
        await provider.Reached;
        await opened.Harness.CloseAsync(Ctx);

        // 重开同一后备态：进行中的提交被再获取并最终结算。
        var second = first.Reopen();
        var secondSetup = HarnessTestSupport.NewChatSetup(
            HarnessTestSupport.AssistantReply("after reopen"));
        var reopened = await HarnessTestSupport.OpenChatAsync(second, secondSetup);
        var submission = await reopened.Harness.SubmissionAsync(id, Ctx);
        Assert.NotNull(submission);
        var record = Assert.IsType<SubmissionRecord.InputRecord>(await submission!.StatusAsync(Ctx));
        Assert.Equal(SubmissionStatus.Placed, record.Status);
        reopened.Harness.Resume();
        var settled = await submission.WaitAsync(Ctx);
        var settledRecord = Assert.IsType<SubmissionRecord.InputRecord>(settled.Record);
        Assert.Equal(SubmissionStatus.Done, settledRecord.Status);
        await reopened.Harness.CloseAsync(Ctx);

        // 再度 reopen：已结算提交保持稳定，重复请求 ID 仍去重。
        var third = second.Reopen();
        var thirdOpened = await HarnessTestSupport.OpenChatAsync(
            third, HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("after reopen")));
        var again = await thirdOpened.Harness.SubmissionAsync(id, Ctx);
        Assert.NotNull(again);
        Assert.Equal(settledRecord.Status, Assert.IsType<SubmissionRecord.InputRecord>(
            (await again!.WaitAsync(Ctx)).Record).Status);
        var dup = await thirdOpened.Root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")], RequestId = "print" }, Ctx);
        Assert.Equal(id, dup.Id);
        await thirdOpened.Harness.CloseAsync(Ctx);
    }

    // ─── 8. 提交或等待即启用调度 ────────────────────────────────────────────

    [Fact]
    public async Task EnablesSchedulingWhenACallerSubmitsOrWaits()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("answer")));
        try
        {
            // 未调用 resume()：submit 自身索取进度。
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            var settled = await submission.WaitAsync(Ctx);
            Assert.Equal(SubmissionStatus.Done, Assert.IsType<SubmissionRecord.InputRecord>(settled.Record).Status);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }

        var passive = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("x")));
        try
        {
            var taskId = await passive.Root.CommitAsync(
                tx => tx.CreateTaskAsync(
                    GenerationTaskTask(),
                    null,
                    new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = passive.Root.Id }),
                Ctx);
            var objId = TaskId<object?>.From(taskId.Value);
            // 仅提交任务不启动调度；等待它才启动。
            Assert.Equal(TaskStatus.Pending, (await passive.Harness.GetTaskAsync(objId, Ctx))!.State.Status);
            var settled = await passive.Harness.WaitForTaskAsync(objId, Ctx);
            Assert.Equal(TaskStatus.Terminal, settled.Record.State.Status);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(passive.Harness);
        }
    }

    // ─── 9. 在事务中以当前记录结算 ──────────────────────────────────────────

    [Fact]
    public async Task SettlesSubmissionsByTheirCurrentRecordInTheTransaction()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused")));
        try
        {
            var session = harness;
            var entry = await root.CommitAsync(
                async tx => (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" })).Id, Ctx);
            async Task<SubmissionId> CreateAsync(SubmissionCreate create)
                => await session.CommitWithAsync(async tx => (await tx.CreateSubmissionAsync(create)).Id, Ctx);

            var queued = await CreateAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = root.Id,
                Status = SubmissionStatus.Queued,
            });
            var write = await CreateAsync(new SubmissionCreate.WriteCreate
            {
                ConversationId = root.Id,
                Status = SubmissionStatus.Queued,
            });

            var answer = new SubmissionSettlement.Done(entry);
            var queuedError = await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync(
                tx =>
                {
                    tx.SettleSubmission(queued, answer);
                    return Task.FromResult(true);
                }, Ctx));
            Assert.Contains("is not a placed input", queuedError.Message);

            var writeError = await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync(
                tx =>
                {
                    tx.SettleSubmission(write, answer);
                    return Task.FromResult(true);
                }, Ctx));
            Assert.Contains("is not a placed input", writeError.Message);

            var missingError = await Assert.ThrowsAsync<InvalidOperationException>(() => root.CommitAsync(
                tx =>
                {
                    tx.SettleSubmission(SubmissionId.From(999_999), new SubmissionSettlement.Unanswered("x"));
                    return Task.FromResult(true);
                }, Ctx));
            Assert.Contains("does not exist", missingError.Message);

            // 同一次提交内先创建后结算；第二次结算保留第一次的结局。
            var placed = await session.CommitWithAsync(async tx =>
            {
                var created = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
                {
                    ConversationId = root.Id,
                    Status = SubmissionStatus.Placed,
                    Entry = entry,
                });
                tx.SettleSubmission(created.Id, answer);
                tx.SettleSubmission(created.Id, new SubmissionSettlement.Unanswered("late"));
                return created.Id;
            }, Ctx);
            var settled = Assert.IsType<SubmissionRecord.InputRecord>(
                (await (await harness.SubmissionAsync(placed, Ctx))!.StatusAsync(Ctx)));
            Assert.Equal(SubmissionStatus.Done, settled.Status);
            Assert.Equal(entry, settled.Entry);
            Assert.Equal(entry, settled.Answer);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 10. 类型化条目 ─────────────────────────────────────────────────────

    [Fact]
    public async Task AppendsAndReadsTypedEntriesThroughTokens()
    {
        var counter = DurableEntries.DefineEntry<Dictionary<string, object?>>("app.counter");
        var marker = DurableEntries.DefineEntry<object?>("app.marker");
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused")));
        try
        {
            var appended = await root.CommitAsync(
                tx => tx.AppendEntryAsync(counter, root.Id, new TypedEntryDraft<Dictionary<string, object?>>
                {
                    Data = new Dictionary<string, object?> { ["n"] = 1 },
                }), Ctx);
            Assert.Equal(1, Convert.ToInt32(Assert.IsAssignableFrom<JsonDict>(appended.Data)["n"]));
            Assert.Equal(root.Id, appended.ConversationId);
            Assert.Equal("app.counter", appended.Kind);

            var markerEntry = await root.CommitAsync(
                tx => tx.AppendEntryAsync(marker, root.Id, new TypedEntryDraft<object?>()), Ctx);
            Assert.Equal("app.marker", markerEntry.Kind);

            var readBack = await root.CommitAsync(tx => tx.GetEntryAsync(counter, appended.Id), Ctx);
            Assert.Equal(appended.Id, readBack!.Id);
            Assert.Null(await root.CommitAsync(tx => tx.GetEntryAsync(marker, appended.Id), Ctx));
            Assert.Null(await root.CommitAsync(tx => tx.GetEntryAsync(counter, EntryId.From(999_999)), Ctx));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static DurableTask<JsonDict, JsonDict, JsonDict> GenerationTaskTask()
        => HarnessTestSupport.DefineTask("pi.generation", 1, [("run", HarnessTestSupport.CompleteWith(null))]);

    /// <summary>在第一次 <c>GetSubmissionAsync</c> 时进入并阻塞，直到释放（对应 TS <c>HeldReads</c>）。</summary>
    private sealed class HeldReadsStorage(IStorage inner) : IStorage
    {
        private HarnessTestSupport.Deferred<Unit>? _entered;
        private HarnessTestSupport.Deferred<Unit>? _release;

        public void ArmGate(HarnessTestSupport.Deferred<Unit> entered, HarnessTestSupport.Deferred<Unit> release)
        {
            _entered = entered;
            _release = release;
        }

        public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
        {
            if (_entered is { } entered && _release is { } release)
            {
                _entered = null;
                _release = null;
                entered.Resolve(default);
                await release.Task;
            }

            return await inner.GetSubmissionAsync(id);
        }

        public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
            => inner.CommitAsync(writes, signal);

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
}
