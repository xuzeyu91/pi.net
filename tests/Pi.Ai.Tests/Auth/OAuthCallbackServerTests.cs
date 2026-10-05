using System.Net.Sockets;
using System.Net;
using System.Net.Http;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>OAuth 回调服务器测试。对应 TS oauth-callback-server.test.ts。</summary>
[Collection("loopback-callback")] // 共享 loopback 监听器，避免并行争端口
public class OAuthCallbackServerTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly List<IDisposable> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers) server.Dispose();
        _http.Dispose();
    }

    private OAuthCallbackServer<string> Start(
        Func<string, Task<string>>? complete = null,
        string? state = "expected-state",
        string? redirectHost = null,
        CancellationToken signal = default,
        int? timeoutMs = null)
    {
        var server = OAuthCallbackServer<string>.Start(new OAuthCallbackServerOptions<string>
        {
            ProviderName = "Example",
            Host = "127.0.0.1",
            Port = 0,
            Path = "/callback",
            State = state,
            Complete = complete ?? (code => Task.FromResult($"completed:{code}")),
            Signal = signal,
            TimeoutMs = timeoutMs,
            RedirectHost = redirectHost,
        });
        _servers.Add(server);
        return server;
    }

    private static string CallbackUrl(string redirectUri, params (string Key, string Value)[] pairs)
    {
        var builder = new UriBuilder(redirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
        foreach (var (key, value) in pairs) query[key] = value;
        builder.Query = query.ToString();
        return builder.ToString();
    }

    private record Page(HttpStatusCode Status, string? ContentType, string Body);

    private async Task<Page> GetPage(string url)
    {
        var response = await _http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return new Page(response.StatusCode, response.Content.Headers.ContentType?.ToString(), body);
    }

    [Fact]
    public async Task IgnoresStrayRequestsAndResolvesWithCompletedCode()
    {
        var server = Start();
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/callback$", server.RedirectUri);

        var wrongPath = await GetPage(new Uri(new Uri(server.RedirectUri), "/other").ToString());
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.Status);

        var wrongState = await GetPage(CallbackUrl(server.RedirectUri, ("code", "c"), ("state", "other")));
        Assert.Equal(HttpStatusCode.BadRequest, wrongState.Status);
        Assert.Equal("text/html; charset=utf-8", wrongState.ContentType);
        Assert.Contains("State mismatch.", wrongState.Body);

        var post = await _http.PostAsync(CallbackUrl(server.RedirectUri, ("code", "c"), ("state", "expected-state")), null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);

        var missingCode = await GetPage(CallbackUrl(server.RedirectUri, ("state", "expected-state")));
        Assert.Equal(HttpStatusCode.BadRequest, missingCode.Status);

        var success = await GetPage(CallbackUrl(server.RedirectUri, ("code", "the-code"), ("state", "expected-state")));
        Assert.Equal(HttpStatusCode.OK, success.Status);
        Assert.Equal("text/html; charset=utf-8", success.ContentType);
        Assert.Contains("Authentication successful", success.Body);
        Assert.Contains("Signed in to Example.", success.Body);
        Assert.Contains("fill=\"#F09082\"", success.Body);
        Assert.Contains("fill=\"#4D9ABF\"", success.Body);
        Assert.Contains("fill=\"#F1BE58\"", success.Body);

        Assert.Equal("completed:the-code", await server.WaitAsync());
    }

    [Fact]
    public async Task UsesRedirectHostAndSkipsStateCheckWhenNoneExpected()
    {
        var server = Start(state: null, redirectHost: "localhost");
        Assert.Matches(@"^http://localhost:\d+/callback$", server.RedirectUri);
        var response = await GetPage(CallbackUrl(server.RedirectUri, ("code", "no-state")));
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("completed:no-state", await server.WaitAsync());
    }

    [Fact]
    public async Task ShowsCompletionFailuresOnPageAndRejectsWait()
    {
        var server = Start(_ => throw new InvalidOperationException("token exchange failed"));
        var failure = await GetPage(CallbackUrl(server.RedirectUri, ("code", "c"), ("state", "expected-state")));
        Assert.Equal(HttpStatusCode.BadGateway, failure.Status);
        Assert.Contains("Example sign-in failed.", failure.Body);
        Assert.Contains("token exchange failed", failure.Body);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync());
        Assert.Contains("token exchange failed", error.Message);
    }

    [Fact]
    public async Task RejectsWaitWhenProviderRedirectsWithError()
    {
        var server = Start();
        var failure = await GetPage(CallbackUrl(server.RedirectUri,
            ("error", "access_denied"), ("error_description", "User denied access"), ("state", "expected-state")));
        Assert.Equal(HttpStatusCode.BadRequest, failure.Status);
        Assert.Contains("User denied access", failure.Body);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.WaitAsync());
        Assert.Equal("Example authorization failed: User denied access", error.Message);
    }

    [Fact]
    public async Task CompletesOnlyTheFirstCallback()
    {
        var exchangeStarted = new TaskCompletionSource();
        var finishExchange = new TaskCompletionSource<string>();
        var server = Start(_ =>
        {
            exchangeStarted.TrySetResult();
            return finishExchange.Task;
        });

        var url = CallbackUrl(server.RedirectUri, ("code", "c"), ("state", "expected-state"));
        var first = _http.GetAsync(url);
        await exchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await GetPage(url);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);

        // 已 claim 的回调在调用方切换到手动输入后仍继续完成。
        server.Cancel();
        finishExchange.TrySetResult("done");
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal("done", await server.WaitAsync());
    }

    [Fact]
    public async Task ResolvesWithUndefinedAfterCancel()
    {
        var server = Start();
        server.Cancel();
        Assert.Null(await server.WaitAsync());
        var late = await GetPage(CallbackUrl(server.RedirectUri, ("code", "c"), ("state", "expected-state")));
        Assert.Equal(HttpStatusCode.Conflict, late.Status);
    }

    [Fact]
    public async Task RejectsWaitOnAbortAndOnTimeout()
    {
        var cts = new CancellationTokenSource();
        var aborted = Start(signal: cts.Token);
        cts.Cancel();
        var abortError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted.WaitAsync());
        Assert.Contains("Login cancelled", abortError.Message);

        var timedOut = Start(timeoutMs: 10);
        var timeoutError = await Assert.ThrowsAsync<InvalidOperationException>(() => timedOut.WaitAsync());
        Assert.Contains("Example sign-in timed out", timeoutError.Message);

        var alreadyAborted = new CancellationTokenSource();
        alreadyAborted.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Start(signal: alreadyAborted.Token));
    }

    [Fact]
    public async Task FailsInsteadOfPickingAnotherPortWhenRequestedPortIsTaken()
    {
        var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        blocker.Listen(1);
        try
        {
            var takenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            Assert.ThrowsAny<Exception>(() => StartWithPort(takenPort));
        }
        finally
        {
            blocker.Dispose();
        }
    }

    private static OAuthCallbackServer<string> StartWithPort(int port)
        => OAuthCallbackServer<string>.Start(new OAuthCallbackServerOptions<string>
        {
            ProviderName = "Example",
            Host = "127.0.0.1",
            Port = port,
            Path = "/callback",
            State = "expected-state",
            Complete = code => Task.FromResult(code),
        });
}

/// <summary>waitForCallbackOrManualInput 竞速测试。</summary>
[Collection("loopback-callback")]
public class WaitForCallbackOrManualInputTests : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    private static OAuthCallbackServer<string> Start()
        => OAuthCallbackServer<string>.Start(new OAuthCallbackServerOptions<string>
        {
            ProviderName = "Example",
            Host = "127.0.0.1",
            Port = 0,
            Path = "/callback",
            Complete = code => Task.FromResult(code),
        });

    private static FakeInteraction Interaction(Func<AuthPrompt, CancellationToken, Task<string>> promptHandler)
        => new() { PromptHandler = promptHandler };

    [Fact]
    public async Task ReturnsBrowserCallbackAndAbortsManualPrompt()
    {
        var server = Start();
        try
        {
            var manualOpened = new TaskCompletionSource();
            CancellationToken observedPromptSignal = default;
            var interaction = new FakeInteraction
            {
                PromptHandler = async (_, promptSignal) =>
                {
                    // 挂起的手动 prompt：回调先到时应被中止。
                    observedPromptSignal = promptSignal;
                    manualOpened.TrySetResult();
                    await Task.Delay(Timeout.Infinite, promptSignal);
                    return "";
                },
            };
            interaction.PromptObserved = manualOpened;

            var resultTask = OAuthCallbackFlow.WaitForCallbackOrManualInput(
                interaction.ToInteraction(), server,
                new ManualPrompt("paste", server.RedirectUri));

            await manualOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _http.GetAsync($"{server.RedirectUri}?code=from-browser");
            var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(5));

            var callback = Assert.IsType<CallbackOrManualInput<string>.Callback>(result);
            Assert.Equal("from-browser", callback.Value);
            // 回调胜出后，手动 prompt 的取消令牌应已被中止。
            Assert.True(observedPromptSignal.IsCancellationRequested);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ReturnsPastedInputAndStopsWaitingForBrowser()
    {
        var server = Start();
        try
        {
            var interaction = Interaction(async (_, _) => "pasted");
            var result = await OAuthCallbackFlow.WaitForCallbackOrManualInput(
                interaction.ToInteraction(), server,
                new ManualPrompt("paste", server.RedirectUri));
            var manual = Assert.IsType<CallbackOrManualInput<string>.Manual>(result);
            Assert.Equal("pasted", manual.Input);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task UsesOnlyManualPromptWithoutCallbackServer()
    {
        var interaction = Interaction(async (_, _) => "pasted");
        var result = await OAuthCallbackFlow.WaitForCallbackOrManualInput<string>(
            interaction.ToInteraction(), null,
            new ManualPrompt("paste", "http://localhost/callback"));
        var manual = Assert.IsType<CallbackOrManualInput<string>.Manual>(result);
        Assert.Equal("pasted", manual.Input);
    }

    [Fact]
    public async Task PropagatesManualPromptFailures()
    {
        var server = Start();
        try
        {
            var interaction = Interaction((_, _) => throw new InvalidOperationException("prompt cancelled"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OAuthCallbackFlow.WaitForCallbackOrManualInput(
                    interaction.ToInteraction(), server,
                    new ManualPrompt("paste", server.RedirectUri)));
            Assert.Equal("prompt cancelled", error.Message);
        }
        finally
        {
            server.Dispose();
        }
    }
}
