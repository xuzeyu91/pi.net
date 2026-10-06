using Pi.Chord.Context;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 内建系统提示段管理：<c>pi.system</c> 条目的重放、渲染与规划。
/// 对应 TS <c>harness/prompt.ts</c> 全量。
/// </summary>
public static class Prompt
{
    /// <summary>把工具注册擦除为其模型声明。</summary>
    public static ToolDefinition DeclarationOf(IToolRegistration tool)
        => new(tool.Name, tool.Description, tool.Parameters);

    /// <summary>
    /// 按顺序重放系统消息后的生效段：就地设置、<c>null</c> 删除、重加即追加。
    /// 对应 TS <c>replaySections()</c>。
    /// </summary>
    public static Dictionary<string, string> ReplaySections(IReadOnlyList<ChatMessage> messages)
    {
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is not SystemMessage system || system.Sections is null) continue;
            foreach (var (key, value) in system.Sections)
            {
                if (value is null) shown.Remove(key);
                else shown[key] = value;
            }
        }

        return shown;
    }

    /// <summary>
    /// 按序渲染 agent 的段。<c>null</c> 渲染省略该段；带标记文本包裹为 <c>&lt;key&gt;\n…\n&lt;/key&gt;</c>。
    /// 抛错的段保留其已显示文本（若有）并上报；<paramref name="context"/> 中止后的错误向上传播。
    /// 对应 TS <c>renderSections()</c>。
    /// </summary>
    public static async Task<Dictionary<string, string>> RenderSections(
        IReadOnlyList<IPromptSection> sections, PromptInput input, IReadOnlyDictionary<string, string> shown,
        Action<Exception> report, Context context)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            string? text;
            try
            {
                text = await section.RenderAsync(input, context).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (context.AbortSignal is { IsCancellationRequested: true }) throw;
                report(error);
                if (shown.TryGetValue(section.Key, out var kept)) desired[section.Key] = kept;
                continue;
            }

            if (text is null) continue;
            desired[section.Key] = section.Tag ?? true
                ? $"<{section.Key}>\n{text}\n</{section.Key}>"
                : text;
        }

        return desired;
    }

    /// <summary>
    /// 规划使 <paramref name="view"/> 的重放段与工具等于 <paramref name="desired"/> 与 <paramref name="tools"/>
    /// 的 <c>pi.system</c> 条目：头标记后无后续系统条目时写完整基线（省略更早的系统条目）；最小补丁会改变
    /// 顺序时先移除全部已显示段再按序重加；否则取最小补丁或无条目。工具变更搭在最后一条上（无段时单独一条）。
    /// 对应 TS <c>planSystemEntries()</c>。
    /// </summary>
    public static List<TypedEntryDraft<object?>> PlanSystemEntries(
        ContextView view, IReadOnlyDictionary<string, string> desired,
        IReadOnlyList<ToolDefinition> tools, long timestamp)
    {
        var head = view.Head;
        if (head is { } headRecord && !view.Entries.Any(entry => entry.Kind == "pi.system" && entry.Id > headRecord.Id))
        {
            var edits = view.Entries
                .Where(entry => entry.Kind == "pi.system")
                .Select(entry => (ContextEdit)new ContextEdit.Omit(entry.Id))
                .ToList();
            var sections0 = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key0, value0) in desired) sections0[key0] = value0;
            var baseline = SystemEntry(
                sections0,
                new ToolChanges([], tools),
                timestamp);
            return edits.Count == 0 ? [baseline] : [baseline with { Edits = edits }];
        }

        var sections = PlanSections(ReplaySections(view.Messages), desired);
        var changes = PlanTools(Transcript.GetCurrentTools(view.Messages), tools);
        if (changes.ToolsRemoved.Count == 0 && changes.ToolsAdded.Count == 0)
        {
            var patches = new List<TypedEntryDraft<object?>>();
            foreach (var patch in sections) patches.Add(SystemEntry(patch, null, timestamp));
            return patches;
        }

        if (sections.Count == 0) return [SystemEntry(null, changes, timestamp)];
        var planned = new List<TypedEntryDraft<object?>>();
        for (var index = 0; index < sections.Count; index++)
        {
            planned.Add(SystemEntry(sections[index], index == sections.Count - 1 ? changes : null, timestamp));
        }

        return planned;
    }

    private sealed record ToolChanges(IReadOnlyList<string> ToolsRemoved, IReadOnlyList<ToolDefinition> ToolsAdded);

    /// <summary>
    /// 从 <paramref name="offered"/> 到 <paramref name="desired"/> 的工具变更。声明变化的工具先移除再重加；
    /// 重放保留既有工具并追加新增；当那样得不到期望顺序时，移除全部已提供工具并按序重加全部期望工具。
    /// 对应 TS <c>planTools()</c>。
    /// </summary>
    private static ToolChanges PlanTools(IReadOnlyList<ToolDefinition> offered, IReadOnlyList<ToolDefinition> desired)
    {
        var wanted = desired.ToDictionary(tool => tool.Name, tool => tool, StringComparer.Ordinal);
        var kept = offered.Where(tool => wanted.TryGetValue(tool.Name, out var next) && Transcript.DeclarationsEqual(tool, next)).ToList();
        var keptNames = kept.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var added = desired.Where(tool => !keptNames.Contains(tool.Name)).ToList();
        var replayed = kept.Concat(added).ToList();
        var orderMatches = true;
        for (var index = 0; index < replayed.Count && index < desired.Count; index++)
        {
            if (replayed[index].Name != desired[index].Name)
            {
                orderMatches = false;
                break;
            }
        }

        if (replayed.Count != desired.Count) orderMatches = false;
        if (!orderMatches)
        {
            return new ToolChanges(
                offered.Select(tool => tool.Name).ToList(),
                desired);
        }

        return new ToolChanges(
            offered.Where(tool => !keptNames.Contains(tool.Name)).Select(tool => tool.Name).ToList(),
            added);
    }

    /// <summary>段补丁：无、最小补丁，或顺序将变时的「移除全部 / 重加全部」对。对应 TS <c>planSections()</c>。</summary>
    private static List<Dictionary<string, string?>> PlanSections(
        IReadOnlyDictionary<string, string> shown, IReadOnlyDictionary<string, string> desired)
    {
        var patchedOrder = shown.Keys.Where(desired.ContainsKey)
            .Concat(desired.Keys.Where(key => !shown.ContainsKey(key)))
            .ToList();
        var desiredOrder = desired.Keys.ToList();
        var orderChanges = patchedOrder.Count != desiredOrder.Count;
        if (!orderChanges)
        {
            for (var index = 0; index < patchedOrder.Count; index++)
            {
                if (patchedOrder[index] != desiredOrder[index])
                {
                    orderChanges = true;
                    break;
                }
            }
        }

        if (orderChanges)
        {
            var removals = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in shown.Keys) removals[key] = null;
            var restorations = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in desired) restorations[key] = value;
            return [removals, restorations];
        }

        var patch = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in shown)
        {
            desired.TryGetValue(key, out var next);
            if (next != value) patch[key] = next ?? null;
        }

        foreach (var (key, value) in desired)
        {
            if (!shown.ContainsKey(key)) patch[key] = value;
        }

        return patch.Count == 0 ? [] : [patch];
    }

    private static TypedEntryDraft<object?> SystemEntry(
        IReadOnlyDictionary<string, string?>? sections, ToolChanges? tools, long timestamp)
        => new()
        {
            Model =
            [
                new SystemMessage(
                    Content: "",
                    Sections: sections,
                    ToolsAdded: tools is null || tools.ToolsAdded.Count == 0 ? null : tools.ToolsAdded,
                    ToolsRemoved: tools is null || tools.ToolsRemoved.Count == 0 ? null : tools.ToolsRemoved,
                    Timestamp: timestamp),
            ],
        };
}
