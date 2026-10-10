using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>Port of the TS <c>HuggingFaceModel</c>.</summary>
public sealed record HuggingFaceModel(string Id, double Downloads);

/// <summary>Port of the TS <c>HuggingFaceQuantization</c>.</summary>
public sealed record HuggingFaceQuantization(string Name, double? Size);

/// <summary>Port of the TS <c>HuggingFaceModelDetails</c> (<c>gated</c> is <c>false</c>, <c>auto</c> or <c>manual</c>).</summary>
public sealed record HuggingFaceModelDetails(string Id, string Gated, IReadOnlyList<HuggingFaceQuantization> Quantizations);

/// <summary>Port of <c>extensions/llama/huggingface.ts</c>.</summary>
public static partial class HuggingFace
{
    public const string DefaultHuggingFaceUrl = "https://huggingface.co";

    /// <summary>JS <c>Number.MAX_SAFE_INTEGER</c>, used as the missing-size sentinel in the sort.</summary>
    internal const double MaxSafeInteger = 9007199254740991;

    [GeneratedRegex("(?:^|[-_.])((?:UD-)?(?:IQ\\d(?:_[A-Z0-9]+)+|Q\\d(?:_[A-Z0-9]+)+|BF16|F16|F32|MXFP\\d(?:_[A-Z0-9]+)*))$",
        RegexOptions.IgnoreCase)]
    internal static partial Regex QuantizationPattern();

    [GeneratedRegex("-\\d{5}-of-\\d{5}$")]
    internal static partial Regex ShardSuffixPattern();

    [GeneratedRegex("(?:^|;)t=(\\d+)")]
    private static partial Regex RateLimitDelayPattern();

    [GeneratedRegex("/+$")]
    internal static partial Regex TrailingSlashes();

    /// <summary>The TS <c>payloadError(payload, fallback)</c>.</summary>
    public static string PayloadError(JsonNode? payload, string fallback)
    {
        if (payload is not JsonObject obj)
        {
            return fallback;
        }

        var error = obj["error"];
        return error is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : fallback;
    }

    /// <summary>The TS <c>parseRateLimitDelay(value)</c>.</summary>
    public static double? ParseRateLimitDelay(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var match = RateLimitDelayPattern().Match(value);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The TS <c>findHuggingFaceToken(env)</c>. <paramref name="env"/> maps environment names to
    /// values; <paramref name="homeDir"/> stands in for <c>homedir()</c> (both injectable, difference C121).
    /// </summary>
    public static async Task<string?> FindHuggingFaceTokenAsync(
        IReadOnlyDictionary<string, string?>? env = null,
        string? homeDir = null)
    {
        env ??= new Dictionary<string, string?>();
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var fromEnvironment = Get(env, "HF_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        var paths = new List<string>();
        AddPath(paths, Get(env, "HF_TOKEN_PATH"));
        if (Get(env, "HF_HOME") is { } hfHome)
        {
            AddPath(paths, NodePath.Join(hfHome, "token"));
        }

        if (Get(env, "XDG_CACHE_HOME") is { } xdgCache)
        {
            AddPath(paths, NodePath.Join(xdgCache, "huggingface", "token"));
        }

        AddPath(paths, NodePath.Join(homeDir, ".cache", "huggingface", "token"));

        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var token = await ReadTokenAsync(path).ConfigureAwait(false);
            if (token is not null)
            {
                return token;
            }
        }

        return null;
    }

    private static void AddPath(List<string> paths, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            paths.Add(path);
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string?> env, string name) =>
        env.TryGetValue(name, out var value) ? value : null;

    private static async Task<string?> ReadTokenAsync(string path)
    {
        try
        {
            var token = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Port of the TS <c>HuggingFaceClient</c>.</summary>
public sealed class HuggingFaceClient
{
    private const int RequestTimeoutMs = 15_000;

    private readonly string? _token;
    private readonly string _baseUrl;
    private readonly LlamaFetch _fetch;

    public HuggingFaceClient(string? token = null, string baseUrl = HuggingFace.DefaultHuggingFaceUrl, LlamaFetch? fetch = null)
    {
        _token = token;
        _baseUrl = HuggingFace.TrailingSlashes().Replace(baseUrl, "");
        _fetch = fetch ?? LlamaFetchDefaults.Fetch;
    }

    private async Task<JsonNode?> RequestAsync(string path, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_token is not null)
        {
            headers["Authorization"] = $"Bearer {_token}";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeoutMs);
        var response = await _fetch($"{_baseUrl}{path}", new LlamaHttpRequest("GET", Headers: headers), timeout.Token)
            .ConfigureAwait(false);
        var payload = response.Json();
        if (!response.Ok)
        {
            var fallback = $"Hugging Face returned HTTP {response.Status}";
            if (response.Status == 429)
            {
                var retryAfter = ParseJsNumber(response.Header("retry-after"));
                var delay = retryAfter is not null and not 0
                    ? retryAfter
                    : HuggingFace.ParseRateLimitDelay(response.Header("ratelimit"));
                throw new InvalidOperationException(delay is not null
                    ? $"Hugging Face rate limit reached; retry in {JsNumber(delay.Value)}s"
                    : "Hugging Face rate limit reached");
            }

            throw new InvalidOperationException(HuggingFace.PayloadError(payload, fallback));
        }

        return payload;
    }

    /// <summary>The TS <c>search(query, signal)</c>.</summary>
    public async Task<IReadOnlyList<HuggingFaceModel>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("search", query),
            new("filter", "gguf"),
            new("sort", "downloads"),
            new("direction", "-1"),
            new("limit", "20"),
        };
        var payload = await RequestAsync($"/api/models?{UrlSearchParams(parameters)}", cancellationToken).ConfigureAwait(false);
        if (payload is not JsonArray array)
        {
            throw new InvalidOperationException("Hugging Face returned invalid search results");
        }

        var models = new List<HuggingFaceModel>();
        foreach (var value in array)
        {
            if (value is not JsonObject obj || obj["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id))
            {
                continue;
            }

            var downloads = obj["downloads"] is JsonValue downloadsValue && downloadsValue.TryGetValue<double>(out var number)
                ? number
                : 0;
            models.Add(new HuggingFaceModel(id, downloads));
        }

        return models;
    }

    /// <summary>The TS <c>details(id, signal)</c>.</summary>
    public async Task<HuggingFaceModelDetails> DetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        var encodedId = string.Join("/", id.Split('/').Select(JsUri.EncodeUriComponent));
        var payload = await RequestAsync($"/api/models/{encodedId}?blobs=true", cancellationToken).ConfigureAwait(false);
        // TS checks `typeof payload !== "object" || payload === null`; an array passes (arrays are
        // objects) and simply has no `id`/`gated`/`siblings`, so only a scalar/null throws.
        if (payload is null || payload is JsonValue)
        {
            throw new InvalidOperationException("Hugging Face returned invalid model details");
        }

        var model = payload as JsonObject;

        var sizes = new Dictionary<string, (double Total, bool Complete)>(StringComparer.Ordinal);
        if (model?["siblings"] is JsonArray siblings)
        {
            foreach (var value in siblings)
            {
                if (value is not JsonObject file)
                {
                    continue;
                }

                if (file["rfilename"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var rfilename)
                    || !rfilename.ToLowerInvariant().EndsWith(".gguf", StringComparison.Ordinal))
                {
                    continue;
                }

                var filename = rfilename.Split('/')[^1];
                if (filename.ToLowerInvariant().StartsWith("mmproj", StringComparison.Ordinal))
                {
                    continue;
                }

                var stem = HuggingFace.ShardSuffixPattern().Replace(filename[..^5], "");
                var match = HuggingFace.QuantizationPattern().Match(stem);
                if (!match.Success)
                {
                    continue;
                }

                var quantization = match.Groups[1].Value.ToUpperInvariant();
                var current = sizes.TryGetValue(quantization, out var existing) ? existing : (Total: 0.0, Complete: true);
                if (file["size"] is JsonValue sizeValue && sizeValue.TryGetValue<double>(out var size))
                {
                    current.Total += size;
                }
                else
                {
                    current.Complete = false;
                }

                sizes[quantization] = current;
            }
        }

        var quantizations = sizes
            .Select(pair => new HuggingFaceQuantization(pair.Key, pair.Value.Complete ? pair.Value.Total : null))
            .OrderBy(quantization => quantization, Comparer<HuggingFaceQuantization>.Create(CompareQuantizations))
            .ToList();

        var gated = model?["gated"] is JsonValue gatedValue && gatedValue.TryGetValue<string>(out var gatedText)
            && gatedText is "auto" or "manual"
            ? gatedText
            : "false";

        return new HuggingFaceModelDetails(
            model?["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var modelId) ? modelId : id,
            gated,
            quantizations);
    }

    private static int CompareQuantizations(HuggingFaceQuantization left, HuggingFaceQuantization right)
    {
        if (left.Name == "Q4_K_M")
        {
            return -1;
        }

        if (right.Name == "Q4_K_M")
        {
            return 1;
        }

        var bySize = (left.Size ?? HuggingFace.MaxSafeInteger).CompareTo(right.Size ?? HuggingFace.MaxSafeInteger);
        return bySize != 0 ? bySize : string.CompareOrdinal(left.Name, right.Name);
    }

    private static string UrlSearchParams(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters.Select(pair => $"{FormEncode(pair.Key)}={FormEncode(pair.Value)}"));

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
                builder.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>The TS <c>Number(value)</c>; null when the value is absent or NaN.</summary>
    private static double? ParseJsNumber(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static string JsNumber(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
}
