// ============================================================================
// MCP content → model-facing content — port of packages/mcp/src/protocol/content.ts
// ============================================================================
//
// The MCP protocol layer of the .NET port (Pi.Mcp) carried the result types but not the content
// conversion the coding-agent MCP extension needs (`toLlmContent` in TS). This file lands it:
// text and images pass through, embedded text resources become text, embedded image resources
// become images, and other blocks (audio, resource links, binary resources) become a short text
// placeholder. A result without content blocks but with `structuredContent` becomes its JSON,
// since servers should, but do not always, mirror structured results as text.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Mcp.Protocol;

/// <summary>Model-facing content of an MCP result. TS <c>LlmContent</c>.</summary>
public abstract record LlmContent;

/// <summary>Text for the model.</summary>
public sealed record LlmTextContent(string Text) : LlmContent;

/// <summary>Base64 image for the model.</summary>
public sealed record LlmImageContent(string Data, string MimeType) : LlmContent;

/// <summary>Content conversion of the MCP protocol. Port of TS <c>toLlmContent</c> / <c>blockToLlmContent</c>.</summary>
public static class McpContent
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Convert a tool result to text and image content for a model. A result without content
    /// blocks but with <paramref name="structuredContent"/> becomes its JSON.
    /// </summary>
    public static IReadOnlyList<LlmContent> ToLlmContent(
        IReadOnlyList<McpContentBlock> blocks,
        JsonObject? structuredContent)
    {
        var content = blocks.Select(BlockToLlmContent).ToList();
        if (content.Count == 0 && structuredContent is not null)
        {
            content.Add(new LlmTextContent(JsonSerializer.Serialize(structuredContent, Indented)));
        }

        return content;
    }

    /// <summary>Model-facing content of one block.</summary>
    public static LlmContent BlockToLlmContent(McpContentBlock block)
    {
        var raw = block.Raw;
        var type = raw.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeText)
                ? typeText
                : null;

        switch (type)
        {
            case "text":
                return new LlmTextContent(Text(raw));

            case "image":
                return new LlmImageContent(Text(raw, "data"), String(raw, "mimeType"));

            case "audio":
                return new LlmTextContent($"[audio {Optional(raw, "mimeType")} omitted]");

            case "resource_link":
                return new LlmTextContent($"{Text(raw, "name")}: {Text(raw, "uri")}");

            case "resource":
                return ResourceToLlmContent(raw);

            default:
                return new LlmTextContent($"[unsupported MCP content {type}]");
        }
    }

    private static LlmContent ResourceToLlmContent(JsonObject raw)
    {
        if (raw["resource"] is not JsonObject resource)
        {
            return new LlmTextContent("[unsupported MCP content resource]");
        }

        if (resource.TryGetPropertyValue("text", out var textNode) && textNode is JsonValue textValue
            && textValue.TryGetValue<string>(out var text))
        {
            return new LlmTextContent(text);
        }

        var mimeType = Optional(resource, "mimeType");
        if (mimeType?.StartsWith("image/", StringComparison.Ordinal) == true)
        {
            return new LlmImageContent(Text(resource, "blob"), mimeType);
        }

        return new LlmTextContent(
            $"[binary resource {Text(resource, "uri")} ({mimeType ?? "unknown type"}) omitted]");
    }

    private static string Text(JsonObject raw, string key) =>
        raw.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static string Text(JsonObject raw) => Text(raw, "text");

    private static string String(JsonObject raw, string key) => Text(raw, key);

    private static string? Optional(JsonObject raw, string key) =>
        raw.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;
}
