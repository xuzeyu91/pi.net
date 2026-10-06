using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskScheduler = System.Threading.Tasks.TaskScheduler;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// P57：<c>Harness.inspect()</c>（harness-inspect.test.ts）。
/// <para>覆盖 <see cref="HarnessImpl.InspectAsync"/> 的存活状态派生——ready（是否 migrates）、waiting、
/// blocked 的 <c>missing_task</c> / <c>task_too_old</c> / <c>migration_failed</c>——以及调度开关对
/// 迁移与运行的影响。<c>inspect</c> 不运行任务代码。</para>
/// </summary>
public class HarnessInspectTests
{
    private static readonly Context Ctx = Context.Background;

    /// <summary>单阶段、阻塞于门闩、随后完成的任务定义。对应 TS <c>task()</c>。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> GatedTask(
        string name, int version, Task<object?>? gate = null, Action? migrate = null)
        => HarnessTestSupport.DefineTask(
            name, version,
            [("run", (task, runtime, context) => (gate ?? Task.FromResult<object?>(null)).ContinueWith(
                _ => runtime.CommitAsync((_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context),
                TaskScheduler.Default).Unwrap())],
            abort: null,
            migrate: migrate);

    private static TaskInspection? StateOf(IReadOnlyList<TaskInspection> tasks, TaskId<JsonDict> id)
        => tasks.FirstOrDefault(entry => entry.Record.Id.Value == id.Value);

    private static TaskId<object?> Obj(TaskId<JsonDict> id) => TaskId<object?>.From(id.Value);

    [Fact]
    public async Task Inspect_DerivesEveryLiveTaskStateWithoutRunningTaskCode()
    {
        var gate = HarnessTestSupport.NewDeferred<object?>();
        var gateTask = GatedTask("test.gate", 1, gate.Task);

        // test.dependent：wait 阶段提交 waiting(gate)，done 阶段完成。
        var gateIdHolder = new TaskId<JsonDict>[1];
        var dependent = HarnessTestSupport.DefineTask("test.dependent", 1,
        [
            ("wait", (task, runtime, context) => runtime.CommitAsync(
                (_, _) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Waiting,
                    Checkpoint = new Dictionary<string, object?> { ["phase"] = "done" },
                    On = [Obj(gateIdHolder[0])],
                    Policy = JoinPolicy.AllSettled,
                }),
                context)),
            ("done", HarnessTestSupport.CompleteWith(null)),
        ]);

        var migrations = 0;
        var registered = new List<AnyDurableTask>
        {
            AnyDurableTask.From(gateTask),
            AnyDurableTask.From(dependent),
            AnyDurableTask.From(GatedTask("test.migrating", 2, migrate: () => migrations++)),
            AnyDurableTask.From(GatedTask("test.no-migration", 2)),
            AnyDurableTask.From(GatedTask("test.failing", 2, migrate: () => throw new InvalidOperationException("cannot migrate"))),
            AnyDurableTask.From(GatedTask("test.too-old", 1)),
        };

        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(new MemoryStorage(), registered);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var owner = new TaskOwnership.ConversationOwner();
            var ids = await root.CommitAsync(async tx =>
            {
                Task<TaskId<JsonDict>> Stored(string name, int version)
                    => HarnessTestSupport.CreateRawTaskAsync(tx, name, version, "run", root.Id);
                var g = await tx.CreateTaskAsync(gateTask, null, new TaskOptions { Ownership = owner, ConversationId = root.Id });
                gateIdHolder[0] = g;
                return new
                {
                    Gate = g,
                    Dependent = await tx.CreateTaskAsync(dependent, null, new TaskOptions { Ownership = owner, ConversationId = root.Id }),
                    // 以不同于注册定义的版本存储。
                    Migrating = await Stored("test.migrating", 1),
                    NoMigration = await Stored("test.no-migration", 1),
                    Failing = await Stored("test.failing", 1),
                    TooOld = await Stored("test.too-old", 2),
                    Missing = await Stored("test.missing", 1),
                };
            }, Ctx);

            var paused = await harness.InspectAsync(Ctx);
            Assert.Equal("paused", paused.Scheduling);
            Assert.Equal(
                new[]
                {
                    ids.Gate.Value, ids.Dependent.Value, ids.Migrating.Value, ids.NoMigration.Value,
                    ids.Failing.Value, ids.TooOld.Value, ids.Missing.Value,
                },
                paused.Tasks.Select(entry => entry.Record.Id.Value).ToArray());
            Assert.False(Assert.IsType<TaskInspection.Ready>(StateOf(paused.Tasks, ids.Gate)!).Migrates);
            Assert.False(Assert.IsType<TaskInspection.Ready>(StateOf(paused.Tasks, ids.Dependent)!).Migrates);
            Assert.True(Assert.IsType<TaskInspection.Ready>(StateOf(paused.Tasks, ids.Migrating)!).Migrates);
            // 从未尝试的迁移不会被运行以判定。
            Assert.True(Assert.IsType<TaskInspection.Ready>(StateOf(paused.Tasks, ids.Failing)!).Migrates);
            var noMigration = Assert.IsType<TaskInspection.Blocked>(StateOf(paused.Tasks, ids.NoMigration)!);
            Assert.Equal("migration_failed", noMigration.Reason);
            Assert.Contains("test.no-migration version 2 has no migration from 1", noMigration.Error!.ToString());
            Assert.Equal("task_too_old", Assert.IsType<TaskInspection.Blocked>(StateOf(paused.Tasks, ids.TooOld)!).Reason);
            Assert.Equal("missing_task", Assert.IsType<TaskInspection.Blocked>(StateOf(paused.Tasks, ids.Missing)!).Reason);
            Assert.Equal(0, migrations);
            Assert.Equal("paused", (await harness.InspectAsync(Ctx)).Scheduling);

            harness.Resume();
            await HarnessTestSupport.WaitForAsync(() => migrations == 1);
            await harness.WaitForTaskAsync(Obj(ids.Migrating), Ctx);
            var running = await harness.InspectAsync(Ctx);
            await HarnessTestSupport.WaitForAsync(async () =>
            {
                running = await harness.InspectAsync(Ctx);
                return StateOf(running.Tasks, ids.Gate) is TaskInspection.Running
                    && StateOf(running.Tasks, ids.Dependent) is TaskInspection.Waiting;
            });
            var waiting = Assert.IsType<TaskInspection.Waiting>(StateOf(running.Tasks, ids.Dependent)!);
            Assert.Equal(ids.Gate.Value, Assert.Single(waiting.On).Value);
            Assert.Equal("running", running.Scheduling);
            Assert.IsType<TaskInspection.Running>(StateOf(running.Tasks, ids.Gate)!);
            // 迁移完成的任务已不在存活集。
            Assert.Null(StateOf(running.Tasks, ids.Migrating));
            Assert.Equal("migration_failed", Assert.IsType<TaskInspection.Blocked>(StateOf(running.Tasks, ids.Failing)!).Reason);

            gate.Resolve(null);
            await harness.WaitForTaskAsync(Obj(ids.Dependent), Ctx);
            var settled = await harness.InspectAsync(Ctx);
            Assert.Equal(
                new[] { ids.NoMigration.Value, ids.Failing.Value, ids.TooOld.Value, ids.Missing.Value },
                settled.Tasks.Select(entry => entry.Record.Id.Value).ToArray());
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Inspect_ListsUnsettledSubmissions()
    {
        // 未应答 provider：输入提交停留在运行中，其 generation 任务应可被 inspect 观察到。
        var provider = new FakeProvider("fake", _ => Reply("never"), block: true);
        var reports = new List<object>();
        var harness = await DurableHarness.OpenAsync(
            new MemoryStorage(), IntegrationOptions(provider, reports), Ctx);
        try
        {
            var root = await harness.RootAsync(Ctx, new AgentChange { Model = new ModelRef("fake", "fake-1") });
            var other = await harness.CreateConversationAsync(
                new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
            await other.SubmitAsync(new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "note" } }, Ctx);
            var input = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await provider.Reached;

            var inspection = await harness.InspectAsync(Ctx);
            var status = await input.StatusAsync(Ctx);
            Assert.Equal(new[] { status.Id.Value }, inspection.Submissions.Select(record => record.Id.Value).ToArray());
            Assert.Equal(
                new[] { ("pi.generation", "running") },
                inspection.Tasks.Select(entry => (entry.Record.Kind, StatusKind(entry))).ToArray());
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static string StatusKind(TaskInspection inspection) => inspection switch
    {
        TaskInspection.Running => "running",
        TaskInspection.Ready => "ready",
        TaskInspection.Waiting => "waiting",
        TaskInspection.Completing => "completing",
        TaskInspection.Blocked => "blocked",
        _ => "?",
    };

    private static AssistantMessage Reply(string text)
        => new([new TextContent(text)])
        {
            StopReason = StopReason.Stop,
            Model = "fake-1",
            Provider = "fake",
            UsageStats = new Pi.Ai.Types.Usage(5, 7),
        };

    private static HarnessOptions IntegrationOptions(FakeProvider provider, List<object> reports)
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(provider);
        return new HarnessOptions
        {
            Models = models,
            Registry = Registry.CreateRegistry(),
            Settings = new HarnessSettings
            {
                Extensions = [],
                Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            },
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            OnReport = reports.Add,
        };
    }
}
