// ============================================================================
// Exec — port of core/exec.ts (4d-2b)
// ============================================================================
//
// Shared command execution for extensions and custom tools (`pi.exec`). The TS version spawns
// with `shell: false`, accumulates stdout/stderr, and kills on timeout or abort (SIGTERM, then
// SIGKILL after 5s). The port maps that onto `ChildProcess` (utils/child-process.ts, already
// ported in 4a): the same spawn flags, the same kill ladder, and `WaitForChildProcessAsync`
// for the "do not hang on inherited stdio handles" wait.

using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Options for <see cref="Exec.ExecCommand"/>. Port of the TS <c>ExecOptions</c>.</summary>
public sealed record ExecOptions
{
    /// <summary>Cancels the command; an already-cancelled token kills it immediately.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Timeout in milliseconds. Values &lt;= 0 mean no timeout.</summary>
    public int? Timeout { get; init; }

    /// <summary>Working directory; defaults to the current directory.</summary>
    public string? Cwd { get; init; }
}

/// <summary>Result of <see cref="Exec.ExecCommand"/>. Port of the TS <c>ExecResult</c>.</summary>
public sealed record ExecResult(string Stdout, string Stderr, int Code, bool Killed);

/// <summary>Port of <c>core/exec.ts</c>.</summary>
public static class Exec
{
    /// <summary>Grace period after SIGTERM before SIGKILL. TS: the 5s force-kill timer.</summary>
    private const int KillGraceMs = 5000;

    /// <summary>Execute a command and return its captured output, exit code and killed flag.</summary>
    public static async Task<ExecResult> ExecCommand(
        string command,
        IReadOnlyList<string> args,
        string cwd,
        ExecOptions? options = null)
    {
        var process = ChildProcess.SpawnProcess(
            command,
            args,
            new SpawnOptions
            {
                Cwd = cwd,
                Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
            });

        var killed = false;
        void KillProcess()
        {
            if (killed) return;
            killed = true;
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // The process may have exited between the check and the kill; TS ignores ESRCH too.
            }

            // Force kill after the grace period if SIGTERM did not work.
            _ = Task.Delay(KillGraceMs).ContinueWith(
                _ =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                        // Best effort, like the TS timer.
                    }
                },
                TaskScheduler.Default);
        }

        using var timeoutCts = options?.Timeout is > 0
            ? new CancellationTokenSource(options.Timeout.Value)
            : null;
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(options!.Signal, timeoutCts.Token);
        var cancellationToken = linkedCts?.Token ?? options?.Signal ?? CancellationToken.None;

        await using var registration = cancellationToken.Register(KillProcess);

        var stdoutTask = process.ReadStdoutAsync();
        var stderrTask = process.ReadStderrAsync();

        int code;
        try
        {
            code = await ChildProcess.WaitForChildProcessAsync(process) ?? 0;
        }
        catch (Exception)
        {
            // TS resolves with code 1 when the wait fails (e.g. the spawn itself failed).
            code = 1;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ExecResult(stdout, stderr, code, killed);
    }
}
