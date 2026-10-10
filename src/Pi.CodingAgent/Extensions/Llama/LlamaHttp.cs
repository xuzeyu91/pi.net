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
