using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// JavaScript's <c>encodeURI</c> and <c>encodeURIComponent</c>.
/// </summary>
/// <remarks>
/// <para>
/// Neither is <see cref="Uri.EscapeDataString"/>: the unescaped sets are different and, more
/// importantly, <see cref="Uri.EscapeDataString"/> escapes <c>!</c>, <c>'</c>, <c>(</c>, <c>)</c> and
/// <c>*</c>, which JavaScript leaves alone. Both JavaScript functions percent-encode the UTF-8 bytes
/// with upper-case hex digits, which is what these do.
/// </para>
/// <para>
/// The unescaped sets come from the specification: <c>encodeURI</c> keeps <c>uriReserved</c> plus
/// <c>uriUnescaped</c> plus <c>#</c>, while <c>encodeURIComponent</c> keeps only <c>uriUnescaped</c>.
/// Note that neither keeps <c>%</c>, so an already-encoded sequence is escaped again
/// (<c>encodeURI("%41")</c> is <c>"%2541"</c>).
/// </para>
/// </remarks>
public static class JsUri
{
    /// <summary>Characters <c>encodeURI</c> leaves untouched: <c>uriReserved uriUnescaped #</c>.</summary>
    private const string UriUnescapedCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()";

    private const string UriReservedCharacters = ";/?:@&=+$,";

    /// <summary>JavaScript's <c>encodeURI</c>.</summary>
    public static string EncodeUri(string value) => Encode(value, UriUnescapedCharacters + UriReservedCharacters + "#");

    /// <summary>JavaScript's <c>encodeURIComponent</c>.</summary>
    public static string EncodeUriComponent(string value) => Encode(value, UriUnescapedCharacters);

    /// <summary>
    /// JavaScript's <c>decodeURIComponent</c>: percent-decode to bytes, then decode those bytes as UTF-8,
    /// throwing on malformed escapes or invalid UTF-8 exactly as <c>URIError</c> does.
    /// </summary>
    /// <exception cref="UriFormatException">
    /// A <c>%</c> not followed by two hex digits, a lone surrogate, or bytes that are not valid UTF-8.
    /// </exception>
    public static string DecodeUriComponent(string input)
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
                if (!char.IsHighSurrogate(value) || index + 1 >= input.Length || !char.IsLowSurrogate(input[index + 1]))
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

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    private static string Encode(string value, string unescaped)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (unescaped.Contains(character, StringComparison.Ordinal))
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Length + 16).Append(value, 0, index);

            // JavaScript encodes the UTF-8 bytes of the code point. A lone surrogate is not a valid
            // scalar value, so it encodes to the replacement character, which is what the runtime does.
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                AppendCodePoint(builder, char.ConvertToUtf32(character, value[index + 1]));
                index++;
                continue;
            }

            AppendCodePoint(builder, char.IsSurrogate(character) ? 0xFFFD : character);
        }

        return builder?.ToString() ?? value;
    }

    private static void AppendCodePoint(StringBuilder builder, int codePoint)
    {
        Span<byte> bytes = stackalloc byte[4];
        var count = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint), bytes);
        for (var index = 0; index < count; index++)
        {
            builder.Append('%')
                .Append(bytes[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
