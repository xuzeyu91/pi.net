using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Agent.Types;
using Xunit;

namespace Pi.Agent.Tests;

/// <summary>
/// Agent 类（低层循环的状态封装）行为测试：状态归约、队列、订阅与运行控制。
/// </summary>
public class AgentTests : IDisposable
{
    private readonly Agent _agent;

    public AgentTests()
    {
        _agent = new Agent(new AgentOptions
        {
            SystemPrompt = "你是测试助手",
            StreamFn = Faux.CreateStreamFn([Faux.Response.Text("回复")]),
            Model = Faux.DefaultModel,
        });
    }

    public void Dispose()
    {
        _agent.Abort();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PromptAppendsMessagesAndUpdatesState()
    {
        var run = _agent.Prompt("你好");
        await run;

        // 初始 system 基线 + user + assistant。
        Assert.Equal(3, _agent.Messages.Count);
        Assert.IsType<SystemMessage>(_agent.Messages[0]);
        Assert.Equal("你是测试助手", _agent.SystemPrompt);
        var assistant = Assert.IsType<AssistantMessage>(_agent.Messages[2]);
        Assert.Equal("回复", ((TextContent)assistant.Content[0]).Text);
        Assert.False(_agent.IsStreaming);
        Assert.Null(_agent.ErrorMessage);
    }

    [Fact]
    public async Task SubscribeReceivesLifecycleEvents()
    {
        var received = new List<AgentEvent>();
        using var subscription = _agent.Subscribe((@event, _) =>
        {
            received.Add(@event);
            return Task.CompletedTask;
        });

        await _agent.Prompt("你好");

        Assert.Contains(received, e => e is AgentEvent.AgentStart);
        Assert.Contains(received, e => e is AgentEvent.TurnStart);
        Assert.Contains(received, e => e is AgentEvent.MessageStart);
        Assert.Contains(received, e => e is AgentEvent.AgentEnd);
    }

    [Fact]
    public async Task SteerQueuesMessageForMidRunInjection()
    {
        var script = Faux.CreateStreamFn([Faux.Response.Text("一"), Faux.Response.Text("二")]);
        var agent = new Agent(new AgentOptions { StreamFn = script, Model = Faux.DefaultModel });

        var firstTurnDone = new TaskCompletionSource();
        agent.Subscribe((@event, _) =>
        {
            // 第一个助手回合结束后投递 steering 消息。
            if (@event is AgentEvent.TurnEnd { Message: AssistantMessage { StopReason: StopReason.Stop } }
                && agent.Messages.Count(m => m is AssistantMessage) == 1)
            {
                agent.Steer(Messages.UserText("插入指令"));
                firstTurnDone.TrySetResult();
            }
            return Task.CompletedTask;
        });

        await agent.Prompt("开始");
        Assert.True(firstTurnDone.Task.IsCompleted);
        // 等待第二次运行结束——run 是一个整体（agent_end 后 idle）。
        await agent.WaitForIdle();

        // steer 在运行中途注入：最终消息 = user + assistant(一) + steer + assistant(二)。
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal("二", ((TextContent)((AssistantMessage)agent.Messages[3]).Content[0]).Text);
    }

    [Fact]
    public async Task FollowUpRunsAfterAgentWouldStop()
    {
        var script = Faux.CreateStreamFn([Faux.Response.Text("一"), Faux.Response.Text("二")]);
        var agent = new Agent(new AgentOptions { StreamFn = script, Model = Faux.DefaultModel });
        agent.FollowUp(Messages.UserText("跟进"));

        await agent.Prompt("开始");
        await agent.WaitForIdle();

        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal("二", ((TextContent)((AssistantMessage)agent.Messages[3]).Content[0]).Text);
    }

    [Fact]
    public async Task ConcurrentPromptThrows()
    {
        // 用一个永不结束的脚本让第一次运行保持活动（等待取消）。
        var agent = new Agent(new AgentOptions
        {
            StreamFn = (_, _, _, ct) =>
            {
                var stream = new Pi.Ai.Stream.AssistantMessageEventStream();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException) { }
                }, CancellationToken.None);
                return Task.FromResult<Pi.Ai.Types.IAssistantMessageEventStream>(stream);
            },
            Model = Faux.DefaultModel,
        });

        var first = agent.Prompt("第一条");
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Prompt("第二条"));
        agent.Abort();
        await first;
    }

    [Fact]
    public void ResetKeepsSystemBaseline()
    {
        _agent.Reset();
        Assert.Single(_agent.Messages);
        Assert.IsType<SystemMessage>(_agent.Messages[0]);
        Assert.False(_agent.HasQueuedMessages());
    }
}
