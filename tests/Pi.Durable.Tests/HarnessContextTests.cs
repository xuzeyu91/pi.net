using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable;
using Pi.Durable.Session;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Tests;

/// <summary>P52：harness 上下文派生（context.ts）与任务图挂载（task-graph.ts）。</summary>
public class HarnessContextTests
{
    private static Context Ctx => Context.Background;

    private static UserMessage User(string text, long ts = 1)
        => new([new TextContent(text)], ts);

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => new(content.ToList());

    // ─── orderToolResults ────────────────────────────────────────────────

    [Fact]
    public void OrderToolResults_PlacesResultsAfterCallsAndSynthesizesMissing()
    {
        var user = User("hi");
        var callA = new ToolCallContent("a", "read", new Dictionary<string, object?>());
        var callB = new ToolCallContent("b", "edit", new Dictionary<string, object?>());
        var assistant = Assistant(callA, callB);
        var resultB = new ToolResultMessage("b", "edit", [new TextContent("ok")], Timestamp: 2);
        var later = User("next", 3);

        var ordered = ConversationContext.OrderToolResults([user, assistant, resultB, later]);

        // 结果按调用顺序插在 assistant 之后；缺失的 a 被合成；匹配的 b 原位取用。
        Assert.Equal(5, ordered.Count);
        Assert.Same(user, ordered[0]);
        Assert.Same(assistant, ordered[1]);
        var missing = Assert.IsType<ToolResultMessage>(ordered[2]);
        Assert.Equal("a", missing.ToolCallId);
        Assert.True(missing.IsError);
        Assert.Same(resultB, ordered[3]);
        Assert.Same(later, ordered[4]);
    }

    [Fact]
    public void OrderToolResults_DropsUnmatchedResults()
    {
        var orphan = new ToolResultMessage("ghost", "read", [new TextContent("?")], Timestamp: 1);
        var ordered = ConversationContext.OrderToolResults([orphan, User("x", 2)]);
        // 无 assistant 的结果被丢弃。
        Assert.Single(ordered);
        Assert.IsType<UserMessage>(ordered[0]);
    }

    // ─── deriveContext ───────────────────────────────────────────────────

    [Fact]
    public async Task DeriveContext_AppliesHeadMarkerEditsAndStopReasonFilter()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);

        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())).Id, Ctx);

        var ids = await session.CommitAsync<(EntryId First, EntryId Errored, EntryId Head, EntryId Original, EntryId Editor)>(async tx =>
        {
            var first = await tx.AppendEntryAsync(conv, new EntryDraft
            {
                Kind = "user",
                Model = [User("one")],
            });
            var errored = await tx.AppendEntryAsync(conv, new EntryDraft
            {
                Kind = "assistant",
                Model = [new AssistantMessage([new TextContent("boom")], StopReason.Error)],
            });
            var head = await tx.AppendEntryAsync(conv, new EntryDraft
            {
                Kind = "marker",
                Head = errored.Id,
            });
            var original = await tx.AppendEntryAsync(conv, new EntryDraft
            {
                Kind = "user",
                Model = [User("original", 5)],
            });
            // 编辑挂在后继条目上，替换其目标条目的模型消息。
            var editor = await tx.AppendEntryAsync(conv, new EntryDraft
            {
                Kind = "edit",
                Edits = [new ContextEdit.Replace(original.Id, [User("replaced", 5)])],
            });
            return (first.Id, errored.Id, head.Id, original.Id, editor.Id);
        }, Ctx);
        var (e1, e2, e3, e4, e5) = ids;

        var bounds = await session.ReadOnLineAsync(
            () => ConversationContext.CaptureContextBoundsAsync(storage, conv, Ctx));
        Assert.NotNull(bounds);
        // 最新 head 标记是 e3。
        Assert.Equal(e3, bounds.Head!.Id);

        var view = await ConversationContext.DeriveContextAsync(storage, conv, bounds, Ctx);
        // 活动条目 = head 标记 + 其 head 之后的非 head 条目。
        // head 范围从其 head（含）到尾部：e2 也在范围内（其错误消息随后被停因过滤）。
        Assert.Equal([e3, e2, e4, e5], view.Entries.Select(e => e.Id));
        // 错误终态的 assistant 不进模型上下文；e5 的 Replace 编辑替换了 e4 的消息。
        Assert.Equal(["replaced"], view.Messages
            .OfType<UserMessage>()
            .Select(m => ((TextContent)m.Content[0]).Text));
    }

    [Fact]
    public async Task CaptureContextBounds_EmptyConversationYieldsNothing()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())).Id, Ctx);
        var bounds = await session.ReadOnLineAsync(
            () => ConversationContext.CaptureContextBoundsAsync(storage, conv, Ctx));
        Assert.Null(bounds);
    }

    [Fact]
    public async Task CaptureContextBounds_RejectsInvisibleEntry()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())).Id, Ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ReadOnLineAsync(
                () => ConversationContext.CaptureContextBoundsAsync(
                    storage, conv, Ctx, EntryId.From(999))));
    }

    // ─── task graph ──────────────────────────────────────────────────────

    [Fact]
    public async Task TaskGraphView_ShowsLiveTasksAndOwnedConversations()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var view = new TaskGraphView(session, storage);

        // 输入与 checkpoint 必须是 JSON 安全形状（存储按严格 JSON 克隆）。
        Dictionary<string, object?> Checkpoint() => new() { ["phase"] = "call" };
        var demoTask = new DurableTask<Dictionary<string, object?>, Dictionary<string, object?>, string>
        {
            Definition = new TaskDefinition<Dictionary<string, object?>, Dictionary<string, object?>, string>
            {
                Name = "demo",
                Version = 1,
                Initial = input => Checkpoint(),
            },
        };

        var owner = await session.CommitAsync<(TaskId<object?> Id, ConversationId Child)>(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(new ConversationOwnership.Ownerless());
            var id = await tx.CreateTaskAsync(
                demoTask,
                Checkpoint(),
                new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = conversation.Id,
                });
            var child = await tx.CreateConversationAsync(
                new ConversationOwnership.TaskOwned(TaskId<object?>.From(id.Value)));
            return (TaskId<object?>.From(id.Value), child.Id);
        }, Ctx);
        var taskId = owner.Id;
        var childId = owner.Child;

        var graph = (await view.StateAsync(Ctx)).Value!;
        var node = Assert.Single(TaskGraphReader.Nodes(graph));
        Assert.Equal(taskId.Value, node.Id.Value);
        Assert.Equal("demo", node.Kind);
        Assert.Equal("call", Assert.IsType<TaskGraphState.Active>(node.State).Phase);
        // 任务拥有的子对话进入节点。
        Assert.Equal([childId.Value], node.Conversations.Select(c => c.Value));

        // 提交推进：再创建一个任务，状态源在发布后更新。
        var second = await session.CommitAsync<TaskId<object?>>(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(new ConversationOwnership.Ownerless());
            var id = await tx.CreateTaskAsync(
                demoTask,
                Checkpoint(),
                new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = conversation.Id,
                });
            return TaskId<object?>.From(id.Value);
        }, Ctx);
        graph = (await view.StateAsync(Ctx)).Value!;
        Assert.Equal(2, TaskGraphReader.Count(graph));
        Assert.NotNull(TaskGraphReader.Node(graph, second.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task TaskGraphView_WatchAdvancesWithCommits()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var view = new TaskGraphView(session, storage);

        // 输入与 checkpoint 必须是 JSON 安全形状（存储按严格 JSON 克隆）。
        Dictionary<string, object?> Checkpoint() => new() { ["phase"] = "call" };
        var demoTask = new DurableTask<Dictionary<string, object?>, Dictionary<string, object?>, string>
        {
            Definition = new TaskDefinition<Dictionary<string, object?>, Dictionary<string, object?>, string>
            {
                Name = "demo",
                Version = 1,
                Initial = input => Checkpoint(),
            },
        };

        var watch = await view.WatchAsync(Ctx);
        var frames = new List<int>();
        watch.Start((graph, _, _) =>
        {
            lock (frames) frames.Add(TaskGraphReader.Count(graph));
            return Task.CompletedTask;
        });

        var first = await session.CommitAsync<TaskId<object?>>(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(new ConversationOwnership.Ownerless());
            var id = await tx.CreateTaskAsync(
                demoTask,
                Checkpoint(),
                new TaskOptions
                {
                    Ownership = new TaskOwnership.ConversationOwner(),
                    ConversationId = conversation.Id,
                });
            return TaskId<object?>.From(id.Value);
        }, Ctx);

        for (var i = 0; i < 50 && frames.All(count => count == 0); i++)
        {
            await Task.Delay(20);
        }

        watch.Cancel();
        await watch.Closed;
        lock (frames) Assert.Contains(1, frames);
    }
}
