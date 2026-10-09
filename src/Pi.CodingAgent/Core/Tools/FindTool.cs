using System.Text;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>FindToolDetails</c>.</summary>
public sealed record FindToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public int? ResultLimitReached { get; init; }
}

/// <summary>
/// Pluggable operations for the find tool. Override these to delegate file search to remote
/// systems (for example SSH). Port of the TS <c>FindOperations</c>.
/// </summary>
public interface IFindToolOperations
{
    /// <summary>Check if path exists.</summary>
    Task<bool> ExistsAsync(string absolutePath);

    /// <summary>Find files matching glob pattern. Returns relative or absolute paths.</summary>
    Task<IReadOnlyList<string>> GlobAsync(string pattern, string cwd, FindGlobOptions options);
}

/// <summary>Port of the TS glob options object.</summary>
public sealed record FindGlobOptions
{
    public required IReadOnlyList<string> Ignore { get; init; }

    public required int Limit { get; init; }
}

/// <summary>Port of the TS <c>FindToolOptions</c>.</summary>
public sealed record FindToolOptions
{
    /// <summary>Custom operations for find. Default: local filesystem plus fd.</summary>
    public IFindToolOperations? Operations { get; init; }
}

/// <summary>The find tool. Port of <c>core/tools/find.ts</c>.</summary>
public static class FindTool
{
    /// <summary>The TS <c>findToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Find files by glob pattern (respects .gitignore)";

    private const int DefaultLimit = 1000;

    private static readonly ToolSchema FindSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["pattern"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'",
            },
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Directory to search in (default: current directory)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of results (default: 1000)",
            },
        },
        ["required"] = new[] { "pattern" },
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// Relativize a find result against the search root and normalize it to posix separators.
    /// The TS <c>relativizeFindResultPath</c>.
    /// </summary>
    public static string RelativizeFindResultPath(string resultPath, string searchPath)
    {
        var hadTrailingSeparator =
            resultPath.EndsWith(Path.DirectorySeparatorChar)
            || (Path.DirectorySeparatorChar == '\\' && resultPath.EndsWith('/'));
        var relativePath = Path.IsPathRooted(resultPath) ? Path.GetRelativePath(searchPath, resultPath) : resultPath;
        var posixPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return hadTrailingSeparator && !posixPath.EndsWith('/') ? $"{posixPath}/" : posixPath;
    }

    private static string BuildResultOutput(IReadOnlyList<string> relativized, int effectiveLimit, out FindToolDetails? details)
    {
        var resultLimitReached = relativized.Count >= effectiveLimit;
        var rawOutput = string.Join("\n", relativized);
        var truncation = Truncate.TruncateHead(rawOutput, new Truncate.TruncationOptions { MaxLines = int.MaxValue });
        var resultOutput = truncation.Content;
        var notices = new List<string>();
        if (resultLimitReached)
        {
            notices.Add($"{effectiveLimit} results limit reached. Use limit={effectiveLimit * 2} for more, or refine pattern");
        }

        if (truncation.Truncated)
        {
            notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
        }

        details = notices.Count > 0
            ? new FindToolDetails
            {
                Truncation = truncation.Truncated ? truncation : null,
                ResultLimitReached = resultLimitReached ? effectiveLimit : null,
            }
            : null;
        if (notices.Count > 0)
        {
            resultOutput += $"\n\n[{string.Join(". ", notices)}]";
        }

        return resultOutput;
    }

    /// <summary>The TS <c>createFindToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateFindToolDefinition(string cwd, FindToolOptions? options = null)
    {
        var customOps = options?.Operations;
        return new ToolDefinition
        {
            Name = "find",
            Label = "find",
            Description =
                $"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to {DefaultLimit} results or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
            PromptSnippet = Snippet,
            Parameters = FindSchema,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var pattern = ToolArgs.GetString(args, "pattern") ?? "";
                var searchDir = ToolArgs.GetString(args, "path");
                var limit = ToolArgs.GetInt(args, "limit");

                var searchPath = ToolPathUtils.ResolveToCwd(searchDir ?? ".", ToolContexts.ResolveCwd(ctx, cwd));
                var effectiveLimit = limit ?? DefaultLimit;
                signal.ThrowIfCancellationRequested();

                // If custom operations provide glob(), use that instead of fd.
                if (customOps is not null)
                {
                    if (!await customOps.ExistsAsync(searchPath).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException($"Path not found: {searchPath}");
                    }

                    signal.ThrowIfCancellationRequested();
                    var results = await customOps.GlobAsync(pattern, searchPath, new FindGlobOptions
                    {
                        Ignore = ["**/node_modules/**", "**/.git/**"],
                        Limit = effectiveLimit,
                    }).ConfigureAwait(false);
                    signal.ThrowIfCancellationRequested();
                    if (results.Count == 0)
                    {
                        return new AgentToolResult([new TextContent("No files found matching pattern")]);
                    }

                    // Relativize paths against the search root for stable output.
                    var relativized = results.Select(p => RelativizeFindResultPath(p, searchPath)).ToList();
                    return new AgentToolResult(
                        [new TextContent(BuildResultOutput(relativized, effectiveLimit, out var customDetails))],
                        Details: customDetails);
                }

                // Default implementation uses fd.
                var fdPath = await ToolsManager.EnsureToolAsync("fd", cancellationToken: signal).ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();
                if (fdPath is null)
                {
                    throw new InvalidOperationException("fd is not available and could not be downloaded");
                }

                var fdArgs = new List<string> { "--glob", "--color=never", "--hidden" };

                // fd normally ignores .gitignore outside git repos, so keep --no-require-git
                // there. Inside repos, use fd's default git-aware behavior so parent
                // .gitignore rules stop at nested repo boundaries.
                var insideGitRepo = false;
                for (var current = searchPath; ;)
                {
                    if (await ToolPathUtils.PathExistsAsync(Path.Combine(current, ".git")).ConfigureAwait(false))
                    {
                        insideGitRepo = true;
                        break;
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (parent is null || parent == current)
                    {
                        break;
                    }

                    current = parent;
                }

                if (!insideGitRepo)
                {
                    fdArgs.Add("--no-require-git");
                }

                fdArgs.Add("--max-results");
                fdArgs.Add(effectiveLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));

                // fd --glob matches against the basename unless --full-path is set; in --full-path
                // mode it matches against the absolute candidate path, so a path-containing
                // pattern like 'src/**/*.spec.ts' needs a leading '**/' to match anything.
                var effectivePattern = pattern;
                if (pattern.Contains('/'))
                {
                    fdArgs.Add("--full-path");
                    if (!pattern.StartsWith('/') && !pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**")
                    {
                        effectivePattern = $"**/{pattern}";
                    }

                    // fd matches full paths using native separators on Windows.
                    if (NodePath.IsWindows)
                    {
                        effectivePattern = effectivePattern.Replace("/", @"[/\\]", StringComparison.Ordinal);
                    }
                }

                fdArgs.Add("--");
                fdArgs.Add(effectivePattern);
                fdArgs.Add(searchPath);

                var lines = new List<string>();
                var stderr = new StringBuilder();
                var child = ChildProcess.SpawnProcess(fdPath, fdArgs, new SpawnOptions
                {
                    Cwd = cwd,
                    Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
                    WindowsHide = true,
                });

                child.StdoutData += data =>
                {
                    var text = Encoding.UTF8.GetString(data);
                    foreach (var line in text.Split('\n'))
                    {
                        lines.Add(line);
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

                var rawLines = string.Join("\n", lines);
                if (exitCode != 0)
                {
                    var errorMsg = stderr.ToString().Trim();
                    if (rawLines.Length == 0)
                    {
                        throw new InvalidOperationException(errorMsg.Length > 0 ? errorMsg : $"fd exited with code {exitCode}");
                    }
                }

                if (rawLines.Length == 0)
                {
                    return new AgentToolResult([new TextContent("No files found matching pattern")]);
                }

                var relativizedResults = new List<string>();
                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r').Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    relativizedResults.Add(RelativizeFindResultPath(line, searchPath));
                }

                return new AgentToolResult(
                    [new TextContent(BuildResultOutput(relativizedResults, effectiveLimit, out var details))],
                    Details: details);
            },
        };
    }

    /// <summary>The TS <c>createFindTool(cwd, options)</c>.</summary>
    public static AgentTool CreateFindTool(string cwd, FindToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateFindToolDefinition(cwd, options));
}
