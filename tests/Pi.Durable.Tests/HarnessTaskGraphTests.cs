using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Testing;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// P57：任务图视图（harness-task-graph.test.ts）。
/// <para>覆盖 <see cref="TaskGraphView"/> 的挂载与推进：存活任务贯穿状态 / 属主边 / 自有对话、
/// 提交构建与 reopen 后 pending 化、abort 标记与精确帧、自有对话 ID 序、取消的获取不注册、
/// 无节点变更的提交不发布修订、以及在观察者间共享同一挂载。</para>
/// </summary>
public class HarnessTaskGraphTests
{
    private static readonly Context Ctx = Context.Background;

    /// <summary>图值的 JSON 形状（严格 JSON），进程内深比较用。</summary>
    private static string Json(object? value)
        => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>节点键（十进制 ID 字符串）→ 状态串。对应 TS <c>statuses()</c>。</summary>
    private static Dictionary<string, string> Statuses(JsonDict graph)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in TaskGraphReader.Nodes(graph))
        {
            var state = node.State switch
            {
                TaskGraphState.Active active => active.Status,
                TaskGraphState.WaitingOn => "waiting",
                TaskGraphState.Completing => "completing",
                _ => "?",
            };
            result[$"{node.Kind}#{node.Id.Value}"] = state;
        }

        return result;
    }

    private static TaskId<object?> Obj(TaskId<JsonDict> id) => TaskId<object?>.From(id.Value);

    private static void AssertDeepEqual(object? actual, object? expected)
        => ConformanceAssertions.Create().DeepEqual(actual, expected);

    // ─── 1. 贯穿状态 / 属主边 / 自有对话 ────────────────────────────────────

    [Fact]
    public async Task FollowsEveryLiveTaskThroughItsStatusesOwnerEdgesAndOwnedConversations()
    {
        var child = HarnessTestSupport.NewDeferred<Unit>();
        var late = HarnessTestSupport.NewDeferred<Unit>();
        var (parent, childTask) = Family(child.Task, late.Task);
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(
            new MemoryStorage(), [AnyDurableTask.From(parent), AnyDurableTask.From(childTask)]);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var seen = new List<JsonDict>();
            async Task<AttachedReplicatedState<JsonDict>> ObserveAsync()
            {
                var opened = await harness.TaskGraphAsync(Ctx);
                opened.Subscribe((value, _, delivery) =>
                {
                    if (delivery.Kind == ReplicatedStateDeliveryKind.Update) seen.Add(value);
                    return null;
                });
                return opened;
            }

            var graph = await ObserveAsync();
            AssertDeepEqual(graph.Value, new Dictionary<string, object?> { ["tasks"] = new Dictionary<string, object?>() });

            // 最后一个观察者离开后，推进值等于从 Storage 新建的值。
            async Task RebuildAsync()
            {
                var advanced = graph.Value;
                graph.Dispose();
                graph = await ObserveAsync();
                Assert.NotSame(advanced, graph.Value);
                AssertDeepEqual(graph.Value, advanced);
            }

            var parentId = await root.CommitAsync(
                tx => tx.CreateTaskAsync(parent, null, new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = root.Id,
                }), Ctx);
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value!, parentId.Value.ToString()) is not null);

            var parentNode = TaskGraphReader.Node(graph.Value!, parentId.Value.ToString())!;
            Assert.Equal(parentId.Value, parentNode.Id.Value);
            Assert.Equal("test.graph-parent", parentNode.Kind);
            Assert.Equal(root.Id, parentNode.ConversationId);
            Assert.False(parentNode.Background);
            Assert.False(parentNode.AbortRequested);
            Assert.Equal(new TaskGraphState.Active("pending", "spawn"), parentNode.State);
            Assert.Empty(parentNode.Conversations);

            harness.Resume();
            string ChildId()
            {
                var node = TaskGraphReader.Nodes(graph.Value!)
                    .FirstOrDefault(entry => entry.Kind == "test.graph-child");
                return node?.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }

            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Count(graph.Value!) == 2 && ChildId() != "",
                timeoutMs: 5000);

            var first = TaskId<JsonDict>.From(Convert.ToInt64(ChildId()));
            var parentLive = TaskGraphReader.Node(graph.Value!, parentId.Value.ToString())!;
            var waiting = Assert.IsType<TaskGraphState.WaitingOn>(parentLive.State);
            Assert.Equal("join", waiting.Phase);
            Assert.Equal([first.Value], waiting.On.Select(id => id.Value).ToArray());
            Assert.Equal(JoinPolicy.AllSettled, waiting.Policy);
            Assert.Equal(2, parentLive.Conversations.Count);
            Assert.Equal(
                parentLive.Conversations.OrderBy(id => id.Value).ToArray(),
                parentLive.Conversations.ToArray());
            var owned = parentLive.Conversations[0];
            var childNode = TaskGraphReader.Node(graph.Value!, first.Value.ToString())!;
            Assert.Equal(parentId.Value, childNode.Owner!.Value.Value);
            Assert.Equal(root.Id, childNode.ConversationId);
            Assert.NotNull(await harness.ConversationAsync(owned, Ctx));
            await RebuildAsync();

            child.Resolve(default);
            // 父任务在 late 子任务存活时完成：其结局被保留。
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value!, parentId.Value.ToString())?.State is TaskGraphState.Completing,
                timeoutMs: 5000);
            var completing = TaskGraphReader.Node(graph.Value!, parentId.Value.ToString())!;
            Assert.Equal(new TaskGraphState.Completing("completed"), completing.State);
            Assert.Null(TaskGraphReader.Node(graph.Value!, first.Value.ToString()));
            Assert.Equal(parentLive.Conversations, completing.Conversations);
            await RebuildAsync();

            late.Resolve(default);
            await harness.WaitForTaskAsync(Obj(parentId), Ctx);
            await HarnessTestSupport.WaitForAsync(() => TaskGraphReader.Count(graph.Value!) == 0);
            AssertDeepEqual(graph.Value, new Dictionary<string, object?> { ["tasks"] = new Dictionary<string, object?>() });
            // 每个修订都是一次改变节点的提交；没有重复其前驱。
            for (var index = 1; index < seen.Count; index++)
            {
                Assert.NotEqual(Json(seen[index]), Json(seen[index - 1]));
            }

            // 创建两个对话的提交发布了一个把它们设上的修订。
            Assert.Contains(seen, value =>
                TaskGraphReader.Node(value, parentId.Value.ToString())?.Conversations.Count == 2);
            graph.Dispose();
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 2. 由已提交任务构建；reopen 后 pending；abort 标记与精确帧 ──────────

    [Fact]
    public async Task BuildsFromCommittedTasksShowsSurvivingTasksAsPendingAfterReopenAndMarksAborts()
    {
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        var work = WorkTask(gate.Task);
        var firstStorage = new MemoryStorage();
        var (firstHarness, _, _) = await HarnessTestSupport.OpenTasksAsync(firstStorage, [AnyDurableTask.From(work)]);

        var firstRoot = await firstHarness.RootAsync(Ctx);
        var created = await firstRoot.CommitAsync(async tx =>
        {
            var foreground = await tx.CreateTaskAsync(work, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = firstRoot.Id,
            });
            var background = await tx.CreateTaskAsync(work, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = firstRoot.Id,
                Background = true,
            });
            var owned = await tx.CreateConversationAsync(new ConversationOwnership.TaskOwned(Obj(foreground)));
            return (Foreground: foreground, Background: background, Owned: owned.Id);
        }, Ctx);

        firstHarness.Resume();
        await HarnessTestSupport.WaitForAsync(async () =>
        {
            var inspection = await firstHarness.InspectAsync(Ctx);
            return inspection.Tasks.All(task => task is TaskInspection.Running);
        });
        var running = await firstHarness.TaskGraphAsync(Ctx);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"test.graph-work#{created.Foreground.Value}"] = "running",
                [$"test.graph-work#{created.Background.Value}"] = "running",
            },
            Statuses(running.Value!));
        running.Dispose();
        await firstHarness.CloseAsync(Ctx);

        // reopen 后再获取：由已提交记录与属主边构建；open 把 running 核回 pending。
        var secondStorage = firstStorage.Reopen();
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(secondStorage, [AnyDurableTask.From(work)]);
        try
        {
            var watch = await harness.WatchTaskGraphAsync(Ctx);
            Assert.Equal(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$"test.graph-work#{created.Foreground.Value}"] = "pending",
                    [$"test.graph-work#{created.Background.Value}"] = "pending",
                },
                Statuses(watch.Value));
            var fgNode = TaskGraphReader.Node(watch.Value, created.Foreground.Value.ToString())!;
            Assert.Equal([created.Owned], fgNode.Conversations.ToArray());
            Assert.True(TaskGraphReader.Node(watch.Value, created.Background.Value.ToString())!.Background);

            // 精确帧：从获取修订起重放 ops 得到每个投递值。
            var replica = (object?)watch.Value;
            var frames = new List<IReadOnlyList<DeltaOp>>();
            watch.Start((value, ops, _) =>
            {
                replica = DeltaApply.Apply(ops, replica);
                AssertDeepEqual(replica, value);
                frames.Add(ops);
                return Task.CompletedTask;
            });
            harness.Resume();
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(watch.Value, created.Background.Value.ToString())?.State
                    is TaskGraphState.Active { Status: "running" });
            Assert.Equal("marked", await harness.AbortTaskAsync(Obj(created.Background), Ctx));
            await harness.WaitForTaskAsync(Obj(created.Background), Ctx);
            await HarnessTestSupport.WaitForAsync(() =>
                TaskGraphReader.Count((JsonDict)replica!) == 1 && frames.Count >= 3);
            // 有一帧把 abortRequested 置上，最后一帧删除该节点。
            Assert.Contains(frames, ops => ops.Any(op => op is DeltaOp.Set set
                && set.Path.ToString().Contains(created.Background.Value.ToString(), StringComparison.Ordinal)
                && set.Value is JsonDict { } node
                && node.TryGetValue("abortRequested", out var flag)
                && Convert.ToBoolean(flag)));
            Assert.Contains(frames, ops => ops.Any(op => op is DeltaOp.Delete delete
                && delete.Path.ToString().Contains(created.Background.Value.ToString(), StringComparison.Ordinal)));
            Assert.Equal(
                [created.Foreground.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                TaskGraphReader.Nodes((JsonDict)replica!).Select(node => node.Id.Value.ToString()).ToArray());
            await watch.Stop();
            gate.Resolve(default);
            await harness.WaitForTaskAsync(Obj(created.Foreground), Ctx);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 3. 自有对话按 ID 序列出 ────────────────────────────────────────────

    [Fact]
    public async Task ListsOwnedConversationsInIdOrderWhateverOrderOneCommitCreatesThemIn()
    {
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        var created = new List<long>();
        var spawner = SpawnerTask(gate.Task, created);
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(
            new MemoryStorage(), [AnyDurableTask.From(spawner)]);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var graph = await harness.TaskGraphAsync(Ctx);
            var id = await root.CommitAsync(async tx =>
            {
                var entry = await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" });
                return await tx.CreateTaskAsync(spawner, new Dictionary<string, object?> { ["at"] = entry.Id.Value },
                    new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = root.Id });
            }, Ctx);

            harness.Resume();
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value!, id.Value.ToString())?.Conversations.Count == 2);
            var advanced = graph.Value!;
            var node = TaskGraphReader.Node(advanced, id.Value.ToString())!;
            Assert.Equal(created.OrderBy(value => value).ToArray(), node.Conversations.Select(c => c.Value).ToArray());
            graph.Dispose();
            var rebuilt = await harness.TaskGraphAsync(Ctx);
            AssertDeepEqual(rebuilt.Value, advanced);
            rebuilt.Dispose();
            gate.Resolve(default);
            await harness.WaitForTaskAsync(Obj(id), Ctx);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 4. 在线路上等待时被取消的获取不注册任何东西 ────────────────────────

    [Fact]
    public async Task RegistersNothingForAnAcquisitionCancelledWhileItWaitsForTheLine()
    {
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(storage, []);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var never = GatesNever();
            await root.CommitAsync(
                tx => tx.CreateTaskAsync(Family(never.Child, never.Late).ChildTask,
                    new Dictionary<string, object?> { ["late"] = false },
                    new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = root.Id }),
                Ctx);

            // 让获取在线路上等待：持有一个提交，再启动 watch。
            var (entered, release) = storage.HoldCommits();
            var blocking = harness.CommitWithAsync(
                async tx =>
                {
                    await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "blocker" });
                    return true;
                }, Ctx);
            await entered;

            using var source = new CancellationTokenSource();
            var cancelled = harness.WatchTaskGraphAsync(
                ContextSignals.WithAbortSignal(source.Token, Ctx));
            source.Cancel();
            release();
            await blocking;
            await Assert.ThrowsAnyAsync<Exception>(() => cancelled);

            // 没有观察者保留挂载：每个新观察者构建新修订。
            var first = await harness.TaskGraphAsync(Ctx);
            var value = first.Value;
            first.Dispose();
            var second = await harness.TaskGraphAsync(Ctx);
            Assert.NotSame(value, second.Value);
            AssertDeepEqual(second.Value, value);
            second.Dispose();
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 5. 无节点变更的提交不发布修订；观察者共享一个挂载 ──────────────────

    [Fact]
    public async Task PublishesNoRevisionForACommitThatChangesNoNodeAndSharesOneMountBetweenObservers()
    {
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        var reached = HarnessTestSupport.NewDeferred<Unit>();
        var memo = MemoTask(gate.Task, reached);
        var (harness, _, _) = await HarnessTestSupport.OpenTasksAsync(
            new MemoryStorage(), [AnyDurableTask.From(memo)]);
        try
        {
            var root = await harness.RootAsync(Ctx);
            var id = await root.CommitAsync(
                tx => tx.CreateTaskAsync(memo, null, new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = root.Id,
                }), Ctx);

            var first = await harness.TaskGraphAsync(Ctx);
            var second = await harness.TaskGraphAsync(Ctx);
            Assert.Same(first.Value, second.Value);

            var updates = new List<string>();
            first.Subscribe((value, _, delivery) =>
            {
                if (delivery.Kind == ReplicatedStateDeliveryKind.Update)
                {
                    updates.Add(TaskGraphReader.Node(value, id.Value.ToString()) is { } node
                        ? StatusOf(node)
                        : "gone");
                }

                return null;
            });

            harness.Resume();
            await reached.Task;
            await HarnessTestSupport.WaitForAsync(() => updates.Count == 1);
            // 保留改变了节点；memo 提交没有。
            Assert.Equal(["running"], updates);

            gate.Resolve(default);
            await harness.WaitForTaskAsync(Obj(id), Ctx);
            await HarnessTestSupport.WaitForAsync(() => updates.Count == 2);
            Assert.Equal(["running", "gone"], updates);
            var last = first.Value!;
            first.Dispose();
            second.Dispose();
            // 没有观察者留下，挂载被丢弃：新观察者构建新修订。
            var rebuilt = await harness.TaskGraphAsync(Ctx);
            AssertDeepEqual(rebuilt.Value, last);
            Assert.NotSame(last, rebuilt.Value);
            rebuilt.Dispose();
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static string StatusOf(TaskGraphNode node) => node.State switch
    {
        TaskGraphState.Active active => active.Status,
        TaskGraphState.WaitingOn => "waiting",
        TaskGraphState.Completing => "completing",
        _ => "?",
    };

    // ─── 任务定义 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 一个父任务：创建子任务与它拥有的对话，等待子任务，创建第二个子任务，并在后者存活时完成，
    /// 使其结局被保留为 completing。
    /// </summary>
    private static (DurableTask<JsonDict, JsonDict, JsonDict> Parent, DurableTask<JsonDict, JsonDict, JsonDict> ChildTask)
        Family(Task childGate, Task lateGate)
    {
        var child = new DurableTask<JsonDict, JsonDict, JsonDict>
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "test.graph-child",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "work" },
                Phases = new Dictionary<string, object?>
                {
                    ["work"] = (TaskPhaseHandler)((task, runtime, context) =>
                        RunChildAsync(task, runtime, context, childGate, lateGate)),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };
        var parent = new DurableTask<JsonDict, JsonDict, JsonDict>
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "test.graph-parent",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "spawn" },
                Phases = new Dictionary<string, object?>
                {
                    ["spawn"] = (TaskPhaseHandler)((task, runtime, context) => SpawnAsync(task, runtime, context, child)),
                    ["join"] = (TaskPhaseHandler)((task, runtime, context) => JoinAsync(task, runtime, context, child)),
                    ["finish"] = (TaskPhaseHandler)((_, runtime, context) =>
                        runtime.CommitAsync((_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context)),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };
        return (parent, child);
    }

    private static bool LateOf(JsonDict input)
        => input.TryGetValue("late", out var value) && Convert.ToBoolean(value);

    private static async Task RunChildAsync(
        TaskRecord task, ITaskRuntime runtime, Context context, Task childGate, Task lateGate)
    {
        await (LateOf(task.Input as JsonDict ?? new Dictionary<string, object?>()) ? lateGate : childGate);
        await runtime.CommitAsync((_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
    }

    private static async Task SpawnAsync(
        TaskRecord task, ITaskRuntime runtime, Context context, DurableTask<JsonDict, JsonDict, JsonDict> child)
    {
        var ownership = new ConversationOwnership.TaskOwned(task.Id);
        // 一次提交里创建两个对话，且不改变父任务记录。
        await runtime.CommitAsync(async (tx, _) =>
        {
            await tx.CreateConversationAsync(ownership);
            await tx.CreateConversationAsync(ownership);
            return null;
        }, context);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var created = await tx.CreateTaskAsync(child,
                new Dictionary<string, object?> { ["late"] = false },
                new TaskOptions { Ownership = new TaskOwnership.TaskOwner(task.Id), ConversationId = task.ConversationId });
            return new TaskState
            {
                Status = TaskStatus.Waiting,
                Checkpoint = new Dictionary<string, object?>
                {
                    ["phase"] = "join",
                    ["child"] = created.Value,
                },
                On = [TaskId<object?>.From(created.Value)],
                Policy = JoinPolicy.AllSettled,
            };
        }, context);
    }

    private static Task JoinAsync(
        TaskRecord task, ITaskRuntime runtime, Context context, DurableTask<JsonDict, JsonDict, JsonDict> child)
        => runtime.CommitAsync(async (tx, _) =>
        {
            await tx.CreateTaskAsync(child,
                new Dictionary<string, object?> { ["late"] = true },
                new TaskOptions { Ownership = new TaskOwnership.TaskOwner(task.Id), ConversationId = task.ConversationId });
            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "finish" },
            };
        }, context);

    /// <summary>永不释放的子 / late 门闩。对应 TS <c>gatesNever()</c>。</summary>
    private static (Task Child, Task Late) GatesNever()
    {
        var never = new TaskCompletionSource();
        return (never.Task, never.Task);
    }

    /// <summary>等待门闩或中止；abort 阶段提交 aborted 终态。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> WorkTask(Task gate)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "test.graph-work",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "work" },
                Phases = new Dictionary<string, object?>
                {
                    ["work"] = (TaskPhaseHandler)(async (_, runtime, context) =>
                    {
                        await HarnessTestSupport.RaceAsync(gate, runtime.Signal);
                        await runtime.CommitAsync(
                            (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
                    }),
                },
                Abort = (TaskPhaseHandler)((_, runtime, context) => runtime.CommitAsync(
                    (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.AbortedWith("test")), context)),
            },
        };

    /// <summary>创建自有对话（fork + 新建）后等待门闩并完成。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> SpawnerTask(Task gate, List<long> created)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "test.graph-spawner",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "spawn" },
                Phases = new Dictionary<string, object?>
                {
                    ["spawn"] = (TaskPhaseHandler)(async (task, runtime, context) =>
                    {
                        var ownership = new ConversationOwnership.TaskOwned(task.Id);
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            var at = EntryId.From(Convert.ToInt64(Intersect(task.Input, "at")));
                            var forked = await tx.ForkConversationAsync(task.ConversationId, at, ownership);
                            var fresh = await tx.CreateConversationAsync(ownership);
                            created.Add(forked.Id.Value);
                            created.Add(fresh.Id.Value);
                            return null;
                        }, context);
                        await gate;
                        await runtime.CommitAsync(
                            (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
                    }),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };

    /// <summary>提交一个 memo 后等待门闩并完成。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> MemoTask(Task gate, HarnessTestSupport.Deferred<Unit> reached)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "test.graph-memo",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "work" },
                Phases = new Dictionary<string, object?>
                {
                    ["work"] = (TaskPhaseHandler)(async (_, runtime, context) =>
                    {
                        await runtime.MemoAsync("seen", true, context);
                        reached.Resolve(default);
                        await gate;
                        await runtime.CommitAsync(
                            (_, _) => Task.FromResult<TaskState?>(HarnessTestSupport.Completed(null)), context);
                    }),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };

    private static object? Intersect(object? input, string key)
        => input is JsonDict dict && dict.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"missing {key}");
}
