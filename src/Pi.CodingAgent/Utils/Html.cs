using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>A decoded HTML entity and how many characters it occupied (port of <c>utils/html.ts</c>).</summary>
/// <param name="Text">The decoded text.</param>
/// <param name="Length">Number of source characters consumed, including the leading <c>&amp;</c> and the <c>;</c>.</param>
public readonly record struct DecodedHtmlEntity(string Text, int Length);

/// <summary>Port of <c>utils/html.ts</c>.</summary>
public static class Html
{
    private const int MaxCodePoint = 0x10FFFF;

    /// <summary>Decode an HTML entity name or numeric reference, without the surrounding <c>&amp;</c>/<c>;</c>.</summary>
    public static string? DecodeHtmlEntity(string entity) => entity switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        _ => DecodeNumeric(entity),
    };

    private static string? DecodeNumeric(string entity)
    {
        if (entity.StartsWith("#x", StringComparison.Ordinal) || entity.StartsWith("#X", StringComparison.Ordinal))
        {
            return DecodeCodePoint(JsRegex.ParseInt(JsString.Slice(entity, 2), 16));
        }

        if (entity.StartsWith('#'))
        {
            return DecodeCodePoint(JsRegex.ParseInt(JsString.Slice(entity, 1), 10));
        }

        return null;
    }

    /// <summary>
    /// Decode the entity starting at <paramref name="index"/> (which must point at the <c>&amp;</c>).
    /// Returns <see langword="null"/> when there is no <c>;</c> within 16 characters or the reference is
    /// not decodable.
    /// </summary>
    public static DecodedHtmlEntity? DecodeHtmlEntityAt(string html, int index)
    {
        var searchFrom = index + 1;
        if (searchFrom < 0)
        {
            // JS `indexOf` clamps a negative start index to 0.
            searchFrom = 0;
        }

        if (searchFrom > html.Length)
        {
            return null;
        }

        var semicolonIndex = html.IndexOf(';', searchFrom);
        if (semicolonIndex == -1 || semicolonIndex - index > 16)
        {
            return null;
        }

        var entity = JsString.Slice(html, index + 1, semicolonIndex);
        var decoded = DecodeHtmlEntity(entity);
        return decoded is null ? null : new DecodedHtmlEntity(decoded, semicolonIndex - index + 1);
    }

    private static string? DecodeCodePoint(double? codePoint)
    {
        // `Number.isInteger` plus the range check. `ParseInt` never returns a fractional value, so only
        // NaN (null) and the range can fail.
        if (codePoint is null || codePoint < 0 || codePoint > MaxCodePoint)
        {
            return null;
        }

        return FromCodePoint((int)codePoint.Value);
    }

    /// <summary>
    /// JS <c>String.fromCodePoint</c>. .NET's <see cref="char.ConvertFromUtf32"/> rejects surrogate code
    /// points, while JS happily produces the lone surrogate (<c>&amp;#xD800;</c> decodes to <c>"\uD800"</c>),
    /// so those are emitted directly.
    /// </summary>
    private static string FromCodePoint(int codePoint) =>
        codePoint is >= 0xD800 and <= 0xDFFF
            ? ((char)codePoint).ToString()
            : char.ConvertFromUtf32(codePoint);
}
