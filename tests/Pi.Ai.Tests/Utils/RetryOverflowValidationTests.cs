using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>utils/retry.ts 测试（助手回合重试策略与可重试性判定）。</summary>
public class RetryTests
{
    private static AssistantMessage Message(StopReason reason, string? errorMessage = null)
        => new([], reason, ErrorMessage: errorMessage, UsageStats: new Usage(1, 1));

    [Fact]
    public void RetryDelayGrowsExponentiallyAndCaps()
    {
        var policy = new RetryPolicy { Enabled = true, MaxRetries = 5, BaseDelayMs = 1000 };
        Assert.Equal(1000, Retry.RetryDelayMs(policy, 1));
        Assert.Equal(2000, Retry.RetryDelayMs(policy, 2));
        Assert.Equal(4000, Retry.RetryDelayMs(policy, 3));
        // 默认上限 60s。
        Assert.Equal(60_000, Retry.RetryDelayMs(policy, 10));
        // 显式上限。
        Assert.Equal(500, Retry.RetryDelayMs(policy with { MaxAgentDelayMs = 500 }, 4));
    }

    [Fact]
    public void RetryableClassification()
    {
        Assert.True(Retry.IsRetryableAssistantError(Message(StopReason.Error, "429 Too Many Requests")));
        Assert.True(Retry.IsRetryableAssistantError(Message(StopReason.Error, "Connection refused")));
        Assert.True(Retry.IsRetryableAssistantError(Message(StopReason.Error, "Anthropic stream ended before message_stop")));
        // 订阅/计费限额不可重试。
        Assert.False(Retry.IsRetryableAssistantError(Message(StopReason.Error, "insufficient_quota")));
        Assert.False(Retry.IsRetryableAssistantError(Message(StopReason.Error, "GoUsageLimitError")));
        // 非错误回合不可重试。
        Assert.False(Retry.IsRetryableAssistantError(Message(StopReason.Stop)));
        Assert.False(Retry.IsRetryableAssistantError(Message(StopReason.Error)));
    }

    [Fact]
    public async Task RetriesUntilSuccessAndReportsCallbacks()
    {
        var attempts = 0;
        var scheduled = new List<int>();
        var finished = new List<(bool Success, int Attempt)>();
        var message = await Retry.AssistantCallAsync(
            () =>
            {
                attempts++;
                return Task.FromResult(attempts < 3
                    ? Message(StopReason.Error, "503 service unavailable")
                    : Message(StopReason.Stop));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 3, BaseDelayMs = 1 },
            callbacks: new RetryCallbacks
            {
                OnRetryScheduled = (attempt, _, _, _) =>
                {
                    scheduled.Add(attempt);
                    return Task.CompletedTask;
                },
                OnRetryFinished = (success, attempt, _) =>
                {
                    finished.Add((success, attempt));
                    return Task.CompletedTask;
                },
            });

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal(3, attempts);
        Assert.Equal([1, 2], scheduled);
        Assert.Equal([(true, 2)], finished);
    }

    [Fact]
    public async Task NonRetryableErrorReturnsImmediately()
    {
        var attempts = 0;
        var message = await Retry.AssistantCallAsync(
            () =>
            {
                attempts++;
                return Task.FromResult(Message(StopReason.Error, "quota exceeded"));
            },
            new RetryPolicy { Enabled = true, MaxRetries = 5, BaseDelayMs = 1 });

        Assert.Equal(1, attempts);
        Assert.Equal("quota exceeded", message.ErrorMessage);
    }

    [Fact]
    public async Task DisabledPolicyNeverRetries()
    {
        var attempts = 0;
        await Retry.AssistantCallAsync(
            () =>
            {
                attempts++;
                return Task.FromResult(Message(StopReason.Error, "429 too many requests"));
            },
            new RetryPolicy { Enabled = false, MaxRetries = 5, BaseDelayMs = 1 });
        Assert.Equal(1, attempts);
    }
}

/// <summary>utils/overflow.ts 测试（上下文溢出识别）。</summary>
public class OverflowTests
{
    private static AssistantMessage Message(StopReason reason, string? errorMessage = null,
        long input = 0, long cacheRead = 0, long output = 0, string? provider = null)
        => new([], reason, ErrorMessage: errorMessage,
            UsageStats: new Usage(input, output, cacheRead, 0), Provider: provider);

    [Fact]
    public void DetectsProviderOverflowMessages()
    {
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Error, "prompt is too long: 213462 tokens > 200000 maximum")));
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Error, "Your input exceeds the context window of this model")));
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Error, "maximum context length is 131072 tokens")));
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Error, "Range of input length should be [1, 4096]")));
        Assert.False(Overflow.IsContextOverflow(Message(StopReason.Error, "some unrelated failure")));
    }

    [Fact]
    public void NonOverflowPatternsWinOverOverflowPatterns()
    {
        // Bedrock 把限流格式化成 "ThrottlingException: Too many tokens..."，不应判为溢出。
        Assert.False(Overflow.IsContextOverflow(
            Message(StopReason.Error, "Throttling error: Too many tokens, please wait before trying again.")));
        Assert.False(Overflow.IsContextOverflow(Message(StopReason.Error, "rate limit exceeded")));
    }

    [Fact]
    public void CerebrasBodylessOverflowRequiresProvider()
    {
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Error, "400 (no body)", provider: "cerebras")));
        Assert.False(Overflow.IsContextOverflow(Message(StopReason.Error, "400 (no body)", provider: "other")));
    }

    [Fact]
    public void DetectsSilentAndLengthOverflow()
    {
        // 静默溢出：成功但输入超窗。
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Stop, input: 200_000), 128_000));
        Assert.False(Overflow.IsContextOverflow(Message(StopReason.Stop, input: 1_000), 128_000));

        // length 截断溢出：输入填满窗口且无输出。
        Assert.True(Overflow.IsContextOverflow(Message(StopReason.Length, input: 127_000, output: 0), 128_000));
        Assert.False(Overflow.IsContextOverflow(Message(StopReason.Length, input: 127_000, output: 10), 128_000));
    }

    [Fact]
    public void RecoverableLengthNeedsShortOutput()
    {
        Assert.True(Overflow.IsRecoverableLength(Message(StopReason.Length, output: 10), 4096));
        Assert.False(Overflow.IsRecoverableLength(Message(StopReason.Length, output: 4096), 4096));
        Assert.False(Overflow.IsRecoverableLength(Message(StopReason.Stop, output: 0), 4096));
    }
}

/// <summary>utils/validation.ts 测试（工具参数校验与强制转换）。</summary>
public class ValidationTests
{
    private static ToolDefinition ReadTool() => new(
        "read",
        "Read a file",
        new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["count"] = new Dictionary<string, object?> { ["type"] = "number" },
                ["note"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new List<object?> { "path" },
            ["additionalProperties"] = false,
        }));

    [Fact]
    public void ValidatesAndCoercesArguments()
    {
        var result = Validation.ValidateToolArguments(ReadTool(),
            new ToolCallContent("call-1", "read", new JsonObject { ["path"] = "/tmp/a.txt", ["count"] = "3" }));

        Assert.NotNull(result);
        Assert.Equal("/tmp/a.txt", result!["path"]!.GetValue<string>());
        // "3" 被强制为数字。
        Assert.Equal(JsonValueKind.Number, result["count"]!.GetValueKind());
    }

    [Fact]
    public void DropsOptionalNulls()
    {
        var result = Validation.ValidateToolArguments(ReadTool(),
            new ToolCallContent("call-1", "read", new JsonObject { ["path"] = "/x", ["note"] = null }));
        Assert.NotNull(result);
        Assert.False(result!.AsObject().ContainsKey("note"));
    }

    [Fact]
    public void ReportsMissingRequiredPropertyWithPath()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Validation.ValidateToolArguments(
            ReadTool(), new ToolCallContent("call-1", "read", new JsonObject { ["count"] = 1 })));
        Assert.Contains("Validation failed for tool \"read\"", error.Message);
        Assert.Contains("Expected required property path", error.Message);
        Assert.Contains("Received arguments:", error.Message);
    }

    [Fact]
    public void RejectsAdditionalProperties()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Validation.ValidateToolArguments(
            ReadTool(), new ToolCallContent("call-1", "read", new JsonObject { ["path"] = "/x", ["extra"] = 1 })));
        Assert.Contains("Expected no additional properties", error.Message);
    }

    [Fact]
    public void UnknownToolThrows()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Validation.ValidateToolCall(
            [ReadTool()], new ToolCallContent("call-1", "write", new JsonObject())));
        Assert.Contains("Tool \"write\" not found", error.Message);
    }
}
