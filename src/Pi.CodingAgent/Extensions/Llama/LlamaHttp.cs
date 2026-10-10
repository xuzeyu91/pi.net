using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Extensions.Llama;

// ============================================================================
// llama HTTP seam — the `fetch` boundary for the llama.cpp and Hugging Face clients
// ============================================================================

/// <summary>
/// One HTTP request as the llama clients issue it. Port of the <c>RequestInit</c> slice the TS
/// clients use (method, JSON body, headers).
/// </summary>
public sealed record LlamaHttpRequest(
    string Method = "GET",
    string? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// One HTTP response as the llama clients read it. Port of the <c>Response</c> slice the TS clients
/// use (<c>ok</c>, <c>status</c>, <c>headers</c>, <c>json()</c>). <see cref="Body"/> is the raw body;
/// <see cref="Json"/> parses it lazily the way <c>response.json()</c> does.
/// </summary>
public sealed record LlamaHttpResponse(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>The TS <c>await response.json()</c>; null when the body is missing or not valid JSON.</summary>
    public JsonNode? Json()
    {
        if (string.IsNullOrEmpty(Body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(Body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public string? Header(string name) =>
        Headers is not null && Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The <c>fetch</c> seam. The default implementation uses <see cref="HttpClient"/>; tests inject a
/// scripted queue so the vectors stay offline and deterministic (difference C118).
/// </summary>
public delegate Task<LlamaHttpResponse> LlamaFetch(string url, LlamaHttpRequest request, CancellationToken cancellationToken);

/// <summary>The default <see cref="LlamaFetch"/>, backed by a shared <see cref="HttpClient"/>.</summary>
public static class LlamaFetchDefaults
{
    private static readonly HttpClient Client = new();

    public static async Task<LlamaHttpResponse> Fetch(string url, LlamaHttpRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");
        }

        if (request.Headers is not null)
        {
            foreach (var (name, value) in request.Headers)
            {
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        using var response = await Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return new LlamaHttpResponse((int)response.StatusCode, body, headers);
    }
}

/// <summary>
/// One streaming response — the <c>fetch()</c> slice <c>watch()</c> reads: <c>ok</c>, <c>status</c> and the
/// body as a sequence of byte chunks (<c>response.body.getReader()</c>). <see cref="Chunks"/> is
/// <see langword="null"/> when there is no body at all, which the caller treats as an error.
/// </summary>
public sealed record LlamaStreamResponse(int Status, IAsyncEnumerable<byte[]>? Chunks)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// The streaming half of the <c>fetch</c> seam (difference C125). <c>watch()</c> reads the body
/// incrementally, so it cannot go through <see cref="LlamaFetch"/>: the chunks stay bytes and the UTF-8
/// decoding plus SSE framing live in <see cref="LlamaClient"/>, where the differential vectors can pin
/// them (including the split-multibyte and malformed-byte cases).
/// </summary>
public delegate Task<LlamaStreamResponse> LlamaOpenStream(string url, LlamaHttpRequest request, CancellationToken cancellationToken);

/// <summary>The default <see cref="LlamaOpenStream"/>, backed by a shared <see cref="HttpClient"/>.</summary>
public static class LlamaStreamDefaults
{
    private const int BufferSize = 8192;

    private static readonly HttpClient Client = new();

    public static async Task<LlamaStreamResponse> OpenStream(string url, LlamaHttpRequest request, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), url);
        if (request.Headers is not null)
        {
            foreach (var (name, value) in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await Client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            message.Dispose();
            throw;
        }

        message.Dispose();
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            return new LlamaStreamResponse(status, null);
        }

        // WHATWG makes the body of a "null body status" null, and `watch()` treats a null body as an
        // error. `HttpClient` would hand back an empty stream instead, which would silently look like
        // "the stream ended without a single event" (difference C129).
        if (IsNullBodyStatus(response.StatusCode))
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            return new LlamaStreamResponse(status, null);
        }

        return new LlamaStreamResponse((int)response.StatusCode, ReadChunks(response, cancellationToken));
    }

    /// <summary>The Fetch spec's "null body status": the statuses whose <c>Response.body</c> is null.</summary>
    private static bool IsNullBodyStatus(HttpStatusCode status) =>
        status is HttpStatusCode.Continue or HttpStatusCode.SwitchingProtocols
            or HttpStatusCode.NoContent or HttpStatusCode.ResetContent or HttpStatusCode.NotModified;

    private static async IAsyncEnumerable<byte[]> ReadChunks(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    yield break;
                }

                yield return buffer[..read];
            }
        }
    }
}
