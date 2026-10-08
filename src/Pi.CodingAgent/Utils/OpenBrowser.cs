using System.Diagnostics;

namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/open-browser.ts</c>.</summary>
public static class OpenBrowser
{
    /// <summary>
    /// Open a URL or file in the platform browser/default handler.
    /// </summary>
    /// <remarks>
    /// This intentionally never invokes a shell. On Windows the TS version uses
    /// <c>rundll32 url.dll,FileProtocolHandler</c> rather than <c>cmd /c start</c>, because cmd.exe
    /// re-parses metacharacters (<c>&amp;</c>, <c>|</c>, <c>^</c>, …) before <c>start</c> runs, which would
    /// make attacker-controlled URLs injectable.
    /// </remarks>
    public static void Open(string target)
    {
        var (command, args) = ResolveLauncher(target);
        try
        {
            // Browser launch is best-effort: callers still present the target to the user, so a launcher
            // failure (for example, missing xdg-open) must not become a process crash.
            var startInfo = new ProcessStartInfo(command) { UseShellExecute = false };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Ignore launcher failures.
        }
    }

    /// <summary>The launcher command and arguments for the current platform.</summary>
    internal static (string Command, string[] Args) ResolveLauncher(string target) => ProcessInfo.Platform switch
    {
        "darwin" => ("open", [target]),
        "win32" => ("rundll32", ["url.dll,FileProtocolHandler", target]),
        _ => ("xdg-open", [target]),
    };
}
