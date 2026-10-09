using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>The pieces <c>hosted-git-info</c>'s per-host <c>extract</c> pulls out of a URL.</summary>
internal sealed record HostedGitSegments(string? User, string Project, string Committish);

/// <summary>A resolved hosted repository, as far as <c>utils/git.ts</c> needs it.</summary>
internal sealed record HostedGitInfo(
    string Type,
    string Domain,
    string? User,
    string? Project,
    string? Committish,
    string? Default);

/// <summary>
/// Port of <c>hosted-git-info@8.1.0</c>'s <c>fromUrl</c>, for the hosts this codebase can encounter:
/// github, gitlab, bitbucket, gist and sourcehut.
/// </summary>
/// <remarks>
/// <para>
/// Only the recognition half of the library is ported — the per-host tables (<c>domain</c>,
/// <c>protocols</c>, <c>extract</c>) and the URL parser. The URL <em>formatting</em> half
/// (<c>ssh()</c>, <c>https()</c>, <c>browse()</c>, <c>tarball()</c>, and the dozen templates behind them)
/// is not: <c>git.ts</c> reads only <c>domain</c>, <c>user</c>, <c>project</c> and <c>committish</c>, and
/// nothing else in the repository imports the package.
/// </para>
/// <para>
/// The library's 1000-entry LRU cache is also omitted. It is pure memoisation, so the only observable
/// difference would be reference identity of the returned object, and callers here read fields.
/// </para>
/// <para>
/// The library also computes an <c>auth</c> component (the userinfo of an authenticating scheme) and
/// returns it alongside the rest. Nothing in this codebase reads it, so it is not surfaced here.
/// </para>
/// </remarks>
internal static class HostedGit
{
    private sealed record HostDefinition(string Domain, string[] Protocols, Func<JsUrlValue, HostedGitSegments?> Extract);

    private static readonly Dictionary<string, HostDefinition> Hosts = new(StringComparer.Ordinal)
    {
        ["github"] = new(
            "github.com",
            ["git:", "http:", "git+ssh:", "git+https:", "ssh:", "https:"],
            static url =>
            {
                var segments = JsUrl.Split(url.Pathname, ['/']);
                var user = segments.Count > 1 ? segments[1] : null;
                var project = segments.Count > 2 ? segments[2] : null;
                var type = segments.Count > 3 ? segments[3] : null;
                var committish = segments.Count > 4 ? segments[4] : null;

                if (!string.IsNullOrEmpty(type) && type != "tree")
                {
                    return null;
                }

                if (string.IsNullOrEmpty(type))
                {
                    committish = url.Hash.Length > 0 ? url.Hash[1..] : string.Empty;
                }

                if (project is not null && project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)
                    ? null
                    : new HostedGitSegments(user, project, committish ?? string.Empty);
            }),
        ["bitbucket"] = new(
            "bitbucket.org",
            ["git+ssh:", "git+https:", "ssh:", "https:"],
            static url =>
            {
                var segments = JsUrl.Split(url.Pathname, ['/']);
                var user = segments.Count > 1 ? segments[1] : null;
                var project = segments.Count > 2 ? segments[2] : null;
                var aux = segments.Count > 3 ? segments[3] : null;

                if (aux == "get")
                {
                    return null;
                }

                if (project is not null && project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)
                    ? null
                    : new HostedGitSegments(user, project, url.Hash.Length > 0 ? url.Hash[1..] : string.Empty);
            }),
        ["gitlab"] = new(
            "gitlab.com",
            ["git+ssh:", "git+https:", "ssh:", "https:"],
            static url =>
            {
                var path = url.Pathname.Length > 0 ? url.Pathname[1..] : string.Empty;
                if (path.Contains("/-/", StringComparison.Ordinal) || path.Contains("/archive.tar.gz", StringComparison.Ordinal))
                {
                    return null;
                }

                var segments = JsUrl.Split(path, ['/']);
                var project = segments[^1];
                segments.RemoveAt(segments.Count - 1);
                if (project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                var user = string.Join('/', segments);
                return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)
                    ? null
                    : new HostedGitSegments(user, project, url.Hash.Length > 0 ? url.Hash[1..] : string.Empty);
            }),
        ["gist"] = new(
            "gist.github.com",
            ["git:", "git+ssh:", "git+https:", "ssh:", "https:"],
            static url =>
            {
                var segments = JsUrl.Split(url.Pathname, ['/']);
                var user = segments.Count > 1 ? segments[1] : null;
                var project = segments.Count > 2 ? segments[2] : null;
                var aux = segments.Count > 3 ? segments[3] : null;

                if (aux == "raw")
                {
                    return null;
                }

                if (string.IsNullOrEmpty(project))
                {
                    if (string.IsNullOrEmpty(user))
                    {
                        return null;
                    }

                    project = user;
                    user = null;
                }

                if (project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                return new HostedGitSegments(user, project, url.Hash.Length > 0 ? url.Hash[1..] : string.Empty);
            }),
        ["sourcehut"] = new(
            "git.sr.ht",
            ["git+ssh:", "https:"],
            static url =>
            {
                var segments = JsUrl.Split(url.Pathname, ['/']);
                var user = segments.Count > 1 ? segments[1] : null;
                var project = segments.Count > 2 ? segments[2] : null;
                var aux = segments.Count > 3 ? segments[3] : null;

                if (aux == "archive")
                {
                    return null;
                }

                if (project is not null && project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)
                    ? null
                    : new HostedGitSegments(user, project, url.Hash.Length > 0 ? url.Hash[1..] : string.Empty);
            }),
    };

    /// <summary>Scheme to host name, e.g. <c>github:</c> to <c>github</c>.</summary>
    private static readonly Dictionary<string, string> ByShortcut = new(StringComparer.Ordinal)
    {
        ["github:"] = "github",
        ["bitbucket:"] = "bitbucket",
        ["gitlab:"] = "gitlab",
        ["gist:"] = "gist",
        ["sourcehut:"] = "sourcehut",
    };

    private static readonly Dictionary<string, string> ByDomain = new(StringComparer.Ordinal)
    {
        ["github.com"] = "github",
        ["bitbucket.org"] = "bitbucket",
        ["gitlab.com"] = "gitlab",
        ["gist.github.com"] = "gist",
        ["git.sr.ht"] = "sourcehut",
    };

    /// <summary>
    /// The schemes the library knows, mapped to the representation name <c>fromUrl</c> reports as the
    /// default. A <see langword="null"/> value means the scheme has no named representation, so the
    /// default is the scheme itself without its colon.
    /// </summary>
    /// <remarks>
    /// The library also records an <c>auth</c> flag per scheme, used only to build the resolved URL's
    /// userinfo. Since <c>auth</c> is not surfaced here, the flag is dropped.
    /// </remarks>
    private static readonly Dictionary<string, string?> Protocols = new(StringComparer.Ordinal)
    {
        ["git+ssh:"] = "sshurl",
        ["ssh:"] = "sshurl",
        ["git+https:"] = "https",
        ["git:"] = null,
        ["http:"] = null,
        ["https:"] = null,
        ["git+http:"] = null,
        ["github:"] = "github",
        ["bitbucket:"] = "bitbucket",
        ["gitlab:"] = "gitlab",
        ["gist:"] = "gist",
        ["sourcehut:"] = "sourcehut",
    };

    /// <summary>
    /// Resolve <paramref name="url"/> to a hosted repository, or <see langword="null"/> when it does not
    /// name one of the known hosts.
    /// </summary>
    internal static HostedGitInfo? FromUrl(string url)
    {
        // `if (!giturl) return` in the original: the empty string is falsy.
        if (url.Length == 0)
        {
            return null;
        }

        var parsed = ParseUrl(IsGitHubShorthand(url) ? "github:" + url : url);
        if (parsed is null)
        {
            return null;
        }

        ByShortcut.TryGetValue(parsed.Protocol, out var shortcut);
        var hostname = parsed.Hostname.StartsWith("www.", StringComparison.Ordinal) ? parsed.Hostname[4..] : parsed.Hostname;
        ByDomain.TryGetValue(hostname, out var domainHost);
        var name = shortcut ?? domainHost;
        if (name is null)
        {
            return null;
        }

        var host = Hosts[name];
        var representation = Protocols.GetValueOrDefault(parsed.Protocol);

        string? user;
        string project;
        string? committish;
        string? defaultRepresentation;

        try
        {
            if (shortcut is not null)
            {
                var pathname = parsed.Pathname.StartsWith('/') ? parsed.Pathname[1..] : parsed.Pathname;
                var firstAt = pathname.IndexOf('@');
                if (firstAt > -1)
                {
                    // Auth is ignored for a shortcut, so it is trimmed off rather than carried.
                    pathname = pathname[(firstAt + 1)..];
                }

                var lastSlash = pathname.LastIndexOf('/');
                if (lastSlash > -1)
                {
                    var candidate = JsUri.DecodeUriComponent(pathname[..lastSlash]);
                    user = candidate.Length == 0 ? null : candidate;
                    project = JsUri.DecodeUriComponent(pathname[(lastSlash + 1)..]);
                }
                else
                {
                    user = null;
                    project = JsUri.DecodeUriComponent(pathname);
                }

                if (project.EndsWith(".git", StringComparison.Ordinal))
                {
                    project = project[..^4];
                }

                committish = parsed.Hash.Length > 0 ? JsUri.DecodeUriComponent(parsed.Hash[1..]) : null;
                defaultRepresentation = "shortcut";
            }
            else
            {
                if (!host.Protocols.Contains(parsed.Protocol))
                {
                    return null;
                }

                var segments = host.Extract(parsed);
                if (segments is null)
                {
                    return null;
                }

                user = segments.User is null ? null : JsUri.DecodeUriComponent(segments.User);
                project = JsUri.DecodeUriComponent(segments.Project);
                committish = JsUri.DecodeUriComponent(segments.Committish);
                defaultRepresentation = representation ?? parsed.Protocol[..^1];
            }
        }
        catch (UriFormatException)
        {
            // The original rethrows anything that is not a URIError; a malformed escape is the only thing
            // that can throw here.
            return null;
        }

        return new HostedGitInfo(name, host.Domain, user, project, committish, defaultRepresentation);
    }

    /// <summary>
    /// <c>parse-url</c>: try the URL as written, and if that fails, repair an scp-style URL first.
    /// </summary>
    private static JsUrlValue? ParseUrl(string giturl)
    {
        var withProtocol = CorrectProtocol(giturl);
        return JsUrl.TryParse(withProtocol) ?? JsUrl.TryParse(CorrectUrl(withProtocol));
    }

    /// <summary>Insert a missing <c>//</c> after the scheme, unless the input is already usable.</summary>
    private static string CorrectProtocol(string arg)
    {
        var firstColon = arg.IndexOf(':');
        var proto = arg[..(firstColon + 1)];
        if (Protocols.ContainsKey(proto))
        {
            return arg;
        }

        var firstAt = arg.IndexOf('@');
        if (firstAt > -1)
        {
            // `git@host:path` is scp syntax: only a scheme-less authority needs the prefix.
            return firstAt > firstColon ? "git+ssh://" + arg : arg;
        }

        if (arg.IndexOf("//", StringComparison.Ordinal) == firstColon + 1)
        {
            return arg;
        }

        return string.Concat(arg.AsSpan(0, firstColon + 1), "//", arg.AsSpan(firstColon + 1));
    }

    /// <summary>
    /// Rewrite an scp-style URL so that <c>new URL()</c> can parse it: the colon between host and path
    /// becomes a slash, and a scheme is added when there is none.
    /// </summary>
    private static string CorrectUrl(string giturl)
    {
        // An '@' or ':' after the first '#' belongs to the committish, which may contain both, so only the
        // part before the hash is considered.
        var firstAt = LastIndexOfBefore(giturl, '@', '#');
        var lastColonBeforeHash = LastIndexOfBefore(giturl, ':', '#');

        if (lastColonBeforeHash > firstAt)
        {
            giturl = string.Concat(giturl.AsSpan(0, lastColonBeforeHash), "/", giturl.AsSpan(lastColonBeforeHash + 1));
        }

        if (LastIndexOfBefore(giturl, ':', '#') == -1 && giturl.IndexOf("//", StringComparison.Ordinal) == -1)
        {
            giturl = "git+ssh://" + giturl;
        }

        return giturl;
    }

    /// <summary>JavaScript's <c>lastIndexOf(ch, str.indexOf(before))</c>, with the <c>Infinity</c> case.</summary>
    private static int LastIndexOfBefore(string value, char character, char before)
    {
        if (value.Length == 0)
        {
            return -1;
        }

        var startPosition = value.IndexOf(before);
        return value.LastIndexOf(character, startPosition > -1 ? startPosition : value.Length - 1);
    }

    /// <summary>
    /// Whether the argument is a bare <c>user/repo</c> that should be read as a GitHub shorthand.
    /// </summary>
    /// <remarks>
    /// The tests are about what must <em>not</em> be in the string before the <c>#</c>: a space, an
    /// <c>@</c>, a <c>:</c> or a second slash all suggest a protocol, a scoped package or a path, and a
    /// leading dot or trailing slash suggests a file path.
    /// </remarks>
    private static bool IsGitHubShorthand(string arg)
    {
        var firstHash = arg.IndexOf('#');
        var firstSlash = arg.IndexOf('/');
        var secondSlash = firstSlash < 0 ? -1 : arg.IndexOf('/', firstSlash + 1);
        var firstColon = arg.IndexOf(':');
        var firstAt = arg.IndexOf('@');

        var firstSpace = -1;
        for (var index = 0; index < arg.Length; index++)
        {
            if (JsString.IsWhitespace(arg[index]))
            {
                firstSpace = index;
                break;
            }
        }

        var spaceOnlyAfterHash = firstSpace < 0 || (firstHash > -1 && firstSpace > firstHash);
        var atOnlyAfterHash = firstAt == -1 || (firstHash > -1 && firstAt > firstHash);
        var colonOnlyAfterHash = firstColon == -1 || (firstHash > -1 && firstColon > firstHash);
        var secondSlashOnlyAfterHash = secondSlash == -1 || (firstHash > -1 && secondSlash > firstHash);
        var hasSlash = firstSlash > 0;
        var doesNotEndWithSlash = firstHash > -1 ? arg[firstHash - 1] != '/' : !arg.EndsWith('/');
        var doesNotStartWithDot = !arg.StartsWith('.');

        return spaceOnlyAfterHash
            && hasSlash
            && doesNotEndWithSlash
            && doesNotStartWithDot
            && atOnlyAfterHash
            && colonOnlyAfterHash
            && secondSlashOnlyAfterHash;
    }
}
