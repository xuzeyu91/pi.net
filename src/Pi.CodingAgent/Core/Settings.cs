using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pi.CodingAgent.Core;

/// <summary>
/// The persisted settings document. Port of the TS <c>Settings</c> interface
/// (<c>core/settings-manager.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// The TS interface is a bag of ~60 optional fields whose values are never validated on load: getters
/// coerce or fall back. The port keeps that permissiveness by typing only the plain fields and leaving
/// the union-typed and nested-object fields as <see cref="JsonObject"/> / <see cref="JsonNode"/>, so a
/// hand-edited settings file round-trips exactly and malformed values surface where TS surfaces them
/// (in the getter) rather than at load time.
/// </para>
/// <para>
/// Enum-valued fields (<c>defaultThinkingLevel</c>, <c>modelThinkingLevels</c>, <c>transport</c>,
/// <c>tuiMode</c>, …) are <see cref="string"/> rather than enums, because TS accepts any string there.
/// Use <see cref="ThinkingLevels"/> to convert at the boundary.
/// </para>
/// </remarks>
public sealed record Settings
{
    public string? LastChangelogVersion { get; init; }

    public string? DefaultProvider { get; init; }

    public string? DefaultModel { get; init; }

    public string? DefaultThinkingLevel { get; init; }

    /// <summary>Per-model thinking-level overrides keyed by <c>provider/modelId</c>.</summary>
    public JsonObject? ModelThinkingLevels { get; init; }

    public string? Transport { get; init; }

    public string? SteeringMode { get; init; }

    public string? FollowUpMode { get; init; }

    public string? Theme { get; init; }

    public JsonObject? Compaction { get; init; }

    public JsonObject? BranchSummary { get; init; }

    public JsonObject? Retry { get; init; }

    public bool? HideThinkingBlock { get; init; }

    public bool? ShowCacheMissNotices { get; init; }

    public string? ExternalEditor { get; init; }

    public string? ShellPath { get; init; }

    /// <summary><c>true</c> hides all startup output; <c>"header"</c> keeps only the header.</summary>
    public JsonNode? QuietStartup { get; init; }

    public string? DefaultProjectTrust { get; init; }

    public string? ShellCommandPrefix { get; init; }

    public IReadOnlyList<string>? NpmCommand { get; init; }

    public bool? CollapseChangelog { get; init; }

    public bool? EnableInstallTelemetry { get; init; }

    public bool? EnableAnalytics { get; init; }

    public string? TrackingId { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>npm/git package sources: a string, or an object with filtering.</summary>
    public IReadOnlyList<JsonNode?>? Packages { get; init; }

    public IReadOnlyList<string>? Extensions { get; init; }

    public IReadOnlyList<string>? Skills { get; init; }

    public IReadOnlyList<string>? Prompts { get; init; }

    public IReadOnlyList<string>? Themes { get; init; }

    public bool? EnableSkillCommands { get; init; }

    public JsonObject? Terminal { get; init; }

    public JsonObject? Images { get; init; }

    public IReadOnlyList<string>? EnabledModels { get; init; }

    /// <summary>Initial tool selection; <c>+name</c>/<c>-name</c> entries modify the inherited selection.</summary>
    public IReadOnlyList<string>? DefaultTools { get; init; }

    public string? DoubleEscapeAction { get; init; }

    public string? TreeFilterMode { get; init; }

    public JsonObject? ThinkingBudgets { get; init; }

    public double? EditorPaddingX { get; init; }

    public double? OutputPad { get; init; }

    public double? AutocompleteMaxVisible { get; init; }

    public bool? ShowHardwareCursor { get; init; }

    public JsonObject? Markdown { get; init; }

    public JsonObject? Warnings { get; init; }

    public JsonObject? Codemode { get; init; }

    public string? SessionDir { get; init; }

    public string? HttpProxy { get; init; }

    public double? HttpIdleTimeoutMs { get; init; }

    public string? CacheWarming { get; init; }

    public double? WebsocketConnectTimeoutMs { get; init; }

    public string? TuiMode { get; init; }

    public string? FullscreenExitOutput { get; init; }

    public string? FullscreenScrollbar { get; init; }

    public bool? FullscreenCopyOnSelect { get; init; }

    /// <summary>A line count, or the string <c>"auto"</c>.</summary>
    public JsonNode? FullscreenWheelScrollLines { get; init; }
}

/// <summary>JSON conventions for the settings/auth/models files. Port of <c>JSON.stringify(x, null, 2)</c>.</summary>
internal static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // JSON.stringify leaves non-ASCII, <, >, & and ' alone; the default encoder escapes them.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Serialize with a two-space indent, matching <c>JSON.stringify(value, null, 2)</c>.</summary>
    public static string Stringify(object? value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// Conversions between the thinking-level wire text and <see cref="Pi.Ai.Types.ThinkingLevel"/>.
/// </summary>
/// <remarks>
/// The enum name <c>XHigh</c> does not map to the wire text <c>"xhigh"</c> under any naming policy, so
/// the mapping is explicit. (4e-2b gave the enum itself matching
/// <c>[JsonStringEnumMemberName]</c> attributes, but this helper is still the seam the settings layer
/// uses for parsing, where an unknown string must yield null rather than throw.) TS settings accept any
/// string; <see cref="Parse"/> returns null for an unknown one rather than throwing.
/// </remarks>
public static class ThinkingLevels
{
    public static string ToText(Pi.Ai.Types.ThinkingLevel level) => level switch
    {
        Pi.Ai.Types.ThinkingLevel.Off => "off",
        Pi.Ai.Types.ThinkingLevel.Minimal => "minimal",
        Pi.Ai.Types.ThinkingLevel.Low => "low",
        Pi.Ai.Types.ThinkingLevel.Medium => "medium",
        Pi.Ai.Types.ThinkingLevel.High => "high",
        Pi.Ai.Types.ThinkingLevel.XHigh => "xhigh",
        _ => "max",
    };

    public static Pi.Ai.Types.ThinkingLevel? Parse(string? text) => text switch
    {
        "off" => Pi.Ai.Types.ThinkingLevel.Off,
        "minimal" => Pi.Ai.Types.ThinkingLevel.Minimal,
        "low" => Pi.Ai.Types.ThinkingLevel.Low,
        "medium" => Pi.Ai.Types.ThinkingLevel.Medium,
        "high" => Pi.Ai.Types.ThinkingLevel.High,
        "xhigh" => Pi.Ai.Types.ThinkingLevel.XHigh,
        "max" => Pi.Ai.Types.ThinkingLevel.Max,
        _ => null,
    };

    /// <summary>Whether the text names a thinking level (the pi-ai set, without <c>off</c>).</summary>
    public static bool IsValidWithoutOff(string? text) =>
        text is "minimal" or "low" or "medium" or "high" or "xhigh" or "max";
}
