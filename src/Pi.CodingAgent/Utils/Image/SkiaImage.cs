using System.Runtime.InteropServices;
using SkiaSharp;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>
/// The port's stand-in for the photon wasm module (<c>utils/photon.ts</c> + the
/// <c>@silvia-odwyer/photon-node</c> API surface the coding-agent actually touches).
/// </summary>
/// <remarks>
/// <para>
/// Photon's model is an RGBA8 (unpremultiplied) raster: <c>new_from_byteslice</c> decodes an
/// encoded image into it, <c>get_raw_pixels</c> hands out the raster, <c>get_bytes</c> re-encodes
/// it as PNG, <c>get_bytes_jpeg(quality)</c> as JPEG, and <c>resize(w, h, Lanczos3)</c> resamples.
/// The SkiaSharp equivalents are <see cref="Decode"/> (pinned to
/// <see cref="SKColorType.Rgba8888"/> / <see cref="SKAlphaType.Unpremul"/> so the raster shape
/// matches), <see cref="EncodePng"/>, <see cref="EncodeJpeg"/>, and <see cref="Resize"/>.
/// </para>
/// <para>
/// Documented divergences (encoder-level, not behavioral): Skia's PNG/JPEG encoders and its cubic
/// resampler are not byte-identical to photon's Rust image crate — encoded payloads differ in size
/// (which can flip the format choice for images right at the <c>maxBytes</c> boundary) and resized
/// pixel values differ slightly. Skia's decode support follows its codec registry; photon's wasm
/// build supports a slightly different format set. Neither affects the control flow of any caller.
/// </para>
/// </remarks>
internal static class SkiaImage
{
    /// <summary>
    /// The TS <c>PhotonImage.new_from_byteslice</c>: decode an encoded image into the canonical
    /// RGBA8 unpremultiplied raster. Returns null when the payload cannot be decoded (the TS
    /// callers catch and treat that as "conversion unavailable").
    /// </summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        using var stream = new SKMemoryStream(bytes.ToArray());
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    /// <summary>The TS <c>image.get_bytes()</c>: encode the raster as PNG.</summary>
    public static byte[]? EncodePng(SKBitmap image)
    {
        using var skImage = SKImage.FromBitmap(image);
        if (skImage is null)
        {
            return null;
        }

        using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>The TS <c>image.get_bytes_jpeg(quality)</c>: encode the raster as JPEG.</summary>
    public static byte[]? EncodeJpeg(SKBitmap image, int quality)
    {
        using var skImage = SKImage.FromBitmap(image);
        if (skImage is null)
        {
            return null;
        }

        using (var data = skImage.Encode(SKEncodedImageFormat.Jpeg, quality))
        {
            if (data is not null)
            {
                return data.ToArray();
            }
        }

        // Some Skia builds refuse unpremultiplied JPEG sources; convert through a canvas draw
        // (which premultiplies) and retry. Only transparent-pixel RGB values change, which JPEG
        // discards anyway.
        var info = new SKImageInfo(image.Info.Width, image.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var converted = new SKBitmap(info);
        using (var canvas = new SKCanvas(converted))
        {
            // 1:1 copy, so nearest sampling is exact. `DrawBitmap(bitmap, x, y, paint)` is obsolete in
            // SkiaSharp 4.x in favor of the SKSamplingOptions overload.
            canvas.DrawBitmap(image, 0f, 0f, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        using var premulImage = SKImage.FromBitmap(converted);
        if (premulImage is null)
        {
            return null;
        }

        using var premulData = premulImage.Encode(SKEncodedImageFormat.Jpeg, quality);
        return premulData?.ToArray();
    }

    /// <summary>
    /// The TS <c>photon.resize(image, width, height, SamplingFilter.Lanczos3)</c>. Skia has no
    /// Lanczos resampler; the Catmull-Rom cubic is the same high-quality class and is the closest
    /// built-in (encoder-level divergence, see the type remarks). Implemented with
    /// <see cref="SKPixmap.ScalePixels"/> — the resampling primitive the higher-level helpers use.
    /// </summary>
    public static SKBitmap? Resize(SKBitmap image, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using SKPixmap? source = image.PeekPixels();
        if (source is null)
        {
            return null;
        }

        var target = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var result = new SKBitmap(target);
        using SKPixmap? destination = result.PeekPixels();
        if (destination is null ||
            !destination.ScalePixels(source, new SKSamplingOptions(SKCubicResampler.CatmullRom)))
        {
            result.Dispose();
            return null;
        }

        return result;
    }

    /// <summary>The raw RGBA raster (the TS <c>get_raw_pixels</c>). The bitmap must be 4Bpp.</summary>
    public static byte[] GetPixels(SKBitmap image)
    {
        var pixels = new byte[image.Info.BytesSize];
        Marshal.Copy(image.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>Write a raw RGBA raster back (TS mutates the PhotonImage in place).</summary>
    public static void SetPixels(SKBitmap image, byte[] pixels)
    {
        if (pixels.Length != image.Info.BytesSize)
        {
            throw new ArgumentException($"pixel buffer is {pixels.Length} bytes, expected {image.Info.BytesSize}");
        }

        Marshal.Copy(pixels, 0, image.GetPixels(), pixels.Length);
    }
}
