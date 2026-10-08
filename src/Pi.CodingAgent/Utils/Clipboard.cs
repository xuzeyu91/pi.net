using System.Text;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>A clipboard command to try (port of the <c>[command, args]</c> tuples in <c>clipboard.ts</c>).</summary>
/// <param name="Command">The executable.</param>
/// <param name="Args">Its arguments.</param>
public sealed record ClipboardCommandSpec(string Command, IReadOnlyList<string> Args);

/// <summary>Port of <c>utils/clipboard.ts</c>.</summary>
/// <remarks>
/// <para>
/// The command planning is exposed as pure helpers (<see cref="ReadCommands"/>, <see cref="CopyCommands"/>,
/// <see cref="IsRemoteSession"/>, <see cref="IsHeadlessLinux"/>, <see cref="CopyFailureMessage"/>) because
/// the TS versions are inline expressions inside the async functions, and the environment they branch on
/// is otherwise unreachable from a test.
/// </para>
/// <para>
/// Deviation from TS: the native clipboard interface in the port always has a setter, so the
/// <c>clipboard?.setText</c> capability probe becomes "a native clipboard exists".
/// </para>
/// </remarks>
public static class Clipboard
{
    /// <summary>OSC 52 payloads longer than this are refused rather than emitted.</summary>
    public const int MaxOsc52EncodedLength = 100_000;

    private static Func<string, string?>? _envOverride;
    private static TextWriter? _osc52WriterOverride;
    private static Func<string>? _platformOverride;
    private static Func<string, IReadOnlyList<string>, ClipboardCommandOptions, Task<byte[]?>>? _commandRunnerOverride;
    private static Func<INativeClipboard?>? _nativeClipboardOverride;

    /// <summary>
    /// Replace the environment lookup. The TS reads <c>process.env</c> at call time; a static initializer
    /// cannot be re-run, so the port exposes an override instead.
    /// </summary>
    internal static Func<string, string?>? EnvOverride
    {
        get => _envOverride;
        set => _envOverride = value;
    }

    /// <summary>Replace the terminal OSC 52 writer, which normally writes to standard output.</summary>
    internal static TextWriter? Osc52WriterOverride
    {
        get => _osc52WriterOverride;
        set => _osc52WriterOverride = value;
    }

    /// <summary>
    /// Replace the reported platform. The TS branches on <c>os.platform()</c>, so the Linux/darwin
    /// matrices are otherwise only reachable from a host of that platform.
    /// </summary>
    internal static Func<string>? PlatformOverride
    {
        get => _platformOverride;
        set => _platformOverride = value;
    }

    /// <summary>Replace the clipboard command runner, which normally spawns a real tool.</summary>
    internal static Func<string, IReadOnlyList<string>, ClipboardCommandOptions, Task<byte[]?>>? CommandRunnerOverride
    {
        get => _commandRunnerOverride;
        set => _commandRunnerOverride = value;
    }

    /// <summary>
    /// Replace the native clipboard lookup. The TS reads a module-level singleton that its tests stub;
    /// the port exposes the same seam so the "native clipboard present" and "absent" branches can both
    /// be replayed on one host.
    /// </summary>
    internal static Func<INativeClipboard?>? NativeClipboardOverride
    {
        get => _nativeClipboardOverride;
        set => _nativeClipboardOverride = value;
    }

    private static Func<string, string?> Env => _envOverride ?? Environment.GetEnvironmentVariable;

    private static string Platform => _platformOverride?.Invoke() ?? ProcessInfo.Platform;

    private static INativeClipboard? Native() =>
        _nativeClipboardOverride is { } lookup ? lookup() : NativePlatform.GetNativeClipboard();

    private static Task<byte[]?> RunCommand(string command, IReadOnlyList<string> args, ClipboardCommandOptions options) =>
        _commandRunnerOverride is { } runner
            ? runner(command, args, options)
            : ClipboardCommand.RunAsync(command, args, options);

    /// <summary>Whether the session is remote, where only the terminal's own clipboard can be reached.</summary>
    internal static bool IsRemoteSession(Func<string, string?> env) =>
        !string.IsNullOrEmpty(env("SSH_CONNECTION")) ||
        !string.IsNullOrEmpty(env("SSH_CLIENT")) ||
        !string.IsNullOrEmpty(env("MOSH_CONNECTION"));

    /// <summary>
    /// A Linux session with no display: containers, and WSL without WSLg. The terminal is the only
    /// clipboard route there.
    /// </summary>
    internal static bool IsHeadlessLinux(string platform, Func<string, string?> env) =>
        platform == "linux" &&
        string.IsNullOrEmpty(env("DISPLAY")) &&
        string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) &&
        string.IsNullOrEmpty(env("TERMUX_VERSION"));

    /// <summary>The commands tried when reading the clipboard on Linux, in order.</summary>
    internal static IReadOnlyList<ClipboardCommandSpec> ReadCommands(string platform, Func<string, string?> env)
    {
        if (platform != "linux")
        {
            return [];
        }

        var commands = new List<ClipboardCommandSpec>();
        if (!string.IsNullOrEmpty(env("TERMUX_VERSION")))
        {
            commands.Add(new ClipboardCommandSpec("termux-clipboard-get", []));
        }

        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            commands.Add(new ClipboardCommandSpec("wl-paste", ["--no-newline", "--type", "text"]));
        }

        if (!string.IsNullOrEmpty(env("DISPLAY")))
        {
            commands.Add(new ClipboardCommandSpec("xclip", ["-selection", "clipboard", "-out"]));
            commands.Add(new ClipboardCommandSpec("xsel", ["--clipboard", "--output"]));
        }

        return commands;
    }

    /// <summary>The commands tried when writing the clipboard, in order.</summary>
    internal static IReadOnlyList<ClipboardCommandSpec> CopyCommands(string platform, Func<string, string?> env)
    {
        if (platform == "darwin")
        {
            return [new ClipboardCommandSpec("pbcopy", [])];
        }

        if (platform == "win32")
        {
            return [new ClipboardCommandSpec("clip", [])];
        }

        var commands = new List<ClipboardCommandSpec>();
        if (!string.IsNullOrEmpty(env("TERMUX_VERSION")))
        {
            commands.Add(new ClipboardCommandSpec("termux-clipboard-set", []));
        }

        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            commands.Add(new ClipboardCommandSpec("wl-copy", []));
        }

        if (!string.IsNullOrEmpty(env("DISPLAY")))
        {
            commands.Add(new ClipboardCommandSpec("xclip", ["-selection", "clipboard"]));
            commands.Add(new ClipboardCommandSpec("xsel", ["--clipboard", "--input"]));
        }

        return commands;
    }

    /// <summary>The error reported when no clipboard route worked on Linux, or <see langword="null"/>.</summary>
    internal static string? CopyFailureMessage(string platform, Func<string, string?> env)
    {
        if (platform != "linux")
        {
            return null;
        }

        if (!string.IsNullOrEmpty(env("TERMUX_VERSION")))
        {
            return "Clipboard unavailable: install the Termux:API app and `termux-api` package";
        }

        if (!string.IsNullOrEmpty(env("WAYLAND_DISPLAY")))
        {
            return "Clipboard unavailable: install `wl-clipboard` (`wl-copy`) or check Wayland access";
        }

        if (!string.IsNullOrEmpty(env("DISPLAY")))
        {
            return "Clipboard unavailable: install `xclip` or `xsel`, or check X11 access";
        }

        return null;
    }

    /// <summary>Write the text to the terminal's clipboard with OSC 52. Returns false when too long.</summary>
    internal static bool EmitOsc52(string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (encoded.Length > MaxOsc52EncodedLength)
        {
            return false;
        }

        var writer = _osc52WriterOverride ?? Console.Out;
        writer.Write("\u001b]52;c;" + encoded + "\u0007");
        return true;
    }

    /// <summary>
    /// WSL without WSLg has no Linux display, so the Windows clipboard is written through interop.
    /// PowerShell reads the text from a file because <c>clip.exe</c> and PowerShell stdin decode piped
    /// bytes with the console code page, which mangles non-ASCII UTF-8.
    /// </summary>
    private static async Task<bool> CopyViaWindowsClipboardAsync(string text)
    {
        var tmpFile = Path.Join(Path.GetTempPath(), "pi-wsl-clip-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(tmpFile, text, new UTF8Encoding(false)).ConfigureAwait(false);
            var winPathBytes = await RunCommand("wslpath", ["-w", tmpFile], new ClipboardCommandOptions { TimeoutMs = 1000 })
                .ConfigureAwait(false);
            var winPath = winPathBytes is null ? null : Encoding.UTF8.GetString(winPathBytes).Trim();
            if (string.IsNullOrEmpty(winPath))
            {
                return false;
            }

            var script =
                "Set-Clipboard -Value ([System.IO.File]::ReadAllText('" +
                winPath.Replace("'", "''", StringComparison.Ordinal) +
                "', [System.Text.Encoding]::UTF8))";
            var result = await RunCommand("powershell.exe", ["-NoProfile", "-Command", script], new ClipboardCommandOptions { TimeoutMs = 5000 })
                .ConfigureAwait(false);
            return result is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(tmpFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file may not have been created.
            }
        }
    }

    /// <summary>Read plain text from the system clipboard.</summary>
    public static async Task<string?> ReadClipboardTextAsync()
    {
        var env = Env;
        var platform = Platform;

        foreach (var spec in ReadCommands(platform, env))
        {
            var bytes = await RunCommand(spec.Command, spec.Args, new ClipboardCommandOptions { TimeoutMs = 5000 })
                .ConfigureAwait(false);
            if (bytes is not null)
            {
                var text = Encoding.UTF8.GetString(bytes);
                return text.Length == 0 ? null : text;
            }
        }

        try
        {
            var clipboard = Native();
            if (clipboard is null)
            {
                return null;
            }

            var value = await clipboard.GetTextAsync().ConfigureAwait(false);
            if (value is not { } text)
            {
                return null;
            }

            return text.Value.Length == 0 ? null : text.Value;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Read file paths, such as Finder file copies, from the native clipboard.</summary>
    public static async Task<string[]?> ReadClipboardFilePathsAsync()
    {
        var clipboard = Native();
        if (clipboard is null)
        {
            return null;
        }

        var paths = await clipboard.GetFilePathsAsync().ConfigureAwait(false);
        return paths is { Length: > 0 } ? paths : null;
    }

    /// <summary>Write text to the system clipboard, falling back through the platform commands and OSC 52.</summary>
    public static async Task CopyToClipboardAsync(string text)
    {
        var env = Env;
        var platform = Platform;
        var copied = false;

        // Direct writes precede OSC 52 so the terminal cannot race the native writer. Linux tools retain
        // clipboard selection ownership after this call returns.
        if (platform != "linux")
        {
            try
            {
                var clipboard = Native();
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text).ConfigureAwait(false);
                    copied = true;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException)
            {
                // Try the platform commands next.
            }
        }

        if (!copied)
        {
            foreach (var spec in CopyCommands(platform, env))
            {
                var result = await RunCommand(spec.Command, spec.Args, new ClipboardCommandOptions { Input = text, TimeoutMs = 5000 })
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    copied = true;
                    break;
                }
            }
        }

        var osc52Emitted = false;
        if (!copied && platform == "linux" && Wsl.IsWSL(env))
        {
            // Windows Terminal supports OSC 52; prefer it over the slower PowerShell round trip.
            if (!string.IsNullOrEmpty(env("WT_SESSION")))
            {
                osc52Emitted = EmitOsc52(text);
            }

            copied = osc52Emitted || await CopyViaWindowsClipboardAsync(text).ConfigureAwait(false);
        }

        // OSC 52 cannot be verified, so a desktop session with a display reports the failure instead
        // (#9618). Without a display the terminal is the only clipboard route (containers, WSL without
        // WSLg), and remote sessions always emit it to reach the client clipboard.
        var oversized = false;
        if (!osc52Emitted && (IsRemoteSession(env) || (!copied && IsHeadlessLinux(platform, env))))
        {
            if (EmitOsc52(text))
            {
                copied = true;
            }
            else
            {
                oversized = true;
            }
        }

        if (copied)
        {
            return;
        }

        if (oversized)
        {
            throw new InvalidOperationException("Clipboard unavailable: text exceeds the OSC 52 size limit");
        }

        throw new InvalidOperationException(CopyFailureMessage(platform, env) ?? "Clipboard unavailable");
    }
}
