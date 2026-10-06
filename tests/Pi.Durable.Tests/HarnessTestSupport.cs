using System.Runtime.CompilerServices;
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

    /// <summary>冲刷待定微任务与一个宏任务轮次。对应 TS <c>flush()</c>（<c>setTimeout(0)</c>）。</summary>
    internal static async Task FlushAsync()
    {
        await Task.Yield();
        await Task.Delay(1).ConfigureAwait(false);
    }

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

    /// <summary>
    /// 跨 close/reopen 幸存的 models 与 registry（如同宿主进程自己的对象）。
    /// 对应 TS <c>ChatSetup</c>（chat-support.ts）。
    /// </summary>
    internal sealed class ChatSetup
    {
        public required IProvider Provider { get; init; }

        /// <summary>夹具使用的模型 ID（<c>&lt;provider&gt;-1</c>）。对应 TS 的 faux 模型 <c>faux-1</c>。</summary>
        public required string ModelId { get; init; }

        public required Models Models { get; init; }

        public required IRegistry Registry { get; init; }

        public required List<object> Reports { get; init; }

        /// <summary>活的 Harness 设置；测试在决策之间赋值以改变它们（对应 TS <c>setup.settings</c> 原地改动）。</summary>
        public HarnessSettings Settings { get; set; } = new();

        /// <summary>宿主时钟；默认单调递增的固定值。</summary>
        public Func<long> Now { get; set; } = () => 1;
    }

    /// <summary>按给定的应答构造一个 chat 夹具。对应 TS <c>chatSetup()</c>。</summary>
    internal static ChatSetup NewChatSetup(AssistantMessage response, bool block = false)
        => NewChatSetup(new FakeProvider("faux", _ => response, block));

    internal static ChatSetup NewChatSetup(IProvider provider)
    {
        var models = new Models();
        models.SetProvider(provider);
        return new ChatSetup
        {
            Provider = provider,
            ModelId = provider.GetModels()[0].Id,
            Models = models,
            Registry = Registry.CreateRegistry(),
            Reports = [],
            Settings = new HarnessSettings
            {
                Extensions = [],
                Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            },
        };
    }
    /// <summary>在 <paramref name="storage"/> 上打开 Harness 并返回其 root。对应 TS <c>openChat()</c>。</summary>
    internal static async Task<(HarnessImpl Harness, IConversation Root)> OpenChatAsync(
        IStorage storage, ChatSetup setup)
    {
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = setup.Models,
            Registry = setup.Registry,
            SettingsSource = () => setup.Settings,
            Now = () => setup.Now(),
            OnReport = error => setup.Reports.Add(error),
        }, Ctx).ConfigureAwait(false);
        var root = await harness.RootAsync(
            Ctx, new AgentChange { Model = new ModelRef(setup.Provider.Id, setup.ModelId) })
            .ConfigureAwait(false);
        return (harness, root);
    }

    /// <summary>按脚本化 provider 构造一个 chat 夹具。对应 TS <c>chatSetup()</c>。</summary>
    internal static ChatSetup NewScriptedChatSetup(ScriptedProvider provider) => NewChatSetup(provider);

    /// <summary>一个对话的全部原始条目，最旧在前。对应 TS <c>allEntries()</c>。</summary>
    internal static async Task<IReadOnlyList<EntryRecord>> AllEntriesAsync(
        IConversation conversation, Context? context = null)
    {
        var page = await conversation.EntriesAsync(
            new EntryQuery { ConversationId = conversation.Id }, 1000, null, context ?? Ctx)
            .ConfigureAwait(false);
        return [.. page.Items.Reverse()];
    }

    /// <summary>等待 <paramref name="gate"/> 或取消（取消时抛出）。对应 TS <c>Promise.race([gate, aborted(signal)])</c>。</summary>
    internal static async Task RaceAsync(Task gate, CancellationToken signal)
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => aborted.TrySetCanceled());
        var completed = await Task.WhenAny(gate, aborted.Task).ConfigureAwait(false);
        if (completed == aborted.Task) signal.ThrowIfCancellationRequested();
        await gate.ConfigureAwait(false);
    }

    /// <summary>一个 assistant 应答消息（provider 为 <c>faux</c>）。对应 TS <c>fauxAssistantMessage(text)</c>。</summary>
    internal static AssistantMessage AssistantReply(string text) => AssistantReply("faux", text);

    internal static AssistantMessage AssistantReply(string providerId, string text)
        => new([new TextContent(text)])
        {
            StopReason = StopReason.Stop,
            Model = $"{providerId}-1",
            Provider = providerId,
            UsageStats = new Pi.Ai.Types.Usage(5, 7),
        };

    // ─── 脚本化响应（faux provider 队列） ──────────────────────────────────

    /// <summary>
    /// 一步脚本化响应：固定消息，或按请求上下文（可选等待取消信号）算出的工厂。对应 TS <c>FauxResponseStep</c>。
    /// </summary>
    internal sealed class FakeStep
    {
        private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AssistantMessage>> _resolve;

        private FakeStep(Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AssistantMessage>> resolve)
            => _resolve = resolve;

        public static FakeStep Of(AssistantMessage message) => new((_, _) => Task.FromResult(message));

        public static FakeStep Of(Func<IReadOnlyList<ChatMessage>, AssistantMessage> factory)
            => new((messages, _) => Task.FromResult(factory(messages)));

        public static FakeStep Of(Func<IReadOnlyList<ChatMessage>, Task<AssistantMessage>> factory)
            => new((messages, _) => factory(messages));

        /// <summary>被门闩持有直到释放或取消的一步；<paramref name="reached"/> 在请求发出时完成。</summary>
        public static (FakeStep Step, Task Reached, Action Release) Gated(AssistantMessage message)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var step = new FakeStep(async (_, signal) =>
            {
                reached.TrySetResult();
                await RaceAsync(gate.Task, signal).ConfigureAwait(false);
                return message;
            });
            return (step, reached.Task, () => gate.TrySetResult());
        }

        public Task<AssistantMessage> ResolveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken signal)
            => _resolve(messages, signal);
    }

    /// <summary>
    /// 一个脚本化 provider：按队列依次应答，记录调用次数。对应 TS <c>fauxProvider</c>
    /// （<c>setResponses</c> / <c>state.callCount</c>）。
    /// </summary>
    internal sealed class ScriptedProvider : IProvider
    {
        private readonly List<FakeStep> _steps = [];

        public ScriptedProvider(string id = "faux")
        {
            Id = id;
            ModelId = $"{id}-1";
            Models =
            [
                new ModelSpec
                {
                    Id = ModelId,
                    Name = "Faux Model",
                    Api = "faux",
                    Provider = id,
                    BaseUrl = "http://localhost:0",
                    ContextWindow = 128_000,
                    MaxTokens = 16_384,
                },
            ];
        }

        public int CallCount { get; private set; }

        public string Id { get; }

        public string ModelId { get; }

        public string Name => Id;

        public string? BaseUrl => "http://localhost:0";

        public IReadOnlyList<ModelSpec> Models { get; }

        public IReadOnlyList<ModelSpec> GetModels() => Models;

        /// <summary>替换响应队列。对应 TS <c>setResponses</c>。</summary>
        public void SetResponses(params FakeStep[] steps)
        {
            _steps.Clear();
            _steps.AddRange(steps);
        }

        public void SetResponses(params AssistantMessage[] messages)
            => SetResponses([.. messages.Select(FakeStep.Of)]);

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, JsonDict? options = null)
            => new StreamImpl(this, model, context);

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, JsonDict? options = null)
            => Stream(model, context, options);

        private sealed class StreamImpl(
            ScriptedProvider owner, ModelSpec model, IReadOnlyList<ChatMessage> context)
            : IAssistantMessageEventStream
        {
            public AssistantMessage? Partial { get; private set; }

            /// <summary>本次请求取用的脚本步骤：在流创建时一次性取用（对应 TS 的 <c>pendingResponses.shift()</c>）。</summary>
            private readonly Lazy<FakeStep?> _step = new(() =>
            {
                lock (owner._steps)
                {
                    owner.CallCount++;
                    if (owner._steps.Count == 0) return null;
                    var taken = owner._steps[0];
                    owner._steps.RemoveAt(0);
                    return taken;
                }
            }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            /// <summary>已解析的终态消息（多次枚举时复用，保证脚本步骤只解析一次）。</summary>
            private AssistantMessage? _resolved;

            public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
                => IterateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            public async Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default)
            {
                var enumerator = IterateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                AssistantMessage? result = null;
                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        if (enumerator.Current is AssistantMessageEvent.Done done)
                        {
                            result = done.Message;
                            break;
                        }
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                return result ?? throw new InvalidOperationException("Stream ended without a done event");
            }

            private async IAsyncEnumerable<AssistantMessageEvent> IterateAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var step = _step.Value;

                if (step is null)
                {
                    var missing = new AssistantMessage([])
                    {
                        StopReason = StopReason.Error,
                        ErrorMessage = "No more faux responses queued",
                        Model = model.Id,
                        Provider = model.Provider,
                        UsageStats = new Pi.Ai.Types.Usage(0, 0),
                    };
                    yield return new AssistantMessageEvent.Error(
                        StopReason.Error, missing.ErrorMessage ?? "error", missing);
                    yield break;
                }

                AssistantMessage final;
                if (_resolved is { } done)
                {
                    final = done;
                }
                else
                {
                    try
                    {
                        final = await step.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }

                    final = final with { Model = model.Id, Provider = model.Provider };
                    _resolved = final;
                }

                // faux 流：start → 文本增量 → done（按码元切分，与 TS 的字符切分对等）。
                var empty = final with { Content = final.Content.OfType<TextContent>().Any()
                    ? [new TextContent("")]
                    : final.Content };
                yield return new AssistantMessageEvent.Start(empty);

                var text = string.Concat(final.Content.OfType<TextContent>().Select(block => block.Text));
                var built = "";
                for (var index = 0; index < text.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    built += text[index];
                    var partial = final with { Content = [new TextContent(built)] };
                    Partial = partial;
                    yield return new AssistantMessageEvent.TextDelta(0, text[index].ToString(), index, partial);
                    await Task.Yield();
                }

                if (final.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    yield return new AssistantMessageEvent.Error(
                        final.StopReason, final.ErrorMessage ?? "error", final);
                    yield break;
                }

                Partial = final;
                yield return new AssistantMessageEvent.Done(final.StopReason, final);
            }
        }
    }

    // ─── 扩展安装辅助（harness-support.ts） ────────────────────────────────

    /// <summary>把一个只含指定工具的扩展装进注册表。对应 TS <c>addTool()</c>。</summary>
    internal static void AddTool(IRegistry registry, IToolRegistration tool, string? name = null)
        => registry.Install(new SimpleExtension(name ?? $"tool:{tool.Name}", tools: [tool]));

    /// <summary>把一个只含指定钩子的扩展装进注册表。对应 TS <c>addHooks()</c>。</summary>
    internal static void AddHooks(IRegistry registry, string taskName, object handlers, string? name = null)
        => registry.Install(new SimpleExtension(
            name ?? $"hooks:{taskName}:{Guid.NewGuid():N}", hooks: [new HookRegistration(taskName, handlers)]));

    /// <summary>一个只含工具 / 钩子 / 任务的极简扩展。</summary>
    internal sealed class SimpleExtension(
        string name,
        IReadOnlyList<IToolRegistration>? tools = null,
        IReadOnlyList<IPromptSection>? sections = null,
        IReadOnlyList<HookRegistration>? hooks = null,
        IReadOnlyList<AnyDurableTask>? tasks = null) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => tools;

        public IReadOnlyList<IPromptSection>? Sections => sections;

        public IReadOnlyList<HookRegistration>? Hooks => hooks;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    /// <summary>按名称、描述与执行函数构造一个无参数工具。对应 TS <c>defineTool</c> 的最小形状。</summary>
    internal sealed class ScriptedTool(
        string name, string description, Func<IToolExecutionApi, Context, Task<ToolExecutionResult>> execute)
        : IToolRegistration
    {
        public string Name => name;

        public string Description => description;

        public ToolSchema Parameters => new(new Dictionary<string, object?> { ["type"] = "object" });

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => execute(api, context);
    }

    /// <summary>由处理函数数组构造一个生成钩子。对应 TS <c>hook(GenerationTask, {onYield, …})</c>。</summary>
    internal sealed class GenerationHooksImpl : IGenerationHooks
    {
        public Func<AssistantMessage, (IReadOnlyList<ContentBlock> Continue, bool ContinueRun)?>? OnYieldHandler
        {
            get;
            init;
        }

        public Task<(IReadOnlyList<ChatMessage> Messages, bool Used)?> BeforeRequest(
            IReadOnlyList<ChatMessage> messages, IHookApi api, Context context)
            => Task.FromResult<(IReadOnlyList<ChatMessage>, bool)?>(null);

        public Task AfterResponse(AssistantMessage message, IHookApi api, Context context) => Task.CompletedTask;

        public Task<(IReadOnlyList<ContentBlock> Continue, bool ContinueRun)?> OnYield(
            AssistantMessage answer, IHookApi api, Context context)
            => Task.FromResult(OnYieldHandler?.Invoke(answer));

        public Task AfterTools(
            EntryId assistant, IReadOnlyList<EntryId> results, IHookApi api, Context context)
            => Task.CompletedTask;
    }

    /// <summary>由处理函数构造一个工具钩子。对应 TS <c>hook(ToolTask, {afterTool})</c>。</summary>
    internal sealed class ToolHooksImpl(
        Func<ToolCallContent, ToolExecutionResult, ToolExecutionResult?>? afterTool = null) : IToolHooks
    {
        public Task<ToolHookDecision?> BeforeTool(ToolCallContent call, IHookApi api, Context context)
            => Task.FromResult<ToolHookDecision?>(null);

        public Task<ToolExecutionResult?> AfterTool(
            ToolCallContent call, ToolExecutionResult result, IHookApi api, Context context)
            => Task.FromResult(afterTool?.Invoke(call, result));
    }
}

/// <summary>
/// 记录提交批次并对指定读取加门闩的存储装饰器。对应 TS <c>ControlledStorage</c>
/// （session-support.ts 的子集：commits 记录 + submission 读门闩）。
/// </summary>
internal sealed class ControlledHarnessStorage(IStorage inner) : IStorage
{
    /// <summary>已受理的提交批次（借用视图）。</summary>
    public readonly List<IReadOnlyList<StorageWrite>> AdmittedCommits = [];

    /// <summary>为值断言而解耦的提交批次。</summary>
    public readonly List<IReadOnlyList<StorageWrite>> Commits = [];

    private HarnessTestSupport.Deferred<Unit>? _submissionGate;
    private HarnessTestSupport.Deferred<Unit>? _commitGate;
    private HarnessTestSupport.Deferred<Unit>? _commitEntered;

    /// <summary>让下一次 <c>GetSubmissionAsync</c> 阻塞，直到释放；返回进入信号与释放动作。</summary>
    public (Task Entered, Action Release) HoldSubmissionReads()
    {
        var gate = new HarnessTestSupport.Deferred<Unit>();
        _submissionGate = gate;
        var entered = new HarnessTestSupport.Deferred<Unit>();
        EnteredSignal = entered;
        return (entered.Task, () => gate.Resolve(default));
    }

    /// <summary>让下一次 <c>CommitAsync</c> 阻塞，直到释放；返回进入信号与释放动作。对应 TS <c>holdCommits()</c>。</summary>
    public (Task Entered, Action Release) HoldCommits()
    {
        var gate = new HarnessTestSupport.Deferred<Unit>();
        _commitGate = gate;
        var entered = new HarnessTestSupport.Deferred<Unit>();
        _commitEntered = entered;
        return (entered.Task, () => gate.Resolve(default));
    }

    private HarnessTestSupport.Deferred<Unit>? EnteredSignal { get; set; }

    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
    {
        AdmittedCommits.Add(writes);
        Commits.Add([.. writes]);
        if (_commitGate is { } gate)
        {
            _commitGate = null;
            _commitEntered?.Resolve(default);
            await gate.Task.ConfigureAwait(false);
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

    public async Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
    {
        if (_submissionGate is { } gate)
        {
            _submissionGate = null;
            EnteredSignal?.Resolve(default);
            await gate.Task.ConfigureAwait(false);
        }

        return await inner.GetSubmissionAsync(id).ConfigureAwait(false);
    }

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

/// <summary>无载荷的占位值类型。</summary>
internal readonly record struct Unit;
