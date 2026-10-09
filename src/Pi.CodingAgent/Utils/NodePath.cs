using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of the parts of Node's <c>path</c> module the coding agent uses, for the <em>host</em> path
/// flavour.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <see cref="Path"/> class is not a substitute. It differs from Node on inputs that appear in
/// this codebase: <c>Path.GetDirectoryName("/a/b/")</c> is the empty string where Node's
/// <c>dirname</c> is <c>"/a"</c>; <c>Path.Join</c> does not collapse <c>.</c>, <c>..</c> or duplicate
/// separators the way Node's <c>join</c> (which normalizes) does; and
/// <c>Path.IsPathFullyQualified("/a")</c> is <see langword="false"/> on Windows where Node's
/// <c>win32.isAbsolute("/a")</c> is <see langword="true"/>.
/// </para>
/// <para>
/// The algorithms below are transcribed from Node's <c>lib/path.js</c>. One behaviour is worth calling
/// out because it is not documented anywhere: a <em>relative</em> Windows path whose first segment ends
/// with a colon is prefixed with <c>.\</c> by <c>normalize</c> (<c>"1:"</c> → <c>".\1:"</c>,
/// <c>"abc:"</c> → <c>".\abc:"</c>, but <c>"a/b:c"</c> → <c>"a\b:c"</c> and <c>"1:2"</c> → <c>"1:2"</c>).
/// The rule was derived by probing Node 24 and is reproduced verbatim; see
/// <c>tools/gen-coding-agent-utils-corpus.mjs</c> for the vectors.
/// </para>
/// <para>
/// <c>relative</c> delegates to <see cref="Path.GetRelativePath"/>. For the same-drive paths this
/// module receives that is equivalent to Node's <c>relative</c>; for a pair on different drives both
/// return the second path unchanged.
/// </para>
/// </remarks>
public static class NodePath
{
    /// <summary>Whether the host uses Windows path semantics (Node's <c>process.platform === "win32"</c>).</summary>
    public static bool IsWindows => string.Equals(ProcessInfo.Platform, "win32", StringComparison.Ordinal);

    /// <summary>Node's <c>path.sep</c>.</summary>
    public static char Separator => IsWindows ? '\\' : '/';

    /// <summary>Node's <c>path.delimiter</c>.</summary>
    public static char Delimiter => IsWindows ? ';' : ':';

    private static bool IsSeparator(char c, bool windows) => c == '/' || (windows && c == '\\');

    private static Func<string>? _cwdOverride;

    /// <summary>
    /// Overrides the working directory that <see cref="Resolve(string[])"/> and
    /// <see cref="Relative(string, string)"/> consult. Node reads <c>process.cwd()</c>; the differential
    /// corpus needs to replay vectors captured in a different directory, and since both functions are
    /// pure string work the injected value need not exist on disk.
    /// </summary>
    internal static Func<string>? CwdOverride
    {
        get => _cwdOverride;
        set => _cwdOverride = value;
    }

    private static string Cwd() => _cwdOverride?.Invoke() ?? Directory.GetCurrentDirectory();

    private static bool IsDeviceRoot(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    // Node's WINDOWS_RESERVED_NAMES: the DOS device names, including the superscript-digit COM/LPT
    // spellings. A path whose first segment is one of these is a device reference, not a filename.
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// Node's <c>isWindowsReservedName</c>. Note that <paramref name="colonIndex"/> may be <c>-1</c>, in
    /// which case the device part is the path minus its last character — an oddity of the original
    /// (<c>path.slice(0, -1)</c>) that is reproduced rather than corrected, because it decides whether
    /// <c>normalize("NUL")</c> takes the reserved-name branch.
    /// </summary>
    private static bool IsWindowsReservedName(string path, int colonIndex)
    {
        var end = colonIndex < 0 ? path.Length - 1 : colonIndex;
        if (end <= 0)
        {
            return false;
        }

        return WindowsReservedNames.Contains(path[..end].ToUpperInvariant());
    }

    /// <summary>Node's <c>path.normalize</c> for the host flavour.</summary>
    public static string Normalize(string path) => Normalize(path, IsWindows);

    /// <summary>Node's <c>path.normalize</c>.</summary>
    public static string Normalize(string path, bool windows)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        if (!windows)
        {
            return NormalizePosix(path);
        }

        return NormalizeWindows(path);
    }

    private static string NormalizePosix(string path)
    {
        var isAbsolute = path[0] == '/';
        var trailingSeparator = path[^1] == '/';

        var normalized = NormalizeString(path, !isAbsolute, '/', windows: false);

        if (normalized.Length == 0)
        {
            if (isAbsolute)
            {
                return "/";
            }

            return trailingSeparator ? "./" : ".";
        }

        if (trailingSeparator)
        {
            normalized += "/";
        }

        return isAbsolute ? "/" + normalized : normalized;
    }

    private static string NormalizeWindows(string path)
    {
        var length = path.Length;
        if (length == 1)
        {
            // A single separator collapses to a backslash; anything else is already normalized.
            return path[0] == '/' ? "\\" : path;
        }

        var rootEnd = 0;
        string? device = null;
        var isAbsolute = false;
        var first = path[0];

        if (IsSeparator(first, windows: true))
        {
            // Starting with a separator means the path is absolute, whether or not it is a UNC root.
            isAbsolute = true;

            if (IsSeparator(path[1], windows: true))
            {
                // Possible UNC device root: two non-empty segments after the leading separators.
                var j = 2;
                var last = j;
                while (j < length && !IsSeparator(path[j], windows: true))
                {
                    j++;
                }

                if (j < length && j != last)
                {
                    var firstPart = path[last..j];
                    last = j;
                    while (j < length && IsSeparator(path[j], windows: true))
                    {
                        j++;
                    }

                    if (j < length && j != last)
                    {
                        last = j;
                        while (j < length && !IsSeparator(path[j], windows: true))
                        {
                            j++;
                        }

                        if (j == length || j != last)
                        {
                            if (firstPart is "." or "?")
                            {
                                // A device root such as \\.\PHYSICALDRIVE0.
                                device = "\\\\" + firstPart;
                                rootEnd = 4;
                                var colonIndex = path.IndexOf(':');
                                // JavaScript's slice clamps, so a missing (or too-early) colon yields the
                                // empty string rather than a negative length.
                                var end = Math.Clamp(colonIndex + 1, 4, length);
                                var possibleDevice = path[4..end];
                                if (IsWindowsReservedName(possibleDevice, possibleDevice.Length - 1))
                                {
                                    // A reserved device path such as \\?\COM1:.
                                    device = "\\\\?\\" + possibleDevice;
                                    rootEnd = 4 + possibleDevice.Length;
                                }
                            }
                            else if (j == length)
                            {
                                // The whole path is a UNC root: normalizing it appends a trailing
                                // separator, which is what makes it distinguishable from a directory.
                                return "\\\\" + firstPart + "\\" + path[last..] + "\\";
                            }
                            else
                            {
                                device = "\\\\" + firstPart + "\\" + path[last..j];
                                rootEnd = j;
                            }
                        }
                    }
                }
            }
            else
            {
                rootEnd = 1;
            }
        }
        else
        {
            var colonIndex = path.IndexOf(':');
            if (colonIndex > 0)
            {
                if (IsDeviceRoot(first) && colonIndex == 1)
                {
                    // Drive-relative: the drive becomes the device, but the path is only absolute when a
                    // separator follows the colon.
                    device = path[..2];
                    rootEnd = 2;
                    if (length > 2 && IsSeparator(path[2], windows: true))
                    {
                        isAbsolute = true;
                        rootEnd = 3;
                    }
                }
                else if (IsWindowsReservedName(path, colonIndex))
                {
                    device = path[..(colonIndex + 1)];
                    rootEnd = colonIndex + 1;
                }
            }
        }

        var tail = rootEnd < length
            ? NormalizeString(path[rootEnd..], !isAbsolute, '\\', windows: true)
            : string.Empty;

        if (tail.Length == 0 && !isAbsolute)
        {
            tail = ".";
        }

        if (tail.Length > 0 && IsSeparator(path[^1], windows: true))
        {
            tail += '\\';
        }

        if (!isAbsolute && device is null && path.Contains(':', StringComparison.Ordinal))
        {
            // CVE-2024-36139: a relative path must not normalize into something Windows would read as
            // absolute. Either the result already looks like a drive path, or a colon anywhere in the
            // input was at the end or followed by a separator.
            if (tail.Length >= 2 && IsDeviceRoot(tail[0]) && tail[1] == ':')
            {
                return ".\\" + tail;
            }

            var index = path.IndexOf(':');
            do
            {
                if (index == length - 1 || IsSeparator(path[index + 1], windows: true))
                {
                    return ".\\" + tail;
                }
            }
            while ((index = path.IndexOf(':', index + 1)) != -1);
        }

        var reservedColon = path.IndexOf(':');
        if (IsWindowsReservedName(path, reservedColon))
        {
            return ".\\" + (device ?? string.Empty) + tail;
        }

        if (device is null)
        {
            return isAbsolute ? "\\" + tail : tail;
        }

        return isAbsolute ? device + "\\" + tail : device + tail;
    }

    /// <summary>Node's <c>path.join</c> for the host flavour.</summary>
    public static string Join(params string[] parts) => Join(IsWindows, parts);

    /// <summary>Node's <c>path.join</c>: concatenate, then normalize.</summary>
    public static string Join(bool windows, params string[] parts)
    {
        var joined = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append(windows ? '\\' : '/');
            }

            joined.Append(part);
        }

        return joined.Length == 0 ? "." : Normalize(joined.ToString(), windows);
    }

    /// <summary>Node's <c>path.dirname</c> for the host flavour.</summary>
    public static string Dirname(string path) => Dirname(path, IsWindows);

    /// <summary>Node's <c>path.dirname</c>.</summary>
    public static string Dirname(string path, bool windows)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        if (!windows)
        {
            return DirnamePosix(path);
        }

        return DirnameWindows(path);
    }

    private static string DirnamePosix(string path)
    {
        if (path.Length == 1)
        {
            return path[0] == '/' ? "/" : ".";
        }

        // Trim trailing separators, but keep the root.
        var end = path.Length - 1;
        while (end > 0 && path[end] == '/')
        {
            end--;
        }

        if (path[end] != '/')
        {
            while (end >= 0 && path[end] != '/')
            {
                end--;
            }
        }

        if (end <= 0)
        {
            return path[0] == '/' ? "/" : ".";
        }

        while (end > 0 && path[end - 1] == '/')
        {
            end--;
        }

        return path[..end];
    }

    private static string DirnameWindows(string path)
    {
        var length = path.Length;
        if (length == 1)
        {
            return IsSeparator(path[0], windows: true) ? path : ".";
        }

        // Locate the root. A UNC path also has a device, which acts as the root for dirname.
        var rootEnd = -1;
        var offset = 0;
        var first = path[0];

        if (IsSeparator(first, windows: true))
        {
            // Node sets both before looking for a UNC device, so a path that is nothing but separators
            // still resolves its root to the first separator ("//" -> "/").
            rootEnd = offset = 1;

            if (IsSeparator(path[1], windows: true))
            {
                var j = 2;
                var last = j;
                while (j < length && !IsSeparator(path[j], windows: true))
                {
                    j++;
                }

                if (j < length && j != last)
                {
                    last = j;
                    while (j < length && IsSeparator(path[j], windows: true))
                    {
                        j++;
                    }

                    if (j < length && j != last)
                    {
                        last = j;
                        while (j < length && !IsSeparator(path[j], windows: true))
                        {
                            j++;
                        }

                        if (j == length)
                        {
                            // The whole path is a UNC root, so there is nothing to strip.
                            return path;
                        }

                        if (j != last)
                        {
                            // A UNC root with leftovers: offset by one to include the separator after
                            // the device, so it is treated as a normal root on top of the UNC root.
                            rootEnd = offset = j + 1;
                        }
                    }
                }
            }
        }
        else if (IsDeviceRoot(first) && path[1] == ':')
        {
            rootEnd = length > 2 && IsSeparator(path[2], windows: true) ? 3 : 2;
            offset = rootEnd;
        }

        var end = -1;
        var matchedSlash = true;
        for (var i = length - 1; i >= offset; i--)
        {
            if (IsSeparator(path[i], windows: true))
            {
                if (!matchedSlash)
                {
                    end = i;
                    break;
                }
            }
            else
            {
                matchedSlash = false;
            }
        }

        if (end == -1)
        {
            if (rootEnd == -1)
            {
                return ".";
            }

            end = rootEnd;
        }

        return path[..end];
    }

    /// <summary>Node's <c>path.basename</c> for the host flavour.</summary>
    public static string Basename(string path) => Basename(path, null);

    /// <summary>Node's <c>path.basename</c>, optionally stripping <paramref name="ext"/>.</summary>
    public static string Basename(string path, string? ext) => Basename(path, ext, IsWindows);

    /// <summary>Node's <c>path.basename</c>.</summary>
    public static string Basename(string path, string? ext, bool windows)
    {
        var start = 0;
        var end = -1;
        var matchedSlash = true;

        if (!string.IsNullOrEmpty(ext) && ext.Length <= path.Length)
        {
            if (string.Equals(ext, path, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var extIndex = ext.Length - 1;
            var firstNonSlashEnd = -1;
            for (var i = path.Length - 1; i >= 0; i--)
            {
                var code = path[i];
                if (IsSeparator(code, windows))
                {
                    if (!matchedSlash)
                    {
                        start = i + 1;
                        break;
                    }
                }
                else
                {
                    if (firstNonSlashEnd == -1)
                    {
                        matchedSlash = false;
                        firstNonSlashEnd = i + 1;
                    }

                    if (extIndex >= 0)
                    {
                        if (code == ext[extIndex])
                        {
                            if (--extIndex == -1)
                            {
                                end = i;
                            }
                        }
                        else
                        {
                            extIndex = -1;
                            end = firstNonSlashEnd;
                        }
                    }
                }
            }

            if (start == end)
            {
                end = firstNonSlashEnd;
            }
            else if (end == -1)
            {
                end = path.Length;
            }

            return path[start..end];
        }

        // Skip a drive letter so the following separator is not mistaken for a trailing one.
        if (path.Length >= 2 && IsDeviceRoot(path[0]) && path[1] == ':')
        {
            start = 2;
        }

        for (var i = path.Length - 1; i >= start; i--)
        {
            if (IsSeparator(path[i], windows))
            {
                if (!matchedSlash)
                {
                    start = i + 1;
                    break;
                }
            }
            else if (end == -1)
            {
                matchedSlash = false;
                end = i + 1;
            }
        }

        return end == -1 ? string.Empty : path[start..end];
    }

    /// <summary>Node's <c>path.isAbsolute</c> for the host flavour.</summary>
    public static bool IsAbsolute(string path) => IsAbsolute(path, IsWindows);

    /// <summary>Node's <c>path.isAbsolute</c>.</summary>
    /// <remarks>
    /// On Windows a bare drive (<c>"C:"</c>) is <em>not</em> absolute; <c>"C:/"</c> is.
    /// </remarks>
    public static bool IsAbsolute(string path, bool windows)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (!windows)
        {
            return path[0] == '/';
        }

        return IsSeparator(path[0], windows: true) ||
               (path.Length > 2 &&
                IsDeviceRoot(path[0]) &&
                path[1] == ':' &&
                IsSeparator(path[2], windows: true));
    }

    /// <summary>
    /// Node's <c>path.resolve</c> for one or two segments, which is the shape
    /// <c>utils/paths.ts</c> uses.
    /// </summary>
    /// <summary>Node's <c>path.resolve</c> for the host flavour.</summary>
    public static string Resolve(params string[] parts) => Resolve(IsWindows, parts);

    /// <summary>
    /// Node's <c>path.resolve</c>: walk the arguments right to left until an absolute one is found,
    /// then normalize the accumulated tail.
    /// </summary>
    /// <remarks>
    /// This is not <see cref="Path.GetFullPath(string)"/>. Two behaviours are load-bearing here and
    /// neither is reproduced by the BCL:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// A rooted-but-driveless path (<c>/other/a</c>) is absolute in the sense that it stops the walk,
    /// yet it still needs a drive, which is taken from the current working directory — so
    /// <c>resolve("/other/a")</c> is <c>D:\other\a</c> on drive D:.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The POSIX flavour drops the drive indicator and normalizes separators on Windows, so
    /// <c>resolve(…, windows: false)</c> is <em>not</em> the Windows answer with slashes swapped.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public static string Resolve(bool windows, params string[] parts) =>
        windows ? ResolveWindows(parts) : ResolvePosix(parts);

    /// <summary>Node's <c>path.toNamespacedPath</c> for the host flavour.</summary>
    public static string ToNamespacedPath(string path) => ToNamespacedPath(path, IsWindows);

    /// <summary>
    /// Node's <c>path.toNamespacedPath</c>: rewrite a Windows path into the extended-length form
    /// (<c>\\?\</c> or <c>\\?\UNC\</c>) so that it is not subject to <c>MAX_PATH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The POSIX implementation is the identity function, including for the empty string.
    /// </para>
    /// <para>
    /// Two details are easy to get wrong. A path whose <em>resolved</em> form is two characters or
    /// shorter returns the <em>input</em> rather than the resolved path, and the UNC rewrite is skipped
    /// when the third character is already <c>?</c> or <c>.</c>, which is what leaves <c>\\?\C:\x</c> and
    /// <c>\\.\pipe\x</c> untouched.
    /// </para>
    /// </remarks>
    public static string ToNamespacedPath(string path, bool windows)
    {
        if (!windows || path.Length == 0)
        {
            return path;
        }

        var resolved = Resolve(windows: true, path);
        if (resolved.Length <= 2)
        {
            return path;
        }

        if (resolved[0] == '\\')
        {
            if (resolved[1] == '\\')
            {
                var code = resolved[2];
                if (code != '?' && code != '.')
                {
                    return @"\\?\UNC\" + resolved[2..];
                }
            }
        }
        else if (IsDeviceRoot(resolved[0]) && resolved[1] == ':' && resolved[2] == '\\')
        {
            return @"\\?\" + resolved;
        }

        return resolved;
    }

    private static string ResolveWindows(IReadOnlyList<string> parts)
    {
        var resolvedDevice = string.Empty;
        var resolvedTail = string.Empty;
        var resolvedAbsolute = false;

        for (var i = parts.Count - 1; i >= -1; i--)
        {
            string path;
            if (i >= 0)
            {
                path = parts[i];
                if (path.Length == 0)
                {
                    continue;
                }
            }
            else
            {
                // Node prefers the per-drive variable (="D:"=…) once a drive is known, falling back to
                // the process cwd and then to the drive root. .NET has no per-drive cwd, so the process
                // cwd is the only source; the drive-mismatch fallback below still applies.
                path = Cwd();
                if (resolvedDevice.Length > 0 &&
                    (path.Length < 2 ||
                     !path[..2].Equals(resolvedDevice, StringComparison.OrdinalIgnoreCase) ||
                     (path.Length > 2 && path[2] != '\\')))
                {
                    path = resolvedDevice + "\\";
                }
            }

            var length = path.Length;
            var rootEnd = 0;
            var device = string.Empty;
            var isAbsolute = false;
            var code = path[0];

            if (length == 1)
            {
                if (IsSeparator(code, windows: true))
                {
                    rootEnd = 1;
                    isAbsolute = true;
                }
            }
            else if (IsSeparator(code, windows: true))
            {
                isAbsolute = true;

                if (IsSeparator(path[1], windows: true))
                {
                    // Possible UNC root: two non-empty segments after the leading separators.
                    var j = 2;
                    var last = j;
                    while (j < length && !IsSeparator(path[j], windows: true))
                    {
                        j++;
                    }

                    if (j < length && j != last)
                    {
                        var firstPart = path[last..j];
                        last = j;
                        while (j < length && IsSeparator(path[j], windows: true))
                        {
                            j++;
                        }

                        if (j < length && j != last)
                        {
                            last = j;
                            while (j < length && !IsSeparator(path[j], windows: true))
                            {
                                j++;
                            }

                            if (j == length || j != last)
                            {
                                if (firstPart is "." or "?")
                                {
                                    // A device root such as \\.\PHYSICALDRIVE0. Unlike normalize, the
                                    // reserved-name fixup is not applied here.
                                    device = "\\\\" + firstPart;
                                    rootEnd = 4;
                                }
                                else
                                {
                                    device = "\\\\" + firstPart + "\\" + path[last..j];
                                    rootEnd = j;
                                }
                            }
                        }
                    }
                }
                else
                {
                    rootEnd = 1;
                }
            }
            else if (IsDeviceRoot(code) && path[1] == ':')
            {
                device = path[..2];
                rootEnd = 2;
                if (length > 2 && IsSeparator(path[2], windows: true))
                {
                    isAbsolute = true;
                    rootEnd = 3;
                }
            }

            if (device.Length > 0)
            {
                if (resolvedDevice.Length > 0)
                {
                    if (!device.Equals(resolvedDevice, StringComparison.OrdinalIgnoreCase))
                    {
                        // This path points to another device, so it is not applicable.
                        continue;
                    }
                }
                else
                {
                    resolvedDevice = device;
                }
            }

            if (resolvedAbsolute)
            {
                if (resolvedDevice.Length > 0)
                {
                    break;
                }
            }
            else
            {
                resolvedTail = path[rootEnd..] + "\\" + resolvedTail;
                resolvedAbsolute = isAbsolute;
                if (isAbsolute && resolvedDevice.Length > 0)
                {
                    break;
                }
            }
        }

        resolvedTail = NormalizeString(resolvedTail, !resolvedAbsolute, '\\', windows: true);
        if (resolvedAbsolute)
        {
            return resolvedDevice + "\\" + resolvedTail;
        }

        var relative = resolvedDevice + resolvedTail;
        return relative.Length > 0 ? relative : ".";
    }

    private static string ResolvePosix(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 || (parts.Count == 1 && parts[0] is "" or "."))
        {
            var fastCwd = PosixCwd();
            if (fastCwd.Length > 0 && fastCwd[0] == '/')
            {
                return fastCwd;
            }
        }

        var resolvedPath = string.Empty;
        var resolvedAbsolute = false;
        for (var i = parts.Count - 1; i >= 0 && !resolvedAbsolute; i--)
        {
            var path = parts[i];
            if (path.Length == 0)
            {
                continue;
            }

            resolvedPath = path + "/" + resolvedPath;
            resolvedAbsolute = path[0] == '/';
        }

        if (!resolvedAbsolute)
        {
            var cwd = PosixCwd();
            resolvedPath = cwd + "/" + resolvedPath;
            resolvedAbsolute = cwd.Length > 0 && cwd[0] == '/';
        }

        resolvedPath = NormalizeString(resolvedPath, !resolvedAbsolute, '/', windows: false);
        if (resolvedAbsolute)
        {
            return "/" + resolvedPath;
        }

        return resolvedPath.Length > 0 ? resolvedPath : ".";
    }

    /// <summary>
    /// Node's <c>posixCwd</c>: on Windows the working directory is slash-normalized and its drive
    /// indicator dropped, which is why the POSIX flavour resolves to a rooted path without a drive.
    /// </summary>
    private static string PosixCwd()
    {
        var cwd = Cwd();
        if (!IsWindows)
        {
            return cwd;
        }

        var slashed = cwd.Replace('\\', '/');
        var index = slashed.IndexOf('/');
        return index < 0 ? slashed : slashed[index..];
    }

    /// <summary>Node's <c>path.relative</c> for the host flavour.</summary>
    public static string Relative(string from, string to) => Relative(IsWindows, from, to);

    /// <summary>
    /// Node's <c>path.relative</c>: resolve and normalize both paths, then describe
    /// <paramref name="to"/> relative to <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not <see cref="Path.GetRelativePath(string, string)"/>. Two differences matter to
    /// callers: the BCL returns <c>.</c> for equal paths where Node returns the empty string, and the
    /// BCL does not re-normalize its inputs, so <c>relative("a/../b", "b")</c> is not recognized as
    /// equal. <c>Paths.GetCwdRelativePath</c> relies on the empty string to mean "the target <em>is</em>
    /// the cwd", which it then renders as <c>.</c> only at the very end.
    /// </para>
    /// <para>
    /// The Windows flavour has a second, segment-wise strategy that is used when case folding changes
    /// a path's length — otherwise a character-wise comparison would be comparing misaligned strings.
    /// </para>
    /// </remarks>
    public static string Relative(bool windows, string from, string to)
    {
        if (from == to)
        {
            return string.Empty;
        }

        var fromOrig = Resolve(windows, from);
        var toOrig = Resolve(windows, to);
        if (fromOrig == toOrig)
        {
            return string.Empty;
        }

        if (!windows)
        {
            return RelativePosix(fromOrig, toOrig);
        }

        var fromLower = fromOrig.ToLowerInvariant();
        var toLower = toOrig.ToLowerInvariant();
        if (fromLower == toLower)
        {
            return string.Empty;
        }

        return fromOrig.Length != fromLower.Length || toOrig.Length != toLower.Length
            ? RelativeWindowsBySegment(fromOrig, toOrig)
            : RelativeWindows(fromLower, toLower, fromOrig, toOrig);
    }

    private static string RelativePosix(string from, string to)
    {
        const char separator = '/';
        var fromStart = 1;
        var fromEnd = from.Length;
        var fromLen = fromEnd - fromStart;
        var toStart = 1;
        var toLen = to.Length - toStart;

        var length = Math.Min(fromLen, toLen);
        var lastCommonSep = -1;
        var index = 0;
        for (; index < length; index++)
        {
            var code = from[fromStart + index];
            if (code != to[toStart + index])
            {
                break;
            }

            if (code == separator)
            {
                lastCommonSep = index;
            }
        }

        if (index == length)
        {
            if (toLen > length)
            {
                // "from" is the exact base path for "to", e.g. /foo/bar -> /foo/bar/baz.
                if (CharAt(to, toStart + index) == separator)
                {
                    return to[(toStart + index + 1)..];
                }

                // "from" is the root, e.g. / -> /foo.
                if (index == 0)
                {
                    return to[(toStart + index)..];
                }
            }
            else if (fromLen > length)
            {
                if (CharAt(from, fromStart + index) == separator)
                {
                    // "to" is the exact base path for "from".
                    lastCommonSep = index;
                }
                else if (index == 0)
                {
                    // "to" is the root.
                    lastCommonSep = 0;
                }
            }
        }

        var above = new StringBuilder();
        for (index = fromStart + lastCommonSep + 1; index <= fromEnd; index++)
        {
            if (index == fromEnd || CharAt(from, index) == separator)
            {
                above.Append(above.Length == 0 ? ".." : "/..");
            }
        }

        return above + to[(toStart + lastCommonSep)..];
    }

    private static string RelativeWindows(string from, string to, string fromOrig, string toOrig)
    {
        const char separator = '\\';

        // Leading backslashes are dropped from both, and trailing ones from UNC roots.
        var fromStart = 0;
        while (fromStart < from.Length && from[fromStart] == separator)
        {
            fromStart++;
        }

        var fromEnd = from.Length;
        while (fromEnd - 1 > fromStart && from[fromEnd - 1] == separator)
        {
            fromEnd--;
        }

        var fromLen = fromEnd - fromStart;

        var toStart = 0;
        while (toStart < to.Length && to[toStart] == separator)
        {
            toStart++;
        }

        var toEnd = to.Length;
        while (toEnd - 1 > toStart && to[toEnd - 1] == separator)
        {
            toEnd--;
        }

        var toLen = toEnd - toStart;

        var length = Math.Min(fromLen, toLen);
        var lastCommonSep = -1;
        var index = 0;
        for (; index < length; index++)
        {
            var code = from[fromStart + index];
            if (code != to[toStart + index])
            {
                break;
            }

            if (code == separator)
            {
                lastCommonSep = index;
            }
        }

        if (index != length)
        {
            // A mismatch before any common separator: there is no shared root, so "to" is absolute.
            if (lastCommonSep == -1)
            {
                return toOrig;
            }
        }
        else
        {
            if (toLen > length)
            {
                if (CharAt(to, toStart + index) == separator)
                {
                    // "from" is the exact base path for "to".
                    return toOrig[(toStart + index + 1)..];
                }

                if (index == 2)
                {
                    // "from" is the device root, e.g. C:\ -> C:\foo.
                    return toOrig[(toStart + index)..];
                }
            }

            if (fromLen > length)
            {
                if (CharAt(from, fromStart + index) == separator)
                {
                    // "to" is the exact base path for "from".
                    lastCommonSep = index;
                }
                else if (index == 2)
                {
                    // "to" is the device root, e.g. C:\foo\bar -> C:\.
                    lastCommonSep = 3;
                }
            }

            if (lastCommonSep == -1)
            {
                lastCommonSep = 0;
            }
        }

        var above = new StringBuilder();
        for (index = fromStart + lastCommonSep + 1; index <= fromEnd; index++)
        {
            if (index == fromEnd || CharAt(from, index) == separator)
            {
                above.Append(above.Length == 0 ? ".." : "\\..");
            }
        }

        toStart += lastCommonSep;

        if (above.Length > 0)
        {
            return above + toOrig[toStart..toEnd];
        }

        if (CharAt(toOrig, toStart) == separator)
        {
            toStart++;
        }

        return toOrig[toStart..toEnd];
    }

    /// <summary>
    /// The Windows fallback used when case folding changed a path's length, so the paths must be
    /// compared segment by segment instead of character by character.
    /// </summary>
    private static string RelativeWindowsBySegment(string fromOrig, string toOrig)
    {
        var fromSplit = new List<string>(fromOrig.Split('\\'));
        var toSplit = new List<string>(toOrig.Split('\\'));
        if (fromSplit[^1].Length == 0)
        {
            fromSplit.RemoveAt(fromSplit.Count - 1);
        }

        if (toSplit[^1].Length == 0)
        {
            toSplit.RemoveAt(toSplit.Count - 1);
        }

        var fromLen = fromSplit.Count;
        var toLen = toSplit.Count;
        var length = Math.Min(fromLen, toLen);

        var index = 0;
        for (; index < length; index++)
        {
            if (!fromSplit[index].Equals(toSplit[index], StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        if (index == 0)
        {
            return toOrig;
        }

        if (index == length)
        {
            if (toLen > length)
            {
                return string.Join('\\', toSplit.Skip(index));
            }

            if (fromLen > length)
            {
                return string.Concat(Enumerable.Repeat("..\\", fromLen - 1 - index)) + "..";
            }

            return string.Empty;
        }

        return string.Concat(Enumerable.Repeat("..\\", fromLen - index)) + string.Join('\\', toSplit.Skip(index));
    }

    /// <summary>
    /// JavaScript's <c>charCodeAt</c> for out-of-range indices yields <c>NaN</c>, which never equals a
    /// separator; <c>'\0'</c> is the C# stand-in and is never a separator either.
    /// </summary>
    private static char CharAt(string value, int index) =>
        index >= 0 && index < value.Length ? value[index] : '\0';

    /// <summary>
    /// Node's <c>normalizeString</c> helper: the segment stack shared by <c>normalize</c> and
    /// <c>join</c>.
    /// </summary>
    private static string NormalizeString(string path, bool allowAboveRoot, char separator, bool windows)
    {
        var res = new StringBuilder();
        var lastSegmentLength = 0;
        var lastSlash = -1;
        var dots = 0;
        var code = '\0';

        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length)
            {
                code = path[i];
            }
            else if (IsSeparator(code, windows))
            {
                break;
            }
            else
            {
                code = '/';
            }

            if (IsSeparator(code, windows))
            {
                if (lastSlash == i - 1 || dots == 1)
                {
                    // Repeated separator, or a "." segment: nothing to do.
                }
                else if (dots == 2)
                {
                    if (res.Length < 2 ||
                        lastSegmentLength != 2 ||
                        res[^1] != '.' ||
                        res[^2] != '.')
                    {
                        if (res.Length > 2)
                        {
                            // Node derives the cut from the last segment's length, not from a search
                            // for the separator: a segment may itself contain one on the other flavour.
                            var lastSlashIndex = res.Length - lastSegmentLength - 1;
                            if (lastSlashIndex == -1)
                            {
                                res.Clear();
                                lastSegmentLength = 0;
                            }
                            else
                            {
                                res.Length = lastSlashIndex;
                                lastSegmentLength = res.Length - 1 - LastIndexOf(res, separator);
                            }

                            lastSlash = i;
                            dots = 0;
                            continue;
                        }

                        if (res.Length is 2 or 1)
                        {
                            res.Clear();
                            lastSegmentLength = 0;
                            lastSlash = i;
                            dots = 0;
                            continue;
                        }
                    }

                    if (allowAboveRoot)
                    {
                        if (res.Length > 0)
                        {
                            res.Append(separator).Append("..");
                        }
                        else
                        {
                            res.Append("..");
                        }

                        lastSegmentLength = 2;
                    }
                }
                else
                {
                    if (res.Length > 0)
                    {
                        res.Append(separator);
                    }

                    res.Append(path, lastSlash + 1, i - lastSlash - 1);
                    lastSegmentLength = i - lastSlash - 1;
                }

                lastSlash = i;
                dots = 0;
            }
            else if (code == '.' && dots != -1)
            {
                dots++;
            }
            else
            {
                dots = -1;
            }
        }

        return res.ToString();
    }

    private static int LastIndexOf(StringBuilder builder, char value)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            if (builder[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
