using Pi.Ai.Models;
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
/// harness 系列测试的共享脚手架。对应 TS 的 <c>task-support.ts</c> 与 <c>chat-support.ts</c>：
/// 延迟门闩、轮询等待、任务打开与断言辅助，供多份 harness-*.test.ts 移植共用。
/// </summary>
internal static class HarnessTestSupport
{
    internal static readonly Context Ctx = Context.Background;

    /// <summary>供测试判定的延迟门闩；对应 TS <c>deferred()</c>。</summary>
    internal sealed class Deferred<T>
    {
        private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _source.Task;

        public void Resolve(T value) => _source.TrySetResult(value);

        public void Reject(Exception error) => _source.TrySetException(error);

        public void Resolve() => _source.TrySetResult(default!);
    }

    internal static Deferred<T> NewDeferred<T>() => new();

    /// <summary>按次数冲刷宏任务轮次，直到 <paramref name="check"/> 成立。对应 TS <c>eventually()</c>。</summary>
    internal static async Task EventuallyAsync(Func<Task<bool>> check)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await check().ConfigureAwait(false)) return;
            await Task.Yield();
        }

        throw new TimeoutException("Condition was not reached");
    }

    internal static async Task EventuallyAsync(Func<bool> check)
        => await EventuallyAsync(() => Task.FromResult(check())).ConfigureAwait(false);

    /// <summary>实时轮询 <paramref name="check"/> 直到成立。对应 TS <c>waitFor()</c>。</summary>
    internal static async Task WaitForAsync(Func<Task<bool>> check, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await check().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    internal static async Task WaitForAsync(Func<bool> check, int timeoutMs = 5000)
        => await WaitForAsync(() => Task.FromResult(check()), timeoutMs).ConfigureAwait(false);

    /// <summary>下一个完成任务的持久化状态。对应 TS <c>completed()</c>。</summary>
    internal static TaskState Completed(object? result) => new()
    {
        Status = TaskStatus.Terminal,
        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = result },
    };

    /// <summary>下一个中止任务的持久化状态。对应 TS <c>abortedWith()</c>。</summary>
    internal static TaskState AbortedWith(string reason) => new()
    {
        Status = TaskStatus.Terminal,
        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted, Reason = reason },
    };

    /// <summary>登记 fake 任务的扩展。对应 TS <c>defineExtension({ name, tasks })</c>。</summary>
    internal sealed class TaskExtension(string name, AnyDurableTask[] tasks) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    /// <summary>打开装有所述任务定义的 Harness。对应 TS <c>openTasks()</c>。</summary>
    internal static async Task<(HarnessImpl Harness, IRegistry Registry, List<Exception> Reports)> OpenTasksAsync(
        IStorage storage, IReadOnlyList<AnyDurableTask> tasks, Func<long>? now = null)
    {
        var registry = Registry.CreateRegistry();
        if (tasks.Count > 0) registry.Install(new TaskExtension("tasks", [.. tasks]));
        var reports = new List<Exception>();
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = new Models(),
            Registry = registry,
            OnReport = report => reports.Add(report as Exception ?? new InvalidOperationException(report?.ToString())),
            Now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        }, Ctx).ConfigureAwait(false);
        return (harness, registry, reports);
    }

    /// <summary>由阶段处理器数组构造一个无输入/状态/结果约束的 fake 任务定义。对应 TS <c>task()</c>。</summary>
    internal static DurableTask<JsonDict, JsonDict, JsonDict> DefineTask(
        string name, int version, (string Name, TaskPhaseHandler Handler)[] phases, TaskPhaseHandler? abort = null,
        Action? migrate = null)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = name,
                Version = version,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = phases[0].Name },
                Phases = phases.ToDictionary(pair => pair.Name, pair => (object?)pair.Handler),
                Abort = abort,
                // 擦除迁移委托以 ErasedMigrate 形状承载；仅计数/抛出，不改变输入与 checkpoint。
                Migrate = migrate is null
                    ? null
                    : new ErasedMigrate((input, checkpoint, fromVersion) =>
                    {
                        migrate();
                        return (input, checkpoint);
                    }),
            },
        };

    /// <summary>
    /// 以单个 phase="run" 的裸定义创建任务、指定其存储版本（用于制造迁移场景）并返回 ID。
    /// 对应 TS <c>tx.createTask(task(name, version), null, …)</c>。
    /// </summary>
    internal static async Task<TaskId<JsonDict>> CreateRawTaskAsync(
        ITx tx, string name, int version, string phase, ConversationId conversationId)
    {
        var definition = DefineTask(name, version, [(phase, (_, _, _) => Task.CompletedTask)]);
        return await tx.CreateTaskAsync(definition, null, new TaskOptions
        {
            Ownership = new TaskOwnership.ConversationOwner(),
            ConversationId = conversationId,
        }).ConfigureAwait(false);
    }

    /// <summary>一个总是指向下一阶段的状态提交处理器。</summary>
    internal static TaskPhaseHandler AdvanceTo(string phase)
        => (task, runtime, context) => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = phase },
            }),
            context);

    /// <summary>一个完成任务的状态提交处理器。</summary>
    internal static TaskPhaseHandler CompleteWith(object? result)
        => (task, runtime, context) => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(Completed(result)),
            context);

    /// <summary>关闭 Harness，收尾不抛。</summary>
    internal static async Task CloseQuietlyAsync(IHarness harness)
    {
        try
        {
            await harness.CloseAsync(Ctx).ConfigureAwait(false);
        }
        catch
        {
            // 测试收尾不抛。
        }
    }
}
