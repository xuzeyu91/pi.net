using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>ReadToolDetails</c>.</summary>
public sealed record ReadToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }
}

/// <summary>
/// Pluggable operations for the read tool. Override these to delegate file reading to remote
/// systems (for example SSH). Port of the TS <c>ReadOperations</c>.
/// </summary>
public interface IReadToolOperations
{
    /// <summary>Read file contents as bytes.</summary>
    Task<byte[]> ReadFileAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Check if file is readable (throw if not).</summary>
    Task AccessAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Detect image MIME type; null for non-images.</summary>
    Func<string, Task<string?>>? DetectImageMimeType { get; }
}

/// <summary>Port of the TS <c>ReadToolOptions</c>.</summary>
public sealed record ReadToolOptions
{
    /// <summary>Whether to auto-resize images. Default: true.</summary>
    public bool? AutoResizeImages { get; init; }

    /// <summary>Fallback resize profile when the execution context has no model metadata.</summary>
    public ModelImageResizeOptions? ResizeOptions { get; init; }

    /// <summary>Custom operations for file reading. Default: local filesystem.</summary>
    public IReadToolOperations? Operations { get; init; }
}

/// <summary>The read tool. Port of <c>core/tools/read.ts</c>.</summary>
public static class ReadTool
{
    /// <summary>The TS <c>readToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Read file contents";

    public static readonly IReadOnlyList<string> Guidelines = ["Use read to examine files instead of cat or sed."];

    private static readonly ToolSchema ReadSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Path to the file to read (relative or absolute)",
            },
            ["offset"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Line number to start reading from (1-indexed)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of lines to read",
            },
        },
        ["required"] = new[] { "path" },
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// The TS <c>readOutputSchema</c>: a union of the text for text files and an image block for
    /// images that codemode's <c>image()</c> accepts. Property descriptions are left out so the type
    /// stays on one line in tool descriptions.
    /// </summary>
    private static readonly ToolSchema ReadOutputSchema = new(new Dictionary<string, object?>
    {
        ["anyOf"] = new object?[]
        {
            new Dictionary<string, object?> { ["type"] = "string" },
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["type"] = new Dictionary<string, object?> { ["const"] = "image" },
                    ["data"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["mimeType"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["note"] = new Dictionary<string, object?> { ["type"] = "string" },
                },
                ["required"] = new[] { "type", "data", "mimeType", "note" },
                ["additionalProperties"] = false,
            },
        },
    });

    private sealed class LocalReadOperations : IReadToolOperations
    {
        public Task<byte[]> ReadFileAsync(string absolutePath, CancellationToken cancellationToken) =>
            File.ReadAllBytesAsync(absolutePath, cancellationToken);

        public Task AccessAsync(string absolutePath, CancellationToken cancellationToken)
        {
            // Node's access(F_OK) succeeds for directories too, and R_OK then fails on read; the
            // port mirrors that by probing existence first and opening the file for read.
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                throw new FileNotFoundException(
                    $"ENOENT: no such file or directory, access '{absolutePath}'", absolutePath);
            }

            if (File.Exists(absolutePath))
            {
                using var stream = File.Open(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            return Task.CompletedTask;
        }

        public Func<string, Task<string?>>? DetectImageMimeType =>
            filePath => Mime.DetectSupportedImageMimeTypeFromFileAsync(filePath);
    }

    /// <summary>The image block and its note, or the text for text files and images that could not be processed.</summary>
    private static object ToReadOutput(IReadOnlyList<ContentBlock> content)
    {
        var text = content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
        var image = content.OfType<ImageContent>().FirstOrDefault();
        return image is not null
            ? new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["data"] = image.Data,
                ["mimeType"] = image.MimeType,
                ["note"] = text,
            }
            : text;
    }

    private static string? GetNonVisionImageNote(ModelSpec? model)
    {
        if (model is null || model.Input.Contains("image"))
        {
            return null;
        }

        return "[Current model does not support images. The image will be omitted from this request.]";
    }

    /// <summary>The TS <c>createReadToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateReadToolDefinition(string cwd, ReadToolOptions? options = null)
    {
        var autoResizeImages = options?.AutoResizeImages ?? true;
        var fallbackResizeOptions = options?.ResizeOptions;
        var ops = options?.Operations ?? new LocalReadOperations();
        return new ToolDefinition
        {
            Name = "read",
            Label = "read",
            Description =
                $"Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines.ToList(),
            Parameters = ReadSchema,
            OutputSchema = ReadOutputSchema,
            ConstrainedSampling = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "json_schema",
                ["strict"] = "prefer",
            },
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var path = ToolArgs.GetString(args, "path") ?? "";
                var offset = ToolArgs.GetInt(args, "offset");
                var limit = ToolArgs.GetInt(args, "limit");

                var absolutePath = await ToolPathUtils
                    .ResolveReadPathAsync(path, ToolContexts.ResolveCwd(ctx, cwd))
                    .ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();

                // Check if file exists and is readable.
                await ops.AccessAsync(absolutePath, signal).ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();

                var mimeType = ops.DetectImageMimeType is { } detect
                    ? await detect(absolutePath).ConfigureAwait(false)
                    : null;
                List<ContentBlock> content;
                ReadToolDetails? details = null;
                var nonVisionImageNote = GetNonVisionImageNote(ctx?.Model);
                if (!string.IsNullOrEmpty(mimeType))
                {
                    // Read image as binary.
                    var buffer = await ops.ReadFileAsync(absolutePath, signal).ConfigureAwait(false);
                    var processed = await ImageProcess.ProcessImageAsync(buffer, mimeType, new ProcessImageOptions
                    {
                        AutoResizeImages = autoResizeImages,
                        ResizeOptions = (ctx?.Model?.InputLimits?.Images?.Resize ?? fallbackResizeOptions) is { } profile
                            ? ImageResizeOptions.FromModelProfile(profile)
                            : null,
                    }).ConfigureAwait(false);
                    if (processed is not ProcessImageResult.Ok ok)
                    {
                        var textNote = $"Read image file [{mimeType}]\n{(processed as ProcessImageResult.Fail)?.Message}";
                        if (nonVisionImageNote is not null)
                        {
                            textNote += $"\n{nonVisionImageNote}";
                        }

                        content = [new TextContent(textNote)];
                    }
                    else
                    {
                        var textNote = $"Read image file [{ok.MimeType}]";
                        if (ok.Hints.Count > 0)
                        {
                            textNote += $"\n{string.Join("\n", ok.Hints)}";
                        }

                        if (nonVisionImageNote is not null)
                        {
                            textNote += $"\n{nonVisionImageNote}";
                        }

                        content = [new TextContent(textNote), new ImageContent(ok.Data, ok.MimeType)];
                    }
                }
                else
                {
                    // Read text content.
                    var buffer = await ops.ReadFileAsync(absolutePath, signal).ConfigureAwait(false);
                    var textContent = System.Text.Encoding.UTF8.GetString(buffer);
                    var allLines = textContent.Split('\n');
                    var totalFileLines = allLines.Length;

                    // Apply offset if specified. Convert from 1-indexed input to 0-indexed array access.
                    var startLine = offset is > 0 ? Math.Max(0, offset.Value - 1) : 0;
                    var startLineDisplay = startLine + 1;

                    // Check if offset is out of bounds.
                    if (startLine >= allLines.Length)
                    {
                        throw new InvalidOperationException(
                            $"Offset {offset} is beyond end of file ({allLines.Length} lines total)");
                    }

                    string selectedContent;
                    int? userLimitedLines = null;

                    // If limit is specified by the user, honor it first. Otherwise truncateHead decides.
                    if (limit is not null)
                    {
                        var endLine = Math.Min(startLine + limit.Value, allLines.Length);
                        selectedContent = string.Join("\n", allLines[startLine..endLine]);
                        userLimitedLines = endLine - startLine;
                    }
                    else
                    {
                        selectedContent = string.Join("\n", allLines[startLine..]);
                    }

                    // Apply truncation, respecting both line and byte limits.
                    var truncation = Truncate.TruncateHead(selectedContent);
                    string outputText;
                    if (truncation.FirstLineExceedsLimit)
                    {
                        // First line alone exceeds the byte limit. Point the model at a bash fallback.
                        var firstLineSize = Truncate.FormatSize(System.Text.Encoding.UTF8.GetByteCount(allLines[startLine]));
                        outputText =
                            $"[Line {startLineDisplay} is {firstLineSize}, exceeds {Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit. Use bash: sed -n '{startLineDisplay}p' {path} | head -c {Truncate.DefaultMaxBytes}]";
                        details = new ReadToolDetails { Truncation = truncation };
                    }
                    else if (truncation.Truncated)
                    {
                        // Truncation occurred. Build an actionable continuation notice.
                        var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
                        var nextOffset = endLineDisplay + 1;
                        outputText = truncation.Content;
                        if (truncation.TruncatedBy == TruncatedBy.Lines)
                        {
                            outputText +=
                                $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines}. Use offset={nextOffset} to continue.]";
                        }
                        else
                        {
                            outputText +=
                                $"\n\n[Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Use offset={nextOffset} to continue.]";
                        }

                        details = new ReadToolDetails { Truncation = truncation };
                    }
                    else if (userLimitedLines is not null && startLine + userLimitedLines.Value < allLines.Length)
                    {
                        // User-specified limit stopped early, but the file still has more content.
                        var remaining = allLines.Length - (startLine + userLimitedLines.Value);
                        var nextOffset = startLine + userLimitedLines.Value + 1;
                        outputText =
                            $"{truncation.Content}\n\n[{remaining} more lines in file. Use offset={nextOffset} to continue.]";
                    }
                    else
                    {
                        // No truncation and no remaining user-limited content.
                        outputText = truncation.Content;
                    }

                    content = [new TextContent(outputText)];
                }

                signal.ThrowIfCancellationRequested();
                return new AgentToolResult(content, Details: details, StructuredContent: ToReadOutput(content));
            },
        };
    }

    /// <summary>The TS <c>createReadTool(cwd, options)</c>.</summary>
    public static AgentTool CreateReadTool(string cwd, ReadToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateReadToolDefinition(cwd, options));
}
