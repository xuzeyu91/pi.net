using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of Node's <c>url.fileURLToPath</c> for <c>file:</c> URLs, including the WHATWG URL parsing
/// that produces the <c>hostname</c> and <c>pathname</c> it consumes.
/// </summary>
/// <remarks>
/// <para>
/// <c>utils/paths.ts</c> calls <c>fileURLToPath</c> on any input matching <c>^file://</c>, and the
/// call is <em>host dependent</em>: on Windows a pathname that is not <c>\C:\…</c> is rejected, while
/// on POSIX a non-empty host is rejected. Reproducing that matters because the failure is a thrown
/// error, not a returned value — silently falling back to <see cref="Uri.LocalPath"/> would turn a
/// hard failure into a wrong path.
/// </para>
/// <para>
/// The WHATWG pieces implemented here are only those observable through <c>fileURLToPath</c>: scheme
/// discrimination, authority parsing (the <c>localhost</c> fold, credentials and port rejection,
/// forbidden host code points, ASCII case folding, IPv4 normalization), <c>\</c> treated as a
/// separator, the <c>C|</c> → <c>C:</c> drive-letter fixup, and the path state machine's <c>.</c>/<c>..</c>
/// folding (including the percent-encoded spellings <c>%2e</c> and <c>%2e%2e</c>). URL
/// serialization, query/fragment parsing, and IDN host handling are not modelled.
/// </para>
/// <para><b>Known divergences.</b> Each is unreachable from pi's own call sites and pinned by the
/// <c>fileUrlToPathDivergence</c> corpus section, so it cannot drift silently.</para>
/// <list type="number">
/// <item>
/// <description>
/// <b>IDN hosts are not decoded.</b> Node's Windows branch passes the host through
/// <c>domainToUnicode</c>, an ICU-backed UTS#46 implementation, so <c>file://xn--fsqu00a/x</c>
/// becomes <c>\\例子\x</c>. This port returns the <c>xn--</c> label verbatim. Not ported because
/// <c>url.hostname</c> is always ASCII (the parser punycodes IDN input) so only already-punycoded
/// hosts can differ, and UTS#46's mapping tables are far larger than the rest of this file. A
/// non-ASCII host is otherwise unreachable: POSIX throws <c>ERR_INVALID_FILE_URL_HOST</c> first.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>IPv4-embedded IPv6 literals</b> are serialized in .NET's dotted-quad form rather than the
/// spec's hex-pair form: <c>file://[::ffff:1.2.3.4]/x</c> yields <c>\\[::ffff:1.2.3.4]\x</c> where
/// Node yields <c>\\[::ffff:102:304]\x</c>. Plain and zero-compressed literals match exactly.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class FileUrl
{
    /// <summary>Node's <c>ERR_INVALID_FILE_URL_PATH</c>.</summary>
    internal const string InvalidPathCode = "ERR_INVALID_FILE_URL_PATH";

    /// <summary>Node's <c>ERR_INVALID_FILE_URL_HOST</c>.</summary>
    internal const string InvalidHostCode = "ERR_INVALID_FILE_URL_HOST";

    /// <summary>Node's <c>ERR_INVALID_URL</c>.</summary>
    internal const string InvalidUrlCode = "ERR_INVALID_URL";

    /// <summary>Node's <c>ERR_INVALID_URL_SCHEME</c>.</summary>
    internal const string InvalidSchemeCode = "ERR_INVALID_URL_SCHEME";

    /// <summary>
    /// Convert a <c>file:</c> URL to a filesystem path using the <em>host</em> platform's rules, which
    /// is what Node's single-argument <c>fileURLToPath</c> does.
    /// </summary>
    internal static string ToPath(string url) => ToPath(url, NodePath.IsWindows);

    /// <summary>
    /// Convert a <c>file:</c> URL to a filesystem path, choosing the Windows or POSIX rules explicitly.
    /// This mirrors Node's <c>fileURLToPath(url, { windows })</c> option, which is what lets the
    /// differential corpus exercise both branches from one host.
    /// </summary>
    /// <exception cref="NodeIoException">
    /// The URL does not parse, its scheme is not <c>file</c>, the pathname is not absolute (Windows),
    /// the host is not empty/<c>localhost</c> (POSIX), or the pathname contains an encoded separator.
    /// </exception>
    /// <exception cref="UriFormatException">The pathname has malformed percent-encoding or invalid UTF-8.</exception>
    internal static string ToPath(string url, bool windows)
    {
        var (host, pathname) = Parse(url);

        if (windows)
        {
            // Node scans the *raw* pathname, before the '/' → '\' rewrite, so %2f and %5c are
            // rejected as written.
            RejectEncodedSeparator(pathname, backslashToo: true);
            var decoded = Decode(pathname.Replace('/', '\\'));
            if (host.Length > 0)
            {
                // A non-empty host makes this a UNC path: \\server\share\…
                return "\\\\" + host + decoded;
            }

            // A local path must be \C:\… — index 1 is the drive letter and index 2 the colon.
            var letter = char.ToLowerInvariant(decoded.Length > 1 ? decoded[1] : '\0');
            var separator = decoded.Length > 2 ? decoded[2] : '\0';
            if (letter is < 'a' or > 'z' || separator != ':')
            {
                throw new NodeIoException("File URL path must be absolute", InvalidPathCode);
            }

            return decoded[1..];
        }

        // POSIX checks the host *before* the pathname, so a UNC URL reports the host, not the path.
        if (host.Length > 0)
        {
            throw new NodeIoException(
                $"File URL host must be \"localhost\" or empty on {ProcessInfo.Platform}",
                InvalidHostCode);
        }

        RejectEncodedSeparator(pathname, backslashToo: false);
        return Decode(pathname);
    }

    /// <summary>
    /// Reject <c>%2f</c> (and on Windows <c>%5c</c>) in the pathname. Node deliberately refuses to
    /// decode these, because doing so would let a URL smuggle in a path separator.
    /// </summary>
    private static void RejectEncodedSeparator(string pathname, bool backslashToo)
    {
        for (var n = 0; n < pathname.Length; n++)
        {
            if (pathname[n] != '%' || n + 2 >= pathname.Length)
            {
                continue;
            }

            var third = char.ToLowerInvariant(pathname[n + 2]);
            if ((pathname[n + 1] == '2' && third == 'f') || (backslashToo && pathname[n + 1] == '5' && third == 'c'))
            {
                throw new NodeIoException(
                    "File URL path must not include encoded \\ or / characters",
                    InvalidPathCode);
            }
        }
    }

    /// <summary>The parsed host and folded pathname of a <c>file:</c> URL.</summary>
    private readonly record struct ParsedFileUrl(string Host, string Pathname);

    /// <summary>
    /// Parse a <c>file:</c> URL far enough to produce the <c>hostname</c> and <c>pathname</c> that
    /// <c>fileURLToPath</c> reads.
    /// </summary>
    private static ParsedFileUrl Parse(string input)
    {
        // WHATWG URL input pre-processing: strip ASCII tab and newline anywhere, then trim leading and
        // trailing C0 controls and spaces.
        var cleaned = StripAsciiTabOrNewline(input).Trim(TrimCharacters);

        // Scheme state: an ASCII alpha followed by alphanumerics, '+', '-' or '.', up to the first ':'.
        var colon = cleaned.IndexOf(':');
        if (colon <= 0 || !IsScheme(cleaned.AsSpan(0, colon)))
        {
            throw new NodeIoException("Invalid URL", InvalidUrlCode);
        }

        if (!cleaned.AsSpan(0, colon).Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            throw new NodeIoException("The URL must be of scheme file", InvalidSchemeCode);
        }

        // The query and the fragment are not part of the pathname.
        var rest = TruncateAtQueryOrFragment(cleaned[(colon + 1)..]);

        string host;
        string path;

        if (rest.Length > 0 && IsSeparator(rest[0]))
        {
            if (rest.Length > 1 && IsSeparator(rest[1]))
            {
                if (StartsWindowsDriveLetter(rest))
                {
                    // "file://C:/x": the parser never enters the authority states, so "C:" becomes the
                    // path's first segment rather than a host.
                    host = string.Empty;
                    path = rest[2..];
                }
                else
                {
                    // "file://<authority><path>". The authority runs to the next separator, and that
                    // separator is the pathname's leading slash.
                    var afterSlashes = rest[2..];
                    var end = afterSlashes.IndexOfAny(Separators);
                    var authority = end < 0 ? afterSlashes : afterSlashes[..end];
                    var tail = end < 0 ? string.Empty : afterSlashes[end..];

                    host = ParseHost(authority);
                    path = tail.Length > 0 ? tail[1..] : string.Empty;
                }
            }
            else
            {
                // A single separator ("file:/x", "file:\x") is the pathname's leading slash.
                host = string.Empty;
                path = rest[1..];
            }
        }
        else
        {
            // "file:x" — no authority, and no leading slash to strip.
            host = string.Empty;
            path = rest;
        }

        return new ParsedFileUrl(host, FoldPath(path));
    }

    /// <summary>
    /// Apply the URL parser's host parsing to the text between <c>//</c> and the first separator.
    /// </summary>
    /// <remarks>
    /// <c>file:</c> is a special scheme, so the host is not opaque: it is percent-decoded, ASCII
    /// case-folded, checked against the forbidden host code points, and — when it "ends in a number" —
    /// re-interpreted as an IPv4 address. The <c>localhost</c> fold happens after case folding, which
    /// is why <c>file://LOCALHOST/a</c> also has an empty host.
    /// </remarks>
    private static string ParseHost(string authority)
    {
        if (authority.Length == 0)
        {
            return string.Empty;
        }

        if (authority[0] == '[')
        {
            // An IPv6 literal keeps its brackets and is re-serialized in compressed lowercase form.
            if (authority[^1] != ']' ||
                !IPAddress.TryParse(authority[1..^1], out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw InvalidUrl();
            }

            return "[" + address.ToString() + "]";
        }

        // A file URL carries neither credentials nor a port.
        if (authority.Contains('@') || authority.Contains(':'))
        {
            throw InvalidUrl();
        }

        var host = DecodeHost(authority).ToLowerInvariant();
        foreach (var value in host)
        {
            if (IsForbiddenDomainCodePoint(value))
            {
                throw InvalidUrl();
            }
        }

        // "If isNotSpecial is false and domain is 'localhost', return the empty host."
        if (host.Equals("localhost", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return EndsInNumber(host) ? SerializeIpv4(ParseIpv4(host)) : host;
    }

    private static NodeIoException InvalidUrl() => new("Invalid URL", InvalidUrlCode);

    /// <summary>
    /// The URL parser abandons the authority states when the text after <c>//</c> starts with a Windows
    /// drive letter followed by a separator or the end of input.
    /// </summary>
    private static bool StartsWindowsDriveLetter(string rest) =>
        rest.Length >= 4 &&
        char.IsAsciiLetter(rest[2]) &&
        rest[3] is ':' or '|' &&
        (rest.Length == 4 || IsSeparator(rest[4]));

    /// <summary>
    /// The URL spec's "ends in a number" test, applied to an already case-folded host.
    /// </summary>
    /// <remarks>
    /// This is a <em>syntactic</em> test, not "the last label parses as an IPv4 number": <c>08</c>
    /// looks like a number, so it selects the IPv4 path, and the radix-8 re-interpretation then rejects
    /// it — which is what makes <c>file://08/a</c> fail rather than keep the host <c>08</c>. The
    /// predicates below are pinned by the corpus, which covers the leading-zero, <c>0x</c> and
    /// trailing-dot forms.
    /// </remarks>
    private static bool EndsInNumber(string host)
    {
        var labels = host.Split('.');
        var last = labels[^1];
        if (last.Length == 0)
        {
            if (labels.Length == 1)
            {
                return false;
            }

            last = labels[^2];
        }

        return IsNumberLike(last);
    }

    private static bool IsNumberLike(string label)
    {
        if (label.Length == 0)
        {
            return false;
        }

        var start = 0;
        if (label.Length >= 2 && label[0] == '0' && label[1] == 'x')
        {
            // "0x" alone is a number (zero); "0xg" is not, so the host survives unchanged.
            start = 2;
        }

        var radix = start == 0 ? 10 : 16;
        for (var index = start; index < label.Length; index++)
        {
            var digit = HexValue(label[index]);
            if (digit < 0 || digit >= radix)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The URL spec's "IPv4 parser", returning the address as a 32-bit number.</summary>
    private static ulong ParseIpv4(string host)
    {
        var parts = new List<string>(host.Split('.'));
        if (parts[^1].Length == 0 && parts.Count > 1)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count > 4)
        {
            throw InvalidUrl();
        }

        var numbers = new List<ulong>(parts.Count);
        foreach (var part in parts)
        {
            if (!TryParseIpv4Number(part, out var number))
            {
                throw InvalidUrl();
            }

            numbers.Add(number);
        }

        if (numbers.Count == 0)
        {
            throw InvalidUrl();
        }

        // Every part but the last must fit in one octet.
        for (var index = 0; index < numbers.Count - 1; index++)
        {
            if (numbers[index] > 255)
            {
                throw InvalidUrl();
            }
        }

        // The last part fills the remaining octets.
        var last = numbers[^1];
        if (last >= Pow256(5 - numbers.Count))
        {
            throw InvalidUrl();
        }

        var address = last;
        for (var index = 0; index < numbers.Count - 1; index++)
        {
            address += numbers[index] * Pow256(3 - index);
        }

        if (address > uint.MaxValue)
        {
            throw InvalidUrl();
        }

        return address;
    }

    private static ulong Pow256(int exponent)
    {
        var result = 1UL;
        for (var index = 0; index < exponent; index++)
        {
            result *= 256;
        }

        return result;
    }

    /// <summary>The URL spec's "IPv4 number parser": radix 10, or 16 for <c>0x</c>, or 8 for a leading zero.</summary>
    private static bool TryParseIpv4Number(string input, out ulong value)
    {
        value = 0;
        if (input.Length == 0)
        {
            return false;
        }

        var radix = 10;
        var start = 0;
        if (input.Length >= 2 && input[0] == '0' && input[1] == 'x')
        {
            radix = 16;
            start = 2;
        }
        else if (input.Length >= 2 && input[0] == '0')
        {
            // "0177" is octal 127, but "08" is a failure — not decimal 8.
            radix = 8;
            start = 1;
        }

        var result = 0UL;
        for (var index = start; index < input.Length; index++)
        {
            var digit = HexValue(input[index]);
            if (digit < 0 || digit >= radix)
            {
                return false;
            }

            // The spec treats overflow as a validation error rather than a failure, but any value that
            // saturates here is rejected by the range checks in ParseIpv4 anyway.
            if (result > (ulong.MaxValue - (ulong)digit) / (ulong)radix)
            {
                result = ulong.MaxValue;
                continue;
            }

            result = (result * (ulong)radix) + (ulong)digit;
        }

        value = result;
        return true;
    }

    private static string SerializeIpv4(ulong address) =>
        $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";

    /// <summary>
    /// Split a pathname into segments and fold <c>.</c> / <c>..</c> exactly as the URL parser's path
    /// state does, then serialize with forward slashes.
    /// </summary>
    /// <remarks>
    /// The state machine is reproduced rather than approximated, because its observable details are
    /// easy to get wrong: a separator appends a (possibly empty) segment, so <c>file:///a//</c> keeps
    /// both trailing separators, while a <c>.</c> or <c>..</c> that ends the input still leaves an empty
    /// final segment — <c>file:///a/..</c> is <c>/</c> and not the empty string.
    /// </remarks>
    private static string FoldPath(string path)
    {
        var segments = new List<string>();
        var buffer = new StringBuilder();

        for (var index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && !IsSeparator(path[index]))
            {
                buffer.Append(path[index]);
                continue;
            }

            // A separator and end-of-input are handled identically, except that only end-of-input
            // closes the path.
            var atEnd = index == path.Length;
            var text = buffer.ToString();
            buffer.Clear();

            if (IsDoubleDotSegment(text))
            {
                Shorten(segments);
                if (atEnd)
                {
                    segments.Add(string.Empty);
                }

                continue;
            }

            if (IsSingleDotSegment(text))
            {
                if (atEnd)
                {
                    segments.Add(string.Empty);
                }

                continue;
            }

            // "If url's scheme is file, url's path is empty, and buffer is a Windows drive letter,
            // then replace the second code point in buffer with ':'."
            if (segments.Count == 0 && IsWindowsDriveLetter(text))
            {
                text = string.Concat(text.AsSpan(0, 1), ":", text.AsSpan(2));
            }

            segments.Add(text);
        }

        return "/" + string.Join("/", segments);
    }

    /// <summary>Apply the URL spec's "shorten a URL's path" step.</summary>
    private static void Shorten(List<string> segments)
    {
        if (segments.Count == 0)
        {
            return;
        }

        // Never fold away a lone Windows drive letter: "/C:/.." stays "/C:/".
        if (segments.Count == 1 && IsWindowsDriveLetter(segments[0]))
        {
            return;
        }

        segments.RemoveAt(segments.Count - 1);
    }

    private static bool IsSingleDotSegment(string segment) =>
        segment == "." || segment.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDotSegment(string segment) =>
        segment == ".." ||
        segment.Equals(".%2e", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("%2e.", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

    /// <summary>A "Windows drive letter" is an ASCII alpha followed by <c>:</c> or <c>|</c>.</summary>
    private static bool IsWindowsDriveLetter(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] is ':' or '|';

    private static bool IsSeparator(char value) => value is '/' or '\\';

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var character in value[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static string TruncateAtQueryOrFragment(string value)
    {
        var index = value.IndexOfAny('?', '#');
        return index < 0 ? value : value[..index];
    }

    /// <summary>
    /// The URL spec's forbidden host code points, plus the extra ones forbidden in a domain: C0
    /// controls, <c>%</c> and DEL.
    /// </summary>
    private static bool IsForbiddenDomainCodePoint(char value) =>
        value is <= '\u0020' or '\u007F' or '#' or '%' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|';

    private static readonly char[] Separators = ['/', '\\'];

    // U+0000..U+0020: the WHATWG "leading/trailing C0 control or space" trim set.
    private static readonly char[] TrimCharacters =
        [.. Enumerable.Range(0, 0x21).Select(value => (char)value)];

    private static string StripAsciiTabOrNewline(string input)
    {
        if (input.IndexOfAny('\t', '\n', '\r') < 0)
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);
        foreach (var value in input)
        {
            if (value is not ('\t' or '\n' or '\r'))
            {
                builder.Append(value);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decode the pathname, skipping the work entirely when there is nothing to decode — the same
    /// fast path Node takes, and the reason a pathname without <c>%</c> is never rejected as
    /// malformed.
    /// </summary>
    private static string Decode(string input) =>
        input.IndexOf('%') < 0 ? input : DecodeUriComponent(input);

    /// <summary>
    /// Port of JavaScript's <c>decodeURIComponent</c>: percent-decode to bytes, then decode those bytes
    /// as UTF-8, throwing on malformed escapes or invalid UTF-8 exactly as <c>URIError</c> does.
    /// </summary>
    private static string DecodeUriComponent(string input)
    {
        var bytes = new List<byte>(input.Length);
        for (var index = 0; index < input.Length; index++)
        {
            var value = input[index];
            if (value == '%')
            {
                if (index + 2 >= input.Length)
                {
                    throw new UriFormatException("URI malformed");
                }

                var high = HexValue(input[index + 1]);
                var low = HexValue(input[index + 2]);
                if (high < 0 || low < 0)
                {
                    throw new UriFormatException("URI malformed");
                }

                bytes.Add((byte)((high << 4) | low));
                index += 2;
                continue;
            }

            // A lone surrogate has no UTF-8 encoding and makes JavaScript's decoder throw.
            if (char.IsSurrogate(value))
            {
                if (!char.IsHighSurrogate(value) ||
                    index + 1 >= input.Length ||
                    !char.IsLowSurrogate(input[index + 1]))
                {
                    throw new UriFormatException("URI malformed");
                }

                bytes.AddRange(Encoding.UTF8.GetBytes(input.Substring(index, 2)));
                index++;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(input.Substring(index, 1)));
        }

        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new UriFormatException("URI malformed", exception);
        }
    }

    /// <summary>
    /// The URL spec's lenient "percent-decode" followed by "UTF-8 decode without BOM": a <c>%</c> that
    /// is not followed by two hex digits is left alone, and undecodable bytes become U+FFFD.
    /// </summary>
    /// <remarks>
    /// This is deliberately not <see cref="DecodeUriComponent"/>, which throws on both. The leniency
    /// is observable: <c>file://%zz/a</c> keeps the literal <c>%</c>, which is then rejected as a
    /// forbidden domain code point, so the URL fails with <c>ERR_INVALID_URL</c> rather than the
    /// <c>URIError</c> a throwing decoder would produce.
    /// </remarks>
    private static string DecodeHost(string authority)
    {
        var bytes = new List<byte>(authority.Length);
        for (var index = 0; index < authority.Length; index++)
        {
            var value = authority[index];
            if (value == '%' && index + 2 < authority.Length)
            {
                var high = HexValue(authority[index + 1]);
                var low = HexValue(authority[index + 2]);
                if (high >= 0 && low >= 0)
                {
                    bytes.Add((byte)((high << 4) | low));
                    index += 2;
                    continue;
                }
            }

            if (char.IsHighSurrogate(value) && index + 1 < authority.Length && char.IsLowSurrogate(authority[index + 1]))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(authority.Substring(index, 2)));
                index++;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(authority.Substring(index, 1)));
        }

        // Encoding.UTF8 is lenient: an invalid sequence becomes U+FFFD, as "UTF-8 decode without BOM" does.
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
