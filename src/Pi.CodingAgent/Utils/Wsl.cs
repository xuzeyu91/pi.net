using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/wsl.ts</c>.</summary>
public static partial class Wsl
{
    /// <summary>
    /// Windows Subsystem for Linux, where Windows executables are reachable through interop.
    /// </summary>
    /// <param name="env">
    /// Environment lookup to use; defaults to the process environment. Injectable so tests do not have
    /// to mutate process-wide state.
    /// </param>
    public static bool IsWSL(Func<string, string?>? env = null)
    {
        var lookup = env ?? Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(lookup("WSL_DISTRO_NAME")) || !string.IsNullOrEmpty(lookup("WSLENV")))
        {
            return true;
        }

        try
        {
            var release = File.ReadAllText("/proc/version");
            return WslMarkerRegex().IsMatch(release);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex("microsoft|wsl", RegexOptions.IgnoreCase)]
    private static partial Regex WslMarkerRegex();
}
