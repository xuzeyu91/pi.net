using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>
/// 内建 <c>pi.agent</c> 文档：对话的存储 agent 选择，以及设置解析、变更应用、
/// 创建拷贝与 agent 解析。对应 TS <c>harness/agent.ts</c> 全量。
/// </summary>
public static class AgentDocs
{
    /// <summary>agent 的 <c>instructions</c> 段的保留段键。对应 TS <c>INSTRUCTIONS_KEY</c>。</summary>
    public const string InstructionsKey = "instructions";

    public static readonly ConversationRetryPolicy DefaultRetryPolicy = new()
    {
        Enabled = true,
        MaxRetries = 3,
        BaseDelayMs = 2000,
        MaxAgentDelayMs = 60000,
    };

    public static readonly CompactionPolicy DefaultCompactionPolicy = new()
    {
        Enabled = true,
        ReserveTokens = 16384,
        KeepRecentTokens = 20000,
        BackgroundTokens = 32768,
    };

    public static readonly ProgressPolicy DefaultProgressPolicy = new()
    {
        PartialIntervalMs = 100,
        OutputIntervalMs = 100,
    };

    /// <summary>
    /// 内建 agent 文档；rewindable 使 fork 从其 fork 条目处的 agent 起步。
    /// </summary>
    public static readonly DocToken<Dictionary<string, object?>> AgentDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.agent",
            Version = 1,
            Initial = () => new Dictionary<string, object?>(),
            CheckpointWhen = (_, _, _) => true,
            Semantics = new DocumentSemantics.RewindableConversationScope(ConversationFork.AsOf),
        });

    // ─── 设置解析 ───────────────────────────────────────────────────────────

    /// <summary>agent 文档的初值（<c>{}</c>）；事件与快照的退役文档回退。对应 TS <c>AgentDoc.definition.initial()</c>。</summary>
    public static Dictionary<string, object?> InitialAgent() => new();

    /// <summary>解析宿主设置：每个字段取其内建默认值，对象字段合并。对应 TS <c>resolveSettings</c>。</summary>
    /// <remarks>
    /// TS 的对象展开允许宿主提供部分策略对象（未提供字段回落默认）；C# 策略 record 的
    /// 字段为必填，提供的 record 总是完整的——字段级合并仅对可空字段
    /// （<see cref="ConversationRetryPolicy.MaxAgentDelayMs"/>）保留默认回落语义（差异记录）。
    /// </remarks>
    public static Settings ResolveSettings(HarnessSettings? settings) => new()
    {
        Extensions = settings?.Extensions,
        Stream = settings?.Stream ?? new ConversationStreamOptions(),
        Retry = settings?.Retry ?? DefaultRetryPolicy,
        Compaction = settings?.Compaction ?? DefaultCompactionPolicy,
        Progress = settings?.Progress ?? DefaultProgressPolicy,
        ToolExecution = settings?.ToolExecution ?? ToolExecutionMode.Parallel,
        SteeringMode = settings?.SteeringMode ?? QueueMode.OneAtATime,
        FollowUpMode = settings?.FollowUpMode ?? QueueMode.OneAtATime,
    };

    // ─── 配置变更 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 对 <c>pi.agent</c> 应用一个变更：给定字段替换存储值，<c>ClearX</c> 清除，未设置的字段不动。
    /// 对应 TS <c>configure()</c>（TS 以 null 表示清除；C# 为显式清除标记）。
    /// </summary>
    public static async Task ConfigureAsync(Transaction tx, ConversationId conversationId, AgentChange change)
    {
        var doc = await tx.DocAsync(AgentDoc, conversationId).ConfigureAwait(false);
        ApplyChange(doc, change);
    }

    /// <summary>
    /// 一轮工具的 <c>addTools</c>：数组形状把缺少的名字逐个追加，<c>{"remove": …}</c> 形状
    /// 剔除给出的名字；未设置 tools 时本就提供每个工具，不写任何内容。
    /// 对应 TS <c>addTools()</c>。
    /// </summary>
    public static async Task AddToolsAsync(Transaction tx, ConversationId conversationId, IReadOnlyList<string> added)
    {
        var doc = await tx.DocAsync(AgentDoc, conversationId).ConfigureAwait(false);
        if (!doc.Draft.TryGetValue("tools", out var value) || value is null) return;
        if (value is IReadOnlyList<object?> exact)
        {
            var count = exact.Count;
            var missing = added
                .Where(name => !exact.Any(existing => (string)existing! == name))
                .Cast<object?>()
                .ToList();
            if (missing.Count > 0) doc.Splice(ToolsPath, count, 0, missing);
            return;
        }

        if (value is IReadOnlyDictionary<string, object?> edit
            && edit.TryGetValue("remove", out var removedValue)
            && removedValue is IReadOnlyList<object?> removed)
        {
            var names = removed.Select(value2 => (string)value2!).ToList();
            if (names.Any(name => added.Contains(name)))
            {
                doc.Set(ToolsPath.Append(Seg.Key("remove")),
                    names.Where(name => !added.Contains(name)).Cast<object?>().ToList());
            }
        }
    }

    private static void ApplyChange(TxDocChange doc, AgentChange change)
    {
        if (change.Model is { } model)
        {
            doc.Set(Key("model"), new Dictionary<string, object?>
            {
                ["provider"] = model.Provider,
                ["modelId"] = model.ModelId,
            });
        }
        else if (change.ClearModel)
        {
            doc.Delete(Key("model"));
        }

        if (change.ThinkingLevel is { } level) doc.Set(Key("thinkingLevel"), CamelCase(level.ToString()));
        else if (change.ClearThinkingLevel) doc.Delete(Key("thinkingLevel"));

        if (change.ClearExtensions) doc.Delete(Key("extensions"));
        else if (change.Extensions is { } extensions)
        {
            if (extensions.Exact is { } exact)
            {
                doc.Set(Key("extensions"), Names(exact));
            }
            else
            {
                var edit = new Dictionary<string, object?>();
                if (extensions.Add is { } add) edit["add"] = Names(add);
                if (extensions.Remove is { } remove) edit["remove"] = Names(remove);
                doc.Set(Key("extensions"), edit);
            }
        }

        if (change.ClearTools) doc.Delete(Key("tools"));
        else if (change.Tools is { } tools)
        {
            if (tools.Exact is { } exact)
            {
                doc.Set(Key("tools"), Names(exact));
            }
            else
            {
                doc.Set(Key("tools"), new Dictionary<string, object?>
                {
                    ["remove"] = Names(tools.Remove ?? []),
                });
            }
        }

        if (change.Instructions is not null) doc.Set(Key("instructions"), change.Instructions);
        else if (change.ClearInstructions) doc.Delete(Key("instructions"));

        if (change.Cwd is not null) doc.Set(Key("cwd"), change.Cwd);
        else if (change.ClearCwd) doc.Delete(Key("cwd"));
    }

    private static Path Key(string name) => Path.Root.Append(Seg.Key(name));

    private static string CamelCase(string value)
        => char.ToLowerInvariant(value[0]) + value[1..];

    private static List<object?> Names(IReadOnlyList<IExtension> items)
        => items.Select(item => (object?)item.Name).ToList();

    private static List<object?> Names(IReadOnlyList<IToolRegistration> items)
        => items.Select(item => (object?)item.Name).ToList();

    private static readonly Path ToolsPath = Key("tools");

    // ─── 创建拷贝 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 创建或 fork 对话的 Harness 提交中 <c>pi.agent</c> 的内建部分：fork 保留其 <c>asOf</c>
    /// 拷贝；新的任务属主对话拷贝其属主任务对话的存储 agent；新的无主对话从空开始。
    /// 对应 TS <c>createAgent()</c>。
    /// </summary>
    public static async Task CreateAgentAsync(Transaction tx, ConversationRecord conversation)
    {
        if (conversation.Parent is not null) return;
        var agent = await tx.DocAsync(AgentDoc, conversation.Id).ConfigureAwait(false);
        if (conversation.Owner is not { } owner) return;
        var ownerDoc = await tx.DocAsync(AgentDoc, owner.ConversationId).ConfigureAwait(false);
        // Object.assign：逐键整体拷贝（Set 载荷由 applier 深拷贝，无别名）。
        foreach (var (name, value) in ownerDoc.Draft.ToList()) agent.Set(Key(name), value);
    }

    // ─── 钩子 ───────────────────────────────────────────────────────────────

    /// <summary>所选扩展中某任务名的钩子处理器，按扩展顺序。对应 TS <c>agentHooks()</c>。</summary>
    public static IReadOnlyList<object> AgentHooks(Agent agent, string taskName)
    {
        var handlers = new List<object>();
        foreach (var extension in agent.Extensions)
        {
            foreach (var hook in extension.Hooks ?? [])
            {
                if (hook.Task == taskName) handlers.Add(hook.Handlers);
            }
        }

        return handlers;
    }

    // ─── agent 解析 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 从存储状态（缺省：每个字段未设置）、注册表快照与已解析设置解析 agent。
    /// 抛出或改名的包装丢弃其目标并上报；没有目标的包装不做事。
    /// 对应 TS <c>resolveAgent()</c>。
    /// </summary>
    public static Agent ResolveAgent(
        AgentState? state, IRegistrySnapshot snapshot, Settings settings, Action<object> report)
    {
        var extensions = SelectExtensions(state?.Extensions, snapshot, settings);

        var composed = new Dictionary<string, IToolRegistration>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            foreach (var tool in extension.Tools ?? []) composed[tool.Name] = tool;
        }

        var sections = new Dictionary<string, IPromptSection>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            foreach (var section in extension.Sections ?? []) sections[section.Key] = section;
        }

        foreach (var extension in extensions)
        {
            foreach (var wrap in extension.Wraps ?? [])
            {
                if (wrap is Wrap.Tool toolWrap)
                {
                    ApplyWrap(composed, toolWrap.Name, toolWrap.Wrap, tool => tool.Name, report);
                }
                else if (wrap is Wrap.Section sectionWrap)
                {
                    ApplyWrap(sections, sectionWrap.Key, sectionWrap.Wrap, section => section.Key, report);
                }
            }
        }

        IReadOnlyList<IToolRegistration> tools;
        var filter = state?.Tools;
        if (filter is null)
        {
            tools = composed.Values.ToList();
        }
        else if (filter.Exact is { } exact)
        {
            var selected = new List<IToolRegistration>();
            foreach (var name in exact.Distinct())
            {
                if (composed.TryGetValue(name, out var tool)) selected.Add(tool);
            }

            tools = selected;
        }
        else
        {
            var removed = new HashSet<string>((filter.Remove ?? []).Distinct());
            tools = composed.Values.Where(tool => !removed.Contains(tool.Name)).ToList();
        }

        var instructions = state?.Instructions;
        var agentSections = sections.Values.ToList();
        if (instructions is not null) agentSections.Add(new InstructionsSection(instructions));

        return new Agent
        {
            Model = state?.Model,
            ThinkingLevel = state?.ThinkingLevel ?? ThinkingLevel.Off,
            Extensions = extensions,
            Tools = tools,
            Sections = agentSections,
            Instructions = instructions,
            Cwd = state?.Cwd,
        };
    }

    /// <summary>从存储 JSON 还原类型化 <see cref="AgentState"/>（缺省形状皆未设置）。</summary>
    public static AgentState? StateFromJson(IReadOnlyDictionary<string, object?>? json)
    {
        if (json is null) return null;

        ModelRef? model = null;
        if (json.TryGetValue("model", out var modelValue)
            && modelValue is IReadOnlyDictionary<string, object?> modelJson)
        {
            model = new ModelRef((string)modelJson["provider"]!, (string)modelJson["modelId"]!);
        }

        ThinkingLevel? thinkingLevel = null;
        if (json.TryGetValue("thinkingLevel", out var levelValue)
            && levelValue is string levelText
            && Enum.TryParse<ThinkingLevel>(levelText, ignoreCase: true, out var level))
        {
            thinkingLevel = level;
        }

        AgentState.ExtensionSelection? extensions = null;
        if (json.TryGetValue("extensions", out var extensionsValue))
        {
            if (extensionsValue is IReadOnlyList<object?> exact)
            {
                extensions = new AgentState.ExtensionSelection
                { Exact = exact.Select(value => (string)value!).ToList() };
            }
            else if (extensionsValue is IReadOnlyDictionary<string, object?> edit)
            {
                extensions = new AgentState.ExtensionSelection
                {
                    Add = Strings(edit, "add"),
                    Remove = Strings(edit, "remove"),
                };
            }
        }

        AgentState.ToolSelection? tools = null;
        if (json.TryGetValue("tools", out var toolsValue))
        {
            if (toolsValue is IReadOnlyList<object?> exactTools)
            {
                tools = new AgentState.ToolSelection
                { Exact = exactTools.Select(value => (string)value!).ToList() };
            }
            else if (toolsValue is IReadOnlyDictionary<string, object?> edit)
            {
                tools = new AgentState.ToolSelection { Remove = Strings(edit, "remove") };
            }
        }

        return new AgentState
        {
            Model = model,
            ThinkingLevel = thinkingLevel,
            Extensions = extensions,
            Tools = tools,
            Instructions = json.TryGetValue("instructions", out var instructions) ? instructions as string : null,
            Cwd = json.TryGetValue("cwd", out var cwd) ? cwd as string : null,
        };
    }

    private static IReadOnlyList<string>? Strings(IReadOnlyDictionary<string, object?> json, string key)
        => json.TryGetValue(key, out var value) && value is IReadOnlyList<object?> items
            ? items.Select(item => (string)item!).ToList()
            : null;

    /// <summary>所选已安装扩展：存储的数组，或由 <c>{"add","remove"}</c> 编辑的默认选择。</summary>
    private static IReadOnlyList<IExtension> SelectExtensions(
        AgentState.ExtensionSelection? stored, IRegistrySnapshot snapshot, Settings settings)
    {
        List<string> selected;
        if (stored?.Exact is { } exact)
        {
            selected = [.. exact];
        }
        else
        {
            var baseNames = settings.Extensions?.Select(extension => extension.Name).ToList()
                ?? snapshot.Installed().Select(extension => extension.Name).ToList();
            var removed = new HashSet<string>(stored?.Remove ?? []);
            selected = [.. baseNames.Concat(stored?.Add ?? []).Where(name => !removed.Contains(name))];
        }

        var extensions = new List<IExtension>();
        foreach (var name in selected.Distinct())
        {
            var extension = snapshot.Extension(name);
            if (extension is not null) extensions.Add(extension);
        }

        return extensions;
    }

    private static void ApplyWrap<T>(
        Dictionary<string, T> items, string target, Func<T, T> wrap, Func<T, string> nameOf, Action<object> report)
    {
        if (!items.TryGetValue(target, out var item)) return;
        try
        {
            var wrapped = wrap(item);
            if (nameOf(wrapped) != target)
                throw new InvalidOperationException($"Wrapper renamed {target} to {nameOf(wrapped)}");
            items[target] = wrapped;
        }
        catch (Exception error)
        {
            items.Remove(target);
            report(error);
        }
    }

    /// <summary><c>instructions</c> 的内建提示段：在扩展段之后渲染。对应 TS 匿名段形状。</summary>
    private sealed class InstructionsSection(string instructions) : IPromptSection
    {
        public string Key => InstructionsKey;

        public Task<string?> RenderAsync(PromptInput input, Context context)
            => Task.FromResult<string?>(instructions);

        public bool? Tag => null;
    }
}
