namespace Pi.Ai.Utils;

/// <summary>重试选项。对应 TS <c>ProviderRetryOptions</c>（utils/provider-retry.ts）。</summary>
public sealed record ProviderRetryOptions
{
    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>
/// 复刻 OpenAI/Anthropic SDK 的重试策略，但退避等待可被请求的取消令牌打断。
/// 对应 TS <c>retryProviderRequest</c>：x-should-retry 显式控制 → 无 status 可重试 →
/// 408/409/429/5xx；retry-after-ms / retry-after（秒或 HTTP 日期）优先，指数退避
/// 兜底（0.5·2^n 秒，封顶 8 秒，±25% 抖动）；服务器要求的延迟超过
/// <see cref="ProviderRetryOptions.MaxRetryDelayMs"/>（默认 60 秒）立即失败。
/// </summary>
public static class ProviderRetry
{
    private const int DefaultMaxRetryDelayMs = 60_000;

    /// <summary>镜像 OpenAI/Anthropic SDK 重试判定；升级 SDK 时需复查。</summary>
    private static bool IsRetryable(ProviderHttpException error)
    {
        string? shouldRetry = null;
        if (error.Headers is not null)
        {
            shouldRetry = error.Headers.TryGetValue("x-should-retry", out var value) ? value : null;
        }
        if (shouldRetry == "true") return true;
        if (shouldRetry == "false") return false;

        return error.Status is 408 or 409 or 429 || error.Status >= 500;
    }

    private static long ValidateServerRetryDelayMs(
        long delayMs, int? maxRetryDelayMs, string providerErrorMessage)
    {
        var maxDelay = maxRetryDelayMs ?? DefaultMaxRetryDelayMs;
        if (maxDelay > 0 && delayMs > maxDelay)
        {
            throw new InvalidOperationException(
                $"Server requested {(delayMs + 999) / 1000}s retry delay (max: {(maxDelay + 999) / 1000}s). {providerErrorMessage}");
        }
        return delayMs;
    }

    private static long GetRetryDelayMs(ProviderHttpException error, int retryIndex, int? maxRetryDelayMs)
    {
        if (error.Headers?.TryGetValue("retry-after-ms", out var retryAfterMs) == true && retryAfterMs is not null)
        {
            if (double.TryParse(retryAfterMs, System.Globalization.CultureInfo.InvariantCulture, out var ms)
                && !double.IsNaN(ms) && !double.IsInfinity(ms))
            {
                return ValidateServerRetryDelayMs((long)ms, maxRetryDelayMs, error.Message);
            }
        }

        if (error.Headers?.TryGetValue("retry-after", out var retryAfter) == true && retryAfter is not null)
        {
            long delayMs;
            if (double.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && !double.IsNaN(seconds) && !double.IsInfinity(seconds))
            {
                delayMs = (long)(seconds * 1000);
            }
            else if (DateTimeOffset.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                         out var retryDate))
            {
                delayMs = (long)((retryDate - DateTimeOffset.UtcNow).TotalMilliseconds);
            }
            else
            {
                delayMs = -1;
            }
            if (delayMs >= 0 && !double.IsNaN(delayMs) && !double.IsInfinity(delayMs))
            {
                return ValidateServerRetryDelayMs(delayMs, maxRetryDelayMs, error.Message);
            }
        }

        var exponentialDelayMs = (long)Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000;
        return (long)(exponentialDelayMs * (1 - Random.Shared.NextDouble() * 0.25));
    }

    private static Exception CreateAbortError(CancellationToken signal)
        => new OperationCanceledException("Request aborted", signal);

    private static async Task AbortableSleepAsync(long ms, CancellationToken signal)
    {
        if (signal.IsCancellationRequested) throw CreateAbortError(signal);
        try
        {
            await Task.Delay((int)Math.Max(0, ms), signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw CreateAbortError(signal);
        }
    }

    /// <summary>带重试的请求执行。请求委托每次重试都重新构造（对齐 SDK 全新请求语义）。</summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> request, ProviderRetryOptions? options = null)
    {
        var maxRetries = options?.MaxRetries ?? 0;
        var retriesRemaining = maxRetries;

        while (true)
        {
            try
            {
                // 每次重试都是全新请求，计数头保持为零。
                return await request().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (options is { Signal.CanBeCanceled: true } && options.Signal.IsCancellationRequested)
                    throw CreateAbortError(options.Signal);
                if (error is not ProviderHttpException providerError
                    || retriesRemaining <= 0
                    || !IsRetryable(providerError))
                    throw;

                var retryIndex = maxRetries - retriesRemaining;
                retriesRemaining--;
                await AbortableSleepAsync(
                    GetRetryDelayMs(providerError, retryIndex, options?.MaxRetryDelayMs),
                    options?.Signal ?? default).ConfigureAwait(false);
            }
        }
    }
}
