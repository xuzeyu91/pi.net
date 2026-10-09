using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>LsToolDetails</c>.</summary>
public sealed record LsToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public int? EntryLimitReached { get; init; }
}

/// <summary>
/// Pluggable operations for the ls tool. Override these to delegate directory listing to remote
/// systems (for example SSH). Port of the TS <c>LsOperations</c>.
/// </summary>
public interface ILsToolOperations
{
    /// <summary>Check if path exists.</summary>
    Task<bool> ExistsAsync(string absolutePath);

    /// <summary>Get file or directory stats. Throws if not found.</summary>
    Task<bool> IsDirectoryAsync(string absolutePath);

    /// <summary>Read directory entries.</summary>
    Task<IReadOnlyList<string>> ReadDirAsync(string absolutePath);
}

/// <summary>Port of the TS <c>LsToolOptions</c>.</summary>
public sealed record LsToolOptions
{
    /// <summary>Custom operations for directory listing. Default: local filesystem.</summary>
    public ILsToolOperations? Operations { get; init; }
}

/// <summary>The ls tool. Port of <c>core/tools/ls.ts</c>.</summary>
public static class LsTool
{
    /// <summary>The TS <c>lsToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "List directory contents";

    private const int DefaultLimit = 500;

    private static readonly ToolSchema LsSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Directory to list (default: current directory)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of entries to return (default: 500)",
            },
        },
        ["additionalProperties"] = false,
    });

    private sealed class LocalLsOperations : ILsToolOperations
    {
        public Task<bool> ExistsAsync(string absolutePath) =>
            Task.FromResult(File.Exists(absolutePath) || Directory.Exists(absolutePath));

        public Task<bool> IsDirectoryAsync(string absolutePath) =>
            Task.FromResult(Directory.Exists(absolutePath));

        public Task<IReadOnlyList<string>> ReadDirAsync(string absolutePath) =>
            Task.FromResult<IReadOnlyList<string>>(Directory.GetFileSystemEntries(absolutePath)
                .Select(entry => Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .ToList());
    }

    /// <summary>The TS <c>createLsToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateLsToolDefinition(string cwd, LsToolOptions? options = null)
    {
        var ops = options?.Operations ?? new LocalLsOperations();
        return new ToolDefinition
        {
            Name = "ls",
            Label = "ls",
            Description =
                $"List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to {DefaultLimit} entries or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
            PromptSnippet = Snippet,
            Parameters = LsSchema,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var path = ToolArgs.GetString(args, "path");
                var limit = ToolArgs.GetInt(args, "limit");

                var dirPath = ToolPathUtils.ResolveToCwd(path ?? ".", ToolContexts.ResolveCwd(ctx, cwd));
                var effectiveLimit = limit ?? DefaultLimit;
                signal.ThrowIfCancellationRequested();

                // Check if path exists.
                if (!await ops.ExistsAsync(dirPath).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Path not found: {dirPath}");
                }

                // Check if path is a directory.
                if (!await ops.IsDirectoryAsync(dirPath).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Not a directory: {dirPath}");
                }

                // Read directory entries.
                IReadOnlyList<string> entries;
                try
                {
                    entries = await ops.ReadDirAsync(dirPath).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"Cannot read directory: {error.Message}");
                }

                signal.ThrowIfCancellationRequested();

                // Sort alphabetically, case-insensitive. The JS localeCompare is culture sensitive;
                // the port keeps the ordinal-insensitive ordering so the result is stable across hosts.
                var sorted = entries.OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase).ToList();

                // Format entries with directory indicators.
                var results = new List<string>();
                var entryLimitReached = false;
                foreach (var entry in sorted)
                {
                    if (results.Count >= effectiveLimit)
                    {
                        entryLimitReached = true;
                        break;
                    }

                    var fullPath = Path.Combine(dirPath, entry);
                    bool isDirectory;
                    try
                    {
                        isDirectory = await ops.IsDirectoryAsync(fullPath).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // Skip entries we cannot stat.
                        continue;
                    }

                    results.Add(isDirectory ? $"{entry}/" : entry);
                }

                if (results.Count == 0)
                {
                    return new AgentToolResult([new TextContent("(empty directory)")]);
                }

                var rawOutput = string.Join("\n", results);
                // Apply byte truncation. There is no separate line limit because entry count is already capped.
                var truncation = Truncate.TruncateHead(rawOutput, new Truncate.TruncationOptions { MaxLines = int.MaxValue });
                var output = truncation.Content;
                var notices = new List<string>();
                if (entryLimitReached)
                {
                    notices.Add($"{effectiveLimit} entries limit reached. Use limit={effectiveLimit * 2} for more");
                }

                if (truncation.Truncated)
                {
                    notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
                }

                LsToolDetails? details = notices.Count > 0
                    ? new LsToolDetails
                    {
                        Truncation = truncation.Truncated ? truncation : null,
                        EntryLimitReached = entryLimitReached ? effectiveLimit : null,
                    }
                    : null;
                if (notices.Count > 0)
                {
                    output += $"\n\n[{string.Join(". ", notices)}]";
                }

                return new AgentToolResult([new TextContent(output)], Details: details);
            },
        };
    }

    /// <summary>The TS <c>createLsTool(cwd, options)</c>.</summary>
    public static AgentTool CreateLsTool(string cwd, LsToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateLsToolDefinition(cwd, options));
}
