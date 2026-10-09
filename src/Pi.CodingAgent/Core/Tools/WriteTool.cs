using System.Text;
using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// Pluggable operations for the write tool. Override these to delegate file writing to remote
/// systems (for example SSH). Port of the TS <c>WriteOperations</c>.
/// </summary>
public interface IWriteToolOperations
{
    /// <summary>Write content to a file.</summary>
    Task WriteFileAsync(string absolutePath, string content, CancellationToken cancellationToken);

    /// <summary>Create directory recursively.</summary>
    Task MkdirAsync(string dir, CancellationToken cancellationToken);
}

/// <summary>Port of the TS <c>WriteToolOptions</c>.</summary>
public sealed record WriteToolOptions
{
    /// <summary>Custom operations for file writing. Default: local filesystem.</summary>
    public IWriteToolOperations? Operations { get; init; }
}

/// <summary>The write tool. Port of <c>core/tools/write.ts</c>.</summary>
public static class WriteTool
{
    /// <summary>The TS <c>writeToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Create or overwrite files";

    public static readonly IReadOnlyList<string> Guidelines = ["Use write only for new files or complete rewrites."];

    private static readonly ToolSchema WriteSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Path to the file to write (relative or absolute)",
            },
            ["content"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Content to write to the file",
            },
        },
        ["required"] = new[] { "path", "content" },
        ["additionalProperties"] = false,
    });

    private sealed class LocalWriteOperations : IWriteToolOperations
    {
        public Task WriteFileAsync(string absolutePath, string content, CancellationToken cancellationToken) =>
            File.WriteAllTextAsync(absolutePath, content, new UTF8Encoding(false), cancellationToken);

        public Task MkdirAsync(string dir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(dir);
            return Task.CompletedTask;
        }
    }

    /// <summary>The TS <c>createWriteToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateWriteToolDefinition(string cwd, WriteToolOptions? options = null)
    {
        var ops = options?.Operations ?? new LocalWriteOperations();
        return new ToolDefinition
        {
            Name = "write",
            Label = "write",
            Description =
                "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.",
            PromptSnippet = Snippet,
            PromptGuidelines = Guidelines.ToList(),
            Parameters = WriteSchema,
            ConstrainedSampling = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "json_schema",
                ["strict"] = "prefer",
            },
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var path = ToolArgs.GetString(args, "path") ?? "";
                var content = ToolArgs.GetString(args, "content") ?? "";

                var absolutePath = ToolPathUtils.ResolveToCwd(path, ToolContexts.ResolveCwd(ctx, cwd));
                var dir = Path.GetDirectoryName(absolutePath);
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

                        // Create parent directories if needed. Node's dirname answers "." for a
                        // bare filename where Path.GetDirectoryName answers "".
                        await ops.MkdirAsync(dir is { Length: > 0 } ? dir : ".", signal).ConfigureAwait(false);
                        ThrowIfAborted();

                        // Write the file contents.
                        await ops.WriteFileAsync(absolutePath, content, signal).ConfigureAwait(false);
                        ThrowIfAborted();

                        return new AgentToolResult(
                            [new TextContent($"Successfully wrote to {path}")],
                            Details: null);
                    }).ConfigureAwait(false);
            },
        };
    }

    /// <summary>The TS <c>createWriteTool(cwd, options)</c>.</summary>
    public static AgentTool CreateWriteTool(string cwd, WriteToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateWriteToolDefinition(cwd, options));
}
