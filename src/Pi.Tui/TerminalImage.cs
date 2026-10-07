namespace Pi.Tui;

/// <summary>Inline image protocol supported by a terminal.</summary>
public enum ImageProtocol
{
    None,
    Kitty,
    Iterm2,
}

/// <summary>Detected terminal capabilities.</summary>
public sealed record TerminalCapabilities(ImageProtocol Images, bool TrueColor, bool Hyperlinks);

/// <summary>Terminal cell size in pixels.</summary>
public readonly record struct CellDimensions(int WidthPx, int HeightPx);

/// <summary>
/// Port of <c>terminal-image.ts</c>'s capability detection, image-line detection and Kitty delete
/// sequences. The full Kitty/iTerm2 encoders and image dimension parsers are not yet ported (see
/// <c>docs/migration-plan.md</c>); they are only needed by the <c>Image</c> component.
/// </summary>
public static class TerminalImage
{
    private const string KittyPrefix = "\x1b_G";
    private const string Iterm2Prefix = "\x1b]1337;File=";

    private static TerminalCapabilities? _cachedCapabilities;
    private static CellDimensions _cellDimensions = new(9, 18);

    public static CellDimensions GetCellDimensions() => _cellDimensions;

    public static void SetCellDimensions(CellDimensions dims) => _cellDimensions = dims;

    public static TerminalCapabilities GetCapabilities() => _cachedCapabilities ??= DetectCapabilities();

    public static void ResetCapabilitiesCache() => _cachedCapabilities = null;

    /// <summary>Override the cached capabilities (useful in tests).</summary>
    public static void SetCapabilities(TerminalCapabilities caps) => _cachedCapabilities = caps;

    public static TerminalCapabilities DetectCapabilities(Func<bool>? tmuxForwardsHyperlink = null)
    {
        tmuxForwardsHyperlink ??= ProbeTmuxHyperlinks;

        var termProgram = (Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "").ToLowerInvariant();
        var terminalEmulator = (Environment.GetEnvironmentVariable("TERMINAL_EMULATOR") ?? "").ToLowerInvariant();
        var term = (Environment.GetEnvironmentVariable("TERM") ?? "").ToLowerInvariant();
        var colorTerm = (Environment.GetEnvironmentVariable("COLORTERM") ?? "").ToLowerInvariant();
        var hasTrueColorHint = colorTerm is "truecolor" or "24bit";
        var isWindowsConsole = OperatingSystem.IsWindows();

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX")) || term.StartsWith("tmux"))
        {
            return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, tmuxForwardsHyperlink());
        }

        if (term.StartsWith("screen"))
        {
            return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, false);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")) || termProgram == "kitty")
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (termProgram == "ghostty" || term.Contains("ghostty") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE")) || termProgram == "wezterm")
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (termProgram == "warpterminal"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WARP_SESSION_ID"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WARP_TERMINAL_SESSION_UUID")))
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ITERM_SESSION_ID")) || termProgram == "iterm.app")
        {
            return new TerminalCapabilities(ImageProtocol.Iterm2, true, true);
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            return new TerminalCapabilities(ImageProtocol.None, true, true);
        }

        if (termProgram == "vscode")
        {
            return new TerminalCapabilities(ImageProtocol.None, true, true);
        }

        if (termProgram == "alacritty")
        {
            return new TerminalCapabilities(ImageProtocol.None, true, true);
        }

        if (terminalEmulator == "jetbrains-jediterm")
        {
            return new TerminalCapabilities(ImageProtocol.None, true, false);
        }

        if (isWindowsConsole)
        {
            return new TerminalCapabilities(ImageProtocol.None, true, false);
        }

        return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, false);
    }

    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tmux", "display-message -p '#{client_termfeatures}'")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(250);
            return output.Split(',').Select(f => f.Trim()).Contains("hyperlinks");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether a rendered line carries an inline-image escape sequence.</summary>
    public static bool IsImageLine(string line)
    {
        if (line.StartsWith(KittyPrefix, StringComparison.Ordinal) || line.StartsWith(Iterm2Prefix, StringComparison.Ordinal))
        {
            return true;
        }
        return line.Contains(KittyPrefix, StringComparison.Ordinal) || line.Contains(Iterm2Prefix, StringComparison.Ordinal);
    }

    /// <summary>Generate a random Kitty graphics image ID.</summary>
    public static int AllocateImageId() => Random.Shared.Next(1, int.MaxValue);

    public static string DeleteKittyImage(int imageId) => $"\x1b_Ga=d,d=I,i={imageId},q=2\x1b\\";

    public static string DeleteAllKittyImages() => "\x1b_Ga=d,d=A,q=2\x1b\\";

    public static string DeleteAllKittyPlacements() => "\x1b_Ga=d,d=a,q=2\x1b\\";

    /// <summary>Wrap text in an OSC 8 hyperlink.</summary>
    public static string Hyperlink(string text, string url) => $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";
}
