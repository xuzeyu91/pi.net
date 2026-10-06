using Pi.Ai.Models;
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
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 移植 <c>packages/durable/test/harness-inbox.test.ts</c>：内建 <c>pi.inbox</c> 的边界语义
/// ——忙碌提交的排队与放置顺序、steer 与 follow-up 的选择、排队重置、过期 head 写入、
/// 撤回、跨 reopen、位置化 Chord op，以及 <c>pi.usage</c> 账本核算。
/// </summary>
public sealed class HarnessInboxTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    // ─── 共享辅助（对应 TS 文件顶部的 answer / gated / holdTool / transcript / inbox / toolRunning） ──

    /// <summary>一个纯文本 assistant 应答。对应 TS <c>answer()</c>。</summary>
    private static AssistantMessage Answer(string text)
        => HarnessTestSupport.AssistantReply(text);

    /// <summary>一段被门闩持有、释放或取消后返回的脚本步骤。对应 TS <c>gated()</c>。</summary>
    private static (HarnessTestSupport.FakeStep Step, Task Reached, Action Release) Gated(AssistantMessage message)
        => HarnessTestSupport.FakeStep.Gated(message);

    /// <summary>一个以 <c>error</c> 停止原因结束的空应答。对应 TS <c>fauxAssistantMessage([], {stopReason:"error"})</c>。</summary>
    private static AssistantMessage ErrorReply(string message)
        => new([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    /// <summary>
    /// 注册一个名为 <c>hold</c> 的工具：调用等待门闩后返回给定结果。对应 TS <c>holdTool()</c>。
    /// </summary>
    private static void HoldTool(HarnessTestSupport.ChatSetup setup, Task gate, ToolExecutionResult? result = null)
        => HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool(
            "hold", "Waits for the test", async (_, _) =>
            {
                await gate.ConfigureAwait(false);
                return result ?? new ToolExecutionResult { Content = [] };
            }));

    /// <summary>请求一次 <c>hold</c> 工具调用的 assistant 消息。对应 TS <c>HOLD</c>。</summary>
    private static AssistantMessage Hold =>
        new([new ToolCallContent("c1", "hold", new Dictionary<string, object?>())])
        {
            StopReason = StopReason.ToolUse,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    /// <summary>一次提交的持久化记录。对应 TS <c>status()</c>。</summary>
    private static async Task<SubmissionRecord> StatusAsync(ISubmission submission)
        => await submission.StatusAsync(Ctx).ConfigureAwait(false);

    /// <summary>某条消息的文本（system 无文本）。对应 TS <c>textOf()</c>。</summary>
    private static string? TextOf(ChatMessage? message)
    {
        switch (message)
        {
            case null:
            case SystemMessage:
                return null;
            case UserMessage user:
                return user.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            case AssistantMessage assistant:
                return assistant.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            default:
                return null;
        }
    }

    /// <summary>每条条目的 kind 与文本，跳过 system 条目。对应 TS <c>transcript()</c>。</summary>
    private static string[] Transcript(IReadOnlyList<EntryRecord> entries)
        => [.. entries
            .Where(entry => entry.Kind != "pi.system")
            .Select(entry =>
            {
                var message = entry.Model?.FirstOrDefault();
                var text = message is ToolResultMessage ? null : TextOf(message);
                return text is null ? entry.Kind : $"{entry.Kind}:{text}";
            })];

    /// <summary>收件箱的 <c>[id, mode]</c> 列表。对应 TS <c>inbox()</c>。</summary>
    private static async Task<object?[][]> InboxAsync(HarnessImpl harness, IConversation root)
    {
        var snapshot = await harness.SnapshotAsync(Inbox.InboxDoc, root.Id, Ctx).ConfigureAwait(false);
        var items = snapshot is null || !snapshot.TryGetValue("items", out var value)
            ? []
            : ((IReadOnlyList<object?>)value!).OfType<IReadOnlyDictionary<string, object?>>().ToList();
        return [.. items.Select(item => new object?[] { item["id"], item["mode"] })];
    }

    /// <summary>等到第一个工具槽进入 running。对应 TS <c>toolRunning()</c>。</summary>
    private static async Task ToolRunningAsync(HarnessImpl harness, IConversation root)
        => await HarnessTestSupport.WaitForAsync(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx).ConfigureAwait(false);
            return live is not null
                && live.TryGetValue("tools", out var tools)
                && tools is IReadOnlyList<object?> list
                && list.Count > 0
                && list[0] is IReadOnlyDictionary<string, object?> slot
                && slot.TryGetValue("status", out var status)
                && status is "running";
        }).ConfigureAwait(false);

    /// <summary>等到第 <paramref name="index"/> 个工具槽进入 <paramref name="status"/>。对应 TS 内联 waitFor。</summary>
    private static async Task ToolSlotAsync(HarnessImpl harness, IConversation root, int index, string status)
        => await HarnessTestSupport.WaitForAsync(async () =>
        {
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx).ConfigureAwait(false);
            return live is not null
                && live.TryGetValue("tools", out var tools)
                && tools is IReadOnlyList<object?> list
                && list.Count > index
                && list[index] is IReadOnlyDictionary<string, object?> slot
                && slot.TryGetValue("status", out var value)
                && value as string == status;
        }).ConfigureAwait(false);

    // ─── 1. 忙碌提交的排队与放置顺序 ──────────────────────────────────────────

    [Fact]
    public async Task QueuesBusySubmissionsAndPlacesWritesBeforeUserItemsAtTheFinalBoundary()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step, HarnessTestSupport.FakeStep.Of(Answer("second")),
            HarnessTestSupport.FakeStep.Of(Answer("third")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            var f1 = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("f1")] }, Ctx);
            var write = await root.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "note", Data = "w" },
            }, Ctx);
            var f2 = await root.SubmitAsync(new SubmissionDraft.Input
            {
                Content = [new TextContent("f2")],
                WhenBusy = "followUp",
            }, Ctx);
            foreach (var submission in new[] { f1, write, f2 })
            {
                Assert.Equal(SubmissionStatus.Queued, StatusOf(await StatusAsync(submission)));
            }

            Assert.Equal(
                [[f1.Id.Value, "followUp"], [write.Id.Value, "write"], [f2.Id.Value, "followUp"]],
                await InboxAsync(harness, root));
            Assert.Equal(["pi.user:a"], Transcript(await HarnessTestSupport.AllEntriesAsync(root)));

            first.Release();
            await f2.WaitAsync(Ctx);
            Assert.Equal(
                [
                    "pi.user:a", "pi.assistant:first", "note", "pi.user:f1",
                    "pi.assistant:second", "pi.user:f2", "pi.assistant:third",
                ],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
            var answers = (await HarnessTestSupport.AllEntriesAsync(root))
                .Where(entry => entry.Kind == "pi.assistant").ToList();
            var inputRecord = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(input));
            Assert.Equal(SubmissionStatus.Done, inputRecord.Status);
            Assert.Equal(answers[0].Id, inputRecord.Answer);
            Assert.Equal(SubmissionStatus.Done, StatusOf(await StatusAsync(write)));
            var f1Record = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(f1));
            Assert.Equal(SubmissionStatus.Done, f1Record.Status);
            Assert.Equal(answers[1].Id, f1Record.Answer);
            var f2Record = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(f2));
            Assert.Equal(SubmissionStatus.Done, f2Record.Status);
            Assert.Equal(answers[2].Id, f2Record.Answer);
            Assert.Empty(await InboxAsync(harness, root));
            Assert.Empty((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static SubmissionStatus StatusOf(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => throw new InvalidOperationException("unknown submission record"),
    };

    // ─── 2. followUpMode / steeringMode = all ────────────────────────────────

    [Fact]
    public async Task PlacesEveryFollowUpInOneSuccessorRunWithFollowUpModeAll()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step, HarnessTestSupport.FakeStep.Of(Answer("both")));
        setup.Settings = setup.Settings with { FollowUpMode = QueueMode.All };
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            var f1 = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f1")] }, Ctx);
            var f2 = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f2")] }, Ctx);
            first.Release();
            var settled = await f2.WaitAsync(Ctx);
            var f1Record = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(f1));
            Assert.Equal(f1.Id, f1Record.Id);
            Assert.Equal(SubmissionStatus.Done, f1Record.Status);
            Assert.Equal(((SubmissionRecord.InputRecord)settled.Record).Answer, f1Record.Answer);
            Assert.Equal(
                ["pi.user:a", "pi.assistant:first", "pi.user:f1", "pi.user:f2", "pi.assistant:both"],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ReadsQueueModesWhenTheFinalBoundarysCommitRunsOnTheSessionLine()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step, HarnessTestSupport.FakeStep.Of(Answer("both")));
        var yielded = HarnessTestSupport.NewDeferred<Unit>();
        // onYield 在应答到达边界选择点时触发（此时边界提交仍排在占用提交之后）。
        HarnessTestSupport.AddHooks(setup.Registry, "pi.generation",
            new HarnessTestSupport.GenerationHooksImpl
            {
                OnYieldHandler = _ =>
                {
                    yielded.Resolve();
                    return null;
                },
            });
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            var f1 = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f1")] }, Ctx);
            var f2 = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f2")] }, Ctx);

            // 占住 Session 线，让应答把边界提交排在它后面，然后改变模式。
            var held = storage.HoldCommits();
            var marker = DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
            {
                Kind = "test.marker",
                Version = 1,
                Initial = () => new Dictionary<string, object?> { ["n"] = 0L },
                Semantics = new DocumentSemantics.SessionScope(),
            });
            var occupying = root.CommitAsync(async tx =>
            {
                var doc = await tx.DocAsync(marker, root.Id).ConfigureAwait(false);
                doc.Set(Pi.Chord.Delta.Path.Root.Append(Pi.Chord.Delta.Seg.Key("n")), 1L);
                return true;
            }, Ctx);
            await held.Entered;
            first.Release();
            await yielded.Task;
            await HarnessTestSupport.FlushAsync();
            setup.Settings = setup.Settings with { FollowUpMode = QueueMode.All };
            held.Release();
            await occupying;
            var settled = await f2.WaitAsync(Ctx);
            var f1Record = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(f1));
            Assert.Equal(((SubmissionRecord.InputRecord)settled.Record).Answer, f1Record.Answer);
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 3. steer 与 follow-up 的边界选择 ───────────────────────────────────

    [Fact]
    public async Task AddsSteersToTheRunAtThePostToolsBoundaryAndHoldsFollowUpsForTheFinalBoundary()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        HoldTool(setup, gate.Task);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Hold),
            HarnessTestSupport.FakeStep.Of(Answer("after tools")),
            HarnessTestSupport.FakeStep.Of(Answer("follow-up")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await ToolRunningAsync(harness, root);
            var steer = await root.SubmitAsync(new SubmissionDraft.Input
            {
                Content = [new TextContent("s")],
                WhenBusy = "steer",
            }, Ctx);
            var followUp = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f")] }, Ctx);
            gate.Resolve();
            await followUp.WaitAsync(Ctx);
            Assert.Equal(
                [
                    "pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:s",
                    "pi.assistant:after tools", "pi.user:f", "pi.assistant:follow-up",
                ],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
            var answers = (await HarnessTestSupport.AllEntriesAsync(root))
                .Where(entry => entry.Kind == "pi.assistant").ToList();
            Assert.Equal(answers[1].Id, ((SubmissionRecord.InputRecord)await StatusAsync(input)).Answer);
            Assert.Equal(answers[1].Id, ((SubmissionRecord.InputRecord)await StatusAsync(steer)).Answer);
            Assert.Equal(answers[2].Id, ((SubmissionRecord.InputRecord)await StatusAsync(followUp)).Answer);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task AddsEverySteerToTheRunAtThePostToolsBoundaryWithSteeringModeAll()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        HoldTool(setup, gate.Task);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Hold),
            HarnessTestSupport.FakeStep.Of(Answer("after tools")),
            HarnessTestSupport.FakeStep.Of(Answer("follow-up")));
        setup.Settings = setup.Settings with { SteeringMode = QueueMode.All };
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await ToolRunningAsync(harness, root);
            var s1 = await root.SubmitAsync(new SubmissionDraft.Input
            {
                Content = [new TextContent("s1")],
                WhenBusy = "steer",
            }, Ctx);
            var f = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f")] }, Ctx);
            var s2 = await root.SubmitAsync(new SubmissionDraft.Input
            {
                Content = [new TextContent("s2")],
                WhenBusy = "steer",
            }, Ctx);
            gate.Resolve();
            await f.WaitAsync(Ctx);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(
                [
                    "pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:s1",
                    "pi.user:s2", "pi.assistant:after tools", "pi.user:f", "pi.assistant:follow-up",
                ],
                Transcript(entries));
            var answers = entries.Where(entry => entry.Kind == "pi.assistant").ToList();
            foreach (var submission in new[] { input, s1, s2 })
            {
                Assert.Equal(answers[1].Id, ((SubmissionRecord.InputRecord)await StatusAsync(submission)).Answer);
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 4. 排队重置 ────────────────────────────────────────────────────────

    [Fact]
    public async Task EndsTheRunAtAQueuedResetAfterToolsAndRunsEarlierFollowUpsInTheNewContext()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        HoldTool(setup, gate.Task);
        var requests = new List<string[]>();
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Hold),
            HarnessTestSupport.FakeStep.Of(messages =>
            {
                requests.Add([.. messages.Select(message => $"{RoleOf(message)}:{TextOf(message) ?? ""}")]);
                return Answer("fresh");
            }));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await ToolRunningAsync(harness, root);
            var followUp = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f")] }, Ctx);
            await root.ResetAsync(null, Ctx);
            gate.Resolve();
            await followUp.WaitAsync(Ctx);
            var inputRecord = Assert.IsType<SubmissionRecord.InputRecord>(await StatusAsync(input));
            Assert.Equal(SubmissionStatus.Unanswered, inputRecord.Status);
            Assert.Equal("reset", inputRecord.Reason);
            Assert.Equal(
                ["pi.user:a", "pi.assistant", "pi.tool-result", "pi.reset", "pi.user:f", "pi.assistant:fresh"],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var reset = entries.Single(entry => entry.Kind == "pi.reset");
            Assert.Equal(reset.Id, reset.Head);
            // follow-up 的请求自重置处开始：先是 follow-up，然后是切割之后的完整系统基线。
            Assert.Single(requests);
            Assert.Equal(["user:f", "system:"], requests[0]);
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static string RoleOf(ChatMessage message) => message switch
    {
        UserMessage => "user",
        AssistantMessage => "assistant",
        ToolResultMessage => "toolResult",
        SystemMessage => "system",
        _ => "unknown",
    };

    [Fact]
    public async Task PlacesAQueuedResetAfterTheAnswerAtTheFinalBoundary()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            await root.ResetAsync("handoff", Ctx);
            first.Release();
            await input.WaitAsync(Ctx);
            await harness.WaitForIdleAsync(Ctx);
            Assert.Equal(SubmissionStatus.Done, StatusOf(await StatusAsync(input)));
            Assert.Equal(
                ["pi.user:a", "pi.assistant:first", "pi.reset:handoff"],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
            var view = await root.ContextAsync(Ctx);
            var message = Assert.IsType<UserMessage>(Assert.Single(view.Messages));
            Assert.Equal("handoff", Assert.IsType<TextContent>(Assert.Single(message.Content)).Text);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ResetsAnIdleConversationAtOnceWithOrWithoutHandoffText()
    {
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused"));
        setup.Now = () => 7;
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.CommitAsync(
                async tx =>
                {
                    await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }).ConfigureAwait(false);
                    return true;
                },
                Ctx);
            await root.ResetAsync(null, Ctx);
            var view = await root.ContextAsync(Ctx);
            Assert.Equal("pi.reset", view.Head?.Kind);
            Assert.Null(view.Head?.Model);
            Assert.Empty(view.Messages);

            await root.ResetAsync("carry on", Ctx);
            view = await root.ContextAsync(Ctx);
            Assert.Equal(view.Head?.Id, view.Head?.Head);
            var message = Assert.IsType<UserMessage>(Assert.Single(view.Messages));
            Assert.Equal("carry on", Assert.IsType<TextContent>(Assert.Single(message.Content)).Text);
            Assert.Equal(7, message.Timestamp);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 5. 过期 head 写入 ──────────────────────────────────────────────────

    [Fact]
    public async Task MakesAQueuedHeadWriteStaleWhenItTargetsAnEntryBeforeTheActiveRange()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step, HarnessTestSupport.FakeStep.Of(Answer("second")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var oldPage = await root.CommitAsync(
                async tx => (await tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }).ConfigureAwait(false)).Id,
                Ctx);
            await root.ResetAsync(null, Ctx);
            var reset = (await root.ContextAsync(Ctx)).Head!;
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            var stale = await root.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "summary", Head = oldPage },
            }, Ctx);
            var fresh = await root.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "summary", Head = reset.Id },
            }, Ctx);
            first.Release();
            await input.WaitAsync(Ctx);
            var staleRecord = Assert.IsType<SubmissionRecord.WriteRecord>(await StatusAsync(stale));
            Assert.Equal(SubmissionStatus.Unanswered, staleRecord.Status);
            Assert.Equal("stale", staleRecord.Reason);
            Assert.Equal(SubmissionStatus.Done, StatusOf(await StatusAsync(fresh)));
            Assert.Empty(await InboxAsync(harness, root));

            // fresh summary 的标记把范围起点设在 reset：范围内的目标不 stale，即便它比标记本身更旧。
            // 同一边界里更早放置的重置会让它 stale。
            var inside = (await HarnessTestSupport.AllEntriesAsync(root)).First(entry => entry.Kind == "pi.user");
            var second = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("b")] }, Ctx);
            var kept = await root.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "summary", Head = inside.Id },
            }, Ctx);
            await second.WaitAsync(Ctx);
            Assert.Equal(SubmissionStatus.Done, StatusOf(await StatusAsync(kept)));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task MakesAHeadWriteStaleBehindAResetPlacedEarlierInTheSameBoundary()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var first = Gated(Answer("first"));
        provider.SetResponses(first.Step);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await first.Reached;
            var target = (await HarnessTestSupport.AllEntriesAsync(root))[0];
            await root.ResetAsync(null, Ctx);
            var summary = await root.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "summary", Head = target.Id },
            }, Ctx);
            first.Release();
            await input.WaitAsync(Ctx);
            var record = Assert.IsType<SubmissionRecord.WriteRecord>(await StatusAsync(summary));
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("stale", record.Reason);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 6. 工具请求 handoff / terminate ─────────────────────────────────────

    [Fact]
    public async Task EndsTheRunWithAPiResetEntryWhenAToolRequestsAHandoff()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        gate.Resolve();
        HoldTool(setup, gate.Task, new ToolExecutionResult
        {
            Content = [],
            Control = new ToolControl { Handoff = "continue here" },
        });
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(Hold));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var settled = await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx)).WaitAsync(Ctx);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var calling = entries.First(entry => entry.Kind == "pi.assistant");
            Assert.Equal(SubmissionStatus.Done, ((SubmissionRecord.InputRecord)settled.Record).Status);
            Assert.Equal(calling.Id, ((SubmissionRecord.InputRecord)settled.Record).Answer);
            Assert.Equal(
                ["pi.user:a", "pi.assistant", "pi.tool-result", "pi.reset:continue here"],
                Transcript(entries));
            Assert.Equal(entries[^1].Id, entries[^1].Head);
            Assert.Equal(1, provider.CallCount);
            Assert.Empty((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartsAQueuedFollowUpAfterATerminatingRound()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var gate = HarnessTestSupport.NewDeferred<Unit>();
        HoldTool(setup, gate.Task, new ToolExecutionResult
        {
            Content = [],
            Control = new ToolControl { Terminate = true },
        });
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Hold),
            HarnessTestSupport.FakeStep.Of(Answer("follow-up")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await ToolRunningAsync(harness, root);
            var f = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f")] }, Ctx);
            gate.Resolve();
            await f.WaitAsync(Ctx);
            var calling = (await HarnessTestSupport.AllEntriesAsync(root))
                .First(entry => entry.Kind == "pi.assistant");
            Assert.Equal(calling.Id, ((SubmissionRecord.InputRecord)await StatusAsync(input)).Answer);
            Assert.Equal(
                ["pi.user:a", "pi.assistant", "pi.tool-result", "pi.user:f", "pi.assistant:follow-up"],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root)));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task WritesTheLastHandoffInCallOrderAndThenRunsQueuedFollowUpsInTheNewContext()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var firstGate = HarnessTestSupport.NewDeferred<Unit>();
        var secondGate = HarnessTestSupport.NewDeferred<Unit>();
        HoldTool(setup, firstGate.Task, new ToolExecutionResult
        {
            Content = [],
            Control = new ToolControl { Handoff = "one" },
        });
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool(
            "later", "Finishes first", async (_, _) =>
            {
                await secondGate.Task;
                return new ToolExecutionResult { Content = [], Control = new ToolControl { Handoff = "two" } };
            }));
        var round = new AssistantMessage(
            [new ToolCallContent("c1", "hold", new Dictionary<string, object?>()),
             new ToolCallContent("c2", "later", new Dictionary<string, object?>())])
        {
            StopReason = StopReason.ToolUse,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(round),
            HarnessTestSupport.FakeStep.Of(Answer("follow-up")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var input = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Ctx);
            await ToolRunningAsync(harness, root);
            var f = await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("f")] }, Ctx);
            secondGate.Resolve();
            await ToolSlotAsync(harness, root, 1, "done");
            firstGate.Resolve();
            await f.WaitAsync(Ctx);
            Assert.Equal(SubmissionStatus.Done, StatusOf(await StatusAsync(input)));
            Assert.Equal(
                ["pi.tool-result", "pi.reset:two", "pi.user:f", "pi.assistant:follow-up"],
                Transcript(await HarnessTestSupport.AllEntriesAsync(root))[^4..]);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
