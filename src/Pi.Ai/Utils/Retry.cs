using System.Text.RegularExpressions;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 助手回合重试策略。对应 TS <c>RetryPolicy</c>（utils/retry.ts）；与
/// <see cref="ProviderRetry"/>（HTTP 状态驱动的请求级重试）不同，这里按**错误文案**
/// 判定可重试性，作用于整轮 assistant 调用。
/// </summary>
public sealed record RetryPolicy
{
    public bool Enabled { get; init; }

    /// <summary>最大重试次数（0 = 不重试）。首次调用不算重试。</summary>
    public int MaxRetries { get; init; }

    /// <summary>基础退避毫秒；第 n 次重试延迟 = <c>BaseDelayMs * 2^(n-1)</c>（抖动前）。</summary>
    public long BaseDelayMs { get; init; }

    /// <summary>agent 级重试延迟上限，缺省 60 秒。对应 TS <c>maxAgentDelayMs</c>。</summary>
    public long? MaxAgentDelayMs { get; init; }
}

/// <summary>
/// <see cref="Retry.AssistantCallAsync"/> 在各次重试前后发出的回调。
/// 对应 TS <c>RetryCallbacks</c>。
/// </summary>
public sealed record RetryCallbacks
{
    /// <summary>每次重试退避睡眠之前（attempt 从 1 起）。</summary>
    public Func<int, int, long, string, Task>? OnRetryScheduled { get; init; }

    /// <summary>退避睡眠之后、重试调用开始之前。</summary>
    public Func<Task>? OnRetryAttemptStart { get; init; }

    /// <summary>循环结束时恰好一次（后续调用正常完成则 success=true）。</summary>
    public Func<bool, int, string?, Task>? OnRetryFinished { get; init; }
}

/// <summary>退避睡眠被中止时抛出的内部异常。对应 TS <c>RetrySleepAbortError</c>。</summary>
internal sealed class RetrySleepAbortException : Exception
{
    public RetrySleepAbortException() : base("Aborted") { }
}

/// <summary>
/// 助手回合的有限重试循环。对应 TS <c>utils/retry.ts</c>：
/// <c>retryDelayMs</c> / <c>retryAssistantCall</c> / <c>isRetryableAssistantError</c>。
/// </summary>
public static class Retry
{
    /// <summary>agent 级重试延迟默认上限。对应 TS <c>DEFAULT_MAX_AGENT_RETRY_DELAY_MS</c>。</summary>
    public const long DefaultMaxAgentRetryDelayMs = 60_000;

    /// <summary>JS <c>Number.MAX_SAFE_INTEGER</c>（2^53-1）。</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991L;

    private static Regex BuildPattern(IEnumerable<string> patterns)
        => new(string.Join("|", patterns), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 订阅/账户限额类错误——不是瞬时限流，重试无意义。对应 TS
    /// <c>NON_RETRYABLE_PROVIDER_LIMIT_ERROR_PATTERN</c>。
    /// </summary>
    private static readonly Regex NonRetryableProviderLimitErrorPattern = BuildPattern(
    [
        // OpenCode Go/free-tier 限额以 429 JSON error type 返回，属订阅/账户限额而非瞬时限流。
        "GoUsageLimitError",
        "FreeUsageLimitError",

        // OpenCode Go 订阅限额文案（滚动/周/月限额用尽后提示启用余额）。
        "Monthly usage limit reached",
        "available balance",

        // 通用配额/预算/计费耗尽：insufficient_quota 是 OpenAI 的计费错误码。
        "insufficient_quota",
        "out of budget",
        "quota exceeded",
        "billing",

        // Sign in with ChatGPT：订阅共享用量限额，按小时而非秒重置。
        "subscription_sharing_usage_limit_exceeded",
    ]);

    /// <summary>
    /// 瞬时 provider/传输错误文案。对应 TS <c>RETRYABLE_PROVIDER_ERROR_PATTERN</c>。
    /// </summary>
    private static readonly Regex RetryableProviderErrorPattern = BuildPattern(
    [
        // 通用 provider 负载、HTTP 状态与服务端瞬时故障。
        "overloaded",
        "currently experiencing high demand",
        "model is at capacity",
        "rate.?limit",
        "too many requests",
        "429",
        "500",
        "502",
        "503",
        "504",
        "520",
        "524",
        "service.?unavailable",
        "server.?error",
        "internal.?error",

        // 上游瞬时故障的包装文案（含 OpenRouter "Provider returned error"）。
        "provider.?returned.?error",
        "exceeded request buffer limit while retrying upstream",

        // 网络/代理/fetch 传输故障（含 Codex raw-fetch 的 upstream connect 等）。
        "network.?error",
        "connection.?error",
        "connection.?refused",
        "connection.?lost",
        "other side closed",
        "fetch failed",
        "getaddrinfo",
        "ENOTFOUND",
        "EAI_AGAIN",
        "upstream.?connect",
        "reset before headers",
        "socket hang up",
        "socket connection was closed",
        "timed? out",
        "timeout",
        "terminated",

        // WebSocket 传输可能报 close/error 文案而非 HTTP/fetch 文案。
        "websocket.?closed",
        "websocket.?error",

        // 过早的流结束（Anthropic / Bedrock / Smithy HTTP2 无响应）。
        "ended without",
        "stream ended before message_stop",
        "stream ended before a terminal response event",
        "http2 request did not get a response",

        // provider 要求的重试延迟上限失败，应走外层重试策略（便于调用方中止退避）。
        "retry delay",

        // OpenAI Responses / Bedrock 流异常中的显式重试指引。
        "you can retry your request",
        "try your request again",
        "please retry your request",

        // gRPC 系 provider（如 NVIDIA NIM）。
        "ResourceExhausted",

        // Sign in with ChatGPT：用量/用户数据暂时不可用（可能中途到达且无 503）。
        "subscription_sharing_usage_unavailable",
        "subscription_sharing_user_unavailable",
    ]);

    /// <summary>
    /// 第 <paramref name="attempt"/> 次重试的退避毫秒。对应 TS <c>retryDelayMs</c>：
    /// <c>BaseDelayMs * 2^(attempt-1)</c>，超出 JS safe integer 时按
    /// <c>Number.MAX_SAFE_INTEGER</c> 处理，最后以 <c>MaxAgentDelayMs</c>（缺省 60s）封顶。
    /// </summary>
    public static long RetryDelayMs(RetryPolicy policy, int attempt)
    {
        var delay = policy.BaseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var safeDelay = delay <= MaxSafeInteger && delay >= -MaxSafeInteger
            ? (long)delay
            : MaxSafeInteger;
        return Math.Min(safeDelay, policy.MaxAgentDelayMs ?? DefaultMaxAgentRetryDelayMs);
    }

    /// <summary>
    /// 判定失败消息是否像瞬时 provider/传输错误（供调用方决定是否重启该助手回合）。
    /// 不实现重试策略本身。对应 TS <c>isRetryableAssistantError</c>。
    /// </summary>
    public static bool IsRetryableAssistantError(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Error || string.IsNullOrEmpty(message.ErrorMessage)) return false;
        var errorMessage = message.ErrorMessage;
        if (NonRetryableProviderLimitErrorPattern.IsMatch(errorMessage)) return false;
        return RetryableProviderErrorPattern.IsMatch(errorMessage);
    }

    /// <summary>
    /// 带有限重试地运行单次助手调用。对应 TS <c>retryAssistantCall</c>：
    /// 成功/中止立即返回（中止永不重试，但若已排过重试则报告失败）；不可重试错误立即返回；
    /// 否则按指数退避重试至 <c>MaxRetries</c>，期间发出三个回调。退避期间的中止会被归一为
    /// <c>StopReason=Aborted</c> 的助手消息。
    /// </summary>
    public static async Task<AssistantMessage> AssistantCallAsync(
        Func<Task<AssistantMessage>> produce,
        RetryPolicy? policy,
        CancellationToken signal = default,
        RetryCallbacks? callbacks = null)
    {
        var maxAttempts = policy is { Enabled: true } ? policy.MaxRetries : 0;

        var attempt = 0;
        (int Attempt, string ErrorMessage)? lastRetry = null;
        while (true)
        {
            var response = await produce().ConfigureAwait(false);

            // 中止：终态但不算成功；中止消息永不重试。
            if (response.StopReason == StopReason.Aborted)
            {
                if (lastRetry is { } abortedRetry && callbacks?.OnRetryFinished is { } onAbortedFinished)
                {
                    await onAbortedFinished(false, abortedRetry.Attempt, null).ConfigureAwait(false);
                }
                return response;
            }

            // 成功：非 error、非 abort 原样返回。
            if (response.StopReason != StopReason.Error)
            {
                if (lastRetry is { } okRetry && callbacks?.OnRetryFinished is { } onOkFinished)
                {
                    await onOkFinished(true, okRetry.Attempt, null).ConfigureAwait(false);
                }
                return response;
            }

            // 不可重试或预算耗尽：返回最终错误消息。
            if (attempt >= maxAttempts || !IsRetryableAssistantError(response))
            {
                if (lastRetry is { } finalRetry && callbacks?.OnRetryFinished is { } onFinalFinished)
                {
                    await onFinalFinished(false, finalRetry.Attempt, response.ErrorMessage).ConfigureAwait(false);
                }
                return response;
            }

            attempt++;
            lastRetry = (attempt, string.IsNullOrEmpty(response.ErrorMessage) ? "Unknown error" : response.ErrorMessage);
            var delayMs = RetryDelayMs(policy!, attempt);
            if (callbacks?.OnRetryScheduled is { } onScheduled)
            {
                await onScheduled(attempt, maxAttempts, delayMs, lastRetry.Value.ErrorMessage).ConfigureAwait(false);
            }

            // 退避期间的中止归一为与 provider 流中止同形状的助手消息。
            try
            {
                await SleepAsync(delayMs, signal).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (callbacks?.OnRetryFinished is { } onSleepFailed)
                {
                    await onSleepFailed(false, attempt, lastRetry.Value.ErrorMessage).ConfigureAwait(false);
                }
                if (error is RetrySleepAbortException)
                {
                    return response with { StopReason = StopReason.Aborted, ErrorMessage = null };
                }
                throw;
            }

            if (callbacks?.OnRetryAttemptStart is { } onStart)
            {
                await onStart().ConfigureAwait(false);
            }
        }
    }

    private static async Task SleepAsync(long ms, CancellationToken signal)
    {
        if (signal.IsCancellationRequested) throw new RetrySleepAbortException();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ms), signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new RetrySleepAbortException();
        }
    }
}
