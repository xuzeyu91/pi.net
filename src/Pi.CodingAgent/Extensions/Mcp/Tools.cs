// ============================================================================
// MCP tools → pi tool definitions — port of extensions/mcp/tools.ts (4d-4b)
// ============================================================================
//
// Results map onto pi's model-facing content (text and images). Text over 20KB keeps its start and
// end with the middle cut out, like Codex does, and the full text is saved to a temp file the model
// can read. Binary resources other than images are saved to temp files too, and resource links name
// the `read_mcp_resource` tool. Codemode scripts receive the whole `CallToolResult` without `_meta`
// (`content` blocks as sent by the server, `structuredContent`, `isError`), never truncated: it is
// the tool's `structuredContent`, and every MCP tool declares a `CallToolResult` output schema. MCP
// errors (`isError`) are error results for the model, but scripts still resolve to the result.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
using Pi.Mcp.Protocol;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using ToolSchema = Pi.Ai.Types.ToolSchema;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>Limits of MCP tool names and model-facing output.</summary>
public static class McpToolLimits
{
    /// <summary>Provider tool names are limited to 64 characters of <c>[A-Za-z0-9_-]</c>.</summary>
    public const int MaxToolNameLength = 64;

    /// <summary>Model-facing text of an MCP result beyond this is cut in the middle.</summary>
    public const int McpOutputMaxBytes = 20 * 1024;

    /// <summary>Visual (wrapped) result lines shown before the output is expanded.</summary>
    public const int OutputPreviewLines = 5;
}

/// <summary>Tool details of an MCP tool result. TS <c>McpToolDetails</c>.</summary>
public sealed record McpToolDetails
{
    public required string Server { get; init; }

    public required string Tool { get; init; }

    /// <summary>Temp file with the full text output, when the model-facing text was truncated.</summary>
    public string? FullOutputPath { get; init; }
}

/// <summary>
/// Saves the full text of a truncated result, or a binary resource, and returns the file path.
/// <paramref name="extension"/> includes the dot, for example <c>.txt</c>.
/// </summary>
public delegate Task<string> McpOutputSaver(ReadOnlyMemory<byte> data, string extension);

/// <summary>Calls one MCP tool. TS <c>McpToolCaller</c>.</summary>
public interface IMcpToolCaller
{
    Task<CallToolResult> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> args,
        McpCallOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Options of one <see cref="IMcpToolCaller.CallToolAsync"/> call. TS <c>McpRequestOptions</c>.</summary>
public sealed record McpCallOptions
{
    /// <summary>Request timeout in milliseconds.</summary>
    public long? TimeoutMs { get; init; }

    /// <summary>Progress notifications of the call.</summary>
    public Func<McpProgress, Task>? OnProgress { get; init; }
}

/// <summary>A progress notification of an MCP tool call. TS <c>Progress</c>.</summary>
public sealed record McpProgress(double Progress, double? Total = null, string? Message = null);

/// <summary>Options of <see cref="McpTools.ConvertMcpResult"/>. TS <c>ConvertMcpResultOptions</c>.</summary>
public sealed record ConvertMcpResultOptions
{
    /// <summary>Saves truncated text and binary resources. Default: a temp file.</summary>
    public Func<ReadOnlyMemory<byte>, string, Task<string>>? SaveOutput { get; init; }

    /// <summary>Whether the server's resources can be read with <c>read_mcp_resource</c>.</summary>
    public bool ReadableResources { get; init; }
}

/// <summary>Adapts MCP tools to pi tool definitions. Port of <c>extensions/mcp/tools.ts</c>.</summary>
public static partial class McpTools
{
    /// <summary>
    /// Tool exposure of an MCP exposure. <c>codemode</c> and <c>deferred</c> both leave tools out of
    /// the codemode description; they differ only in which tool the MCP extension activates to
    /// reach them.
    /// </summary>
    public static string ToToolExposure(string exposure) =>
        exposure == McpServers.Exposures.Codemode ? McpServers.Exposures.Deferred : exposure;

    /// <summary>
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, sanitized and shortened with a hash suffix when too
    /// long. Like Codex, everything but <c>[A-Za-z0-9_]</c> becomes <c>_</c>, so the name is also the
    /// identifier codemode scripts call it by. <paramref name="isTaken"/> reports names used by a
    /// different MCP tool: sanitizing can map two tools to one name (<c>a-b</c> and <c>a_b</c>),
    /// which then get the hash suffix.
    /// </summary>
    public static string CreateMcpToolName(
        string server,
        string tool,
        Func<string, bool>? isTaken = null)
    {
        var name = SanitizeRegex().Replace($"mcp__{server}__{tool}", "_");
        if (name.Length <= McpToolLimits.MaxToolNameLength && isTaken?.Invoke(name) != true)
        {
            return name;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{server}\0{tool}")))[..8];
        var keep = Math.Min(name.Length, McpToolLimits.MaxToolNameLength - hash.Length - 1);
        return $"{name[..keep]}_{hash}";
    }

    /// <summary>
    /// Output schema of every MCP tool: the <c>CallToolResult</c> scripts receive, with the tool's
    /// own output schema as <c>structuredContent</c>. Codemode detects this shape to render
    /// <c>CallToolResult&lt;T&gt;</c> declarations.
    /// </summary>
    public static JsonObject CreateMcpResultSchema(JsonObject? structuredContentSchema)
    {
        var properties = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
            },
        };

        if (structuredContentSchema is not null)
        {
            properties["structuredContent"] = structuredContentSchema.DeepClone();
        }

        properties["isError"] = new JsonObject { ["type"] = "boolean" };
        properties["_meta"] = new JsonObject { ["type"] = "object" };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("content"),
        };
    }

    /// <summary>
    /// Keep model-facing text within <see cref="McpOutputMaxBytes"/>. Longer text becomes one text
    /// block in Codex's truncation format, followed by the path of the file with the full text;
    /// images follow it.
    /// </summary>
    public static async Task<(IReadOnlyList<ContentBlock> Content, string? FullOutputPath)> LimitMcpContentAsync(
        IReadOnlyList<ContentBlock> content,
        Func<ReadOnlyMemory<byte>, string, Task<string>>? saveOutput = null)
    {
        var combined = TextOf(content);
        var truncation = Truncate.TruncateMiddle(combined, McpToolLimits.McpOutputMaxBytes);
        if (!truncation.Truncated)
        {
            return (content, null);
        }

        string? fullOutputPath = null;
        string where;
        try
        {
            fullOutputPath = await (saveOutput ?? SaveToTempFileAsync)(
                Encoding.UTF8.GetBytes(combined), ".txt");
            where = $"[Full output: {fullOutputPath} (read it with offset/limit)]";
        }
        catch (Exception error)
        {
            where = $"[Could not save the full output: {error.Message}]";
        }

        var tokens = (long)Math.Ceiling(truncation.TotalBytes / 4.0);
        var text =
            $"Warning: truncated output (original token count: {tokens})\n" +
            $"Total output lines: {truncation.TotalLines}\n\n{truncation.Content}\n\n{where}";

        List<ContentBlock> limited = [new TextContent(text)];
        limited.AddRange(content.Where(block => block is ImageContent));
        return (limited, fullOutputPath);
    }

    /// <summary>Model-facing content of <paramref name="server"/>'s content blocks, before the output limit.</summary>
    public static async Task<IReadOnlyList<ContentBlock>> ToModelContentAsync(
        string server,
        IReadOnlyList<McpContentBlock> blocks,
        ConvertMcpResultOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ContentBlock>();
        foreach (var block in blocks)
        {
            result.AddRange(await BlockToContentAsync(server, block, options, cancellationToken));
        }

        return result;
    }

    /// <summary>
    /// Convert an MCP result. <c>isError</c> results become error results that keep the structured
    /// result.
    /// </summary>
    public static async Task<AgentToolResult> ConvertMcpResultAsync(
        string server,
        string tool,
        CallToolResult result,
        ConvertMcpResultOptions options,
        CancellationToken cancellationToken = default)
    {
        // Without content blocks, ToLlmContent falls back to the structured content as JSON.
        List<ContentBlock> converted;
        if (result.Content.Count > 0)
        {
            converted = (await ToModelContentAsync(server, result.Content, options, cancellationToken)).ToList();
        }
        else
        {
            converted = McpContent.ToLlmContent(result.Content, result.StructuredContent)
                .Select(ToContentBlock)
                .ToList();
        }

        if (result.IsError && TextOf(converted) == "")
        {
            converted.Add(new TextContent($"MCP tool {server}/{tool} returned an error"));
        }

        var (content, fullOutputPath) = await LimitMcpContentAsync(
            converted,
            options.SaveOutput is null ? null : (data, extension) => options.SaveOutput(data, extension));

        // Codemode scripts receive the whole result without `_meta`.
        var scriptResult = new JsonObject
        {
            ["content"] = ToRawContent(result.Content),
        };
        if (result.StructuredContent is not null)
        {
            scriptResult["structuredContent"] = result.StructuredContent.DeepClone();
        }

        if (result.IsError)
        {
            scriptResult["isError"] = true;
        }

        return new AgentToolResult(
            content,
            Details: new McpToolDetails { Server = server, Tool = tool, FullOutputPath = fullOutputPath },
            StructuredContent: scriptResult,
            IsError: result.IsError);
    }

    /// <summary>
    /// Create the pi tool definition of one MCP tool.
    /// </summary>
    public static ToolDefinition CreateMcpToolDefinition(CreateMcpToolOptions options)
    {
        var server = options.Server;
        var tool = options.Tool;
        var annotations = ToToolAnnotations(tool);
        var label = $"{server}/{tool.Name}";

        return new ToolDefinition
        {
            Name = options.Name,
            Label = label,
            Description = string.IsNullOrWhiteSpace(tool.Description)
                ? tool.Annotations?["title"]?.GetValue<string>() ?? tool.Title ?? $"MCP tool {tool.Name} from server {server}"
                : tool.Description.Trim(),
            Parameters = ToParameters(tool.InputSchema),
            OutputSchema = tool.OutputSchema is null ? null : ToParameters(tool.OutputSchema),
            Exposure = ToToolExposure(options.Exposure),
            Namespace = options.Namespace,
            Annotations = annotations,
            Execute = async (_, parameters, signal, onUpdate, _) =>
            {
                var client = await options.GetClient();
                var result = await client.CallToolAsync(
                    tool.Name,
                    parameters,
                    new McpCallOptions
                    {
                        TimeoutMs = options.TimeoutMs,
                        OnProgress = progress =>
                        {
                            var total = progress.Total is null ? "" : $"/{progress.Total}";
                            var text = progress.Message ?? $"Progress {progress.Progress}{total}";
                            return onUpdate is null
                                ? Task.CompletedTask
                                : onUpdate(new AgentToolResult(
                                    [new TextContent(text)],
                                    Details: new McpToolDetails { Server = server, Tool = tool.Name }));
                        },
                    },
                    signal);

                return await ConvertMcpResultAsync(
                    server,
                    tool.Name,
                    result,
                    new ConvertMcpResultOptions { ReadableResources = options.ReadableResources?.Invoke() == true },
                    signal);
            },
        };
    }

    /// <summary>
    /// Renderers of calls to an MCP tool, labeled <c>server/tool</c>, also used before the tool is
    /// registered.
    /// </summary>
    /// <remarks>
    /// Difference C109: the TS renderers build TUI components (<c>Text</c>, <c>Container</c>,
    /// <c>Spacer</c>, <c>VisualLinePreview</c>) and read the theme. They land with 4f, which ports
    /// the Theme and the interactive components; until then the MCP tools carry no renderers, the
    /// same placeholder state the 4d-1 contract layer recorded as C85.
    /// </remarks>
    public static ToolRenderers CreateMcpToolRenderers(string label) => new();

    /// <summary>Saves the full text of a truncated result, or a binary resource.</summary>
    public static Task<string> SaveToTempFileAsync(ReadOnlyMemory<byte> data, string extension) =>
        OutputFiles.WriteOutputFileAsync("pi-mcp", extension, data);

    // ------------------------------------------------------------------ internals

    /// <summary>Options of <see cref="CreateMcpToolDefinition"/>.</summary>
    public sealed record CreateMcpToolOptions
    {
        public required string Server { get; init; }

        public required McpTool Tool { get; init; }

        public required string Name { get; init; }

        public required string Exposure { get; init; }

        public required ToolNamespace Namespace { get; init; }

        public required long TimeoutMs { get; init; }

        public required Func<Task<IMcpToolCaller>> GetClient { get; init; }

        /// <summary>Whether <c>read_mcp_resource</c> can read the server's resources.</summary>
        public Func<bool>? ReadableResources { get; init; }
    }

    /// <summary>Tool input schemas must be objects. MCP servers may omit <c>type</c>, and some providers reject object schemas without <c>properties</c>.</summary>
    private static ToolSchema ToParameters(JsonObject schema)
    {
        var normalized = schema.DeepClone();
        if (normalized["type"] is null)
        {
            normalized["type"] = "object";
        }

        if (normalized["properties"] is null)
        {
            normalized["properties"] = new JsonObject();
        }

        var dictionary = new Dictionary<string, object?>();
        foreach (var (key, value) in normalized.AsObject())
        {
            dictionary[key] = value;
        }

        return new ToolSchema(dictionary);
    }

    private static readonly string[] AnnotationHints =
        ["readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint"];

    /// <summary>The boolean hints of an MCP tool's annotations, or null when it has none.</summary>
    private static ToolAnnotations? ToToolAnnotations(McpTool tool)
    {
        ToolAnnotations? annotations = null;
        foreach (var hint in AnnotationHints)
        {
            if (tool.Annotations?.TryGetPropertyValue(hint, out var node) != true)
            {
                continue;
            }

            if (node is not JsonValue value || !value.TryGetValue<bool>(out var flag))
            {
                continue;
            }

            annotations ??= new ToolAnnotations();
            switch (hint)
            {
                case "readOnlyHint":
                    annotations = annotations with { ReadOnlyHint = flag };
                    break;
                case "destructiveHint":
                    annotations = annotations with { DestructiveHint = flag };
                    break;
                case "idempotentHint":
                    annotations = annotations with { IdempotentHint = flag };
                    break;
                default:
                    annotations = annotations with { OpenWorldHint = flag };
                    break;
            }
        }

        return annotations;
    }

    private static string TextOf(IReadOnlyList<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContent>().Select(block => block.Text));

    private static ContentBlock ToContentBlock(LlmContent content) => content switch
    {
        LlmTextContent text => new TextContent(text.Text),
        LlmImageContent image => new ImageContent(image.Data, image.MimeType),
        _ => new TextContent(string.Empty),
    };

    /// <summary>Model-facing content of one block of <paramref name="server"/>'s result.</summary>
    private static async Task<IReadOnlyList<ContentBlock>> BlockToContentAsync(
        string server,
        McpContentBlock block,
        ConvertMcpResultOptions options,
        CancellationToken cancellationToken)
    {
        var raw = block.Raw;
        var type = raw.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeText)
                ? typeText
                : null;

        if (type == "resource_link")
        {
            var details = new List<string>();
            var mimeType = Optional(raw, "mimeType");
            if (!string.IsNullOrEmpty(mimeType))
            {
                details.Add(mimeType);
            }

            if (raw["size"] is JsonValue sizeValue)
            {
                // `size` arrives as a JSON number; System.Text.Json keeps it as a typed JsonValue,
                // so read it through its JSON text rather than a generic conversion.
                if (double.TryParse(sizeValue.ToJsonString(), out var size) && size > 0)
                {
                    details.Add(Truncate.FormatSize((int)Math.Round(size)));
                }
            }

            var read = options.ReadableResources
                ? $". Read it with {McpServers.ToolNames.ReadMcpResource} (server \"{server}\")"
                : "";
            var description = Optional(raw, "description") is { Length: > 0 } described
                ? $": {described}"
                : "";
            var title = Optional(raw, "title") ?? Optional(raw, "name");
            var suffix = details.Count > 0 ? $" ({string.Join(", ", details)})" : "";
            return new ContentBlock[]
            {
                new TextContent($"[Resource {Text(raw, "uri")} \"{title}\"{suffix}{description}{read}]"),
            };
        }

        if (type == "resource"
            && raw["resource"] is JsonObject resource
            && resource["blob"] is JsonValue blobValue
            && blobValue.TryGetValue<string>(out var blob)
            && Optional(resource, "mimeType")?.StartsWith("image/", StringComparison.Ordinal) != true)
        {
            var uri = Text(resource, "uri");
            var mimeType = Optional(resource, "mimeType");
            var data = Convert.FromBase64String(blob);
            if (IsTextMimeType(mimeType))
            {
                return [new TextContent(Encoding.UTF8.GetString(data))];
            }

            var kind = $"{mimeType ?? "unknown type"}, {Truncate.FormatSize(data.Length)}";
            try
            {
                var save = options.SaveOutput ?? SaveToTempFileAsync;
                var path = await save(data, ExtensionOf(uri));
                return [new TextContent($"[Binary resource {uri} ({kind}) saved to {path}]")];
            }
            catch (Exception error)
            {
                return [new TextContent($"[Binary resource {uri} ({kind}) could not be saved: {error.Message}]")];
            }
        }

        return McpContent.BlockToLlmContent(block) switch
        {
            LlmTextContent text => new ContentBlock[] { new TextContent(text.Text) },
            LlmImageContent image => new ContentBlock[] { new ImageContent(image.Data, image.MimeType) },
            _ => [new TextContent(string.Empty)],
        };
    }

    /// <summary>File extension for a saved binary resource: the one its URI ends in, else <c>.bin</c>.</summary>
    private static string ExtensionOf(string uri)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : uri;
        var match = ExtensionRegex().Match(path);
        return match.Success ? match.Value : ".bin";
    }

    /// <summary>Blobs of these types are shown as text.</summary>
    private static bool IsTextMimeType(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType))
        {
            return false;
        }

        var type = mimeType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return type.StartsWith("text/", StringComparison.Ordinal)
            || type == "application/json"
            || type.EndsWith("+json", StringComparison.Ordinal)
            || type.EndsWith("+xml", StringComparison.Ordinal);
    }

    private static JsonArray ToRawContent(IReadOnlyList<McpContentBlock> content)
    {
        var array = new JsonArray();
        foreach (var block in content)
        {
            array.Add(block.Raw.DeepClone());
        }

        return array;
    }

    private static string Text(JsonObject raw, string key) =>
        raw.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static string? Optional(JsonObject raw, string key) =>
        raw.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex SanitizeRegex();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex ExtensionRegex();
}
