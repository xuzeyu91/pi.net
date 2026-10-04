using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Agent.Types;
using Xunit;

namespace Pi.Agent.Tests;

/// <summary>
/// agent-loop 的端到端行为测试：以 Faux provider 脚本驱动完整循环，
/// 验证事件序列、工具执行、钩子与终止语义（对应 TS 侧 vitest 覆盖的核心路径）。
/// </summary>
public class AgentLoopTests
{
    /// <summary>收集全部事件的订阅器。</summary>
    private static async Task<List<AgentEvent>> Subscribe(EventStream<AgentEvent, IReadOnlyList<ChatMessage>> stream)
    {
        var events = new List<AgentEvent>();
        await foreach (var @event in stream) events.Add(@event);
        return events;
    }

    private static AgentLoopConfig CreateConfig(Model model) => new()
    {
        Model = model,
        ConvertToLlm = messages => Task.FromResult<IReadOnlyList<ChatMessage>>(messages),
        StreamOptions = new SimpleStreamOptions(),
    };

    private static AgentTool CreateEchoTool(string name = "echo", bool isError = false, bool terminate = false)
        => new(
            Name: name,
            Description: "回显输入的测试工具",
            Parameters: new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            Label: "Echo",
            Execute: (toolCallId, args, _, _) => Task.FromResult(new AgentToolResult(
                [new TextContent($"echo:{args}")],
                IsError: isError,
                Terminate: terminate)));

    [Fact]
    public async Task SimpleTextTurnEmitsFullEventSequence()
    {
        var streamFn = Faux.CreateStreamFn([Faux.Response.Text("你好")]);
        var context = new AgentContext { Messages = [] };
        var config = CreateConfig(Faux.DefaultModel);

        var stream = AgentLoop.Run(
            [Messages.UserText("hi")], context, config, CancellationToken.None, streamFn);
        var events = await Subscribe(stream);

        // 终值 = 本次运行的新消息：user prompt + assistant 回复（无工具时不插入声明消息）。
        var result = await stream.WaitForResultAsync();
        Assert.Equal(2, result.Count);
        Assert.IsType<UserMessage>(result[0]);
        var assistant = Assert.IsType<AssistantMessage>(result[1]);
        Assert.Equal("你好", ((TextContent)assistant.Content[0]).Text);
        Assert.Equal(StopReason.Stop, assistant.StopReason);

        // 事件序列骨架：agent_start, turn_start, [消息事件], turn_end, agent_end。
        Assert.IsType<AgentEvent.AgentStart>(events[0]);
        Assert.IsType<AgentEvent.TurnStart>(events[1]);
        Assert.IsType<AgentEvent.TurnEnd>(events[^2]);
        Assert.IsType<AgentEvent.AgentEnd>(events[^1]);
        // user 消息一对 start/end；assistant 消息以 start 开始、end 结束、中间是 update。
        Assert.IsType<AgentEvent.MessageStart>(events[2]);
        Assert.IsType<AgentEvent.MessageEnd>(events[3]);
        Assert.IsType<AgentEvent.MessageStart>(events[4]);
        Assert.IsType<AgentEvent.MessageEnd>(events[^3]);
        // 流式过程中有 message_update（faux 分块推送文本增量）。
        Assert.Contains(events, e => e is AgentEvent.MessageUpdate);
    }

    [Fact]
    public async Task ToolCallTurnExecutesToolAndContinues()
    {
        var streamFn = Faux.CreateStreamFn([
            Faux.Response.ToolUse(Faux.ToolCall("echo", new Dictionary<string, object?> { ["input"] = "x" }, id: "call-1")),
            Faux.Response.Text("done"),
        ]);
        var context = new AgentContext { Messages = [], Tools = [CreateEchoTool()] };
        var config = CreateConfig(Faux.DefaultModel) with { ToolExecution = ToolExecutionMode.Sequential };

        var stream = AgentLoop.Run(
            [Messages.UserText("use the tool")], context, config, CancellationToken.None, streamFn);
        var events = await Subscribe(stream);
        var result = await stream.WaitForResultAsync();

        // 新消息 = system(工具声明) + user + assistant(toolUse) + toolResult + assistant(done)。
        Assert.Equal(5, result.Count);
        Assert.IsType<SystemMessage>(result[0]); // declareToolChanges 在 user 之前插入声明消息
        var toolResult = Assert.IsType<ToolResultMessage>(result[3]);
        Assert.Equal("call-1", toolResult.ToolCallId);
        Assert.False(toolResult.IsError);

        // 工具执行事件齐全。
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionStart { ToolName: "echo" });
        Assert.Contains(events, e => e is AgentEvent.ToolExecutionEnd { ToolName: "echo", IsError: false });
        Assert.Contains(events, e => e is AgentEvent.TurnEnd { ToolResults.Count: 1 });
    }

    [Fact]
    public async Task BlockedToolCallProducesErrorResult()
    {
        var streamFn = Faux.CreateStreamFn([
            Faux.Response.ToolUse(Faux.ToolCall("echo", null, id: "call-1")),
            Faux.Response.Text("ok"),
        ]);
        var context = new AgentContext { Messages = [], Tools = [CreateEchoTool()] };
        var config = CreateConfig(Faux.DefaultModel) with
        {
            BeforeToolCall = (_, _) => Task.FromResult<BeforeToolCallResult?>(
                new BeforeToolCallResult(Block: true, Reason: "不允许执行")),
        };

        var stream = AgentLoop.Run(
            [Messages.UserText("go")], context, config, CancellationToken.None, streamFn);
        var events = await Subscribe(stream);
        var result = await stream.WaitForResultAsync();

        // user + system + assistant(toolUse) + toolResult(错误) + assistant(ok)。
        Assert.Equal(5, result.Count);
        var toolResult = Assert.IsType<ToolResultMessage>(result[3]);
        Assert.True(toolResult.IsError);
        Assert.Contains("不允许执行", ((TextContent)toolResult.Content[0]).Text);
        // 工具本体不应被执行（echo 工具若执行会返回非错误结果）。
        var endEvent = events.OfType<AgentEvent.ToolExecutionEnd>().Single();
        Assert.True(endEvent.IsError);
    }

    [Fact]
    public async Task ErrorStopReasonIsHardExit()
    {
        var streamFn = Faux.CreateStreamFn([
            new Faux.Response([], StopReason.Error, ErrorMessage: "boom"),
            Faux.Response.Text("unreachable"),
        ]);
        var context = new AgentContext { Messages = [] };
        var config = CreateConfig(Faux.DefaultModel);

        var stream = AgentLoop.Run(
            [Messages.UserText("hi")], context, config, CancellationToken.None, streamFn);
        var events = await Subscribe(stream);
        var result = await stream.WaitForResultAsync();

        // 只有 user + 失败的 assistant 消息；不再继续请求。
        Assert.Equal(2, result.Count);
        var assistant = Assert.IsType<AssistantMessage>(result[1]);
        Assert.Equal(StopReason.Error, assistant.StopReason);
        Assert.Equal("boom", assistant.ErrorMessage);
        // 只有一个 turn_start（错误后不再发起新回合）。
        Assert.Single(events.OfType<AgentEvent.TurnStart>());
    }

    [Fact]
    public async Task TruncatedToolCallsAllFail()
    {
        var streamFn = Faux.CreateStreamFn([
            new Faux.Response(
                [Faux.ToolCall("echo", null, id: "call-1"), Faux.ToolCall("echo", null, id: "call-2")],
                StopReason.Length),
            Faux.Response.Text("recovered"),
        ]);
        var context = new AgentContext { Messages = [], Tools = [CreateEchoTool()] };
        var config = CreateConfig(Faux.DefaultModel) with { ToolExecution = ToolExecutionMode.Parallel };

        var stream = AgentLoop.Run(
            [Messages.UserText("go")], context, config, CancellationToken.None, streamFn);
        var result = await stream.WaitForResultAsync();

        // user + system + assistant(截断) + 2x toolResult(错误) + assistant(恢复) = 6 条。
        Assert.Equal(6, result.Count);
        var first = Assert.IsType<ToolResultMessage>(result[3]);
        Assert.True(first.IsError);
        Assert.Contains("output token limit", ((TextContent)first.Content[0]).Text);
    }

    [Fact]
    public async Task SteeringMessagesInjectMidRun()
    {
        var streamFn = Faux.CreateStreamFn([
            Faux.Response.Text("first"),
            Faux.Response.Text("second"),
        ]);
        var context = new AgentContext { Messages = [] };
        var steeringDelivered = new List<ChatMessage> { Messages.UserText("steer") };
        var polled = false;
        var config = CreateConfig(Faux.DefaultModel) with
        {
            GetSteeringMessages = () =>
            {
                // 首次轮询（循环起点）返回空，模拟"用户稍后才输入"。
                if (!polled)
                {
                    polled = true;
                    return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
                }
                var copy = steeringDelivered.ToList();
                steeringDelivered.Clear();
                return Task.FromResult<IReadOnlyList<ChatMessage>>(copy);
            },
        };

        var stream = AgentLoop.Run(
            [Messages.UserText("start")], context, config, CancellationToken.None, streamFn);
        var result = await stream.WaitForResultAsync();

        // steering 消息在回合之间注入：user, assistant(first), steer(user), assistant(second)。
        Assert.Equal(4, result.Count);
        var steer = Assert.IsType<UserMessage>(result[2]);
        Assert.Equal("steer", ((TextContent)steer.Content[0]).Text);
    }

    [Fact]
    public void ContinueRejectsAssistantLastMessage()
    {
        var context = new AgentContext
        {
            Messages = [Messages.UserText("hi"), Messages.AssistantText("done")],
        };
        Assert.Throws<InvalidOperationException>(() =>
            AgentLoop.RunContinue(context, CreateConfig(Faux.DefaultModel), CancellationToken.None,
                Faux.CreateStreamFn([Faux.Response.Text("x")])));
    }
}
