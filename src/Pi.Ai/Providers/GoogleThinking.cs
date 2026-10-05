using System.Text.Json.Nodes;

namespace Pi.Ai.Providers;

/// <summary>Google thinking 档位。对应 TS <c>ResolvedGoogleThinkingLevel</c>。</summary>
public enum GoogleThinkingLevel
{
    Minimal,
    Low,
    Medium,
    High,
}

/// <summary>thinking budget 表。对应 TS <c>getGoogleBudget</c>（-1 = 动态）。</summary>
public static class GoogleThinking
{
    /// <summary>按模型 id 与档位取 thinking budget；thinkingBudgets 自定义优先。对应 TS <c>getGoogleBudget</c>。</summary>
    public static int GetBudget(string modelId, GoogleThinkingLevel level,
        IReadOnlyDictionary<string, int>? customBudgets = null)
    {
        if (customBudgets is not null
            && customBudgets.TryGetValue(level.ToString().ToLowerInvariant(), out var custom))
        {
            return custom;
        }
        var id = modelId.ToLowerInvariant();
        if (id.Contains("2.5-pro"))
        {
            return level switch
            {
                GoogleThinkingLevel.Minimal => 128,
                GoogleThinkingLevel.Low => 2048,
                GoogleThinkingLevel.Medium => 8192,
                GoogleThinkingLevel.High => 32768,
                _ => -1,
            };
        }
        if (id.Contains("2.5-flash-lite"))
        {
            return level switch
            {
                GoogleThinkingLevel.Minimal => 512,
                GoogleThinkingLevel.Low => 2048,
                GoogleThinkingLevel.Medium => 8192,
                GoogleThinkingLevel.High => 24576,
                _ => -1,
            };
        }
        if (id.Contains("2.5-flash"))
        {
            return level switch
            {
                GoogleThinkingLevel.Minimal => 128,
                GoogleThinkingLevel.Low => 2048,
                GoogleThinkingLevel.Medium => 8192,
                GoogleThinkingLevel.High => 24576,
                _ => -1,
            };
        }
        return -1; // 动态 thinking
    }

    /// <summary>
    /// 构造 generationConfig.thinkingConfig：disabled / budget / 离散 level。
    /// 对应 TS <c>getDisabledGoogleThinkingConfig</c> + <c>ThinkingConfig</c> 分支。
    /// </summary>
    public static JsonObject BuildConfig(string modelId, bool enabled,
        GoogleThinkingLevel level = GoogleThinkingLevel.Medium,
        IReadOnlyDictionary<string, int>? customBudgets = null)
    {
        if (!enabled)
        {
            return new JsonObject
            {
                ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 },
            };
        }
        // Gemini 2.5 系列用 budget；离散 thinkingLevel 控制是新版 wire 格式（逐模型启用）。
        var budget = GetBudget(modelId, level, customBudgets);
        return new JsonObject
        {
            ["thinkingConfig"] = budget >= 0
                ? new JsonObject { ["thinkingBudget"] = budget }
                : new JsonObject { ["thinkingBudget"] = -1 },
        };
    }
}

/// <summary>
/// 共享 provider 重试策略：408/409/429/5xx 带退避（尊重 retry-after）。
/// 对应 TS <c>retryProviderRequest</c> / <c>retryGoogleRequest</c>。
/// </summary>
public static class RetryPolicy
{
    private static readonly int[] RetryableStatuses = [408, 409, 429];

    public static bool IsRetryable(int status)
        => RetryableStatuses.Contains(status) || status >= 500;

    /// <summary>带退避的重试：status 可重试时最多 maxRetries 次（retry-after 优先）。</summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> request, int maxRetries = 2, int maxRetryDelayMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            Exception failure;
            try
            {
                return await request().ConfigureAwait(false);
            }
            catch (ProviderHttpException error)
            {
                if (attempt >= maxRetries || !IsRetryable(error.Status)) throw;
                failure = error;
            }
            var delay = failure is ProviderHttpException { RetryAfterMs: { } retryAfter }
                ? Math.Min(retryAfter, maxRetryDelayMs)
                : Math.Min(500 * (int)Math.Pow(2, attempt), maxRetryDelayMs);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>带状态码的 HTTP 异常（重试策略依据）。对应 TS 里 SDK ApiError.status 的归一化。</summary>
public sealed class ProviderHttpException(int status, string message, int? retryAfterMs = null)
    : Exception(message)
{
    public int Status { get; } = status;

    public int? RetryAfterMs { get; } = retryAfterMs;
}
