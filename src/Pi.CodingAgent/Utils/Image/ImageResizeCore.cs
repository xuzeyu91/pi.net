using SkiaSharp;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>Port of the TS <c>ImageResizeOptions</c> (image-resize-core.ts).</summary>
public sealed record ImageResizeOptions
{
    public int? MaxWidth { get; init; }

    public int? MaxHeight { get; init; }

    public long? MaxBytes { get; init; }

    public int? JpegQuality { get; init; }

    /// <summary>
    /// Bridges the model catalog's nominal <see cref="Pi.Ai.Types.ModelImageResizeOptions"/> to this
    /// type. The two are structurally identical but deliberately separate (difference C30): one is the
    /// profile the catalog hands down, the other the generic caller-facing override.
    /// </summary>
    public static ImageResizeOptions FromModelProfile(Pi.Ai.Types.ModelImageResizeOptions profile) => new()
    {
        MaxWidth = profile.MaxWidth,
        MaxHeight = profile.MaxHeight,
        MaxBytes = profile.MaxBytes,
        JpegQuality = profile.JpegQuality,
    };
}

/// <summary>Port of the TS <c>ResizedImage</c>: the payload is base64.</summary>
public sealed record ResizedImage
{
    public required string Data { get; init; }

    public required string MimeType { get; init; }

    public required int OriginalWidth { get; init; }

    public required int OriginalHeight { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool WasResized { get; init; }
}

/// <summary>
/// Port of <c>utils/image-resize-core.ts</c>: resize an image to fit within the specified max
/// dimensions and encoded byte budget.
/// </summary>
/// <remarks>
/// <para>
/// Strategy (identical to the original): first fit the dimension limits; try PNG and JPEG at a
/// ladder of qualities and take the first candidate under <c>maxBytes</c>; on failure shrink both
/// sides by 75% and retry until 1×1; return null when even that fails. The TS math uses
/// <c>Math.round</c> (half up) and <c>Math.floor</c> — the port pins
/// <see cref="MidpointRounding.AwayFromZero"/> because .NET's default <see cref="Math.Round"/> is
/// banker's rounding, a real behavioral difference at exact .5 ratios.
/// </para>
/// <para>
/// The photon calls go through <see cref="SkiaImage"/>; the encoded sizes therefore differ from
/// the TS run (different encoders), which can flip the PNG/JPEG choice for payloads sitting
/// right at the budget boundary. The algorithm, limits and semantics are unchanged.
/// </para>
/// </remarks>
public static class ImageResizeCore
{
    // 4.5MB of base64 payload. Provides headroom below Anthropic's 5MB limit.
    public const long DefaultMaxBytes = 4_718_592; // 4.5 * 1024 * 1024

    /// <summary>The TS <c>resizeImageInProcess</c>. Null when the image cannot be decoded or shrunk.</summary>
    public static ResizedImage? ResizeImageInProcess(byte[] inputBytes, string mimeType, ImageResizeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);
        int maxWidth = options?.MaxWidth ?? 2000;
        int maxHeight = options?.MaxHeight ?? 2000;
        long maxBytes = options?.MaxBytes ?? DefaultMaxBytes;
        int jpegQuality = options?.JpegQuality ?? 80;

        int inputBase64Size = (inputBytes.Length + 2) / 3 * 4; // Math.ceil(len / 3) * 4

        SKBitmap? rawImage = SkiaImage.Decode(inputBytes);
        if (rawImage is null)
        {
            return null;
        }

        SKBitmap? image = null;
        try
        {
            image = ExifOrientation.ApplyExifOrientation(rawImage, inputBytes);
            if (!ReferenceEquals(image, rawImage))
            {
                rawImage.Dispose();
            }

            int originalWidth = image.Info.Width;
            int originalHeight = image.Info.Height;
            string format = mimeType.Contains('/') ? mimeType.Split('/')[1] : "png";

            // Check if already within all limits (dimensions AND encoded size)
            if (originalWidth <= maxWidth && originalHeight <= maxHeight && inputBase64Size < maxBytes)
            {
                return new ResizedImage
                {
                    Data = Convert.ToBase64String(inputBytes),
                    MimeType = mimeType.Length > 0 ? mimeType : $"image/{format}",
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    Width = originalWidth,
                    Height = originalHeight,
                    WasResized = false,
                };
            }

            // Calculate initial dimensions respecting max limits
            int targetWidth = originalWidth;
            int targetHeight = originalHeight;

            if (targetWidth > maxWidth)
            {
                targetHeight = (int)Math.Round((double)targetHeight * maxWidth / targetWidth,
                    MidpointRounding.AwayFromZero);
                targetWidth = maxWidth;
            }

            if (targetHeight > maxHeight)
            {
                targetWidth = (int)Math.Round((double)targetWidth * maxHeight / targetHeight,
                    MidpointRounding.AwayFromZero);
                targetHeight = maxHeight;
            }

            var qualitySteps = DistinctInInsertionOrder(jpegQuality, 85, 70, 55, 40);
            int currentWidth = targetWidth;
            int currentHeight = targetHeight;

            while (true)
            {
                foreach (var candidate in TryEncodings(image, currentWidth, currentHeight, qualitySteps))
                {
                    if (candidate.EncodedSize < maxBytes)
                    {
                        return new ResizedImage
                        {
                            Data = candidate.Data,
                            MimeType = candidate.MimeType,
                            OriginalWidth = originalWidth,
                            OriginalHeight = originalHeight,
                            Width = currentWidth,
                            Height = currentHeight,
                            WasResized = true,
                        };
                    }
                }

                if (currentWidth == 1 && currentHeight == 1)
                {
                    break;
                }

                int nextWidth = currentWidth == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentWidth * 0.75));
                int nextHeight = currentHeight == 1 ? 1 : Math.Max(1, (int)Math.Floor(currentHeight * 0.75));
                if (nextWidth == currentWidth && nextHeight == currentHeight)
                {
                    break;
                }

                currentWidth = nextWidth;
                currentHeight = nextHeight;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            image?.Dispose();
        }
    }

    /// <summary>The TS <c>tryEncodings</c>: a fresh resize, then PNG first and JPEG per quality.</summary>
    private static List<EncodedCandidate> TryEncodings(
        SKBitmap image, int width, int height, IReadOnlyList<int> jpegQualities)
    {
        using SKBitmap? resized = SkiaImage.Resize(image, width, height)
            ?? throw new InvalidOperationException("image resize failed");

        var candidates = new List<EncodedCandidate>();
        byte[]? png = SkiaImage.EncodePng(resized);
        if (png is not null)
        {
            candidates.Add(EncodeCandidate(png, "image/png"));
        }

        foreach (int quality in jpegQualities)
        {
            byte[]? jpeg = SkiaImage.EncodeJpeg(resized, quality);
            if (jpeg is not null)
            {
                candidates.Add(EncodeCandidate(jpeg, "image/jpeg"));
            }
        }

        return candidates;
    }

    /// <summary>The TS <c>encodeCandidate</c>: base64 payload; its UTF-8 length is the base64 length.</summary>
    private static EncodedCandidate EncodeCandidate(byte[] buffer, string mimeType)
    {
        string data = Convert.ToBase64String(buffer);
        return new EncodedCandidate(data, data.Length, mimeType);
    }

    /// <summary>TS <c>Array.from(new Set([...]))</c>: dedupe, preserving first-occurrence order.</summary>
    private static IReadOnlyList<int> DistinctInInsertionOrder(params int[] values)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (int value in values)
        {
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private sealed record EncodedCandidate(string Data, long EncodedSize, string MimeType);
}
