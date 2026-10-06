using System.Threading;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 移植 <c>packages/durable/test/harness-lifecycle.test.ts</c>：Harness open / close 生命周期与
/// 暂停调度的调度门控、registry 恢复时取值。
/// <para>
/// 已移植（6 例，全部通过）：<c>Open_ClosesWithoutCancelledCallerContextAndRethrowsOriginalError</c>
/// （open 失败时不带取消的调用方 context 关闭并重抛原错误）；
/// <c>Open_ReportsFailingCloseAndStillRethrowsOpenError</c>（open 失败且 close 也失败时上报 close 错误仍重抛）；
/// <c>Close_JoinsTaskHandlerIgnoringSignalBeforeClosingStorage</c>（close 加入忽略信号的任务处理器后再关存储）；
/// <c>Close_IsReentrantAndJoinsBeforeClosingStorage</c>（重复/并发 close 均加入运行中任务后再关存储）；
/// <c>Close_SettlesDurablyCommitWhoseCommitterWasCancelled</c>（committer 在存储中被取消的提交仍持久化）；
/// <c>Registry_RunsWhatRegistryHoldsAtResume</c>（恢复时运行 registry 当时持有的定义）。
/// </para>
/// <para>
/// 关键差异：C# <see cref="Harness.CloseAsync(Context)"/> 经 <c>ContextSignals.WithoutAbortSignal</c>
/// 剥离调用方中止信号，close 不可取消；TS 的 <c>close(signal)</c> 可取消并在取消后由第二次 close 完成关闭。
/// 因此 <c>Close_IsReentrantAndJoinsBeforeClosingStorage</c> 移植其可等价验证的内核（加入运行中任务 + 重复 close 语义），
/// 而非取消语义。
/// </para>
/// <para>
/// 待续（需额外 C# 适配，见各 TODO）：tool/hook 忽略信号的 close 加入（需 faux 工具调用响应夹具）；
/// close 期间结算的提交不向状态/观察者发帧（需 <see cref="Pi.Chord.Delta"/> 观察者订阅签名适配）；
/// 在线取消的 watch 获取不留订阅（需反射计数 <c>Session._commitListeners</c> + FindDocument 门闩）；
/// close 开始后拒绝全部会话/Harness 操作；封条处排队的读取/等待语义；暂停 Harness 的只读查看器不调度 / 每个进度调用都调度（需 <c>createSubmission</c> 排队写入夹具）。
/// </para>
/// <para>对应 TS 支撑：<c>session-support.ts</c> 的 <c>ControlledStorage</c>（failNextCommit / holdFindDocument）、
/// <c>task-support.ts</c> 的 <c>settled</c> / <c>countingReader</c> / <c>oneStep</c> / <c>start</c> / <c>openTasks</c>。</para>
/// </summary>
public sealed class HarnessLifecycleTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── 支撑helper（对应 TS 模块级函数） ──────────────────────────────────────

    /// <summary>单阶段任务：运行 <paramref name="run"/> 后以 null 完成。对应 TS <c>oneStep()</c>。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> OneStep(string name, Func<Task>? run = null)
        => HarnessTestSupport.DefineTask(
            name, 1,
            [("run", (TaskRecord _, ITaskRuntime runtime, Context context) => RunStepAsync(run, runtime, context))],
            abort: null);

    private static async Task RunStepAsync(Func<Task>? run, ITaskRuntime runtime, Context context)
    {
        if (run is not null)
        {
            await run();
        }

        await runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
    }

    /// <summary>在对话提交中创建属主为该对话的任务。对应 TS <c>start()</c>。</summary>
    private static async Task<TaskId<JsonDict>> StartAsync(
        IConversation conversation, DurableTask<JsonDict, JsonDict, JsonDict> task, bool background = false)
        => await conversation.CommitAsync(
            tx => tx.CreateTaskAsync(task, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = conversation.Id,
                Background = background,
            }),
            Ctx);

    private static Context WithSignal(CancellationToken signal) => ContextSignals.WithAbortSignal(signal, Ctx);

    /// <summary>原始存储读取是否仍成功（作为已加入调用代码的代理）。对应 TS <c>storageOpen()</c>。</summary>
    private static async Task<bool> StorageOpenAsync(IStorage storage)
    {
        try
        {
            await storage.GetTaskAsync(TaskId<object?>.From(1));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>提交一个带 <c>running</c> 任务的对话，如崩溃遗留，使 open 有一次对账提交可失败。对应 TS <c>seedRunningTask()</c>。</summary>
    private static async Task SeedRunningTaskAsync(IStorage storage)
    {
        var conversationId = await storage.MintIdAsync<ConversationId>();
        var id = await storage.MintIdAsync<TaskId<object?>>();
        var task = new TaskRecord
        {
            Id = id,
            ConversationId = conversationId,
            Kind = "test.seeded",
            Version = 1,
            Input = null,
            Background = false,
            AbortRequested = false,
            State = new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "run" },
            },
        };
        await storage.CommitAsync(
        [
            new StorageWrite.Conversation(new ConversationRecord { Id = conversationId }),
            new StorageWrite.Task(task),
        ]);
    }

    private static HarnessOptions OpenOptions(IRegistryReader registry, List<object>? reports = null)
        => new()
        {
            Models = new Pi.Ai.Models.Models(),
            Registry = registry,
            Settings = new HarnessSettings
            {
                Extensions = [],
                Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            },
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            OnReport = reports is null ? null : reports.Add,
        };

    // ─── Harness open ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Open_ClosesWithoutCancelledCallerContextAndRethrowsOriginalError()
    {
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        await SeedRunningTaskAsync(storage);
        var reader = new HarnessTestSupport.CountingReader(Registry.CreateRegistry());
        var held = storage.HoldCommits();
        storage.FailNextCommit(new InvalidOperationException("disk full"));
        var cts = new CancellationTokenSource();
        var opening = DurableHarness.OpenAsync(storage, OpenOptions(reader), WithSignal(cts.Token));
        await held.Entered;
        cts.Cancel();
        held.Release();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await opening);
        Assert.Contains("disk full", error.Message);
        Assert.Equal(0, reader.Subscriptions);
        Assert.False(await StorageOpenAsync(storage));
    }

    [Fact]
    public async Task Open_ReportsFailingCloseAndStillRethrowsOpenError()
    {
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        await SeedRunningTaskAsync(storage);
        storage.FailNextCommit(new InvalidOperationException("disk full"));
        storage.FailNextClose(new InvalidOperationException("close failed"));
        var reports = new List<object>();
        var opening = DurableHarness.OpenAsync(storage, OpenOptions(Registry.CreateRegistry(), reports), Ctx);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await opening);
        Assert.Contains("disk full", error.Message);
        var report = Assert.IsType<InvalidOperationException>(Assert.Single(reports));
        Assert.Equal("close failed", report.Message);
    }

    // ─── Harness close ────────────────────────────────────────────────────────

    [Fact]
    public async Task Close_JoinsTaskHandlerIgnoringSignalBeforeClosingStorage()
    {
        var storage = new MemoryStorage();
        var reached = HarnessTestSupport.NewDeferred<Unit>();
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        var readAfterRelease = false;
        var stubborn = OneStep("test.close-stubborn", async () =>
        {
            reached.Resolve(default);
            await gate.Task;
            readAfterRelease = await StorageOpenAsync(storage);
        });
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(storage, [AnyDurableTask.From(stubborn)]);
        try
        {
            var root = await harness.RootAsync(Ctx);
            await StartAsync(root, stubborn);
            harness.Resume();
            await reached.Task;
            var closing = harness.CloseAsync(Ctx);
            Assert.False(await HarnessTestSupport.SettledAsync(closing));
            Assert.True(await StorageOpenAsync(storage));
            gate.Resolve(default);
            await closing;
            Assert.True(readAfterRelease);
            Assert.False(await StorageOpenAsync(storage));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    /// <summary>
    /// 对应 TS <c>close 取消后仍保持关闭并在第二次 close 时完成</c>。
    /// 差异说明：C# <see cref="Harness.CloseAsync(Context)"/> 通过
    /// <c>ContextSignals.WithoutAbortSignal</c> 剥离调用方中止信号，close 不可取消；
    /// 因此移植其可等价验证的内核——close 会加入运行中的阶段处理器后再关存储，
    /// 且并发/重复的 close 都等待同一顽固任务完成、之后统一关存储。
    /// </summary>
    [Fact]
    public async Task Close_IsReentrantAndJoinsBeforeClosingStorage()
    {
        var storage = new MemoryStorage();
        var reached = HarnessTestSupport.NewDeferred<Unit>();
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        var readAfterRelease = false;
        var stubborn = OneStep("test.close-reentrant", async () =>
        {
            reached.Resolve(default);
            await gate.Task;
            readAfterRelease = await StorageOpenAsync(storage);
        });
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(storage, [AnyDurableTask.From(stubborn)]);
        var root = await harness.RootAsync(Ctx);
        await StartAsync(root, stubborn);
        harness.Resume();
        await reached.Task;
        var first = harness.CloseAsync(Ctx);
        Assert.False(await HarnessTestSupport.SettledAsync(first));
        // 第二次 close 同样被同一顽固任务阻塞（不提前关存储）。
        var second = harness.CloseAsync(Ctx);
        Assert.False(await HarnessTestSupport.SettledAsync(second));
        Assert.True(await StorageOpenAsync(storage));
        gate.Resolve(default);
        await first;
        await second;
        Assert.True(readAfterRelease);
        Assert.False(await StorageOpenAsync(storage));
    }

    [Fact]
    public async Task Close_SettlesDurablyCommitWhoseCommitterWasCancelled()
    {
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        var harness = await DurableHarness.OpenAsync(storage, OpenOptions(Registry.CreateRegistry()), Ctx);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var held = storage.HoldCommits();
            var cts = new CancellationTokenSource();
            var committing = root.CommitAsync(
                async tx => (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" })).Id,
                WithSignal(cts.Token));
            await held.Entered;
            cts.Cancel();
            held.Release();
            var id = await committing;
            var page = await root.EntriesAsync(new EntryQuery { ConversationId = root.Id }, 10, null, Ctx);
            Assert.Equal([id], page.Items.Select(entry => entry.Id).ToArray());
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── registry changes before resume ───────────────────────────────────────

    [Fact]
    public async Task Registry_RunsWhatRegistryHoldsAtResume()
    {
        var log = new List<string>();
        DurableTask<JsonDict, JsonDict, JsonDict> Define(string label)
            => OneStep("test.late-definition", () =>
            {
                log.Add(label);
                return Task.CompletedTask;
            });

        var (harness, registry, _) = await HarnessTestSupport.OpenTasksAsync(new MemoryStorage(), []);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var missing = await StartAsync(root, Define("unused"));
            var blocked = await harness.InspectAsync(Ctx);
            Assert.Equal("missing_task", Assert.IsType<TaskInspection.Blocked>(Assert.Single(blocked.Tasks)).Reason);

            // 同名扩展就地替换定义，且都在 resume 之前。
            var v1 = new HarnessTestSupport.TaskExtension("tasks", [AnyDurableTask.From(Define("v1"))]);
            registry.Install(v1);
            Assert.IsType<TaskInspection.Ready>(Assert.Single((await harness.InspectAsync(Ctx)).Tasks));
            registry.Uninstall(v1);
            registry.Install(new HarnessTestSupport.TaskExtension("tasks", [AnyDurableTask.From(Define("v2"))]));

            harness.Resume();
            await harness.WaitForTaskAsync(TaskId<object?>.From(missing.Value), Ctx);
            Assert.Equal(["v2"], log);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
