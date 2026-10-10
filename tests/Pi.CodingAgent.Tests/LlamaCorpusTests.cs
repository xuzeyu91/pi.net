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
        Assert.Equal(6, Corpus.GetProperty("huggingFaceSearch").GetArrayLength());
        Assert.Equal(8, Corpus.GetProperty("huggingFaceDetails").GetArrayLength());
        Assert.Equal(8, Corpus.GetProperty("findHuggingFaceToken").GetArrayLength());

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
