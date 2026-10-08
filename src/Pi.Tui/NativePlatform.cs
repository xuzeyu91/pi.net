using System.Runtime.InteropServices;

namespace Pi.Tui;

/// <summary>Modifier keys queried from the native platform helper.</summary>
public enum ModifierKey
{
    Shift,
    Command,
    Control,
    Option,
}

/// <summary>Native clipboard access.</summary>
public interface INativeClipboard
{
    /// <summary>Undefined (<c>null</c>) means unavailable, an empty <see cref="NativeClipboardText"/> means no text; transfer failures throw.</summary>
    Task<NativeClipboardText?> GetTextAsync();

    /// <summary>Undefined (<c>null</c>) means unavailable, an empty array means no image; transfer failures throw.</summary>
    Task<byte[]?> GetImageAsync();

    /// <summary>Paths of file URLs on the clipboard. Undefined (<c>null</c>) means unsupported, an empty array means no files.</summary>
    Task<string[]?> GetFilePathsAsync();

    /// <summary>Linux uses command-line tools to retain clipboard ownership instead.</summary>
    Task SetTextAsync(string text);
}

/// <summary>Clipboard text, distinguishing "no text" from "unavailable".</summary>
public readonly record struct NativeClipboardText(string Value);

/// <summary>A native platform helper: clipboard plus optional extras.</summary>
public interface INativePlatformHelper : INativeClipboard
{
    bool EnableVirtualTerminalInput();

    bool IsModifierPressed(ModifierKey key);
}

/// <summary>
/// Port of <c>native-platform.ts</c> / <c>native-modifiers.ts</c> / <c>native-module-path.ts</c>.
///
/// <para>
/// Deviation from TS: the TS modules <c>createRequire()</c> a compiled Node addon
/// (<c>native/&lt;platform&gt;/prebuilds/&lt;platform&gt;-&lt;arch&gt;/&lt;platform&gt;-platform[-x11].node</c>), probing a list of
/// packaging locations and caching the result per path. .NET has no Node addon ABI, so there is no
/// file to probe. The helper is instead supplied explicitly by the host (via
/// <see cref="SetNativePlatformHelper"/>) — for example a P/Invoke wrapper or a managed clipboard
/// implementation. With no helper installed, every query reports "unavailable", which is exactly the
/// TS behaviour when the addon is missing or the platform is unsupported.
/// </para>
///
/// <para>
/// The module-candidate probing in <c>native-module-path.ts</c> has no .NET equivalent and is not
/// ported; <see cref="GetNativeModuleCandidates"/> is kept only to document the search order.
/// </para>
/// </summary>
public static class NativePlatform
{
    private static INativePlatformHelper? _helper;

    /// <summary>Install (or clear) the native platform helper. Host-specific; see the type remarks.</summary>
    public static void SetNativePlatformHelper(INativePlatformHelper? helper) => _helper = helper;

    /// <summary>The native platform helper, or <c>null</c> when the current platform has none.</summary>
    public static INativePlatformHelper? GetNativePlatformHelper()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }
        return _helper;
    }

    /// <summary>Load a clipboard helper without opening the display until a read is requested.</summary>
    public static INativeClipboard? GetNativeClipboard()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetNativePlatformHelper();
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }
        return _helper;
    }

    /// <summary>Whether a native modifier key is currently held; <c>false</c> when no helper is installed.</summary>
    public static bool IsNativeModifierPressed(ModifierKey key)
    {
        var helper = GetNativePlatformHelper();
        if (helper is null)
        {
            return false;
        }
        try
        {
            return helper.IsModifierPressed(key);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The packaging locations the TS loader probes, in order. Not used by this port (see the type
    /// remarks); kept for documentation and diagnostics.
    /// </summary>
    public static IReadOnlyList<string> GetNativeModuleCandidates(string nativePath, string? moduleDirectory = null, string? execPath = null)
    {
        var moduleDir = moduleDirectory ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(moduleDir, "..", nativePath)),
            Path.GetFullPath(Path.Combine(moduleDir, nativePath)),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(execPath ?? Environment.ProcessPath ?? AppContext.BaseDirectory) ?? moduleDir, nativePath)),
        };
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }
}
