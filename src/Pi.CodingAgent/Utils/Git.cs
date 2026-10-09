using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>Parsed git URL information.</summary>
/// <param name="Type">Always <c>"git"</c> for git sources.</param>
/// <param name="Repo">Clone URL, always valid for <c>git clone</c> and without a ref suffix.</param>
/// <param name="Host">Git host domain, e.g. <c>github.com</c>.</param>
/// <param name="Path">Repository path, e.g. <c>user/repo</c>.</param>
/// <param name="Ref">Git ref (branch, tag, commit) when one was specified.</param>
/// <param name="Pinned">Whether a ref was specified, in which case the package is not auto-updated.</param>
public sealed record GitSource(
    string Type,
    string Repo,
    string Host,
    string Path,
    string? Ref,
    bool Pinned);

/// <summary>
/// Port of <c>utils/git.ts</c>: read a user-supplied source string as a git repository.
/// </summary>
/// <remarks>
/// <para>
/// With a <c>git:</c> prefix every historical shorthand is accepted; without one only an explicit
/// protocol URL is. The recognition itself is delegated to <see cref="HostedGit"/>, the port of
/// <c>hosted-git-info</c>.
/// </para>
/// <para>
/// JavaScript's <c>String.prototype.trim</c> is used rather than <see cref="string.Trim()"/> because the
/// two disagree on U+FEFF and U+0085; see <see cref="JsString"/>.
/// </para>
/// </remarks>
public static class Git
{
    /// <summary>JavaScript's <c>.</c>, which excludes every line terminator rather than just <c>\n</c>.</summary>
    private const string JsDot = "[^\n\r\u2028\u2029]";

    /// <summary>The scp-like form <c>git@host:path</c>, used both to split a ref and to read the host.</summary>
    private static readonly Regex ScpLikeRegex = new(
        $"^git@([^:]+):({JsDot}+)\\z",
        RegexOptions.Compiled);

    /// <summary>An explicit protocol — the only form accepted without a <c>git:</c> prefix.</summary>
    private static readonly Regex ExplicitProtocolRegex = new(
        @"^(https?|ssh|git)://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parse a git source into a <see cref="GitSource"/>, or <see langword="null"/> when it is not one.
    /// </summary>
    /// <remarks>
    /// Rules: with a <c>git:</c> prefix, accept all historical shorthand forms; without it, only accept
    /// explicit protocol URLs.
    /// </remarks>
    public static GitSource? ParseGitUrl(string source)
    {
        var trimmed = JsString.Trim(source);
        var hasGitPrefix = trimmed.StartsWith("git:", StringComparison.Ordinal);
        var url = hasGitPrefix ? JsString.Trim(trimmed[4..]) : trimmed;

        if (!hasGitPrefix && !ExplicitProtocolRegex.IsMatch(url))
        {
            return null;
        }

        var (repo, reference) = SplitRef(url);

        // The ref is folded back into a fragment so that `hosted-git-info` sees it as a committish.
        foreach (var candidate in HostedCandidates(repo, reference, url))
        {
            var info = HostedGit.FromUrl(candidate);
            if (info is null)
            {
                continue;
            }

            if (reference is not null && info.Project is not null && info.Project.Contains('@'))
            {
                // An `@` in the project means the ref was really part of the path, not a ref.
                continue;
            }

            var useHttpsPrefix = !repo.StartsWith("http://", StringComparison.Ordinal)
                && !repo.StartsWith("https://", StringComparison.Ordinal)
                && !repo.StartsWith("ssh://", StringComparison.Ordinal)
                && !repo.StartsWith("git://", StringComparison.Ordinal)
                && !repo.StartsWith("git@", StringComparison.Ordinal);

            return BuildGitSource(
                useHttpsPrefix ? "https://" + repo : repo,
                info.Domain,
                Path(info),
                FirstNonEmpty(info.Committish, reference));
        }

        foreach (var candidate in HttpsCandidates(repo, reference, url))
        {
            var info = HostedGit.FromUrl(candidate);
            if (info is null)
            {
                continue;
            }

            if (reference is not null && info.Project is not null && info.Project.Contains('@'))
            {
                continue;
            }

            return BuildGitSource(
                "https://" + repo,
                info.Domain,
                Path(info),
                FirstNonEmpty(info.Committish, reference));
        }

        return ParseGenericGitUrl(url);
    }

    /// <summary>
    /// The candidates tried as written: the ref-carrying form first (when there is a ref), then the
    /// original URL. The original's <c>.filter(Boolean)</c> drops an empty <paramref name="url"/>.
    /// </summary>
    private static List<string> HostedCandidates(string repo, string? reference, string url)
    {
        var candidates = new List<string>(2);
        if (reference is not null)
        {
            candidates.Add(repo + "#" + reference);
        }

        if (url.Length > 0)
        {
            candidates.Add(url);
        }

        return candidates;
    }

    /// <summary>The candidates tried with an <c>https://</c> prefix, for a bare host path.</summary>
    private static List<string> HttpsCandidates(string repo, string? reference, string url)
    {
        var candidates = new List<string>(2);
        if (reference is not null)
        {
            candidates.Add("https://" + repo + "#" + reference);
        }

        candidates.Add("https://" + url);
        return candidates;
    }

    /// <summary>
    /// <c>`${info.user}/${info.project}`</c>. A missing user stringifies to <c>null</c> in the original,
    /// because a template literal calls <c>String()</c> on it.
    /// </summary>
    private static string Path(HostedGitInfo info) =>
        (info.User ?? "null") + "/" + info.Project;

    /// <summary>JavaScript's <c>a || b || undefined</c>: the first truthy value, where <c>""</c> is falsy.</summary>
    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrEmpty(first))
        {
            return first;
        }

        return string.IsNullOrEmpty(second) ? null : second;
    }

    /// <summary>
    /// Split a trailing <c>@ref</c> off a URL, in whichever of the three shapes it arrives: scp-like,
    /// protocol URL, or a bare <c>host/path</c>.
    /// </summary>
    private static (string Repo, string? Ref) SplitRef(string url)
    {
        var scpLike = ScpLikeRegex.Match(url);
        if (scpLike.Success)
        {
            return SplitAt(scpLike.Groups[2].Value, url, $"git@{scpLike.Groups[1].Value}:");
        }

        if (url.Contains("://", StringComparison.Ordinal))
        {
            var parsed = JsUrl.TryParse(url);
            if (parsed is null)
            {
                return (url, null);
            }

            var pathWithMaybeRef = parsed.Pathname.TrimStart('/');
            var separator = pathWithMaybeRef.IndexOf('@');
            if (separator < 0)
            {
                return (url, null);
            }

            var repoPath = pathWithMaybeRef[..separator];
            var reference = pathWithMaybeRef[(separator + 1)..];
            if (repoPath.Length == 0 || reference.Length == 0)
            {
                return (url, null);
            }

            // The original assigns to `parsed.pathname` and re-serialises the whole URL, which is how the
            // ref disappears from the clone URL without disturbing the rest of it.
            var reserialized = JsUrl.Serialize(parsed with { Pathname = "/" + repoPath });

            // `.replace(/\/$/, "")` removes a single trailing slash, not a run of them.
            return (reserialized.EndsWith('/') ? reserialized[..^1] : reserialized, reference);
        }

        var slashIndex = url.IndexOf('/');
        if (slashIndex < 0)
        {
            return (url, null);
        }

        return SplitAt(url[(slashIndex + 1)..], url, url[..slashIndex] + "/");
    }

    /// <summary>
    /// The common tail of <see cref="SplitRef"/>: split <paramref name="pathWithMaybeRef"/> at its first
    /// <c>@</c> and rebuild the clone URL from <paramref name="prefix"/> plus the part before it.
    /// </summary>
    private static (string Repo, string? Ref) SplitAt(string pathWithMaybeRef, string url, string prefix)
    {
        var separator = pathWithMaybeRef.IndexOf('@');
        if (separator < 0)
        {
            return (url, null);
        }

        var repoPath = pathWithMaybeRef[..separator];
        var reference = pathWithMaybeRef[(separator + 1)..];
        if (repoPath.Length == 0 || reference.Length == 0)
        {
            return (url, null);
        }

        return (prefix + repoPath, reference);
    }

    /// <summary>
    /// <c>decodeURIComponent</c> where a malformed escape means "not a valid value" rather than an error.
    /// </summary>
    private static string? DecodeForValidation(string value)
    {
        try
        {
            return JsUri.DecodeUriComponent(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a part of a git install target could escape the directory it is meant to name. Both the
    /// raw and the decoded form are checked, so an encoded traversal is caught as well as a literal one.
    /// </summary>
    private static bool HasUnsafeGitInstallPart(string value, bool allowSlash)
    {
        var decoded = DecodeForValidation(value);
        if (decoded is null)
        {
            return true;
        }

        foreach (var candidate in (ReadOnlySpan<string>)[value, decoded])
        {
            if (candidate.Contains('\0') || candidate.Contains('\\') || candidate.StartsWith('/'))
            {
                return true;
            }

            if (!allowSlash && candidate.Contains('/'))
            {
                return true;
            }

            if (Array.IndexOf(candidate.Split('/'), "..") >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Assemble a <see cref="GitSource"/>, rejecting anything whose host or path could not be a safe
    /// repository target.
    /// </summary>
    private static GitSource? BuildGitSource(string repo, string host, string path, string? reference)
    {
        if (path.StartsWith('/'))
        {
            return null;
        }

        var normalizedPath = (path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path).TrimStart('/');
        if (host.Length == 0 || normalizedPath.Length == 0 || normalizedPath.Split('/').Length < 2)
        {
            return null;
        }

        if (HasUnsafeGitInstallPart(host, false) || HasUnsafeGitInstallPart(normalizedPath, true))
        {
            return null;
        }

        return new GitSource("git", repo, host, normalizedPath, reference, !string.IsNullOrEmpty(reference));
    }

    /// <summary>
    /// The fallback for a URL that names no known host: read the host and path structurally, requiring at
    /// least two path segments.
    /// </summary>
    private static GitSource? ParseGenericGitUrl(string url)
    {
        var (repoWithoutRef, reference) = SplitRef(url);
        var repo = repoWithoutRef;
        string host;
        string path;

        var scpLike = ScpLikeRegex.Match(repoWithoutRef);
        if (scpLike.Success)
        {
            host = scpLike.Groups[1].Value;
            path = scpLike.Groups[2].Value;
        }
        else if (repoWithoutRef.StartsWith("https://", StringComparison.Ordinal)
            || repoWithoutRef.StartsWith("http://", StringComparison.Ordinal)
            || repoWithoutRef.StartsWith("ssh://", StringComparison.Ordinal)
            || repoWithoutRef.StartsWith("git://", StringComparison.Ordinal))
        {
            var parsed = JsUrl.TryParse(repoWithoutRef);
            if (parsed is null)
            {
                return null;
            }

            host = parsed.Hostname;
            path = parsed.Pathname.TrimStart('/');
        }
        else
        {
            var slashIndex = repoWithoutRef.IndexOf('/');
            if (slashIndex < 0)
            {
                return null;
            }

            host = repoWithoutRef[..slashIndex];
            path = repoWithoutRef[(slashIndex + 1)..];

            // A bare `host/path` only counts when the host looks like a domain; `localhost` is the one
            // name that is accepted without a dot.
            if (!host.Contains('.') && host != "localhost")
            {
                return null;
            }

            repo = "https://" + repoWithoutRef;
        }

        return BuildGitSource(repo, host, path, reference);
    }
}
