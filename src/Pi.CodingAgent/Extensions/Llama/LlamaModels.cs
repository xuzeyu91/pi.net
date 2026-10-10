using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>Port of the TS <c>LlamaModelStatus</c> value union.</summary>
public static class LlamaModelStatusValue
{
    public const string Unloaded = "unloaded";

    public const string Loading = "loading";

    public const string Loaded = "loaded";

    public const string Downloading = "downloading";

    public const string Sleeping = "sleeping";
}

/// <summary>
/// A llama.cpp router model. Port of the TS <c>LlamaModelInfo</c>; the raw JSON is kept so every
/// field survives even before the typed accessors are needed (differences C119).
/// </summary>
public sealed record LlamaModelInfo(JsonObject Raw)
{
    public string Id => Raw["id"]?.GetValue<string>() ?? "";

    public JsonObject? StatusRaw => Raw["status"] as JsonObject;

    public string StatusValue => StatusRaw?["value"]?.GetValue<string>() ?? "";

    public bool StatusFailed => StatusRaw?["failed"]?.GetValue<bool>() ?? false;

    public int? StatusExitCode => StatusRaw?["exit_code"] is JsonValue value && value.TryGetValue<int>(out var code) ? code : null;

    public IReadOnlyList<string>? Aliases => Raw["aliases"] is JsonArray array
        ? array.Select(node => node?.GetValue<string>() ?? "").ToList()
        : null;

    public string? Source => Raw["source"]?.GetValue<string>();
}

/// <summary>Port of the TS <c>LlamaServerProps</c>.</summary>
public sealed record LlamaServerProps
{
    public bool? ModelsAutoload { get; init; }

    public string? ChatTemplate { get; init; }
}

/// <summary>Port of the TS <c>LlamaProgress</c>.</summary>
public sealed record LlamaProgress
{
    public required string Message { get; init; }

    public double? Ratio { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// Port of the TS <c>LlamaModelEvent</c>: one frame off the <c>/models/sse</c> stream. Only frames whose
/// <c>model</c> and <c>event</c> are both strings reach a watcher, which is what makes
/// <see cref="LlamaModelEvent"/> non-nullable here.
/// </summary>
public sealed record LlamaModelEvent(string Model, string Event, JsonNode? Data);

/// <summary>The pure helpers and progress parsers of <c>extensions/llama/client.ts</c>.</summary>
public static partial class LlamaModels
{
    [GeneratedRegex("/+$")]
    private static partial Regex TrailingSlashes();

    [GeneratedRegex("/v1$")]
    private static partial Regex TrailingV1();

    [GeneratedRegex("/$")]
    private static partial Regex TrailingSlash();

    /// <summary>The TS <c>errorMessage(payload, fallback)</c>: a nested <c>error.message</c> or the fallback.</summary>
    public static string ErrorMessage(JsonNode? payload, string fallback)
    {
        if (payload is not JsonObject obj)
        {
            return fallback;
        }

        if (obj["error"] is not JsonObject error)
        {
            return fallback;
        }

        var message = error["message"];
        return message is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : fallback;
    }

    /// <summary>The TS <c>isModelInfo</c>: an object with a string <c>id</c> and a string <c>status.value</c>.</summary>
    public static bool IsModelInfo(JsonNode? value) =>
        value is JsonObject obj
        && obj["id"] is JsonValue id && id.TryGetValue<string>(out _)
        && obj["status"] is JsonObject status
        && status["value"] is JsonValue statusValue && statusValue.TryGetValue<string>(out _);

    /// <summary>The TS <c>formatBytes</c>.</summary>
    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
        {
            return $"{JsNumber(bytes)} B";
        }

        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var value = bytes / 1024;
        var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++)
        {
            value /= 1024;
            unit = units[index];
        }

        return $"{ToFixed(value, value >= 10 ? 1 : 2)} {unit}";
    }

    /// <summary>
    /// The TS <c>normalizeLlamaServerUrl</c>: strip the hash/query, trailing slashes and a trailing
    /// <c>/v1</c>, then drop the final slash. Uses <see cref="JsUrl"/> rather than <see cref="Uri"/>
    /// so the WHATWG serializer matches (difference C120).
    /// </summary>
    public static string NormalizeLlamaServerUrl(string value)
    {
        var url = JsUrl.TryParse(value.Trim())
            ?? throw new UriFormatException("Invalid URL");
        if (url.Protocol is not ("http:" or "https:"))
        {
            throw new InvalidOperationException("Server URL must use http or https");
        }

        var pathname = TrailingSlashes().Replace(url.Pathname, "");
        pathname = TrailingV1().Replace(pathname, "");
        if (pathname.Length == 0)
        {
            pathname = "/";
        }

        var normalized = url with { Hash = "", Query = "", Pathname = pathname };
        return TrailingSlash().Replace(JsUrl.Serialize(normalized), "");
    }

    /// <summary>The TS <c>llamaInferenceUrl</c>.</summary>
    public static string LlamaInferenceUrl(string serverUrl) => $"{NormalizeLlamaServerUrl(serverUrl)}/v1";

    /// <summary>The TS <c>parseLoadProgress</c>.</summary>
    public static LlamaProgress? ParseLoadProgress(JsonNode? data)
    {
        if (data is not JsonObject obj || obj["progress"] is not JsonObject progress)
        {
            return null;
        }

        var stage = progress["current"] is JsonValue current && current.TryGetValue<string>(out var currentText)
            ? currentText
            : progress["stage"] is JsonValue stageValue && stageValue.TryGetValue<string>(out var stageText)
                ? stageText
                : null;
        var stages = progress["stages"] is JsonArray array
            ? array.Where(node => node is JsonValue value && value.TryGetValue<string>(out _)).Select(node => node!.GetValue<string>()).ToList()
            : [];
        double? stageRatio = progress["value"] is JsonValue ratio && ratio.TryGetValue<double>(out var ratioValue)
            ? Math.Max(0, Math.Min(1, ratioValue))
            : null;

        var finalRatio = stageRatio;
        if (stage is not null && stages.Count > 0)
        {
            var index = stages.IndexOf(stage);
            if (index >= 0)
            {
                finalRatio = (index + (stageRatio ?? 0)) / stages.Count;
            }
        }

        return new LlamaProgress
        {
            Message = stage is not null ? $"Loading {stage.Replace("_", " ")}" : "Loading model",
            Ratio = finalRatio,
        };
    }

    /// <summary>The TS <c>parseDownloadProgress</c>.</summary>
    public static LlamaProgress? ParseDownloadProgress(JsonNode? data)
    {
        if (data is not JsonObject and not JsonArray)
        {
            return null;
        }

        var nested = (data as JsonObject)?["progress"];
        var files = nested is JsonObject or JsonArray ? nested : data;
        double done = 0;
        double total = 0;
        foreach (var value in ObjectValues(files))
        {
            if (value is not JsonObject entry)
            {
                continue;
            }

            if (entry["done"] is not JsonValue doneValue || !doneValue.TryGetValue<double>(out var entryDone))
            {
                continue;
            }

            if (entry["total"] is not JsonValue totalValue || !totalValue.TryGetValue<double>(out var entryTotal))
            {
                continue;
            }

            done += entryDone;
            total += entryTotal;
        }

        if (total <= 0)
        {
            return null;
        }

        return new LlamaProgress
        {
            Message = "Downloading model",
            Ratio = done / total,
            Detail = $"{FormatBytes(done)} / {FormatBytes(total)}",
        };
    }

    private static IEnumerable<JsonNode?> ObjectValues(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Select(pair => pair.Value),
        JsonArray array => array,
        _ => [],
    };

    /// <summary>The TS number-to-string coercion for the whole-number branch of <c>formatBytes</c>.</summary>
    private static string JsNumber(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The TS <c>Number.prototype.toFixed</c> (round half away from zero).</summary>
    private static string ToFixed(double value, int digits) =>
        value.ToString($"F{digits}", CultureInfo.InvariantCulture);
}
