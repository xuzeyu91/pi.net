using System.Text.RegularExpressions;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 上下文溢出识别。对应 TS <c>utils/overflow.ts</c>：按各 provider 的错误文案识别
/// 输入超窗，并覆盖「静默溢出」与「length 截断溢出」两种非报错形态。
/// </summary>
public static class Overflow
{
    /// <summary>
    /// 各 provider 的超窗错误文案（含注释里的示例）。对应 TS <c>OVERFLOW_PATTERNS</c>。
    /// </summary>
    private static readonly Regex[] OverflowPatterns =
    [
        new(@"prompt (?:is )?too long", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Anthropic / z.ai
        new(@"prompt exceeds max length", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // z.ai CN
        new(@"request_too_large", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Anthropic 413 字节超限
        new(@"input is too long for requested model", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Bedrock
        new(@"exceeds the context window", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // OpenAI
        new(@"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // OpenAI 兼容代理（LiteLLM）
        new(@"input token count.*exceeds the maximum", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Google
        new(@"maximum prompt length is \d+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // xAI
        new(@"reduce the length of the messages", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Groq
        new(@"maximum context length is \d+ tokens", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // OpenRouter
        new(@"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // OpenRouter/Poolside
        new(@"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Together AI
        new(@"exceeds the limit of \d+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // GitHub Copilot
        new(@"exceeds the available context size", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // llama.cpp
        new(@"greater than the context length", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // LM Studio
        new(@"context window exceeds limit", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // MiniMax
        new(@"exceeded model token limit", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Kimi For Coding
        new(@"too large for model with \d+ maximum context length", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Mistral
        new(@"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // DS4
        new(@"model_context_window_exceeded", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // z.ai finish_reason
        new(@"prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Ollama
        new(@"range of input length should be", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // DashScope/Qwen
        new(@"context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // 通用兜底
        new(@"too many tokens", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // 通用兜底
        new(@"token limit exceeded", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // 通用兜底
    ];

    /// <summary>Cerebras 无 body 的 400/413 超窗。对应 TS <c>CEREBRAS_BODYLESS_OVERFLOW_PATTERN</c>。</summary>
    private static readonly Regex CerebrasBodylessOverflowPattern =
        new(@"^4(?:00|13)\s*(?:status code)?\s*\(no body\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 非溢出错误（限流/服务不可用）。命中的消息即便同时匹配溢出文案也排除。
    /// 对应 TS <c>NON_OVERFLOW_PATTERNS</c>（Bedrock 的 ThrottlingException 会命中 /too many tokens/i）。
    /// </summary>
    private static readonly Regex[] NonOverflowPatterns =
    [
        new(@"^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), // Bedrock
        new(@"rate limit", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
        new(@"too many requests", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
    ];

    /// <summary>
    /// 消息是否代表上下文溢出。对应 TS <c>isContextOverflow</c>：三种形态——
    /// ①错误文案命中；②静默溢出（stop 但 usage.input 超窗）；③length 截断溢出
    /// （Xiaomi MiMo 风格：length + output=0 且输入填满窗口）。
    /// </summary>
    public static bool IsContextOverflow(AssistantMessage message, long? contextWindow = null)
    {
        // 形态 1：错误文案。
        if (message.StopReason == StopReason.Error && !string.IsNullOrEmpty(message.ErrorMessage))
        {
            var errorMessage = message.ErrorMessage;
            var isNonOverflow = NonOverflowPatterns.Any(pattern => pattern.IsMatch(errorMessage));
            if (!isNonOverflow)
            {
                if (OverflowPatterns.Any(pattern => pattern.IsMatch(errorMessage))) return true;
                if (message.Provider == "cerebras" && CerebrasBodylessOverflowPattern.IsMatch(errorMessage))
                {
                    return true;
                }
            }
        }

        var inputTokens = (message.UsageStats?.Input ?? 0) + (message.UsageStats?.CacheRead ?? 0);

        // 形态 2：静默溢出（成功但输入超窗）。
        if (contextWindow is > 0 && message.StopReason == StopReason.Stop && inputTokens > contextWindow.Value)
        {
            return true;
        }

        // 形态 3：length 截断溢出（输入填满窗口、无输出余量）。
        if (contextWindow is > 0 && message.StopReason == StopReason.Length && (message.UsageStats?.Output ?? 0) == 0
            && inputTokens >= contextWindow.Value * 0.99)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// length 停止是否低于调用方或模型期望的输出上限（可能由上下文压力或 provider 截断导致，
    /// 调用方可做一次有界的压缩重试）。<paramref name="desiredMaxOutput"/> 必须是
    /// 任何上下文收敛之前的原始上限。对应 TS <c>isRecoverableLength</c>。
    /// </summary>
    public static bool IsRecoverableLength(AssistantMessage message, long desiredMaxOutput)
        => message.StopReason == StopReason.Length
            && desiredMaxOutput > 0
            && (message.UsageStats?.Output ?? 0) < desiredMaxOutput;

    /// <summary>供测试使用的溢出模式表。对应 TS <c>getOverflowPatterns</c>。</summary>
    public static IReadOnlyList<Regex> GetOverflowPatterns() => OverflowPatterns;
}
