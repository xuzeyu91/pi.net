using System.Text;
using Pi.Chord.Context;
using Pi.Durable;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

public class HarnessBaseTests
{
    private static Context Ctx => Context.Background;

    // ---------- ToolOutput ----------

    [Fact]
    public void SanitizeOutput_RemovesControlCharsButKeepsTabsAndNewlines()
    {
        Assert.Equal("a\tb\nc\u00a0d", ToolOutput.SanitizeOutput("a\tb\nc\u0000\u00a0d"));
        Assert.Equal("x", ToolOutput.SanitizeOutput("x\ufff9\ufffa\ufffb"));
    }

    [Fact]
    public void BoundOutput_HeadRetainsFirstLinesWithinByteLimit()
    {
        var text = "one\ntwo\nthree\nfour";
        var slice = ToolOutput.BoundOutput(text, new OutputLimits
        {
            MaxBytes = 100,
            MaxLines = 2,
            Retain = OutputRetain.Head,
        });
        Assert.Equal("one\ntwo\n", slice.Text);
        Assert.Equal(8, slice.Bytes);
        // 全文 18 字节，保留 8 字节，丢弃 three 与 four 两行共 10 字节。
        Assert.Equal(10, slice.DroppedBytes);
        Assert.Equal(2, slice.DroppedLines);
    }

    [Fact]
    public void BoundOutput_TailRetainsLastLines()
    {
        var text = "one\ntwo\nthree\nfour";
        var slice = ToolOutput.BoundOutput(text, new OutputLimits
        {
            MaxBytes = 100,
            MaxLines = 2,
            Retain = OutputRetain.Tail,
        });
        Assert.Equal("three\nfour", slice.Text);
        Assert.Equal(2, slice.DroppedLines);
    }

    [Fact]
    public void BoundOutput_CutsLongSingleLineOnCharacterBoundary()
    {
        // "ééé…" 每字符 2 字节：6 字节上限在字符边界截断。
        var text = new string('é', 10);
        var slice = ToolOutput.BoundOutput(text, new OutputLimits
        {
            MaxBytes = 6,
            MaxLines = 10,
            Retain = OutputRetain.Head,
        });
        Assert.Equal(6, slice.Bytes);
        Assert.Equal(new string('é', 3), slice.Text);
        // 字符边界不落在字节中间。
        Assert.Equal("éé", ToolOutput.BoundOutput("ééé", new OutputLimits
        {
            MaxBytes = 5,
            MaxLines = 10,
            Retain = OutputRetain.Head,
        }).Text);
    }

    [Fact]
    public void OutputBuffer_HeadStopsStoringOnceFull()
    {
        var buffer = new ToolOutput.Buffer(new OutputLimits
        {
            MaxBytes = 10,
            MaxLines = 100,
            Retain = OutputRetain.Head,
        });
        buffer.Push("0123456789");
        buffer.Push("abcdefghij");
        buffer.End();
        var snapshot = buffer.Snapshot();
        Assert.Equal("0123456789", snapshot.Text);
        Assert.Equal(10, snapshot.DroppedBytes);
        Assert.Equal(0, snapshot.DroppedLines);
    }

    [Fact]
    public void OutputBuffer_TailKeepsTheLastWindow()
    {
        // MaxBytes 必须容得下 three 与 four 两行（10 字节），窗口才保留两行。
        var buffer = new ToolOutput.Buffer(new OutputLimits
        {
            MaxBytes = 12,
            MaxLines = 2,
            Retain = OutputRetain.Tail,
        });
        buffer.Push("one\ntwo\nthree\nfour");
        buffer.End();
        var snapshot = buffer.Snapshot();
        Assert.Equal("three\nfour", snapshot.Text);
        Assert.Equal(8, snapshot.DroppedBytes);
    }

    [Fact]
    public void OutputBuffer_SkipCountsOmittedOutputExactly()
    {
        var buffer = new ToolOutput.Buffer(new OutputLimits
        {
            MaxBytes = 100,
            MaxLines = 100,
            Retain = OutputRetain.Tail,
        });
        buffer.Push("kept");
        buffer.Push("later", new ShellOutputSkip(Bytes: 12, Newlines: 1, EndsWithNewline: true));
        buffer.End();
        // TS 语义：skip 到达时清空此前已存内容（它们不可能再进入窗口）。
        var snapshot = buffer.Snapshot();
        Assert.Equal("later", snapshot.Text);
        Assert.Equal(16, snapshot.DroppedBytes);
        Assert.Equal(1, snapshot.DroppedLines);
    }

    [Fact]
    public async Task Progress_CoalescesMarksAndPacesByWrittenBytes()
    {
        var writes = 0;
        var lastBytes = 0L;
        var progress = new ToolOutput.Progress(
            write: () =>
            {
                writes++;
                return Task.FromResult(Interlocked.Read(ref lastBytes));
            },
            onError: _ => { },
            minIntervalMs: 20);
        Interlocked.Exchange(ref lastBytes, 2048);
        // 首次提交立即发生；只有 markAndWait 的一个等待者被结算。
        await progress.MarkAndWait();
        Assert.Equal(1, writes);
        await progress.Stop();
    }

    // ---------- Waiters / util ----------

    [Fact]
    public async Task Waiters_ResolvesByKeyAndRejectsAll()
    {
        var waiters = new Waiters<string, int>();
        var task = waiters.Add("a", Ctx);
        Assert.Equal(new[] { "a" }, waiters.Keys());
        waiters.Resolve("a", 7);
        Assert.Equal(7, await task);

        var pending = waiters.Add("b", Ctx);
        waiters.RejectAll(new InvalidOperationException("stop"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
    }

    [Fact]
    public async Task Waiters_CancelsWhenContextAborts()
    {
        var waiters = new Waiters<string, int>();
        using var cts = new CancellationTokenSource();
        var context = Context.Background.WithValue(Context.AbortSignalKey, cts.Token);
        var task = waiters.Add("a", context);
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
        // 取消的等待者被移除，键组消失。
        Assert.Empty(waiters.Keys());
    }

    // ---------- HarnessJson ----------

    [Fact]
    public void AssignJson_AssignsLeafByLeafWithoutWholeContainerSets()
    {
        var target = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?> { ["text"] = "hel" },
        };
        HarnessJson.AssignJson(target, "message", new Dictionary<string, object?> { ["text"] = "hello" });
        var message = (Dictionary<string, object?>)target["message"]!;
        Assert.Equal("hello", message["text"]);

        // 数组增长。
        var list = new Dictionary<string, object?> { ["items"] = new List<object?> { "a", "b" } };
        HarnessJson.AssignJson(list, "items", new List<object?> { "a", "b", "c" });
        Assert.Equal(new List<object?> { "a", "b", "c" }, list["items"]);

        // 缩短的替换走整体赋值。
        HarnessJson.AssignJson(list, "items", new List<object?> { "x" });
        Assert.Equal(new List<object?> { "x" }, list["items"]);
    }

    // ---------- Usage ----------

    [Fact]
    public void AddUsage_AccumulatesAllCounters()
    {
        var total = Usage.ToJson(new Pi.Ai.Types.Usage(10, 20, Cost: 1.5));
        Usage.AddUsage(total, new Pi.Ai.Types.Usage(1, 2, Cost: 0.25));
        Assert.Equal(11L, total["input"]);
        Assert.Equal(22L, total["output"]);
        Assert.Equal(1.75, (double)total["cost"]!, 5);
    }

    [Fact]
    public async Task RecordUsage_WritesAndAccumulatesInUsageDoc()
    {
        var session = new DurableSession(new MemoryStorage());
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())).Id, Ctx);
        await session.CommitAsync(tx => Usage.RecordUsageAsync(
            (Transaction)tx, conv, Usage.ModelsBucket, "prov/m1",
            new Pi.Ai.Types.Usage(10, 20, Cost: 1.0)), Ctx);
        await session.CommitAsync(tx => Usage.RecordUsageAsync(
            (Transaction)tx, conv, Usage.ModelsBucket, "prov/m1",
            new Pi.Ai.Types.Usage(1, 2, Cost: 0.5)), Ctx);
        var snapshot = await session.SnapshotAsync(Usage.UsageDoc, conv, Ctx);
        Assert.NotNull(snapshot);
        var models = snapshot["models"] as IReadOnlyDictionary<string, object?>;
        var m1 = models?["prov/m1"] as IReadOnlyDictionary<string, object?>;
        Assert.NotNull(models);
        Assert.NotNull(m1);
        Assert.Equal(11L, m1["input"]);
        Assert.Equal(22L, m1["output"]);
        Assert.Equal(1.5, (double)m1["cost"]!, 5);
    }
}
