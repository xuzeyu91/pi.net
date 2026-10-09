using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>How a child's standard stream is connected (Node's <c>stdio</c> entries).</summary>
public enum StdioMode
{
    /// <summary>Redirect into a pipe the parent reads.</summary>
    Pipe,

    /// <summary>Discard.</summary>
    Ignore,

    /// <summary>Share the parent's stream.</summary>
    Inherit,
}

/// <summary>Options for <see cref="ChildProcess.SpawnProcess"/> (Node's <c>SpawnOptions</c> subset).</summary>
public sealed record SpawnOptions
{
    /// <summary>Working directory; defaults to the current directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Stdin, stdout, stderr in that order. A single entry applies to all three.</summary>
    public IReadOnlyList<StdioMode>? Stdio { get; init; }

    /// <summary>Put the child in its own process group so the whole tree can be killed.</summary>
    public bool Detached { get; init; }

    /// <summary>Do not create a console window on Windows.</summary>
    public bool WindowsHide { get; init; }

    /// <summary>Environment overlay; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Env { get; init; }
}

/// <summary>Options for <see cref="ChildProcess.SpawnSync"/>.</summary>
public sealed record SpawnSyncOptions
{
    /// <summary>Working directory; defaults to the current directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Stdin, stdout, stderr in that order. A single entry applies to all three.</summary>
    public IReadOnlyList<StdioMode>? Stdio { get; init; }

    /// <summary>Do not create a console window on Windows.</summary>
    public bool WindowsHide { get; init; }

    /// <summary>Kill the child and report a timeout error after this many milliseconds.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Only <c>"utf-8"</c> changes anything: it forces UTF-8 decoding of the captured output.</summary>
    public string? Encoding { get; init; }

    /// <summary>Environment overlay; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Env { get; init; }

    /// <summary>
    /// Written to the child's standard input, which is then closed (Node's <c>input</c> option). Setting
    /// it forces stdin into <see cref="StdioMode.Pipe"/> regardless of <see cref="Stdio"/>.
    /// </summary>
    public string? Input { get; init; }
}

/// <summary>The result of <see cref="ChildProcess.SpawnSync"/> (Node's <c>SpawnSyncReturns</c>).</summary>
/// <param name="Status">Exit code, or <see langword="null"/> when the child was killed or failed to start.</param>
/// <param name="Stdout">Decoded standard output.</param>
/// <param name="Stderr">Decoded standard error.</param>
/// <param name="Error">The startup or timeout error, if any.</param>
public sealed record SpawnSyncReturns(int? Status, string Stdout, string Stderr, Exception? Error);

/// <summary>
/// Port of <c>utils/child-process.ts</c>: spawning with Windows command-shim resolution, and waiting for
/// a child without hanging on stdio handles inherited by detached descendants.
/// </summary>
/// <remarks>
/// <para>
/// Deviation from TS: the TS module returns Node's <c>ChildProcess</c> with event-emitting
/// <c>Readable</c> streams, and several callers attach their own <c>data</c> listeners. A .NET
/// <see cref="Stream"/> can only be consumed once, so <see cref="ChildProcessHandle"/> pumps the pipes
/// itself and re-broadcasts them through <see cref="ChildProcessHandle.StdoutData"/> and
/// <see cref="ChildProcessHandle.StderrData"/>; <see cref="ChildProcessHandle.ReadStdoutAsync"/> and
/// <see cref="ChildProcessHandle.ReadStderrAsync"/> return the buffered whole.
/// </para>
/// <para>
/// On Windows the TS module goes through <c>cross-spawn</c> so the shims npm, pnpm and yarn install can
/// be executed. <see cref="ChildProcess.ResolveCommand"/> reproduces that: resolve the command through
/// <c>PATH</c> and <c>PATHEXT</c>, and unless it is a <c>.com</c>/<c>.exe</c> hand the whole command line
/// to <c>cmd.exe /d /s /c</c> with cross-spawn's escaping.
/// </para>
/// <para>
/// <see cref="SpawnOptions.Detached"/> has no .NET equivalent. On Unix the child is started through
/// <c>setsid</c> when that executable exists, which is what makes the negative-PID group kill in
/// <see cref="Shell.KillProcessTree"/> safe; when <c>setsid</c> is missing the flag is ignored and only
/// the child itself is killed. On Windows the flag is ignored, matching Node's behaviour of creating a
/// new process group without a console.
/// </para>
/// </remarks>
public static class ChildProcess
{
    /// <summary>
    /// Grace period after <c>exit</c> before the stdio pipes are considered idle. The timer is re-armed
    /// on every chunk so an actively writing descendant keeps us reading.
    /// </summary>
    public const int ExitStdioGraceMs = 100;

    // cross-spawn's metaCharsRegExp. Note the ']' and the backtick: cmd.exe treats both as metacharacters.
    private static readonly Regex CmdMetaCharacters = new("([()\\][%!^\"`<>&|;, *?])", RegexOptions.Compiled);

    // cross-spawn's isExecutableRegExp: a .com/.exe runs without a shell; anything else needs cmd.exe.
    private static readonly Regex CmdExecutableExtension = new(@"\.(?:com|exe)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // cross-spawn's isCmdShimRegExp: a cmd-shim in node_modules/.bin proxies to Node, so its meta
    // characters are interpreted twice and must be escaped twice.
    private static readonly Regex CmdShimPath = new(@"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Start a child process (Node's <c>spawn</c>, through <c>cross-spawn</c> on Windows).</summary>
    public static ChildProcessHandle SpawnProcess(string command, IReadOnlyList<string> args, SpawnOptions? options = null)
        => ChildProcessHandle.Start(command, args, options ?? new SpawnOptions());

    /// <summary>Run a child process to completion, capturing its output.</summary>
    public static SpawnSyncReturns SpawnSync(string command, IReadOnlyList<string> args, SpawnSyncOptions? options = null)
    {
        options ??= new SpawnSyncOptions();
        var stdio = ExpandStdio(options.Stdio);
        if (options.Input is not null)
        {
            stdio = [StdioMode.Pipe, stdio[1], stdio[2]];
        }
        var captureStdout = stdio[1] == StdioMode.Pipe;
        var captureStderr = stdio[2] == StdioMode.Pipe;
        var stdout = string.Empty;
        var stderr = string.Empty;

        Process? process = null;
        try
        {
            var startInfo = BuildStartInfo(command, args, options.Cwd, options.Env, options.WindowsHide, stdio, detached: false);
            startInfo.RedirectStandardOutput = captureStdout;
            startInfo.RedirectStandardError = captureStderr;
            if (options.Encoding == "utf-8")
            {
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                startInfo.StandardOutputEncoding = utf8;
                startInfo.StandardErrorEncoding = utf8;
            }

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SpawnSyncReturns(null, string.Empty, string.Empty, new InvalidOperationException($"Failed to start {command}"));
            }

            var readOutput = captureStdout ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var readError = captureStderr ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

            if (options.Input is not null)
            {
                process.StandardInput.Write(options.Input);
                process.StandardInput.Close();
            }

            var timedOut = false;
            if (options.TimeoutMs is int timeout && timeout > 0)
            {
                if (!process.WaitForExit(timeout))
                {
                    timedOut = true;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The child exited between the timeout firing and the kill.
                    }

                    process.WaitForExit();
                }
            }
            else
            {
                process.WaitForExit();
            }

            stdout = readOutput.GetAwaiter().GetResult();
            stderr = readError.GetAwaiter().GetResult();

            return timedOut
                ? new SpawnSyncReturns(null, stdout, stderr, new TimeoutException("ETIMEDOUT"))
                : new SpawnSyncReturns(process.ExitCode, stdout, stderr, null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new SpawnSyncReturns(null, stdout, stderr, e);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Wait for a child to terminate without hanging on inherited stdio handles.
    /// </summary>
    /// <remarks>
    /// A short-lived child can exit while a detached descendant keeps its stdout/stderr pipe open. The
    /// TS comment records the bug this avoids (earendil-works/pi#5303): resolving and destroying the
    /// streams on a fixed deadline measured from <c>exit</c> silently loses output written past that
    /// deadline. Instead the grace timer is re-armed on every chunk, so an actively writing descendant
    /// keeps us reading while a quiet inherited handle still releases us after the grace elapses.
    /// </remarks>
    public static Task<int?> WaitForChildProcessAsync(ChildProcessHandle child) => child.WaitAsync();

    /// <summary>Build the start info, applying Windows shim resolution and Unix detachment.</summary>
    internal static ProcessStartInfo BuildStartInfo(
        string command,
        IReadOnlyList<string> args,
        string? cwd,
        IReadOnlyDictionary<string, string?>? env,
        bool windowsHide,
        IReadOnlyList<StdioMode> stdio,
        bool detached)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = windowsHide,
            RedirectStandardInput = stdio[0] == StdioMode.Pipe,
            WorkingDirectory = string.IsNullOrEmpty(cwd) ? Directory.GetCurrentDirectory() : cwd,
        };

        var (fileName, effectiveArgs, verbatim) = ResolveCommand(command, args);
        if (detached && !NodePath.IsWindows && ResolveExecutablePath("setsid") is string setsid)
        {
            // Node's `detached` puts the child in its own process group; setsid is the POSIX equivalent.
            startInfo.FileName = setsid;
            startInfo.ArgumentList.Add(fileName);
            foreach (var arg in effectiveArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }
        else
        {
            startInfo.FileName = fileName;
            if (verbatim)
            {
                // cross-spawn sets `windowsVerbatimArguments` here: the arguments were already escaped
                // for cmd.exe, so they must be passed through without .NET quoting them again.
                startInfo.Arguments = string.Join(' ', effectiveArgs);
            }
            else
            {
                foreach (var arg in effectiveArgs)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Resolve <paramref name="command"/> the way <c>cross-spawn</c>'s <c>parseNonShell</c> does on
    /// Windows. On other platforms the command is passed through unchanged, matching the TS branch that
    /// calls <c>spawn</c> directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three details are easy to get wrong and all three are load-bearing for the package-manager
    /// commands this module spawns:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The shell is needed for everything that is <em>not</em> a <c>.com</c>/<c>.exe</c>, not just for
    /// <c>.cmd</c>/<c>.bat</c>. An unresolved command also goes through the shell, because cmd.exe can
    /// still find it on its own <c>PATH</c>.
    /// </description></item>
    /// <item><description>
    /// The command itself is escaped with <c>escapeCommand</c>, which only prefixes meta characters with
    /// <c>^</c>; only the arguments are quote-wrapped and backslash-doubled by <c>escapeArgument</c>.
    /// </description></item>
    /// <item><description>
    /// A cmd-shim under <c>node_modules/.bin</c> has its meta characters escaped <em>twice</em>, because
    /// the shim passes them through to Node before cmd.exe ever sees the real invocation.
    /// </description></item>
    /// </list>
    /// <para>
    /// Not ported: cross-spawn's shebang detection (<c>readShebang</c>). It only matters when a script
    /// file is spawned directly, and every spawn site in this package starts a real executable or a
    /// package-manager shim (<c>npm</c>, <c>pnpm</c>, <c>bun</c>, <c>git</c>, <c>bash</c>,
    /// <c>taskkill.exe</c>), so nothing here can reach it.
    /// </para>
    /// </remarks>
    internal static (string FileName, IReadOnlyList<string> Args, bool Verbatim) ResolveCommand(
        string command,
        IReadOnlyList<string> args)
    {
        if (!NodePath.IsWindows)
        {
            return (command, args, false);
        }

        // cross-spawn keeps the resolved file only for the extension tests; the command line keeps the
        // original spelling so cmd.exe performs its own PATH lookup.
        var commandFile = ResolveExecutablePath(command);
        var needsShell = commandFile is null || !CmdExecutableExtension.IsMatch(commandFile);
        if (!needsShell)
        {
            return (command, args, false);
        }

        var doubleEscape = commandFile is not null && CmdShimPath.IsMatch(commandFile);
        var escaped = new List<string>(args.Count + 1) { EscapeCmdCommand(NodePath.Normalize(command)) };
        foreach (var arg in args)
        {
            escaped.Add(EscapeCmdArgument(arg, doubleEscape));
        }

        var comspec = Environment.GetEnvironmentVariable("comspec") ?? "cmd.exe";
        var shellCommand = string.Join(' ', escaped);
        return (comspec, ["/d", "/s", "/c", "\"" + shellCommand + "\""], true);
    }

    /// <summary>
    /// Locate an executable the way Node's <c>which</c> does: an explicit path is used as-is, otherwise
    /// each <c>PATH</c> entry is probed for the name itself and then for the name plus every
    /// <c>PATHEXT</c> extension.
    /// </summary>
    internal static string? ResolveExecutablePath(string command)
    {
        if (command.Length == 0)
        {
            return null;
        }

        if (command.Contains('/') || command.Contains('\\'))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in PathEntries())
        {
            var direct = Path.Join(directory, command);
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Join(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The <c>PATH</c> entries, honouring the variable spelling on each platform.</summary>
    internal static IReadOnlyList<string> PathEntries()
    {
        var path = Environment.GetEnvironmentVariable("PATH")
                   ?? Environment.GetEnvironmentVariable("Path")
                   ?? string.Empty;
        return path.Split(NodePath.Delimiter, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// cross-spawn's <c>escapeCommand</c>: prefix every cmd.exe meta character with <c>^</c>. Unlike an
    /// argument, a command is not quote-wrapped.
    /// </summary>
    internal static string EscapeCmdCommand(string command) => CmdMetaCharacters.Replace(command, "^$1");

    /// <summary>
    /// cross-spawn's <c>escapeArgument</c> for the <c>cmd.exe /c</c> command line, which is based on
    /// <c>https://qntm.org/cmd</c>.
    /// </summary>
    /// <param name="arg">The argument to escape.</param>
    /// <param name="doubleEscapeMetaChars">
    /// Escape meta characters a second time, which is required for a <c>node_modules/.bin</c> cmd-shim.
    /// </param>
    /// <remarks>
    /// The original runs two regular expressions, <c>/(?=(\\+?)?)\1"/g</c> and <c>/(?=(\\+?)?)\1$/</c>.
    /// Both put the backslash run in a <em>lazy</em> quantifier <em>inside a lookahead</em>, and V8 does
    /// not backtrack a lookahead that already succeeded to grow that capture: only the <b>final</b>
    /// backslash of a run is ever the one matched, and the backreference then has to line up with the
    /// text that follows. The upshot, measured against the real library rather than derived on paper:
    /// a run of <c>n</c> backslashes grows to <c>n + 1</c> at the end of the string, and to <c>n + 2</c>
    /// when it precedes a double quote (<c>n = 0</c> grows to <c>1</c>, the escape backslash itself).
    /// Porting the regexes literally produces <c>2n</c> / <c>2n + 1</c> and is wrong.
    /// </remarks>
    internal static string EscapeCmdArgument(string arg, bool doubleEscapeMetaChars = false)
    {
        // A backslash before a double quote is doubled and the quote gains an escape backslash.
        arg = DoubleBackslashesBeforeQuotes(arg);
        // A trailing backslash is doubled, because a closing quote is about to be appended.
        arg = DoubleTrailingBackslash(arg);
        arg = "\"" + arg + "\"";
        arg = CmdMetaCharacters.Replace(arg, "^$1");
        return doubleEscapeMetaChars ? CmdMetaCharacters.Replace(arg, "^$1") : arg;
    }

    /// <summary>
    /// Grow every backslash run that sits directly in front of a <c>"</c> by two, or by one when the run
    /// is empty (that single backslash escapes the quote).
    /// </summary>
    private static string DoubleBackslashesBeforeQuotes(string arg)
    {
        if (!arg.Contains('"'))
        {
            return arg;
        }

        var builder = new StringBuilder(arg.Length + 8);
        for (var index = 0; index < arg.Length; index++)
        {
            if (arg[index] == '"')
            {
                var backslashes = 0;
                for (var probe = index - 1; probe >= 0 && arg[probe] == '\\'; probe--)
                {
                    backslashes++;
                }

                builder.Append('\\');
                if (backslashes > 0)
                {
                    builder.Append('\\');
                }
            }

            builder.Append(arg[index]);
        }

        return builder.ToString();
    }

    /// <summary>Grow a trailing backslash run by one, which doubles the final backslash.</summary>
    private static string DoubleTrailingBackslash(string arg)
    {
        var backslashes = 0;
        for (var index = arg.Length - 1; index >= 0 && arg[index] == '\\'; index--)
        {
            backslashes++;
        }

        return backslashes == 0 ? arg : arg + "\\";
    }

    private static IReadOnlyList<StdioMode> ExpandStdio(IReadOnlyList<StdioMode>? stdio)
    {
        if (stdio is null || stdio.Count == 0)
        {
            return [StdioMode.Pipe, StdioMode.Pipe, StdioMode.Pipe];
        }

        return stdio.Count == 1 ? [stdio[0], stdio[0], stdio[0]] : stdio;
    }

    /// <summary>Normalize the optional stdio list to exactly three entries.</summary>
    internal static IReadOnlyList<StdioMode> NormalizeStdio(IReadOnlyList<StdioMode>? stdio) => ExpandStdio(stdio);
}

/// <summary>
/// A running child process with event-style stdio, mirroring the part of Node's <c>ChildProcess</c>
/// surface this codebase uses.
/// </summary>
public sealed class ChildProcessHandle : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stdoutEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _stdoutBuffer = new();
    private readonly MemoryStream _stderrBuffer = new();
    private readonly List<byte[]> _pendingStdout = [];
    private readonly List<byte[]> _pendingStderr = [];
    private readonly Lock _bufferLock = new();
    private Action<byte[]>? _stdoutListeners;
    private Action<byte[]>? _stderrListeners;
    private long _lastDataMs;
    private int _disposed;

    private ChildProcessHandle(Process process, bool hasStdoutPipe, bool hasStderrPipe)
    {
        _process = process;
        if (!hasStdoutPipe)
        {
            _stdoutEnded.TrySetResult();
        }

        if (!hasStderrPipe)
        {
            _stderrEnded.TrySetResult();
        }
    }

    /// <summary>
    /// Standard output chunks, in arrival order. Only raised when stdout is piped.
    /// </summary>
    /// <remarks>
    /// Difference C96: Node's <c>child.stdout</c> is a paused <c>Readable</c> that buffers until a
    /// <c>"data"</c> listener puts it into flowing mode, so a listener attached after <c>spawn</c>
    /// still sees everything the child wrote in the meantime. The port therefore buffers chunks until
    /// the first subscriber arrives and replays them, instead of dropping output written before the
    /// subscription.
    /// </remarks>
    public event Action<byte[]>? StdoutData
    {
        add
        {
            lock (_bufferLock)
            {
                _stdoutListeners += value;
                foreach (var chunk in _pendingStdout)
                {
                    value!(chunk);
                }

                _pendingStdout.Clear();
            }
        }

        remove
        {
            lock (_bufferLock)
            {
                _stdoutListeners -= value;
            }
        }
    }

    /// <summary>Standard error chunks, in arrival order. Only raised when stderr is piped.</summary>
    /// <remarks>Buffered the same way as <see cref="StdoutData"/> (difference C96).</remarks>
    public event Action<byte[]>? StderrData
    {
        add
        {
            lock (_bufferLock)
            {
                _stderrListeners += value;
                foreach (var chunk in _pendingStderr)
                {
                    value!(chunk);
                }

                _pendingStderr.Clear();
            }
        }

        remove
        {
            lock (_bufferLock)
            {
                _stderrListeners -= value;
            }
        }
    }

    /// <summary>The process id, or <see langword="null"/> when it could not be started.</summary>
    public int? Id { get; private init; }

    /// <summary>The exit code once the process has exited, otherwise <see langword="null"/>.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// The signal that terminated the process. .NET does not surface this, so the port always reports
    /// <see langword="null"/>; callers that branch on it must treat a null exit code as a failure.
    /// </summary>
    public string? SignalCode => null;

    /// <summary>Whether the process has exited.</summary>
    public bool HasExited => _exit.Task.IsCompleted;

    /// <summary>The standard input stream when stdin is piped, otherwise <see langword="null"/>.</summary>
    public Stream? Stdin { get; private init; }

    /// <summary>The startup failure, if the process never started.</summary>
    public Exception? StartupError { get; private init; }

    internal static ChildProcessHandle Start(string command, IReadOnlyList<string> args, SpawnOptions options)
    {
        var stdio = ChildProcess.NormalizeStdio(options.Stdio);
        var hasStdin = stdio[0] == StdioMode.Pipe;
        var hasStdout = stdio[1] == StdioMode.Pipe;
        var hasStderr = stdio[2] == StdioMode.Pipe;

        Process process;
        try
        {
            var startInfo = ChildProcess.BuildStartInfo(command, args, options.Cwd, options.Env, options.WindowsHide, stdio, options.Detached);
            startInfo.RedirectStandardOutput = hasStdout;
            startInfo.RedirectStandardError = hasStderr;
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return Failed(new InvalidOperationException($"Failed to start {command}"));
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Node's spawn reports a start failure through an asynchronous `error` event, not a throw.
            return Failed(e);
        }

        var handle = new ChildProcessHandle(process, hasStdout, hasStderr)
        {
            Id = process.Id,
            Stdin = hasStdin ? process.StandardInput.BaseStream : null,
        };

        Interlocked.Exchange(ref handle._lastDataMs, NowMs());
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            Interlocked.Exchange(ref handle._lastDataMs, NowMs());
            handle.ExitCode = TryReadExitCode(process);
            handle._exit.TrySetResult(handle.ExitCode);
        };

        if (hasStdout)
        {
            _ = handle.PumpAsync(process.StandardOutput.BaseStream, handle._stdoutBuffer, isStdout: true, handle._stdoutEnded);
        }

        if (hasStderr)
        {
            _ = handle.PumpAsync(process.StandardError.BaseStream, handle._stderrBuffer, isStdout: false, handle._stderrEnded);
        }

        if (process.HasExited)
        {
            Interlocked.Exchange(ref handle._lastDataMs, NowMs());
            handle.ExitCode = TryReadExitCode(process);
            handle._exit.TrySetResult(handle.ExitCode);
        }

        return handle;
    }

    private static ChildProcessHandle Failed(Exception error) =>
        new(new Process(), hasStdoutPipe: false, hasStderrPipe: false) { StartupError = error };

    /// <summary>Read the whole of standard output once the process has finished.</summary>
    public async Task<string> ReadStdoutAsync()
    {
        await _stdoutEnded.Task.ConfigureAwait(false);
        lock (_bufferLock)
        {
            return Encoding.UTF8.GetString(_stdoutBuffer.ToArray());
        }
    }

    /// <summary>Read the whole of standard error once the process has finished.</summary>
    public async Task<string> ReadStderrAsync()
    {
        await _stderrEnded.Task.ConfigureAwait(false);
        lock (_bufferLock)
        {
            return Encoding.UTF8.GetString(_stderrBuffer.ToArray());
        }
    }

    /// <summary>Send a termination request to the process itself.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The process already exited.
        }
    }

    /// <summary>Kill the process and everything it started.</summary>
    public void KillTree()
    {
        if (Id is int pid)
        {
            Shell.KillProcessTree(pid);
            return;
        }

        Kill();
    }

    /// <summary>Wait for termination using the idle-grace rule described on <see cref="ChildProcess"/>.</summary>
    public async Task<int?> WaitAsync()
    {
        if (StartupError is not null)
        {
            throw StartupError;
        }

        var exitCode = await _exit.Task.ConfigureAwait(false);
        var bothEnded = Task.WhenAll(_stdoutEnded.Task, _stderrEnded.Task);

        while (!bothEnded.IsCompleted)
        {
            var remaining = ChildProcess.ExitStdioGraceMs - (NowMs() - Interlocked.Read(ref _lastDataMs));
            if (remaining <= 0)
            {
                break;
            }

            var finished = await Task.WhenAny(bothEnded, Task.Delay((int)remaining)).ConfigureAwait(false);
            if (finished == bothEnded)
            {
                break;
            }
        }

        DestroyStreams();
        return exitCode;
    }

    /// <summary>Release the pipes and the child handle.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DestroyStreams();
        _process.Dispose();
        _readCancellation.Dispose();
    }

    private void DestroyStreams()
    {
        try
        {
            _readCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private async Task PumpAsync(Stream stream, MemoryStream buffer, bool isStdout, TaskCompletionSource ended)
    {
        var chunk = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, _readCancellation.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                Interlocked.Exchange(ref _lastDataMs, NowMs());
                var copy = chunk[..read];

                // Read the listener list under the lock and dispatch outside it, so a handler that
                // subscribes or unsubscribes does not deadlock against the pump. Until the first
                // subscriber arrives the chunk is parked, to be replayed on subscription
                // (difference C96).
                Action<byte[]>? listeners;
                lock (_bufferLock)
                {
                    buffer.Write(copy);
                    if (isStdout)
                    {
                        listeners = _stdoutListeners;
                        if (listeners is null)
                        {
                            _pendingStdout.Add(copy);
                        }
                    }
                    else
                    {
                        listeners = _stderrListeners;
                        if (listeners is null)
                        {
                            _pendingStderr.Add(copy);
                        }
                    }
                }

                listeners?.Invoke(copy);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The stream was torn down by the grace rule, or the child closed it abruptly.
        }
        finally
        {
            ended.TrySetResult();
        }
    }

    private static int? TryReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static long NowMs() => Environment.TickCount64;

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int PosixKill(int pid, int signal);

    /// <summary>Send SIGKILL to a process group or process through <c>libc</c>.</summary>
    internal static bool TrySendSigkill(int pid)
    {
        if (NodePath.IsWindows)
        {
            return false;
        }

        try
        {
            return PosixKill(pid, 9) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
