using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// P57：系统提示准备（harness-prompt.test.ts）。
/// <para>覆盖 <see cref="Prompt.RenderSections"/>（按序渲染 / 标记 / 省略 / 失败上报 / 取消传播）、
/// <see cref="Prompt.ReplaySections"/>（就地重放 / null 删除 / 重加追加）与
/// <see cref="Prompt.PlanSystemEntries"/>（最小补丁 / 顺序重写 / 头标记后重建基线）。</para>
/// </summary>
public class HarnessPromptTests
{
    private static readonly Context Ctx = Context.Background;

    private static readonly IReadOnlyDictionary<string, object?> EmptySchema =
        new Dictionary<string, object?> { ["type"] = "object" };

    internal static ToolDefinition Declaration(string name, string? description = null)
        => new(name, description ?? name, new ToolSchema(EmptySchema));

    private static UserMessage User(string text) => new([new TextContent(text)], 0);

    private static OrderedStringMap<string> Desired(params (string Key, string Value)[] pairs)
    {
        var map = new OrderedStringMap<string>();
        foreach (var (key, value) in pairs) map.Set(key, value);
        return map;
    }

    /// <summary>按序段的最小实现。对应 TS <c>section()</c> / <c>addSection()</c>。</summary>
    internal sealed class FakeSection(string key, Func<Task<string?>> render, bool? tag = null) : IPromptSection
    {
        public string Key => key;

        public bool? Tag => tag;

        public Task<string?> RenderAsync(PromptInput input, Context context) => render();
    }

    internal static PromptInput InputFor() => new()
    {
        ConversationId = DurableIds.ConversationId(1),
        Agent = new Agent { ThinkingLevel = ThinkingLevel.Off, Extensions = [], Tools = [], Sections = [] },
        Env = null,
        Shown = new Dictionary<string, string>(),
        Read = null!,
    };

    [Fact]
    public async Task RenderSections_RendersInOrderWithTagsAndOmissionsAndFailures()
    {
        var sections = new List<IPromptSection>
        {
            new FakeSection("preamble", () => Task.FromResult<string?>("You are helpful."), tag: false),
            // wrapSection("cwd", inner => inner.render(...) + " (git)") 的等价：渲染结果已含包装后缀，
            // 再按 Tag 缺省包裹为 <cwd>…</cwd>。
            new FakeSection("cwd", () => Task.FromResult<string?>("/repo (git)")),
            new FakeSection("skipped", () => Task.FromResult<string?>(null)),
            new FakeSection("failing", () => Task.FromException<string?>(new InvalidOperationException("render failed"))),
            new FakeSection("new-failing", () => Task.FromException<string?>(new InvalidOperationException("also failed"))),
        };
        var shown = new Dictionary<string, string>
        {
            ["failing"] = "<failing>\nold\n</failing>",
            ["cwd"] = "stale",
        };
        var reports = new List<Exception>();

        var desired = await Prompt.RenderSections(sections, InputFor(), shown, reports.Add, Ctx);

        Assert.Equal(
            new[]
            {
                ("preamble", "You are helpful."),
                ("cwd", "<cwd>\n/repo (git)\n</cwd>"),
                ("failing", "<failing>\nold\n</failing>"),
            },
            desired.Select(pair => (pair.Key, pair.Value)).ToArray());
        Assert.Equal(new[] { "render failed", "also failed" }, reports.Select(error => error.Message).ToArray());
    }

    [Fact]
    public async Task RenderSections_PropagatesSectionErrorsAfterCancellation()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var cancelled = ContextSignals.WithAbortSignal(source.Token, Ctx);
        var failing = new FakeSection("a", () => Task.FromException<string?>(new OperationCanceledException("cancelled")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Prompt.RenderSections(
            [failing], InputFor(), new Dictionary<string, string>(), _ => { }, cancelled));
    }

    [Fact]
    public void ReplaySections_ReplaysInPlaceDeletesOnNullAndAppendsReAdditions()
    {
        var shown = Prompt.ReplaySections(
        [
            new SystemMessage(Sections: new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }, Timestamp: 1),
            User("x"),
            new SystemMessage(Sections: new Dictionary<string, string?> { ["b"] = "20", ["a"] = null }),
            new SystemMessage(Sections: new Dictionary<string, string?> { ["a"] = "10" }),
        ]);

        Assert.Equal(
            new[] { ("b", "20"), ("c", "3"), ("a", "10") },
            shown.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    // ─── 规划（纯函数，直接以构造的 ContextView 驱动）───

    private static ContextView View(IReadOnlyList<EntryRecord> entries, EntryRecord? head = null)
    {
        var messages = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            if (entry.Model is not null) messages.AddRange(entry.Model);
        }

        return new ContextView
        {
            Head = head,
            Entries = entries,
            Contributions = entries.Select(entry => entry.Model ?? []).ToList(),
            Messages = messages,
        };
    }

    private static EntryRecord SystemEntry(long id, IReadOnlyDictionary<string, string?> sections)
        => new()
        {
            Id = DurableIds.EntryId(id),
            ConversationId = DurableIds.ConversationId(1),
            Kind = "pi.system",
            Model = [new SystemMessage(Sections: sections)],
        };

    [Fact]
    public void PlanSystemEntries_EmitsMinimalValuePatchesRemovalsAndAdditions()
    {
        // 空视图上首次规划：全部为新增。
        var first = Prompt.PlanSystemEntries(View([]),
            Desired(("a", "1"), ("b", "2"), ("c", "3")), [], 7);
        var firstMessage = Assert.IsType<SystemMessage>(Assert.Single(Assert.Single(first).Model!));
        Assert.Equal(
            new (string, string?)[] { ("a", "1"), ("b", "2"), ("c", "3") },
            firstMessage.Sections!.Select(pair => (pair.Key, pair.Value)).ToArray());
        Assert.Null(firstMessage.ToolsAdded);
    }

    [Fact]
    public void PlanSystemEntries_WritesCompleteBaselineAfterHeadMarker()
    {
        var head = SystemEntry(1, new Dictionary<string, string?> { ["a"] = "1" });
        var view = View([head], head);
        var drafts = Prompt.PlanSystemEntries(view, Desired(("a", "1")), [], 7);
        var draft = Assert.Single(drafts);
        var message = Assert.IsType<SystemMessage>(Assert.Single(draft.Model!));
        Assert.Equal(new (string, string?)[] { ("a", "1") }, message.Sections!.Select(pair => (pair.Key, pair.Value)).ToArray());
        // 头前/头本身被省略。
        Assert.NotNull(draft.Edits);
        var omit = Assert.IsType<ContextEdit.Omit>(Assert.Single(draft.Edits!));
        Assert.Equal(head.Id, omit.Target);
    }

    [Fact]
    public void PlanSystemEntries_RewritesOrderOnlyChangesAsTwoEntries()
    {
        // 已显示段 {a,b}，期望反序 {b,a} → 先移除全部再重加全部。
        var view = View([
            SystemEntry(1, new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" }),
        ]);
        var drafts = Prompt.PlanSystemEntries(view,
            Desired(("b", "2"), ("a", "1")), [], 7);
        Assert.Equal(2, drafts.Count);
        var remove = Assert.IsType<SystemMessage>(Assert.Single(drafts[0].Model!));
        Assert.Equal(
            new[] { ("a", (string?)null), ("b", (string?)null) },
            remove.Sections!.Select(pair => (pair.Key, pair.Value)).ToArray());
        var restore = Assert.IsType<SystemMessage>(Assert.Single(drafts[1].Model!));
        Assert.Equal(
            new (string, string?)[] { ("b", "2"), ("a", "1") },
            restore.Sections!.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    [Fact]
    public void PlanSystemEntries_NoChangeYieldsNoEntries()
    {
        var view = View([
            SystemEntry(1, new Dictionary<string, string?> { ["a"] = "1", ["c"] = "3", ["d"] = "4" }),
        ]);
        var drafts = Prompt.PlanSystemEntries(view,
            Desired(("a", "1"), ("c", "3"), ("d", "4")), [], 7);
        Assert.Empty(drafts);
    }
}
