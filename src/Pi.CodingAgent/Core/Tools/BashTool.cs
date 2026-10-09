using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>BashToolDetails</c>.</summary>
public sealed record BashToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public string? FullOutputPath { get; init; }
}

/// <summary>
/// Pluggable operations for the shell tools. Override these to delegate command execution to remote
/// systems (for example SSH). Port of the TS <c>BashOperations</c>.
/// </summary>
public interface IBashToolOperations
{
    /// <summary>
    /// Execute a command and stream output. Report signal terminations as 128 + signal number; a null
    /// exit code is treated as a failed command.
    /// </summary>
    Task<ShellExecResult> ExecAsync(
        string command,
        string cwd,
        ShellExecOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Options for <see cref="IBashToolOperations.ExecAsync"/>. Port of the TS exec options.</summary>
public sealed record ShellExecOptions
{
    public required Action<byte[]> OnData { get; init; }

    public int? Timeout { get; init; }

    public IReadOnlyDictionary<string, string?>? Env { get; init; }
}

/// <summary>Port of the TS <c>{ exitCode: number | null }</c>.</summary>
public sealed record ShellExecResult(int? ExitCode);

/// <summary>Port of the TS <c>BashSpawnContext</c>.</summary>
public sealed record BashSpawnContext(string Command, string Cwd, IReadOnlyDictionary<string, string?> Env);

/// <summary>Port of the TS <c>BashSpawnHook</c>.</summary>
public delegate BashSpawnContext BashSpawnHook(BashSpawnContext context);

/// <summary>Port of the TS <c>BashToolOptions</c>.</summary>
public sealed record BashToolOptions
{
    /// <summary>Custom operations for command execution. Default: local shell.</summary>
    public IBashToolOperations? Operations { get; init; }

    /// <summary>Command prefix prepended to every command (for example shell setup commands).</summary>
    public string? CommandPrefix { get; init; }

    /// <summary>Optional explicit shell path from settings.</summary>
    public string? ShellPath { get; init; }

    /// <summary>Expose current Pi session metadata as PI_* environment variables. Default: true.</summary>
    public bool? ExposeSessionEnvironment { get; init; }

    /// <summary>Hook to adjust command, cwd, or env before execution.</summary>
    public BashSpawnHook? SpawnHook { get; init; }
}

/// <summary>Port of the TS <c>ShellToolConfig</c>.</summary>
public sealed record ShellToolConfig
{
    public required string Name { get; init; }

    public required string Label { get; init; }

    public required string ShellName { get; init; }

    public required string Prompt { get; init; }

    public required string PromptSnippet { get; init; }

    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public required string TempFilePrefix { get; init; }
}

/// <summary>The bash and powershell tools. Port of <c>core/tools/bash.ts</c>.</summary>
public static class BashTool
{
    private const int MaxTimeoutMs = 2_147_483_647;

    /// <summary>Output limit of <c>structuredContent.output</c>, which programmatic callers such as codemode scripts receive.</summary>
    private const int StructuredOutputMaxBytes = 1024 * 1024;

    private const int MaxTimeoutSeconds = MaxTimeoutMs / 1000;

    /// <summary>The TS <c>BASH_UPDATE_THROTTLE_MS</c> from renderers/bash.ts.</summary>
    internal const int BashUpdateThrottleMs = 100;

    /// <summary>The TS <c>bashToolSystemPromptContribution</c>.</summary>
    public const string BashSnippet = "Execute bash commands (ls, grep, find, etc.)";

    public static readonly IReadOnlyList<string> BashGuidelines =
        ["You can inspect PI_* environment variables for current model and session details."];

    private static readonly ToolSchema BashSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["command"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Shell command to execute",
            },
            ["timeout"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Timeout in seconds (optional, no default timeout)",
            },
        },
        ["required"] = new[] { "command" },
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// Result for programmatic callers such as codemode scripts. A non-zero exit code is an error
    /// result for the model, but scripts still resolve to this value. <c>output</c> is not limited
    /// like the model-facing output: callers decide how much of it reaches the model.
    /// </summary>
    private static readonly ToolSchema BashOutputSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["output"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Combined stdout and stderr, possibly truncated",
            },
            ["truncated"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["full_output_path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Full output, when truncated",
            },
            ["exit_code"] = new Dictionary<string, object?> { ["type"] = "number" },
            ["wall_time_seconds"] = new Dictionary<string, object?> { ["type"] = "number" },
        },
        ["required"] = new[] { "output", "truncated", "exit_code", "wall_time_seconds" },
        ["additionalProperties"] = false,
    });

    private static int? ResolveTimeoutMs(int? timeout)
    {
        if (timeout is null)
        {
            return null;
        }

        if (!double.IsFinite(timeout.Value) || timeout.Value <= 0)
        {
            throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
        }

        var timeoutMs = (int)(timeout.Value * 1000);
        if (timeoutMs > MaxTimeoutMs)
        {
            throw new InvalidOperationException($"Invalid timeout: maximum is {MaxTimeoutSeconds} seconds");
        }

        return timeoutMs;
    }

    /// <summary>Shared process execution used by the built-in shell tools.</summary>
    public static IBashToolOperations CreateLocalShellOperations(string shellName, Func<ShellConfig> resolveShellConfig) =>
        new LocalShellOperations(shellName, resolveShellConfig);

    /// <summary>
    /// Create bash operations using pi's built-in local shell execution backend. Useful for
    /// extensions that intercept user_bash and still want pi's standard local shell behavior while
    /// wrapping or rewriting commands.
    /// </summary>
    public static IBashToolOperations CreateLocalBashOperations(string? shellPath = null) =>
        CreateLocalShellOperations("bash", () => Shell.GetShellConfig(shellPath));

    private sealed class LocalShellOperations(string shellName, Func<ShellConfig> resolveShellConfig) : IBashToolOperations
    {
        public async Task<ShellExecResult> ExecAsync(
            string command, string cwd, ShellExecOptions options, CancellationToken cancellationToken)
        {
            var timeoutMs = ResolveTimeoutMs(options.Timeout);
            cancellationToken.ThrowIfCancellationRequested();
            var shellConfig = resolveShellConfig();
            if (!Directory.Exists(cwd))
            {
                throw new InvalidOperationException(
                    $"Working directory does not exist: {cwd}\nCannot execute {shellName} commands.");
            }

            var commandFromStdin = shellConfig.CommandTransport == "stdin";
            var args = commandFromStdin
                ? shellConfig.Args
                : (IReadOnlyList<string>)[.. shellConfig.Args, command];
            var child = ChildProcess.SpawnProcess(shellConfig.Shell, args, new SpawnOptions
            {
                Cwd = cwd,
                Detached = !NodePath.IsWindows,
                Env = options.Env ?? Shell.GetShellEnv(),
                Stdio =
                [
                    commandFromStdin ? StdioMode.Pipe : StdioMode.Ignore,
                    StdioMode.Pipe,
                    StdioMode.Pipe,
                ],
                WindowsHide = true,
            });

            if (commandFromStdin && child.Stdin is { } stdin)
            {
                // Node ignores a stdin write error (EPIPE when the shell exits first); the port
                // swallows it the same way.
                try
                {
                    var commandBytes = Encoding.UTF8.GetBytes(command);
                    await stdin.WriteAsync(commandBytes, cancellationToken).ConfigureAwait(false);
                    await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stdin.Close();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // The shell closed stdin early; nothing to do.
                }
            }

            if (child.Id is int trackedPid)
            {
                Shell.TrackDetachedChildPid(trackedPid);
            }

            var timedOut = false;
            System.Threading.Timer? timeoutHandle = null;
            void OnAbort()
            {
                if (child.Id is int pid)
                {
                    Shell.KillProcessTree(pid);
                }
            }

            try
            {
                // Set timeout if provided.
                if (timeoutMs is int ms)
                {
                    timeoutHandle = new System.Threading.Timer(
                        _ =>
                        {
                            timedOut = true;
                            if (child.Id is int pid)
                            {
                                Shell.KillProcessTree(pid);
                            }
                        },
                        null,
                        ms,
                        Timeout.Infinite);
                }

                // Stream stdout and stderr.
                child.StdoutData += options.OnData;
                child.StderrData += options.OnData;

                // Handle abort signal by killing the entire process tree.
                using var registration = cancellationToken.Register(OnAbort);

                // Handle shell spawn errors and wait for the process to terminate without hanging
                // on inherited stdio handles held by detached descendants.
                var exitCode = await child.WaitAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (timedOut)
                {
                    throw new InvalidOperationException($"timeout:{options.Timeout}");
                }

                // A signal-killed shell has no exit code. Use the standard shell convention so
                // callers do not mistake the termination for a successful command. The port's
                // ChildProcessHandle.SignalCode is always null (.NET does not surface signals), so
                // the fallback is the plain failure code 1.
                return new ShellExecResult(exitCode ?? 1);
            }
            finally
            {
                child.StdoutData -= options.OnData;
                child.StderrData -= options.OnData;
                if (child.Id is int untrackedPid)
                {
                    Shell.UntrackDetachedChildPid(untrackedPid);
                }

                timeoutHandle?.Dispose();
            }
        }
    }

    private static BashSpawnContext ResolveSpawnContext(
        string command,
        string cwd,
        BashSpawnHook? spawnHook,
        bool exposeSessionEnvironment,
        IToolContext? ctx)
    {
        var env = new Dictionary<string, string?>(Shell.GetShellEnv(), StringComparer.Ordinal);
        env.Remove("PI_SESSION_ID");
        env.Remove("PI_SESSION_FILE");
        env.Remove("PI_PROVIDER");
        env.Remove("PI_MODEL");
        env.Remove("PI_REASONING_LEVEL");
        if (exposeSessionEnvironment && ctx is not null)
        {
            var model = ctx.Model;
            env["PI_SESSION_ID"] = ctx.SessionId;
            var sessionFile = ctx.SessionFile;
            if (sessionFile is not null)
            {
                env["PI_SESSION_FILE"] = sessionFile;
            }

            if (model is not null)
            {
                env["PI_PROVIDER"] = model.Provider;
                env["PI_MODEL"] = model.Id;
            }

            if (ctx.ThinkingLevel is not null)
            {
                env["PI_REASONING_LEVEL"] = ctx.ThinkingLevel;
            }
        }

        var baseContext = new BashSpawnContext(command, cwd, env);
        return spawnHook?.Invoke(baseContext) ?? baseContext;
    }

    /// <summary>The TS <c>createShellToolDefinition(cwd, config, options)</c>.</summary>
    public static ToolDefinition CreateShellToolDefinition(
        string cwd,
        ShellToolConfig config,
        BashToolOptions? options = null)
    {
        var ops = options?.Operations ?? CreateLocalBashOperations(options?.ShellPath);
        var commandPrefix = options?.CommandPrefix;
        var exposeSessionEnvironment = options?.ExposeSessionEnvironment ?? true;
        var spawnHook = options?.SpawnHook;
        return new ToolDefinition
        {
            Name = config.Name,
            Label = config.Label,
            Description =
                $"Execute a {config.ShellName} command in the current working directory. Returns stdout and stderr. Output is truncated to last {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.",
            PromptSnippet = config.PromptSnippet,
            PromptGuidelines = exposeSessionEnvironment && config.PromptGuidelines is not null
                ? config.PromptGuidelines.ToList()
                : null,
            Parameters = BashSchema,
            OutputSchema = BashOutputSchema,
            ConstrainedSampling = new JsonObject
            {
                ["type"] = "json_schema",
                ["strict"] = "prefer",
            },
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var command = ToolArgs.GetString(args, "command") ?? "";
                var timeout = ToolArgs.GetInt(args, "timeout");

                var resolvedCommand = commandPrefix is not null ? $"{commandPrefix}\n{command}" : command;
                var spawnContext = ResolveSpawnContext(
                    resolvedCommand,
                    ToolContexts.ResolveCwd(ctx, cwd),
                    spawnHook,
                    exposeSessionEnvironment,
                    ctx);
                var output = new OutputAccumulator(new OutputAccumulatorOptions { TempFilePrefix = config.TempFilePrefix });
                var acceptingOutput = true;
                System.Threading.Timer? updateTimer = null;
                var updateDirty = false;
                var lastUpdateAt = Environment.TickCount64;

                void EmitOutputUpdate()
                {
                    if (onUpdate is null || !updateDirty)
                    {
                        return;
                    }

                    updateDirty = false;
                    lastUpdateAt = Environment.TickCount64;
                    var snapshot = output.Snapshot(persistIfTruncated: true);
                    _ = onUpdate(new AgentToolResult(
                        [new TextContent(snapshot.Content.Length > 0 ? snapshot.Content : "")],
                        new BashToolDetails
                        {
                            Truncation = snapshot.Truncation.Truncated ? snapshot.Truncation : null,
                            FullOutputPath = snapshot.FullOutputPath,
                        }));
                }

                void ClearUpdateTimer()
                {
                    updateTimer?.Dispose();
                    updateTimer = null;
                }

                void ScheduleOutputUpdate()
                {
                    if (onUpdate is null)
                    {
                        return;
                    }

                    updateDirty = true;
                    var delay = BashUpdateThrottleMs - (Environment.TickCount64 - lastUpdateAt);
                    if (delay <= 0)
                    {
                        ClearUpdateTimer();
                        EmitOutputUpdate();
                        return;
                    }

                    updateTimer ??= new System.Threading.Timer(
                        _ =>
                        {
                            updateTimer = null;
                            EmitOutputUpdate();
                        },
                        null,
                        (int)delay,
                        Timeout.Infinite);
                }

                if (onUpdate is not null)
                {
                    _ = onUpdate(new AgentToolResult([], Details: null));
                }

                void HandleData(byte[] data)
                {
                    if (!acceptingOutput)
                    {
                        return;
                    }

                    output.Append(data);
                    ScheduleOutputUpdate();
                }

                async Task<OutputSnapshot> FinishOutputAsync()
                {
                    acceptingOutput = false;
                    output.Finish();
                    ClearUpdateTimer();
                    EmitOutputUpdate();
                    var snapshot = output.Snapshot(persistIfTruncated: true);
                    await output.CloseTempFileAsync().ConfigureAwait(false);
                    return snapshot;
                }

                var startedAt = Stopwatch.GetTimestamp();

                (string Text, BashToolDetails? Details) FormatOutput(OutputSnapshot snapshot, string emptyText = "(no output)")
                {
                    var truncation = snapshot.Truncation;
                    var text = snapshot.Content.Length > 0 ? snapshot.Content : emptyText;
                    BashToolDetails? details = null;
                    if (truncation.Truncated)
                    {
                        details = new BashToolDetails
                        {
                            Truncation = truncation,
                            FullOutputPath = snapshot.FullOutputPath,
                        };
                        var startLine = truncation.TotalLines - truncation.OutputLines + 1;
                        var endLine = truncation.TotalLines;
                        if (truncation.LastLinePartial)
                        {
                            var lastLineSize = Truncate.FormatSize(output.GetLastLineBytes());
                            text +=
                                $"\n\n[Showing last {Truncate.FormatSize(truncation.OutputBytes)} of line {endLine} (line is {lastLineSize}). Full output: {snapshot.FullOutputPath}]";
                        }
                        else if (truncation.TruncatedBy == TruncatedBy.Lines)
                        {
                            text +=
                                $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines}. Full output: {snapshot.FullOutputPath}]";
                        }
                        else
                        {
                            text +=
                                $"\n\n[Showing lines {startLine}-{endLine} of {truncation.TotalLines} ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit). Full output: {snapshot.FullOutputPath}]";
                        }
                    }

                    return (text, details);
                }

                static string AppendStatus(string text, string status) =>
                    text.Length > 0 ? $"{text}\n\n{status}" : status;

                try
                {
                    int? exitCode;
                    try
                    {
                        var result = await ops.ExecAsync(
                            spawnContext.Command,
                            spawnContext.Cwd,
                            new ShellExecOptions
                            {
                                OnData = HandleData,
                                Timeout = timeout,
                                Env = spawnContext.Env,
                            },
                            signal).ConfigureAwait(false);
                        exitCode = result.ExitCode;
                    }
                    catch (Exception error) when (error is OperationCanceledException)
                    {
                        var snapshot = await FinishOutputAsync().ConfigureAwait(false);
                        var (text, _) = FormatOutput(snapshot, "");
                        throw new InvalidOperationException(AppendStatus(text, "Command aborted"));
                    }
                    catch (InvalidOperationException error) when (error.Message.StartsWith("timeout:", StringComparison.Ordinal))
                    {
                        var snapshot = await FinishOutputAsync().ConfigureAwait(false);
                        var (text, _) = FormatOutput(snapshot, "");
                        var timeoutSecs = error.Message.Split(':')[1];
                        throw new InvalidOperationException(AppendStatus(text, $"Command timed out after {timeoutSecs} seconds"));
                    }

                    var finalSnapshot = await FinishOutputAsync().ConfigureAwait(false);
                    var (outputText, details) = FormatOutput(finalSnapshot);
                    if (exitCode is null)
                    {
                        throw new InvalidOperationException(AppendStatus(outputText, "Command terminated without an exit code"));
                    }

                    var elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
                    var wallTimeSeconds = Math.Round(elapsedMs / 100, MidpointRounding.AwayFromZero) / 10;
                    var fullOutput = await output.ReadFullOutputAsync(StructuredOutputMaxBytes).ConfigureAwait(false);
                    var structuredContent = new JsonObject
                    {
                        ["output"] = fullOutput.Content,
                        ["truncated"] = fullOutput.Truncated,
                        ["exit_code"] = exitCode.Value,
                        ["wall_time_seconds"] = wallTimeSeconds,
                    };
                    if (fullOutput.Truncated && finalSnapshot.FullOutputPath is not null)
                    {
                        structuredContent["full_output_path"] = finalSnapshot.FullOutputPath;
                    }

                    if (exitCode.Value != 0)
                    {
                        return new AgentToolResult(
                            [new TextContent(AppendStatus(outputText, $"Command exited with code {exitCode.Value}"))],
                            details,
                            structuredContent,
                            IsError: true);
                    }

                    return new AgentToolResult([new TextContent(outputText)], details, structuredContent);
                }
                finally
                {
                    ClearUpdateTimer();
                }
            },
        };
    }

    private static readonly ShellToolConfig BashToolConfig = new()
    {
        Name = "bash",
        Label = "bash",
        ShellName = "bash",
        Prompt = "$",
        PromptSnippet = BashSnippet,
        PromptGuidelines = BashGuidelines,
        TempFilePrefix = "pi-bash",
    };

    /// <summary>The TS <c>createBashToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateBashToolDefinition(string cwd, BashToolOptions? options = null) =>
        CreateShellToolDefinition(cwd, BashToolConfig, options);

    /// <summary>The TS <c>createBashTool(cwd, options)</c>.</summary>
    public static AgentTool CreateBashTool(string cwd, BashToolOptions? options = null)
    {
        var definition = CreateBashToolDefinition(cwd, options);
        return ToolDefinitionWrapper.WrapToolDefinition(definition) with
        {
            PromptSnippet = definition.PromptSnippet,
            PromptGuidelines = definition.PromptGuidelines,
        };
    }
}
