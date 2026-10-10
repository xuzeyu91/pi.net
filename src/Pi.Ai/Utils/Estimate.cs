using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>上下文用量估算结果。对应 TS <c>ContextUsageEstimate</c>。</summary>
public sealed record ContextUsageEstimate(
    long Tokens,
    long UsageTokens,
    long TrailingTokens,
    int? LastUsageIndex);

/// <summary>
/// 上下文 token 估算（字符数/4 的粗估）。对应 TS <c>utils/estimate.ts</c>。
/// </summary>
public static class Estimate
{
    /// <summary>TS <c>CHARS_PER_TOKEN</c>（3.5，非整数——取整只发生在 <c>Math.ceil</c> 处）。</summary>
    private const double CharsPerToken = 3.5;

    private const int EstimatedImageChars = 4800;

    /// <summary>上下文占用：优先最近可用助手消息的用量，其余为尾部估算。对应 TS <c>calculateContextTokens</c>。</summary>
    /// <remarks>
    /// TS：<c>usage.totalTokens || input + output + cacheRead + cacheWrite</c>——provider 上报的
    /// totalTokens 为 0 或缺失时回退到全桶求和。各 provider 的 totalTokens 定义不一致
    /// （bedrock 用 input+output），故此处保留该回退而非直接求和。
    /// </remarks>
    public static long CalculateContextTokens(Usage usage)
        => usage.TotalTokens != 0
            ? usage.TotalTokens
            : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite;

    /// <summary>纯文本 token 估算。</summary>
    public static long EstimateTextTokens(string text)
        => (long)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>
    /// user/toolResult 内容块（文本/图片）token 估算。
    /// 对应 TS <c>estimateTextAndImageContentTokens(content: string | Array&lt;TextContent | ImageContent&gt;)</c>：
    /// 非文本块一律按图片计（<c>block.type === "text" ? text.length : 4800</c>），字符串入参按其长度计。
    /// </summary>
    public static long EstimateTextAndImageContentTokens(IReadOnlyList<ContentBlock> content)
    {
        long chars = 0;
        foreach (var block in content)
        {
            chars += block switch
            {
                TextContent text => (text.Text ?? "").Length,
                _ => EstimatedImageChars,
            };
        }
        return (long)Math.Ceiling(chars / CharsPerToken);
    }

    /// <summary>字符串入参的重载（TS 的联合入参在 C# 拆成两个重载）。</summary>
    public static long EstimateTextAndImageContentTokens(string content)
        => (long)Math.Ceiling(content.Length / CharsPerToken);

    /// <summary>单条消息 token 估算。对应 TS <c>estimateMessageTokens</c>。</summary>
    public static long EstimateMessageTokens(ChatMessage message)
    {
        switch (message)
        {
            case SystemMessage system:
            {
                var tokens = EstimateTextTokens(Text.GetSystemMessageText(system));
                tokens += EstimateToolsTokens(system.ToolsAdded);
                tokens += EstimateToolsTokens(system.ToolsRemoved);
                return tokens;
            }
            case UserMessage user:
                return EstimateTextAndImageContentTokens(user.Content);
            case ToolResultMessage toolResult:
                return EstimateTextAndImageContentTokens(toolResult.Content);
            case AssistantMessage assistant:
            {
                long chars = 0;
                foreach (var block in assistant.Content)
                {
                    chars += block switch
                    {
                        TextContent text => (text.Text ?? "").Length,
                        ThinkingContent thinking => thinking.Thinking.Length,
                        ToolCallContent toolCall => toolCall.Name.Length +
                            SafeJsonStringify(toolCall.Arguments).Length,
                        _ => 0,
                    };
                }
                return (long)Math.Ceiling(chars / (double)CharsPerToken);
            }
            default:
                return 0;
        }
    }

    /// <summary>整段上下文的用量估算。对应 TS <c>estimateContextTokens</c>。</summary>
    public static ContextUsageEstimate EstimateContextTokens(TranscriptContext context)
        => EstimateContextTokens(context.Messages);

    /// <summary>消息列表的用量估算。</summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<ChatMessage> messages)
    {
        // 最近可用助手用量：时间戳晚于它的前缀消息插入（如压缩摘要）会使该用量失效。
        long latestPrefixTimestamp = long.MinValue;
        Usage? latestUsage = null;
        int latestUsageIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is AssistantMessage assistant)
            {
                var appliesToPrefix = assistant.Timestamp >= latestPrefixTimestamp;
                if (appliesToPrefix
                    && assistant.StopReason is not (StopReason.Aborted or StopReason.Error)
                    && assistant.UsageStats is { } usage
                    && CalculateContextTokens(usage) > 0)
                {
                    latestUsage = usage;
                    latestUsageIndex = index;
                }
            }
            var timestamp = messageTimestamp(messages[index]);
            if (timestamp > latestPrefixTimestamp) latestPrefixTimestamp = timestamp;
        }

        if (latestUsage is not null && latestUsageIndex >= 0)
        {
            var usageTokens = CalculateContextTokens(latestUsage);
            long trailing = 0;
            for (var index = latestUsageIndex + 1; index < messages.Count; index++)
            {
                trailing += EstimateMessageTokens(messages[index]);
            }
            return new ContextUsageEstimate(usageTokens + trailing, usageTokens, trailing, latestUsageIndex);
        }

        long total = 0;
        foreach (var message in messages) total += EstimateMessageTokens(message);
        return new ContextUsageEstimate(total, 0, total, null);
    }

    private static long EstimateToolsTokens(IReadOnlyList<ToolDefinition>? tools)
        => tools is not { Count: > 0 } ? 0
            : EstimateTextTokens(SafeJsonStringify(tools));

    private static long EstimateToolsTokens(IReadOnlyList<string>? tools)
        => tools is not { Count: > 0 } ? 0
            : EstimateTextTokens(string.Join(",", tools));

    private static string SafeJsonStringify(object? value)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }
        catch
        {
            return "[unserializable]";
        }
    }

    private static long messageTimestamp(ChatMessage message) => message switch
    {
        SystemMessage system => system.Timestamp ?? 0,
        UserMessage user => user.Timestamp,
        AssistantMessage assistant => assistant.Timestamp ?? 0,
        ToolResultMessage toolResult => toolResult.Timestamp ?? 0,
        _ => 0,
    };
}
