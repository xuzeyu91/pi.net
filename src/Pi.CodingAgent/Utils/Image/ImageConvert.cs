using Pi.Tui;
using Pi.Tui.Components;
using SkiaSharp;
using TuiImage = Pi.Tui.Components.Image;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>The result of the TS <c>convertToPng</c>: re-encoded payload plus its new MIME type.</summary>
public sealed record PngConversion(string Data, string MimeType);

/// <summary>
/// Port of <c>utils/image-convert.ts</c>: convert arbitrary image payloads to oriented PNG for
/// terminal display (the Kitty graphics protocol requires PNG, f=100).
/// </summary>
/// <remarks>
/// photon is replaced by <see cref="SkiaImage"/>; unlike the TS loader (which can fail to import
/// the wasm module and makes every entry return null), SkiaSharp is a compiled dependency and
/// always available — the "photon not available" branch is dead code here and not carried over.
/// The TS <c>Buffer.from(x, "base64")</c> decoder is lenient (it skips characters outside the
/// alphabet); <see cref="LenientBase64.Decode"/> reproduces that instead of throwing.
/// </remarks>
public static class ImageConvert
{
    /// <summary>The TS <c>encodePng</c>: decode, apply EXIF orientation, re-encode as PNG.</summary>
    private static byte[]? EncodePng(ReadOnlySpan<byte> bytes)
    {
        try
        {
            SKBitmap? rawImage = SkiaImage.Decode(bytes);
            if (rawImage is null)
            {
                return null;
            }

            SKBitmap image = ExifOrientation.ApplyExifOrientation(rawImage, bytes);
            try
            {
                return SkiaImage.EncodePng(image);
            }
            finally
            {
                image.Dispose();
                if (!ReferenceEquals(image, rawImage))
                {
                    rawImage.Dispose();
                }
            }
        }
        catch
        {
            // Conversion failed
            return null;
        }
    }

    /// <summary>The TS <c>convertImageBytesToPng</c>. Null when the payload cannot be converted.</summary>
    public static Task<byte[]?> ConvertImageBytesToPngAsync(byte[] bytes) =>
        Task.FromResult(EncodePng(bytes));

    /// <summary>
    /// The TS <c>convertToPng</c>: already-PNG input passes through untouched (same bytes); anything
    /// else is decoded, oriented and re-encoded. Null when conversion is impossible.
    /// </summary>
    public static Task<PngConversion?> ConvertToPngAsync(string base64Data, string mimeType)
    {
        // Already PNG, no conversion needed
        if (mimeType == "image/png")
        {
            return Task.FromResult<PngConversion?>(new PngConversion(base64Data, mimeType));
        }

        byte[] bytes = LenientBase64.Decode(base64Data);
        byte[]? pngBytes = EncodePng(bytes);
        if (pngBytes is null)
        {
            return Task.FromResult<PngConversion?>(null);
        }

        return Task.FromResult<PngConversion?>(new PngConversion(Convert.ToBase64String(pngBytes), "image/png"));
    }

    /// <summary>
    /// The TS <c>loadPngTranscoder</c>: a synchronous PNG transcoder for pi-tui's Kitty image
    /// rendering. Returns the pi-tui <see cref="ImageTranscoder"/> delegate, or null-equivalent
    /// task failure path only for decode errors of individual payloads (per-payload null).
    /// </summary>
    public static Task<ImageTranscoder?> LoadPngTranscoderAsync()
    {
        ImageTranscoder transcoder = (base64Data, _) =>
        {
            byte[]? pngBytes = EncodePng(LenientBase64.Decode(base64Data));
            return pngBytes is null ? null : Convert.ToBase64String(pngBytes);
        };
        return Task.FromResult<ImageTranscoder?>(transcoder);
    }

    private static Task<bool>? _pngTranscoderLoad;
    private static bool _pngTranscoderRegistered;

    /// <summary>
    /// The TS <c>ensurePngTranscoder</c>: on Kitty-protocol terminals, register the transcoder with
    /// pi-tui so non-PNG images render. Loads once; <paramref name="onRegistered"/> runs after
    /// registration (so callers can re-render images that showed text fallbacks) and is not called
    /// when the transcoder was already registered or cannot load.
    /// </summary>
    public static void EnsurePngTranscoder(Action onRegistered)
    {
        if (_pngTranscoderRegistered || TerminalImage.GetCapabilities().Images != ImageProtocol.Kitty)
        {
            return;
        }

        _pngTranscoderLoad ??= Task.Run(async () =>
        {
            var transcoder = await LoadPngTranscoderAsync().ConfigureAwait(false);
            if (transcoder is null)
            {
                return false;
            }

            TuiImage.SetImageTranscoder(transcoder);
            _pngTranscoderRegistered = true;
            return true;
        });

        _ = _pngTranscoderLoad.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result)
            {
                onRegistered();
            }
        }, TaskScheduler.Default);
    }
}
