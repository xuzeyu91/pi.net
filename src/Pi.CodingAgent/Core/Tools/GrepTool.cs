using System.Text;
using System.Text.Json;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>GrepToolDetails</c>.</summary>
public sealed record GrepToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public int? MatchLimitReached { get; init; }

    public bool? LinesTruncated { get; init; }
}

/// <summary>
/// Pluggable operations for the grep tool. Override these to delegate search to remote systems
/// (for example SSH). Port of the TS <c>GrepOperations</c>.
/// </summary>
public interface IGrepToolOperations
{
    /// <summary>Check if path is a directory. Throws if path does not exist.</summary>
    Task<bool> IsDirectoryAsync(string absolutePath);

    /// <summary>Read file contents for context lines.</summary>
    Task<string> ReadFileAsync(string absolutePath);
}

/// <summary>Port of the TS <c>GrepToolOptions</c>.</summary>
public sealed record GrepToolOptions
{
    /// <summary>Custom operations for grep. Default: local filesystem plus ripgrep.</summary>
    public IGrepToolOperations? Operations { get; init; }
}

/// <summary>The grep tool. Port of <c>core/tools/grep.ts</c>.</summary>
public static class GrepTool
{
    /// <summary>The TS <c>grepToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Search file contents for patterns (respects .gitignore)";

    private const int DefaultLimit = 100;

    private static readonly ToolSchema GrepSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["pattern"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Search pattern (regex or literal string)",
            },
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Directory or file to search (default: current directory)",
            },
            ["glob"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'",
            },
            ["ignoreCase"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Case-insensitive search (default: false)",
            },
            ["literal"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Treat pattern as literal string instead of regex (default: false)",
            },
            ["context"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Number of lines to show before and after each match (default: 0)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of matches to return (default: 100)",
            },
        },
        ["required"] = new[] { "pattern" },
        ["additionalProperties"] = false,
    });

    private sealed class LocalGrepOperations : IGrepToolOperations
    {
        public Task<bool> IsDirectoryAsync(string absolutePath) =>
            Task.FromResult(Directory.Exists(absolutePath));

        public Task<string> ReadFileAsync(string absolutePath) =>
            File.ReadAllTextAsync(absolutePath, Encoding.UTF8);
    }

    /// <summary>The TS <c>createGrepToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateGrepToolDefinition(string cwd, GrepToolOptions? options = null)
    {
        var customOps = options?.Operations;
        return new ToolDefinition
        {
            Name = "grep",
            Label = "grep",
            Description =
                $"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to {DefaultLimit} matches or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Long lines are truncated to {Truncate.GrepMaxLineLength} chars.",
            PromptSnippet = Snippet,
            Parameters = GrepSchema,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var pattern = ToolArgs.GetString(args, "pattern") ?? "";
                var searchDir = ToolArgs.GetString(args, "path");
                var glob = ToolArgs.GetString(args, "glob");
                var ignoreCase = ToolArgs.GetBool(args, "ignoreCase") ?? false;
                var literal = ToolArgs.GetBool(args, "literal") ?? false;
                var context = ToolArgs.GetInt(args, "context");
                var limit = ToolArgs.GetInt(args, "limit");

                var rgPath = await ToolsManager.EnsureToolAsync("rg", cancellationToken: signal).ConfigureAwait(false);
                if (rgPath is null)
                {
                    throw new InvalidOperationException("ripgrep (rg) is not available and could not be downloaded");
                }

                var searchPath = ToolPathUtils.ResolveToCwd(searchDir ?? ".", ToolContexts.ResolveCwd(ctx, cwd));
                var ops = customOps ?? new LocalGrepOperations();
                bool isDirectory;
                try
                {
                    isDirectory = await ops.IsDirectoryAsync(searchPath).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"Path not found: {searchPath}");
                }

                var contextValue = context is > 0 ? context.Value : 0;
                var effectiveLimit = Math.Max(1, limit ?? DefaultLimit);

                string FormatPath(string filePath)
                {
                    if (isDirectory)
                    {
                        var relative = Path.GetRelativePath(searchPath, filePath);
                        if (relative.Length > 0 && !relative.StartsWith("..", StringComparison.Ordinal))
                        {
                            return relative.Replace('\\', '/');
                        }
                    }

                    return Path.GetFileName(filePath);
                }

                var fileCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                async Task<List<string>> GetFileLines(string filePath)
                {
                    if (fileCache.TryGetValue(filePath, out var cached))
                    {
                        return cached;
                    }

                    List<string> lines;
                    try
                    {
                        var content = await ops.ReadFileAsync(filePath).ConfigureAwait(false);
                        lines = EditDiff.NormalizeToLf(content).Split('\n').ToList();
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        lines = [];
                    }

                    fileCache[filePath] = lines;
                    return lines;
                }

                var rgArgs = new List<string> { "--json", "--line-number", "--color=never", "--hidden" };
                if (ignoreCase)
                {
                    rgArgs.Add("--ignore-case");
                }

                if (literal)
                {
                    rgArgs.Add("--fixed-strings");
                }

                if (glob is not null)
                {
                    rgArgs.Add("--glob");
                    rgArgs.Add(glob);
                }

                rgArgs.Add("--");
                rgArgs.Add(pattern);
                rgArgs.Add(searchPath);

                var matches = new List<(string FilePath, int LineNumber, string? LineText)>();
                var outputLines = new List<string>();
                var matchCount = 0;
                var matchLimitReached = false;
                var linesTruncated = false;
                var killedDueToLimit = false;
                var stderr = new StringBuilder();

                var child = ChildProcess.SpawnProcess(rgPath, rgArgs, new SpawnOptions
                {
                    Cwd = cwd,
                    Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
                    WindowsHide = true,
                });

                child.StdoutData += data =>
                {
                    // ripgrep emits one JSON event per line; the port buffers the raw bytes and
                    // splits them the way Node's readline interface does.
                    var text = Encoding.UTF8.GetString(data);
                    foreach (var line in text.Split('\n'))
                    {
                        if (line.Trim().Length == 0 || matchCount >= effectiveLimit)
                        {
                            continue;
                        }

                        JsonDocument? doc = null;
                        try
                        {
                            doc = JsonDocument.Parse(line);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        using (doc)
                        {
                            var root = doc.RootElement;
                            if (root.TryGetProperty("type", out var typeElement)
                                && typeElement.GetString() == "match"
                                && root.TryGetProperty("data", out var dataElement))
                            {
                                matchCount++;
                                var filePath = dataElement.TryGetProperty("path", out var pathElement)
                                    ? pathElement.GetProperty("text").GetString()
                                    : null;
                                var lineNumber = dataElement.TryGetProperty("line_number", out var lineNumberElement)
                                    ? lineNumberElement.GetInt32()
                                    : (int?)null;
                                var lineText = dataElement.TryGetProperty("lines", out var linesElement)
                                    ? linesElement.GetProperty("text").GetString()
                                    : null;
                                if (filePath is not null && lineNumber is not null)
                                {
                                    matches.Add((filePath, lineNumber.Value, lineText));
                                }

                                if (matchCount >= effectiveLimit)
                                {
                                    matchLimitReached = true;
                                    if (child.Id is int pid)
                                    {
                                        Shell.KillProcessTree(pid);
                                        killedDueToLimit = true;
                                    }
                                }
                            }
                        }
                    }
                };

                child.StderrData += data => stderr.Append(Encoding.UTF8.GetString(data));

                using var registration = signal.Register(() =>
                {
                    if (child.Id is int pid)
                    {
                        Shell.KillProcessTree(pid);
                    }
                });

                var exitCode = await child.WaitAsync().ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();

                // ripgrep exits 0 on matches, 1 on no matches; anything else is a real failure.
                if (!killedDueToLimit && exitCode is not 0 and not 1)
                {
                    var errorMsg = stderr.ToString().Trim();
                    throw new InvalidOperationException(
                        errorMsg.Length > 0 ? errorMsg : $"ripgrep exited with code {exitCode}");
                }

                if (matchCount == 0)
                {
                    return new AgentToolResult([new TextContent("No matches found")]);
                }

                async Task<List<string>> FormatBlock(string filePath, int lineNumber)
                {
                    var relativePath = FormatPath(filePath);
                    var lines = await GetFileLines(filePath).ConfigureAwait(false);
                    if (lines.Count == 0)
                    {
                        return [$"{relativePath}:{lineNumber}: (unable to read file)"];
                    }

                    var block = new List<string>();
                    var start = contextValue > 0 ? Math.Max(1, lineNumber - contextValue) : lineNumber;
                    var end = contextValue > 0 ? Math.Min(lines.Count, lineNumber + contextValue) : lineNumber;
                    for (var current = start; current <= end; current++)
                    {
                        var lineText = lines[current - 1];
                        var sanitized = lineText.Replace("\r", "", StringComparison.Ordinal);
                        var isMatchLine = current == lineNumber;
                        var (truncatedText, wasTruncated) = Truncate.TruncateLine(sanitized);
                        if (wasTruncated)
                        {
                            linesTruncated = true;
                        }

                        block.Add(isMatchLine
                            ? $"{relativePath}:{current}: {truncatedText}"
                            : $"{relativePath}-{current}- {truncatedText}");
                    }

                    return block;
                }

                foreach (var match in matches)
                {
                    if (contextValue == 0 && match.LineText is not null)
                    {
                        var relativePath = FormatPath(match.FilePath);
                        var sanitized = match.LineText
                            .Replace("\r\n", "\n", StringComparison.Ordinal)
                            .Replace("\r", "", StringComparison.Ordinal);
                        if (sanitized.EndsWith('\n'))
                        {
                            sanitized = sanitized[..^1];
                        }

                        var (truncatedText, wasTruncated) = Truncate.TruncateLine(sanitized);
                        if (wasTruncated)
                        {
                            linesTruncated = true;
                        }

                        outputLines.Add($"{relativePath}:{match.LineNumber}: {truncatedText}");
                    }
                    else
                    {
                        outputLines.AddRange(await FormatBlock(match.FilePath, match.LineNumber).ConfigureAwait(false));
                    }
                }

                var rawOutput = string.Join("\n", outputLines);
                // Apply byte truncation. There is no line limit here because the match limit already capped rows.
                var truncation = Truncate.TruncateHead(rawOutput, new Truncate.TruncationOptions
                {
                    MaxLines = int.MaxValue,
                });
                var output = truncation.Content;
                var notices = new List<string>();
                if (matchLimitReached)
                {
                    notices.Add($"{effectiveLimit} matches limit reached. Use limit={effectiveLimit * 2} for more, or refine pattern");
                }

                if (truncation.Truncated)
                {
                    notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
                }

                if (linesTruncated)
                {
                    notices.Add($"Some lines truncated to {Truncate.GrepMaxLineLength} chars. Use read tool to see full lines");
                }

                GrepToolDetails? details = notices.Count > 0
                    ? new GrepToolDetails
                    {
                        Truncation = truncation.Truncated ? truncation : null,
                        MatchLimitReached = matchLimitReached ? effectiveLimit : null,
                        LinesTruncated = linesTruncated ? true : null,
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

    /// <summary>The TS <c>createGrepTool(cwd, options)</c>.</summary>
    public static AgentTool CreateGrepTool(string cwd, GrepToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateGrepToolDefinition(cwd, options));
}
