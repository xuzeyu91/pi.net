using Pi.Chord.Delta;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using DurableIds = Pi.Durable.DurableIds;
using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Testing;

/// <summary>
/// 与运行器无关的 <see cref="IStorage"/> 契约用例。对应 TS <c>testing/storage-conformance.ts</c>
/// 的 <c>createStorageConformance</c>：<c>withStorage</c> 必须为每个用例恰好调用并等待一次回调，
/// 每次提供一个全新存储。
/// <para>移植说明：TS 的 <c>expect(...)</c> 门面（toBe/toEqual/toMatchObject/…）在 C# 中无法用
/// 动态属性实现，故改为等价的方法化断言（<c>Be</c>/<c>Equal</c>/<c>MatchObject</c>/…），
/// 断言强度与判据逐条对齐 TS。</para>
/// <para>未移植：TS "keeps indexed string identities lossless" 用例（<c>\ud800</c> 孤立代理项作为
/// JS 字符串键的驻留陷阱）；C# <c>string</c> 用 UTF-16 码元数组语义，不存在 TS 的键驻留问题，
/// 无语义等价物。</para>
/// </summary>
public static class StorageConformance
{
    /// <summary>创建与运行器无关的存储 conformance 用例。</summary>
    public static IReadOnlyList<StorageConformanceCase> CreateStorageConformance(StorageConformanceOptions options)
    {
        var assert = options.Assertions;
        var cases = new List<StorageConformanceCase>();

        void AddCase(string name, Func<IStorage, Task> test) =>
            cases.Add(new StorageConformanceCase
            {
                Name = name,
                Run = () => options.WithStorage(test),
            });

        AddCase("reserves ID 1 for the immutable root conversation", async storage =>
        {
            Assert.Equal(assert, (await storage.MintIdAsync<ConversationId>()).Value, 2L);
            Assert.Equal(assert, await CreateRootAsync(storage), RootConversationId);
            Assert.DeepEqual(assert, await storage.GetConversationAsync(RootConversationId), new ConversationRecord { Id = RootConversationId });
            await assert.Rejects(() => storage.CommitAsync([new StorageWrite.Conversation(new ConversationRecord { Id = RootConversationId })]),
                $"ID {RootConversationId.Value} already belongs to conversation");
        });

        AddCase("commits mixed table writes atomically and rolls all of them back on failure", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var entryId = await storage.MintIdAsync<EntryId>();
            var taskId = await storage.MintIdAsync<TaskId<object?>>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            var task = PendingTask(taskId, rootId);
            var input = new SubmissionRecord.InputRecord
            {
                Id = submissionId,
                ConversationId = rootId,
                RequestId = "request-1",
                Status = SubmissionStatus.Placed,
                Entry = entryId,
            };
            var initialSeq = await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(entryId, rootId, "user", Data: TextData("hello"))),
                    new StorageWrite.Task(task),
                    new StorageWrite.Submission(input),
                ]);

            Assert.DeepEqual(assert, await storage.GetEntryAsync(entryId), new StoredEntry
            {
                Entry = Entry(entryId, rootId, "user", Data: TextData("hello")),
                CommitSeq = initialSeq,
            });
            Assert.DeepEqual(assert, await storage.GetTaskAsync(taskId), task);
            Assert.DeepEqual(assert, await storage.GetSubmissionAsync(submissionId), input);

            var transientEntryId = await storage.MintIdAsync<EntryId>();
            var runningTask = task with
            {
                State = new TaskState { Status = TaskStatus.Running, Checkpoint = PhaseCheckpoint("effect") },
            };
            var doneInput = input with { Status = SubmissionStatus.Done, Answer = transientEntryId };
            await assert.Rejects(() => storage.CommitAsync(
                    [
                        new StorageWrite.Task(runningTask),
                        new StorageWrite.Submission(doneInput),
                        new StorageWrite.Entry(Entry(transientEntryId, rootId, "assistant")),
                        new StorageWrite.Conversation(new ConversationRecord { Id = rootId }),
                    ]),
                $"ID {rootId.Value} already belongs to conversation");

            Assert.DeepEqual(assert, await storage.GetTaskAsync(taskId), task);
            Assert.DeepEqual(assert, await storage.GetSubmissionAsync(submissionId), input);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(transientEntryId), null);
            var afterRollbackSeq = await storage.CommitAsync(
                [new StorageWrite.Entry(Entry(await storage.MintIdAsync<EntryId>(), rootId, "after-rollback"))]);
            Assert.GreaterThan(assert, afterRollbackSeq.Value, initialSeq.Value);
        });

        AddCase("detaches retained writes and every returned record", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var entryId = await storage.MintIdAsync<EntryId>();
            var taskId = await storage.MintIdAsync<TaskId<object?>>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            var entryData = NestedData(1, 2);
            var checkpoint = PhaseWithCount("ready", 1);
            var detail = CodesData("initial");
            var storedEntry = Entry(entryId, rootId, "note", Data: entryData);
            var storedTask = PendingTask(taskId, rootId) with
            {
                State = new TaskState { Status = TaskStatus.Pending, Checkpoint = checkpoint },
            };
            var storedInput = new SubmissionRecord.InputRecord
            {
                Id = submissionId,
                ConversationId = rootId,
                Status = SubmissionStatus.Unanswered,
                Reason = "failed",
                Detail = detail,
            };
            await storage.CommitAsync(
                [
                    new StorageWrite.Entry(storedEntry),
                    new StorageWrite.Task(storedTask),
                    new StorageWrite.Submission(storedInput),
                ]);

            // 变更调用方持有的对象，已存储的值必须不受影响。
            NestedValues(entryData)[1] = 3;
            PhaseNested(checkpoint)["count"] = 2;
            CodesValues(detail).Add("mutated");
            Assert.DeepEqual(assert, EntryData(await storage.GetEntryAsync(entryId)), NestedData(1, 2));
            Assert.DeepEqual(assert, (await storage.GetTaskAsync(taskId))!.State, new TaskState
            {
                Status = TaskStatus.Pending,
                Checkpoint = PhaseWithCount("ready", 1),
            });
            Assert.DeepEqual(assert, ((SubmissionRecord.InputRecord)(await storage.GetSubmissionAsync(submissionId))!).Detail, CodesData("initial"));

            // 变更返回的记录，再次读取必须仍为原始值。
            var readEntry = (await storage.GetEntryAsync(entryId))!.Entry;
            NestedValues(readEntry.Data!).Add(9);
            var readTask = (await storage.GetTaskAsync(taskId))!;
            if (readTask.State.Status != TaskStatus.Terminal)
            {
                PhaseNested(readTask.State.Checkpoint!)["count"] = 9;
            }

            var readInput = (await storage.GetSubmissionAsync(submissionId))!;
            CodesValues(((SubmissionRecord.InputRecord)readInput).Detail!).Add("read mutation");

            Assert.DeepEqual(assert, EntryData(await storage.GetEntryAsync(entryId)), NestedData(1, 2));
            Assert.DeepEqual(assert, (await storage.GetTaskAsync(taskId))!.State, new TaskState
            {
                Status = TaskStatus.Pending,
                Checkpoint = PhaseWithCount("ready", 1),
            });
            Assert.DeepEqual(assert, ((SubmissionRecord.InputRecord)(await storage.GetSubmissionAsync(submissionId))!).Detail, CodesData("initial"));
        });

        AddCase("detaches prototype-like JSON keys without changing object prototypes", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var entryId = await storage.MintIdAsync<EntryId>();
            var data = new Dictionary<string, object?>
            {
                ["__proto__"] = new Dictionary<string, object?> { ["polluted"] = false },
                ["constructor"] = new Dictionary<string, object?> { ["label"] = "stored" },
                ["toString"] = "value",
            };
            await storage.CommitAsync([new StorageWrite.Entry(Entry(entryId, rootId, "note", Data: data))]);

            // 调用方持有的对象被改写。
            ((Dictionary<string, object?>)data["__proto__"]!)["polluted"] = true;
            ((Dictionary<string, object?>)data["constructor"]!)["label"] = "mutated";

            var firstRead = (Dictionary<string, object?>)(await storage.GetEntryAsync(entryId))!.Entry.Data!;
            Assert.DeepEqual(assert, firstRead["__proto__"], new Dictionary<string, object?> { ["polluted"] = false });
            Assert.DeepEqual(assert, firstRead["constructor"], new Dictionary<string, object?> { ["label"] = "stored" });
            Assert.StrictEqual(assert, firstRead["toString"], "value");

            // 变更读到的副本，再次读取必须仍为原始值。
            ((Dictionary<string, object?>)firstRead["__proto__"]!)["polluted"] = true;
            var secondRead = (Dictionary<string, object?>)(await storage.GetEntryAsync(entryId))!.Entry.Data!;
            Assert.DeepEqual(assert, secondRead["__proto__"], new Dictionary<string, object?> { ["polluted"] = false });
            Assert.DeepEqual(assert, secondRead["constructor"], new Dictionary<string, object?> { ["label"] = "stored" });
            Assert.StrictEqual(assert, secondRead["toString"], "value");
        });

        AddCase("indexes entries committed out of ID order", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(DurableIds.EntryId(30), rootId)),
                    new StorageWrite.Entry(Entry(DurableIds.EntryId(10), rootId)),
                    new StorageWrite.Entry(Entry(DurableIds.EntryId(20), rootId, "marker", Head: DurableIds.EntryId(10))),
                ]);

            Assert.DeepEqual(
                assert,
                (await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 10)).Items.Select(e => e.Id.Value).ToList(),
                new List<object?> { 30L, 20L, 10L });
            Assert.Equal(assert, (await storage.FindLatestHeadMarkerAsync(rootId, null))!.Id.Value, 20L);
        });

        AddCase("continues an entry cursor below its last item after a newer commit", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var oldestId = await storage.MintIdAsync<EntryId>();
            var middleId = await storage.MintIdAsync<EntryId>();
            var newestId = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(oldestId, rootId)),
                    new StorageWrite.Entry(Entry(middleId, rootId)),
                    new StorageWrite.Entry(Entry(newestId, rootId)),
                ]);

            var first = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 2);
            Assert.DeepEqual(assert, first.Items.Select(e => e.Id.Value).ToList(), new List<object?> { newestId.Value, middleId.Value });

            var appendedId = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync([new StorageWrite.Entry(Entry(appendedId, rootId))]);
            var second = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = rootId }, 2, first.Next);
            Assert.DeepEqual(assert, second.Items.Select(e => e.Id.Value).ToList(), new List<object?> { oldestId.Value });
            Assert.StrictEqual(assert, second.Next, null);
        });

        AddCase("paginates conversations by opaque cursor in ascending ID order", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var secondId = await storage.MintIdAsync<ConversationId>();
            var thirdId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord { Id = thirdId }),
                    new StorageWrite.Conversation(new ConversationRecord { Id = secondId }),
                ]);

            var first = await storage.ScanConversationsAsync(null, 2);
            Assert.DeepEqual(assert, first.Items.Select(c => c.Id.Value).ToList(), new List<object?> { rootId.Value, secondId.Value });
            Assert.Ok(assert, first.Next is not null, "first page has a cursor");
            var roundTripped = RoundTripCursor(first.Next!);
            var second = await storage.ScanConversationsAsync(null, 2, roundTripped);
            Assert.DeepEqual(assert, second.Items.Select(c => c.Id.Value).ToList(), new List<object?> { thirdId.Value });
            Assert.StrictEqual(assert, second.Next, null);
        });

        AddCase("filters and pages conversations by durable owner edges", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var otherOwnerId = await storage.MintIdAsync<ConversationId>();
            var firstTaskId = await storage.MintIdAsync<TaskId<object?>>();
            var secondTaskId = await storage.MintIdAsync<TaskId<object?>>();
            var firstId = await storage.MintIdAsync<ConversationId>();
            var secondId = await storage.MintIdAsync<ConversationId>();
            var thirdId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord { Id = otherOwnerId }),
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = firstId,
                        Owner = new ConversationOwner { ConversationId = rootId, TaskId = firstTaskId },
                    }),
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = secondId,
                        Owner = new ConversationOwner { ConversationId = rootId, TaskId = secondTaskId },
                    }),
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = thirdId,
                        Owner = new ConversationOwner { ConversationId = otherOwnerId, TaskId = firstTaskId },
                    }),
                ]);

            var first = await storage.ScanConversationsAsync(new ConversationQuery { OwnerConversationId = rootId }, 1);
            Assert.DeepEqual(assert, first.Items.Select(c => c.Id.Value).ToList(), new List<object?> { firstId.Value });
            Assert.Ok(assert, first.Next is not null, "first owner page has a cursor");
            var second = await storage.ScanConversationsAsync(new ConversationQuery { OwnerConversationId = rootId }, 1, first.Next);
            Assert.DeepEqual(assert, second.Items.Select(c => c.Id.Value).ToList(), new List<object?> { secondId.Value });
            Assert.StrictEqual(assert, second.Next, null);
            Assert.DeepEqual(
                assert,
                (await storage.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = firstTaskId }, 10)).Items.Select(c => c.Id.Value).ToList(),
                new List<object?> { firstId.Value, thirdId.Value });
            Assert.DeepEqual(
                assert,
                (await storage.ScanConversationsAsync(
                    new ConversationQuery { OwnerConversationId = rootId, OwnerTaskId = firstTaskId }, 10)).Items.Select(c => c.Id.Value).ToList(),
                new List<object?> { firstId.Value });
        });

        AddCase("scans deep fork history newest-first through every ancestor cap", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var rootFirst = await storage.MintIdAsync<EntryId>();
            var rootForkPoint = await storage.MintIdAsync<EntryId>();
            var rootExcludedSameCommit = await storage.MintIdAsync<EntryId>();
            var rootEntriesSeq = await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(rootFirst, rootId)),
                    new StorageWrite.Entry(Entry(rootForkPoint, rootId, "marker", Head: rootFirst)),
                    new StorageWrite.Entry(Entry(rootExcludedSameCommit, rootId)),
                ]);
            var childId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = childId,
                        Parent = new ConversationParent { ConversationId = rootId, At = rootForkPoint },
                    }),
                ]);
            var childForkPoint = await storage.MintIdAsync<EntryId>();
            var childExcluded = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(childForkPoint, childId, "note")),
                    new StorageWrite.Entry(Entry(childExcluded, childId)),
                ]);
            var rootExcludedLater = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync([new StorageWrite.Entry(Entry(rootExcludedLater, rootId))]);
            var grandchildId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = grandchildId,
                        Parent = new ConversationParent { ConversationId = childId, At = childForkPoint },
                    }),
                ]);
            var grandchildHead = await storage.MintIdAsync<EntryId>();
            var grandchildTail = await storage.MintIdAsync<EntryId>();
            var grandchildEntriesSeq = await storage.CommitAsync(
                [
                    new StorageWrite.Entry(Entry(grandchildHead, grandchildId, "marker", Head: grandchildHead)),
                    new StorageWrite.Entry(Entry(grandchildTail, grandchildId)),
                ]);
            var childExcludedLater = await storage.MintIdAsync<EntryId>();
            await storage.CommitAsync([new StorageWrite.Entry(Entry(childExcludedLater, childId))]);

            var first = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2);
            Assert.DeepEqual(assert, first.Items.Select(e => e.Id.Value).ToList(), new List<object?> { grandchildTail.Value, grandchildHead.Value });
            var second = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2, first.Next);
            Assert.DeepEqual(assert, second.Items.Select(e => e.Id.Value).ToList(), new List<object?> { childForkPoint.Value, rootForkPoint.Value });
            var third = await storage.ScanEntriesAsync(new EntryQuery { ConversationId = grandchildId }, 2, second.Next);
            Assert.DeepEqual(assert, third.Items.Select(e => e.Id.Value).ToList(), new List<object?> { rootFirst.Value });
            Assert.StrictEqual(assert, third.Next, null);

            var currentMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, null);
            Assert.Equal(assert, currentMarker!.Id.Value, grandchildHead.Value);
            Assert.Equal(assert, currentMarker.Head!.Value.Value, grandchildHead.Value);
            var historicalMarker = await storage.FindLatestHeadMarkerAsync(grandchildId, childForkPoint);
            Assert.Equal(assert, historicalMarker!.Id.Value, rootForkPoint.Value);
            Assert.Equal(assert, historicalMarker.Head!.Value.Value, rootFirst.Value);
            Assert.StrictEqual(assert, await storage.FindLatestHeadMarkerAsync(grandchildId, rootFirst), null);

            var activeFirst = await storage.ScanEntriesAsync(
                new EntryQuery { ConversationId = grandchildId, MinEntryId = currentMarker.Head!.Value }, 1);
            Assert.DeepEqual(assert, activeFirst.Items.Select(e => e.Id.Value).ToList(), new List<object?> { grandchildTail.Value });
            Assert.Ok(assert, activeFirst.Next is not null, "active first page has a cursor");
            var activeSecond = await storage.ScanEntriesAsync(
                new EntryQuery { ConversationId = grandchildId, MinEntryId = currentMarker.Head!.Value }, 1, activeFirst.Next);
            Assert.DeepEqual(assert, activeSecond.Items.Select(e => e.Id.Value).ToList(), new List<object?> { grandchildHead.Value });
            Assert.StrictEqual(assert, activeSecond.Next, null);

            Assert.DeepEqual(
                assert,
                (await storage.ScanEntriesAsync(
                    new EntryQuery
                    {
                        ConversationId = grandchildId,
                        MinEntryId = historicalMarker.Head!.Value,
                        MaxEntryId = childForkPoint,
                    }, 10)).Items.Select(e => e.Id.Value).ToList(),
                new List<object?> { childForkPoint.Value, rootForkPoint.Value, rootFirst.Value });

            Assert.DeepEqual(assert, await storage.GetEntryAsync(rootFirst), new StoredEntry
            {
                Entry = Entry(rootFirst, rootId),
                CommitSeq = rootEntriesSeq,
            });
            Assert.Equal(assert, (await storage.GetEntryAsync(rootForkPoint))!.CommitSeq.Value, rootEntriesSeq.Value);
            Assert.Equal(assert, (await storage.GetEntryAsync(grandchildHead))!.CommitSeq.Value, grandchildEntriesSeq.Value);
            Assert.Equal(assert, (await storage.GetEntryAsync(grandchildTail))!.CommitSeq.Value, grandchildEntriesSeq.Value);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(DurableIds.EntryId(999_999)), null);

            Assert.DeepEqual(assert, await storage.GetEntryAsync(grandchildId, rootFirst), new StoredEntry
            {
                Entry = Entry(rootFirst, rootId),
                CommitSeq = rootEntriesSeq,
            });
            Assert.Equal(assert, (await storage.GetEntryAsync(grandchildId, childForkPoint))!.Entry.ConversationId.Value, childId.Value);
            Assert.Equal(assert, (await storage.GetEntryAsync(grandchildId, grandchildTail))!.CommitSeq.Value, grandchildEntriesSeq.Value);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(grandchildId, rootExcludedSameCommit), null);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(grandchildId, rootExcludedLater), null);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(grandchildId, childExcluded), null);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(grandchildId, childExcludedLater), null);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(rootId, grandchildHead), null);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(grandchildId, DurableIds.EntryId(999_999)), null);
            await assert.Rejects(() => storage.GetEntryAsync(DurableIds.ConversationId(999_999), rootFirst), "Unknown conversation");
            await assert.Rejects(() => storage.ScanEntriesAsync(new EntryQuery { ConversationId = DurableIds.ConversationId(999_999) }, 10),
                "Unknown conversation");
        });

        AddCase("replaces complete task records and pages filtered task scans", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var firstId = await storage.MintIdAsync<TaskId<object?>>();
            var secondId = await storage.MintIdAsync<TaskId<object?>>();
            var thirdId = await storage.MintIdAsync<TaskId<object?>>();
            var first = PendingTask(firstId, rootId) with
            {
                Memos = new Dictionary<string, object?> { ["winner"] = "first" },
            };
            var second = PendingTask(secondId, rootId) with { Background = true };
            var third = PendingTask(thirdId, rootId) with { AbortRequested = true };
            await storage.CommitAsync(
                [
                    new StorageWrite.Task(first),
                    new StorageWrite.Task(second),
                    new StorageWrite.Task(third),
                ]);

            var running = first with
            {
                State = new TaskState { Status = TaskStatus.Running, Checkpoint = PhaseAttempt("effect", 1) },
                AbortRequested = true,
            };
            await storage.CommitAsync([new StorageWrite.Task(running)]);
            Assert.DeepEqual(assert, await storage.GetTaskAsync(firstId), running);

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
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = new Dictionary<string, object?> { ["entryId"] = 99L } },
                },
                Background = false,
                AbortRequested = true,
            };
            await storage.CommitAsync([new StorageWrite.Task(terminal)]);
            Assert.DeepEqual(assert, await storage.GetTaskAsync(firstId), terminal);

            var pendingPage = await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 1);
            Assert.DeepEqual(assert, pendingPage.Items.Select(t => t.Id.Value).ToList(), new List<object?> { secondId.Value });
            Assert.Ok(assert, pendingPage.Next is not null, "pending page has a cursor");
            Assert.DeepEqual(
                assert,
                (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 1, pendingPage.Next)).Items.Select(t => t.Id.Value).ToList(),
                new List<object?> { thirdId.Value });
            Assert.DeepEqual(
                assert,
                (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Terminal, AbortRequested = true }, 10)).Items.ToList(),
                new List<object?> { terminal });
            Assert.DeepEqual(
                assert,
                (await storage.ScanTasksAsync(new TaskQuery { Background = true }, 10)).Items.Select(t => t.Id.Value).ToList(),
                new List<object?> { secondId.Value });
        });

        AddCase("stores owners and scans waiting and completing tasks by status", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
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
                    Checkpoint = PhaseCheckpoint("next"),
                    On = [ownerId],
                    Policy = JoinPolicy.AllSettled,
                },
                Memos = new Dictionary<string, object?> { ["kept"] = true },
            };
            var baseTask = PendingTask(completingId, rootId);
            var completing = baseTask with
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
            await storage.CommitAsync(
                [
                    new StorageWrite.Task(owner),
                    new StorageWrite.Task(waiting),
                    new StorageWrite.Task(completing),
                ]);
            Assert.DeepEqual(assert, await storage.GetTaskAsync(waitingId), waiting);
            Assert.DeepEqual(assert, await storage.GetTaskAsync(completingId), completing);

            async Task<List<TaskRecord>> Scan(TaskStatus status) =>
                (await storage.ScanTasksAsync(new TaskQuery { Status = status }, 10)).Items.ToList();

            Assert.DeepEqual(assert, await Scan(TaskStatus.Waiting), new List<object?> { waiting });
            Assert.DeepEqual(assert, await Scan(TaskStatus.Completing), new List<object?> { completing });
            Assert.DeepEqual(assert, (await Scan(TaskStatus.Pending)).Select(t => t.Id.Value).ToList(), new List<object?> { ownerId.Value });

            var terminal = completing with
            {
                State = new TaskState { Status = TaskStatus.Terminal, Outcome = completing.State.Outcome! },
            };
            await storage.CommitAsync([new StorageWrite.Task(terminal)]);
            Assert.DeepEqual(assert, await Scan(TaskStatus.Completing), new List<object?>());
            Assert.DeepEqual(assert, await Scan(TaskStatus.Terminal), new List<object?> { terminal });
        });

        AddCase("indexes request IDs per conversation and replaces complete submission records", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var secondConversationId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync([new StorageWrite.Conversation(new ConversationRecord { Id = secondConversationId })]);
            var firstId = await storage.MintIdAsync<SubmissionId>();
            var secondId = await storage.MintIdAsync<SubmissionId>();
            var otherConversationId = await storage.MintIdAsync<SubmissionId>();
            var first = new SubmissionRecord.InputRecord
            {
                Id = firstId,
                ConversationId = rootId,
                RequestId = "same",
                Status = SubmissionStatus.Queued,
            };
            var second = new SubmissionRecord.InputRecord
            {
                Id = secondId,
                ConversationId = rootId,
                RequestId = "other",
                Status = SubmissionStatus.Queued,
            };
            var otherConversation = new SubmissionRecord.InputRecord
            {
                Id = otherConversationId,
                ConversationId = secondConversationId,
                RequestId = "same",
                Status = SubmissionStatus.Queued,
            };
            await storage.CommitAsync(
                [
                    new StorageWrite.Submission(first),
                    new StorageWrite.Submission(second),
                    new StorageWrite.Submission(otherConversation),
                ]);
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(rootId, "same"), first);
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(secondConversationId, "same"), otherConversation);

            var placedSecond = second with
            {
                Status = SubmissionStatus.Placed,
                Entry = await storage.MintIdAsync<EntryId>(),
            };
            await storage.CommitAsync([new StorageWrite.Submission(placedSecond)]);
            Assert.DeepEqual(assert, await storage.GetSubmissionAsync(secondId), placedSecond);
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(rootId, "other"), placedSecond);

            async Task<List<long>> Ids(SubmissionQuery? query)
            {
                var found = new List<long>();
                IReadOnlyDictionary<string, object?>? cursor = null;
                do
                {
                    var page = await storage.ScanSubmissionsAsync(query, 1, cursor);
                    found.AddRange(page.Items.Select(s => s.Id.Value));
                    cursor = page.Next;
                }
                while (cursor is not null);

                return found;
            }

            Assert.DeepEqual(assert, await Ids(null), new List<object?> { firstId.Value, secondId.Value, otherConversationId.Value });
            Assert.DeepEqual(assert, await Ids(new SubmissionQuery { ConversationId = rootId }), new List<object?> { firstId.Value, secondId.Value });
            // 状态变化会把记录在状态扫描之间移动。
            Assert.DeepEqual(assert, await Ids(new SubmissionQuery { Status = SubmissionStatus.Queued }), new List<object?> { firstId.Value, otherConversationId.Value });
            Assert.DeepEqual(assert, await Ids(new SubmissionQuery { Status = SubmissionStatus.Placed }), new List<object?> { secondId.Value });
            Assert.DeepEqual(
                assert,
                await Ids(new SubmissionQuery { ConversationId = secondConversationId, Status = SubmissionStatus.Queued }),
                new List<object?> { otherConversationId.Value });
            Assert.DeepEqual(
                assert,
                await Ids(new SubmissionQuery { ConversationId = secondConversationId, Status = SubmissionStatus.Placed }),
                new List<object?>());
            Assert.DeepEqual(
                assert,
                (await storage.ScanSubmissionsAsync(new SubmissionQuery { Status = SubmissionStatus.Placed }, 10)).Items.ToList(),
                new List<object?> { placedSecond });
        });

        AddCase("stores passive write submissions without input-only lifecycle states", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var doneId = await storage.MintIdAsync<SubmissionId>();
            var failedId = await storage.MintIdAsync<SubmissionId>();
            var queuedDone = new SubmissionRecord.WriteRecord
            {
                Id = doneId,
                ConversationId = rootId,
                RequestId = "passive-done",
                Status = SubmissionStatus.Queued,
            };
            var queuedFailed = new SubmissionRecord.WriteRecord
            {
                Id = failedId,
                ConversationId = rootId,
                RequestId = "passive-failed",
                Status = SubmissionStatus.Queued,
            };
            await storage.CommitAsync(
                [
                    new StorageWrite.Submission(queuedDone),
                    new StorageWrite.Submission(queuedFailed),
                ]);

            var done = queuedDone with
            {
                Status = SubmissionStatus.Done,
                Entry = await storage.MintIdAsync<EntryId>(),
            };
            var unanswered = queuedFailed with
            {
                Status = SubmissionStatus.Unanswered,
                Reason = "closed",
                Detail = new Dictionary<string, object?> { ["retryable"] = false },
            };
            await storage.CommitAsync(
                [
                    new StorageWrite.Submission(done),
                    new StorageWrite.Submission(unanswered),
                ]);
            Assert.DeepEqual(assert, await storage.GetSubmissionAsync(doneId), done);
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(rootId, "passive-done"), done);
            Assert.DeepEqual(assert, await storage.GetSubmissionAsync(failedId), unanswered);
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(rootId, "passive-failed"), unanswered);
        });

        AddCase("reconstructs rewindable documents and preserves half-open incarnations", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var firstId = await storage.MintIdAsync<DocumentId>();
            var firstRecord = new DocumentCreate
            {
                Id = firstId,
                Kind = "conversation.notes",
                Scope = new DocumentScope.ConversationScope(rootId),
                History = ConversationHistory.Rewindable,
                Fork = ConversationFork.AsOf,
            };
            var initial = new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a" },
                ["nested"] = new Dictionary<string, object?> { ["count"] = 1L },
            };
            var createdAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(firstRecord, new DocumentContent.Base(1, initial)),
                ]);
            var appended = new List<object?> { "b" };
            var ops = new List<DeltaOp>
            {
                new DeltaOp.Splice(P("items"), 1, 0, appended),
                new DeltaOp.Set(P("nested", "count"), 2L),
            };
            var changedAt = await storage.CommitAsync(
                [new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(1, ops))]);

            ((List<object?>)initial["items"]!).Add("caller mutation");
            appended.Add("caller mutation");
            Assert.MatchObject(assert, await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(createdAt)), new Dictionary<string, object?>
            {
                ["Version"] = 1,
                ["Value"] = new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "a" },
                    ["nested"] = new Dictionary<string, object?> { ["count"] = 1L },
                },
                ["DeltasSinceBase"] = 0L,
            });
            var changed = (await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(changedAt)))!;
            Assert.DeepEqual(assert, changed.Value, new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a", "b" },
                ["nested"] = new Dictionary<string, object?> { ["count"] = 2L },
            });
            Assert.Equal(assert, changed.DeltasSinceBase, 1L);

            ((List<object?>)changed.Value["items"]!).Add("read mutation");
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "a", "b" },
                ["nested"] = new Dictionary<string, object?> { ["count"] = 2L },
            });

            var checkpointAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(firstId, new DocumentContent.Base(2, new Dictionary<string, object?>
                    {
                        ["items"] = new List<object?> { "checkpoint" },
                        ["nested"] = new Dictionary<string, object?> { ["count"] = 3L },
                    })),
                ]);
            var replacedAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(2,
                    [
                        new DeltaOp.Replace(new Dictionary<string, object?>
                        {
                            ["items"] = new List<object?> { "replacement" },
                            ["nested"] = new Dictionary<string, object?> { ["count"] = 4L },
                        }),
                    ])),
                ]);
            Assert.MatchObject(assert, await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(changedAt)), new Dictionary<string, object?>
            {
                ["Version"] = 1,
                ["Value"] = new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "a", "b" },
                    ["nested"] = new Dictionary<string, object?> { ["count"] = 2L },
                },
            });
            Assert.MatchObject(assert, await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(checkpointAt)), new Dictionary<string, object?>
            {
                ["Version"] = 2,
                ["Value"] = new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "checkpoint" },
                    ["nested"] = new Dictionary<string, object?> { ["count"] = 3L },
                },
                ["DeltasSinceBase"] = 0L,
            });
            Assert.MatchObject(assert, await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(replacedAt)), new Dictionary<string, object?>
            {
                ["Value"] = new Dictionary<string, object?>
                {
                    ["items"] = new List<object?> { "replacement" },
                    ["nested"] = new Dictionary<string, object?> { ["count"] = 4L },
                },
                ["DeltasSinceBase"] = 1L,
            });
            Assert.Equal(assert, (await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.DeltasSinceBase, 1L);

            var secondId = await storage.MintIdAsync<DocumentId>();
            var retiredAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(firstRecord with { Id = secondId }, new DocumentContent.Base(1, new Dictionary<string, object?>
                    {
                        ["items"] = new List<object?> { "new" },
                    })),
                    new StorageWrite.DocumentRetire(firstId),
                    new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(2, [new DeltaOp.Set(P("retiring"), true)])),
                ]);
            var address = new DocumentAddress { Kind = firstRecord.Kind, Scope = firstRecord.Scope };
            Assert.Equal(assert, (await storage.FindDocumentAsync(address, new DocumentPoint.AtSeq(changedAt)))!.Id.Value, firstId.Value);
            Assert.MatchObject(assert, await storage.FindDocumentAsync(address, new DocumentPoint.AtSeq(retiredAt)), new Dictionary<string, object?>
            {
                ["Id"] = secondId,
                ["CreatedAt"] = retiredAt,
            });
            Assert.DeepEqual(
                assert,
                (await storage.ScanDocumentsAsync(new DocumentQuery { Scope = firstRecord.Scope, At = new DocumentPoint.AtSeq(changedAt) }, 10))
                    .Items.Select(d => d.Id.Value).ToList(),
                new List<object?> { firstId.Value });
            Assert.DeepEqual(
                assert,
                (await storage.ScanDocumentsAsync(new DocumentQuery { Scope = firstRecord.Scope, At = new DocumentPoint.AtSeq(retiredAt) }, 10))
                    .Items.Select(d => d.Id.Value).ToList(),
                new List<object?> { secondId.Value });
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(firstId, new DocumentPoint.AtSeq(retiredAt)), null);
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(secondId, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["items"] = new List<object?> { "new" },
            });
        });

        AddCase("streams long document tails across root replacement deltas", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = id,
                Kind = "conversation.long-tail",
                Scope = new DocumentScope.ConversationScope(rootId),
                History = ConversationHistory.Rewindable,
                Fork = ConversationFork.AsOf,
            };
            var initial = LongTailValue(0, 512, value => $"{value}", 0);
            var createdAt = await storage.CommitAsync(
                [new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, initial))]);
            var beforeReplacement = CloneLongTail(initial);
            var beforeReplacementAt = createdAt;
            for (var revision = 1; revision <= 24; revision++)
            {
                var index = revision * 17 % RowsOf(beforeReplacement).Count;
                RowsOf(beforeReplacement)[index] = new Dictionary<string, object?>
                {
                    ["value"] = (long)-revision,
                    ["stable"] = $"row-{index}",
                };
                beforeReplacement["revision"] = (long)revision;
                beforeReplacementAt = await storage.CommitAsync(
                    [
                        new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1,
                        [
                            new DeltaOp.Set(P("rows", (long)index, "value"), (long)-revision),
                            new DeltaOp.Set(P("revision"), (long)revision),
                        ])),
                    ]);
            }

            var replacement = LongTailValue(100, 512, value => $"{10_000 + value}", 10_000, "new-");
            var replacementSnapshot = CloneLongTail(replacement);
            var replacementAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1, [new DeltaOp.Replace(replacement)])),
                ]);
            RowsOf(replacement)[0] = new Dictionary<string, object?> { ["value"] = -999L, ["stable"] = "new-0" };

            var current = CloneLongTail(replacementSnapshot);
            for (var revision = 101; revision <= 124; revision++)
            {
                var index = revision * 19 % RowsOf(current).Count;
                RowsOf(current)[index] = new Dictionary<string, object?>
                {
                    ["value"] = (long)-revision,
                    ["stable"] = $"new-{index}",
                };
                current["revision"] = (long)revision;
                await storage.CommitAsync(
                    [
                        new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1,
                        [
                            new DeltaOp.Set(P("rows", (long)index, "value"), (long)-revision),
                            new DeltaOp.Set(P("revision"), (long)revision),
                        ])),
                    ]);
            }

            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(createdAt)))!.Value, initial);
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(beforeReplacementAt)))!.Value, beforeReplacement);
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(replacementAt)))!.Value, replacementSnapshot);
            var read = (await storage.GetDocumentAsync(id, new DocumentPoint.Current()))!;
            Assert.DeepEqual(assert, read.Value, current);
            RowsOf((Dictionary<string, object?>)read.Value)[0] = new Dictionary<string, object?> { ["value"] = -1_000L, ["stable"] = "new-0" };
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(id, new DocumentPoint.Current()))!.Value, current);
        });

        AddCase("copies stored document bases independently and rejects ambiguous sources", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var childId = await storage.MintIdAsync<ConversationId>();
            var secondChildId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord { Id = childId }),
                    new StorageWrite.Conversation(new ConversationRecord { Id = secondChildId }),
                ]);
            var sourceId = await storage.MintIdAsync<DocumentId>();
            var sourceRecord = new DocumentCreate
            {
                Id = sourceId,
                Kind = "copy.source",
                Scope = new DocumentScope.ConversationScope(rootId),
                History = ConversationHistory.Rewindable,
                Fork = ConversationFork.AsOf,
            };
            var createdAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(sourceRecord, new DocumentContent.Base(2, new Dictionary<string, object?>
                    {
                        ["count"] = 1L,
                        ["rows"] = new List<object?> { new Dictionary<string, object?> { ["value"] = "base" } },
                    })),
                ]);
            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(sourceId, new DocumentContent.Delta(2,
                    [
                        new DeltaOp.Set(P("count"), 2L),
                        new DeltaOp.Splice(P("rows"), 1, 0, new List<object?> { new Dictionary<string, object?> { ["value"] = "current" } }),
                    ])),
                ]);
            var historicalCopyId = await storage.MintIdAsync<DocumentId>();
            var currentCopyId = await storage.MintIdAsync<DocumentId>();
            var retiredCopyId = await storage.MintIdAsync<DocumentId>();

            DocumentCreate ChildRecord(DocumentId childDocumentId, ConversationId conversationId) => new()
            {
                Id = childDocumentId,
                Kind = sourceRecord.Kind,
                Scope = new DocumentScope.ConversationScope(conversationId),
                History = ConversationHistory.Rewindable,
                Fork = ConversationFork.AsOf,
            };

            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCopyWrite(
                        ChildRecord(historicalCopyId, childId),
                        new DocumentCopySource { Id = sourceId, At = new DocumentPoint.AtSeq(createdAt) }),
                    new StorageWrite.DocumentCopyWrite(
                        ChildRecord(currentCopyId, secondChildId),
                        new DocumentCopySource { Id = sourceId, At = new DocumentPoint.Current() }),
                    new StorageWrite.DocumentCopyWrite(
                        ChildRecord(retiredCopyId, rootId),
                        new DocumentCopySource { Id = sourceId, At = new DocumentPoint.Current() }),
                    new StorageWrite.DocumentRetire(retiredCopyId),
                ]);
            Assert.MatchObject(assert, await storage.GetDocumentAsync(historicalCopyId, new DocumentPoint.Current()), new Dictionary<string, object?>
            {
                ["Version"] = 2,
                ["Value"] = new Dictionary<string, object?>
                {
                    ["count"] = 1L,
                    ["rows"] = new List<object?> { new Dictionary<string, object?> { ["value"] = "base" } },
                },
            });
            Assert.MatchObject(assert, await storage.GetDocumentAsync(currentCopyId, new DocumentPoint.Current()), new Dictionary<string, object?>
            {
                ["Version"] = 2,
                ["Value"] = new Dictionary<string, object?>
                {
                    ["count"] = 2L,
                    ["rows"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["value"] = "base" },
                        new Dictionary<string, object?> { ["value"] = "current" },
                    },
                },
            });
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(retiredCopyId, new DocumentPoint.Current()), null);

            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(sourceId, new DocumentContent.Base(2, new Dictionary<string, object?>
                    {
                        ["count"] = 99L,
                        ["rows"] = new List<object?>(),
                    })),
                    new StorageWrite.DocumentRetire(sourceId),
                ]);
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(currentCopyId, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["count"] = 2L,
                ["rows"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["value"] = "base" },
                    new Dictionary<string, object?> { ["value"] = "current" },
                },
            });

            var latestSourceId = await storage.MintIdAsync<DocumentId>();
            var latestCopyId = await storage.MintIdAsync<DocumentId>();
            var latestSource = new DocumentCreate
            {
                Id = latestSourceId,
                Kind = "copy.latest",
                Scope = new DocumentScope.ConversationScope(rootId),
                History = ConversationHistory.Latest,
                Fork = ConversationFork.Current,
            };
            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(latestSource, new DocumentContent.Base(4, new Dictionary<string, object?>
                    {
                        ["retained"] = "copy",
                    })),
                ]);
            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCopyWrite(
                        latestSource with { Id = latestCopyId, Scope = new DocumentScope.ConversationScope(childId) },
                        new DocumentCopySource { Id = latestSourceId, At = new DocumentPoint.Current() }),
                ]);
            await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(latestSourceId, new DocumentContent.Base(4, new Dictionary<string, object?>
                    {
                        ["retained"] = "source-only",
                    })),
                    new StorageWrite.DocumentRetire(latestSourceId),
                ]);
            Assert.MatchObject(assert, await storage.GetDocumentAsync(latestCopyId, new DocumentPoint.Current()), new Dictionary<string, object?>
            {
                ["Version"] = 4,
                ["Value"] = new Dictionary<string, object?> { ["retained"] = "copy" },
            });

            var conflictId = await storage.MintIdAsync<DocumentId>();
            await assert.Rejects(() => storage.CommitAsync(
                    [
                        new StorageWrite.DocumentCopyWrite(
                            ChildRecord(conflictId, childId),
                            new DocumentCopySource { Id = currentCopyId, At = new DocumentPoint.Current() }),
                        new StorageWrite.DocumentRetire(currentCopyId),
                    ]),
                "StorageRejected");
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(conflictId, new DocumentPoint.Current()), null);
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(currentCopyId, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["count"] = 2L,
                ["rows"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["value"] = "base" },
                    new Dictionary<string, object?> { ["value"] = "current" },
                },
            });

            var mismatchId = await storage.MintIdAsync<DocumentId>();
            await assert.Rejects(() => storage.CommitAsync(
                    [
                        new StorageWrite.DocumentCopyWrite(
                            ChildRecord(mismatchId, childId) with { Kind = "copy.mismatch" },
                            new DocumentCopySource { Id = currentCopyId, At = new DocumentPoint.Current() }),
                    ]),
                "StorageRejected");
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(mismatchId, new DocumentPoint.Current()), null);
        });

        AddCase("uses bases for version transitions and rejects historical reads of current-only documents", async storage =>
        {
            await CreateRootAsync(storage);
            var id = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = id,
                Kind = "session.settings",
                Scope = new DocumentScope.SessionScope(),
            };
            await storage.CommitAsync(
                [new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1L }))]);
            await storage.CommitAsync(
                [new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), 2L)]))]);
            var migratedAt = await storage.CommitAsync(
                [new StorageWrite.DocumentChange(id, new DocumentContent.Base(2, new Dictionary<string, object?> { ["count"] = 3L }))]);
            Assert.MatchObject(assert, await storage.GetDocumentAsync(id, new DocumentPoint.Current()), new Dictionary<string, object?>
            {
                ["Version"] = 2,
                ["Value"] = new Dictionary<string, object?> { ["count"] = 3L },
            });
            await assert.Rejects(() => storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(migratedAt)),
                "does not retain historical content");

            await assert.Rejects(() => storage.CommitAsync(
                    [new StorageWrite.DocumentChange(id, new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), 4L)]))]),
                "version transition requires a base");
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(id, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["count"] = 3L,
            });
            await storage.CommitAsync([new StorageWrite.DocumentRetire(id)]);
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(id, new DocumentPoint.Current()), null);
        });

        AddCase("indexes logical addresses and exact-scope scans independently", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var firstId = await storage.MintIdAsync<DocumentId>();
            var secondId = await storage.MintIdAsync<DocumentId>();
            var conversationId = await storage.MintIdAsync<DocumentId>();
            var taskId = await storage.MintIdAsync<TaskId<object?>>();
            var taskSingletonId = await storage.MintIdAsync<DocumentId>();
            var taskFamilyId = await storage.MintIdAsync<DocumentId>();
            var taskOtherKindId = await storage.MintIdAsync<DocumentId>();
            var createdAt = await storage.CommitAsync(
                [
                    new StorageWrite.Task(PendingTask(taskId, rootId)),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate { Id = firstId, Kind = "cache", Scope = new DocumentScope.SessionScope(), Key = "__proto__" },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "first" })),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate { Id = secondId, Kind = "cache", Scope = new DocumentScope.SessionScope(), Key = "constructor" },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "second" })),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = conversationId,
                            Kind = "cache",
                            Scope = new DocumentScope.ConversationScope(rootId),
                            History = ConversationHistory.Latest,
                            Fork = ConversationFork.Current,
                            Key = "__proto__",
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "conversation" })),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate { Id = taskSingletonId, Kind = "task.cache", Scope = new DocumentScope.TaskScope(taskId) },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "singleton" })),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate { Id = taskFamilyId, Kind = "task.cache", Scope = new DocumentScope.TaskScope(taskId), Key = "member" },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "family" })),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate { Id = taskOtherKindId, Kind = "task.other", Scope = new DocumentScope.TaskScope(taskId) },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["owner"] = "other" })),
                ]);

            Assert.Equal(
                assert,
                (await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = "cache", Scope = new DocumentScope.SessionScope(), Key = "__proto__" },
                    new DocumentPoint.Current()))!.Id.Value,
                firstId.Value);
            Assert.Equal(
                assert,
                (await storage.ScanDocumentsAsync(new DocumentQuery { Scope = new DocumentScope.SessionScope(), At = new DocumentPoint.Current() }, 1)).Items.Count,
                1);
            var first = await storage.ScanDocumentsAsync(new DocumentQuery { Scope = new DocumentScope.SessionScope(), At = new DocumentPoint.Current() }, 1);
            var second = await storage.ScanDocumentsAsync(new DocumentQuery { Scope = new DocumentScope.SessionScope(), At = new DocumentPoint.Current() }, 1, first.Next);
            Assert.DeepEqual(
                assert,
                first.Items.Concat(second.Items).Select(d => d.Id.Value).ToList(),
                new List<object?> { firstId.Value, secondId.Value });
            Assert.DeepEqual(
                assert,
                (await storage.ScanDocumentsAsync(
                    new DocumentQuery { Scope = new DocumentScope.ConversationScope(rootId), At = new DocumentPoint.Current() }, 10))
                    .Items.Select(d => d.Id.Value).ToList(),
                new List<object?> { conversationId.Value });
            Assert.Equal(
                assert,
                (await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = "task.cache", Scope = new DocumentScope.TaskScope(taskId) },
                    new DocumentPoint.Current()))!.Id.Value,
                taskSingletonId.Value);
            Assert.Equal(
                assert,
                (await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = "task.cache", Scope = new DocumentScope.TaskScope(taskId), Key = "member" },
                    new DocumentPoint.Current()))!.Id.Value,
                taskFamilyId.Value);
            Assert.DeepEqual(
                assert,
                (await storage.ScanDocumentsAsync(
                    new DocumentQuery { Scope = new DocumentScope.TaskScope(taskId), At = new DocumentPoint.Current(), Kind = "task.cache" }, 10))
                    .Items.Select(d => d.Id.Value).ToList(),
                new List<object?> { taskSingletonId.Value, taskFamilyId.Value });
            await assert.Rejects(() => storage.GetDocumentAsync(taskSingletonId, new DocumentPoint.AtSeq(createdAt)),
                "does not retain historical content");
        });

        AddCase("keeps document lifecycle failures atomic and gives create-plus-retire an empty lifetime", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var firstId = await storage.MintIdAsync<DocumentId>();
            var secondId = await storage.MintIdAsync<DocumentId>();
            var record = new DocumentCreate
            {
                Id = firstId,
                Kind = "singleton",
                Scope = new DocumentScope.SessionScope(),
            };
            await storage.CommitAsync(
                [new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 1L }))]);
            await assert.Rejects(() => storage.CommitAsync(
                    [
                        new StorageWrite.DocumentCreateWrite(
                            record with { Id = secondId },
                            new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 2L })),
                        new StorageWrite.DocumentChange(firstId, new DocumentContent.Delta(1, [])),
                    ]),
                "already has a current incarnation");
            Assert.DeepEqual(assert, (await storage.GetDocumentAsync(firstId, new DocumentPoint.Current()))!.Value, new Dictionary<string, object?>
            {
                ["value"] = 1L,
            });
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(secondId, new DocumentPoint.Current()), null);

            var emptyId = await storage.MintIdAsync<DocumentId>();
            var emptyAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = emptyId,
                            Kind = record.Kind,
                            Key = "empty",
                            Scope = new DocumentScope.ConversationScope(rootId),
                            History = ConversationHistory.Rewindable,
                            Fork = ConversationFork.Initial,
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?>())),
                    new StorageWrite.DocumentRetire(emptyId),
                ]);
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(emptyId, new DocumentPoint.Current()), null);
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(emptyId, new DocumentPoint.AtSeq(emptyAt)), null);
            Assert.StrictEqual(
                assert,
                await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = record.Kind, Scope = new DocumentScope.ConversationScope(rootId), Key = "empty" },
                    new DocumentPoint.AtSeq(emptyAt)),
                null);
        });

        AddCase("rolls back record tables and secondary indexes when a document command fails", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var taskId = await storage.MintIdAsync<TaskId<object?>>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            var documentId = await storage.MintIdAsync<DocumentId>();
            var task = PendingTask(taskId, rootId);
            var submission = new SubmissionRecord.InputRecord
            {
                Id = submissionId,
                ConversationId = rootId,
                RequestId = "atomic",
                Status = SubmissionStatus.Queued,
            };
            var record = new DocumentCreate
            {
                Id = documentId,
                Kind = "atomic",
                Scope = new DocumentScope.SessionScope(),
            };
            var baselineSeq = await storage.CommitAsync(
                [
                    new StorageWrite.Task(task),
                    new StorageWrite.Submission(submission),
                    new StorageWrite.DocumentCreateWrite(record, new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 1L })),
                ]);

            var entryId = await storage.MintIdAsync<EntryId>();
            var conflictingDocumentId = await storage.MintIdAsync<DocumentId>();
            await assert.Rejects(() => storage.CommitAsync(
                    [
                        new StorageWrite.Task(task with
                        {
                            State = new TaskState { Status = TaskStatus.Running, Checkpoint = PhaseCheckpoint("effect") },
                        }),
                        new StorageWrite.Submission(submission with { Status = SubmissionStatus.Unanswered, Reason = "failed" }),
                        new StorageWrite.Entry(Entry(entryId, rootId, "transient")),
                        new StorageWrite.DocumentCreateWrite(
                            record with { Id = conflictingDocumentId },
                            new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 2L })),
                    ]),
                "already has a current incarnation");

            Assert.DeepEqual(assert, await storage.GetTaskAsync(taskId), task);
            Assert.DeepEqual(assert, (await storage.ScanTasksAsync(new TaskQuery { Status = TaskStatus.Pending }, 10)).Items.ToList(), new List<object?> { task });
            Assert.DeepEqual(assert, await storage.GetSubmissionByRequestAsync(rootId, "atomic"), submission);
            Assert.StrictEqual(assert, await storage.GetEntryAsync(entryId), null);
            Assert.StrictEqual(assert, await storage.GetDocumentAsync(conflictingDocumentId, new DocumentPoint.Current()), null);
            Assert.Equal(
                assert,
                (await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = record.Kind, Scope = record.Scope },
                    new DocumentPoint.Current()))!.Id.Value,
                documentId.Value);
            var afterRollbackSeq = await storage.CommitAsync(
                [new StorageWrite.DocumentChange(documentId, new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), 3L)]))]);
            Assert.GreaterThan(assert, afterRollbackSeq.Value, baselineSeq.Value);
        });

        AddCase("keeps one global record ID namespace and rejects exhausted ID minting", async storage =>
        {
            var rootId = await CreateRootAsync(storage);
            var explicitEntryId = DurableIds.EntryId(100);
            await storage.CommitAsync([new StorageWrite.Entry(Entry(explicitEntryId, rootId))]);
            Assert.Equal(assert, (await storage.MintIdAsync<EntryId>()).Value, 101L);
            await assert.Rejects(() => storage.CommitAsync(
                    [new StorageWrite.Task(PendingTask(DurableIds.TaskId<object?>(explicitEntryId.Value), rootId))]),
                $"ID {explicitEntryId.Value} already belongs to entry");

            await storage.CommitAsync(
                [new StorageWrite.Entry(Entry(DurableIds.EntryId(long.MaxValue), rootId, "last-id"))]);
            await assert.Rejects(() => storage.MintIdAsync<EntryId>(), "ID space is exhausted");
            await assert.Rejects(() => storage.MintIdAsync<EntryId>(), "ID space is exhausted");
        });

        AddCase("rejects every operation after close", async storage =>
        {
            await CreateRootAsync(storage);
            await storage.CloseAsync();
            await assert.Rejects(() => storage.GetConversationAsync(RootConversationId), "closed");
            await assert.Rejects(() => storage.CommitAsync([]), "closed");
            await assert.Rejects(() => storage.MintIdAsync<ConversationId>(), "closed");
        });

        return cases;
    }

    // ─── 辅助 ──────────────────────────────────────────────────────────────

    /// <summary>构造 Chord 路径：字符串段为对象键，<c>long</c> 段为数组索引。对应 TS 的 <c>["a", "b", 0]</c> 字面量。</summary>
    private static Path P(params object[] segments) =>
        new([.. segments.Select(segment => segment switch
        {
            long index => Seg.Index(index),
            int index => Seg.Index(index),
            _ => Seg.Key((string)segment),
        })]);

    /// <summary>根对话 ID（TS <c>ROOT_CONVERSATION_ID</c>）。</summary>
    public static ConversationId RootConversationId { get; } = DurableIds.ConversationId(1);

    private static async Task<ConversationId> CreateRootAsync(IStorage storage)
    {
        await storage.CommitAsync([new StorageWrite.Conversation(new ConversationRecord { Id = RootConversationId })]);
        return RootConversationId;
    }

    private static TaskRecord PendingTask(TaskId<object?> id, ConversationId conversationId, string phase = "ready") => new()
    {
        Id = id,
        ConversationId = conversationId,
        Kind = "test.task",
        Version = 1,
        Input = new Dictionary<string, object?> { ["value"] = id.Value },
        State = new TaskState { Status = TaskStatus.Pending, Checkpoint = PhaseCheckpoint(phase) },
        Background = false,
        AbortRequested = false,
    };

    private static EntryRecord Entry(
        EntryId id,
        ConversationId conversationId,
        string kind = "message",
        object? Data = null,
        EntryId? Head = null) => new()
    {
        Id = id,
        ConversationId = conversationId,
        Kind = kind,
        Data = Data,
        Head = Head,
    };

    private static Dictionary<string, object?> PhaseCheckpoint(string phase) =>
        new() { ["phase"] = phase };

    private static Dictionary<string, object?> PhaseWithCount(string phase, long count) =>
        new()
        {
            ["phase"] = phase,
            ["nested"] = new Dictionary<string, object?> { ["count"] = count },
        };

    private static Dictionary<string, object?> PhaseAttempt(string phase, long attempt) =>
        new() { ["phase"] = phase, ["attempt"] = attempt };

    private static Dictionary<string, object?> NestedData(long first, long second) =>
        new() { ["nested"] = new List<object?> { first, second } };

    private static Dictionary<string, object?> TextData(string text) =>
        new() { ["text"] = text };

    private static Dictionary<string, object?> CodesData(string code) =>
        new() { ["codes"] = new List<object?> { code } };

    private static List<object?> NestedValues(object data) =>
        (List<object?>)((Dictionary<string, object?>)data)["nested"]!;

    private static Dictionary<string, object?> PhaseNested(object checkpoint) =>
        (Dictionary<string, object?>)((Dictionary<string, object?>)checkpoint)["nested"]!;

    private static List<object?> CodesValues(object data) =>
        (List<object?>)((Dictionary<string, object?>)data)["codes"]!;

    private static object? EntryData(StoredEntry? stored) => stored?.Entry.Data;

    private static IReadOnlyDictionary<string, object?>? RoundTripCursor(IReadOnlyDictionary<string, object?> cursor) =>
        (IReadOnlyDictionary<string, object?>)JsonRoundTrip(cursor)!;

    /// <summary>JSON 往返：仅保留 JSON 可表达的形态（对象→字典、数组→列表、标量原样），对齐 TS 的 JSON.parse(JSON.stringify(x))。</summary>
    private static object? JsonRoundTrip(object? value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return ToJsonShape(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));
    }

    private static object? ToJsonShape(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ToJsonShape(property.Value)),
        System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ToJsonShape).ToList(),
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : (object)element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> LongTailValue(long revision, int rowCount, Func<int, string> stable, long valueBase, string prefix = "row-")
    {
        var rows = new List<object?>();
        for (var value = 0; value < rowCount; value++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["value"] = valueBase + value,
                ["stable"] = $"{prefix}{value}",
            });
        }

        return new Dictionary<string, object?>
        {
            ["revision"] = revision,
            ["rows"] = rows,
        };
    }

    private static List<object?> RowsOf(Dictionary<string, object?> value) => (List<object?>)value["rows"]!;

    private static Dictionary<string, object?> CloneLongTail(Dictionary<string, object?> source)
    {
        var rows = new List<object?>();
        foreach (var row in RowsOf(source))
        {
            var map = (Dictionary<string, object?>)row!;
            rows.Add(new Dictionary<string, object?>(map));
        }

        return new Dictionary<string, object?>
        {
            ["revision"] = source["revision"],
            ["rows"] = rows,
        };
    }
}
