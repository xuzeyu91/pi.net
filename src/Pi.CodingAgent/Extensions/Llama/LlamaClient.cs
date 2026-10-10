using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>Port of the TS <c>LlamaClient</c> (<c>extensions/llama/client.ts</c>).</summary>
/// <remarks>
/// <c>watch</c> / <c>loadAndWait</c> / <c>downloadAndWait</c> are the SSE + polling half of the
/// client; they land with batch 4d-7b because they need a streaming seam. Everything else is here.
/// </remarks>
public sealed class LlamaClient
{
    private const int RequestTimeoutMs = 15_000;

    private readonly string? _apiKey;
    private readonly LlamaFetch _fetch;

    public LlamaClient(string serverUrl, string? apiKey = null, LlamaFetch? fetch = null)
    {
        ServerUrl = LlamaModels.NormalizeLlamaServerUrl(serverUrl);
        _apiKey = apiKey;
        _fetch = fetch ?? LlamaFetchDefaults.Fetch;
    }

    public string ServerUrl { get; }

    private async Task<JsonNode?> RequestAsync(string path, LlamaHttpRequest init, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (init.Headers is not null)
        {
            foreach (var (name, value) in init.Headers)
            {
                headers[name] = value;
            }
        }

        if (init.Body is not null)
        {
            headers["Content-Type"] = "application/json";
        }

        if (_apiKey is not null)
        {
            headers["Authorization"] = $"Bearer {_apiKey}";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeoutMs);
        var response = await _fetch(
            $"{ServerUrl}{path}",
            init with { Headers = headers },
            timeout.Token).ConfigureAwait(false);
        var payload = response.Json();
        if (!response.Ok)
        {
            throw new InvalidOperationException(
                LlamaModels.ErrorMessage(payload, $"llama.cpp returned HTTP {response.Status}"));
        }

        return payload;
    }

    /// <summary>The TS <c>list({ reload, signal })</c>.</summary>
    public async Task<IReadOnlyList<LlamaModelInfo>> ListAsync(CancellationToken cancellationToken = default, bool reload = false)
    {
        var payload = await RequestAsync(
            $"/models{(reload ? "?reload=1" : "")}",
            new LlamaHttpRequest("GET"),
            cancellationToken).ConfigureAwait(false);
        if (payload is not JsonObject obj || obj["data"] is not JsonArray data)
        {
            throw new InvalidOperationException("llama.cpp returned an invalid model catalog");
        }

        if (!data.All(LlamaModels.IsModelInfo))
        {
            throw new InvalidOperationException("Server is not running in llama.cpp router mode");
        }

        return data.Select(node => new LlamaModelInfo((JsonObject)node!)).ToList();
    }

    /// <summary>The TS <c>props({ model, signal })</c>.</summary>
    public async Task<LlamaServerProps> PropsAsync(CancellationToken cancellationToken = default, string? model = null)
    {
        var query = model is not null
            ? $"?{UrlSearchParamsToString(new Dictionary<string, string> { ["model"] = model, ["autoload"] = "false" })}"
            : "";
        var payload = await RequestAsync($"/props{query}", new LlamaHttpRequest("GET"), cancellationToken).ConfigureAwait(false);
        if (payload is not JsonObject obj)
        {
            return new LlamaServerProps();
        }

        return new LlamaServerProps
        {
            ModelsAutoload = obj["models_autoload"] is JsonValue autoload && autoload.TryGetValue<bool>(out var autoloadValue)
                ? autoloadValue
                : null,
            ChatTemplate = obj["chat_template"] is JsonValue template && template.TryGetValue<string>(out var templateValue)
                ? templateValue
                : null,
        };
    }

    /// <summary>The TS <c>load(model, signal)</c>.</summary>
    public Task LoadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models/load", new LlamaHttpRequest("POST", JsonBody(model)), cancellationToken);

    /// <summary>The TS <c>unload(model, signal)</c>.</summary>
    public Task UnloadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models/unload", new LlamaHttpRequest("POST", JsonBody(model)), cancellationToken);

    /// <summary>The TS <c>download(model, signal)</c>.</summary>
    public Task DownloadAsync(string model, CancellationToken cancellationToken = default) =>
        RequestAsync("/models", new LlamaHttpRequest("POST", JsonBody(model)), cancellationToken);

    /// <summary>The TS <c>unloadAndWait(model, signal)</c>.</summary>
    public async Task UnloadAndWaitAsync(string model, CancellationToken cancellationToken = default)
    {
        await UnloadAsync(model, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var entry = (await ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Id == model);
            if (entry is null || entry.StatusValue == LlamaModelStatusValue.Unloaded)
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string JsonBody(string model) => new JsonObject { ["model"] = model }.ToJsonString();

    /// <summary>The TS <c>new URLSearchParams({...}).toString()</c> (form encoding: space as <c>+</c>).</summary>
    private static string UrlSearchParamsToString(IReadOnlyDictionary<string, string> parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            parts.Add($"{FormEncode(key)}={FormEncode(value)}");
        }

        return string.Join("&", parts);
    }

    private static string FormEncode(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '*' or '-' or '.' or '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(((int)b).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
