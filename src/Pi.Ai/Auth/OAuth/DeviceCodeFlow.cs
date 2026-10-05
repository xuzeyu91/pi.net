namespace Pi.Ai.Auth.OAuth;

/// <summary>设备码轮询单步结果（判别联合）。对应 TS <c>OAuthDeviceCodePollResult&lt;T&gt;</c>。</summary>
public abstract record DeviceCodePollResult<T>
{
    /// <summary>用户尚未完成授权。对应 TS <c>{ status: "pending" }</c>。</summary>
    public sealed record Pending() : DeviceCodePollResult<T>;

    /// <summary>要求放慢轮询（可带服务器下发的最小间隔秒数）。对应 TS <c>{ status: "slow_down" }</c>。</summary>
    public sealed record SlowDown(int? IntervalSeconds = null) : DeviceCodePollResult<T>;

    /// <summary>不可恢复失败。对应 TS <c>{ status: "failed" }</c>。</summary>
    public sealed record Failed(string Message) : DeviceCodePollResult<T>;

    /// <summary>完成。对应 TS <c>{ status: "complete", value }</c>。</summary>
    public sealed record Complete(T Value) : DeviceCodePollResult<T>;
}

/// <summary>设备码轮询选项。对应 TS <c>OAuthDeviceCodePollOptions&lt;T&gt;</c>。</summary>
public sealed record DeviceCodePollOptions<T>
{
    /// <summary>初始轮询间隔（秒）。</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>设备码有效期（秒）；超时抛错。</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>首次轮询前是否先等待一个间隔。</summary>
    public bool WaitBeforeFirstPoll { get; init; }

    /// <summary>单步轮询委托。</summary>
    public required Func<Task<DeviceCodePollResult<T>>> Poll { get; init; }

    public CancellationToken Signal { get; init; }

    /// <summary>休眠实现（测试注入用）；默认可中止的真实休眠。</summary>
    internal Func<int, CancellationToken, Task>? SleepAsync { get; init; }

    /// <summary>时钟（测试注入用）；默认 Environment.TickCount64（毫秒）。</summary>
    internal Func<long>? Now { get; init; }
}

/// <summary>
/// RFC 8628 设备授权流轮询循环。对应 TS <c>pollOAuthDeviceCodeFlow</c>
/// （auth/oauth/device-code.ts）：pending 继续、slow_down 增间隔（服务器下发
/// 优先，否则 +5s）、failed 直接抛错、超时报错区分是否经历过 slow_down。
/// </summary>
public static class DeviceCodeFlow
{
    private const string CancelMessage = "Login cancelled";
    private const string TimeoutMessage = "Device flow timed out";
    private const string SlowDownTimeoutMessage =
        "Device flow timed out after one or more slow_down responses. This is often caused by clock drift in WSL or VM environments. Please sync or restart the VM clock and try again.";
    private const int MinimumIntervalMs = 1000;

    /// <summary>进程级默认休眠覆盖（测试用；options.SleepAsync 未设时生效）。</summary>
    internal static Func<int, CancellationToken, Task>? DefaultSleepAsync { get; set; }

    // RFC 8628 §3.2：授权服务器省略 interval 时客户端必须用 5 秒。
    private const int DefaultPollIntervalSeconds = 5;

    // RFC 8628 §3.5：slow_down 表示轮询间隔必须增加 5 秒。
    private const int SlowDownIntervalIncrementMs = 5000;

    /// <summary>可中止休眠。对应 TS <c>abortableSleep</c>（取消时抛 CancelMessage）。</summary>
    public static async Task AbortableSleepAsync(int ms, CancellationToken signal)
    {
        if (signal.IsCancellationRequested) throw new OperationCanceledException(CancelMessage, signal);
        try
        {
            await Task.Delay(ms, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(CancelMessage, signal);
        }
    }

    public static async Task<T> PollAsync<T>(DeviceCodePollOptions<T> options)
    {
        var sleep = options.SleepAsync ?? DeviceCodeFlow.DefaultSleepAsync ?? AbortableSleepAsync;
        var clockNow = options.Now ?? new Func<long>(() => Environment.TickCount64);
        var deadline = options.ExpiresInSeconds is { } expires
            ? clockNow() + expires * 1000L
            : long.MaxValue;
        var intervalMs = Math.Max(
            MinimumIntervalMs,
            (options.IntervalSeconds ?? DefaultPollIntervalSeconds) * 1000);

        var slowDownResponses = 0;
        if (options.WaitBeforeFirstPoll)
        {
            var remainingMs = deadline - clockNow();
            if (remainingMs > 0)
            {
                await sleep((int)Math.Min(intervalMs, remainingMs), options.Signal).ConfigureAwait(false);
            }
        }

        while (clockNow() < deadline)
        {
            if (options.Signal.IsCancellationRequested)
            {
                throw new OperationCanceledException(CancelMessage, options.Signal);
            }

            var result = await options.Poll().ConfigureAwait(false);
            switch (result)
            {
                case DeviceCodePollResult<T>.Complete complete:
                    return complete.Value;
                case DeviceCodePollResult<T>.Failed failed:
                    throw new InvalidOperationException(failed.Message);
                case DeviceCodePollResult<T>.SlowDown slowDown:
                    slowDownResponses++;
                    // 服务器下发 interval 时直接采用（GitHub 会报告新的最小值）；仅靠客户端
                    // 计数在 WSL/VM 时钟漂移下可能永远提前轮询。否则按 RFC 8628 §3.5 加 5 秒。
                    intervalMs = slowDown.IntervalSeconds is { } serverInterval && serverInterval > 0
                        ? Math.Max(MinimumIntervalMs, serverInterval * 1000)
                        : Math.Max(MinimumIntervalMs, intervalMs + SlowDownIntervalIncrementMs);
                    break;
            }

            var remaining = deadline - clockNow();
            if (remaining <= 0) break;

            await sleep((int)Math.Min(intervalMs, remaining), options.Signal).ConfigureAwait(false);
        }

        throw new TimeoutException(slowDownResponses > 0 ? SlowDownTimeoutMessage : TimeoutMessage);
    }
}
