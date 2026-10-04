using System.Net;
using System.Text;

namespace Pi.Mcp.OAuth;

/// <summary>授权回调结果：code + state（+ iss，RFC 9207）。对应 TS <c>OAuthCallback</c>。</summary>
public sealed record OAuthCallback(string Code, string State, string? Iss = null);

/// <summary>重定向后浏览器页面呈现的结果。对应 TS <c>OAuthCallbackPage</c>。</summary>
public sealed record OAuthCallbackPage(bool Ok, string? Message = null, string? Details = null)
{
    public static OAuthCallbackPage Success() => new(true);

    public static OAuthCallbackPage Failure(string message, string? details = null) => new(false, message, details);
}

/// <summary>回调服务器选项。对应 TS <c>OAuthCallbackServerOptions</c>。</summary>
public sealed record OAuthCallbackServerOptions
{
    /// <summary>监听地址。默认 <c>127.0.0.1</c>。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>redirectUrl 里的主机名（如注册为 localhost 但实际监听 127.0.0.1）。默认同 Host。</summary>
    public string? RedirectHost { get; init; }

    /// <summary>端口。0 = 自动分配。</summary>
    public int Port { get; init; }

    /// <summary>回调路径。默认 <c>/callback</c>。</summary>
    public string Path { get; init; } = "/callback";

    /// <summary>额外接受回调的路径（如 redirect URI 的服务器专用路径）。</summary>
    public IReadOnlyList<string>? ExtraPaths { get; init; }

    /// <summary>等待超时（毫秒）。默认 5 分钟。</summary>
    public int TimeoutMs { get; init; } = 5 * 60_000;

    /// <summary>用 HTML 渲染浏览器页。默认纯文本消息。</summary>
    public Func<OAuthCallbackPage, string>? RenderPage { get; init; }
}

/// <summary>
/// 本地 HTTP 回调服务器：等待授权服务器把浏览器重定向回来。
/// 对应 TS <c>OAuthCallbackServer</c>。一个实例可服务多个并发 state。
/// </summary>
public sealed class OAuthCallbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string[] _paths;
    private readonly Func<OAuthCallbackPage, string>? _renderPage;
    private readonly Dictionary<string, TaskCompletionSource<OAuthCallback>> _pending = new();
    private readonly Dictionary<string, (string? Path, CancellationTokenSource Timeout)> _pendingMeta = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();

    public string RedirectUrl { get; }

    private OAuthCallbackServer(HttpListener listener, string redirectUrl, string[] paths,
        int timeoutMs, Func<OAuthCallbackPage, string>? renderPage)
    {
        _listener = listener;
        RedirectUrl = redirectUrl;
        _paths = paths;
        TimeoutMs = timeoutMs;
        _renderPage = renderPage;
    }

    /// <summary>等待超时（毫秒）。</summary>
    public int TimeoutMs { get; }

    /// <summary>启动监听（端口 0 = 自动分配）。对应 TS <c>OAuthCallbackServer.listen</c>。</summary>
    public static async Task<OAuthCallbackServer> ListenAsync(
        OAuthCallbackServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OAuthCallbackServerOptions();
        var redirectHost = options.RedirectHost ?? options.Host;
        var listener = new HttpListener();
        var port = options.Port == 0 ? FindFreePort(options.Host) : options.Port;
        listener.Prefixes.Add($"http://{options.Host}:{port}/");
        listener.Start();

        var path = options.Path;
        var instance = new OAuthCallbackServer(
            listener,
            $"http://{(redirectHost.Contains(':') ? $"[{redirectHost}]" : redirectHost)}:{port}{path}",
            [path, .. options.ExtraPaths ?? []],
            options.TimeoutMs, options.RenderPage);
        _ = instance.AcceptLoopAsync(cancellationToken);
        return instance;
    }

    private static int FindFreePort(string host)
    {
        var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Parse(host), 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Dispose();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => Handle(context), linked.Token);
            }
        }
        catch (Exception) when (linked.Token.IsCancellationRequested)
        {
            // 关闭流程，忽略监听器异常。
        }
    }

    /// <summary>等待指定 state 的授权响应。path 给定时，其他路径的响应视为失败（RFC 9700 §4.4.2.2）。</summary>
    public Task<OAuthCallback> WaitForCallbackAsync(string state, string? path = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<OAuthCallback> completion;
        lock (_lock)
        {
            if (_pending.ContainsKey(state))
                throw new InvalidOperationException("OAuth state is already pending");
            completion = new TaskCompletionSource<OAuthCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout = new CancellationTokenSource(TimeoutMs);
            timeout.Token.Register(() =>
            {
                lock (_lock) _pending.Remove(state);
                completion.TrySetException(new TimeoutException("OAuth callback timed out"));
            });
            _pending[state] = completion;
            _pendingMeta[state] = (path, timeout);
        }
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        List<TaskCompletionSource<OAuthCallback>> pending;
        lock (_lock)
        {
            pending = _pending.Values.ToList();
            foreach (var (_, (_, timeout)) in _pendingMeta) timeout.Dispose();
            _pending.Clear();
            _pendingMeta.Clear();
        }
        foreach (var completion in pending)
            completion.TrySetException(new InvalidOperationException("OAuth callback server closed"));
        _shutdown.Cancel();
        try { _listener.Stop(); } catch { }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void Reply(HttpListenerResponse response, int status, OAuthCallbackPage page)
    {
        try
        {
            if (_renderPage is not null)
            {
                response.StatusCode = status;
                response.ContentType = "text/html; charset=utf-8";
                response.AddHeader("cache-control", "no-store");
                var html = Encoding.UTF8.GetBytes(_renderPage(page));
                response.ContentLength64 = html.Length;
                response.OutputStream.Write(html);
            }
            else
            {
                var message = page.Ok
                    ? "Authorization complete. You may close this window."
                    : page.Details is not null ? $"{page.Message}\n\n{page.Details}" : page.Message ?? "";
                response.StatusCode = status;
                response.ContentType = "text/plain; charset=utf-8";
                var body = Encoding.UTF8.GetBytes(message);
                response.ContentLength64 = body.Length;
                response.OutputStream.Write(body);
            }
        }
        finally
        {
            response.OutputStream.Dispose();
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var rawUrl = context.Request.Url ?? new Uri(RedirectUrl);
        var url = new Uri(rawUrl.ToString());
        if (!_paths.Contains(url.AbsolutePath))
        {
            Reply(context.Response, 404, OAuthCallbackPage.Failure("Not found"));
            return;
        }
        var state = url.Query.Length > 0
            ? System.Web.HttpUtility.ParseQueryString(url.Query)["state"] : null;
        TaskCompletionSource<OAuthCallback>? pending = null;
        (string? Path, CancellationTokenSource Timeout) meta = (null, null!);
        lock (_lock)
        {
            if (state is not null && _pending.TryGetValue(state, out var found))
            {
                pending = found;
                _pendingMeta.TryGetValue(state, out meta);
            }
        }
        if (state is null || pending is null)
        {
            Reply(context.Response, 400, OAuthCallbackPage.Failure("Invalid or expired OAuth state"));
            return;
        }
        lock (_lock)
        {
            _pending.Remove(state);
            _pendingMeta.Remove(state);
        }
        meta.Timeout.Dispose();

        if (meta.Path is not null && url.AbsolutePath != meta.Path)
        {
            pending.TrySetException(new InvalidOperationException(
                "The authorization response arrived on another redirect URI"));
            Reply(context.Response, 400, OAuthCallbackPage.Failure("Unexpected redirect URI"));
            return;
        }
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var error = query["error"];
        if (error is not null)
        {
            var description = query["error_description"] ?? error;
            pending.TrySetException(new InvalidOperationException(description));
            Reply(context.Response, 200, OAuthCallbackPage.Failure(
                "Authorization failed. You may close this window.", description));
            return;
        }
        var code = query["code"];
        if (string.IsNullOrEmpty(code))
        {
            pending.TrySetException(new InvalidOperationException(
                "OAuth callback did not include an authorization code"));
            Reply(context.Response, 400, OAuthCallbackPage.Failure("Missing authorization code"));
            return;
        }
        var iss = query["iss"];
        pending.TrySetResult(new OAuthCallback(code, state, iss));
        Reply(context.Response, 200, OAuthCallbackPage.Success());
    }
}
