// ============================================================================
// Crash log — port of core/crash-log.ts (4e-1)
// ============================================================================
//
// A tiny bounded journal (`<agentDir>/crashes.json`) that survives a hard exit, so the next start can
// tell the user what went wrong and which extension was on the stack. Everything here is best-effort:
// a failure to read or write never propagates, because the caller is already crashing.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>The two kinds of record. TS <c>CrashRecord["kind"]</c>.</summary>
public static class CrashKind
{
    /// <summary>An exception escaped the top-level handler.</summary>
    public const string UncaughtException = "uncaught_exception";

    /// <summary>A fatal error was reported through the process error path.</summary>
    public const string FatalError = "fatal_error";
}

/// <summary>One recorded crash. Port of the TS <c>CrashRecord</c>.</summary>
/// <remarks>
/// <para>
/// The TS type declares seven required members, but the reader's guard only checks that <c>timestamp</c>
/// and <c>message</c> are strings — everything else is whatever the file happened to hold. A hand-edited
/// entry with two keys therefore survives a read/write round trip unchanged, and the port has to
/// represent that. Rather than give every member a default that the file never carried, the record
/// <em>is</em> the member bag, with typed accessors on top: a record built by <see cref="CrashLog.Record"/>
/// carries all seven members, one read from a file carries exactly what was there, in file order.
/// </para>
/// <para>
/// This is why there is no <c>JsonExtensionData</c> catch-all alongside typed properties: unknown members
/// and their position are preserved by construction, so the difference recorded for the first cut of this
/// port no longer exists.
/// </para>
/// </remarks>
public sealed record CrashRecord
{
    /// <summary>The members exactly as the file held them, in order.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Members { get; init; }

    /// <summary>ISO-8601 instant the crash happened. TS <c>timestamp</c>.</summary>
    [JsonIgnore]
    public string Timestamp => Text("timestamp") ?? "";

    /// <summary>Version of the package that crashed, absent when the file omitted it.</summary>
    [JsonIgnore]
    public string? Version => Text("version");

    /// <summary>One of <see cref="CrashKind"/>, absent when the file omitted it.</summary>
    [JsonIgnore]
    public string? Kind => Text("kind");

    /// <summary>The error message.</summary>
    [JsonIgnore]
    public string Message => Text("message") ?? "";

    /// <summary>The stack trace, or <see langword="null"/> when there was none.</summary>
    [JsonIgnore]
    public string? Stack => Text("stack");

    /// <summary>The session file active at the time, or <see langword="null"/>.</summary>
    [JsonIgnore]
    public string? SessionFile => Text("sessionFile");

    /// <summary>Working directory at the time, absent when the file omitted it.</summary>
    [JsonIgnore]
    public string? Cwd => Text("cwd");

    /// <summary>Set once the next start has announced this crash. TS <c>record.notified</c> truthiness.</summary>
    /// <remarks>
    /// TS reads the member with `!record.notified`, so *any* truthy JSON value counts — a file written by
    /// hand with <c>"notified": "yes"</c> is announced already. Modelling it as "the member is exactly
    /// <c>true</c>" would announce it a second time.
    /// </remarks>
    [JsonIgnore]
    public bool Notified =>
        Members is not null &&
        Members.TryGetValue("notified", out var value) &&
        IsTruthy(value);

    /// <summary>
    /// Mark the record as announced. TS's <c>{ ...record, notified: true }</c> spread keeps the member's
    /// original position when it already exists and appends it otherwise, which is what assigning through
    /// the indexer does.
    /// </summary>
    public CrashRecord WithNotified()
    {
        var members = new Dictionary<string, JsonElement>(Members ?? [], StringComparer.Ordinal)
        {
            ["notified"] = JsonSerializer.SerializeToElement(true),
        };
        return this with { Members = members };
    }

    /// <summary>JS <c>Boolean(value)</c> for a parsed JSON value.</summary>
    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => value.GetString() is { Length: > 0 },
        _ => true,
    };

    private string? Text(string name) =>
        Members is not null &&
        Members.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The input to <see cref="CrashLog.Record"/>. TS inline record.</summary>
public sealed record CrashInput
{
    /// <summary>One of <see cref="CrashKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The thrown value; TS types it <c>unknown</c>.</summary>
    public object? Error { get; init; }

    /// <summary>The session file active at the time.</summary>
    public string? SessionFile { get; init; }

    /// <summary>Working directory at the time.</summary>
    public required string Cwd { get; init; }
}

/// <summary>The fields of an extension a stack trace can be matched against. TS <c>Pick&lt;Extension, …&gt;</c>.</summary>
public sealed record ExtensionStackMetadata(string Path, string ResolvedPath, SourceInfo SourceInfo)
{
    /// <summary>Project a loaded extension onto the metadata this module reads.</summary>
    public static ExtensionStackMetadata From(Extension extension) =>
        new(extension.Path, extension.ResolvedPath, extension.SourceInfo);
}

/// <summary>Port of <c>core/crash-log.ts</c>.</summary>
public static partial class CrashLog
{
    private const int MaxCrashRecords = 5;
    private const long MaxAgeMs = 7L * 24 * 60 * 60 * 1000;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The default log path: <c>&lt;agentDir&gt;/crashes.json</c>.</summary>
    public static string DefaultPath(string? agentDir = null) =>
        NodePath.Join(agentDir ?? Config.GetAgentDir(), "crashes.json");

    /// <summary>Read the log, dropping entries that do not carry a string <c>timestamp</c> and <c>message</c>.</summary>
    public static IReadOnlyList<CrashRecord> Read(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            // `readFileSync(path, "utf8")` keeps a byte-order mark, and `JSON.parse` then rejects it.
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetString(File.ReadAllBytes(path));
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var records = new List<CrashRecord>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !IsString(element, "timestamp") ||
                    !IsString(element, "message"))
                {
                    continue;
                }

                if (element.Deserialize<CrashRecord>(WriteOptions) is { } record)
                {
                    records.Add(record);
                }
            }

            return records;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Record a crash, keeping only the newest <see cref="MaxCrashRecords"/> entries.</summary>
    public static CrashRecord? Record(CrashInput crash, string? path = null) =>
        Record(crash, path, DateTimeOffset.UtcNow);

    /// <summary>The seam-taking overload: <paramref name="now"/> replaces <c>new Date()</c>.</summary>
    internal static CrashRecord? Record(CrashInput crash, string? path, DateTimeOffset now)
    {
        path ??= DefaultPath();
        try
        {
            // The TS object literal spells out every member, `stack` and `sessionFile` included as
            // explicit nulls, so a freshly recorded crash always carries all seven. `stack` follows the
            // TS `error instanceof Error && error.stack ? … : null`, which also treats an empty trace as
            // absent.
            var stack = (crash.Error as Exception)?.StackTrace is { Length: > 0 } trace ? trace : null;
            var record = new CrashRecord
            {
                Members = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["timestamp"] = JsonSerializer.SerializeToElement(
                        now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                    ["version"] = JsonSerializer.SerializeToElement(Config.Version),
                    ["kind"] = JsonSerializer.SerializeToElement(crash.Kind),
                    ["message"] = JsonSerializer.SerializeToElement(ErrorMessage(crash.Error)),
                    ["stack"] = JsonSerializer.SerializeToElement(stack),
                    ["sessionFile"] = JsonSerializer.SerializeToElement(crash.SessionFile),
                    ["cwd"] = JsonSerializer.SerializeToElement(crash.Cwd),
                },
            };

            var records = Read(path).ToList();
            records.Add(record);
            Write(records.Count > MaxCrashRecords ? records[^MaxCrashRecords..] : records, path);
            return record;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest crash younger than a week that has not been announced yet, marking every pending
    /// record as announced so it is shown once.
    /// </summary>
    public static CrashRecord? TakeUnnotified(string? path = null, long? now = null)
    {
        path ??= DefaultPath();
        var currentMs = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var records = Read(path);
        var crash = records
            .Reverse()
            .FirstOrDefault(record =>
                !record.Notified &&
                ParseTimestamp(record.Timestamp) is { } timestamp &&
                currentMs - timestamp <= MaxAgeMs);
        if (crash is null)
        {
            return null;
        }

        try
        {
            Write(records.Select(record => record.Notified ? record : record.WithNotified()).ToList(), path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Showing the notice again is harmless.
        }

        return crash;
    }

    /// <summary>Delete the log. Missing files are not an error.</summary>
    public static void Clear(string? path = null)
    {
        path ??= DefaultPath();
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The records can be attached again if cleanup fails.
        }
    }

    /// <summary>
    /// The labels of the loaded extensions whose source files appear in <paramref name="stack"/>, in
    /// extension order and deduplicated by label.
    /// </summary>
    public static IReadOnlyList<string> FindExtensionStackMatches(
        string? stack,
        IReadOnlyList<ExtensionStackMetadata> extensions)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return [];
        }

        var lines = stack.Split('\n')
            .Skip(1)
            .Where(line => AtFramePattern().IsMatch(line))
            .Select(DecodeUriLenient)
            .ToList();
        var normalizedStack = string.Join('\n', lines).Replace('\\', '/');

        var matches = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            var resolvedPath = NormalizeStackPath(extension.ResolvedPath);
            var source = extension.SourceInfo.Source;
            var singleFilePackage =
                extension.SourceInfo.Origin == SourceOrigin.Package &&
                !PackageSourcePattern().IsMatch(source) &&
                ScriptExtensionPattern().IsMatch(source);
            var packageRoot =
                extension.SourceInfo.Origin == SourceOrigin.Package && !singleFilePackage &&
                extension.SourceInfo.BaseDir is { Length: > 0 } baseDir
                    ? baseDir
                    : null;
            var slashIndex = resolvedPath.LastIndexOf('/');
            var directoryEntry = IndexEntryPattern().IsMatch(resolvedPath);

            var matched = packageRoot is not null
                ? StackContainsPath(normalizedStack, packageRoot, includeDescendants: true)
                : directoryEntry && slashIndex != -1
                    ? StackContainsPath(normalizedStack, resolvedPath[..slashIndex], includeDescendants: true)
                    : StackContainsPath(normalizedStack, resolvedPath, includeDescendants: false);
            if (!matched)
            {
                continue;
            }

            var label = extension.SourceInfo.Origin == SourceOrigin.Package && source.Length > 0
                ? source
                : extension.Path;
            if (seen.Add(label))
            {
                matches.Add(label);
            }
        }

        return matches;
    }

    private static void Write(IReadOnlyList<CrashRecord> records, string path)
    {
        var directory = NodePath.Dirname(path);
        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(records, WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool IsString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static long? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;

    /// <summary>TS <c>error instanceof Error ? error.message || error.name : String(error)</c>.</summary>
    /// <remarks>
    /// The JS <c>error.name</c> fallback is the constructor name (<c>"TypeError"</c>); the C# analogue is
    /// the exception class name. <c>String(value)</c> has no exact counterpart either: JS has
    /// <c>undefined</c> (which stringifies to <c>"undefined"</c>) and object <c>toString</c> defaults,
    /// whereas the port only ever sees what the caller passed as <c>object?</c>.
    /// </remarks>
    private static string ErrorMessage(object? error) => error switch
    {
        Exception exception => exception.Message.Length > 0 ? exception.Message : exception.GetType().Name,
        null => "null",
        string text => text,
        _ => error.ToString() ?? "",
    };

    /// <summary>TS <c>value.replace(/\\/g, "/").replace(/\/+$/u, "")</c>.</summary>
    private static string NormalizeStackPath(string value) => value.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Whether a stack contains a path. With <paramref name="includeDescendants"/> any descendant
    /// counts; otherwise the path must end at a boundary so <c>/a/b</c> does not match <c>/a/bc</c>.
    /// </summary>
    private static bool StackContainsPath(string stack, string targetPath, bool includeDescendants)
    {
        var target = NormalizeStackPath(targetPath);
        if (target.Length == 0 || SourceInfos.IsSyntheticPath(target))
        {
            return false;
        }

        var caseInsensitive = DriveLetterPattern().IsMatch(target);
        var haystack = caseInsensitive ? stack.ToLowerInvariant() : stack;
        var needle = caseInsensitive ? target.ToLowerInvariant() : target;
        if (includeDescendants)
        {
            return haystack.Contains(needle + "/", StringComparison.Ordinal);
        }

        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index != -1)
        {
            var nextIndex = index + needle.Length;
            if (nextIndex >= haystack.Length)
            {
                return true;
            }

            var next = haystack[nextIndex];
            if (next is ':' or ')' || JsString.IsWhitespace(next))
            {
                return true;
            }

            index = haystack.IndexOf(needle, nextIndex, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>TS <c>try { decodeURI(line) } catch { line }</c>.</summary>
    private static string DecodeUriLenient(string line)
    {
        try
        {
            return JsUri.DecodeUri(line);
        }
        catch (UriFormatException)
        {
            return line;
        }
    }

    /// <summary>The TS <c>/^\s+at\s/u</c> test.</summary>
    [GeneratedRegex("^" + JsRegex.WhitespaceClass + "+at" + JsRegex.WhitespaceClass)]
    private static partial Regex AtFramePattern();

    /// <summary>The TS <c>/^[a-z]:\//iu</c> test, which selects case-insensitive comparison.</summary>
    [GeneratedRegex("^[a-z]:/", RegexOptions.IgnoreCase)]
    private static partial Regex DriveLetterPattern();

    /// <summary>The TS <c>/^(?:npm:|git:|https?:\/\/|ssh:\/\/)/u</c> test.</summary>
    [GeneratedRegex("^(?:npm:|git:|https?://|ssh://)")]
    private static partial Regex PackageSourcePattern();

    /// <summary>The TS <c>/\.[cm]?[jt]s$/u</c> test.</summary>
    [GeneratedRegex(@"\.[cm]?[jt]s$")]
    private static partial Regex ScriptExtensionPattern();

    /// <summary>The TS <c>/\/index\.[cm]?[jt]s$/u</c> test.</summary>
    [GeneratedRegex(@"/index\.[cm]?[jt]s$")]
    private static partial Regex IndexEntryPattern();
}
