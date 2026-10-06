using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;

/// <summary>
/// P57：注册表与 agent 解析（harness-registry.test.ts）。
/// <para>覆盖 <see cref="Registry"/> 的安装 / 原位替换 / 按名卸载 / 已发布快照不可变、安装态校验
/// （重名工具 / 重名段 / 非法段键 / 保留段键 / 任务名冲突）、内建任务常驻，以及
/// <see cref="AgentDocs.ResolveAgent"/> 的扩展选择、同名工具原位替换、包装与过滤、
/// 段顺序与 instructions 收尾、钩子收集与字段默认值。</para>
/// </summary>
public class HarnessRegistryTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── fake 工具 / 段 / 任务 ───

    /// <summary>带可空 snippet 的应用工具。对应 TS <c>AppTool</c>。</summary>
    private sealed record AppTool(string Name, string Description, string? Snippet = null) : IToolRegistration
    {
        public ToolSchema Parameters { get; } = new(new Dictionary<string, object?> { ["type"] = "object" });

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => Task.FromResult(new ToolExecutionResult { Content = [] });
    }

    private static AppTool Tool(string name, string? description = null, string? snippet = null)
        => new(name, description ?? $"{name} tool", snippet);

    private sealed class AppSection(string key, Func<Task<string?>> render, bool? tag = null) : IPromptSection
    {
        public string Key => key;

        public bool? Tag => tag;

        public Task<string?> RenderAsync(PromptInput input, Context context) => render();
    }

    private static IPromptSection Section(string key, string? text, bool? tag = null)
        => new AppSection(key, () => Task.FromResult(text), tag);

    private static DurableTask<JsonDict, JsonDict, JsonDict> TaskDefinition(string name, int version = 1)
        => HarnessTestSupport.DefineTask(name, version, [("run", (_, _, _) => System.Threading.Tasks.Task.CompletedTask)]);

    private static AnyDurableTask FakeTask(string name, int version = 1)
        => AnyDurableTask.From(TaskDefinition(name, version));

    private sealed class Extension(
        string name,
        IReadOnlyList<IToolRegistration>? tools = null,
        IReadOnlyList<IPromptSection>? sections = null,
        IReadOnlyList<HookRegistration>? hooks = null,
        IReadOnlyList<Wrap>? wraps = null,
        IReadOnlyList<AnyDurableTask>? tasks = null) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => tools;

        public IReadOnlyList<IPromptSection>? Sections => sections;

        public IReadOnlyList<HookRegistration>? Hooks => hooks;

        public IReadOnlyList<Wrap>? Wraps => wraps;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    private static Extension Ext(
        string name,
        IToolRegistration[]? tools = null,
        IPromptSection[]? sections = null,
        HookRegistration[]? hooks = null,
        Wrap[]? wraps = null,
        AnyDurableTask[]? tasks = null) => new(name, tools, sections, hooks, wraps, tasks);

    private static string[] Names(IEnumerable<IExtension> items) => items.Select(item => item.Name).ToArray();
    private static string[] Names(IEnumerable<IToolRegistration> items) => items.Select(item => item.Name).ToArray();
    private static string[] Keys(IEnumerable<IPromptSection> items) => items.Select(item => item.Key).ToArray();

    private static Agent Resolve(AgentState? state, IRegistrySnapshot snapshot, List<object> reports)
        => AgentDocs.ResolveAgent(state, snapshot, AgentDocs.ResolveSettings(null), reports.Add);

    // ─── registry ───

    [Fact]
    public void Registry_InstallsReplacesInPlaceAndUninstallsByName()
    {
        var registry = Registry.CreateRegistry();
        var listener = new List<string>();
        registry.Subscribe(() => listener.Add(string.Join(",", Names(registry.Snapshot().Installed()))));

        var a = Ext("a", tools: [Tool("read", snippet: "Read files")]);
        var b = Ext("b", tools: [Tool("read"), Tool("bash")]);
        registry.Install(a);
        registry.Install(b);
        var before = registry.Snapshot();
        // 同名新对象原位替换。
        var a2 = Ext("a", tools: [Tool("grep")]);
        registry.Install(a2);
        Assert.Equal(new[] { "a", "b" }, Names(registry.Snapshot().Installed()));
        Assert.Same(a2, registry.Snapshot().Extension("a"));
        Assert.Equal(
            new[] { "grep@a", "read@b", "bash@b" },
            registry.Snapshot().Tools().Select(entry => $"{entry.Tool.Name}@{entry.Extension.Name}").ToArray());
        // 旧快照保持原样。
        Assert.Same(a, before.Extension("a"));
        Assert.Equal("Read files", ((AppTool)before.Tools()[0].Tool).Snippet);
        // 卸载按名匹配（无论对象）；之后的安装追加到末尾。
        registry.Uninstall(a);
        registry.Uninstall(a);
        Assert.Equal(new[] { "b" }, Names(registry.Snapshot().Installed()));
        registry.Install(a);
        Assert.Equal(new[] { "b", "a" }, Names(registry.Snapshot().Installed()));
        Assert.Equal(new[] { "a", "a,b", "a,b", "b", "b,a" }, listener.ToArray());
    }

    [Fact]
    public void Registry_ValidatesAsAfterInstallAndPublishesNothingWhenInvalid()
    {
        var registry = Registry.CreateRegistry();
        registry.Install(Ext("tasks", tasks: [FakeTask("app.index")]));
        var published = 0;
        registry.Subscribe(() => published++);
        var before = registry.Snapshot();

        var invalid = new (IExtension Extension, string Message)[]
        {
            (Ext("x", tools: [Tool("read"), Tool("read")]), "two tools named read"),
            (Ext("x", sections: [Section("a", "1"), Section("a", "2")]), "two sections"),
            (Ext("x", sections: [Section("Bad Key", "")]), "must match"),
            (Ext("x", sections: [Section("instructions", "")]), "reserved"),
            (Ext("x", tasks: [FakeTask("pi.generation")]), "already installed"),
            (Ext("x", tasks: [FakeTask("app.index")]), "already installed"),
        };
        foreach (var (extension, message) in invalid)
        {
            var error = Assert.ThrowsAny<Exception>(() => registry.Install(extension));
            Assert.Contains(message, error.Message);
        }

        Assert.Same(before, registry.Snapshot());
        Assert.Equal(0, published);
        // 替换持有任务名的扩展是合法的：校验在替换后的状态上运行。
        registry.Install(Ext("tasks", tasks: [FakeTask("app.index", 2)]));
        Assert.Equal(2, registry.Snapshot().Task("app.index")!.Version);
    }

    [Fact]
    public void Registry_AlwaysHoldsBuiltInTasksThatAreNotAnExtension()
    {
        var registry = Registry.CreateRegistry();
        Assert.Empty(registry.Snapshot().Installed());
        Assert.Equal(
            Registry.BuiltinTasks.Select(task => task.Name),
            registry.Snapshot().Tasks().Select(task => task.Name));
        var custom = FakeTask("app.custom");
        registry.Install(Ext("custom", tasks: [custom]));
        Assert.Equal(
            Registry.BuiltinTasks.Select(task => task.Name).Append("app.custom"),
            registry.Snapshot().Tasks().Select(task => task.Name));
        Assert.Same(custom, registry.Snapshot().Task("app.custom"));
        registry.Uninstall(Ext("custom"));
        Assert.Null(registry.Snapshot().Task("app.custom"));
    }

    // ─── agent 解析 ───

    private static readonly AppTool Read = Tool("read");
    private static readonly AppTool Bash = Tool("bash");
    private static readonly AppTool Edit = Tool("edit");

    private static Extension CodingExt() => Ext("coding",
        tools: [Read, Bash, Edit],
        sections: [Section("preamble", "You code.", tag: false), Section("cwd", "/repo")]);

    private static Extension SkillsExt() => Ext("skills", sections: [Section("skills", "S")]);
    private static Extension ReviewerExt() => Ext("reviewer", sections: [Section("role", "Review.")]);

    private static IRegistrySnapshot Snapshot(params IExtension[] extensions)
    {
        var registry = Registry.CreateRegistry();
        foreach (var extension in extensions) registry.Install(extension);
        return registry.Snapshot();
    }

    [Fact]
    public void ResolveAgent_SelectsDefaultArrayOrDefaultEditedByAddAndRemove()
    {
        var snapshot = Snapshot(CodingExt(), SkillsExt(), ReviewerExt());
        var reports = new List<object>();

        Assert.Equal(new[] { "coding", "skills", "reviewer" }, Names(Resolve(null, snapshot, reports).Extensions));

        var settings = AgentDocs.ResolveSettings(new HarnessSettings { Extensions = [CodingExt(), SkillsExt()] });
        var picks = AgentDocs.ResolveAgent(null, snapshot, settings, reports.Add);
        Assert.Equal(new[] { "coding", "skills" }, Names(picks.Extensions));

        var explicitPicks = AgentDocs.ResolveAgent(
            new AgentState { Extensions = new AgentState.ExtensionSelection { Exact = ["reviewer", "coding"] } }, snapshot, settings, reports.Add);
        Assert.Equal(new[] { "reviewer", "coding" }, Names(explicitPicks.Extensions));

        // add 追加、remove 剔除、重复保留首位、未安装名跳过。
        var edited = AgentDocs.ResolveAgent(
            new AgentState { Extensions = new AgentState.ExtensionSelection { Add = ["reviewer", "coding", "gone"], Remove = ["skills"] } },
            snapshot, settings, reports.Add);
        Assert.Equal(new[] { "coding", "reviewer" }, Names(edited.Extensions));

        // 旧对象以其名代指：选中的是已安装的扩展。
        var stale = AgentDocs.ResolveAgent(
            new AgentState { Extensions = new AgentState.ExtensionSelection { Exact = ["skills", "skills"] } }, snapshot, settings, reports.Add);
        Assert.Single(stale.Extensions);
    }

    [Fact]
    public void ResolveAgent_SkipsUninstalledNamesAndResolvesThemAgainOnceInstalled()
    {
        var registry = Registry.CreateRegistry();
        registry.Install(CodingExt());
        var state = new AgentState { Extensions = new AgentState.ExtensionSelection { Exact = ["coding", "skills"] } };
        var reports = new List<object>();
        Assert.Equal(new[] { "coding" }, Names(AgentDocs.ResolveAgent(state, registry.Snapshot(), AgentDocs.ResolveSettings(null), reports.Add).Extensions));
        registry.Install(SkillsExt());
        Assert.Equal(new[] { "coding", "skills" }, Names(AgentDocs.ResolveAgent(state, registry.Snapshot(), AgentDocs.ResolveSettings(null), reports.Add).Extensions));
    }

    [Fact]
    public void ResolveAgent_ReplacesSameNameToolsInPlaceWrapsTheWinnerThenAppliesTheFilter()
    {
        var venvBash = Tool("bash", description: "venv bash");
        var calls = new List<string>();
        var timing = Ext("timing", wraps:
        [
            Define.WrapTool(Bash, inner => ((AppTool)inner) with { Description = $"{inner.Description} (timed)" }),
            Define.WrapTool(Bash, inner => ((AppTool)inner) with { Description = $"{inner.Description} [2]" }),
            // 未选中 grep：包装器什么都不做、也不上报。
            Define.WrapTool(Tool("grep"), inner =>
            {
                calls.Add("grep");
                return inner;
            }),
        ]);
        var snapshot = Snapshot(CodingExt(), Ext("venv", tools: [venvBash]), timing);
        var reports = new List<object>();
        var agent = Resolve(null, snapshot, reports);
        Assert.Equal(
            new[] { ("read", "read tool"), ("bash", "venv bash (timed) [2]"), ("edit", "edit tool") },
            agent.Tools.Select(tool => (tool.Name, tool.Description)).ToArray());
        Assert.Empty(reports);
        Assert.Empty(calls);

        // 数组精确按名与序保留，重复名取首位。
        var filtered = Resolve(new AgentState { Tools = new AgentState.ToolSelection { Exact = ["edit", "missing", "read", "edit"] } }, snapshot, reports);
        Assert.Equal(new[] { "edit", "read" }, Names(filtered.Tools));
        var removed = Resolve(new AgentState { Tools = new AgentState.ToolSelection { Remove = ["bash"] } }, snapshot, reports);
        Assert.Equal(new[] { "read", "edit" }, Names(removed.Tools));
    }

    [Fact]
    public void ResolveAgent_DropsToolOrSectionWhoseWrapperThrowsOrRenamesItAndReportsTheFailure()
    {
        var broken = Ext("broken", wraps:
        [
            Define.WrapTool(Read, _ => throw new InvalidOperationException("wrapper failed")),
            Define.WrapTool(Edit, inner => ((AppTool)inner) with { Name = "renamed" }),
            Define.WrapSection("cwd", _ => throw new InvalidOperationException("section wrapper failed")),
        ]);
        var snapshot = Snapshot(CodingExt(), broken);
        var reports = new List<object>();
        var agent = Resolve(null, snapshot, reports);
        Assert.Equal(new[] { "bash" }, Names(agent.Tools));
        Assert.Equal(new[] { "preamble" }, Keys(agent.Sections));
        Assert.Equal(
            new[] { "wrapper failed", "Wrapper renamed edit to renamed", "section wrapper failed" },
            reports.Select(error => ((Exception)error).Message).ToArray());
    }

    [Fact]
    public async Task ResolveAgent_OrdersSectionsByExtensionReplacesSameKeysInPlaceAndRendersInstructionsLastUnwrapped()
    {
        var over = Ext("override",
            sections: [Section("preamble", "You review.", tag: false)],
            wraps:
            [
                Define.WrapSection("cwd", inner => new WrappedSection(inner.Key, async (input, ctx) =>
                    $"{await inner.RenderAsync(input, ctx)}!", inner.Tag)),
                // instructions 不被包裹。
                Define.WrapSection("instructions", _ => throw new InvalidOperationException("never")),
            ]);
        var snapshot = Snapshot(CodingExt(), SkillsExt(), over);
        var reports = new List<object>();
        var agent = AgentDocs.ResolveAgent(
            new AgentState { Instructions = "Be terse." }, snapshot, AgentDocs.ResolveSettings(null), reports.Add);

        var rendered = new List<(string, string?)>();
        foreach (var section in agent.Sections)
        {
            rendered.Add((section.Key, await section.RenderAsync(InputFor(agent), Ctx)));
        }

        Assert.Equal(
            new (string, string?)[] { ("preamble", "You review."), ("cwd", "/repo!"), ("skills", "S"), ("instructions", "Be terse.") },
            rendered.ToArray());
        Assert.Null(agent.Sections[^1].Tag);
        Assert.Empty(reports);
    }

    [Fact]
    public void ResolveAgent_CollectsHooksOfSelectedExtensionsInOrderAndAppliesFieldDefaults()
    {
        var first = new object();
        var second = new object();
        var onYield = new object();
        var registry = Registry.CreateRegistry();
        registry.Install(Ext("a", hooks:
        [
            new HookRegistration(Registry.BuiltinTasks[1].Name, first),
            new HookRegistration(Registry.BuiltinTasks[0].Name, onYield),
        ]));
        registry.Install(Ext("b", hooks: [new HookRegistration(Registry.BuiltinTasks[1].Name, second)]));
        var snapshot = registry.Snapshot();
        var reports = new List<object>();

        var toolTaskName = Registry.BuiltinTasks[1].Name;
        var generationTaskName = Registry.BuiltinTasks[0].Name;
        Assert.Equal([first, second], AgentDocs.AgentHooks(Resolve(null, snapshot, reports), toolTaskName));
        Assert.Equal([second, first], AgentDocs.AgentHooks(
            Resolve(new AgentState { Extensions = new AgentState.ExtensionSelection { Exact = ["b", "a"] } }, snapshot, reports), toolTaskName));
        Assert.Empty(AgentDocs.AgentHooks(
            Resolve(new AgentState { Extensions = new AgentState.ExtensionSelection { Exact = ["b"] } }, snapshot, reports), generationTaskName));

        var defaults = Resolve(null, snapshot, reports);
        Assert.Null(defaults.Model);
        Assert.Equal(ThinkingLevel.Off, defaults.ThinkingLevel);
        Assert.Null(defaults.Cwd);
        var configured = Resolve(new AgentState
        {
            Model = new ModelRef("p", "m"),
            ThinkingLevel = ThinkingLevel.High,
            Cwd = "/w",
        }, snapshot, reports);
        Assert.Equal(new ModelRef("p", "m"), configured.Model);
        Assert.Equal(ThinkingLevel.High, configured.ThinkingLevel);
        Assert.Equal("/w", configured.Cwd);
    }

    private sealed class WrappedSection(string key, Func<PromptInput, Context, Task<string?>> render, bool? tag) : IPromptSection
    {
        public string Key => key;

        public bool? Tag => tag;

        public Task<string?> RenderAsync(PromptInput input, Context context) => render(input, context);
    }

    private static PromptInput InputFor(Agent agent) => new()
    {
        ConversationId = DurableIds.ConversationId(1),
        Agent = agent,
        Env = null,
        Shown = new Dictionary<string, string>(),
        Read = null!,
    };
}
