using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// <c>bash</c> 工具：通过环境的 shell 运行命令。对应 TS <c>tools/bash.ts</c>。
/// </summary>
/// <remarks>
/// 输出流经 <c>api.output()</c>，由 Harness 保留在默认限制内的尾部；结果内容就是保留的输出。
/// 保留窗口交给环境，环境可省略窗口外的输出并报告省略量，因此丢弃计数保持精确。超出限制的输出会溢出到临时
/// 文件，其路径作为诊断上报。非零退出或超时抛出，从而形成仍带输出与诊断的错误结果。
/// </remarks>
public static class BashTool
{
    private const double MaxTimeoutSeconds = 2_147_483_647d / 1000;

    /// <summary><c>bash</c> 的参数 schema。对应 TS <c>bashSchema</c>。</summary>
    public static ToolSchema Schema { get; } = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["command"] = ToolSchemaBuilder.String("Bash command to execute"),
            ["timeout"] = ToolSchemaBuilder.Optional(
                ToolSchemaBuilder.Number("Timeout in seconds (optional, no default timeout)")),
        },
        "command");

    /// <summary>一次 bash 执行的形态。对应 TS <c>BashExecution</c>。</summary>
    public sealed record BashExecution(string Command, string Cwd, Dictionary<string, string> Env, bool InheritEnv);

    /// <summary>执行前的定制回调。对应 TS <c>BashPrepare</c>。</summary>
    public delegate Task BashPrepare(BashExecution execution, IToolExecutionApi api, Context context);

    /// <summary><c>bash</c> 工具的选项。对应 TS <c>BashToolOptions</c>。</summary>
    public sealed record BashToolOptions
    {
        public string? CommandPrefix { get; init; }

        public BashPrepare? Prepare { get; init; }
    }

    internal static void ValidateTimeout(double? timeout)
    {
        if (timeout is not { } value) return;
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new InvalidOperationException("Invalid timeout: must be a finite number of seconds");
        if (value > MaxTimeoutSeconds)
            throw new InvalidOperationException($"Invalid timeout: maximum is {MaxTimeoutSeconds} seconds");
    }

    /// <summary>构建 <c>bash</c> 工具注册。对应 TS <c>createBashTool(options?)</c>。</summary>
    public static IToolRegistration Create(BashToolOptions? options = null) => new Registration(options);

    private sealed class Registration(BashToolOptions? options) : IToolRegistration
    {
        public string Name => "bash";

        public string Description =>
            $"Execute a bash command in the current working directory. Returns combined stdout and stderr. Output is " +
            $"truncated to last {Truncate.DefaultMaxLines} lines or {Truncate.DefaultMaxBytes / 1024}KB (whichever is " +
            "hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.";

        public ToolSchema Parameters => Schema;

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => new() { Retain = "tail" };

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var arguments = ToolArgs.Object(args);
            var command = ToolArgs.RequiredString(arguments, "command");
            var timeout = ToolArgs.OptionalNumber(arguments, "timeout");
            ValidateTimeout(timeout);

            var env = ToolsEnv.RequireEnv(api);
            var execution = new BashExecution(
                options?.CommandPrefix is { } prefix ? $"{prefix}\n{command}" : command,
                env.Cwd,
                new Dictionary<string, string>(StringComparer.Ordinal),
                true);
            if (options?.Prepare is { } prepare) await prepare(execution, api, context).ConfigureAwait(false);

            var execOptions = new ShellExecOptions
            {
                Cwd = execution.Cwd,
                Env = execution.Env,
                InheritEnv = execution.InheritEnv,
                Timeout = timeout,
                OnOutput = (text, _ctx, info) => api.Output(text, info.Skipped),
                Spill = new ShellSpillOptions
                {
                    AfterBytes = Truncate.DefaultMaxBytes,
                    AfterLines = Truncate.DefaultMaxLines,
                },
                // 环境因此可以省略保留尾部之外的输出并报告省略量。
                Window = api.OutputWindow,
            };

            var result = await env.ExecAsync(execution.Command, execOptions, context).ConfigureAwait(false);
            var spillPath = result.IsOk ? result.Value.SpillPath : result.Error.SpillPath;
            if (spillPath is not null)
                api.Diagnostic(new ToolDiagnostic
                {
                    Severity = "info",
                    Code = "full_output",
                    Message = $"Full output: {spillPath}",
                });

            if (!result.IsOk)
            {
                var error = result.Error;
                if (error.Code == ExecutionErrorCode.Aborted && context.AbortSignal is { IsCancellationRequested: true })
                    throw error;
                if (error.Code == ExecutionErrorCode.Timeout)
                    throw new InvalidOperationException($"Command timed out after {timeout} seconds");
                if (error.Code == ExecutionErrorCode.Aborted)
                    throw new InvalidOperationException("Command aborted");
                throw error;
            }

            if (result.Value.ExitCode != 0)
                throw new InvalidOperationException($"Command exited with code {result.Value.ExitCode}");

            return new ToolExecutionResult();
        }
    }
}
