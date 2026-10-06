using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;
using Path = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Tests;

/// <summary>P54：注册表（registry.ts）与内建工具任务（tool.ts）。</summary>
public class RegistryTests
{
    private static AnyDurableTask TaskDef(string name) => new()
    {
        Name = name,
        Version = 1,
        Initial = _ => new Dictionary<string, object?>(),
        Phases = new Dictionary<string, object?>(),
    };

    private sealed class FakeExtension(string name, IToolRegistration[]? tools = null,
        IPromptSection[]? sections = null, AnyDurableTask[]? tasks = null) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => tools;

        public IReadOnlyList<IPromptSection>? Sections => sections;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    private sealed class FakeSection(string key) : IPromptSection
    {
        public string Key => key;

        public Task<string?> RenderAsync(PromptInput input, Context context) => Task.FromResult<string?>(string.Empty);

        public bool? Tag => null;
    }

    private sealed class SimpleFakeTool(string name) : IToolRegistration
    {
        public string Name => name;
        public string Description => "fake";
        public ToolSchema Parameters => new(new Dictionary<string, object?> { ["type"] = "object" });
        public string? Replay => null;
        public ToolExecutionMode? ExecutionMode => null;
        public ToolOutputLimits? OutputLimits => null;
        public object? PrepareArguments(object args) => args;
        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => Task.FromResult(new ToolExecutionResult());
    }

    private static IToolRegistration Tool(string name) => new SimpleFakeTool(name);

    [Fact]
    public void BuiltinTasks_ContainToolTask()
    {
        Assert.Contains(Registry.BuiltinTasks, task => task.Name == "pi.tool");
    }

    [Fact]
    public void Snapshot_StartsWithBuiltinTasksOnly()
    {
        var snapshot = Registry.CreateRegistry().Snapshot();
        Assert.Empty(snapshot.Installed());
        Assert.Empty(snapshot.Tools());
        Assert.Empty(snapshot.Sections());
        // BUILTIN_TASKS 顺序与 TS 一致：Generation、Tool、Compaction。
        Assert.Equal(3, snapshot.Tasks().Count);
        Assert.Equal(["pi.generation", "pi.tool", "pi.compaction"], snapshot.Tasks().Select(task => task.Name));
        Assert.NotNull(snapshot.Task("pi.tool"));
        Assert.NotNull(snapshot.Task("pi.generation"));
        Assert.NotNull(snapshot.Task("pi.compaction"));
    }

    [Fact]
    public void Install_ReplaceAndUninstall_Publish()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", tools: [Tool("read")], sections: [new FakeSection("notes")],
            tasks: [TaskDef("ext.task")]);
        var published = new List<int>();
        using var subscription = registry.Subscribe(() => published.Add(published.Count));

        registry.Install(extension);
        var snapshot = registry.Snapshot();
        Assert.Single(snapshot.Installed());
        Assert.Equal([("demo", "read")], snapshot.Tools().Select(pair => (pair.Extension.Name, pair.Tool.Name)).ToList());
        Assert.Equal([("demo", "notes")], snapshot.Sections().Select(pair => (pair.Extension.Name, pair.Section.Key)).ToList());
        Assert.NotNull(snapshot.Task("ext.task"));
        Assert.NotNull(snapshot.Task("pi.tool"));
        Assert.Single(published);

        // 同名安装就地替换。
        var replacement = new FakeExtension("demo", tools: [Tool("write")]);
        registry.Install(replacement);
        Assert.Equal(["write"], registry.Snapshot().Tools().Select(pair => pair.Tool.Name).ToList());
        // 替换的扩展不再带 ext.task。
        Assert.Null(registry.Snapshot().Task("ext.task"));
        Assert.Equal(2, published.Count);

        // 卸载后内建任务仍在。
        registry.Uninstall(replacement);
        Assert.Empty(registry.Snapshot().Installed());
        Assert.NotNull(registry.Snapshot().Task("pi.tool"));
        Assert.Equal(3, published.Count);

        // 卸载不存在的扩展不发布。
        registry.Uninstall(replacement);
        Assert.Equal(3, published.Count);
    }

    [Fact]
    public void Install_TaskNameCollision_Throws()
    {
        var registry = Registry.CreateRegistry();
        var first = new FakeExtension("a", tasks: [TaskDef("ext.task")]);
        var second = new FakeExtension("b", tasks: [TaskDef("ext.task")]);
        registry.Install(first);
        Assert.Throws<InvalidOperationException>(() => registry.Install(second));
    }

    [Fact]
    public void Install_ExtensionTaskCollidesWithBuiltin_Throws()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", tasks: [TaskDef("pi.tool")]);
        Assert.Throws<InvalidOperationException>(() => registry.Install(extension));
    }

    [Fact]
    public void Install_DuplicateToolNames_Throws()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", tools: [Tool("read"), Tool("read")]);
        Assert.Throws<InvalidOperationException>(() => registry.Install(extension));
    }

    [Fact]
    public void Install_BadSectionKey_Throws()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", sections: [new FakeSection("Bad Key")]);
        Assert.Throws<ArgumentException>(() => registry.Install(extension));
    }

    [Fact]
    public void Install_ReservedInstructionsKey_Throws()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", sections: [new FakeSection("instructions")]);
        Assert.Throws<InvalidOperationException>(() => registry.Install(extension));
    }

    [Fact]
    public void Install_DuplicateSectionKeys_Throws()
    {
        var registry = Registry.CreateRegistry();
        var extension = new FakeExtension("demo", sections: [new FakeSection("notes"), new FakeSection("notes")]);
        Assert.Throws<InvalidOperationException>(() => registry.Install(extension));
    }
}

/// <summary>P54：pi.tool 任务（tool.ts）的阶段流程与结果条目。</summary>
public class ToolTaskTests
{
    private static Context Ctx => Context.Background;

    // ─── 夹具 ───────────────────────────────────────────────────────────────

    private sealed class FakeTool : IToolRegistration
    {
        private readonly Func<object?, IToolExecutionApi, Context, Task<ToolExecutionResult>>? _execute;

        public FakeTool(string name, Dictionary<string, object?> schema, Dictionary<string, object?>? callArgs = null,
            string? replay = null,
            Func<object?, IToolExecutionApi, Context, Task<ToolExecutionResult>>? execute = null)
        {
            Name = name;
            Schema = schema;
            CallArgs = callArgs ?? [];
            Replay = replay;
            _execute = execute;
        }

        public Dictionary<string, object?> Schema { get; }

        public Dictionary<string, object?> CallArgs { get; }

        public List<object?> ExecutedArgs { get; } = [];

        public string Name { get; }

        public string Description => "fake";

        public ToolSchema Parameters => new(Schema);

        public string? Replay { get; }

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            ExecutedArgs.Add(args);
            if (_execute is not null) return _execute(args, api, context);
            return Task.FromResult(new ToolExecutionResult { Content = [new TextContent("done")] });
        }
    }

    private sealed class FakeHooks : IToolHooks
    {
        public Func<ToolCallContent, IHookApi, Context, Task<ToolHookDecision?>>? BeforeToolHandler { get; set; }

        public Task<ToolHookDecision?> BeforeTool(ToolCallContent call, IHookApi api, Context context)
            => BeforeToolHandler is { } handler ? handler(call, api, context) : Task.FromResult<ToolHookDecision?>(null);

        public Task<ToolExecutionResult?> AfterTool(
            ToolCallContent call, ToolExecutionResult result, IHookApi api, Context context)
            => Task.FromResult<ToolExecutionResult?>(null);
    }

    private sealed class FakeRuntime : ITaskRuntime, IHookRunner
    {
        private readonly DurableSession _session;

        public FakeRuntime(DurableSession session, ConversationId conversationId, TaskId<object?> taskId,
            Agent agent, IToolHooks[] hooks)
        {
            _session = session;
            ConversationId = conversationId;
            TaskId = taskId;
            Agent = agent;
            Registry = Pi.Durable.Harness.Registry.CreateRegistry().Snapshot();
            Settings = AgentDocs.ResolveSettings(null);
            HookHandlers = hooks;
            Reported = [];
        }

        public Agent Agent { get; }

        public List<Exception> Reported { get; }

        private IToolHooks[] HookHandlers { get; }

        public TaskId<object?> TaskId { get; }

        public ConversationId ConversationId { get; }

        public CancellationToken Signal { get; set; } = CancellationToken.None;

        public IRegistrySnapshot Registry { get; }

        public Settings Settings { get; }

        public Pi.Ai.Models.Models Models => throw new NotSupportedException();

        public IHookRunner Hooks => this;

        public Task EachAsync(string name, Func<object?, Task> invoke)
        {
            var tasks = new List<Task>();
            foreach (var handler in HookHandlers) tasks.Add(invoke(handler));
            return Task.WhenAll(tasks);
        }

        public Task<Agent> AgentAsync(Context context) => Task.FromResult(Agent);

        public Task<IExecutionEnv?> EnvAsync(Context context) => Task.FromResult<IExecutionEnv?>(null);

        public Task CommitAsync(Func<Transaction, TaskRecord, Task<TaskState?>> change, Context context)
            => _session.CommitWithAsync(async tx =>
            {
                var current = await tx.GetTaskAsync(TaskId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Task {TaskId.Value} is missing");
                var next = await change(tx, current).ConfigureAwait(false);
                if (next is not null) tx.SetTask(current with { State = next });
                return next;
            }, context);

        public Task<object?> MemoAsync(string name, Context context) => Task.FromResult<object?>(null);

        public Task<object> MemoAsync(string name, object candidate, Context context) => Task.FromResult(candidate);

        public Task<TaskId<object?>> CreateTaskAsync(
            AnyDurableTask task, object? input, TaskOptions options, Context context)
            => throw new NotSupportedException();

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
            => _session.CommitAsync(tx => tx.GetTaskAsync(id), context);

        public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IReadOnlyList<TaskId<object?>> ids, Context context)
            => throw new NotSupportedException();

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context)
            => throw new NotSupportedException();

        public Task<EntryRecord?> EntryAsync(EntryId id, Context context)
            => _session.CommitAsync(tx => tx.GetEntryAsync(id), context);

        public Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, Context context)
            => _session.CommitAsync(tx => tx.GetEntryAsync(token, id), context);

        public Task<ContextView> ContextAsync(ConversationId conversationId, Context context, EntryId? at = null)
            => throw new NotSupportedException();

        public long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public void Report(Exception error) => Reported.Add(error);

        public Task SleepAsync(long until, Context context) => throw new NotSupportedException();

        // 文档读取 / 观察面直接转发到会话。
        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, conversationId, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
            DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, taskId, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, key, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, conversationId, key, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsync(token, taskId, key, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, conversationId, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, taskId, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, key, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, conversationId, key, context);

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.DocumentStateAsync(token, taskId, key, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
            DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsOfAsync(token, conversationId, at, context);

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.SnapshotAsOfAsync(token, conversationId, key, at, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, conversationId, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, taskId, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, key, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, conversationId, key, context);

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => _session.WatchDocAsync(token, taskId, key, context);
    }

    private sealed record Fixture(
        DurableSession Session,
        ConversationId Conv,
        TaskId<object?> TaskId,
        FakeTool Tool,
        FakeRuntime Runtime);

    private static async Task<Fixture> CreateFixtureAsync(
        FakeTool tool, IToolHooks[]? hooks = null, bool withSlot = true)
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless()).ConfigureAwait(false))
                .Id,
            Ctx);

        var assistantId = await session.CommitAsync(async tx => (await tx.AppendEntryAsync(conv, new EntryDraft
        {
            Kind = DurableEntries.AssistantEntry.Kind,
            Model = [new AssistantMessage([new ToolCallContent("call1", tool.Name, tool.CallArgs)])],
        }).ConfigureAwait(false)).Id, Ctx);

        var taskId = await session.CommitAsync(async tx => await tx.CreateTaskAsync(
            ToolTask.Instance,
            new ToolTaskInput(assistantId, "call1").ToJson(),
            new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = conv,
            }).ConfigureAwait(false), Ctx);

        if (withSlot)
        {
            await session.CommitAsync(async tx =>
            {
                var change = await tx.DocAsync(Live.LiveDoc, conv).ConfigureAwait(false);
                change.Set(Path.Root.Append(Seg.Key("tools")), new List<object?>
                {
                    new ToolSlot
                    {
                        CallId = "call1",
                        Name = tool.Name,
                        TaskId = TaskId<object?>.From(taskId.Value),
                        Status = "pending",
                    }.ToJson(),
                });
                return 0;
            }, Ctx);
        }

        var agent = new Agent
        {
            ThinkingLevel = ThinkingLevel.Off,
            Extensions = [],
            Tools = [tool],
            Sections = [],
        };
        var runtime = new FakeRuntime(session, conv, TaskId<object?>.From(taskId.Value), agent, hooks ?? []);
        return new Fixture(session, conv, TaskId<object?>.From(taskId.Value), tool, runtime);
    }

    private static TaskPhaseHandler CallPhase => (TaskPhaseHandler)ToolTask.Instance.Definition.Phases!["call"]!;

    private static TaskPhaseHandler ExecutePhase => (TaskPhaseHandler)ToolTask.Instance.Definition.Phases!["execute"]!;

    private static TaskPhaseHandler AbortPhase => (TaskPhaseHandler)ToolTask.Instance.Definition.Abort!;

    private static async Task<TaskRecord> GetTaskAsync(Fixture fixture)
        => (await fixture.Session.CommitAsync(tx => tx.GetTaskAsync(fixture.TaskId), Ctx).ConfigureAwait(false))!;

    private static async Task<IReadOnlyList<object?>> GetToolsAsync(Fixture fixture)
    {
        var live = await fixture.Session.SnapshotAsync(Live.LiveDoc, fixture.Conv, Ctx).ConfigureAwait(false);
        return live is not null && live.TryGetValue("tools", out var value) && value is IReadOnlyList<object?> list
            ? list
            : [];
    }

    private static ToolSlot SlotOf(IReadOnlyList<object?> tools)
        => ToolSlot.FromJson((IReadOnlyDictionary<string, object?>)tools[0]!);

    private static async Task<TypedEntry<IReadOnlyDictionary<string, object?>>> ResultEntryAsync(Fixture fixture, TaskRecord record)
    {
        var entryId = Convert.ToInt64(
            Assert.IsType<Dictionary<string, object?>>(record.State.Outcome!.Result)["entryId"]);
        return (await fixture.Session.CommitAsync(
            tx => tx.GetEntryAsync(DurableEntries.ToolResultEntry, EntryId.From(entryId)), Ctx).ConfigureAwait(false))!;
    }

    private static ToolDiagnostic FirstDiagnostic(TypedEntry<IReadOnlyDictionary<string, object?>> entry)
        => DurableEntries.ToolResultDiagnostics(entry.Data!)[0];

    // ─── JSON 形状 ──────────────────────────────────────────────────────────

    [Fact]
    public void Checkpoint_JsonRoundTrip()
    {
        var call = (ToolTaskCheckpoint)new ToolTaskCheckpoint.Call();
        Assert.Equal(new ToolTaskCheckpoint.Call(), ToolTaskCheckpoint.FromJson(call.ToJson()));

        var execute = new ToolTaskCheckpoint.Execute(
            new Dictionary<string, object?> { ["path"] = "a.txt" }, "safe");
        var restored = Assert.IsType<ToolTaskCheckpoint.Execute>(ToolTaskCheckpoint.FromJson(execute.ToJson()));
        Assert.Equal("a.txt", Assert.IsType<Dictionary<string, object?>>(restored.Arguments)["path"]);
        Assert.Equal("safe", restored.Replay);
    }

    [Fact]
    public void Input_JsonRoundTrip()
    {
        var input = new ToolTaskInput(EntryId.From(42), "call1");
        var restored = ToolTaskInput.FromJson(input.ToJson());
        Assert.Equal(42, restored.Assistant.Value);
        Assert.Equal("call1", restored.CallId);
    }

    // ─── call 阶段 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CallPhase_ExecutesToolAndSettlesCompleted()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read",
            new Dictionary<string, object?> { ["type"] = "object" },
            execute: (args, api, context) =>
            {
                api.Output("streamed ");
                return Task.FromResult(new ToolExecutionResult
                {
                    Content = [new TextContent("done")],
                    Details = new Dictionary<string, object?> { ["n"] = 1L },
                });
            }));

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskStatus.Terminal, record.State.Status);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome?.Status);

        // 工具以校验后的参数执行了一次。
        var executed = Assert.IsType<Dictionary<string, object?>>(Assert.Single(fixture.Tool.ExecutedArgs));
        Assert.Empty(executed);

        // 槽完成、携带条目、进度清除（输出被 finishSlot 清除，条目接管呈现）。
        var entry = await ResultEntryAsync(fixture, record);
        var slot = SlotOf(await GetToolsAsync(fixture));
        Assert.Equal("done", slot.Status);
        Assert.Equal(entry.Id.Value, slot.Entry?.Value);
        Assert.Null(slot.Output);
        Assert.Null(slot.Details);

        // 结果条目：模型消息与结构化诊断。
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.Equal("call1", message.ToolCallId);
        Assert.Equal("read", message.ToolName);
        Assert.Equal("done", Assert.IsType<TextContent>(message.Content[0]).Text);
        Assert.False(message.IsError);
    }

    [Fact]
    public async Task CallPhase_UnavailableTool_SettlesInvalidResult()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" }));
        // agent 没有任何工具 → 调用无法解析。
        var runtime = new FakeRuntime(fixture.Session, fixture.Conv, fixture.TaskId,
            new Agent { ThinkingLevel = ThinkingLevel.Off, Extensions = [], Tools = [], Sections = [] }, []);

        await CallPhase(await GetTaskAsync(fixture), runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome?.Status);
        var entry = await ResultEntryAsync(fixture, record);
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.True(message.IsError);
        Assert.Equal("tool_unavailable", FirstDiagnostic(entry).Code);
        Assert.Empty(fixture.Tool.ExecutedArgs);
    }

    [Fact]
    public async Task CallPhase_ValidationFails_SettlesInvalidArguments()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new List<object?> { "path" },
        }, callArgs: new Dictionary<string, object?>()));

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome?.Status);
        var entry = await ResultEntryAsync(fixture, record);
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.True(message.IsError);
        Assert.Equal("invalid_arguments", FirstDiagnostic(entry).Code);
        Assert.Empty(fixture.Tool.ExecutedArgs);
    }

    [Fact]
    public async Task CallPhase_BeforeToolBlock_SettlesBlocked()
    {
        var hooks = new[]
        {
            new FakeHooks
            {
                BeforeToolHandler = (call, api, context) => Task.FromResult<ToolHookDecision?>(
                    new ToolHookDecision { Block = "not allowed" }),
            },
        };
        var fixture = await CreateFixtureAsync(
            new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" }), hooks: hooks);

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome?.Status);
        var entry = await ResultEntryAsync(fixture, record);
        var diagnostic = FirstDiagnostic(entry);
        Assert.Equal("blocked", diagnostic.Code);
        Assert.Contains("not allowed", diagnostic.Message);
        Assert.Empty(fixture.Tool.ExecutedArgs);
    }

    [Fact]
    public async Task CallPhase_BeforeToolReplacesArguments_UsesReplacedAfterValidation()
    {
        var hooks = new[]
        {
            new FakeHooks
            {
                BeforeToolHandler = (call, api, context) => Task.FromResult<ToolHookDecision?>(
                    new ToolHookDecision
                    {
                        Arguments = new Dictionary<string, object?> { ["path"] = "replaced.txt" },
                    }),
            },
        };
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new List<object?> { "path" },
        }, callArgs: new Dictionary<string, object?> { ["path"] = "original.txt" }), hooks: hooks);

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var executed = Assert.IsType<Dictionary<string, object?>>(Assert.Single(fixture.Tool.ExecutedArgs));
        Assert.Equal("replaced.txt", executed["path"]);
        Assert.Equal(TaskOutcomeStatus.Completed, (await GetTaskAsync(fixture)).State.Outcome?.Status);
    }

    [Fact]
    public async Task CallPhase_ExecuteThrows_SettlesFailedWithToolError()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" },
            execute: (_, _, _) => throw new InvalidOperationException("boom")));

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskStatus.Terminal, record.State.Status);
        Assert.Equal(TaskOutcomeStatus.Failed, record.State.Outcome?.Status);
        Assert.Equal("Tool read threw", record.State.Outcome?.Error?.Message);
        var entry = await ResultEntryAsync(fixture, record);
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.True(message.IsError);
        var diagnostic = FirstDiagnostic(entry);
        Assert.Equal("tool_error", diagnostic.Code);
        Assert.Equal("boom", diagnostic.Message);
    }

    [Fact]
    public async Task CallPhase_ControlIsCarriedIntoOutcome()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" },
            execute: (_, _, _) => Task.FromResult(new ToolExecutionResult
            {
                Content = [new TextContent("done")],
                Control = new ToolControl { Terminate = true, AddTools = ["extra"], Handoff = "next" },
            })));

        await CallPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        var result = Assert.IsType<Dictionary<string, object?>>(record.State.Outcome!.Result);
        var control = Assert.IsType<Dictionary<string, object?>>(result["control"]);
        Assert.Equal(true, control["terminate"]);
        Assert.Equal(new object[] { "extra" }, Assert.IsType<List<object>>(control["addTools"]));
        Assert.Equal("next", control["handoff"]);
    }

    // ─── execute（恢复）与 abort ────────────────────────────────────────────

    private static async Task RunExecuteAsync(Fixture fixture, string replay, Dictionary<string, object?> args)
    {
        await fixture.Session.CommitAsync(async tx =>
        {
            var current = await tx.GetTaskAsync(fixture.TaskId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("task missing");
            ((Transaction)tx).SetTask(current with
            {
                State = new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = new ToolTaskCheckpoint.Execute(args, replay).ToJson(),
                },
            });
            return 0;
        }, Ctx);
        await ExecutePhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);
    }

    [Fact]
    public async Task ExecutePhase_ReplaySafe_RerunsFromScratch()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" },
            replay: "safe"));
        // 槽携带被打断尝试发布的输出；重跑前应被清除。
        await fixture.Session.CommitAsync(async tx =>
        {
            var change = await tx.DocAsync(Live.LiveDoc, fixture.Conv).ConfigureAwait(false);
            var slot = Live.ToolSlotOf(change, fixture.TaskId);
            Live.ReplaceSlot(change, slot! with { Output = "stale" });
            return 0;
        }, Ctx);

        await RunExecuteAsync(fixture, "safe", new Dictionary<string, object?>());

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome?.Status);
        Assert.Single(fixture.Tool.ExecutedArgs);
        var slot = SlotOf(await GetToolsAsync(fixture));
        Assert.Equal("done", slot.Status);
        Assert.Null(slot.Output);
    }

    [Fact]
    public async Task ExecutePhase_Unsafe_SettlesInterrupted()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" }));

        await RunExecuteAsync(fixture, "unsafe", new Dictionary<string, object?>());

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskOutcomeStatus.Failed, record.State.Outcome?.Status);
        Assert.Equal("Tool read was interrupted and may have partially run", record.State.Outcome?.Error?.Message);
        Assert.Empty(fixture.Tool.ExecutedArgs);
    }

    [Fact]
    public async Task ExecutePhase_SafeToolUnsafeStored_DoesNotRerun()
    {
        // 只有存储与当前策略都说 safe 才重跑；任一为 unsafe 都不重跑。
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" },
            replay: "safe"));

        await RunExecuteAsync(fixture, "unsafe", new Dictionary<string, object?>());

        Assert.Empty(fixture.Tool.ExecutedArgs);
        Assert.Equal(TaskOutcomeStatus.Failed, (await GetTaskAsync(fixture)).State.Outcome?.Status);
    }

    [Fact]
    public async Task Abort_SettlesAborted()
    {
        var fixture = await CreateFixtureAsync(new FakeTool("read", new Dictionary<string, object?> { ["type"] = "object" }));

        await AbortPhase(await GetTaskAsync(fixture), fixture.Runtime, Ctx);

        var record = await GetTaskAsync(fixture);
        Assert.Equal(TaskStatus.Terminal, record.State.Status);
        Assert.Equal(TaskOutcomeStatus.Aborted, record.State.Outcome?.Status);
        var entry = await ResultEntryAsync(fixture, record);
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.True(message.IsError);
        Assert.Equal("aborted", FirstDiagnostic(entry).Code);
        Assert.Contains("was aborted", FirstDiagnostic(entry).Message);
    }

    // ─── 结果条目与 usage ───────────────────────────────────────────────────

    [Fact]
    public async Task AppendToolResult_RendersDiagnosticsAndRecordsUsage()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless()).ConfigureAwait(false)).Id,
            Ctx);
        var call = new ToolCallContent("call1", "read", new Dictionary<string, object?>());
        var result = new ToolExecutionResult
        {
            Content = [new TextContent("partial")],
            IsError = true,
            Diagnostics =
            [
                new ToolDiagnostic { Severity = "warn", Code = "truncated", Message = "Output truncated: 2 lines, 100 bytes dropped" },
                new ToolDiagnostic { Severity = "error", Code = "tool_error", Message = "boom" },
            ],
            Usage = new Pi.Ai.Types.Usage(10, 5, 0, 0, 0.5, 0),
        };

        var entry = await session.CommitAsync(
            async tx => await ToolTask.AppendToolResultAsync(tx, conv, call, result, 7), Ctx);

        Assert.Equal(DurableEntries.ToolResultEntry.Kind, entry.Kind);
        var message = Assert.IsType<ToolResultMessage>(entry.Model![0]);
        Assert.Equal(7L, message.Timestamp);
        Assert.True(message.IsError);
        // 内容以渲染后的诊断结尾。
        var rendered = Assert.IsType<TextContent>(message.Content[1]).Text;
        Assert.Equal(
            "<harness>\n[warn] Output truncated: 2 lines, 100 bytes dropped\n[error] boom\n</harness>",
            rendered);
        Assert.Equal(2, DurableEntries.ToolResultDiagnostics(entry.Data!).Count);

        // usage 计入 pi.usage 的 tools 桶。
        var usage = await session.SnapshotAsync(Pi.Durable.Harness.Usage.UsageDoc, conv, Ctx);
        var tools = (IReadOnlyDictionary<string, object?>)usage!["tools"]!;
        var total = (IReadOnlyDictionary<string, object?>)tools["read"]!;
        Assert.Equal(10L, total["input"]);
        Assert.Equal(5L, total["output"]);
        Assert.Equal(0.5, total["cost"]);
    }

    [Fact]
    public void HarnessError_Shape()
    {
        var error = ToolTask.HarnessError("blocked", "no");
        Assert.True(error.IsError);
        Assert.Empty(error.Content!);
        var diagnostic = Assert.Single(error.Diagnostics!);
        Assert.Equal("blocked", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
    }
}
