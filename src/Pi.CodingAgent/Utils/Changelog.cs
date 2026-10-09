using System.Text;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>A parsed <c>CHANGELOG.md</c> section: the version plus the raw markdown that followed it.</summary>
public sealed record ChangelogEntry(int Major, int Minor, int Patch, string Content);

/// <summary>
/// Port of <c>utils/changelog.ts</c>: parse <c>CHANGELOG.md</c> into versioned sections, and rewrite the
/// relative links inside a release note so they point at the right tag on GitHub.
/// </summary>
/// <remarks>
/// <c>getChangelogPath</c> is re-exported from <c>config.ts</c> in TypeScript; here it lives on
/// <see cref="Config"/> and is not duplicated.
/// </remarks>
public static class Changelog
{
    private const string GitHubRepo = "earendil-works/pi";
    private const string ChangelogLinkBasePath = "packages/coding-agent";

    /// <summary>Links that still point at the pre-rename repository, matched without the <c>/g</c> flag.</summary>
    private static readonly Regex LegacyRepoRegex =
        new(@"^https://github\.com/(?:badlogic|earendil-works)/pi-mono(?=/|$)", RegexOptions.Compiled);

    /// <summary>Any string that starts with a URL scheme, e.g. <c>https:</c> or <c>mailto:</c>.</summary>
    private static readonly Regex UrlSchemeRegex =
        new("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>An inline markdown link or image: <c>[label](target)</c> / <c>![alt](target)</c>.</summary>
    private static readonly Regex InlineMarkdownLinkRegex = new(
        $"(!?\\[[^\\]\\n]+\\]\\()([^{JsRegex.WhitespaceBody})]+)((?:{JsRegex.WhitespaceClass}+[^)]*)?\\))",
        RegexOptions.Compiled);

    private static readonly Regex VersionHeaderRegex =
        new(@"##\s+\[?(\d+)\.(\d+)\.(\d+)\]?", RegexOptions.Compiled);

    /// <summary>
    /// Where <c>parseChangelog</c> reports an unreadable file. The TypeScript writes to
    /// <c>console.error</c>; routing it through a writer keeps tests off the process-wide console.
    /// </summary>
    public static TextWriter ErrorWriter { get; set; } = Console.Error;

    private static string EntryVersion(ChangelogEntry entry) => $"{entry.Major}.{entry.Minor}.{entry.Patch}";

    /// <summary>Prefix a version with <c>v</c> unless it already carries one.</summary>
    public static string NormalizeTag(string version) => version.StartsWith('v') ? version : "v" + version;

    /// <summary>Prefix an entry's <c>major.minor.patch</c> with <c>v</c> unless it already carries one.</summary>
    public static string NormalizeTag(ChangelogEntry entry) => NormalizeTag(EntryVersion(entry));

    /// <summary>
    /// Rewrite every inline markdown link in <paramref name="markdown"/> that points at a file in this
    /// repository so it resolves against <paramref name="version"/>'s tag.
    /// </summary>
    public static string NormalizeChangelogLinks(string markdown, string version) =>
        RewriteLinks(markdown, NormalizeTag(version));

    /// <summary>As <see cref="NormalizeChangelogLinks(string, string)"/>, with the tag taken from an entry.</summary>
    public static string NormalizeChangelogLinks(string markdown, ChangelogEntry version) =>
        RewriteLinks(markdown, NormalizeTag(version));

    private static string RewriteLinks(string markdown, string tag) =>
        InlineMarkdownLinkRegex.Replace(
            markdown,
            match => match.Groups[1].Value
                + NormalizeChangelogLinkTarget(match.Groups[2].Value, tag)
                + match.Groups[3].Value);

    /// <summary>
    /// Canonicalise one link target: repoint the old repository, float the branch to the tag, and expand
    /// a repository-relative path into an absolute <c>blob</c>/<c>tree</c> URL.
    /// </summary>
    public static string NormalizeChangelogLinkTarget(string target, string tag)
    {
        var canonicalTarget = LegacyRepoRegex.Replace(target, $"https://github.com/{GitHubRepo}");
        var repoUrl = $"https://github.com/{GitHubRepo}";

        foreach (var route in (string[])["blob", "tree"])
        {
            foreach (var branch in (string[])["main", "master"])
            {
                var floatingRefPrefix = $"{repoUrl}/{route}/{branch}/";
                if (canonicalTarget.StartsWith(floatingRefPrefix, StringComparison.Ordinal))
                {
                    canonicalTarget = $"{repoUrl}/{route}/{tag}/{canonicalTarget[floatingRefPrefix.Length..]}";
                }
            }
        }

        if (canonicalTarget.StartsWith('#') ||
            canonicalTarget.StartsWith("//", StringComparison.Ordinal) ||
            UrlSchemeRegex.IsMatch(canonicalTarget))
        {
            return canonicalTarget;
        }

        var (fragment, pathPart, query) = SplitLocalTarget(canonicalTarget);
        if (pathPart.Length == 0)
        {
            return canonicalTarget;
        }

        var repositoryPath = ResolveRepositoryPath(pathPart);
        if (repositoryPath is null)
        {
            return canonicalTarget;
        }

        var targetRoute = IsDirectoryTarget(pathPart, repositoryPath) ? "tree" : "blob";
        return $"https://github.com/{GitHubRepo}/{targetRoute}/{tag}/{JsUri.EncodeUri(repositoryPath)}{query}{fragment}";
    }

    private static (string Fragment, string PathPart, string Query) SplitLocalTarget(string target)
    {
        var hashIndex = target.IndexOf('#');
        var beforeHash = hashIndex == -1 ? target : target[..hashIndex];
        var fragment = hashIndex == -1 ? string.Empty : target[hashIndex..];
        var queryIndex = beforeHash.IndexOf('?');

        return queryIndex == -1
            ? (fragment, beforeHash, string.Empty)
            : (fragment, beforeHash[..queryIndex], beforeHash[queryIndex..]);
    }

    private static string NormalizePathPart(string value) => value.Replace('\\', '/');

    /// <summary>
    /// Map a link target onto a path inside the repository, or <see langword="null"/> when it escapes the
    /// repository root.
    /// </summary>
    public static string? ResolveRepositoryPath(string targetPath)
    {
        var normalizedTarget = NormalizePathPart(targetPath);
        var joined = normalizedTarget.StartsWith('/')
            ? NodePath.Normalize(normalizedTarget.TrimStart('/'), windows: false)
            : NodePath.Normalize(NodePath.Join(false, ChangelogLinkBasePath, normalizedTarget), windows: false);

        if (joined == "." || joined.StartsWith("../", StringComparison.Ordinal) || joined == "..")
        {
            return null;
        }

        return joined;
    }

    private static bool IsDirectoryTarget(string originalPath, string repositoryPath)
    {
        if (originalPath.EndsWith('/'))
        {
            return true;
        }

        var basename = NodePath.Basename(repositoryPath, null, windows: false);
        return !basename.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Parse the versioned sections of a changelog file. A section starts at a <c>##</c> heading that
    /// carries a semantic version and runs until the next heading; a <c>##</c> heading without a version
    /// ends the previous section and starts nothing.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> ParseChangelog(string changelogPath)
    {
        // Node's existsSync answers for any file-system entry, so a directory passes the guard and then
        // makes readFileSync throw. File.Exists alone would report false for a directory on Windows and
        // silently skip the catch branch, so the directory check is needed for the same behaviour.
        if (!File.Exists(changelogPath) && !Directory.Exists(changelogPath))
        {
            return [];
        }

        try
        {
            var content = File.ReadAllText(changelogPath, Encoding.UTF8);
            var entries = new List<ChangelogEntry>();

            var currentLines = new List<string>();
            (int Major, int Minor, int Patch)? currentVersion = null;

            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush(entries, currentVersion, currentLines);

                    var versionMatch = VersionHeaderRegex.Match(line);
                    if (versionMatch.Success)
                    {
                        currentVersion = (
                            int.Parse(versionMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(versionMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(versionMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                        currentLines = [line];
                    }
                    else
                    {
                        currentVersion = null;
                        currentLines = [];
                    }
                }
                else if (currentVersion is not null)
                {
                    currentLines.Add(line);
                }
            }

            Flush(entries, currentVersion, currentLines);
            return entries;
        }
        catch (Exception error)
        {
            // JavaScript catches everything here. The message text cannot match: `${error}` renders a
            // JavaScript error as "name: message", and there is no JavaScript error in the port.
            ErrorWriter.WriteLine($"Warning: Could not parse changelog: {error}");
            return [];
        }
    }

    private static void Flush(
        List<ChangelogEntry> entries,
        (int Major, int Minor, int Patch)? version,
        List<string> lines)
    {
        if (version is not { } current || lines.Count == 0)
        {
            return;
        }

        entries.Add(new ChangelogEntry(current.Major, current.Minor, current.Patch, string.Join('\n', lines).Trim()));
    }

    /// <summary>
    /// Compare versions: negative when <paramref name="v1"/> is older, zero when equal, positive when newer.
    /// </summary>
    public static int CompareVersions(ChangelogEntry v1, ChangelogEntry v2)
    {
        if (v1.Major != v2.Major)
        {
            return v1.Major - v2.Major;
        }

        return v1.Minor != v2.Minor ? v1.Minor - v2.Minor : v1.Patch - v2.Patch;
    }

    /// <summary>
    /// The entries newer than <paramref name="lastVersion"/>. A malformed version contributes whatever
    /// leading numbers it has and zeroes for the rest, which is what <c>split(".").map(Number)</c> yields.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> GetNewEntries(IReadOnlyList<ChangelogEntry> entries, string lastVersion)
    {
        var parts = lastVersion.Split('.');
        var last = new ChangelogEntry(
            ParsePart(parts, 0),
            ParsePart(parts, 1),
            ParsePart(parts, 2),
            string.Empty);

        return [.. entries.Where(entry => CompareVersions(entry, last) > 0)];
    }

    private static int ParsePart(string[] parts, int index) =>
        index >= parts.Length ? 0 : ToVersionNumber(parts[index]);

    /// <summary>
    /// <c>Number(part) || 0</c> from the original. <c>Number</c> is not <c>parseInt</c>: it accepts a
    /// decimal point, an exponent, <c>0x</c>/<c>0b</c>/<c>0o</c> prefixes and the infinities, and it
    /// yields <c>NaN</c> for anything else. Both <c>NaN</c> and <c>0</c> collapse to <c>0</c>.
    /// </summary>
    private static int ToVersionNumber(string part)
    {
        var trimmed = part.Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        double value;
        if (trimmed is "Infinity" or "+Infinity" or "-Infinity")
        {
            // Both infinities are truthy, and the caller only ever compares the result.
            value = trimmed == "-Infinity" ? double.NegativeInfinity : double.PositiveInfinity;
        }
        else if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            value = double.NaN;
        }

        return double.IsNaN(value) || value == 0 ? 0 : (int)value;
    }
}
