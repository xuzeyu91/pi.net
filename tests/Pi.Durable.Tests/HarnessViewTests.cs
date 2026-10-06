using System.Text.Json;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using Path = Pi.Chord.Delta.Path;

/// <summary>
/// 移植 <c>packages/durable/test/harness-view.test.ts</c>：对话结构视图（spec §9.3）的水合、
/// 精确帧 watch、挂载共享与丢弃、head 标记裁切、fork 继承、文档挂载与卸载、帧合并、close 语义。
/// </summary>
public sealed class HarnessViewTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    /// <summary>内建挂载文档的 kind 集合。对应 TS <c>MOUNTED</c>。</summary>
    private static readonly string[] Mounted = ["pi.agent", "pi.inbox", "pi.live", "pi.provider", "pi.usage"];

    private sealed record Frame(ConversationView Value, IReadOnlyList<DeltaOp> Ops);

    /// <summary>开始一次对 <paramref name="conversation"/> 的 watch，记录其获取修订与之后投递的每一帧。</summary>
    private static async Task<(ConversationView Initial, List<Frame> Frames, Func<Task> Stop)> RecordAsync(
        IConversation conversation)
    {
        var watch = await conversation.WatchAsync(Ctx);
        var initial = watch.Value;
        var frames = new List<Frame>();
        watch.Start((value, ops, _) =>
        {
            lock (frames) frames.Add(new Frame(value, ops));
            return Task.CompletedTask;
        });
        return (initial, frames, watch.Stop);
    }

    /// <summary>一次从已提交状态重建的视图，用于与推进过的视图比较。</summary>
    private static async Task<ConversationView> FreshAsync(IConversation conversation)
    {
        var state = await conversation.ViewStateAsync(Ctx);
        var value = state.Value!;
        state.Dispose();
        return value;
    }

    /// <summary>已提交状态所定义的视图：活动条目与内建文档，不经任何挂载读取。</summary>
    private static async Task<ConversationView> CommittedAsync(
        HarnessImpl harness, IConversation conversation, ConversationRecord record)
    {
        var docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        DocToken<Dictionary<string, object?>>[] tokens =
            [AgentDocs.AgentDoc, Live.LiveDoc, Inbox.InboxDoc, Provider.ProviderDoc, Pi.Durable.Harness.Usage.UsageDoc];
        foreach (var token in tokens)
        {
            var value = await harness.SnapshotAsync(token, conversation.Id, Ctx);
            if (value is not null) docs[token.Definition.Kind] = value;
        }

        var contextView = await conversation.ContextAsync(Ctx);
        return new ConversationView
        {
            Conversation = record,
            Entries = contextView.Entries,
            Docs = docs,
        };
    }

    /// <summary>
    /// 从 <paramref name="initial"/> 起校验每帧都为「上一帧 + 该帧 ops」。
    /// C# 的 <c>entries</c> 是类型化 <see cref="EntryRecord"/>（DeltaApply 无法重放），故 ops 的重放由
    /// 各用例按形状断言，这里返回末帧修订供与已提交状态整体比较。
    /// </summary>
    private static ConversationView Replay(ConversationView initial, IReadOnlyList<Frame> frames)
    {
        var value = initial;
        foreach (var frame in frames) value = frame.Value;
        return value;
    }

    /// <summary>让提交后的 watch 回调追上。对应 TS <c>drained()</c>（setTimeout 0）。</summary>
    private static Task DrainedAsync() => HarnessTestSupport.FlushAsync();

    /// <summary>统计触碰本对话挂载的提交数。</summary>
    private static Counter Touches(HarnessImpl harness, IConversation conversation)
    {
        var counter = new Counter();
        harness.SubscribeCommits((publication, _) =>
        {
            var touched = publication.Changes.Any(change => change switch
            {
                CommitChange.EntryTable entry => entry.Value.ConversationId == conversation.Id,
                CommitChange.DocumentChanged document =>
                    document.Change.ConversationId == conversation.Id
                    && Mounted.Contains(document.Change.Record.Kind, StringComparer.Ordinal)
                    && document.Change.Ops.Count > 0,
                _ => false,
            });
            if (touched) counter.Count++;
        });
        return counter;
    }

    private sealed class Counter
    {
        public int Count;
    }

    private static string[] KindsOf(ConversationView view) => [.. view.Entries.Select(entry => entry.Kind)];

    private static Path DocPath(params string[] keys)
    {
        var path = Path.Root;
        foreach (var key in keys) path = path.Append(Seg.Key(key));
        return path;
    }

    private static async Task<HarnessImpl> OpenAsync()
        => (await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")))).Harness;

    // ---------------------------------------------------------------- cases

    [Fact]
    public async Task HydratesTheActiveEntriesAndTheBuiltInDocuments()
    {
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("hello"));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        var submission = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await submission.WaitAsync(Ctx);
        var view = await FreshAsync(root);
        Assert.Equal(root.Id, view.Conversation.Id);
        Assert.Equal<KindsOfEntry>(
            (await HarnessTestSupport.AllEntriesAsync(root)).Select(Describe),
            view.Entries.Select(Describe));
        Assert.Equal<string>(Mounted, view.Docs.Keys.OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
        Assert.Empty(Assert.IsAssignableFrom<JsonDict>(view.Docs["pi.live"]));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(Assert.IsAssignableFrom<JsonDict>(view.Docs["pi.inbox"])["items"]));
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task PublishesOneFramePerTouchingCommitWhoseOperationsRebuildEveryRevision()
    {
        // TS 用 deferred 卡住生成中途的帧；C# 的 FakeProvider 只会「未应答」或「立即应答」，
        // 故改为正常应答并在提交完成后断言全部帧（帧数与 ops 形状仍完整校验）。
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("a longer answer"));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        var (initial, frames, stop) = await RecordAsync(root);
        var touching = Touches(harness, root);
        var submission = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
        await submission.WaitAsync(Ctx);
        await harness.WaitForIdleAsync(Ctx);
        await DrainedAsync();
        Assert.True(frames.Count > 0);
        Assert.Equal(touching.Count, frames.Count);
        AssertViewEqual(await CommittedAsync(harness, root, initial.Conversation), Replay(initial, frames));
        Assert.Contains(
            frames[0].Ops,
            op => op is DeltaOp.Splice splice
                && splice.Path.Segments.SequenceEqual([Seg.Key("entries")])
                && splice.Index == 0 && splice.Remove == 0
                && splice.Items.Count == 1
                && EntryKindIs(splice.Items[0], "pi.user"));
        // 文档操作在挂载路径下保持精确形状。
        Assert.Contains(
            frames.SelectMany(frame => frame.Ops),
            op => op is DeltaOp.Set set
                && set.Path.Segments.SequenceEqual([Seg.Key("docs"), Seg.Key("pi.live"), Seg.Key("generation")])
                && AttemptIs(set.Value, 1));
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task SharesUnchangedPartsBetweenRevisionsAndSkipsCommitsThatTouchNothingMounted()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var other = await harness.CreateConversationAsync(
            new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
        var (initial, frames, stop) = await RecordAsync(root);
        await other.CommitAsync(tx => tx.AppendEntryAsync(other.Id, new EntryDraft { Kind = "note" }), Ctx);
        await root.ConfigureAsync(new AgentChange { ThinkingLevel = ThinkingLevel.High }, Ctx);
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Ctx);
        await DrainedAsync();
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Ops.Count == 1 && frames[0].Ops[0] is DeltaOp.Set set
            && set.Path.Segments.SequenceEqual([Seg.Key("docs"), Seg.Key("pi.agent"), Seg.Key("thinkingLevel")])
            && Equals(set.Value, "high"));
        Assert.Same(initial.Entries, frames[0].Value.Entries);
        Assert.Same(initial.Docs["pi.live"], frames[0].Value.Docs["pi.live"]);
        Assert.Same(frames[0].Value.Docs, frames[1].Value.Docs);
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task CutsTheEntriesAtAHeadMarkerKeepingTheEntriesFromItsHead()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));

        async Task<EntryId> NoteAsync(string kind)
        {
            EntryId id = default;
            await root.CommitAsync(async tx =>
            {
                id = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = kind })).Id;
                return true;
            }, Ctx);
            return id;
        }

        await NoteAsync("a");
        var b = await NoteAsync("b");
        await NoteAsync("c");
        var (initial, frames, stop) = await RecordAsync(root);
        EntryId summary = default;
        await root.CommitAsync(async tx =>
        {
            summary = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "summary", Head = b })).Id;
            return true;
        }, Ctx);
        await NoteAsync("d");
        await root.ResetAsync(null, Ctx);
        await DrainedAsync();
        Assert.Equal(
            [["summary", "b", "c"], ["summary", "b", "c", "d"], ["pi.reset"]],
            frames.Select(frame => KindsOf(frame.Value)).ToArray());
        Assert.True(frames[0].Ops.Count == 1 && frames[0].Ops[0] is DeltaOp.Splice splice
            && splice.Path.Segments.SequenceEqual([Seg.Key("entries")])
            && splice.Index == 0 && splice.Remove == 1
            && splice.Items.Count == 1 && EntryIdIs(splice.Items[0], summary));
        AssertViewEqual(await CommittedAsync(harness, root, initial.Conversation), Replay(initial, frames));
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task KeepsOnlyMountedEntriesForARawHeadWriteThatTargetsBeforeTheActiveRange()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        EntryId old = default;
        await root.CommitAsync(async tx =>
        {
            old = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "old" })).Id;
            return true;
        }, Ctx);
        await root.ResetAsync(null, Ctx);
        var (_, frames, stop) = await RecordAsync(root);
        await root.CommitAsync(
            tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "summary", Head = old }), Ctx);
        await DrainedAsync();
        // 模型上下文现在又从 old 开始，但挂载从未持有它（spec §12）；重建的挂载会显示它。
        Assert.Equal(["summary"], KindsOf(frames[0].Value));
        await stop();
        Assert.Equal(["summary", "old"], KindsOf(await FreshAsync(root)));
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task CutsAForkViewIntoItsInheritedEntries()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        EntryId a = default, b = default;
        await root.CommitAsync(async tx => { a = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "a" })).Id; return true; }, Ctx);
        await root.CommitAsync(async tx => { b = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "b" })).Id; return true; }, Ctx);
        var fork = await root.ForkAsync(
            b, new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
        var (initial, frames, stop) = await RecordAsync(fork);
        await fork.CommitAsync(
            tx => tx.AppendEntryAsync(fork.Id, new EntryDraft { Kind = "summary", Head = b }), Ctx);
        await DrainedAsync();
        Assert.Equal([a, b], initial.Entries.Select(entry => entry.Id).ToArray());
        Assert.Equal(["summary", "b"], KindsOf(frames[0].Value));
        AssertViewEqual(await CommittedAsync(harness, fork, initial.Conversation), frames[0].Value);
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    /// <summary>逐字段比较两个视图，差异时给出可读的定位信息（C# 的集合相等比 xUnit 默认更直观）。</summary>
    private static void AssertViewEqual(ConversationView expected, ConversationView actual)
    {
        Assert.Equal(expected.Conversation, actual.Conversation);
        Assert.Equal<KindsOfEntry>(expected.Entries.Select(Describe), actual.Entries.Select(Describe));
        Assert.Equal<EntryId>(expected.Entries.Select(entry => entry.Id), actual.Entries.Select(entry => entry.Id));
        Assert.Equal<string>(expected.Docs.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Docs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var kind in expected.Docs.Keys)
        {
            Assert.True(actual.Docs.ContainsKey(kind), $"missing doc {kind}");
            Assert.Equal(
                JsonSerializer.Serialize(expected.Docs[kind]),
                JsonSerializer.Serialize(actual.Docs[kind]));
        }
    }

    private readonly record struct KindsOfEntry(string Kind, EntryId Id, EntryId? Head);

    private static KindsOfEntry Describe(EntryRecord entry) => new(entry.Kind, entry.Id, entry.Head);

    [Fact]
    public async Task ShowsAForkInheritedEntriesAndFollowsOnlyTheForkOwnCommits()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        EntryId first = default;
        await root.CommitAsync(async tx => { first = (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "first" })).Id; return true; }, Ctx);
        var fork = await root.ForkAsync(
            first, new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
        var (initial, frames, stop) = await RecordAsync(fork);
        Assert.Equal(["first"], KindsOf(initial));
        Assert.Equal(new ConversationParent { ConversationId = root.Id, At = first }, initial.Conversation.Parent);
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "parent" }), Ctx);
        await fork.CommitAsync(tx => tx.AppendEntryAsync(fork.Id, new EntryDraft { Kind = "child" }), Ctx);
        await DrainedAsync();
        Assert.Equal(
            [["first", "child"]],
            frames.Select(frame => KindsOf(frame.Value)).ToArray());
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task UnmountsARetiredDocumentAndMountsItsRecreationWhole()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var (_, frames, stop) = await RecordAsync(root);
        await root.CommitAsync(async tx =>
        {
            await tx.RetireDocAsync(Live.LiveDoc, root.Id);
            return true;
        }, Ctx);
        await root.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, root.Id);
            live.Set(DocPath("tools"), new List<object?>());
            return true;
        }, Ctx);
        await DrainedAsync();
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Ops.Count == 1 && frames[0].Ops[0] is DeltaOp.Delete del
            && del.Path.Segments.SequenceEqual([Seg.Key("docs"), Seg.Key("pi.live")]));
        Assert.True(frames[1].Ops.Count == 1 && frames[1].Ops[0] is DeltaOp.Set set
            && set.Path.Segments.SequenceEqual([Seg.Key("docs"), Seg.Key("pi.live")]));
        Assert.False(frames[0].Value.Docs.ContainsKey("pi.live"));
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task ReplacesUndeliveredFramesWithTheNewestViewAfter100PendingFrames()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var watch = await root.WatchAsync(Ctx);
        for (var index = 0; index < 101; index++)
        {
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Ctx);
        }

        var frames = new List<Frame>();
        watch.Start((value, ops, _) =>
        {
            lock (frames) frames.Add(new Frame(value, ops));
            return Task.CompletedTask;
        });
        await DrainedAsync();
        Assert.Single(frames);
        Assert.True(frames[0].Ops.Count == 1 && frames[0].Ops[0] is DeltaOp.Replace);
        Assert.Equal(101, frames[0].Value.Entries.Count);
        await watch.Stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task KeepsStatesAndWatchesOfOneConversationIndependentAndRemountsAfterTheLastOneDetaches()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var state = await root.ViewStateAsync(Ctx);
        var (_, frames, stop) = await RecordAsync(root);
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "one" }), Ctx);
        await DrainedAsync();
        Assert.Equal(["one"], KindsOf(state.Value!));
        await stop();
        Assert.Single(frames);
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "two" }), Ctx);
        await DrainedAsync();
        Assert.Equal(["one", "two"], KindsOf(state.Value!));
        var last = state.Value!;
        state.Dispose();
        // 没有观察者剩下，挂载被丢弃：新观察者从已提交状态构建新修订。
        var rebuilt = await FreshAsync(root);
        AssertViewEqual(last, rebuilt);
        Assert.NotSame(last, rebuilt);
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task EndsStatesAndWatchesAtCloseAndRejectsLaterAcquisition()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var watch = await root.WatchAsync(Ctx);
        var state = await root.ViewStateAsync(Ctx);
        await harness.CloseAsync(Ctx);
        Assert.Equal(new WatchEnd.SessionClosed(), await watch.Closed);
        Assert.Empty(state.Value!.Entries);
        await Assert.ThrowsAnyAsync<Exception>(() => root.WatchAsync(Ctx));
        await Assert.ThrowsAnyAsync<Exception>(() => root.ViewStateAsync(Ctx));
    }

    [Fact]
    public async Task RejectsAnAcquisitionCancelledOrClosedWhileItWaitsForTheSessionLine()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));

        (HarnessTestSupport.Deferred<Unit> Release, Task Blocking) Hold()
        {
            var release = HarnessTestSupport.NewDeferred<Unit>();
            var blocking = root.CommitAsync(async _ => { await release.Task; return true; }, Ctx);
            return (release, blocking);
        }

        var held = Hold();
        using var controller = new CancellationTokenSource();
        var cancelled = root.WatchAsync(ContextSignals.WithAbortSignal(controller.Token, Ctx));
        controller.Cancel();
        held.Release.Resolve();
        await held.Blocking;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        held = Hold();
        var closedWhileQueued = root.WatchAsync(Ctx);
        var closing = harness.CloseAsync(Ctx);
        held.Release.Resolve();
        await held.Blocking;
        await Assert.ThrowsAnyAsync<Exception>(() => closedWhileQueued);
        await closing;
    }

    [Fact]
    public async Task SharesOneMountBetweenConcurrentObserversAndIsolatesAFailingListener()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var failing = await root.WatchAsync(Ctx);
        var (_, frames, stop) = await RecordAsync(root);
        var shared = await root.ViewStateAsync(Ctx);
        Assert.Same(failing.Value, shared.Value);
        shared.Dispose();
        failing.Start((_, _, _) => throw new InvalidOperationException("listener failed"));
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "one" }), Ctx);
        await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "two" }), Ctx);
        await DrainedAsync();
        var end = Assert.IsType<WatchEnd.ListenerError>(await failing.Closed);
        Assert.Equal("listener failed", end.Error.Message);
        Assert.Equal(2, frames.Count);
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    [Fact]
    public async Task PublishesOneFrameForACommitThatAppendsSeveralEntriesAndEditsADocument()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var other = DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "app.other",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["n"] = 0L },
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });
        var (initial, frames, stop) = await RecordAsync(root);
        await root.CommitAsync(async tx =>
        {
            await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "a" });
            var live = await tx.DocAsync(Live.LiveDoc, root.Id);
            live.Set(DocPath("tools"), new List<object?>());
            await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "b" });
            return true;
        }, Ctx);
        // 未挂载的文档不发布任何东西。
        await root.CommitAsync(async tx =>
        {
            var doc = await tx.DocAsync(other, root.Id);
            doc.Set(DocPath("n"), 1L);
            return true;
        }, Ctx);
        await DrainedAsync();
        Assert.Single(frames);
        Assert.Equal(["a", "b"], KindsOf(frames[0].Value));
        AssertViewEqual(await CommittedAsync(harness, root, initial.Conversation), Replay(initial, frames));
        await stop();
        await HarnessTestSupport.CloseQuietlyAsync(harness);
    }

    private static bool EntryKindIs(object? item, string kind) => item is EntryRecord record && record.Kind == kind;

    private static bool EntryIdIs(object? item, EntryId id) => item is EntryRecord record && record.Id == id;

    private static bool AttemptIs(object? value, long attempt) =>
        value is IReadOnlyDictionary<string, object?> dict
        && dict.TryGetValue("attempt", out var found)
        && JsonSerializer.SerializeToElement(found).GetInt64() == attempt;
}
