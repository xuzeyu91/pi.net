using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>Options controlling <see cref="Paths.NormalizePath"/> (port of <c>PathInputOptions</c>).</summary>
/// <param name="Trim">Trim leading/trailing whitespace before normalization.</param>
/// <param name="ExpandTilde">Expand a leading <c>~</c> to a home directory. Defaults to true.</param>
/// <param name="HomeDir">Home directory used for <c>~</c> expansion. Defaults to the OS home directory.</param>
/// <param name="StripAtPrefix">Strip a leading <c>@</c>, used for CLI <c>@file</c> paths.</param>
/// <param name="NormalizeUnicodeSpaces">Normalize unicode space variants to regular spaces.</param>
public sealed record PathInputOptions(
    bool Trim = false,
    bool? ExpandTilde = null,
    string? HomeDir = null,
    bool StripAtPrefix = false,
    bool NormalizeUnicodeSpaces = false);

/// <summary>Port of <c>utils/paths.ts</c>.</summary>
/// <remarks>
/// <para>
/// <c>normalizeWindowsShellPath</c> is a pure string transform and is exposed as such, so it can be
/// exercised on any host. Everything that delegates to Node's <c>path</c> module
/// (<c>resolve</c>/<c>relative</c>/<c>join</c>/<c>isAbsolute</c>/<c>sep</c>) uses the .NET equivalents
/// and therefore follows the <em>host</em> OS, exactly as the TS module follows <c>process.platform</c>.
/// </para>
/// <para>
/// <c>getFileRevision</c> cannot be reproduced bit for bit: Node reads <c>dev</c>, <c>ino</c> and
/// nanosecond <c>mtime</c>/<c>ctime</c> from <c>stat(2)</c> with <c>bigint: true</c>, and .NET exposes
/// none of those portably. The port keeps the field shape (<c>dev:ino:size:mtimeNs:ctimeNs</c>) and
/// fills the fields it can: <c>size</c> exactly, <c>mtimeNs</c> as ticks × 100 (100 ns resolution), and
/// <c>ctimeNs</c> from the creation time, which is also what libuv reports for <c>ctime</c> on Windows.
/// <c>dev</c> and <c>ino</c> are reported as <c>0</c>. Callers only compare revisions for equality, and
/// size plus mtime still move on every write, so the cache invalidation these stores rely on is intact.
/// </para>
/// </remarks>
public static partial class Paths
{
    // JS `\s`-free literal class from paths.ts: note this is *not* the full JS whitespace set — it
    // omits U+1680 and U+FEFF, which the source deliberately leaves alone.
    [GeneratedRegex("[\u00A0\u2000-\u200A\u202F\u205F\u3000]")]
    private static partial Regex UnicodeSpacesRegex();

    // /^\/(?:mnt\/|cygdrive\/)?([a-z])(?:\/(.*))?$/i — anchored with \z because JS `$` (no `m` flag)
    // matches only at end of input while .NET `$` also matches before a trailing newline.
    [GeneratedRegex(@"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?\z", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsShellPathRegex();

    [GeneratedRegex(@"^file://")]
    private static partial Regex FileUrlRegex();

    /// <summary>
    /// Resolve a path to its canonical (real) form, following symlinks. Falls back to the raw path if
    /// resolution fails (for example the target does not exist yet), so callers never crash on missing
    /// filesystem entries.
    /// </summary>
    /// <remarks>
    /// Symlinks are resolved component by component so intermediate links are followed the way
    /// <c>realpath(3)</c> does. One deviation remains: Node's <c>realpathSync</c> also reports the
    /// on-disk casing on Windows, which .NET cannot query without opening a handle, so callers that
    /// compare canonical paths with <c>==</c> can see a case-only mismatch on Windows.
    /// </remarks>
    public static string CanonicalizePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var current = root;
            foreach (var segment in full[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    continue;
                }

                var target = File.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }

            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>
    /// A change token for a file, or <see langword="null"/> when it cannot be read. See the type remarks
    /// for how this differs from the Node original.
    /// </summary>
    public static string? GetFileRevision(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var size = info.Length;
            var mtimeNs = info.LastWriteTimeUtc.Ticks * 100;
            var ctimeNs = info.CreationTimeUtc.Ticks * 100;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"0:0:{size}:{mtimeNs}:{ctimeNs}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the value is <em>not</em> a package source (<c>npm:</c>, <c>git:</c>, …), a built-in
    /// extension (<c>builtin:</c>), or a remote URL protocol. Bare names, relative paths and
    /// <c>file:</c> URLs count as local.
    /// </summary>
    public static bool IsLocalPath(string value)
    {
        var trimmed = value.Trim();
        return !trimmed.StartsWith("npm:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("git:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("github:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("http:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("https:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("ssh:", StringComparison.Ordinal) &&
               !trimmed.StartsWith("builtin:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Convert Git Bash, MSYS, Cygwin and WSL drive paths into a form native Windows APIs accept.
    /// </summary>
    /// <remarks>
    /// Anything that is not a single-slash-rooted POSIX path is returned untouched, including UNC-style
    /// <c>//server/share</c> paths and anything already containing a backslash.
    /// </remarks>
    public static string NormalizeWindowsShellPath(string filePath)
    {
        if (!filePath.StartsWith('/') ||
            filePath.StartsWith("//", StringComparison.Ordinal) ||
            filePath.Contains('\\', StringComparison.Ordinal))
        {
            return filePath;
        }

        var match = WindowsShellPathRegex().Match(filePath);
        if (!match.Success)
        {
            return filePath;
        }

        // Group 2 is absent for "/c" and empty for "/c/" and "/mnt/c/"; both become a bare "C:\".
        var suffix = match.Groups[2].Success
            ? match.Groups[2].Value.Replace("/", "\\", StringComparison.Ordinal)
            : null;

        return match.Groups[1].Value.ToUpperInvariant() + ":\\" + (suffix ?? string.Empty);
    }

    /// <summary>Normalize a user-supplied path: optional trimming, tilde expansion and file URLs.</summary>
    public static string NormalizePath(string input, PathInputOptions? options = null)
    {
        options ??= new PathInputOptions();
        var normalized = options.Trim ? input.Trim() : input;

        if (options.NormalizeUnicodeSpaces)
        {
            normalized = UnicodeSpacesRegex().Replace(normalized, " ");
        }

        if (options.StripAtPrefix && normalized.StartsWith('@'))
        {
            normalized = normalized[1..];
        }

        if (string.Equals(ProcessInfo.Platform, "win32", StringComparison.Ordinal))
        {
            normalized = NormalizeWindowsShellPath(normalized);
        }

        if (options.ExpandTilde ?? true)
        {
            var home = options.HomeDir ?? GetHomeDirectory();
            if (string.Equals(normalized, "~", StringComparison.Ordinal))
            {
                return home;
            }

            if (normalized.StartsWith("~/", StringComparison.Ordinal) ||
                (string.Equals(ProcessInfo.Platform, "win32", StringComparison.Ordinal) &&
                 normalized.StartsWith("~\\", StringComparison.Ordinal)))
            {
                return NodePath.Join(home, normalized[2..]);
            }
        }

        if (FileUrlRegex().IsMatch(normalized))
        {
            return FileUrlToPath(normalized);
        }

        return normalized;
    }

    /// <summary>Normalize then resolve against <paramref name="baseDir"/> (default: current directory).</summary>
    public static string ResolvePath(string input, string? baseDir = null, PathInputOptions? options = null)
    {
        var normalized = NormalizePath(input, options);
        if (baseDir is null)
        {
            return NodePath.Resolve(normalized);
        }

        return NodePath.Resolve(NormalizePath(baseDir), normalized);
    }

    /// <summary>
    /// The path relative to <paramref name="cwd"/>, or <see langword="null"/> when it escapes the
    /// working directory.
    /// </summary>
    public static string? GetCwdRelativePath(string filePath, string cwd)
    {
        var resolvedCwd = ResolvePath(cwd);
        var resolvedPath = ResolvePath(filePath, resolvedCwd);
        var relativePath = NodePath.Relative(resolvedCwd, resolvedPath);
        var separator = NodePath.Separator;
        var isInsideCwd =
            relativePath.Length == 0 ||
            (relativePath != ".." &&
             !relativePath.StartsWith($"..{separator}", StringComparison.Ordinal) &&
             !NodePath.IsAbsolute(relativePath));

        return isInsideCwd ? (relativePath.Length == 0 ? "." : relativePath) : null;
    }

    /// <summary>A cwd-relative path with forward slashes, falling back to the absolute path.</summary>
    public static string FormatPathRelativeToCwdOrAbsolute(string filePath, string cwd)
    {
        var absolutePath = ResolvePath(filePath, cwd);
        return (GetCwdRelativePath(absolutePath, cwd) ?? absolutePath)
            .Replace(NodePath.Separator, '/');
    }

    /// <summary>
    /// Mark a path so cloud-sync clients skip it, using the extended attributes Dropbox and the macOS
    /// file provider understand. A no-op on platforms with no such attribute.
    /// </summary>
    public static void MarkPathIgnoredByCloudSync(string path)
    {
        var platform = ProcessInfo.Platform;
        var attributes = platform switch
        {
            "darwin" => new[] { "com.dropbox.ignored", "com.apple.fileprovider.ignore#P" },
            "linux" => ["user.com.dropbox.ignored"],
            _ => [],
        };

        foreach (var attribute in attributes)
        {
            var (command, args) = platform == "darwin"
                ? ("xattr", new[] { "-w", attribute, "1", path })
                : ("setfattr", ["-n", attribute, "-v", "1", path]);
            _ = ChildProcess.SpawnSync(command, args, new SpawnSyncOptions { Encoding = "utf-8", Stdio = [StdioMode.Ignore] });
        }
    }

    /// <summary>The user's home directory, matching Node's <c>os.homedir()</c> for the common cases.</summary>
    internal static string GetHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// Node's <c>url.fileURLToPath</c>, which <c>normalizePath</c> calls for any <c>file://</c> input.
    /// </summary>
    /// <remarks>
    /// This throws for the same inputs Node throws for — a non-drive-absolute pathname on Windows, a
    /// non-empty host on POSIX, an encoded path separator, or malformed percent-encoding. See
    /// <see cref="FileUrl"/> for the implemented subset of the WHATWG <c>file:</c> URL grammar.
    /// </remarks>
    internal static string FileUrlToPath(string url) => FileUrl.ToPath(url);
}
