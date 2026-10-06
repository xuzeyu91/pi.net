using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TaskScheduler = Pi.Durable.Harness.TaskScheduler;

/// <summary>
/// P55：任务调度器（scheduler.ts）。以 fake 任务定义驱动真实
/// <see cref="TaskScheduler"/>：阶段推进、无进展 fault、abort 级联、orphaned、failFast、空闲等待与
/// open 恢复。Generation 的 FakeProvider 端到端随 harness.ts 装配（P57）做集成覆盖。
/// </summary>
public class SchedulerTests
{
    private static readonly Context Ctx = Context.Background;

    private sealed record Fixture(
        DurableSession Session,
        IRegistry Registry,
        TaskScheduler Scheduler,
        ConversationId Conv,
        List<Exception> Reports,
        List<(TaskRecord Record, SchedulerOutcome Outcome)> Settled) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Session.CloseAsync(Context.Background).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 测试收尾不抛。
            }
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var registry = Registry.CreateRegistry();
        var reports = new List<Exception>();
        var settled = new List<(TaskRecord, SchedulerOutcome)>();
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())
                .ConfigureAwait(false)).Id,
            Context.Background).ConfigureAwait(false);
        var scheduler = new TaskScheduler(new TaskSchedulerOptions
        {
            Session = session,
            Storage = storage,
            Registry = registry,
            // fake 任务不触达模型目录。
            Models = null!,
            Agent = (conversationId, snapshot, context) => Task.FromResult(
                AgentDocs.ResolveAgent(null, snapshot, AgentDocs.ResolveSettings(null), _ => { })),
            Settings = () => AgentDocs.ResolveSettings(null),
            Env = (_, _) => Task.FromResult<Pi.Durable.Env.IExecutionEnv?>(null),
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Report = reports.Add,
            SettleOutcome = (_, record, outcome) =>
            {
                settled.Add((record, outcome));
                return Task.CompletedTask;
            },
            WithdrawInputs = (_, _) => Task.CompletedTask,
            Conversation = (_, _, _) => Task.FromResult<IConversationHandle?>(null),
            Context = Context.Background,
        });
        // 加载存活任务镜像（对应 harness 创建时的 open()）。
        await scheduler.OpenAsync(Context.Background);
        return new Fixture(session, registry, scheduler, conv, reports, settled);
    }

    private static Context TestContext()
        => ContextSignals.WithAbortSignal(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token, Context.Background);

    private static Task<TaskId<object?>> CreateTaskAsync(
        Fixture fixture,
        DurableTask<JsonDict, JsonDict, JsonDict> definition,
        TaskOwnership ownership,
        ConversationId? conversationId = null,
        bool background = false)
        => fixture.Session.CommitAsync(async tx =>
        {
            var id = await tx.CreateTaskAsync(definition, null, new TaskOptions
            {
                Ownership = ownership,
                ConversationId = conversationId,
                Background = background,
            }).ConfigureAwait(false);
            return TaskId<object?>.From(id.Value);
        }, Context.Background);

    private static async Task<TaskRecord> ReadTaskAsync(Fixture fixture, TaskId<object?> id)
        => (await fixture.Session.CommitAsync(tx => tx.GetTaskAsync(id), Context.Background)
            .ConfigureAwait(false))!;

    private static async Task<TaskRecord> WaitUntilAsync(
        Fixture fixture, TaskId<object?> id, Func<TaskStatus, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var record = await ReadTaskAsync(fixture, id);
            if (predicate(record.State.Status)) return record;
            await Task.Delay(10);
        }

        var current = await ReadTaskAsync(fixture, id);
        throw new TimeoutException(
            $"Task {id.Value} did not reach the expected status; state={current.State.Status}"
            + $" outcome={current.State.Outcome?.Status} abort={current.AbortRequested}"
            + $" reports=[{string.Join("; ", fixture.Reports.Select(error => error.Message))}]");
    }

    private static DurableTask<JsonDict, JsonDict, JsonDict> DefineTask(
        string name, (string Name, TaskPhaseHandler Handler)[] phases, TaskPhaseHandler? abort = null)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = name,
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = phases[0].Name },
                Phases = phases.ToDictionary(p => p.Name, p => (object?)p.Handler),
                Abort = abort,
            },
        };

    private sealed class FakeTaskExtension(string name, AnyDurableTask[] tasks) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    private static TaskPhaseHandler Tracked(List<string> visited, string name, TaskPhaseHandler inner)
        => (task, runtime, context) =>
        {
            visited.Add(name);
            return inner(task, runtime, context);
        };

    private static TaskPhaseHandler AdvanceTo(string phase)
        => (task, runtime, context) => runtime.CommitAsync(
            (tx, current) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = phase },
            }),
            context);

    private static TaskPhaseHandler CompleteWith(object? result)
        => (task, runtime, context) => runtime.CommitAsync(
            (tx, current) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = result },
            }),
            context);

    private static TaskPhaseHandler Stall()
        => (task, runtime, context) => Task.CompletedTask;

    private static TaskPhaseHandler AbortAsAborted(List<string>? visited = null)
        => (task, runtime, context) =>
        {
            visited?.Add("abort");
            return runtime.CommitAsync(
                (tx, current) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
                }),
                context);
        };

    [Fact]
    public async Task Scheduler_RunsTaskThroughPhasesToCompleted()
    {
        using var fixture = await CreateFixtureAsync();
        var visited = new List<string>();
        var definition = DefineTask("fake.two",
        [
            ("s1", Tracked(visited, "s1", AdvanceTo("s2"))),
            ("s2", Tracked(visited, "s2", CompleteWith(42))),
        ]);
        fixture.Registry.Install(new FakeTaskExtension("ext", [AnyDurableTask.From(definition)]));
        var id = await CreateTaskAsync(fixture, definition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        fixture.Scheduler.Resume();

        var record = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(["s1", "s2"], visited);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome!.Status);
        Assert.Equal(42, record.State.Outcome!.Result);
        Assert.Empty(fixture.Reports);
        Assert.Empty(fixture.Settled);
    }

    [Fact]
    public async Task Scheduler_FaultsOnNoDurableProgress()
    {
        using var fixture = await CreateFixtureAsync();
        var definition = DefineTask("fake.stall", [("s1", Stall())]);
        fixture.Registry.Install(new FakeTaskExtension("ext", [AnyDurableTask.From(definition)]));
        var id = await CreateTaskAsync(fixture, definition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        fixture.Scheduler.Resume();

        var record = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Faulted, record.State.Outcome!.Status);
        Assert.Contains("without durable progress", record.State.Outcome!.Error!.Message);
        // 只有调度器写入 faulted；其清理在本提交完成。
        var (settledRecord, settledOutcome) = Assert.Single(fixture.Settled);
        Assert.Equal(id.Value, settledRecord.Id.Value);
        Assert.Equal(TaskOutcomeStatus.Faulted, settledOutcome.Status);
        Assert.Empty(fixture.Reports);
    }

    [Fact]
    public async Task Scheduler_AbortWaitsForOwnedWorkThenRunsAbortHandler()
    {
        using var fixture = await CreateFixtureAsync();
        var parentVisited = new List<string>();
        var childVisited = new List<string>();
        var childId = TaskId<object?>.From(0);
        var parentDefinition = DefineTask("fake.parent",
            [("s1", (task, runtime, context) => runtime.CommitAsync(
                (tx, current) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Waiting,
                    On = [childId],
                    Policy = JoinPolicy.AllSettled,

                    // 对齐 TS：等待提交必须保留阶段 checkpoint。
                    Checkpoint = current.State.Checkpoint,
                }),
                context))],
            AbortAsAborted(parentVisited));
        // 子任务存活足够久，使 abort 标记先于其完成落地（父任务在其普通自有工作消失前不会被
        // 重新保留——abort 自底向上）。
        var childDefinition = DefineTask("fake.child",
            [("c1", (TaskPhaseHandler)(async (task, runtime, context) =>
            {
                await runtime.SleepAsync(runtime.Now() + 300, context);
                await CompleteWith(null)(task, runtime, context);
            }))],
            AbortAsAborted(childVisited));
        fixture.Registry.Install(new FakeTaskExtension("ext",
        [
            AnyDurableTask.From(parentDefinition),
            AnyDurableTask.From(childDefinition),
        ]));
        var parentId = await CreateTaskAsync(
            fixture, parentDefinition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        childId = await CreateTaskAsync(fixture, childDefinition, new TaskOwnership.TaskOwner(parentId));
        fixture.Scheduler.Resume();

        await WaitUntilAsync(fixture, parentId, status => status == TaskStatus.Waiting);
        var result = await fixture.Scheduler.AbortAsync(parentId, TestContext());
        Assert.Equal("marked", result);

        var record = await WaitUntilAsync(fixture, parentId, status => status == TaskStatus.Terminal);
        Assert.True(record.AbortRequested,
            $"outcome={record.State.Outcome?.Status} error={record.State.Outcome?.Error?.Message}"
            + $" visited=[{string.Join(",", parentVisited)}]"
            + $" reports=[{string.Join("; ", fixture.Reports.Select(error => error.Message))}]");
        Assert.Equal(TaskOutcomeStatus.Aborted, record.State.Outcome!.Status);
        Assert.Equal(["abort"], parentVisited);
        // 子任务随级联中止（abort 自底向上）。
        var child = await WaitUntilAsync(fixture, childId, status => status == TaskStatus.Terminal);
        Assert.True(child.AbortRequested);
        Assert.Equal(TaskOutcomeStatus.Aborted, child.State.Outcome!.Status);
        Assert.Equal(["abort"], childVisited);
    }

    [Fact]
    public async Task Scheduler_OrphansAbortedTaskNoDefinitionCanTake()
    {
        using var fixture = await CreateFixtureAsync();
        var definition = DefineTask("fake.doomed", [("s1", CompleteWith(null))]);
        var extension = new FakeTaskExtension("doomed", [AnyDurableTask.From(definition)]);
        fixture.Registry.Install(extension);
        var id = await CreateTaskAsync(fixture, definition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        fixture.Registry.Uninstall(extension);
        fixture.Scheduler.Resume();

        var result = await fixture.Scheduler.AbortAsync(id, TestContext());
        Assert.Equal("marked", result);
        var record = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Orphaned, record.State.Outcome!.Status);
        Assert.Equal("missing_task", record.State.Outcome!.Reason);
        var (_, settledOutcome) = Assert.Single(fixture.Settled);
        Assert.Equal(TaskOutcomeStatus.Orphaned, settledOutcome.Status);
    }

    [Fact]
    public async Task Scheduler_FailFastMarksWaitingParentWhenChildFails()
    {
        using var fixture = await CreateFixtureAsync();
        var childIds = new List<TaskId<object?>>();
        var parentWaited = false;
        var parentDefinition = DefineTask("fake.parent",
            [("s1", (task, runtime, context) => runtime.CommitAsync(
                (tx, current) =>
                {
                    if (!parentWaited)
                    {
                        parentWaited = true;
                        return Task.FromResult<TaskState?>(new TaskState
                        {
                            Status = TaskStatus.Waiting,
                            On = [.. childIds],
                            Policy = JoinPolicy.FailFast,
                            Checkpoint = current.State.Checkpoint,
                        });
                    }

                    // failFast 快速失败后等待者恢复：本测试直接完成。
                    return Task.FromResult<TaskState?>(new TaskState
                    {
                        Status = TaskStatus.Terminal,
                        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed },
                    });
                },
                context))]);
        var failing = DefineTask("fake.failing", [("c1", Stall())]);
        // c2 存活足够久，使 failFast 标记在它完成前落地（否则无标记可断言）。
        var succeeding = DefineTask("fake.ok",
            [("c2", (TaskPhaseHandler)(async (task, runtime, context) =>
            {
                await runtime.SleepAsync(runtime.Now() + 200, context);
                await CompleteWith(null)(task, runtime, context);
            }))],
            AbortAsAborted());
        fixture.Registry.Install(new FakeTaskExtension("ext",
        [
            AnyDurableTask.From(parentDefinition),
            AnyDurableTask.From(failing),
            AnyDurableTask.From(succeeding),
        ]));
        var parentId = await CreateTaskAsync(
            fixture, parentDefinition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        childIds.Add(await CreateTaskAsync(fixture, failing, new TaskOwnership.TaskOwner(parentId)));
        childIds.Add(await CreateTaskAsync(fixture, succeeding, new TaskOwnership.TaskOwner(parentId)));
        fixture.Scheduler.Resume();

        // 失败的孩子保留自己的 faulted 结局。
        var failed = await WaitUntilAsync(fixture, childIds[0], status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Faulted, failed.State.Outcome!.Status);
        // failFast 标记另一个成员：abortRequested 落在 c2 上，由其 abort 处理器以 aborted 结束。
        var marked = await WaitUntilAsync(fixture, childIds[1], status => status == TaskStatus.Terminal);
        Assert.True(marked.AbortRequested);
        Assert.Equal(TaskOutcomeStatus.Aborted, marked.State.Outcome!.Status);
        // 等待者随后恢复并完成。
        var parent = await WaitUntilAsync(fixture, parentId, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Completed, parent.State.Outcome!.Status);
        Assert.False(parent.AbortRequested);
    }

    [Fact]
    public async Task Scheduler_WaitForIdleResolvesWhenScopeDrains()
    {
        using var fixture = await CreateFixtureAsync();
        var definition = DefineTask("fake.one", [("s1", CompleteWith(null))]);
        fixture.Registry.Install(new FakeTaskExtension("ext", [AnyDurableTask.From(definition)]));
        var id = await CreateTaskAsync(fixture, definition, new TaskOwnership.ConversationOwner(), fixture.Conv);

        var idle = fixture.Scheduler.WaitForIdleAsync(fixture.Conv, TestContext());
        Assert.False(idle.IsCompleted);
        fixture.Scheduler.Resume();
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);

        // 范围排空后立即完成。
        var done = fixture.Scheduler.WaitForIdleAsync(fixture.Conv, TestContext());
        Assert.True(done.IsCompleted);
    }

    [Fact]
    public async Task Scheduler_OpenRecoversRunningToPending()
    {
        using var fixture = await CreateFixtureAsync();
        var definition = DefineTask("fake.one", [("s1", CompleteWith(null))]);
        fixture.Registry.Install(new FakeTaskExtension("ext", [AnyDurableTask.From(definition)]));
        var id = await CreateTaskAsync(fixture, definition, new TaskOwnership.ConversationOwner(), fixture.Conv);
        // 模拟崩溃：任务卡在 running。
        await fixture.Session.CommitWithAsync(async tx =>
        {
            var record = await tx.GetTaskAsync(id)
                ?? throw new InvalidOperationException("task missing");
            tx.SetTask(record with { State = record.State with { Status = TaskStatus.Running } });
            return 0;
        }, Context.Background);

        await fixture.Scheduler.OpenAsync(Context.Background);
        var recovered = await ReadTaskAsync(fixture, id);
        Assert.Equal(TaskStatus.Pending, recovered.State.Status);

        fixture.Scheduler.Resume();
        var record = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome!.Status);
    }
}

/// <summary>
/// P55：压缩范围选择（compaction.ts 的 selectCut）。以手工构造的 <see cref="ContextView"/> 驱动。
/// </summary>
public class CompactionSelectCutTests
{
    private static ContextView View(params IReadOnlyList<ChatMessage>[] contributions)
    {
        var entries = contributions.Select((messages, index) => new EntryRecord
        {
            Id = EntryId.From(index + 1),
            ConversationId = ConversationId.From(1),
            Kind = "kind",
            Model = messages.Count > 0 ? messages : null,
        }).ToList();
        return new ContextView
        {
            Entries = entries,
            Contributions = contributions,
            Messages = contributions.SelectMany(messages => messages).ToList(),
        };
    }

    private static IReadOnlyList<ChatMessage> User(params ContentBlock[] content)
        => [new UserMessage(content, 1)];

    private static IReadOnlyList<ChatMessage> Assistant(params ContentBlock[] content)
        => [new AssistantMessage(content)];

    private static IReadOnlyList<ChatMessage> AssistantWithCall(string callId)
        => [new AssistantMessage([new ToolCallContent(callId, "read", new Dictionary<string, object?>())])];

    private static IReadOnlyList<ChatMessage> Result(string callId)
        => [new ToolResultMessage(callId, "read", [new TextContent("ok")])];

    [Fact]
    public void ReturnsNullWhenThereIsNothingToCompact()
    {
        Assert.Null(Compaction.SelectCut(View(), keepRecentTokens: 1));
        // 只有工具结果开头的条目：没有候选。
        Assert.Null(Compaction.SelectCut(View(Result("c1")), keepRecentTokens: 1));
    }

    [Fact]
    public void CutsAtAssistantEntry()
    {
        var cut = Compaction.SelectCut(View(User(new TextContent("hi")), Assistant(new TextContent("yo"))), 1);
        Assert.Equal(1, cut);
    }

    [Fact]
    public void UserEntryCarryingPreviousAssistantToolResultIsNotACandidate()
    {
        // [assistant(c1)][user + result(c1)]：user 条目不是候选，且其之前没有可压缩内容。
        var view = View(AssistantWithCall("c1"), [.. User(new TextContent("q")), .. Result("c1")]);
        Assert.Null(Compaction.SelectCut(view, 1));
    }

    [Fact]
    public void CutsAtFirstCandidateAtOrAfterTail()
    {
        var view = View(
            User(new TextContent("one")),
            Assistant(new TextContent("two")),
            User(new TextContent("three")),
            Assistant(new TextContent("four")));
        Assert.Equal(3, Compaction.SelectCut(view, 1));
    }

    [Fact]
    public void UserEntryBeforeItsToolResultIsNotACandidate()
    {
        // [assistant(c1)][user][result(c1)]：user 条目携带的前属调用结果仍跟随其后 → 不是候选。
        var view = View(AssistantWithCall("c1"), User(new TextContent("q")), Result("c1"));
        // 候选只有 idx0（assistant）；尾部回扫命中 idx2 时候选 >= 2 为空 → 回落最后候选 idx0，
        // 且 idx0 之前无内容 → null。
        Assert.Null(Compaction.SelectCut(view, 1));
    }
}
