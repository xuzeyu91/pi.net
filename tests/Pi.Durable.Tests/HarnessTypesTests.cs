using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;
using TaskState = Pi.Durable.Types.TaskState;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Tests;

/// <summary>P51：harness 类型面（HarnessTypes / Define / 文档观察与读取面）的行为验证。</summary>
public class HarnessTypesTests
{
    [Fact]
    public void Define_BuildsSectionsHooksAndWraps()
    {
        var section = Define.Section("k", (_, _) => Task.FromResult<string?>("text"));
        Assert.Equal("k", section.Key);
        Assert.Null(section.Tag); // TS 缺省：tag 未设 → 渲染层按 true 包裹

        var tagged = Define.Section("k", (_, _) => Task.FromResult<string?>("text"), tag: false);
        Assert.False(tagged.Tag);

        var tool = new FakeTool { Name = "edit" };
        var wrap = Define.WrapTool(tool, t => new FakeTool { Name = t.Name });
        Assert.Equal("edit", ((Wrap.Tool)wrap).Name);

        var sectionWrap = Define.WrapSection("s", s => s);
        Assert.Equal("s", ((Wrap.Section)sectionWrap).Key);

        var hook = Define.Hook("pi.generation", new object());
        Assert.Equal("pi.generation", hook.Task);
    }

    [Fact]
    public void AnyDurableTask_ErasesTypedTask()
    {
        var task = new DurableTask<string, Dictionary<string, object?>, int>
        {
            Definition = new TaskDefinition<string, Dictionary<string, object?>, int>
            {
                Name = "pi.demo",
                Version = 2,
                Initial = input => new Dictionary<string, object?> { ["phase"] = "call" },
                Phases = new Dictionary<string, object?> { ["call"] = new object() },
                Abort = new object(),
            },
        };
        var erased = AnyDurableTask.From(task);
        Assert.Equal("pi.demo", erased.Name);
        Assert.Equal(2, erased.Version);
        Assert.True(erased.Initial("hello") is Dictionary<string, object?> initial && string.Equals(initial["phase"] as string, "call", System.StringComparison.Ordinal));
        Assert.Single(erased.Phases);
        Assert.NotNull(erased.Abort);
        Assert.Null(erased.Migrate);
    }

    [Fact]
    public void SubmissionDraft_DiscriminatesInputAndWrite()
    {
        SubmissionDraft input = new SubmissionDraft.Input
        {
            Content = [new TextContent("hi")],
            WhenBusy = "steer",
            RequestId = "r1",
        };
        Assert.Equal("input", input.Type);

        var write = new SubmissionDraft.Write { Entry = new EntryDraft { Kind = "pi.reset" } };
        Assert.Equal("write", write.Type);
        Assert.Null(write.RequestId);
    }

    [Fact]
    public void AgentChange_UsesExplicitClearMarkers()
    {
        var change = new AgentChange { Model = new ModelRef("anthropic", "claude"), Instructions = "be nice" };
        Assert.False(change.ClearModel);

        var clear = change with { Model = null, ClearModel = true, ClearInstructions = true };
        Assert.Null(clear.Model);
        Assert.True(clear.ClearModel);
        Assert.True(clear.ClearInstructions);
        Assert.Equal("be nice", clear.Instructions); // 未清除字段保留（TS undefined 语义）
    }

    [Fact]
    public void TaskInspection_VariantsCarryTheirPayloads()
    {
        var record = new TaskRecord
        {
            Id = TaskId<object?>.From(3),
            ConversationId = ConversationId.From(1),
            Kind = "pi.demo",
            Version = 1,
            Background = false,
            AbortRequested = false,
            State = new TaskState { Status = TaskStatus.Running },
        };
        TaskInspection running = new TaskInspection.Running { Record = record };
        Assert.IsType<TaskInspection.Running>(running);

        var waiting = new TaskInspection.Waiting([TaskId<object?>.From(7)]) { Record = record };
        Assert.Single(waiting.On);

        var blocked = new TaskInspection.Blocked("missing_task", "no definition") { Record = record };
        Assert.Equal("missing_task", blocked.Reason);
    }

    [Fact]
    public void Settings_RequiresEveryResolvedField()
    {
        var settings = new Settings
        {
            Stream = new ConversationStreamOptions(),
            Retry = new ConversationRetryPolicy { Enabled = true, MaxRetries = 3, BaseDelayMs = 500 },
            Compaction = new CompactionPolicy
            {
                Enabled = true, ReserveTokens = 20_000, KeepRecentTokens = 10_000, BackgroundTokens = 8_000,
            },
            Progress = new ProgressPolicy { PartialIntervalMs = 300, OutputIntervalMs = 1_000 },
            ToolExecution = ToolExecutionMode.Parallel,
            SteeringMode = QueueMode.OneAtATime,
            FollowUpMode = QueueMode.All,
        };
        Assert.True(settings.Retry.Enabled);
        Assert.Equal(ToolExecutionMode.Parallel, settings.ToolExecution);
    }

    [Fact]
    public void SettledTask_ExposesTerminalState()
    {
        var record = new TaskRecord
        {
            Id = TaskId<object?>.From(1),
            ConversationId = ConversationId.From(1),
            Kind = "pi.tool",
            Version = 1,
            Background = false,
            AbortRequested = false,
            State = new TaskState { Status = TaskStatus.Terminal },
        };
        var settled = new SettledTask(record);
        Assert.Equal(TaskStatus.Terminal, settled.State.Status);
    }

    private sealed class FakeTool : IToolRegistration
    {
        public string Name { get; init; } = "";

        public string Description => "";

        public ToolSchema Parameters => new(new Dictionary<string, object?> { ["type"] = "object" });

        public string? Replay { get; init; }

        public ToolExecutionMode? ExecutionMode { get; init; }

        public ToolOutputLimits? OutputLimits { get; init; }

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => Task.FromResult(new ToolExecutionResult());
    }
}
