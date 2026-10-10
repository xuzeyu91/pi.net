using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Ai.Tests.Auth;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>P29：estimate/uuid/provider-env/simple-options 测试。</summary>
public class EstimateAndOptionsTests
{
    private static ModelSpec Model(
        bool reasoning = true,
        long contextWindow = 100_000,
        long maxTokens = 8192,
        ThinkingLevelMap? levelMap = null,
        JsonObject? samplingParams = null)
        => new()
        {
            Id = "test-model", Name = "Test", Api = "openai-responses", Provider = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Reasoning = reasoning, ContextWindow = contextWindow, MaxTokens = maxTokens,
            ThinkingLevelMap = levelMap, SamplingParams = samplingParams,
        };

    private static TranscriptContext Context(params ChatMessage[] messages) => new(messages);

    [Fact]
    public void EstimateContextTokensSumsMessages()
    {
        var context = Context(
            new SystemMessage("be brief", Timestamp: 0),
            // 字符 → token 用 TS 的 ceil(len / 3.5)。
            new UserMessage([new TextContent("hello world")], 1), // 11 chars → 4 tokens
            new AssistantMessage(
                [new TextContent("hi there")], StopReason.Stop, Timestamp: 2,  // 8 chars → 3 tokens
                UsageStats: new Usage(10, 5)));
        var estimate = Estimate.EstimateContextTokens(context);
        Assert.Equal(15, estimate.UsageTokens);
        Assert.Equal(0, estimate.TrailingTokens);
        Assert.Equal(2, estimate.LastUsageIndex);
    }

    [Fact]
    public void EstimateFallsBackToPerMessageWhenNoUsage()
    {
        var context = Context(
            new UserMessage([new TextContent("abcd")], 1)); // 4 chars → ceil(4/3.5) = 2 tokens
        var estimate = Estimate.EstimateContextTokens(context);
        Assert.Equal(2, estimate.Tokens);
        Assert.Null(estimate.LastUsageIndex);
    }

    [Fact]
    public void EstimateSkipsAbortedAssistantUsage()
    {
        var context = Context(
            new UserMessage([new TextContent("abcd")], 1),
            new AssistantMessage([], StopReason.Aborted, Timestamp: 2, UsageStats: new Usage(999, 999)));
        var estimate = Estimate.EstimateContextTokens(context);
        Assert.Null(estimate.LastUsageIndex);
        // "abcd" → ceil(4/3.5) = 2；被中止的助手用量不参与。
        Assert.Equal(2, estimate.Tokens);
    }

    [Fact]
    public void UuidV7IsMonotonicAndWellFormed()
    {
        var first = Uuid.Uuidv7();
        var second = Uuid.Uuidv7();
        Assert.NotEqual(first, second);
        Assert.Matches(
            "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", first);
        // 同毫秒序列推进：字符串比较保证时间有序。
        Assert.True(string.CompareOrdinal(first, second) < 0 || second[..8] != first[..8]);
    }

    [Fact]
    public void UuidV7PreservesExplicitTimestamp()
    {
        var id = Uuid.Uuidv7(1_234_567_890_123);
        // 首六字节为毫秒时间戳大端。
        var hex = id.Replace("-", "");
        Assert.Equal("011f71fb04cb", hex[..12]);
    }

    [Fact]
    public void GetSupportedThinkingLevelsRespectsMap()
    {
        var open = Model(levelMap: null);
        Assert.Equal(["off", "minimal", "low", "medium", "high"], ThinkingLevels.GetSupported(open));

        // off: null → off 被禁用；xhigh/max 缺失 → 不可用。
        var withOffNull = Model(levelMap: ThinkingLevelMap.FromJsonObject(
            new JsonObject { ["off"] = null }));
        Assert.Equal(["minimal", "low", "medium", "high"], ThinkingLevels.GetSupported(withOffNull));

        var withXhigh = Model(levelMap: ThinkingLevelMap.FromJsonObject(
            new JsonObject { ["off"] = null, ["xhigh"] = "xhigh" }));
        Assert.Equal(["minimal", "low", "medium", "high", "xhigh"], ThinkingLevels.GetSupported(withXhigh));

        var nonReasoning = Model(reasoning: false);
        Assert.Equal(["off"], ThinkingLevels.GetSupported(nonReasoning));
    }

    [Fact]
    public void ClampThinkingLevelMovesUpThenDown()
    {
        var model = Model(levelMap: ThinkingLevelMap.FromJsonObject(
            new JsonObject { ["off"] = null, ["xhigh"] = "xhigh" }));
        Assert.Equal("minimal", ThinkingLevels.Clamp(model, "off"));
        Assert.Equal("xhigh", ThinkingLevels.Clamp(model, "xhigh"));
        Assert.Equal("xhigh", ThinkingLevels.Clamp(model, "max"));
        Assert.Equal("high", ThinkingLevels.Clamp(model, "high"));
    }

    [Fact]
    public void ClampMaxTokensToContextKeepsAnswerRoom()
    {
        var model = Model(contextWindow: 1000);
        var context = Context(new UserMessage([new TextContent(new string('a', 400))], 1));
        // available = 1000 - 100 - 4096 → 负数 → 下限 1
        Assert.Equal(1, SimpleOptions.ClampMaxTokensToContext(model, context, 50));

        var big = Model(contextWindow: 100_000);
        Assert.Equal(50, SimpleOptions.ClampMaxTokensToContext(big, context, 50));
    }

    [Fact]
    public void ResolveSamplingParamsMergesModelLevelThenRequestLevel()
    {
        var model = Model(samplingParams: new JsonObject { ["top_p"] = 0.9, ["seed"] = 1 });
        var merged = SimpleOptions.ResolveSamplingParams(
            model, "off", new JsonObject { ["seed"] = 2, ["top_k"] = 5 });
        Assert.NotNull(merged);
        Assert.Equal(0.9, merged!.Num("top_p"));
        Assert.Equal(2, merged.Num("seed")); // 请求级覆盖模型级
        Assert.Equal(5, merged.Num("top_k"));
    }

    [Fact]
    public void AdjustMaxTokensForThinkingFitsBudget()
    {
        // maxTokens = min(1000 + 8192, 8192) = 8192；maxTokens <= budget → 预算收敛到 8192-1024。
        var (maxTokens, budget) = SimpleOptions.AdjustMaxTokensForThinking(1000, 8192, "medium");
        Assert.Equal(7168, budget);
        Assert.Equal(8192, maxTokens);
        var (tightMax, tightBudget) = SimpleOptions.AdjustMaxTokensForThinking(4, 8192, "high");
        Assert.Equal(tightMax, tightBudget + SimpleOptions.MinAnswerTokens);
    }
}
