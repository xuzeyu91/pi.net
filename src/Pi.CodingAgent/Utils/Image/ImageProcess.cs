namespace Pi.CodingAgent.Utils.Image;

/// <summary>Port of the TS <c>ProcessImageOptions</c>.</summary>
public sealed record ProcessImageOptions
{
    /// <summary>Whether to resize images to inline provider limits. Default: true.</summary>
    public bool? AutoResizeImages { get; init; }

    /// <summary>Optional resize overrides. Uses <c>resizeImage</c> defaults when omitted.</summary>
    public ImageResizeOptions? ResizeOptions { get; init; }
}

/// <summary>
/// Port of the TS <c>ProcessImageResult</c> discriminated union: a processed payload with hints,
/// or a user-facing omission message.
/// </summary>
public abstract record ProcessImageResult
{
    /// <summary>The TS <c>{ ok: true; data; mimeType; hints }</c>.</summary>
    public sealed record Ok(string Data, string MimeType, IReadOnlyList<string> Hints) : ProcessImageResult;

    /// <summary>The TS <c>{ ok: false; message }</c>.</summary>
    public sealed record Fail(string Message) : ProcessImageResult;
}

/// <summary>
/// Port of <c>utils/image-process.ts</c>: normalize an image payload into a supported inline
/// format and (optionally) resize it under the provider size limits.
/// </summary>
public static class ImageProcess
{
    /// <summary>The TS <c>processImage</c>.</summary>
    public static async Task<ProcessImageResult> ProcessImageAsync(
        byte[] bytes, string mimeType, ProcessImageOptions? options = null)
    {
        bool autoResizeImages = options?.AutoResizeImages ?? true;
        NormalizedImage? normalized = await NormalizeImageAsync(bytes, mimeType).ConfigureAwait(false);
        if (normalized is null)
        {
            return new ProcessImageResult.Fail(
                "[Image omitted: could not be converted to a supported inline image format.]");
        }

        if (autoResizeImages)
        {
            ResizedImage? resized = await ImageResize
                .ResizeImageAsync(normalized.Bytes, normalized.MimeType, options?.ResizeOptions)
                .ConfigureAwait(false);
            if (resized is null)
            {
                return new ProcessImageResult.Fail(
                    "[Image omitted: could not be resized below the inline image size limit.]");
            }

            var hints = new List<string>();
            string? convertedHint = ConversionHint(normalized.ConvertedFrom, resized.MimeType);
            if (convertedHint is not null)
            {
                hints.Add(convertedHint);
            }

            string? dimensionNote = ImageResize.FormatDimensionNote(resized);
            if (dimensionNote is not null)
            {
                hints.Add(dimensionNote);
            }

            return new ProcessImageResult.Ok(resized.Data, resized.MimeType, hints);
        }

        var noResizeHints = new List<string>();
        string? hint = ConversionHint(normalized.ConvertedFrom, normalized.MimeType);
        if (hint is not null)
        {
            noResizeHints.Add(hint);
        }

        return new ProcessImageResult.Ok(
            Convert.ToBase64String(normalized.Bytes), normalized.MimeType, noResizeHints);
    }

    private sealed record NormalizedImage(byte[] Bytes, string MimeType, string? ConvertedFrom = null);

    /// <summary>The TS <c>normalizeImage</c>: pass supported formats through, convert the rest to PNG.</summary>
    private static async Task<NormalizedImage?> NormalizeImageAsync(byte[] bytes, string mimeType)
    {
        string? normalizedMimeType = NormalizeSupportedImageMimeType(mimeType);
        if (normalizedMimeType is not null)
        {
            return new NormalizedImage(bytes, normalizedMimeType);
        }

        byte[]? pngBytes = await ImageConvert.ConvertImageBytesToPngAsync(bytes).ConfigureAwait(false);
        if (pngBytes is null)
        {
            return null;
        }

        return new NormalizedImage(pngBytes, "image/png", ConvertedFrom: BaseMimeType(mimeType));
    }

    /// <summary>The TS <c>baseMimeType</c>: strip parameters, trim, lowercase.</summary>
    internal static string BaseMimeType(string mimeType) =>
        mimeType.Split(';')[0]?.Trim().ToLowerInvariant() ?? mimeType.ToLowerInvariant();

    /// <summary>
    /// The TS <c>normalizeSupportedImageMimeType</c>: the accepted inline formats (jpeg and jpg
    /// both normalize to image/jpeg).
    /// </summary>
    private static string? NormalizeSupportedImageMimeType(string mimeType) =>
        BaseMimeType(mimeType) switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/gif" => "image/gif",
            "image/webp" => "image/webp",
            _ => null,
        };

    /// <summary>The TS <c>conversionHint</c>.</summary>
    private static string? ConversionHint(string? from, string to)
    {
        if (from is null || from == to)
        {
            return null;
        }

        return $"[Image converted from {from} to {to}.]";
    }
}
