using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Extensions.Llama;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the llama non-UI HTTP layer (port of
/// <c>extensions/llama/{client.ts,huggingface.ts}</c>, batch 4d-7a). Replays <c>llama-corpus.json</c>:
/// the byte/size formatter, the server-URL normalizer, the load/download progress parsers, the
/// <see cref="LlamaClient"/> JSON methods against a scripted fetch, and the Hugging Face client plus
/// token discovery.
/// </summary>
public class LlamaCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "llama-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ formatBytes

    [Theory]
    [MemberData(nameof(FormatBytesKeys))]
    public void FormatBytes_MatchesTypeScript(double input)
    {
        var vector = Corpus.GetProperty("formatBytes").EnumerateArray()
            .First(item => Math.Abs(item.GetProperty("input").GetDouble() - input) < double.Epsilon);
        Assert.Equal(vector.GetProperty("output").GetString(), LlamaModels.FormatBytes(input));
    }

    public static IEnumerable<object[]> FormatBytesKeys() =>
        Corpus.GetProperty("formatBytes").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("input").GetDouble() });

    // ------------------------------------------------------------------ normalizeLlamaServerUrl

    [Theory]
    [MemberData(nameof(NormalizeKeys))]
    public void NormalizeLlamaServerUrl_MatchesTypeScript(string input)
    {
        var vector = Corpus.GetProperty("normalizeLlamaServerUrl").EnumerateArray()
            .First(item => item.GetProperty("input").GetString() == input);
        var error = vector.GetProperty("error");

        if (error.ValueKind != JsonValueKind.Null)
        {
            var (name, message) = SplitError(error.GetString()!);
            var thrown = Assert.ThrowsAny<Exception>(() => LlamaModels.NormalizeLlamaServerUrl(input));
            Assert.Equal(message, thrown.Message);
            if (name == "TypeError")
            {
                Assert.IsType<UriFormatException>(thrown);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(thrown);
            }

            return;
        }

        Assert.Equal(vector.GetProperty("output").GetString(), LlamaModels.NormalizeLlamaServerUrl(input));
    }

    public static IEnumerable<object[]> NormalizeKeys() =>
        Corpus.GetProperty("normalizeLlamaServerUrl").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("input").GetString()! });

    [Theory]
    [MemberData(nameof(InferenceKeys))]
    public void LlamaInferenceUrl_MatchesTypeScript(string input)
    {
        var vector = Corpus.GetProperty("llamaInferenceUrl").EnumerateArray()
            .First(item => item.GetProperty("input").GetString() == input);
        Assert.Equal(vector.GetProperty("output").GetString(), LlamaModels.LlamaInferenceUrl(input));
    }

    public static IEnumerable<object[]> InferenceKeys() =>
        Corpus.GetProperty("llamaInferenceUrl").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("input").GetString()! });

    // ------------------------------------------------------------------ progress parsers

    [Fact]
    public void ParseLoadProgress_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("parseLoadProgress").EnumerateArray())
        {
            var data = ToNode(vector.GetProperty("data"));
            var expected = vector.GetProperty("output");
            var actual = LlamaModels.ParseLoadProgress(data);
            CheckProgress("parseLoadProgress", expected, actual);
        }
    }

    [Fact]
    public void ParseDownloadProgress_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("parseDownloadProgress").EnumerateArray())
        {
            var data = ToNode(vector.GetProperty("data"));
            var expected = vector.GetProperty("output");
            var actual = LlamaModels.ParseDownloadProgress(data);
            CheckProgress("parseDownloadProgress", expected, actual);
        }
    }

    private static void CheckProgress(string label, JsonElement expected, LlamaProgress? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetProperty("message").GetString(), actual!.Message);
        if (expected.TryGetProperty("detail", out var detail))
        {
            Assert.Equal(detail.GetString(), actual.Detail);
        }
        else
        {
            Assert.Null(actual.Detail);
        }

        if (expected.TryGetProperty("ratio", out var ratio))
        {
            Assert.NotNull(actual.Ratio);
            Assert.True(Math.Abs(ratio.GetDouble() - actual.Ratio!.Value) < 1e-9, $"{label}: ratio {ratio.GetDouble()} vs {actual.Ratio}");
        }
        else
        {
            Assert.Null(actual.Ratio);
        }
    }

    // ------------------------------------------------------------------ LlamaClient

    [Fact]
    public async Task LlamaClientList_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientList").EnumerateArray())
        {
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new LlamaClient("http://127.0.0.1:8080", "secret-key", scripted.Invoke);
            var expectedError = vector.GetProperty("error");

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.ListAsync(reload: vector.GetProperty("reload").GetBoolean()));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
            }
            else
            {
                var result = await client.ListAsync(reload: vector.GetProperty("reload").GetBoolean());
                var expectedData = vector.GetProperty("result");
                Assert.Equal(expectedData.GetArrayLength(), result.Count);
                for (var index = 0; index < result.Count; index++)
                {
                    Assert.True(
                        JsonNode.DeepEquals(JsonNode.Parse(expectedData[index].GetRawText()), result[index].Raw),
                        $"list #{index} raw JSON differs");
                }
            }

            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    [Fact]
    public async Task LlamaClientProps_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientProps").EnumerateArray())
        {
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new LlamaClient("http://127.0.0.1:8080", null, scripted.Invoke);
            var model = vector.GetProperty("model").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("model").GetString();
            var result = await client.PropsAsync(model: model);
            var expected = vector.GetProperty("result");
            Assert.Equal(GetOptionalBool(expected, "models_autoload"), result.ModelsAutoload);
            Assert.Equal(GetOptionalString(expected, "chat_template"), result.ChatTemplate);
            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    [Fact]
    public async Task LlamaClientActions_MatchTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientActions").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new LlamaClient("http://127.0.0.1:8080", "k", scripted.Invoke);
            var expectedError = vector.GetProperty("error");

            Func<Task> action = label switch
            {
                "load" or "load-error" => () => client.LoadAsync("m1"),
                "unload" => () => client.UnloadAsync("m1"),
                "download" => () => client.DownloadAsync("m1"),
                _ => throw new InvalidOperationException($"unknown action {label}"),
            };

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(action);
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
            }
            else
            {
                await action();
            }

            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    [Fact]
    public async Task LlamaClientUnloadAndWait_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientUnloadAndWait").EnumerateArray())
        {
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new LlamaClient("http://127.0.0.1:8080", null, scripted.Invoke);
            await client.UnloadAndWaitAsync("m1");
            Assert.Equal(vector.GetProperty("requestCount").GetInt32(), scripted.Requests.Count);
            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    // ------------------------------------------------------------------ LlamaClient: SSE + polling
    //
    // Every streaming vector runs under a deadline. The scripted queue repeats its last entry, so a port
    // whose SSE framing regressed would never see the event that breaks `loadAndWait`/`downloadAndWait`
    // out of their poll loops — the test would hang instead of failing. The deadline turns that into an
    // ordinary failure (`OperationCanceledException` is not the exception the vector expects), and it is
    // far above the real cost of a vector (the slowest is one 500 ms poll).

    private static readonly TimeSpan VectorDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LlamaClientWatch_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientWatch").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var scripted = new ScriptedStreamingFetch(vector.GetProperty("queue"));
            var client = new LlamaClient("http://127.0.0.1:8080", GetOptionalString(vector, "apiKey"), scripted.Invoke, scripted.OpenStream);
            var events = new List<LlamaModelEvent>();
            var expectedError = vector.GetProperty("error");
            using var deadline = new CancellationTokenSource(VectorDeadline);

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.WatchAsync(events.Add, deadline.Token));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
            }
            else
            {
                await client.WatchAsync(events.Add, deadline.Token);
            }

            CheckEvents(label, vector.GetProperty("events"), events);
            Assert.Equal(vector.GetProperty("exhausted").GetInt32(), scripted.Exhausted);
            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    [Fact]
    public async Task LlamaClientLoadAndWait_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientLoadAndWait").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var scripted = new ScriptedStreamingFetch(vector.GetProperty("queue"));
            var client = new LlamaClient("http://127.0.0.1:8080", null, scripted.Invoke, scripted.OpenStream);
            var progress = new List<LlamaProgress>();
            var expectedError = vector.GetProperty("error");
            var expectedResult = vector.GetProperty("result");
            using var deadline = new CancellationTokenSource(VectorDeadline);

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.LoadAndWaitAsync("m1", progress.Add, deadline.Token));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
                Assert.Equal(JsonValueKind.Null, expectedResult.ValueKind);
            }
            else
            {
                var result = await client.LoadAndWaitAsync("m1", progress.Add, deadline.Token);
                CheckModel(label, expectedResult, result.Raw);
            }

            CheckProgressSet(label, vector.GetProperty("progress"), progress);
            Assert.Equal(vector.GetProperty("exhausted").GetInt32(), scripted.Exhausted);
            if (vector.GetProperty("assertRequests").GetBoolean())
            {
                CheckRequests(vector.GetProperty("requests"), scripted.Requests);
            }
        }
    }

    [Fact]
    public async Task LlamaClientDownloadAndWait_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("llamaClientDownloadAndWait").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var scripted = new ScriptedStreamingFetch(vector.GetProperty("queue"));
            var client = new LlamaClient("http://127.0.0.1:8080", null, scripted.Invoke, scripted.OpenStream);
            var progress = new List<LlamaProgress>();
            var expectedError = vector.GetProperty("error");
            var expectedResult = vector.GetProperty("result");
            using var deadline = new CancellationTokenSource(VectorDeadline);

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => client.DownloadAndWaitAsync("m1", progress.Add, deadline.Token));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
                Assert.Equal(JsonValueKind.Null, expectedResult.ValueKind);
            }
            else
            {
                var result = await client.DownloadAndWaitAsync("m1", progress.Add, deadline.Token);
                Assert.Equal(expectedResult.GetArrayLength(), result.Count);
                for (var index = 0; index < result.Count; index++)
                {
                    CheckModel(label, expectedResult[index], result[index].Raw);
                }
            }

            CheckProgressSet(label, vector.GetProperty("progress"), progress);
            Assert.Equal(vector.GetProperty("exhausted").GetInt32(), scripted.Exhausted);
            if (vector.GetProperty("assertRequests").GetBoolean())
            {
                CheckRequests(vector.GetProperty("requests"), scripted.Requests);
            }
        }
    }

    // ------------------------------------------------------------------ HuggingFaceClient

    [Fact]
    public async Task HuggingFaceSearch_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("huggingFaceSearch").EnumerateArray())
        {
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new HuggingFaceClient("tok", "https://hf.example/", scripted.Invoke);
            var expectedError = vector.GetProperty("error");

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("llama 3"));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
            }
            else
            {
                var result = await client.SearchAsync("llama 3");
                var expected = vector.GetProperty("result");
                Assert.Equal(expected.GetArrayLength(), result.Count);
                for (var index = 0; index < result.Count; index++)
                {
                    Assert.Equal(expected[index].GetProperty("id").GetString(), result[index].Id);
                    Assert.Equal(expected[index].GetProperty("downloads").GetDouble(), result[index].Downloads);
                }
            }

            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    [Fact]
    public async Task HuggingFaceDetails_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("huggingFaceDetails").EnumerateArray())
        {
            var scripted = new ScriptedFetch(vector.GetProperty("responses"));
            var client = new HuggingFaceClient(null, "https://hf.example", scripted.Invoke);
            var expectedError = vector.GetProperty("error");
            var id = vector.GetProperty("id").GetString()!;

            if (expectedError.ValueKind != JsonValueKind.Null)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DetailsAsync(id));
                Assert.Equal(MessageOf(expectedError.GetString()!), error.Message);
            }
            else
            {
                var result = await client.DetailsAsync(id);
                var expected = vector.GetProperty("result");
                Assert.Equal(expected.GetProperty("id").GetString(), result.Id);
                Assert.Equal(expected.GetProperty("gated").ValueKind == JsonValueKind.False ? "false" : expected.GetProperty("gated").GetString(), result.Gated);
                var expectedQuantizations = expected.GetProperty("quantizations").EnumerateArray().ToList();
                Assert.Equal(expectedQuantizations.Count, result.Quantizations.Count);
                for (var index = 0; index < expectedQuantizations.Count; index++)
                {
                    Assert.Equal(expectedQuantizations[index].GetProperty("name").GetString(), result.Quantizations[index].Name);
                    Assert.Equal(
                        expectedQuantizations[index].TryGetProperty("size", out var size) ? size.GetDouble() : (double?)null,
                        result.Quantizations[index].Size);
                }
            }

            CheckRequests(vector.GetProperty("requests"), scripted.Requests);
        }
    }

    // ------------------------------------------------------------------ findHuggingFaceToken

    [Fact]
    public async Task FindHuggingFaceToken_MatchesTypeScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-llama-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var index = 0;
            foreach (var vector in Corpus.GetProperty("findHuggingFaceToken").EnumerateArray())
            {
                var caseRoot = Path.Combine(root, $"hf-case-{index}");
                Directory.CreateDirectory(caseRoot);
                foreach (var file in vector.GetProperty("files").EnumerateArray().Select(item => item.GetString()!))
                {
                    var full = Path.Combine(caseRoot, file.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, file == "direct/token" ? "path-token\n" : TokenContent(file));
                }

                var env = new Dictionary<string, string?>();
                foreach (var property in vector.GetProperty("env").EnumerateObject())
                {
                    env[property.Name] = property.Value.GetString()!.Replace("<tmp>", root);
                }

                var expected = vector.GetProperty("result");
                var token = await HuggingFace.FindHuggingFaceTokenAsync(env, Path.Combine(caseRoot, "home"));
                Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : expected.GetString(), token);
                index++;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TokenContent(string file) => file switch
    {
        "direct/token" => "path-token\n",
        "hf/token" => "hf-token",
        "xdg/huggingface/token" => "xdg-token",
        "home/.cache/huggingface/token" => "home-token",
        "emptyhf/token" => "   ",
        "hf2/token" => "loser",
        _ => "",
    };

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(26, Corpus.GetProperty("formatBytes").GetArrayLength());
        Assert.Equal(46, Corpus.GetProperty("normalizeLlamaServerUrl").GetArrayLength());
        Assert.Equal(6, Corpus.GetProperty("llamaClientList").GetArrayLength());
        Assert.Equal(29, Corpus.GetProperty("llamaClientWatch").GetArrayLength());
        Assert.Equal(11, Corpus.GetProperty("llamaClientLoadAndWait").GetArrayLength());
        Assert.Equal(9, Corpus.GetProperty("llamaClientDownloadAndWait").GetArrayLength());
        Assert.Equal(6, Corpus.GetProperty("huggingFaceSearch").GetArrayLength());
        Assert.Equal(8, Corpus.GetProperty("huggingFaceDetails").GetArrayLength());
        Assert.Equal(8, Corpus.GetProperty("findHuggingFaceToken").GetArrayLength());

        // The streaming sections keep their own blind spots visible.
        var watch = Corpus.GetProperty("llamaClientWatch").EnumerateArray().ToList();
        Assert.Contains(watch, item => item.GetProperty("error").ValueKind != JsonValueKind.Null);
        Assert.Contains(
            watch,
            item => item.GetProperty("queue").EnumerateArray()
                .Any(entry => entry.TryGetProperty("chunks", out var chunks)
                    && chunks.EnumerateArray().Any(chunk => chunk.ValueKind == JsonValueKind.Array)));
        Assert.Contains(watch, item => item.GetProperty("events").GetArrayLength() > 0);
        Assert.Contains(watch, item => item.GetProperty("events").GetArrayLength() == 0);

        // The poll-driven sections: an event-driven shortcut, a real poll, an error and a varied progress run.
        foreach (var name in new[] { "llamaClientLoadAndWait", "llamaClientDownloadAndWait" })
        {
            var section = Corpus.GetProperty(name).EnumerateArray().ToList();
            Assert.Contains(section, item => !item.GetProperty("assertRequests").GetBoolean());
            Assert.Contains(section, item => item.GetProperty("error").ValueKind != JsonValueKind.Null);
            Assert.Contains(section, item => item.GetProperty("progress").GetArrayLength() > 1);
        }

        // At least one scripted queue runs dry, so the repeat-the-last-entry fallback is exercised.
        Assert.Contains(
            Corpus.GetProperty("llamaClientDownloadAndWait").EnumerateArray(),
            item => item.GetProperty("exhausted").GetInt32() > 0);

        // Both URL error kinds are represented.
        var normalize = Corpus.GetProperty("normalizeLlamaServerUrl").EnumerateArray()
            .Where(item => item.GetProperty("error").ValueKind != JsonValueKind.Null)
            .Select(item => item.GetProperty("error").GetString()!)
            .ToList();
        Assert.Contains(normalize, error => error.StartsWith("TypeError:", StringComparison.Ordinal));
        Assert.Contains(normalize, error => error.StartsWith("Error:", StringComparison.Ordinal));

        // The IPv6 literals: at least one accepted, canonicalized and rejected shape each.
        var ipv6 = Corpus.GetProperty("normalizeLlamaServerUrl").EnumerateArray()
            .Where(item => item.GetProperty("input").GetString()!.Contains('[', StringComparison.Ordinal))
            .ToList();
        Assert.Contains(ipv6, item => item.GetProperty("output").ValueKind != JsonValueKind.Null);
        Assert.Contains(
            ipv6,
            item => item.GetProperty("output").GetString() is { } output
                && output.Contains(":8080", StringComparison.Ordinal));
        Assert.Contains(
            ipv6,
            item => item.GetProperty("output").GetString() is { } output
                && output.Contains("::", StringComparison.Ordinal));
        // The embedded-IPv4 tail is rewritten to its hexadecimal piece form.
        Assert.Contains(
            ipv6,
            item => item.GetProperty("output").GetString() is { } output
                && output.Contains("102:304", StringComparison.Ordinal));
        Assert.Contains(ipv6, item => item.GetProperty("error").ValueKind != JsonValueKind.Null);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class ScriptedStreamingFetch
    {
        private readonly JsonElement[] _queue;

        private int _index;

        public ScriptedStreamingFetch(JsonElement queue) => _queue = queue.EnumerateArray().ToArray();

        /// <summary>How often the queue ran dry. The catalog poll count is timing-dependent, so the last
        /// scripted entry repeats; the count keeps that from happening silently.</summary>
        public int Exhausted { get; private set; }

        public List<(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body)> Requests { get; } = [];

        public Task<LlamaStreamResponse> OpenStream(string url, LlamaHttpRequest request, CancellationToken cancellationToken)
        {
            Record(url, request);
            var entry = Next();
            if (TryGet(entry, "chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                return Task.FromResult(new LlamaStreamResponse(Status(entry), ByteChunks(chunks)));
            }

            // A non-streaming scripted response: the TS `new Response(entry.body ?? null)`. A null body
            // means `response.body === null`, which `watch()` reports as an error.
            return Task.FromResult(new LlamaStreamResponse(
                Status(entry),
                TryGet(entry, "body", out var body) && body.ValueKind == JsonValueKind.String
                    ? ByteChunks(body.GetString()!)
                    : null));
        }

        public Task<LlamaHttpResponse> Invoke(string url, LlamaHttpRequest request, CancellationToken cancellationToken)
        {
            Record(url, request);
            var entry = Next();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (TryGet(entry, "headers", out var headerElement))
            {
                foreach (var property in headerElement.EnumerateObject())
                {
                    headers[property.Name] = property.Value.GetString()!;
                }
            }

            var body = TryGet(entry, "body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()!
                : "";
            return Task.FromResult(new LlamaHttpResponse(Status(entry), body, headers));
        }

        private static bool TryGet(JsonElement entry, string name, out JsonElement value)
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out value))
            {
                return true;
            }

            value = default;
            return false;
        }

        private static int Status(JsonElement entry) =>
            TryGet(entry, "status", out var status) ? status.GetInt32() : 500;

        private static IAsyncEnumerable<byte[]> ByteChunks(JsonElement chunks) => ChunkIterator(chunks);

        private static async IAsyncEnumerable<byte[]> ChunkIterator(JsonElement chunks)
        {
            foreach (var chunk in chunks.EnumerateArray())
            {
                // The generator stores a chunk as a string when it is pure ASCII and as byte values when
                // the vector has to split a multi-byte character across chunks.
                yield return chunk.ValueKind == JsonValueKind.String
                    ? Encoding.UTF8.GetBytes(chunk.GetString()!)
                    : chunk.EnumerateArray().Select(value => (byte)value.GetInt32()).ToArray();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static IAsyncEnumerable<byte[]> ByteChunks(string body) => SingleChunk(body);

        private static async IAsyncEnumerable<byte[]> SingleChunk(string body)
        {
            yield return Encoding.UTF8.GetBytes(body);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private JsonElement Next()
        {
            if (_index < _queue.Length)
            {
                return _queue[_index++];
            }

            Exhausted++;
            return _queue.Length > 0 ? _queue[^1] : default;
        }

        private void Record(string url, LlamaHttpRequest request) =>
            Requests.Add((url, request.Method, request.Headers ?? new Dictionary<string, string>(), request.Body));
    }

    private sealed class ScriptedFetch
    {
        private readonly Queue<LlamaHttpResponse> _responses;

        public ScriptedFetch(JsonElement responses)
        {
            _responses = new Queue<LlamaHttpResponse>(responses.EnumerateArray().Select(ReadResponse));
        }

        public List<(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body)> Requests { get; } = [];

        public Task<LlamaHttpResponse> Invoke(string url, LlamaHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Add((url, request.Method, request.Headers ?? new Dictionary<string, string>(), request.Body));
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new LlamaHttpResponse(500, ""));
        }

        private static LlamaHttpResponse ReadResponse(JsonElement element)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.TryGetProperty("headers", out var headersElement))
            {
                foreach (var property in headersElement.EnumerateObject())
                {
                    headers[property.Name] = property.Value.GetString()!;
                }
            }

            return new LlamaHttpResponse(
                element.GetProperty("status").GetInt32(),
                element.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "",
                headers);
        }
    }

    private static void CheckRequests(JsonElement expected, IReadOnlyList<(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body)> actual)
    {
        var expectedList = expected.EnumerateArray().ToList();
        Assert.Equal(expectedList.Count, actual.Count);
        for (var index = 0; index < expectedList.Count; index++)
        {
            Assert.Equal(expectedList[index].GetProperty("url").GetString(), actual[index].Url);
            Assert.Equal(expectedList[index].GetProperty("method").GetString(), actual[index].Method);
            Assert.Equal(GetOptionalString(expectedList[index], "body"), actual[index].Body);

            // The corpus records the names as `new Headers()` normalizes them (lower-cased); the C# seam
            // keeps the spelling the source wrote. HTTP header names are case-insensitive, so compare them
            // folded — the set of names and every value still has to match exactly.
            var expectedHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in expectedList[index].GetProperty("headers").EnumerateObject())
            {
                expectedHeaders[property.Name.ToLowerInvariant()] = property.Value.GetString()!;
            }

            var actualHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in actual[index].Headers)
            {
                actualHeaders[name.ToLowerInvariant()] = value;
            }

            Assert.Equal(expectedHeaders.Count, actualHeaders.Count);
            foreach (var (name, value) in expectedHeaders)
            {
                Assert.True(actualHeaders.TryGetValue(name, out var actualValue), $"missing header {name}");
                Assert.Equal(value, actualValue);
            }
        }
    }

    private static void CheckEvents(string label, JsonElement expected, IReadOnlyList<LlamaModelEvent> actual)
    {
        var expectedList = expected.EnumerateArray().ToList();
        Assert.True(
            expectedList.Count == actual.Count,
            $"{label}: expected {expectedList.Count} events, got {actual.Count}");
        for (var index = 0; index < expectedList.Count; index++)
        {
            Assert.Equal(expectedList[index].GetProperty("model").GetString(), actual[index].Model);
            Assert.Equal(expectedList[index].GetProperty("event").GetString(), actual[index].Event);
            if (!expectedList[index].TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                // The TS payload had `data: undefined` (dropped by JSON.stringify) or `data: null`.
                Assert.Null(actual[index].Data);
                continue;
            }

            Assert.NotNull(actual[index].Data);
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(data.GetRawText()), actual[index].Data),
                $"{label} event #{index} data differs");
        }
    }

    /// <summary>
    /// Compares progress runs as multisets. `watch()` is fired in the background, so whether an
    /// event-driven progress lands before or after the poll-driven one is implementation-defined; the
    /// corpus records one interleaving. The set of messages, ratios and details still has to match
    /// exactly, so a dropped, duplicated or mis-shaped progress still fails.
    /// </summary>
    private static void CheckProgressSet(string label, JsonElement expected, IReadOnlyList<LlamaProgress> actual)
    {
        var expectedList = expected.EnumerateArray().Select(Describe).ToList();
        var actualList = actual.Select(Describe).ToList();
        Assert.Equal(expectedList.Count, actualList.Count);
        expectedList.Sort(StringComparer.Ordinal);
        actualList.Sort(StringComparer.Ordinal);
        Assert.True(
            expectedList.SequenceEqual(actualList, StringComparer.Ordinal),
            $"{label}: progress [{string.Join(", ", actualList)}] != [{string.Join(", ", expectedList)}]");
    }

    private static string Describe(JsonElement progress) => string.Join(
        "|",
        progress.GetProperty("message").GetString(),
        progress.TryGetProperty("ratio", out var ratio) && ratio.ValueKind == JsonValueKind.Number
            ? ratio.GetDouble().ToString("R", CultureInfo.InvariantCulture)
            : "-",
        progress.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
            ? detail.GetString()
            : "-");

    private static string Describe(LlamaProgress progress) => string.Join(
        "|",
        progress.Message,
        progress.Ratio?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        progress.Detail ?? "-");

    private static void CheckModel(string label, JsonElement expected, JsonObject actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), actual),
            $"{label}: model JSON differs: {actual.ToJsonString()}");

    private static (string Name, string Message) SplitError(string error)
    {
        var separator = error.IndexOf(": ", StringComparison.Ordinal);
        return (error[..separator], error[(separator + 2)..]);
    }

    private static string MessageOf(string error) => SplitError(error).Message;

    private static JsonNode? ToNode(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(element.GetRawText());

    private static bool? GetOptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetBoolean() : null;

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
