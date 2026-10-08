using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>How a command is handed to the shell (port of <c>ShellConfig</c>).</summary>
/// <param name="Shell">The shell executable.</param>
/// <param name="Args">The arguments that precede (or replace) the command.</param>
/// <param name="CommandTransport">
/// <c>"stdin"</c> when the command must be written to standard input instead of passed as an argument;
/// otherwise <see langword="null"/> (<c>"argv"</c>).
/// </param>
public sealed record ShellConfig(string Shell, IReadOnlyList<string> Args, string? CommandTransport = null);

/// <summary>Port of <c>utils/shell.ts</c>: shell discovery, a sanitised environment, and process-tree kills.</summary>
public static partial class Shell
{
    /// <summary>PowerShell arguments used by the <c>powershell</c> tool.</summary>
    public static readonly IReadOnlyList<string> PowerShellArgs = Config.PowerShellArgs;

    // /^[a-z]:\windows\(system32|sysnative)\bash\.exe$/ — anchored with \z because JS `$` without the `m`
    // flag matches only at end of input.
    [GeneratedRegex(@"^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe\z")]
    private static partial Regex LegacyWslBashRegex();

    // sanitizeBinaryOutput: control characters (except tab, LF, CR) plus the interlinear annotation
    // characters U+FFF9..U+FFFB, which crash string-width. All are single UTF-16 code units, so a
    // surrogate pair is never split.
    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\uFFF9-\uFFFB]")]
    private static partial Regex UnsafeBinaryOutputRegex();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineBreakRegex();

    private static readonly HashSet<int> TrackedDetachedChildPids = [];
    private static readonly Lock TrackedPidsLock = new();

    /// <summary>
    /// A legacy WSL bash shim at <c>%SystemRoot%\System32\bash.exe</c> or <c>Sysnative\bash.exe</c>, which
    /// needs the command on stdin rather than as <c>-c</c>.
    /// </summary>
    internal static bool IsLegacyWslBashPath(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        return LegacyWslBashRegex().IsMatch(normalized);
    }

    private static ShellConfig GetBashShellConfig(string shell) =>
        IsLegacyWslBashPath(shell)
            ? new ShellConfig(shell, ["-s"], "stdin")
            : new ShellConfig(shell, ["-c"]);

    /// <summary>
    /// Find an executable on <c>PATH</c>. Windows uses <c>where</c> and verifies the file exists, because
    /// <c>where</c> can return paths that do not; Unix trusts <c>which</c>, which handles Termux and
    /// special filesystems.
    /// </summary>
    internal static string? FindExecutableOnPath(string executable)
    {
        if (NodePath.IsWindows)
        {
            var result = ChildProcess.SpawnSync("where", [executable], new SpawnSyncOptions
            {
                Encoding = "utf-8",
                TimeoutMs = 5000,
                WindowsHide = true,
            });
            if (result.Status == 0 && result.Stdout.Length > 0)
            {
                var firstMatch = FirstLine(result.Stdout);
                if (firstMatch.Length > 0 && File.Exists(firstMatch))
                {
                    return firstMatch;
                }
            }

            return null;
        }

        var unixResult = ChildProcess.SpawnSync("which", [executable], new SpawnSyncOptions
        {
            Encoding = "utf-8",
            TimeoutMs = 5000,
        });
        if (unixResult.Status == 0 && unixResult.Stdout.Length > 0)
        {
            var firstMatch = FirstLine(unixResult.Stdout);
            if (firstMatch.Length > 0)
            {
                return firstMatch;
            }
        }

        return null;
    }

    private static string FirstLine(string value)
    {
        var trimmed = value.Trim();
        var match = LineBreakRegex().Match(trimmed);
        return match.Success ? trimmed[..match.Index] : trimmed;
    }

    /// <summary>
    /// Resolve the shell configuration.
    /// </summary>
    /// <remarks>
    /// Resolution order: an explicit <paramref name="customShellPath"/>; on Windows Git Bash in its two
    /// standard install locations and then <c>bash.exe</c> on <c>PATH</c>; on Unix <c>/bin/bash</c>, then
    /// <c>bash</c> on <c>PATH</c>, then <c>sh</c>.
    /// </remarks>
    public static ShellConfig GetShellConfig(string? customShellPath = null)
    {
        if (!string.IsNullOrEmpty(customShellPath))
        {
            if (File.Exists(customShellPath))
            {
                return GetBashShellConfig(customShellPath);
            }

            throw new InvalidOperationException($"Custom shell path not found: {customShellPath}");
        }

        if (NodePath.IsWindows)
        {
            var paths = new List<string>();
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                paths.Add(programFiles + "\\Git\\bin\\bash.exe");
            }

            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                paths.Add(programFilesX86 + "\\Git\\bin\\bash.exe");
            }

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    return GetBashShellConfig(path);
                }
            }

            // Fallback: search bash.exe on PATH (Cygwin, MSYS2, WSL, …).
            var bashOnPath = FindExecutableOnPath("bash.exe");
            if (bashOnPath is not null)
            {
                return GetBashShellConfig(bashOnPath);
            }

            throw new InvalidOperationException(
                "No bash shell found. Options:\n" +
                "  1. Install Git for Windows: https://git-scm.com/download/win\n" +
                "  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
                "  3. Set shellPath in settings.json\n\n" +
                "Searched Git Bash in:\n" +
                string.Join('\n', paths.Select(p => "  " + p)));
        }

        if (File.Exists("/bin/bash"))
        {
            return GetBashShellConfig("/bin/bash");
        }

        var unixBashOnPath = FindExecutableOnPath("bash");
        return unixBashOnPath is not null
            ? GetBashShellConfig(unixBashOnPath)
            : new ShellConfig("sh", ["-c"]);
    }

    /// <summary>Resolve PowerShell on Windows, preferring PowerShell 7 when available.</summary>
    public static ShellConfig GetPowerShellConfig()
    {
        if (!NodePath.IsWindows)
        {
            throw new InvalidOperationException("The powershell tool is only available on Windows.");
        }

        var shell = FindExecutableOnPath("pwsh.exe") ?? FindExecutableOnPath("powershell.exe");
        if (shell is null)
        {
            throw new InvalidOperationException(
                "No PowerShell executable found. Install PowerShell or add powershell.exe/pwsh.exe to PATH.");
        }

        return new ShellConfig(shell, PowerShellArgs);
    }

    /// <summary>The process environment with the managed bin directory prepended to <c>PATH</c>.</summary>
    public static Dictionary<string, string?> GetShellEnv()
    {
        var binDir = Config.GetBinDir();
        var variables = Environment.GetEnvironmentVariables();
        var pathKey = variables.Keys
            .OfType<string>()
            .FirstOrDefault(key => string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            ?? "PATH";

        var currentPath = variables[pathKey] as string ?? string.Empty;
        var pathEntries = currentPath.Split(NodePath.Delimiter, StringSplitOptions.RemoveEmptyEntries);
        var updatedPath = pathEntries.Contains(binDir, StringComparer.Ordinal)
            ? currentPath
            : string.Join(NodePath.Delimiter, new[] { binDir, currentPath }.Where(entry => entry.Length > 0));

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in variables.Keys)
        {
            result[key] = variables[key] as string;
        }

        result[pathKey] = updatedPath;
        return result;
    }

    /// <summary>
    /// Sanitise binary output for display or storage, removing characters that crash string-width or
    /// cause display problems.
    /// </summary>
    public static string SanitizeBinaryOutput(string str) => UnsafeBinaryOutputRegex().Replace(str, string.Empty);

    /// <summary>
    /// Track a detached child so it can be killed on parent shutdown (SIGHUP/SIGTERM).
    /// </summary>
    public static void TrackDetachedChildPid(int pid)
    {
        lock (TrackedPidsLock)
        {
            TrackedDetachedChildPids.Add(pid);
        }
    }

    /// <summary>Stop tracking a detached child.</summary>
    public static void UntrackDetachedChildPid(int pid)
    {
        lock (TrackedPidsLock)
        {
            TrackedDetachedChildPids.Remove(pid);
        }
    }

    /// <summary>Kill every tracked detached child and clear the set.</summary>
    public static void KillTrackedDetachedChildren()
    {
        int[] pids;
        lock (TrackedPidsLock)
        {
            pids = [.. TrackedDetachedChildPids];
            TrackedDetachedChildPids.Clear();
        }

        foreach (var pid in pids)
        {
            KillProcessTree(pid);
        }
    }

    /// <summary>Kill a process and all its children.</summary>
    /// <remarks>
    /// On Windows this shells out to the trusted <c>System32\taskkill.exe</c> so cleanup does not depend
    /// on <c>PATH</c>. On Unix it sends SIGKILL to the process group, falling back to the process itself.
    /// The group kill only reaches descendants when the child was started detached (see
    /// <see cref="SpawnOptions.Detached"/>).
    /// </remarks>
    public static void KillProcessTree(int pid)
    {
        if (NodePath.IsWindows)
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
            var taskkill = NodePath.Join(systemRoot, "System32", "taskkill.exe");
            try
            {
                using var child = ChildProcess.SpawnProcess(
                    taskkill,
                    ["/F", "/T", "/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    new SpawnOptions { Stdio = [StdioMode.Ignore], Detached = true, WindowsHide = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Ignore a failed taskkill.
            }

            return;
        }

        if (!ChildProcessHandle.TrySendSigkill(-pid))
        {
            ChildProcessHandle.TrySendSigkill(pid);
        }
    }
}
