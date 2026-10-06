using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

public class ProbeTests
{
    [Fact]
    public async Task ProbeHandoff()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var firstGate = HarnessTestSupport.NewDeferred<Unit>();
        var secondGate = HarnessTestSupport.NewDeferred<Unit>();
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool("hold", "Waits", async (_, _) =>
        {
            await Task.CompletedTask;
            return new ToolExecutionResult { Content = [], Control = new ToolControl { Handoff = "one" } };
        }));
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool("later", "Finishes first", async (_, _) =>
        {
            await Task.CompletedTask;
            return new ToolExecutionResult { Content = [], Control = new ToolControl { Handoff = "two" } };
        }));
        var round = new AssistantMessage(
            [new ToolCallContent("c1", "hold", new Dictionary<string, object?>()),
             new ToolCallContent("c2", "later", new Dictionary<string, object?>())])
        { StopReason = StopReason.ToolUse, Model = "faux-1", Provider = "faux", UsageStats = new Pi.Ai.Types.Usage(0, 0) };
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(round), HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("follow-up")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submitTask = root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("a")] }, Context.Background);
            await Task.Delay(1200);
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context.Background);
            string dump = "none";
            if (live is not null && live.TryGetValue("tools", out var tools) && tools is IReadOnlyList<object?> list)
                dump = string.Join(" | ", list.OfType<IReadOnlyDictionary<string, object?>>().Select(s => $"{s.GetValueOrDefault("name")}={s.GetValueOrDefault("status")}"));
            Assert.Fail($"LIVE.TOOLS: {dump}");
        }
        finally { await HarnessTestSupport.CloseQuietlyAsync(harness); }
    }
}
