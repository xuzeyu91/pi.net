using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Pi.Ai.Auth.OAuth;

/// <summary>回调服务器选项。对应 TS <c>OAuthCallbackServerOptions&lt;T&gt;</c>。</summary>
public sealed record OAuthCallbackServerOptions<T>
{
    /// <summary>浏览器页上的 provider 名（如 "OpenAI"）。</summary>
    public required string ProviderName { get; init; }

    /// <summary>监听地址。</summary>
    public required string Host { get; init; }

    /// <summary>监听端口；0 = 自动分配空闲端口。</summary>
    public required int Port { get; init; }

    public required string Path { get; init; }

    /// <summary>redirectUri 中的主机名（与监听地址不同时，如 localhost）。</summary>
    public string? RedirectHost { get; init; }

    /// <summary>期望的 <c>state</c> 参数；provider 不回传 state 时省略。</summary>
    public string? State { get; init; }

    /// <summary>
    /// 在浏览器页发出之前用收到的 code 完成登录，页面因此能显示交换失败。
    /// 传 <c>async code =&gt; code</c> 表示稍后再交换。
    /// </summary>
    public required Func<string, Task<T>> Complete { get; init; }

    public CancellationToken Signal { get; init; }

    /// <summary>等待超时（毫秒）。</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>loopback OAuth 回调服务器句柄。对应 TS <c>OAuthCallbackServer&lt;T&gt;</c>。</summary>
public sealed class OAuthCallbackServer<T> : IDisposable
{
    private readonly OAuthCallbackServerOptions<T> _options;
    private readonly HttpListener _listener;
    private readonly TaskCompletionSource<T?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource? _timeoutCts;

    private readonly object _gate = new();
    private bool _claimed;   // 已有回调进入完成流程
    private bool _settled;   // wait 已终态（完成/失败/取消/关闭）

    private OAuthCallbackServer(OAuthCallbackServerOptions<T> options, HttpListener listener, int boundPort)
    {
        _options = options;
        _listener = listener;
        var redirectHost = options.RedirectHost ?? options.Host;
        RedirectUri =
            $"http://{(redirectHost.Contains(':') ? $"[{redirectHost}]" : redirectHost)}:{boundPort}{options.Path}";
        if (options.TimeoutMs is { } timeout)
        {
            _timeoutCts = new CancellationTokenSource(timeout);
            _timeoutCts.Token.Register(() => FinishError(
                new InvalidOperationException($"{options.ProviderName} sign-in timed out")));
        }
        if (options.Signal.CanBeCanceled)
            options.Signal.Register(static (self, ct) =>
                ((OAuthCallbackServer<T>)self!).FinishError(new OperationCanceledException("Login cancelled", ct)),
                this);
        _ = AcceptLoopAsync();
    }

    /// <summary>重定向地址（浏览器完成授权后回到这里）。</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// 等待 <c>complete</c> 的结果；cancel 后返回 null。provider 重定向带 error、
    /// complete 失败、signal 中止或超时会抛出。
    /// </summary>
    public Task<T?> WaitAsync() => _completion.Task;

    /// <summary>停止等待浏览器——除非回调已在完成流程中。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_claimed) FinishValueLocked(default);
        }
    }

    /// <summary>关闭服务器并让 wait 以错误终态。</summary>
    public void Dispose()
    {
        FinishError(new InvalidOperationException("OAuth callback server closed"));
        try { _listener.Stop(); } catch { /* 已关闭 */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>启动监听。对应 TS <c>startOAuthCallbackServer</c>。</summary>
    public static OAuthCallbackServer<T> Start(OAuthCallbackServerOptions<T> options)
    {
        if (options.Signal.IsCancellationRequested) throw new OperationCanceledException("Login cancelled");

        var port = options.Port;
        if (port == 0) port = FindFreePort(options.Host);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{options.Host}:{port}/");
        listener.Start(); // 端口被占用等失败在此抛 HttpListenerException
        return new OAuthCallbackServer<T>(options, listener, port);
    }

    private static int FindFreePort(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
            address = Dns.GetHostEntry(host).AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? IPAddress.Loopback;
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(address, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(context));
            }
        }
        catch (Exception ex)
        {
            // 监听器意外死亡（含 Dispose 后的正常关闭）：与 TS server.on("error") 一致。
            // 已终态时 TrySetException 为 no-op。
            FinishError(ex);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var url = request.Url ?? new Uri("http://localhost/");
            var query = HttpUtility.ParseQueryString(url.Query);

            if (request.HttpMethod != "GET" || url.AbsolutePath != _options.Path)
            {
                SendPage(response, 404, OAuthPage.ErrorHtml("Callback route not found."));
                return;
            }
            if (_options.State is { } expected && query["state"] != expected)
            {
                SendPage(response, 400, OAuthPage.ErrorHtml("State mismatch."));
                return;
            }
            lock (_gate)
            {
                if (_claimed || _settled)
                {
                    SendPage(response, 409, OAuthPage.ErrorHtml("This sign-in has already been handled."));
                    return;
                }
            }
            if (query["error"] is { Length: > 0 } error)
            {
                var description = query["error_description"] ?? error;
                SendPage(response, 400, OAuthPage.ErrorHtml(
                    $"{_options.ProviderName} authorization failed.", description));
                FinishError(new InvalidOperationException(
                    $"{_options.ProviderName} authorization failed: {description}"));
                return;
            }
            if (query["code"] is not { Length: > 0 } code)
            {
                SendPage(response, 400, OAuthPage.ErrorHtml("Missing authorization code."));
                return;
            }
            lock (_gate) _claimed = true;
            try
            {
                var value = await _options.Complete(code).ConfigureAwait(false);
                SendPage(response, 200, OAuthPage.SuccessHtml(
                    $"Signed in to {_options.ProviderName}. You may now close this page."));
                FinishValue(value);
            }
            catch (Exception ex)
            {
                var failure = ex is OperationCanceledException && _options.Signal.IsCancellationRequested
                    ? new OperationCanceledException("Login cancelled")
                    : ex;
                SendPage(response, 502, OAuthPage.ErrorHtml(
                    $"{_options.ProviderName} sign-in failed.", failure.Message));
                FinishError(failure);
            }
        }
        catch
        {
            // 页面写出失败（浏览器提前断开）：不影响 wait 终态。
            try { response.Abort(); } catch { /* ignore */ }
        }
    }

    private static void SendPage(HttpListenerResponse response, int status, string html)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.AddHeader("cache-control", "no-store");
        var body = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body, 0, body.Length);
        response.OutputStream.Dispose();
    }

    private void FinishValue(T? value)
    {
        lock (_gate)
        {
            FinishValueLocked(value);
        }
    }

    private void FinishValueLocked(T? value)
    {
        if (_settled) return;
        _settled = true;
        _completion.TrySetResult(value);
    }

    private void FinishError(Exception error)
    {
        lock (_gate)
        {
            if (_settled) return;
            _settled = true;
            _completion.TrySetException(error);
        }
    }
}

/// <summary>等待结果：浏览器回调 或 手动输入（判别联合）。对应 TS <c>waitForCallbackOrManualInput</c> 的返回。</summary>
public abstract record CallbackOrManualInput<T>
{
    /// <summary>浏览器回调产出。对应 TS <c>{ type: "callback", value }</c>。</summary>
    public sealed record Callback(T Value) : CallbackOrManualInput<T>;

    /// <summary>用户手动粘贴。对应 TS <c>{ type: "manual", input }</c>。</summary>
    public sealed record Manual(string Input) : CallbackOrManualInput<T>;
}

/// <summary>手动提示的文案。对应 TS <c>waitForCallbackOrManualInput</c> 的 prompt 参数。</summary>
public sealed record ManualPrompt(string Message, string? Placeholder = null);

public static class OAuthCallbackFlow
{
    /// <summary>
    /// 等待浏览器回调，或（浏览器无法访问 loopback 时，如 SSH 远程）等待用户
    /// 粘贴 code/重定向 URL。没有回调服务器时只用手动提示。
    /// 对应 TS <c>waitForCallbackOrManualInput</c>。
    /// </summary>
    public static async Task<CallbackOrManualInput<T>> WaitForCallbackOrManualInput<T>(
        ProviderAuthInteraction interaction,
        OAuthCallbackServer<T>? callback,
        ManualPrompt prompt)
    {
        var manualCts = new CancellationTokenSource();
        Exception? manualError = null;

        // 手动提示与回调竞速：提示先失败（含被中止）也要取消回调等待。
        var manualTask = interaction
            .PromptAsync(new AuthPrompt.ManualCode(prompt.Message, prompt.Placeholder), manualCts.Token)
            .ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    callback?.Cancel();
                    return t.Result;
                }
                var ex = t.Exception?.GetBaseException() ?? new OperationCanceledException();
                manualError = ex;
                callback?.Cancel();
                return null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var value = callback is null ? default : await callback.WaitAsync().ConfigureAwait(false);
            if (manualError is not null) throw manualError;
            if (value is not null) return new CallbackOrManualInput<T>.Callback(value);
            var input = await manualTask.ConfigureAwait(false);
            if (manualError is not null) throw manualError;
            return new CallbackOrManualInput<T>.Manual(input ?? "");
        }
        finally
        {
            // 中止仍挂起的手动提示（回调先到时）。对齐 TS finally { manualAbort.abort(); }
            manualCts.Cancel();
        }
    }
}
