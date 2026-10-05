using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// 「简单选项」公共解析。对应 TS <c>api/simple-options.ts</c>：
/// buildBaseOptions（公共字段装配）、resolveSamplingParams（模型级/档位级/请求级合并）、
/// clampMaxTokensToContext（按上下文窗口收敛）、思考预算辅助（token 型 provider 用）。
/// </summary>
public static class SimpleOptions
{
    private const int ContextSafetyTokens = 4096;
    private const int MinMaxTokens = 1;

    /// <summary>思考预算共享响应上限时给答案预留的 token。对应 TS <c>MIN_ANSWER_TOKENS</c>。</summary>
    public const int MinAnswerTokens = 1024;

    /// <summary>默认思考预算。对应 TS <c>DEFAULT_THINKING_BUDGETS</c>。</summary>
    public static readonly ThinkingBudgets DefaultThinkingBudgets = new(1024, 2048, 8192, 16384);

    /// <summary>按上下文窗口收敛 maxTokens。对应 TS <c>clampMaxTokensToContext</c>。</summary>
    public static int ClampMaxTokensToContext(ModelSpec model, TranscriptContext context, int maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(MinMaxTokens, maxTokens);
        var available = model.ContextWindow - Pi.Ai.Utils.Estimate.EstimateContextTokens(context).Tokens - ContextSafetyTokens;
        return Math.Min(maxTokens, (int)Math.Max(MinMaxTokens, available));
    }

    /// <summary>
    /// 采样参数合并：请求级 &gt; 档位级 &gt; 模型级（TS 展开序一致）。
    /// 对应 TS <c>resolveSamplingParams</c>。
    /// </summary>
    public static JsonObject? ResolveSamplingParams(ModelSpec model, string thinkingLevel, JsonObject? requestParams)
    {
        var effectiveLevel = ThinkingLevels.Clamp(model, thinkingLevel);
        var levelParams = model.SamplingParamsByThinkingLevel is { } byLevel
            && byLevel.TryGetValue(effectiveLevel, out var levelOverride)
            ? levelOverride
            : null;
        if (model.SamplingParams is null && levelParams is null && requestParams is null) return null;

        var merged = new JsonObject();
        foreach (var (key, value) in model.SamplingParams ?? []) merged[key] = value?.DeepClone();
        foreach (var (key, value) in levelParams ?? []) merged[key] = value?.DeepClone();
        foreach (var (key, value) in requestParams ?? []) merged[key] = value?.DeepClone();
        return merged;
    }

    /// <summary>推理档位归一：xhigh/max 在通用档位上降为 high。对应 TS <c>clampReasoning</c>。</summary>
    public static string? ClampReasoning(string? effort)
        => effort is "xhigh" or "max" ? "high" : effort;

    /// <summary>档位对应 token 预算。对应 TS <c>thinkingBudgetForLevel</c>。</summary>
    public static long ThinkingBudgetForLevel(string? reasoningLevel, ThinkingBudgets? customBudgets = null)
    {
        var level = ClampReasoning(reasoningLevel) ?? "off";
        return level switch
        {
            "minimal" => customBudgets?.Minimal ?? DefaultThinkingBudgets.Minimal ?? 1024,
            "low" => customBudgets?.Low ?? DefaultThinkingBudgets.Low ?? 2048,
            "medium" => customBudgets?.Medium ?? DefaultThinkingBudgets.Medium ?? 8192,
            "high" => customBudgets?.High ?? DefaultThinkingBudgets.High ?? 16384,
            _ => 0,
        };
    }

    /// <summary>保证共享上限下答案至少剩 <see cref="MinAnswerTokens"/>。对应 TS <c>clampThinkingBudgetToAnswerRoom</c>。</summary>
    public static long ClampThinkingBudgetToAnswerRoom(long thinkingBudget, long ceiling)
        => Math.Min(thinkingBudget, Math.Max(0, ceiling - MinAnswerTokens));

    /// <summary>
    /// 为思考预算调整响应上限。对应 TS <c>adjustMaxTokensForThinking</c>：
    /// 无显式上限时用模型上限；否则 min(请求上限 + 预算, 模型上限)。
    /// </summary>
    public static (int MaxTokens, long ThinkingBudget) AdjustMaxTokensForThinking(
        int? baseMaxTokens, long modelMaxTokens, string? reasoningLevel, ThinkingBudgets? customBudgets = null)
    {
        var thinkingBudget = ThinkingBudgetForLevel(reasoningLevel, customBudgets);
        var maxTokens = baseMaxTokens is null
            ? modelMaxTokens
            : Math.Min(baseMaxTokens.Value + thinkingBudget, modelMaxTokens);

        if (maxTokens <= thinkingBudget)
        {
            thinkingBudget = ClampThinkingBudgetToAnswerRoom(thinkingBudget, maxTokens);
        }

        return ((int)maxTokens, thinkingBudget);
    }
}
