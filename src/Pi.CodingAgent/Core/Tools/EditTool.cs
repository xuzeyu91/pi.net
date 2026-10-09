using System.Text;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>EditToolDetails</c>.</summary>
public sealed record EditToolDetails
{
    /// <summary>Display-oriented diff of the changes made.</summary>
    public required string Diff { get; init; }

    /// <summary>Standard unified patch of the changes made.</summary>
    public required string Patch { get; init; }

    /// <summary>Line number of the first change in the new file (for editor navigation).</summary>
    public int? FirstChangedLine { get; init; }
}

/// <summary>
/// Pluggable operations for the edit tool. Override these to delegate file editing to remote
/// systems (for example SSH). Port of the TS <c>EditOperations</c>.
/// </summary>
public interface IEditToolOperations
{
    /// <summary>Read file contents as bytes.</summary>
    Task<byte[]> ReadFileAsync(string absolutePath, CancellationToken cancellationToken);

    /// <summary>Write content to a file.</summary>
    Task WriteFileAsync(string absolutePath, string content, CancellationToken cancellationToken);

    /// <summary>Check if file is readable and writable (throw if not).</summary>
    Task AccessAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>Port of the TS <c>EditToolOptions</c>.</summary>
public sealed record EditToolOptions
{
    /// <summary>Custom operations for file editing. Default: local filesystem.</summary>
    public IEditToolOperations? Operations { get; init; }
}

/// <summary>The edit tool. Port of <c>core/tools/edit.ts</c>.</summary>
public static class EditTool
{
    /// <summary>The TS <c>editToolSystemPromptContribution</c>.</summary>
    public const string Snippet =
        "Make precise file edits with exact text replacement, including multiple disjoint edits in one call";

    public static readonly IReadOnlyList<string> Guidelines =
    [
        "Use edit for precise changes (edits[].oldText must match exactly)",
        "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
        "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
        "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.",
    ];

    private static readonly ToolSchema EditSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Path to the file to edit (relative or absolute)",
            },
            ["edits"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] =
                    "One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["oldText"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call.",
                        },
                        ["newText"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "Replacement text for this targeted edit.",
                        },
                    },
                    ["required"] = new[] { "oldText", "newText" },
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new[] { "path", "edits" },
        ["additionalProperties"] = false,
    });

    private sealed class LocalEditOperations : IEditToolOperations
    {
        public Task<byte[]> ReadFileAsync(string absolutePath, CancellationToken cancellationToken) =>
            File.ReadAllBytesAsync(absolutePath, cancellationToken);

        public Task WriteFileAsync(string absolutePath, string content, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(absolutePath, content, new UTF8Encoding(false), cancellationToken);

        public Task AccessAsync(string absolutePath, CancellationToken cancellationToken)
        {
            // Node's access(R_OK | W_OK) succeeds for directories too; the port probes existence
            // first and opens the file for read+write to observe the same permission failures.
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                throw new FileNotFoundException(
                    $"ENOENT: no such file or directory, access '{absolutePath}'", absolutePath);
            }

            if (File.Exists(absolutePath))
            {
                using var stream = File.Open(absolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return Task.CompletedTask;
        }
    }

    private static bool IsSingleEditInput(JsonNode? value) =>
        value is JsonObject edit
        && edit["oldText"] is JsonValue oldText && oldText.TryGetValue<string>(out _)
        && edit["newText"] is JsonValue newText && newText.TryGetValue<string>(out _);

    /// <summary>
    /// The TS <c>prepareEditArguments</c>: some models (Opus 4.6, GLM-5.1) send edits as a JSON
    /// string instead of an array, others send a single edit object instead of a one-element edits
    /// array, and legacy callers send top-level oldText/newText.
    /// </summary>
    public static object? PrepareEditArguments(object? input)
    {
        if (input is not JsonObject args)
        {
            return input;
        }

        if (args["edits"] is JsonValue { } editsValue && editsValue.TryGetValue<string>(out var editsJson))
        {
            try
            {
                var parsed = JsonNode.Parse(editsJson);
                if (parsed is JsonArray)
                {
                    args["edits"] = parsed;
                }
                else if (IsSingleEditInput(parsed))
                {
                    args["edits"] = new JsonArray(parsed?.DeepClone());
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON leaves the field untouched; schema validation reports it.
            }
        }
        else if (IsSingleEditInput(args["edits"]))
        {
            args["edits"] = new JsonArray(args["edits"]?.DeepClone());
        }

        if (args["oldText"] is JsonValue { } oldValue && oldValue.TryGetValue<string>(out var oldText)
            && args["newText"] is JsonValue { } newValue && newValue.TryGetValue<string>(out var newText))
        {
            var edits = args["edits"] as JsonArray ?? new JsonArray();
            edits.Add(new JsonObject { ["oldText"] = oldText, ["newText"] = newText });
            args["edits"] = edits;
            args.Remove("oldText");
            args.Remove("newText");
        }

        return args;
    }

    private static (string Path, List<Edit> Edits) ValidateEditInput(IReadOnlyDictionary<string, object?> input)
    {
        // The runtime hands tools the prepared arguments as a JSON-shaped dictionary (see
        // ToolDefinitionWrapper.CoerceArgs), so the edits array arrives as a JsonNode.
        if (ToolArgs.GetNode(input, "edits") is not JsonArray { Count: > 0 } editsArray)
        {
            throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        }

        var edits = new List<Edit>();
        foreach (var item in editsArray)
        {
            if (item is not JsonObject editObject)
            {
                continue;
            }

            var oldText = editObject["oldText"] is JsonValue ov && ov.TryGetValue<string>(out var ot) ? ot : "";
            var newText = editObject["newText"] is JsonValue nv && nv.TryGetValue<string>(out var nt) ? nt : "";
            edits.Add(new Edit(oldText, newText));
        }

        return (ToolArgs.GetString(input, "path") ?? "", edits);
    }

    /// <summary>The TS <c>createEditToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateEditToolDefinition(string cwd, EditToolOptions? options = null)
    {
        var ops = options?.Operations ?? new LocalEditOperations();
        return new ToolDefinition
        {
            Name = "edit",
            Label = "edit",
            Description =
                "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines.ToList(),
            Parameters = EditSchema,
            ConstrainedSampling = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "json_schema",
                ["strict"] = "prefer",
            },
            RenderShell = RenderShellMode.Self,
            PrepareArguments = PrepareEditArguments,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var (path, edits) = ValidateEditInput(args);
                var absolutePath = ToolPathUtils.ResolveToCwd(path, ToolContexts.ResolveCwd(ctx, cwd));

                return await FileMutationQueue.WithFileMutationQueueAsync(
                    absolutePath,
                    async () =>
                    {
                        // Do not reject from an abort event listener here: that would release the
                        // mutation queue while an in-flight filesystem operation may still finish.
                        // Checking cancellation after each await observes the same aborts while
                        // keeping the queue locked until the current operation has settled.
                        void ThrowIfAborted() => signal.ThrowIfCancellationRequested();

                        ThrowIfAborted();

                        // Check if file exists.
                        try
                        {
                            await ops.AccessAsync(absolutePath, signal).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            ThrowIfAborted();
                            var errorMessage = error is FileNotFoundException or UnauthorizedAccessException
                                ? $"Error code: {GetNodeErrorCode(error)}"
                                : error.Message;
                            throw new InvalidOperationException($"Could not edit file: {path}. {errorMessage}.");
                        }

                        ThrowIfAborted();

                        // Read the file.
                        var buffer = await ops.ReadFileAsync(absolutePath, signal).ConfigureAwait(false);
                        var rawContent = Encoding.UTF8.GetString(buffer);
                        ThrowIfAborted();

                        // Strip BOM before matching. The model will not include an invisible BOM in oldText.
                        var (bom, content) = Text.SplitBom(rawContent);
                        var originalEnding = EditDiff.DetectLineEnding(content);
                        var normalizedContent = EditDiff.NormalizeToLf(content);
                        var (baseContent, newContent) =
                            EditDiff.ApplyEditsToNormalizedContent(normalizedContent, edits, path);
                        ThrowIfAborted();

                        var finalContent = bom + EditDiff.RestoreLineEndings(newContent, originalEnding);
                        await ops.WriteFileAsync(absolutePath, finalContent, signal).ConfigureAwait(false);
                        ThrowIfAborted();

                        var diffResult = EditDiff.GenerateDiffString(baseContent, newContent);
                        var patch = EditDiff.GenerateUnifiedPatch(path, baseContent, newContent);
                        return new AgentToolResult(
                            [new TextContent($"Successfully replaced {edits.Count} block(s) in {path}.")],
                            new EditToolDetails
                            {
                                Diff = diffResult.Diff,
                                Patch = patch,
                                FirstChangedLine = diffResult.FirstChangedLine,
                            });
                    }).ConfigureAwait(false);
            },
        };
    }

    /// <summary>The TS <c>createEditTool(cwd, options)</c>.</summary>
    public static AgentTool CreateEditTool(string cwd, EditToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateEditToolDefinition(cwd, options));

    private static string GetNodeErrorCode(Exception error) => error switch
    {
        FileNotFoundException => "ENOENT",
        DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EACCES",
        PathTooLongException => "ENAMETOOLONG",
        _ => "UNKNOWN",
    };
}
