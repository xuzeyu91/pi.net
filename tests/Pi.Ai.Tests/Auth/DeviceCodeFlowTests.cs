using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>设备码轮询循环测试。对应 TS oauth-device-code.test.ts（fake timers → 注入休眠）。</summary>
public class DeviceCodeFlowTests
{
    /// <summary>假时钟：sleep 推进时间。</summary>
    private sealed class FakeClock
    {
        public long Now { get; private set; }

        public List<int> Sleeps { get; } = [];

        public Task SleepAsync(int ms, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested);
            Sleeps.Add(ms);
            Now += ms;
            return Task.CompletedTask;
        }
    }

    private static DeviceCodePollOptions<T> Options<T>(
        FakeClock clock,
        Func<Task<DeviceCodePollResult<T>>> poll,
        int? intervalSeconds = null,
        int? expiresInSeconds = null,
        bool waitBeforeFirstPoll = false,
        CancellationToken signal = default)
        => new()
        {
            IntervalSeconds = intervalSeconds,
            ExpiresInSeconds = expiresInSeconds,
            WaitBeforeFirstPoll = waitBeforeFirstPoll,
            Poll = poll,
            Signal = signal,
            SleepAsync = clock.SleepAsync,
        };

    [Fact]
    public async Task PollsImmediatelyAndReturnsCompletedValue()
    {
        var clock = new FakeClock();
        var pollTimes = new List<long>();
        var polls = 0;

        var result = await DeviceCodeFlow.PollAsync(Options<string>(
            clock,
            poll: () =>
            {
                pollTimes.Add(clock.Now);
                polls++;
                return Task.FromResult<DeviceCodePollResult<string>>(
                    polls == 1
                        ? new DeviceCodePollResult<string>.Pending()
                        : new DeviceCodePollResult<string>.Complete("token"));
            },
            intervalSeconds: 2,
            expiresInSeconds: 30));

        Assert.Equal("token", result);
        Assert.Equal([0, 2000], pollTimes);
        Assert.Equal([2000], clock.Sleeps);
    }

    [Fact]
    public async Task CanWaitBeforeFirstPoll()
    {
        var clock = new FakeClock();
        var pollTimes = new List<long>();

        var result = await DeviceCodeFlow.PollAsync(Options<string>(
            clock,
            poll: () =>
            {
                pollTimes.Add(clock.Now);
                return Task.FromResult<DeviceCodePollResult<string>>(
                    new DeviceCodePollResult<string>.Complete("token"));
            },
            intervalSeconds: 2,
            expiresInSeconds: 30,
            waitBeforeFirstPoll: true));

        Assert.Equal("token", result);
        Assert.Equal([2000], pollTimes);
        Assert.Equal([2000], clock.Sleeps);
    }

    [Fact]
    public async Task IncreasesIntervalByFiveSecondsAfterSlowDownWithoutServerInterval()
    {
        var clock = new FakeClock();
        var pollTimes = new List<long>();
        var results = new Queue<DeviceCodePollResult<string>>([
            new DeviceCodePollResult<string>.SlowDown(),
            new DeviceCodePollResult<string>.Complete("token"),
        ]);

        var result = await DeviceCodeFlow.PollAsync(Options<string>(
            clock,
            poll: () =>
            {
                pollTimes.Add(clock.Now);
                return Task.FromResult(results.Dequeue());
            },
            intervalSeconds: 2,
            expiresInSeconds: 900));

        Assert.Equal("token", result);
        Assert.Equal([0, 7000], pollTimes); // 2s + 5s slow_down 增量
    }

    [Fact]
    public async Task HonorsServerProvidedSlowDownInterval()
    {
        var clock = new FakeClock();
        var pollTimes = new List<long>();
        var results = new Queue<DeviceCodePollResult<string>>([
            new DeviceCodePollResult<string>.SlowDown(30),
            new DeviceCodePollResult<string>.Complete("token"),
        ]);

        var result = await DeviceCodeFlow.PollAsync(Options<string>(
            clock,
            poll: () =>
            {
                pollTimes.Add(clock.Now);
                return Task.FromResult(results.Dequeue());
            },
            intervalSeconds: 2,
            expiresInSeconds: 900));

        Assert.Equal("token", result);
        Assert.Equal([0, 30000], pollTimes); // 采用服务器下发间隔
    }

    [Fact]
    public async Task FailsWithMessageFromPollResult()
    {
        var clock = new FakeClock();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeviceCodeFlow.PollAsync(Options<string>(
                clock,
                poll: () => Task.FromResult<DeviceCodePollResult<string>>(
                    new DeviceCodePollResult<string>.Failed("denied")),
                intervalSeconds: 2,
                expiresInSeconds: 30)));
        Assert.Equal("denied", error.Message);
    }

    [Fact]
    public async Task TimesOutWithSlowDownSpecificMessage()
    {
        var clock = new FakeClock();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            DeviceCodeFlow.PollAsync(Options<string>(
                clock,
                poll: () => Task.FromResult<DeviceCodePollResult<string>>(
                    new DeviceCodePollResult<string>.SlowDown()),
                intervalSeconds: 2,
                expiresInSeconds: 4)));
        Assert.Contains("slow_down", error.Message);
    }

    [Fact]
    public async Task TimesOutWithPlainMessageWhenNoSlowDown()
    {
        var clock = new FakeClock();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            DeviceCodeFlow.PollAsync(Options<string>(
                clock,
                poll: () => Task.FromResult<DeviceCodePollResult<string>>(
                    new DeviceCodePollResult<string>.Pending()),
                intervalSeconds: 2,
                expiresInSeconds: 4)));
        Assert.Equal("Device flow timed out", error.Message);
    }

    [Fact]
    public async Task CancelsInFlightWait()
    {
        // 真实可中止休眠：取消发生在进行中的等待里。
        var cts = new CancellationTokenSource();
        var task = DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<string>
        {
            IntervalSeconds = 1,
            ExpiresInSeconds = 30,
            Signal = cts.Token,
            Poll = () => Task.FromResult<DeviceCodePollResult<string>>(
                new DeviceCodePollResult<string>.Pending()),
        });
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Contains("Login cancelled", error.Message);
    }
}
