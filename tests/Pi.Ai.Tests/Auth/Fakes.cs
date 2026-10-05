using System.Net;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;

namespace Pi.Ai.Tests.Auth;

/// <summary>假 HttpMessageHandler：按队列返回预制响应并记录请求。</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage>? _responder;
    private int _unserved;

    /// <summary>队列模式：按序消费预制响应。</summary>
    public FakeHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        foreach (var response in responses) _responses.Enqueue(response);
    }

    /// <summary>分派模式：队列耗尽后按请求内容生成响应（并行请求需要确定性分派时用）。</summary>
    public FakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        => _responder = responder;

    public List<FakeRequest> Requests { get; } = [];

    /// <summary>尚未消费的响应数（轮询路径的 pending 步数等场景可断言）。</summary>
    public int Unserved => _unserved;

    public sealed record FakeRequest(string Method, string? Url, string? Body, Dictionary<string, string> Headers);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in request.Headers) headers[key] = string.Join(",", values);
        if (request.Content?.Headers is { } contentHeaders)
        {
            foreach (var (key, values) in contentHeaders) headers[key] = string.Join(",", values);
        }
        Requests.Add(new FakeRequest(request.Method.Method, request.RequestUri?.ToString(), body, headers));

        if (_responses.Count == 0)
        {
            if (_responder is not null) return _responder(request, body);
            Interlocked.Increment(ref _unserved);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("no fake response") };
        }
        return _responses.Dequeue()(request);
    }
}

internal static class FakeResponse
{
    public static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain") };
}

/// <summary>假登录交互：prompt 由委托驱动，notify 收集事件。</summary>
internal sealed class FakeInteraction : IAuthInteraction
{
    public CancellationToken Signal { get; set; } = default;

    public List<AuthPrompt> Prompts { get; } = [];

    public List<AuthEvent> Events { get; } = [];

    /// <summary>按 prompt 类型或消息返回答案的处理器（第二个参数是 prompt 级取消令牌）。</summary>
    public Func<AuthPrompt, CancellationToken, Task<string>> PromptHandler { get; set; } =
        (prompt, _) => throw new InvalidOperationException($"Unexpected prompt: {prompt.GetType().Name}");

    /// <summary>prompt 完成信号（竞速场景同步用）。</summary>
    public TaskCompletionSource? PromptObserved { get; set; }

    public async Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        PromptObserved?.TrySetResult();
        return await PromptHandler(prompt, cancellationToken).ConfigureAwait(false);
    }

    public void Notify(AuthEvent @event) => Events.Add(@event);

    /// <summary>取第一个指定类型的事件。</summary>
    public T Event<T>() where T : AuthEvent
        => Events.OfType<T>().First() ?? throw new InvalidOperationException($"No {typeof(T).Name} event");

    public ProviderAuthInteraction ToInteraction() => new(this, Signal);
}

/// <summary>把 notify 事件转发给观察者的包装交互。</summary>
internal sealed class CapturingInteraction(IAuthInteraction inner, Action<AuthEvent> onEvent) : IAuthInteraction
{
    public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        => inner.PromptAsync(prompt, cancellationToken);

    public void Notify(AuthEvent @event)
    {
        inner.Notify(@event);
        onEvent(@event);
    }

    public ProviderAuthInteraction ToInteraction(CancellationToken signal = default)
        => new(this, signal);
}

/// <summary>带消息断言辅助的基类。</summary>
internal static class AssertX
{
    /// <summary>URL 查询参数取值。</summary>
    public static string? Query(string url, string name)
        => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)[name];

    /// <summary>表单体取值。</summary>
    public static string? Form(string? body, string name)
        => body is null ? null : System.Web.HttpUtility.ParseQueryString(body)[name];
}
