using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// OAuth 流共享 HTTP 辅助：fetch + AbortSignal.timeout(30s) 的 C# 对应
/// （每请求 linked CTS 超时）。TS 用全局 fetch stub，C# 惯用法是注入
/// <see cref="HttpClient"/>（测试用假 <c>HttpMessageHandler</c>）。
/// </summary>
internal static class OAuthHttp
{
    /// <summary>默认请求超时（毫秒）。对应各流程的 AbortSignal.timeout(30_000)。</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>默认共享客户端（仅登录流程使用）。</summary>
    public static readonly HttpClient Shared = CreateClient();

    public static HttpClient CreateClient()
        => new(new SocketsHttpHandler
        {
            UseProxy = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        { Timeout = Timeout.InfiniteTimeSpan }; // 超时由每请求 linked CTS 控制

    /// <summary>带整体取消 + 请求超时的发送。取消时对齐 TS "Login cancelled"。</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken signal, int timeoutMs = DefaultTimeoutMs)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(signal);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await client.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (signal.IsCancellationRequested)
        {
            throw new OperationCanceledException("Login cancelled", signal);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"OAuth request timed out ({timeoutMs} ms): {request.Method} {request.RequestUri}");
        }
    }

    public static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client, string url, JsonObject body, CancellationToken signal, int timeoutMs = DefaultTimeoutMs)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        return SendAsync(client, request, signal, timeoutMs);
    }

    public static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string url, IReadOnlyDictionary<string, string> fields,
        CancellationToken signal, int timeoutMs = DefaultTimeoutMs)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Accept.ParseAdd("application/json");
        return SendAsync(client, request, signal, timeoutMs);
    }

    /// <summary>读响应为 JSON 对象（非对象/解析失败返回 null）。对应各流程的 readJson。</summary>
    public static async Task<JsonObject?> ReadObjectAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读响应文本（失败返回空串）。对应 <c>response.text().catch(() =&gt; "")</c>。</summary>
    public static async Task<string> ReadTextAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>JSON 对象取值辅助（对齐 TS 的 typeof 检查语义）。</summary>
internal static class OAuthJsonExtensions
{
    public static string? Str(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue { } primitive
            && primitive.TryGetValue<string>(out var text)
            ? text
            : null;

    public static double? Num(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue { } primitive
            && primitive.TryGetValue<double>(out var number)
            ? number
            : null;

    public static bool? Bool(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is JsonValue { } primitive
            && primitive.TryGetValue<bool>(out var flag)
            ? flag
            : null;

    /// <summary>键存在且非 null（对齐 TS <c>value !== undefined</c>）。</summary>
    public static bool Has(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value)
            && value is not null
            && value.GetValueKind() != System.Text.Json.JsonValueKind.Null;

    public static JsonObject? Obj(this JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var value) ? value as JsonObject : null;

    /// <summary>正有限数（对齐 TS positiveNumber）。</summary>
    public static int? PositiveInt(this JsonObject obj, string key)
        => obj.Num(key) is { } number && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0
            ? (int)number
            : null;
}
