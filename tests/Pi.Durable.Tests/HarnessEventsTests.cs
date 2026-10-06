using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 移植 <c>packages/durable/test/harness-events.test.ts</c>：内建 agent 事件流
/// （<c>watchEvents</c> / <see cref="Events.WatchEventsAsync"/>）——生命周期事件、批次投递、
/// 溢出折叠为快照、随 Harness 关闭而终止等。
/// <para>
/// 已移植（本期）：<c>EndsTheStreamWithTheHarness</c>（Harness 关闭后流以 <c>session_closed</c> 终态落定）；
/// <c>ReplacesUndeliveredBatchesWithOneSnapshotAfter100PendingBatches</c>（超过待处理上限后未投递批次折叠为单快照，含全部条目）。
/// </para>
/// <para>
/// 待续（需扩展 C# 聊天夹具/生成管线，见各 TODO）：以 message_start/message_update 文本增量重建应答（需令牌流式 faux）；
/// 工具输出追加与尾窗重建（需工具 <c>api.output</c> 流式 + <c>outputLimits</c>）；
/// 排队提交/inbox 变化/auto_retry 事件（需重试启用——注意重试循环当前在测试中不终止，见 harness-generation 记忆）；
/// 思考/文本/工具参数增量的 partial 重建（需令牌流式）；steer/reset/其他对话隔离；挂载取消；
/// 溢出快照后应用增量、usage-only 更新/deferred 轮询的事件差分（需 Live 文档直接操控夹具）。
/// </para>
/// </summary>
public sealed class HarnessEventsTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    /// <summary>冲刷待定微任务与一个宏任务轮次。对应 TS <c>drained()</c>。</summary>
    private static Task DrainedAsync() => HarnessTestSupport.FlushAsync();

    // ─── 1. 流随 Harness 关闭而终止 ───────────────────────────────────────────

    [Fact]
    public async Task EndsTheStreamWithTheHarness()
    {
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("hi"));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        var stream = await Events.WatchEventsAsync(harness, root.Id, Ctx);
        await harness.CloseAsync(Ctx);
        Assert.Equal(new WatchEnd.SessionClosed(), await stream.Closed);
    }

    // ─── 2. 超过待处理上限后折叠为单快照 ───────────────────────────────────────

    [Fact]
    public async Task ReplacesUndeliveredBatchesWithOneSnapshotAfter100PendingBatches()
    {
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("hi"));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        var stream = await Events.WatchEventsAsync(harness, root.Id, Ctx);
        try
        {
            for (var index = 0; index < 101; index++)
            {
                await root.CommitAsync(
                    tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note", Data = $"n{index}" }), Ctx);
            }

            var batches = new List<IReadOnlyList<AgentEvent>>();
            stream.Start((events, _) =>
            {
                batches.Add(events);
                return Task.CompletedTask;
            });
            await DrainedAsync();

            // 超过待处理上限：未投递批次折叠为一份快照，含全部 101 条条目。
            var single = Assert.Single(batches);
            var snapshot = Assert.IsType<AgentEvent.Snapshot>(Assert.Single(single));
            Assert.Equal(101, snapshot.Entries.Count);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
