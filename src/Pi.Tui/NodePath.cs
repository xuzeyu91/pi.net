namespace Pi.Tui;

/// <summary>
/// A transliteration of Node's <c>path.win32</c> (v22.22.2). <c>autocomplete.ts</c> builds
/// user-visible completion strings with <c>join</c> / <c>dirname</c> / <c>basename</c>, and the
/// <see cref="System.IO.Path"/> equivalents normalise differently — <c>Path.Join(".", "x")</c> is
/// <c>".\x"</c> while Node returns <c>"x"</c>. The behaviour here is verified against 769 vectors
/// captured from <c>node:path</c> itself (see <c>tests/Pi.Tui.Tests/autocomplete-corpus.json</c>).
/// </summary>
internal static class NodePath
{
    private const char ForwardSlash = '/';
    private const char BackSlash = '\\';
    private const char Dot = '.';
    private const char Colon = ':';

    private static readonly string[] WindowsReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3",
    ];

    private static bool IsPathSeparator(char code) => code is ForwardSlash or BackSlash;

    private static bool IsWindowsDeviceRoot(char code) => code is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsWindowsReservedName(string path, int colonIndex) =>
        Array.IndexOf(WindowsReservedNames, JsString.Slice(path, 0, colonIndex).ToUpperInvariant()) >= 0;

    /// <summary>Resolves <c>.</c> and <c>..</c> elements in a path with directory names.</summary>
    private static string NormalizeString(string path, bool allowAboveRoot, string separator)
    {
        var res = "";
        var lastSegmentLength = 0;
        var lastSlash = -1;
        var dots = 0;
        var code = '\0';
        for (var i = 0; i <= path.Length; ++i)
        {
            if (i < path.Length)
            {
                code = path[i];
            }
            else if (IsPathSeparator(code))
            {
                break;
            }
            else
            {
                code = ForwardSlash;
            }

            if (IsPathSeparator(code))
            {
                if (lastSlash == i - 1 || dots == 1)
                {
                    // NOOP
                }
                else if (dots == 2)
                {
                    if (res.Length < 2 || lastSegmentLength != 2 || res[^1] != Dot || res[^2] != Dot)
                    {
                        if (res.Length > 2)
                        {
                            var lastSlashIndex = res.Length - lastSegmentLength - 1;
                            if (lastSlashIndex == -1)
                            {
                                res = "";
                                lastSegmentLength = 0;
                            }
                            else
                            {
                                res = res[..lastSlashIndex];
                                lastSegmentLength = res.Length - 1 - res.LastIndexOf(separator, StringComparison.Ordinal);
                            }

                            lastSlash = i;
                            dots = 0;
                            continue;
                        }

                        if (res.Length != 0)
                        {
                            res = "";
                            lastSegmentLength = 0;
                            lastSlash = i;
                            dots = 0;
                            continue;
                        }
                    }

                    if (allowAboveRoot)
                    {
                        res += res.Length > 0 ? separator + ".." : "..";
                        lastSegmentLength = 2;
                    }
                }
                else
                {
                    var segment = JsString.Slice(path, lastSlash + 1, i);
                    res = res.Length > 0 ? res + separator + segment : segment;
                    lastSegmentLength = i - lastSlash - 1;
                }

                lastSlash = i;
                dots = 0;
            }
            else if (code == Dot && dots != -1)
            {
                ++dots;
            }
            else
            {
                dots = -1;
            }
        }

        return res;
    }

    public static string Normalize(string path)
    {
        var len = path.Length;
        if (len == 0)
        {
            return ".";
        }

        var rootEnd = 0;
        string? device = null;
        var isAbsolute = false;
        var code = path[0];

        if (len == 1)
        {
            return IsPathSeparator(code) ? "\\" : path;
        }

        if (IsPathSeparator(code))
        {
            isAbsolute = true;
            if (IsPathSeparator(path[1]))
            {
                var j = 2;
                var last = j;
                while (j < len && !IsPathSeparator(path[j]))
                {
                    j++;
                }

                if (j < len && j != last)
                {
                    var firstPart = path[last..j];
                    last = j;
                    while (j < len && IsPathSeparator(path[j]))
                    {
                        j++;
                    }

                    if (j < len && j != last)
                    {
                        last = j;
                        while (j < len && !IsPathSeparator(path[j]))
                        {
                            j++;
                        }

                        if (j == len || j != last)
                        {
                            if (firstPart is "." or "?")
                            {
                                device = $"\\\\{firstPart}";
                                rootEnd = 4;
                                var colonIndex = path.IndexOf(Colon);
                                var possibleDevice = JsString.Slice(path, 4, colonIndex + 1);
                                if (IsWindowsReservedName(possibleDevice, possibleDevice.Length - 1))
                                {
                                    device = $"\\\\?\\{possibleDevice}";
                                    rootEnd = 4 + possibleDevice.Length;
                                }
                            }
                            else if (j == len)
                            {
                                return $"\\\\{firstPart}\\{path[last..]}\\";
                            }
                            else
                            {
                                device = $"\\\\{firstPart}\\{path[last..j]}";
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
            var colonIndex = path.IndexOf(Colon);
            if (colonIndex > 0)
            {
                if (IsWindowsDeviceRoot(code) && colonIndex == 1)
                {
                    device = path[..2];
                    rootEnd = 2;
                    if (len > 2 && IsPathSeparator(path[2]))
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

        var tail = rootEnd < len ? NormalizeString(path[rootEnd..], !isAbsolute, "\\") : "";
        if (tail.Length == 0 && !isAbsolute)
        {
            tail = ".";
        }

        if (tail.Length > 0 && IsPathSeparator(path[len - 1]))
        {
            tail += "\\";
        }

        if (!isAbsolute && device is null && path.Contains(Colon))
        {
            if (tail.Length >= 2 && IsWindowsDeviceRoot(tail[0]) && tail[1] == Colon)
            {
                return $".\\{tail}";
            }

            var index = path.IndexOf(Colon);
            do
            {
                if (index == len - 1 || IsPathSeparator(JsString.CharAt(path, index + 1)))
                {
                    return $".\\{tail}";
                }
            }
            while ((index = path.IndexOf(Colon, index + 1)) != -1);
        }

        if (IsWindowsReservedName(path, path.IndexOf(Colon)))
        {
            return $".\\{device ?? ""}{tail}";
        }

        if (device is null)
        {
            return isAbsolute ? $"\\{tail}" : tail;
        }

        return isAbsolute ? $"{device}\\{tail}" : $"{device}{tail}";
    }

    public static string Dirname(string path)
    {
        var len = path.Length;
        if (len == 0)
        {
            return ".";
        }

        var rootEnd = -1;
        var offset = 0;
        var code = path[0];

        if (len == 1)
        {
            return IsPathSeparator(code) ? path : ".";
        }

        if (IsPathSeparator(code))
        {
            rootEnd = offset = 1;
            if (IsPathSeparator(path[1]))
            {
                var j = 2;
                var last = j;
                while (j < len && !IsPathSeparator(path[j]))
                {
                    j++;
                }

                if (j < len && j != last)
                {
                    last = j;
                    while (j < len && IsPathSeparator(path[j]))
                    {
                        j++;
                    }

                    if (j < len && j != last)
                    {
                        last = j;
                        while (j < len && !IsPathSeparator(path[j]))
                        {
                            j++;
                        }

                        if (j == len)
                        {
                            return path;
                        }

                        if (j != last)
                        {
                            rootEnd = offset = j + 1;
                        }
                    }
                }
            }
        }
        else if (IsWindowsDeviceRoot(code) && path[1] == Colon)
        {
            rootEnd = len > 2 && IsPathSeparator(path[2]) ? 3 : 2;
            offset = rootEnd;
        }

        var end = -1;
        var matchedSlash = true;
        for (var i = len - 1; i >= offset; --i)
        {
            if (IsPathSeparator(path[i]))
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

    public static string Basename(string path)
    {
        var start = 0;
        var end = -1;
        var matchedSlash = true;

        if (path.Length >= 2 && IsWindowsDeviceRoot(path[0]) && path[1] == Colon)
        {
            start = 2;
        }

        for (var i = path.Length - 1; i >= start; --i)
        {
            if (IsPathSeparator(path[i]))
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

        return end == -1 ? "" : path[start..end];
    }

    public static string Join(params string[] parts)
    {
        if (parts.Length == 0)
        {
            return ".";
        }

        var path = new List<string>();
        foreach (var arg in parts)
        {
            if (arg.Length > 0)
            {
                path.Add(arg);
            }
        }

        if (path.Count == 0)
        {
            return ".";
        }

        var firstPart = path[0];
        var joined = string.Join("\\", path);

        // Make sure that the joined path doesn't start with two slashes, because Normalize() will
        // mistake it for a UNC path then. Skipped when the first argument clearly points at a UNC
        // path (exactly two leading slashes followed by at least one non-slash character).
        var needsReplace = true;
        var slashCount = 0;
        if (IsPathSeparator(firstPart[0]))
        {
            ++slashCount;
            var firstLen = firstPart.Length;
            if (firstLen > 1 && IsPathSeparator(firstPart[1]))
            {
                ++slashCount;
                if (firstLen > 2)
                {
                    if (IsPathSeparator(firstPart[2]))
                    {
                        ++slashCount;
                    }
                    else
                    {
                        needsReplace = false;
                    }
                }
            }
        }

        if (needsReplace)
        {
            while (slashCount < joined.Length && IsPathSeparator(joined[slashCount]))
            {
                slashCount++;
            }

            if (slashCount >= 2)
            {
                joined = $"\\{joined[slashCount..]}";
            }
        }

        // Skip normalization when reserved device names are present.
        var segments = new List<string>();
        var part = "";
        for (var i = 0; i < joined.Length; i++)
        {
            if (joined[i] == BackSlash)
            {
                if (part.Length > 0)
                {
                    segments.Add(part);
                }

                part = "";
                while (i + 1 < joined.Length && joined[i + 1] == BackSlash)
                {
                    i++;
                }
            }
            else
            {
                part += joined[i];
            }
        }

        if (part.Length > 0)
        {
            segments.Add(part);
        }

        if (segments.Any(segment =>
        {
            var colonIndex = segment.IndexOf(Colon);
            return colonIndex != -1 && IsWindowsReservedName(segment, colonIndex);
        }))
        {
            var result = "";
            foreach (var c in joined)
            {
                result += c == ForwardSlash ? BackSlash : c;
            }

            return result;
        }

        return Normalize(joined);
    }
}
