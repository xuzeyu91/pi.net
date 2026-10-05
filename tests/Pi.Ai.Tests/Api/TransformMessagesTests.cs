using Pi.Ai.Auth.OAuth;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>跨 provider 消息变换测试。对应 TS transform-messages 系列测试的核心语义。</summary>
public class TransformMessagesTests
{
    private static ModelSpec VisionModel() => new()
    {
        Id = "vision-1", Name = "Vision", Api = "openai-completions", Provider = "openai",
        BaseUrl = "https://api.openai.com/v1", Input = ["text", "image"],
    };

    private static ModelSpec TextOnlyModel() => new()
    {
        Id = "text-1", Name = "Text", Api = "openai-completions", Provider = "openai",
        BaseUrl = "https://api.openai.com/v1", Input = ["text"],
    };

    private static ModelSpec OtherProviderModel() => new()
    {
        Id = "claude-3", Name = "Claude", Api = "anthropic-messages", Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com", Input = ["text", "image"],
    };

    private static long Ts => 1_000;

    [Fact]
    public void DowngradesImagesForNonVisionModels()
    {
        var messages = new List<ChatMessage>
        {
            new UserMessage(
                [new TextContent("look"), new ImageContent("imgdata", "image/png"), new ImageContent("img2", "image/png")],
                Ts),
            new ToolResultMessage("call-1", "shot",
                [new ImageContent("toolimg", "image/png"), new TextContent("after")],
                IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, TextOnlyModel());

        var user = Assert.IsType<UserMessage>(result[0]);
        var userBlocks = user.Content.OfType<TextContent>().ToList();
        Assert.Equal(["look", "(image omitted: model does not support images)"],
            userBlocks.Select(t => t.Text).ToArray()); // 连续图片折叠成一个占位

        var toolResult = Assert.IsType<ToolResultMessage>(result[1]);
        Assert.Equal("(tool image omitted: model does not support images)",
            toolResult.Content.OfType<TextContent>().First().Text);
        Assert.Equal("after", toolResult.Content.OfType<TextContent>().Last().Text);
    }

    [Fact]
    public void KeepsImagesForVisionModels()
    {
        var messages = new List<ChatMessage>
        {
            new UserMessage([new ImageContent("imgdata", "image/png")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());
        Assert.IsType<ImageContent>(Assert.IsType<UserMessage>(result[0]).Content[0]);
    }

    [Fact]
    public void CrossModelDowngradesThinkingAndStripsSignatures()
    {
        var assistant = new AssistantMessage(
            [
                new ThinkingContent("secret reasoning", Signature: "sig-1"),
                new ThinkingContent("", Signature: "enc"),
                new ThinkingContent("plain thought"),
                new TextContent("answer"),
                new ToolCallContent("call|x|y", "read", null) { ThoughtSignature = "ts" },
            ],
            StopReason.ToolUse, Model: "claude-3", Api: "anthropic-messages", Provider: "anthropic",
            Timestamp: Ts);

        var result = TransformMessages.Transform([assistant], TextOnlyModel());

        var transformed = Assert.IsType<AssistantMessage>(result[0]);
        // 带签名的空思维块被保留？否——跨模型且签名保留仅限同模型。
        // 期望：空思维跳过；plain thought → text；text 规范化；thoughtSignature 剥离。
        // 跨模型：所有思维文本（含带签名的）都转为纯文本（签名丢失）。
        Assert.Equal(
            ["secret reasoning", "plain thought", "answer"],
            transformed.Content.OfType<TextContent>().Select(t => t.Text).ToArray());
        var toolCall = transformed.Content.OfType<ToolCallContent>().Single();
        Assert.Null(toolCall.ThoughtSignature);
        Assert.Equal(4, transformed.Content.Count); // text(secret) + text(plain) + text(answer) + toolCall
    }

    [Fact]
    public void DropsRedactedThinkingAcrossModelsAndKeepsForSameModel()
    {
        var redacted = new ThinkingContent("encrypted", Signature: null) { Redacted = true };
        var crossModel = TransformMessages.Transform(
            [new AssistantMessage([redacted], StopReason: Types.StopReason.Stop,
                Model: "a", Api: "x", Provider: "p", Timestamp: Ts)],
            VisionModel());
        Assert.Empty(crossModel.OfType<AssistantMessage>().First().Content.OfType<ThinkingContent>());

        var sameModel = TransformMessages.Transform(
            [new AssistantMessage([redacted], Model: "vision-1", Api: "openai-completions",
                Provider: "openai", Timestamp: Ts)],
            VisionModel());
        Assert.Single(sameModel.OfType<AssistantMessage>().First().Content.OfType<ThinkingContent>());
    }

    [Fact]
    public void NormalizesToolCallIdsAndRemapsResults()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage(
                [new ToolCallContent("long-id|with|pipes", "search", null)],
                StopReason.ToolUse, Model: "claude-3", Api: "anthropic-messages", Provider: "anthropic",
                Timestamp: Ts),
            new ToolResultMessage("long-id|with|pipes", "search",
                [new TextContent("found")], IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel(),
            (id, _model, _assistant) => $"norm-{id.Length}");

        var toolCall = Assert.IsType<AssistantMessage>(result[0]).ToolCalls.Single();
        Assert.Equal($"norm-{"long-id|with|pipes".Length}", toolCall.Id);
        var toolResult = Assert.IsType<ToolResultMessage>(result[1]);
        Assert.Equal(toolCall.Id, toolResult.ToolCallId); // 结果的 ID 同步归一化
    }

    [Fact]
    public void SkipsErroredAssistantTurns()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage([new TextContent("partial")], StopReason.Error,
                ErrorMessage: "boom", Timestamp: Ts),
            new UserMessage([new TextContent("retry")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());
        Assert.Single(result);
        Assert.IsType<UserMessage>(result[0]);
    }

    [Fact]
    public void SynthesizesResultsForOrphanedToolCalls()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage(
                [new ToolCallContent("call-1", "search", null), new ToolCallContent("call-2", "open", null)],
                StopReason.ToolUse, Timestamp: Ts),
            // 只有 call-1 有结果
            new ToolResultMessage("call-1", "search", [new TextContent("ok")], IsError: false, Timestamp: Ts),
            new UserMessage([new TextContent("next")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var synthetic = result.OfType<ToolResultMessage>().First(r => r.ToolCallId == "call-2");
        Assert.True(synthetic.IsError);
        Assert.Equal("No result provided", synthetic.Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public void HoldsSystemMessagesBetweenToolCallAndResult()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt"),
            new AssistantMessage([new ToolCallContent("call-1", "search", null)],
                StopReason.ToolUse, Timestamp: Ts),
            new SystemMessage("mid-turn instructions"), // 应被扣到结果之后
            new ToolResultMessage("call-1", "search", [new TextContent("ok")], IsError: false, Timestamp: Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var roles = result.Select(m => m.GetType().Name).ToList();
        Assert.Equal(["SystemMessage", "AssistantMessage", "ToolResultMessage", "SystemMessage"], roles);
    }

    [Fact]
    public void InterruptsPendingToolCallsOnUserTurn()
    {
        var messages = new List<ChatMessage>
        {
            new AssistantMessage([new ToolCallContent("call-1", "search", null)],
                StopReason.ToolUse, Timestamp: Ts),
            new UserMessage([new TextContent("stop that")], Ts),
        };

        var result = TransformMessages.Transform(messages, VisionModel());

        var synthetic = result.OfType<ToolResultMessage>().Single();
        Assert.Equal("call-1", synthetic.ToolCallId);
        Assert.True(synthetic.IsError);
        // user 在合成结果之后收尾。
        Assert.IsType<UserMessage>(result[^1]);
    }
}
