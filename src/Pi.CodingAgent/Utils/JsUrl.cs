using System.Buffers;
using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>The URL components <c>hosted-git-info</c> reads, with WHATWG spellings.</summary>
/// <param name="Protocol">The scheme including its trailing colon, lower-cased, e.g. <c>https:</c>.</param>
/// <param name="Username">The userinfo before <c>:</c>, or the empty string.</param>
/// <param name="Password">The userinfo after <c>:</c>, or the empty string.</param>
/// <param name="Hostname">The host, without the port and without IPv6 brackets.</param>
/// <param name="Port">The port digits, or the empty string when there is none.</param>
/// <param name="Pathname">The percent-encoded path. For an opaque path this has no leading slash.</param>
/// <param name="Query">The percent-encoded query <em>including</em> the leading <c>?</c>, or empty.</param>
/// <param name="Hash">The fragment <em>including</em> the leading <c>#</c>, or the empty string.</param>
/// <param name="HasAuthority">
/// Whether the URL has a host at all. False only for an opaque path such as <c>github:user/repo</c>,
/// which is what <see cref="Serialize"/> needs in order to decide on the <c>//</c>.
/// </param>
/// <param name="IsIpv6">Whether <paramref name="Hostname"/> was written in brackets.</param>
internal sealed record JsUrlValue(
    string Protocol,
    string Username,
    string Password,
    string Hostname,
    string Port,
    string Pathname,
    string Query,
    string Hash,
    bool HasAuthority,
    bool IsIpv6);

/// <summary>
/// A subset of the WHATWG URL parser, enough for the URL forms <c>hosted-git-info</c> receives.
/// </summary>
/// <remarks>
/// <para>
/// This is not <see cref="Uri"/>. The differences that matter here are all observable:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A non-special scheme without <c>//</c> gets an <em>opaque</em> path, so <c>github:user/repo</c> has
/// the hostname <c>""</c> and the pathname <c>user/repo</c>; <see cref="Uri"/> rejects it outright.
/// </description></item>
/// <item><description>
/// A special scheme tolerates a missing or partial <c>//</c>, so <c>https:github.com/x</c> still has the
/// host <c>github.com</c>.
/// </description></item>
/// <item><description>
/// Only the scheme is lower-cased for a non-special scheme: the host of <c>ssh://HOST/x</c> stays as
/// written, because a non-special host is an opaque host rather than a domain.
/// </description></item>
/// <item><description>
/// Existing percent-escapes in the path are preserved, which is what lets <c>hosted-git-info</c> decode
/// them itself and raise <c>URIError</c> on a malformed one.
/// </description></item>
/// </list>
/// <para>
/// Not implemented, because nothing in this codebase reaches it: IDNA/domain-to-ASCII (a non-ASCII
/// special-scheme host is accepted as-is rather than punycoded) and the port-number default-port check
/// (so <c>https://h:443/x</c> keeps the explicit <c>:443</c> instead of dropping it).
/// </para>
/// </remarks>
internal static class JsUrl
{
    /// <summary>The schemes the URL spec calls "special".</summary>
    private static readonly HashSet<string> SpecialSchemes = new(StringComparer.Ordinal)
    {
        "ftp:", "file:", "http:", "https:", "ws:", "wss:",
    };

    /// <summary>Host code points a special-scheme host may not contain.</summary>
    private static readonly SearchValues<char> ForbiddenHostCodePoints =
        SearchValues.Create("\0\t\n\r #/:<>?@[\\]^|");

    /// <summary>The path percent-encode set, on top of the C0-control set.</summary>
    private static readonly SearchValues<char> PathEncodeSet = SearchValues.Create(" \"#<>?`{}");

    /// <summary>The fragment percent-encode set, on top of the C0-control set.</summary>
    private static readonly SearchValues<char> FragmentEncodeSet = SearchValues.Create(" \"<>`");

    /// <summary>The query percent-encode set, on top of the C0-control set.</summary>
    private static readonly SearchValues<char> QueryEncodeSet = SearchValues.Create(" \"#<>");

    /// <summary>The characters that end an authority for a special scheme: a path, query or fragment.</summary>
    private static readonly SearchValues<char> SpecialAuthorityTerminators = SearchValues.Create("/\\?#");

    /// <summary>
    /// The characters that end an authority for a non-special scheme. A backslash is <em>not</em> a
    /// separator there, so <c>ssh://HOST\path</c> keeps the backslash inside the host.
    /// </summary>
    private static readonly SearchValues<char> OpaqueAuthorityTerminators = SearchValues.Create("/?#");

    /// <summary>Parse <paramref name="input"/>, or return <see langword="null"/> where <c>new URL()</c> throws.</summary>
    internal static JsUrlValue? TryParse(string input)
    {
        var cleaned = Clean(input);
        var colon = cleaned.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        var scheme = cleaned.AsSpan(0, colon);
        if (!IsScheme(scheme))
        {
            return null;
        }

        var protocol = string.Concat(scheme.ToString().ToLowerInvariant(), ":");
        var special = SpecialSchemes.Contains(protocol);
        var rest = cleaned[(colon + 1)..];

        var hasAuthority = false;
        if (special)
        {
            // "Special authority ignore slashes": a special scheme treats what follows as an authority
            // even when the two slashes are missing or partly there, and it drops <em>every</em> leading
            // slash or backslash rather than just two. That is why `https:////host/x` and `https:host/x`
            // both end up with the host `host`.
            hasAuthority = true;
            rest = rest.TrimStart('/', '\\');
        }
        else if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            // A non-special scheme consumes exactly two slashes, so `git+ssh:///host/x` has an empty host
            // and the path `/host/x`.
            hasAuthority = true;
            rest = rest[2..];
        }

        var username = string.Empty;
        var password = string.Empty;
        var hostname = string.Empty;
        var port = string.Empty;
        var isIpv6 = false;

        if (hasAuthority)
        {
            var end = special
                ? rest.AsSpan().IndexOfAny(SpecialAuthorityTerminators)
                : rest.AsSpan().IndexOfAny(OpaqueAuthorityTerminators);
            var authority = end < 0 ? rest : rest[..end];
            rest = end < 0 ? string.Empty : rest[end..];

            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                var userInfo = authority[..at];
                authority = authority[(at + 1)..];
                var separator = userInfo.IndexOf(':');
                username = separator < 0 ? userInfo : userInfo[..separator];
                password = separator < 0 ? string.Empty : userInfo[(separator + 1)..];
            }

            string host;
            if (authority.StartsWith('['))
            {
                var close = authority.IndexOf(']');
                if (close < 0)
                {
                    return null;
                }

                host = authority[1..close];
                isIpv6 = true;
                if (close + 1 < authority.Length && authority[close + 1] == ':')
                {
                    port = authority[(close + 2)..];
                }
            }
            else
            {
                var portSeparator = authority.LastIndexOf(':');
                host = portSeparator < 0 ? authority : authority[..portSeparator];
                if (portSeparator >= 0)
                {
                    port = authority[(portSeparator + 1)..];
                }
            }

            hostname = NormalizeHost(host, special);
            if (special && (hostname.Length == 0 || hostname.AsSpan().ContainsAny(ForbiddenHostCodePoints)))
            {
                return null;
            }
        }

        var hashIndex = rest.IndexOf('#');
        var beforeHash = hashIndex < 0 ? rest : rest[..hashIndex];
        var fragment = hashIndex < 0 ? string.Empty : rest[(hashIndex + 1)..];

        var queryIndex = beforeHash.IndexOf('?');
        var pathPart = queryIndex < 0 ? beforeHash : beforeHash[..queryIndex];
        var query = queryIndex < 0 ? string.Empty : beforeHash[queryIndex..];

        var pathname = hasAuthority ? BuildPath(pathPart, special) : PercentEncode(pathPart, null);
        if (hasAuthority && pathname.Length == 0 && special)
        {
            pathname = "/";
        }

        return new JsUrlValue(
            protocol,
            username,
            password,
            hostname,
            port,
            pathname,
            query.Length == 0 ? string.Empty : "?" + PercentEncode(query[1..], QueryEncodeSet),
            fragment.Length == 0 ? string.Empty : "#" + PercentEncode(fragment, FragmentEncodeSet),
            hasAuthority,
            isIpv6);
    }

    /// <summary>
    /// The URL serialiser: the inverse of <see cref="TryParse"/> for everything this parser keeps.
    /// </summary>
    /// <remarks>
    /// The userinfo and an opaque host are emitted exactly as parsed rather than re-encoded, because the
    /// parser does not encode them either; for ASCII input the two agree, which covers every caller here.
    /// </remarks>
    internal static string Serialize(JsUrlValue url)
    {
        var builder = new StringBuilder(url.Protocol);
        if (url.HasAuthority)
        {
            builder.Append("//").Append(SerializeAuthority(url));
        }

        return builder.Append(url.Pathname).Append(url.Query).Append(url.Hash).ToString();
    }

    /// <summary>The userinfo, host and port of a URL, i.e. everything after the <c>//</c>.</summary>
    private static string SerializeAuthority(JsUrlValue url)
    {
        var builder = new StringBuilder();
        if (url.Username.Length > 0 || url.Password.Length > 0)
        {
            builder.Append(url.Username);
            if (url.Password.Length > 0)
            {
                builder.Append(':').Append(url.Password);
            }

            builder.Append('@');
        }

        builder.Append(url.IsIpv6 ? "[" + url.Hostname + "]" : url.Hostname);
        if (url.Port.Length > 0)
        {
            builder.Append(':').Append(url.Port);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolve <paramref name="reference"/> against <paramref name="baseUrl"/>, i.e.
    /// <c>new URL(reference, baseUrl)</c>.
    /// </summary>
    /// <remarks>
    /// Only the shapes a redirect <c>Location</c> can take are handled: an absolute URL, a
    /// scheme-relative <c>//host/path</c>, an absolute path, and a path relative to the base directory.
    /// The reference is concatenated with the base and re-parsed, so query, fragment and dot segments
    /// come out of the ordinary parse. Full RFC 3986 base resolution (which would also need to consult
    /// the base's query for a <c>?</c>-only reference) is not implemented, because nothing here needs it.
    /// </remarks>
    internal static JsUrlValue? Resolve(string baseUrl, string reference)
    {
        if (TryParse(reference) is { } absolute)
        {
            return absolute;
        }

        if (TryParse(baseUrl) is not { HasAuthority: true } baseValue)
        {
            return null;
        }

        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            return TryParse(baseValue.Protocol + reference);
        }

        var prefix = baseValue.Protocol + "//" + SerializeAuthority(baseValue);
        if (reference.Length == 0)
        {
            return TryParse(prefix + baseValue.Pathname + baseValue.Query);
        }

        if (reference[0] is '?' or '#')
        {
            return TryParse(prefix + baseValue.Pathname + baseValue.Query + reference);
        }

        var directory = reference[0] == '/'
            ? string.Empty
            : baseValue.Pathname[..(baseValue.Pathname.LastIndexOf('/') + 1)];

        return TryParse(prefix + directory + reference);
    }

    /// <summary>
    /// The URL spec's preprocessing: drop leading and trailing C0 control or space, and remove every tab
    /// and newline wherever it appears.
    /// </summary>
    private static string Clean(string input)
    {
        var start = 0;
        var end = input.Length;
        while (start < end && input[start] <= ' ')
        {
            start++;
        }

        while (end > start && input[end - 1] <= ' ')
        {
            end--;
        }

        var slice = input.AsSpan(start, end - start);
        if (!slice.ContainsAny("\t\n\r"))
        {
            return slice.ToString();
        }

        var builder = new StringBuilder(slice.Length);
        foreach (var character in slice)
        {
            if (character is not ('\t' or '\n' or '\r'))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var character in scheme[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A special-scheme host is a domain: percent-decoded and ASCII-lower-cased. A non-special host is an
    /// opaque host, which is percent-decoded but keeps its case.
    /// </summary>
    private static string NormalizeHost(string host, bool special)
    {
        var decoded = PercentDecode(host);
        if (!special || !decoded.AsSpan().ContainsAnyInRange('A', 'Z'))
        {
            return decoded;
        }

        return string.Create(decoded.Length, decoded, static (target, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                // ASCII-only lowering, as the URL spec's domain-to-ASCII does.
                target[index] = source[index] is >= 'A' and <= 'Z' ? (char)(source[index] + 32) : source[index];
            }
        });
    }

    /// <summary>
    /// Turn the path text into a <c>/</c>-separated, percent-encoded pathname, resolving <c>.</c> and
    /// <c>..</c> the way the spec's path state does. An empty result means "no path", which a special
    /// scheme reports as <c>/</c>.
    /// </summary>
    private static string BuildPath(string pathPart, bool special)
    {
        if (pathPart.Length == 0)
        {
            return string.Empty;
        }

        var pieces = Split(pathPart, special ? ['/', '\\'] : ['/']);
        var segments = new List<string>();
        // The authority always ends at a separator, so the first piece is the empty string in front of it
        // and the spec does not turn that into a segment.
        var index = pieces.Count > 0 && pieces[0].Length == 0 ? 1 : 0;
        for (; index < pieces.Count; index++)
        {
            var piece = PercentEncode(pieces[index], PathEncodeSet);
            var last = index == pieces.Count - 1;
            if (IsSingleDot(piece))
            {
                // A trailing "." still contributes the empty segment that produces the trailing slash.
                if (last)
                {
                    segments.Add(string.Empty);
                }

                continue;
            }

            if (IsDoubleDot(piece))
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                if (last)
                {
                    segments.Add(string.Empty);
                }

                continue;
            }

            segments.Add(piece);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>A single-dot segment: <c>.</c> or <c>%2e</c>.</summary>
    private static bool IsSingleDot(string piece) => DotLength(piece, 0) == piece.Length && DotLength(piece, 0) > 0;

    /// <summary>A double-dot segment: <c>..</c>, <c>.%2e</c>, <c>%2e.</c> or <c>%2e%2e</c>.</summary>
    private static bool IsDoubleDot(string piece)
    {
        var first = DotLength(piece, 0);
        if (first == 0)
        {
            return false;
        }

        var second = DotLength(piece, first);
        return second > 0 && first + second == piece.Length;
    }

    /// <summary>
    /// The length of a dot at <paramref name="offset"/>: 1 for <c>.</c>, 3 for <c>%2e</c>, 0 when there is
    /// no dot there.
    /// </summary>
    private static int DotLength(string piece, int offset)
    {
        if (offset >= piece.Length)
        {
            return 0;
        }

        if (piece[offset] == '.')
        {
            return 1;
        }

        return offset + 2 < piece.Length
            && piece[offset] == '%'
            && piece[offset + 1] == '2'
            && (piece[offset + 2] is 'e' or 'E')
            ? 3
            : 0;
    }

    /// <summary>Percent-decode, leaving an invalid escape alone, as the URL spec's decoder does.</summary>
    private static string PercentDecode(string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length)
            {
                var high = HexValue(value[index + 1]);
                var low = HexValue(value[index + 2]);
                if (high >= 0 && low >= 0)
                {
                    bytes.Add((byte)((high << 4) | low));
                    index += 2;
                    continue;
                }
            }

            AppendUtf8(bytes, value, ref index);
        }

        // "UTF-8 decode without BOM": undecodable bytes become U+FFFD.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString([.. bytes]);
    }

    /// <summary>
    /// Percent-encode <paramref name="value"/> using the given code point set, or the C0-control set when
    /// <paramref name="encodeSet"/> is <see langword="null"/>. An existing <c>%</c> is never encoded, so
    /// escapes survive untouched.
    /// </summary>
    private static string PercentEncode(string value, SearchValues<char>? encodeSet)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character <= '\u001F'
                || character > '\u007E'
                || (encodeSet is not null && encodeSet.Contains(character)))
            {
                builder ??= new StringBuilder(value.Length + 16).Append(value, 0, index);
                AppendEncoded(builder, value, ref index);
                continue;
            }

            builder?.Append(character);
        }

        return builder?.ToString() ?? value;
    }

    private static void AppendEncoded(StringBuilder builder, string value, ref int index)
    {
        Span<byte> bytes = stackalloc byte[4];
        var character = value[index];
        string text;
        if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
        {
            text = value.Substring(index, 2);
            index++;
        }
        else
        {
            text = char.IsSurrogate(character) ? "\uFFFD" : character.ToString();
        }

        var count = Encoding.UTF8.GetBytes(text, bytes);
        for (var offset = 0; offset < count; offset++)
        {
            builder.Append('%')
                .Append(bytes[offset].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void AppendUtf8(List<byte> bytes, string value, ref int index)
    {
        var character = value[index];
        if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(index, 2)));
            index++;
            return;
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(char.IsSurrogate(character) ? "\uFFFD" : value.Substring(index, 1)));
    }

    /// <summary>
    /// JavaScript's <c>String.prototype.split(separator)</c>. Note the difference from
    /// <see cref="string.Split(char)"/> is only in the limit, which callers apply themselves because the
    /// limit <em>truncates</em> rather than letting the last element keep the remainder.
    /// </summary>
    internal static List<string> Split(string value, char[] separators)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (Array.IndexOf(separators, value[index]) >= 0)
            {
                parts.Add(value[start..index]);
                start = index + 1;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    internal static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
