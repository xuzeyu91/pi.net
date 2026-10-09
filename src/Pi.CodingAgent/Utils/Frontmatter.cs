using System.Globalization;
using Pi.Tui;
using YamlDotNet.Serialization;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// The result of <see cref="Frontmatter.Parse"/>: the parsed fields plus the body that follows the
/// block. The TS original is generic (<c>ParsedFrontmatter&lt;T&gt;</c>); the fields are exposed as a
/// string-keyed map, which is the honest translation of <c>T extends Record&lt;string, unknown&gt;</c>.
/// </summary>
/// <param name="Frontmatter">The parsed mapping, or empty when there is no block or its root is not a mapping.</param>
/// <param name="Body">Everything after the closing delimiter, trimmed.</param>
public sealed record ParsedFrontmatter(IReadOnlyDictionary<string, object?> Frontmatter, string Body);

/// <summary>
/// Port of <c>utils/frontmatter.ts</c>: split a <c>---</c>-delimited YAML block off the front of a
/// markdown document.
/// </summary>
/// <remarks>
/// <para>
/// The delimiter handling is a faithful port and has a few non-obvious consequences worth naming:
/// the block is only recognised when the content <em>starts</em> with <c>---</c>; the closing delimiter
/// is the first <c>\n---</c> at or after offset 3; newlines are normalised and a BOM stripped first;
/// and an <em>empty</em> block (yielding the empty string) short-circuits before the parser is called,
/// so it returns empty fields rather than an empty-string parse.
/// </para>
/// <para>
/// The YAML itself goes through YamlDotNet. That is not a byte-for-byte stand-in for <c>yaml@2.9.0</c>:
/// YamlDotNet has no schema selection and resolves scalars closer to YAML 1.1, so a handful of inputs
/// differ (<c>yes</c>/<c>no</c>, timestamps, leading-zero numbers, merge keys). Those are recorded as
/// divergence C25 in <c>docs/coding-agent-porting-status.md</c> and pinned by
/// <c>frontmatter-corpus.json</c>; the duplicate-key check is enabled because <c>yaml@2.9.0</c> rejects
/// duplicate mapping keys and the port must too.
/// </para>
/// </remarks>
public static class Frontmatter
{
    private static readonly IReadOnlyDictionary<string, object?> NoFields =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    /// <summary>
    /// yaml@2.9.0 treats end-of-input as a line break: the extracted block never ends with
    /// <c>\n</c> (the slice stops before the closing delimiter's newline), and a block scalar
    /// (<c>|</c> / <c>&gt;</c>) at EOF still keeps its final newline under clip chomping.
    /// YamlDotNet only preserves that newline when the source itself ends with one, so the port
    /// terminates the document the way the original's parser sees it. A no-op for every other
    /// construct (plain scalars, flow collections and the malformed inputs parse identically).
    /// </summary>
    private static string TerminateDocument(string yaml) =>
        yaml.EndsWith('\n') ? yaml : yaml + "\n";

    /// <summary>The TS <c>parseFrontmatter(content)</c>. Throws on malformed YAML, as the original does.</summary>
    public static ParsedFrontmatter Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (yaml, body) = Extract(content);

        // `if (!yamlString)` in the original: an empty block never reaches the parser.
        if (string.IsNullOrEmpty(yaml))
        {
            return new ParsedFrontmatter(NoFields, body);
        }

        return new ParsedFrontmatter(ToFields(Deserializer.Deserialize<object?>(TerminateDocument(yaml))), body);
    }

    /// <summary>The TS <c>stripFrontmatter(content)</c>: just the body.</summary>
    public static string Strip(string content) => Parse(content).Body;

    /// <summary>
    /// The TS <c>typeof frontmatter[key] === "string" ? … : undefined</c> idiom the consumers use.
    /// </summary>
    public static string? GetString(IReadOnlyDictionary<string, object?> frontmatter, string key) =>
        frontmatter.TryGetValue(key, out var value) && value is string text ? text : null;

    /// <summary>The same idiom narrowed to booleans.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, object?> frontmatter, string key) =>
        frontmatter.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    /// <summary>
    /// <c>extractFrontmatter</c>. Returns a null <c>yamlString</c> when there is no block at all, which
    /// the caller distinguishes from an empty block only by the short-circuit on the empty string.
    /// </summary>
    private static (string? Yaml, string Body) Extract(string content)
    {
        var normalized = NormalizeNewlines(Text.StripBom(content));

        if (!normalized.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, normalized);
        }

        var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex == -1)
        {
            return (null, normalized);
        }

        // `slice(4, endIndex)` can be inverted — "---\n---\n…" closes at index 3 — and JS yields "".
        return (JsString.Slice(normalized, 4, endIndex), normalized[(endIndex + 4)..].Trim());
    }

    /// <summary><c>value.replace(/\r\n/g, "\n").replace(/\r/g, "\n")</c>.</summary>
    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Project the parsed document onto the string-keyed field map. A root that is not a mapping is
    /// deliberately flattened to no fields: the original casts it to <c>T</c> anyway, so every field
    /// read already yields <c>undefined</c>, and reproducing JS's <c>Object.keys("hello")</c> would
    /// model behaviour no consumer can observe.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToFields(object? parsed)
    {
        if (parsed is not IDictionary<object, object> mapping)
        {
            return NoFields;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in mapping)
        {
            if (entry.Key is null)
            {
                continue;
            }

            fields[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
        }

        return fields;
    }
}
