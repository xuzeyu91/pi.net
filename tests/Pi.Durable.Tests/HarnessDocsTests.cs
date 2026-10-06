using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;
using Path = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;

namespace Pi.Durable.Tests;

/// <summary>
/// P53：harness 内建文档令牌（agent.ts / inbox.ts / live.ts / provider.ts）与视图挂载
/// （view.ts）。覆盖设置解析、agent 变更与解析、创建拷贝、收件箱边界放置与撤回、
/// pi.live 的运行结束与压缩/工具槽呈现、视图的构建与推进。
/// </summary>
public class HarnessDocsTests
{
    private static Context Ctx => Context.Background;

    private static UserMessage User(string text, long ts = 1) => new([new TextContent(text)], ts);

    private static async Task<ConversationId> CreateConversationAsync(DurableSession session)
        => await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless()).ConfigureAwait(false))
                .Id,
            Ctx);

    private static void SetItems(Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change inbox, List<object?> items)
        => inbox.Set(Path.Root.Append(Seg.Key("items")), items);

    private static Task<SubmissionRecord?> GetSubmissionAsync(DurableSession session, SubmissionId id)
        => session.CommitAsync(tx => ((Transaction)tx).GetSubmissionAsync(id), Ctx);

    private static SubmissionStatus StatusOf(SubmissionRecord? record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => throw new InvalidOperationException("submission record is missing"),
    };

    private static string? ReasonOf(SubmissionRecord? record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Reason,
        SubmissionRecord.WriteRecord write => write.Reason,
        _ => throw new InvalidOperationException("submission record is missing"),
    };

    private static Dictionary<string, object?> SteerItem(long id, string text) => new()
    {
        ["id"] = id,
        ["mode"] = "steer",
        ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
    };

    private static Dictionary<string, object?> FollowUpItem(long id, string text) => new()
    {
        ["id"] = id,
        ["mode"] = "followUp",
        ["content"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } },
    };

    private static Dictionary<string, object?> WriteItem(long id, object? head = null) => new()
    {
        ["id"] = id,
        ["mode"] = "write",
        ["entry"] = head is null
            ? new Dictionary<string, object?> { ["kind"] = "note" }
            : new Dictionary<string, object?> { ["kind"] = "note", ["head"] = head },
    };

    // ─── resolveSettings ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveSettings_FillsEveryDefault()
    {
        var settings = AgentDocs.ResolveSettings(null);
        Assert.Equal(AgentDocs.DefaultRetryPolicy, settings.Retry);
        Assert.Equal(AgentDocs.DefaultCompactionPolicy, settings.Compaction);
        Assert.Equal(AgentDocs.DefaultProgressPolicy, settings.Progress);
        Assert.Equal(ToolExecutionMode.Parallel, settings.ToolExecution);
        Assert.Equal(QueueMode.OneAtATime, settings.SteeringMode);
        Assert.Equal(QueueMode.OneAtATime, settings.FollowUpMode);
        Assert.Null(settings.Extensions);
        Assert.NotNull(settings.Stream);
    }

    [Fact]
    public void ResolveSettings_OverridesProvidedFields()
    {
        var settings = AgentDocs.ResolveSettings(new HarnessSettings
        {
            ToolExecution = ToolExecutionMode.Sequential,
            SteeringMode = QueueMode.All,
            Retry = new ConversationRetryPolicy
            {
                Enabled = false,
                MaxRetries = 9,
                BaseDelayMs = 5,
                MaxAgentDelayMs = 70000,
            },
        });
        Assert.Equal(ToolExecutionMode.Sequential, settings.ToolExecution);
        Assert.Equal(QueueMode.All, settings.SteeringMode);
        Assert.Equal(QueueMode.OneAtATime, settings.FollowUpMode);
        Assert.False(settings.Retry.Enabled);
        Assert.Equal(9, settings.Retry.MaxRetries);
    }

    // ─── configure / applyChange ─────────────────────────────────────────────

    [Fact]
    public async Task Configure_SetsThenClearsFields()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange
        {
            Model = new ModelRef("prov", "m1"),
            ThinkingLevel = ThinkingLevel.High,
            Extensions = new AgentChange.ExtensionChange { Exact = [NamedExtension("a"), NamedExtension("b")] },
            Tools = new AgentChange.ToolChange { Remove = [NamedTool("hidden")] },
            Instructions = "be nice",
            Cwd = "/tmp",
        }), Ctx);

        var snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx);
        var state = AgentDocs.StateFromJson(snapshot);
        Assert.NotNull(state);
        Assert.Equal(new ModelRef("prov", "m1"), state.Model);
        Assert.Equal(ThinkingLevel.High, state.ThinkingLevel);
        Assert.Equal(["a", "b"], state.Extensions!.Exact);
        Assert.Equal(["hidden"], state.Tools!.Remove);
        Assert.Equal("be nice", state.Instructions);
        Assert.Equal("/tmp", state.Cwd);

        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange
        {
            ClearModel = true,
            ClearThinkingLevel = true,
            ClearExtensions = true,
            ClearTools = true,
            ClearInstructions = true,
            ClearCwd = true,
        }), Ctx);

        var cleared = await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx);
        Assert.Empty(cleared!);
    }

    [Fact]
    public async Task AddTools_AppendsToExactAndPrunesRemove()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        // 数组形状：追加缺少的名字。
        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange
        {
            Tools = new AgentChange.ToolChange { Exact = [NamedTool("read"), NamedTool("edit")] },
        }), Ctx);
        await session.CommitAsync(
            tx => AgentDocs.AddToolsAsync((Transaction)tx, conv, ["edit", "bash", "grep"]), Ctx);
        var snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx);
        Assert.Equal(["read", "edit", "bash", "grep"],
            ((IReadOnlyList<object?>)snapshot!["tools"]!).Select(value => (string)value!).ToList());

        // {remove} 形状：重叠时重写 remove（变更携带值时不再叠加清除标记）。
        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange
        {
            Tools = new AgentChange.ToolChange { Remove = [NamedTool("read"), NamedTool("bash")] },
        }), Ctx);
        await session.CommitAsync(
            tx => AgentDocs.AddToolsAsync((Transaction)tx, conv, ["bash", "grep"]), Ctx);
        snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx);
        var edit = (IReadOnlyDictionary<string, object?>)snapshot!["tools"]!;
        Assert.Equal(["read"], ((IReadOnlyList<object?>)edit["remove"]!).Select(value => (string)value!).ToList());

        // 未设置 tools：无操作（本就提供每个工具）。
        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange
        {
            ClearTools = true,
        }), Ctx);
        await session.CommitAsync(
            tx => AgentDocs.AddToolsAsync((Transaction)tx, conv, ["bash"]), Ctx);
        snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx);
        Assert.False(snapshot!.ContainsKey("tools"));
    }

    // ─── createAgent ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAgent_OwnerlessStartsEmptyAndTaskOwnedCopiesOwner()
    {
        var session = new DurableSession(new MemoryStorage());
        var root = await CreateConversationAsync(session);
        await session.CommitAsync(
            tx => AgentDocs.ConfigureAsync((Transaction)tx, root, new AgentChange { Instructions = "inherited" }), Ctx);

        var taskId = await session.CommitAsync(async tx =>
        {
            var id = await tx.CreateTaskAsync(
                WorkTask,
                new Dictionary<string, object?> { ["path"] = "a" },
                new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = root });
            await tx.CreateConversationAsync(new ConversationOwnership.TaskOwned(TaskId<object?>.From(id.Value)));
            return id;
        }, Ctx);

        await session.CommitAsync(async tx =>
        {
            var owned = await tx.GetConversationAsync(root);
            Assert.NotNull(owned);
            // 无主对话：createAgent 只确保空文档。
            await AgentDocs.CreateAgentAsync((Transaction)tx, owned!);
        }, Ctx);

        var ownedConversation = await session.CommitAsync(async tx =>
        {
            var conversations = await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = TaskId<object?>.From(taskId.Value) }, 10);
            return Assert.Single(conversations.Items);
        }, Ctx);
        await session.CommitAsync(async tx =>
        {
            var record = await tx.GetConversationAsync(ownedConversation.Id);
            Assert.NotNull(record);
            await AgentDocs.CreateAgentAsync((Transaction)tx, record!);
        }, Ctx);

        var copied = await session.SnapshotAsync(AgentDocs.AgentDoc, ownedConversation.Id, Ctx);
        Assert.Equal("inherited", copied!["instructions"]);
    }

    // ─── resolveAgent ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveAgent_ComposesToolsAndSectionsAppliesWrapsAndFilters()
    {
        var reported = new List<object>();
        var snapshot = new FakeSnapshot(
        [
            NamedExtension("a", tools: [NamedTool("read"), NamedTool("edit")],
                sections: [NamedSection("env")],
                wraps: [new Wrap.Tool("read", tool => tool)]),
            NamedExtension("b", tools: [NamedTool("read", description: "later wins")],
                sections: [NamedSection("env")],
                wraps: [new Wrap.Tool("edit", tool => NamedTool("renamed")),
                        new Wrap.Section("env", section => section)]),
        ]);

        var settings = AgentDocs.ResolveSettings(null);
        var agent = AgentDocs.ResolveAgent(new AgentState
        {
            Tools = new AgentState.ToolSelection { Exact = ["read", "read", "missing"] },
            Instructions = "be nice",
            Cwd = "/tmp",
        }, snapshot, settings, error => reported.Add(error));

        // 工具按扩展顺序组合（后者覆盖前者），数组过滤去重并保持顺序，缺失名跳过。
        var tools = agent.Tools.Cast<FakeTool>().ToList();
        Assert.Single(tools);
        Assert.Equal("later wins", tools[0].Description);
        // 扩展段之后渲染 instructions。
        Assert.Equal(2, agent.Sections.Count);
        Assert.Equal("env", agent.Sections[0].Key);
        Assert.Equal(AgentDocs.InstructionsKey, agent.Sections[1].Key);
        Assert.Equal("be nice", agent.Instructions);
        Assert.Equal("/tmp", agent.Cwd);
        // 改名包装丢弃目标并上报。
        Assert.Single(reported);
        Assert.Equal(ThinkingLevel.Off, agent.ThinkingLevel);
    }

    [Fact]
    public void ResolveAgent_RemovesShapeAndExtensionSelection()
    {
        var reported = new List<object>();
        var snapshot = new FakeSnapshot(
        [
            NamedExtension("a", tools: [NamedTool("read"), NamedTool("edit")]),
            NamedExtension("b", tools: [NamedTool("bash")]),
        ]);
        var settings = AgentDocs.ResolveSettings(new HarnessSettings { Extensions = [NamedExtension("a")] });

        var agent = AgentDocs.ResolveAgent(new AgentState
        {
            Extensions = new AgentState.ExtensionSelection { Add = ["b"], Remove = ["a"] },
            Tools = new AgentState.ToolSelection { Remove = ["edit"] },
        }, snapshot, settings, error => reported.Add(error));

        // 宿主默认选择 [a]，被编辑为 [b]；remove 编辑剔除已安装扩展 a。
        Assert.Equal(["b"], agent.Extensions.Select(extension => extension.Name).ToList());
        Assert.Equal(["bash"], agent.Tools.Select(tool => tool.Name).ToList());

        var exact = AgentDocs.ResolveAgent(new AgentState
        {
            Extensions = new AgentState.ExtensionSelection { Exact = ["b", "b"] },
        }, snapshot, settings, error => reported.Add(error));
        Assert.Equal(["b"], exact.Extensions.Select(extension => extension.Name).ToList());
        // 工具过滤未设置：提供组合后的每个工具。
        Assert.Equal(["bash"], exact.Tools.Select(tool => tool.Name).ToList());
    }

    [Fact]
    public void AgentHooks_SelectsMatchingTaskInExtensionOrder()
    {
        var extension = new FakeExtension("a", hooks:
        [
            new HookRegistration("other", new object()),
            new HookRegistration("pi.generation", "handlers-1"),
        ]);
        var agent = new Agent
        {
            ThinkingLevel = ThinkingLevel.Off,
            Extensions = [extension, new FakeExtension("b", hooks: [new HookRegistration("pi.generation", "handlers-2")])],
            Tools = [],
            Sections = [],
        };
        var hooks = AgentDocs.AgentHooks(agent, "pi.generation");
        Assert.Equal(["handlers-1", "handlers-2"], hooks.Cast<string>().ToList());
    }

    // ─── inbox ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyBoundary_PlacesWritesThenUsersAndPromotesResetToFinal()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        // 队列：write(head: self) + steer + steer + followUp。
        var submissionIds = await session.CommitAsync(async tx =>
        {
            var inbox = await tx.DocAsync(Inbox.InboxDoc, conv);
            var ids = new List<SubmissionId>();
            var write = await tx.CreateSubmissionAsync(new SubmissionCreate.WriteCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var steer1 = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var steer2 = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var followUp = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            ids.AddRange([write.Id, steer1.Id, steer2.Id, followUp.Id]);
            SetItems(inbox,
            [
                WriteItem(write.Id.Value, head: "self"),
                SteerItem(steer1.Id.Value, "one"),
                SteerItem(steer2.Id.Value, "two"),
                FollowUpItem(followUp.Id.Value, "later"),
            ]);
            return ids;
        }, Ctx);

        // postTools 边界 + one-at-a-time：重置提升为 final，写入先放置、首个 steer 与
        // 首个 followUp 随后（final 放置 follow-up）。
        var result = await session.CommitAsync(async tx =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(
                (Transaction)tx, conv, QueueMode.OneAtATime, QueueMode.OneAtATime);
            return await Inbox.ApplyBoundaryAsync((Transaction)tx, boundary, BoundaryAt.PostTools, now: 100);
        }, Ctx);
        Assert.True(result.Reset);
        Assert.Equal([submissionIds[1], submissionIds[3]], result.Users);

        var entries = await session.CommitAsync(
            async tx => await tx.ScanEntriesAsync(new EntryQuery { ConversationId = conv }, 10), Ctx);
        var ordered = entries.Items.OrderBy(entry => entry.Id.Value).ToList();
        // 重置写入 + steer + followUp（重置把 postTools 提升为 final）。
        Assert.Equal(3, ordered.Count);
        Assert.Equal("note", ordered[0].Kind);
        Assert.Equal(ordered[0].Id, ordered[0].Head); // head: "self" 写入
        Assert.Equal("pi.user", ordered[1].Kind);
        var message = Assert.IsType<UserMessage>(ordered[1].Model![0]);
        Assert.Equal("one", Assert.IsType<TextContent>(message.Content[0]).Text);
        Assert.Equal(100, message.Timestamp);
        var followUp = Assert.IsType<UserMessage>(ordered[2].Model![0]);
        Assert.Equal("later", Assert.IsType<TextContent>(followUp.Content[0]).Text);

        // 写入与被选中的项已离开收件箱；第二个 steer 仍在（one-at-a-time）。
        var inboxSnapshot = await session.SnapshotAsync(Inbox.InboxDoc, conv, Ctx);
        var items = (IReadOnlyList<object?>)inboxSnapshot!["items"]!;
        Assert.Single(items);
        Assert.Equal(submissionIds[2].Value, Convert.ToInt64(((IReadOnlyDictionary<string, object?>)items[0]!)["id"]!));

        // final 边界放置剩余的 steer？不——final 只放置 follow-up；steer 由下一次 postTools 放置。
        var final = await session.CommitAsync(async tx =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(
                (Transaction)tx, conv, QueueMode.OneAtATime, QueueMode.OneAtATime);
            return await Inbox.ApplyBoundaryAsync((Transaction)tx, boundary, BoundaryAt.PostTools, now: 200);
        }, Ctx);
        Assert.False(final.Reset);
        Assert.Equal([submissionIds[2]], final.Users);
        var finalSnapshot = await session.SnapshotAsync(Inbox.InboxDoc, conv, Ctx);
        Assert.Empty((IReadOnlyList<object?>)finalSnapshot!["items"]!);
    }

    [Fact]
    public async Task ApplyBoundary_QueueModeAllPlacesEverythingAndMarksStaleWrites()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        // 先放置一个 head 标记，使指向其之前条目的写入过期。
        var headEntry = await session.CommitAsync(
            tx => tx.AppendEntryAsync(conv, new EntryDraft { Kind = "user", HeadIsSelf = true }), Ctx);

        var submissionIds = await session.CommitAsync(async tx =>
        {
            var inbox = await tx.DocAsync(Inbox.InboxDoc, conv);
            var stale = await tx.CreateSubmissionAsync(new SubmissionCreate.WriteCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var steer1 = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var steer2 = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            SetItems(inbox,
            [
                WriteItem(stale.Id.Value, head: (long)(headEntry.Id.Value - 1)),
                SteerItem(steer1.Id.Value, "one"),
                SteerItem(steer2.Id.Value, "two"),
            ]);
            return new[] { stale.Id, steer1.Id, steer2.Id };
        }, Ctx);

        var result = await session.CommitAsync(async tx =>
        {
            var boundary = await Inbox.PrepareBoundaryAsync(
                (Transaction)tx, conv, QueueMode.All, QueueMode.All);
            return await Inbox.ApplyBoundaryAsync((Transaction)tx, boundary, BoundaryAt.PostTools, now: 5);
        }, Ctx);
        Assert.Equal([submissionIds[1], submissionIds[2]], result.Users);

        // 过期的写入以 stale 结算为 unanswered；steers 放置。
        var staleRecord = await GetSubmissionAsync(session, submissionIds[0]);
        Assert.Equal(SubmissionStatus.Unanswered, StatusOf(staleRecord));
        Assert.Equal("stale", ReasonOf(staleRecord));
        var placed1 = await GetSubmissionAsync(session, submissionIds[1]);
        Assert.Equal(SubmissionStatus.Placed, StatusOf(placed1));
        var items = (IReadOnlyList<object?>)(await session.SnapshotAsync(Inbox.InboxDoc, conv, Ctx))!["items"]!;
        Assert.Empty(items);
    }

    [Fact]
    public async Task WithdrawQueuedInputs_SettlesInputsAndKeepsWrites()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        var submissionIds = await session.CommitAsync(async tx =>
        {
            var inbox = await tx.DocAsync(Inbox.InboxDoc, conv);
            var steer = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var followUp = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var write = await tx.CreateSubmissionAsync(new SubmissionCreate.WriteCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            SetItems(inbox,
            [
                WriteItem(write.Id.Value),
                SteerItem(steer.Id.Value, "one"),
                FollowUpItem(followUp.Id.Value, "two"),
            ]);
            return new[] { steer.Id, followUp.Id, write.Id };
        }, Ctx);

        await session.CommitAsync(
            tx => Inbox.WithdrawQueuedInputsAsync((Transaction)tx, conv), Ctx);

        foreach (var id in new[] { submissionIds[0], submissionIds[1] })
        {
            var record = await GetSubmissionAsync(session, id);
            Assert.Equal(SubmissionStatus.Unanswered, StatusOf(record));
            Assert.Equal("aborted", ReasonOf(record));
        }

        var writeRecord = await GetSubmissionAsync(session, submissionIds[2]);
        Assert.Equal(SubmissionStatus.Queued, StatusOf(writeRecord));
        var items = (IReadOnlyList<object?>)(await session.SnapshotAsync(Inbox.InboxDoc, conv, Ctx))!["items"]!;
        Assert.Single(items);
        Assert.Equal(submissionIds[2].Value, Convert.ToInt64(((IReadOnlyDictionary<string, object?>)items[0]!)["id"]!));
    }

    [Fact]
    public async Task RemoveInboxItem_DropsOnlyTheNamedItem()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);
        var submissionIds = await session.CommitAsync(async tx =>
        {
            var inbox = await tx.DocAsync(Inbox.InboxDoc, conv);
            var first = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var second = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            SetItems(inbox, [SteerItem(first.Id.Value, "a"), SteerItem(second.Id.Value, "b")]);
            return new[] { first.Id, second.Id };
        }, Ctx);

        await session.CommitAsync(
            tx => Inbox.RemoveInboxItemAsync((Transaction)tx, conv, submissionIds[0]), Ctx);
        var items = (IReadOnlyList<object?>)(await session.SnapshotAsync(Inbox.InboxDoc, conv, Ctx))!["items"]!;
        Assert.Single(items);
        Assert.Equal(submissionIds[1].Value, Convert.ToInt64(((IReadOnlyDictionary<string, object?>)items[0]!)["id"]!));
    }

    // ─── live ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EndRun_SettlesMatchingRunInputsAndClearsPresentation()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);
        var taskId = await session.CommitAsync(async tx => await tx.CreateTaskAsync(
            WorkTask,
            new Dictionary<string, object?> { ["path"] = "a" },
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conv }), Ctx);

        var (inputIds, otherId) = await session.CommitAsync(async tx =>
        {
            var first = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var second = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var other = await tx.CreateSubmissionAsync(new SubmissionCreate.InputCreate
            {
                ConversationId = conv,
                Status = SubmissionStatus.Queued,
            });
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            live.Set(Path.Root.Append(Seg.Key("run")), new Dictionary<string, object?>
            {
                ["taskId"] = taskId.Value,
                ["inputs"] = new List<object?> { first.Id.Value, second.Id.Value },
            });
            live.Set(Path.Root.Append(Seg.Key("generation")), new Dictionary<string, object?> { ["attempt"] = 1L });
            live.Set(Path.Root.Append(Seg.Key("tools")), new List<object?>
            {
                new Dictionary<string, object?> { ["callId"] = "c1", ["name"] = "read", ["status"] = "running" },
            });
            return (new[] { first.Id, second.Id }, other.Id);
        }, Ctx);

        // 不匹配的 run taskId：保留 run，但仍清除 presentation。
        await session.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            Live.EndRun((Transaction)tx, live, TaskId<object?>.From(taskId.Value + 500),
                new SubmissionSettlement.Unanswered("aborted"));
        }, Ctx);
        var snapshot = await session.SnapshotAsync(Live.LiveDoc, conv, Ctx);
        Assert.True(snapshot!.ContainsKey("run"));
        Assert.False(snapshot.ContainsKey("generation"));
        Assert.False(snapshot.ContainsKey("tools"));

        // 匹配的 run：结算每个输入并移除 run。
        await session.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            Live.EndRun((Transaction)tx, live, TaskId<object?>.From(taskId.Value),
                new SubmissionSettlement.Unanswered("aborted"));
        }, Ctx);
        snapshot = await session.SnapshotAsync(Live.LiveDoc, conv, Ctx);
        Assert.False(snapshot!.ContainsKey("run"));
        foreach (var id in inputIds)
        {
            var record = await GetSubmissionAsync(session, id);
            Assert.Equal(SubmissionStatus.Unanswered, StatusOf(record));
            Assert.Equal("aborted", ReasonOf(record));
        }

        // 无关提交不受影响。
        var other = await GetSubmissionAsync(session, otherId);
        Assert.Equal(SubmissionStatus.Queued, StatusOf(other));
    }

    [Fact]
    public async Task Live_CompactionStatusLifecycleAndToolSlots()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);

        var compactionId = await session.CommitAsync(async tx => await tx.CreateTaskAsync(
            WorkTask,
            new Dictionary<string, object?> { ["path"] = "a" },
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conv }),
        Ctx);
        var otherId = await session.CommitAsync(async tx => await tx.CreateTaskAsync(
            WorkTask,
            new Dictionary<string, object?> { ["path"] = "c" },
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conv }),
        Ctx);
        var toolId = await session.CommitAsync(async tx => await tx.CreateTaskAsync(
            WorkTask,
            new Dictionary<string, object?> { ["path"] = "b" },
            new TaskOptions { Ownership = new TaskOwnership.ConversationOwner(), ConversationId = conv }),
        Ctx);

        await session.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            Live.AddCompactionStatus(live, new CompactionStatus
            {
                TaskId = TaskId<object?>.From(compactionId.Value),
                Reason = Pi.Durable.Harness.CompactionReason.Threshold,
                Blocking = true,
                Attempt = 1,
                RetryBackoff = new CompactionStatus.Retry(99, "boom"),
            });
            Live.AddCompactionStatus(live, new CompactionStatus
            {
                TaskId = TaskId<object?>.From(otherId.Value),
                Reason = Pi.Durable.Harness.CompactionReason.Manual,
                Blocking = false,
                Attempt = 2,
            });
            live.Set(Path.Root.Append(Seg.Key("tools")), new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["callId"] = "c1",
                    ["name"] = "bash",
                    ["taskId"] = toolId.Value,
                    ["status"] = "running",
                    ["output"] = "partial",
                    ["droppedLines"] = 3L,
                },
            });
        }, Ctx);

        // 查找与移除。
        await session.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            var status = Live.FindCompactionStatus(live, TaskId<object?>.From(compactionId.Value));
            Assert.NotNull(status);
            Assert.True(status.Blocking);
            Assert.Equal(Pi.Durable.Harness.CompactionReason.Threshold, status.Reason);
            Assert.Equal(99L, status.RetryBackoff!.At);
            Assert.Equal("boom", status.RetryBackoff.Error);

            var slot = Live.ToolSlotOf(live, TaskId<object?>.From(toolId.Value));
            Assert.NotNull(slot);
            Assert.Equal("bash", slot.Name);
            Assert.Equal("running", slot.Status);

            // 完成槽：结果条目携带输出，进度被清除。
            var entry = await tx.AppendEntryAsync(conv, new EntryDraft { Kind = "pi.tool-result" });
            Live.FinishSlot(live, slot, entry.Id);
            Live.RemoveCompactionStatus(live, TaskId<object?>.From(otherId.Value));
        }, Ctx);

        var snapshot = await session.SnapshotAsync(Live.LiveDoc, conv, Ctx);
        var slots = (IReadOnlyList<object?>)snapshot!["tools"]!;
        var finished = ToolSlot.FromJson((IReadOnlyDictionary<string, object?>)slots[0]!);
        Assert.Equal("done", finished.Status);
        Assert.Null(finished.Output);
        Assert.Null(finished.DroppedLines);
        Assert.NotNull(finished.Entry);

        var compactions = (IReadOnlyList<object?>)snapshot!["compactions"]!;
        Assert.Single(compactions);
        Assert.Equal(compactionId.Value, Convert.ToInt64(((IReadOnlyDictionary<string, object?>)compactions[0]!)["taskId"]!));

        // 移除最后一个压缩状态会移除列表本身。
        await session.CommitAsync(async tx =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, conv);
            Live.RemoveCompactionStatus(live, TaskId<object?>.From(compactionId.Value));
        }, Ctx);
        snapshot = await session.SnapshotAsync(Live.LiveDoc, conv, Ctx);
        Assert.False(snapshot!.ContainsKey("compactions"));
    }

    // ─── provider ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderDoc_InitializesFreshSessionId()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await CreateConversationAsync(session);
        var first = await session.CommitAsync(async tx =>
        {
            var doc = await tx.DocAsync(Provider.ProviderDoc, conv);
            return (string)doc.Draft["sessionId"]!;
        }, Ctx);
        Assert.Matches("^[0-9a-f-]{36}$", first);
        // 后续读取稳定：同一对话的身份不变。
        var second = await session.SnapshotAsync(Provider.ProviderDoc, conv, Ctx);
        Assert.Equal(first, second!["sessionId"]);
    }

    // ─── conversation views ──────────────────────────────────────────────────

    private sealed class RecordingObserver : ViewObserver
    {
        public List<(int EntryCount, int OpCount)> Advances = [];
        public List<ConversationView> Values = [];
        public int Publications;
        public int Closes;

        public override void Advance(ConversationView value, IReadOnlyList<DeltaOp> ops, Context context)
        {
            Advances.Add((value.Entries.Count, ops.Count));
            Values.Add(value);
        }

        public override void Publication(
            ConversationView before, ConversationView after, IReadOnlyList<DeltaOp> ops,
            CommitPublication publication, Context context)
            => Publications++;

        public override void CloseSession() => Closes++;
    }

    [Fact]
    public async Task ConversationView_BuildsThenAdvancesWithEntriesAndDocs()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var conv = await CreateConversationAsync(session);

        // 挂载在 pi.agent 文档存在之前：缺失文档缺席。
        var views = new ConversationViews(session, storage);
        var observer = new RecordingObserver();
        var initial = new List<ConversationView>();
        var (_, detach) = await views.AttachAsync(conv, (value, release) =>
        {
            initial.Add(value);
            return observer;
        }, Ctx);
        // 初始修订经 create 交付（无 Advance）；缺失文档缺席。
        Assert.Empty(initial[0].Entries);
        Assert.Empty(initial[0].Docs);

        // 条目追加：splice 操作。
        await session.CommitAsync(
            tx => tx.AppendEntryAsync(conv, new EntryDraft { Kind = "user", Model = [User("hi")] }), Ctx);
        Assert.Equal(1, observer.Advances[^1].OpCount);
        Assert.Single(observer.Values[^1].Entries);
        Assert.Equal("user", observer.Values[^1].Entries[0].Kind);

        // 新文档化身：整体 Set。
        await session.CommitAsync(
            tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange { Instructions = "be nice" }), Ctx);
        Assert.True(observer.Values[^1].Docs.ContainsKey("pi.agent"));
        Assert.Equal("be nice", observer.Values[^1].Docs["pi.agent"]["instructions"]);

        // 同一化身的又一次变更：前缀操作仍然生效。
        await session.CommitAsync(
            tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange { Cwd = "/tmp" }), Ctx);
        Assert.Equal("/tmp", observer.Values[^1].Docs["pi.agent"]["cwd"]);

        // head 标记：保留后缀并前置。
        await session.CommitAsync(
            tx => tx.AppendEntryAsync(conv, new EntryDraft { Kind = "pi.reset", HeadIsSelf = true }), Ctx);
        var entries = observer.Values[^1].Entries;
        Assert.Single(entries);
        Assert.Equal("pi.reset", entries[0].Kind);
        Assert.NotNull(entries[0].Head);

        // 退役挂载文档：从 docs 删除。
        await session.CommitAsync(tx => tx.RetireDocAsync(AgentDocs.AgentDoc, conv), Ctx);
        Assert.False(observer.Values[^1].Docs.ContainsKey("pi.agent"));

        // 每次发布都通知（含无挂载操作的提交）。
        Assert.Equal(5, observer.Publications);
        Assert.Equal(0, observer.Closes);

        detach();
    }

    [Fact]
    public async Task ConversationView_WatchSeesFramesAndSessionCloseTerminates()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var conv = await CreateConversationAsync(session);
        var views = new ConversationViews(session, storage);

        var watch = await views.WatchAsync(conv, Ctx);
        Assert.NotNull(watch.Value);

        var seen = new TaskCompletionSource<ConversationView>(TaskCreationOptions.RunContinuationsAsynchronously);
        watch.Start((value, _, _) =>
        {
            seen.TrySetResult(value);
            return Task.CompletedTask;
        });

        await session.CommitAsync(
            tx => tx.AppendEntryAsync(conv, new EntryDraft { Kind = "user" }), Ctx);
        var observed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(observed.Entries);

        await session.CloseAsync(Ctx);
        Assert.IsType<WatchEnd.SessionClosed>(await watch.Closed);
    }

    // ─── 测试夹具 ───────────────────────────────────────────────────────────

    /// <summary>工作任务（会话/文档测试共用）。对应 SessionTests 的 <c>WorkTask</c>。</summary>
    private static readonly DurableTask<Dictionary<string, object?>, Dictionary<string, object?>, bool> WorkTask =
        DurableTasks.DefineTask(new TaskDefinition<Dictionary<string, object?>, Dictionary<string, object?>, bool>
        {
            Name = "test.work",
            Version = 1,
            Initial = input => new Dictionary<string, object?> { ["phase"] = "start" },
        });

    private static FakeTool NamedTool(string name, string? description = null) => new(name, description ?? name);

    private static FakeSection NamedSection(string key) => new(key);

    private static FakeExtension NamedExtension(
        string name, IReadOnlyList<IToolRegistration>? tools = null,
        IReadOnlyList<IPromptSection>? sections = null, IReadOnlyList<Wrap>? wraps = null)
        => new(name, tools, sections, wraps);

    private sealed class FakeTool(string name, string description) : IToolRegistration
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => throw new NotSupportedException();
    }

    private sealed class FakeSection(string key) : IPromptSection
    {
        public string Key { get; } = key;

        public Task<string?> RenderAsync(PromptInput input, Context context) => Task.FromResult<string?>("text");

        public bool? Tag => null;
    }

    private sealed class FakeExtension(
        string name,
        IReadOnlyList<IToolRegistration>? tools = null,
        IReadOnlyList<IPromptSection>? sections = null,
        IReadOnlyList<Wrap>? wraps = null,
        IReadOnlyList<HookRegistration>? hooks = null) : IExtension
    {
        public string Name { get; } = name;

        public IReadOnlyList<IToolRegistration>? Tools { get; } = tools;

        public IReadOnlyList<IPromptSection>? Sections { get; } = sections;

        public IReadOnlyList<HookRegistration>? Hooks { get; } = hooks;

        public IReadOnlyList<Wrap>? Wraps { get; } = wraps;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }

    private sealed class FakeSnapshot(IReadOnlyList<IExtension> installed) : IRegistrySnapshot
    {
        public IReadOnlyList<IExtension> Installed() => installed;

        public IExtension? Extension(string name) => installed.FirstOrDefault(extension => extension.Name == name);

        public IReadOnlyList<(IExtension Extension, IToolRegistration Tool)> Tools()
            => [.. installed.SelectMany(extension => (extension.Tools ?? []).Select(tool => (extension, tool)))];

        public IReadOnlyList<(IExtension Extension, IPromptSection Section)> Sections()
            => [.. installed.SelectMany(extension => (extension.Sections ?? []).Select(section => (extension, section)))];

        public IReadOnlyList<AnyDurableTask> Tasks() => [];

        public AnyDurableTask? Task(string name) => null;
    }
}
