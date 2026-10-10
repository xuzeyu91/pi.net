using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>Port of the TS <c>LlamaClient</c> (<c>extensions/llama/client.ts</c>).</summary>
public sealed class LlamaClient
{
    private const int RequestTimeoutMs = 15_000;

    /// <summary>The TS <c>sleep(250, signal)</c> between <c>loadAndWait</c> polls.</summary>
    private const int LoadPollMs = 250;

    /// <summary>The TS <c>sleep(500, signal)</c> between <c>downloadAndWait</c> polls.</summary>
    private const int DownloadPollMs = 500;

    private readonly string? _apiKey;
    private readonly LlamaFetch _fetch;
    private readonly LlamaOpenStream _openStream;

    public LlamaClient(string serverUrl, string? apiKey = null, LlamaFetch? fetch = null, LlamaOpenStream? openStream = null)
    {
        ServerUrl = LlamaModels.NormalizeLlamaServerUrl(serverUrl);
        _apiKey = apiKey;
        _fetch = fetch ?? LlamaFetchDefaults.Fetch;
        _openStream = openStream ?? LlamaStreamDefaults.OpenStream;
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

    // ------------------------------------------------------------------ SSE + polling half

    /// <summary>
    /// The TS <c>watch(onEvent, signal)</c>: read <c>/models/sse</c> and hand every well-formed frame to
    /// <paramref name="onEvent"/> until the stream ends.
    /// </summary>
    /// <remarks>
    /// The UTF-8 decoding and the SSE framing live here rather than in the seam, so the differential
    /// vectors pin them (difference C125). Two details are easy to get wrong and are pinned by vectors:
    /// a leading BOM is dropped (WHATWG <c>TextDecoder</c> does that, <see cref="Encoding.UTF8"/> does
    /// not), and the decoder is never flushed, so a truncated multi-byte tail at end of stream is
    /// <em>discarded</em> rather than becoming U+FFFD.
    /// </remarks>
    public async Task WatchAsync(Action<LlamaModelEvent> onEvent, CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_apiKey is not null)
        {
            headers["Authorization"] = $"Bearer {_apiKey}";
        }

        var response = await _openStream(
            $"{ServerUrl}/models/sse",
            new LlamaHttpRequest("GET", null, headers),
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Chunks is null)
        {
            throw new InvalidOperationException($"llama.cpp SSE returned HTTP {response.Status}");
        }

        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
        var scratch = new char[1];
        var buffer = string.Empty;
        var started = false;

        await foreach (var chunk in response.Chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var text = DecodeChunk(decoder, chunk, ref scratch);
            if (!started && text.Length > 0)
            {
                started = true;
                text = text.StartsWith('\uFEFF') ? text[1..] : text;
            }

            if (text.Length == 0)
            {
                continue;
            }

            buffer += text.Replace("\r\n", "\n", StringComparison.Ordinal);
            buffer = DrainFrames(buffer, onEvent);
        }
    }

    /// <summary>The TS <c>loadAndWait(model, onProgress, signal)</c>.</summary>
    public async Task<LlamaModelInfo> LoadAndWaitAsync(
        string model,
        Action<LlamaProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new WatchState();
        Forget(WatchAsync(evt =>
        {
            if (evt.Model != model)
            {
                return;
            }

            if (evt.Event is not ("model_status" or "status_change"))
            {
                return;
            }

            var status = (evt.Data as JsonObject)?["status"] is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : null;
            if (status == LlamaModelStatusValue.Loaded)
            {
                state.EventLoaded = true;
            }

            if (status == LlamaModelStatusValue.Unloaded)
            {
                state.EventError = "Model failed to load";
            }

            if (LlamaModels.ParseLoadProgress(evt.Data) is { } progress)
            {
                onProgress(progress);
            }
        }, watcher.Token));

        try
        {
            await LoadAsync(model, cancellationToken).ConfigureAwait(false);
            onProgress(new LlamaProgress { Message = "Loading model" });
            while (true)
            {
                ThrowIfCancelled(cancellationToken);
                var entry = (await ListAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(candidate => candidate.Id == model);
                if (entry?.StatusValue == LlamaModelStatusValue.Loaded)
                {
                    return entry;
                }

                if (state.EventLoaded && entry is null)
                {
                    return new LlamaModelInfo(new JsonObject
                    {
                        ["id"] = model,
                        ["status"] = new JsonObject { ["value"] = LlamaModelStatusValue.Loaded },
                    });
                }

                var eventError = state.EventError;
                if (entry?.StatusFailed == true || eventError is not null)
                {
                    throw new InvalidOperationException(
                        entry?.StatusExitCode is null
                            ? (eventError ?? "Model failed to load")
                            : $"Model exited with code {entry.StatusExitCode}");
                }

                await Task.Delay(LoadPollMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await CancelAsync(watcher).ConfigureAwait(false);
        }
    }

    /// <summary>The TS <c>downloadAndWait(model, onProgress, signal)</c>.</summary>
    public async Task<IReadOnlyList<LlamaModelInfo>> DownloadAndWaitAsync(
        string model,
        Action<LlamaProgress> onProgress,
        CancellationToken cancellationToken = default)
    {
        using var watcher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new WatchState();
        var polls = 0;
        Forget(WatchAsync(evt =>
        {
            if (evt.Model != model)
            {
                return;
            }

            if (evt.Event == "download_finished")
            {
                state.Finished = true;
            }

            if (evt.Event == "download_failed")
            {
                state.Failure = LlamaModels.ErrorMessage(evt.Data, "Download failed");
            }

            if (evt.Event == "download_progress")
            {
                state.SawDownloading = true;
                if (LlamaModels.ParseDownloadProgress(evt.Data) is { } progress)
                {
                    onProgress(progress);
                }
            }
        }, watcher.Token));

        try
        {
            await DownloadAsync(model, cancellationToken).ConfigureAwait(false);
            onProgress(new LlamaProgress { Message = "Downloading model" });
            while (true)
            {
                ThrowIfCancelled(cancellationToken);
                if (state.Failure is { } failure)
                {
                    throw new InvalidOperationException(failure);
                }

                var models = await ListAsync(cancellationToken).ConfigureAwait(false);
                polls++;
                var entry = models.FirstOrDefault(candidate => candidate.Id == model);
                if (entry?.StatusValue == LlamaModelStatusValue.Downloading)
                {
                    state.SawDownloading = true;
                    if (LlamaModels.ParseDownloadProgress(entry.StatusRaw?["progress"]) is { } progress)
                    {
                        onProgress(progress);
                    }
                }
                else if (state.Finished || (entry is not null && (state.SawDownloading || polls >= 2)))
                {
                    return await ListAsync(cancellationToken, reload: true).ConfigureAwait(false);
                }

                await Task.Delay(DownloadPollMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await CancelAsync(watcher).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The mutable flags the watcher task and the polling loop share. They are <see langword="volatile"/>
    /// because the two run concurrently: the TS closure reads them from the same single-threaded event loop,
    /// while C# needs the visibility guarantee (difference C126).
    /// </summary>
    private sealed class WatchState
    {
        public volatile bool EventLoaded;

        public volatile string? EventError;

        public volatile bool Finished;

        public volatile string? Failure;

        public volatile bool SawDownloading;
    }

    /// <summary>The TS <c>void promise.catch(() =&gt; {})</c>: observe the background watcher and drop its faults.</summary>
    private static void Forget(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>The TS <c>if (signal?.aborted) throw signal.reason ?? new Error("Cancelled")</c>.</summary>
    private static void ThrowIfCancelled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Cancelled", cancellationToken);
        }
    }

    /// <summary>The TS <c>watcher.abort()</c> in the <c>finally</c>: stop the watcher without touching the caller's token.</summary>
    private static async Task CancelAsync(CancellationTokenSource watcher)
    {
        try
        {
            await watcher.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The linked source was already torn down; nothing left to abort.
        }
    }

    /// <summary>
    /// One <c>decoder.decode(chunk, { stream: true })</c>. The scratch buffer grows to the chunk size:
    /// a UTF-8 chunk never yields more chars than it has bytes (a 4-byte sequence yields a surrogate
    /// pair, and each malformed byte yields one U+FFFD).
    /// </summary>
    private static string DecodeChunk(Decoder decoder, byte[] chunk, ref char[] scratch)
    {
        if (scratch.Length < chunk.Length)
        {
            scratch = new char[chunk.Length];
        }

        var count = decoder.GetChars(chunk, 0, chunk.Length, scratch, 0, flush: false);
        return count == 0 ? string.Empty : new string(scratch, 0, count);
    }

    /// <summary>
    /// The TS <c>while (boundary &gt;= 0)</c> frame loop: emit every complete <c>data:</c> frame and return
    /// the unconsumed remainder. Strings are immutable in both languages, so the slicing matches the
    /// original one-for-one (SSE payloads are small, so the copying is not a concern).
    /// </summary>
    private static string DrainFrames(string buffer, Action<LlamaModelEvent> onEvent)
    {
        while (true)
        {
            var boundary = buffer.IndexOf("\n\n", StringComparison.Ordinal);
            if (boundary < 0)
            {
                return buffer;
            }

            var frame = buffer[..boundary];
            buffer = buffer[(boundary + 2)..];

            var data = string.Join(
                "\n",
                frame.Split('\n')
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line[5..].TrimStart()));
            if (data.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(data) is JsonObject payload
                    && payload["model"] is JsonValue model && model.TryGetValue<string>(out var modelText)
                    && payload["event"] is JsonValue kind && kind.TryGetValue<string>(out var eventText))
                {
                    onEvent(new LlamaModelEvent(modelText, eventText, payload["data"]));
                }
            }
            catch (JsonException)
            {
                // Ignore malformed events; catalog polling remains authoritative.
            }
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
