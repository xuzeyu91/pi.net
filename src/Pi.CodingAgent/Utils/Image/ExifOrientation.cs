using SkiaSharp;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>
/// Port of <c>utils/exif-orientation.ts</c>: read the EXIF orientation tag out of a JPEG or WebP
/// payload and apply it to a decoded image.
/// </summary>
/// <remarks>
/// <para>
/// The byte-level half (<c>getExifOrientation</c> and friends) is a line-for-line port. JS
/// out-of-bounds reads yield <c>undefined</c>, whose NaN propagation always ends in the "no
/// orientation found" answer; the port bounds-checks each read and returns the same answers
/// (see the per-method remarks). Arithmetic that JS performs on 32-bit ints (the little-endian
/// <c>read32</c> can go negative via the sign bit, the big-endian one is <c>&gt;&gt;&gt; 0</c>-ed
/// to unsigned) is reproduced with the same signed/unsigned split so corrupt headers land in the
/// same branches.
/// </para>
/// <para>
/// The image half replaces photon with SkiaSharp. <see cref="ApplyExifOrientation"/> operates on
/// an <see cref="SKBitmap"/> in <see cref="SKColorType.Rgba8888"/> / <see cref="SKAlphaType.Unpremul"/>
/// (the port's stand-in for a <c>PhotonImage</c>; the decode step in <see cref="SkiaImage"/>
/// guarantees that shape). Rotations allocate a new bitmap and flips mutate in place, exactly like
/// the original — callers must dispose the old bitmap when a different instance comes back.
/// Rotation is a pure pixel permutation, so the premultiplied-ness of the pixels is irrelevant to
/// its correctness.
/// </para>
/// </remarks>
public static class ExifOrientation
{
    /// <summary>The TS <c>getExifOrientation</c>: 1 (no transform) when nothing usable is found.</summary>
    public static int GetExifOrientation(ReadOnlySpan<byte> bytes)
    {
        // JPEG: starts with FF D8
        int tiffOffset;
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            tiffOffset = FindJpegTiffOffset(bytes);
        }
        // WebP: starts with RIFF....WEBP
        else if (bytes.Length >= 12 &&
                 bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                 bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            tiffOffset = FindWebpTiffOffset(bytes);
        }
        else
        {
            return 1;
        }

        if (tiffOffset == -1)
        {
            return 1;
        }

        return ReadOrientationFromTiff(bytes, tiffOffset);
    }

    /// <summary>The TS <c>readOrientationFromTiff</c>.</summary>
    private static int ReadOrientationFromTiff(ReadOnlySpan<byte> bytes, int tiffStart)
    {
        if (tiffStart + 8 > bytes.Length)
        {
            return 1;
        }

        int byteOrder = (bytes[tiffStart] << 8) | bytes[tiffStart + 1];
        bool le = byteOrder == 0x4949;

        // Out-of-range reads return 0 where JS would propagate undefined → NaN: every consumer
        // (the entry-count loop guard, the 1..8 orientation check) treats that as "not found".
        // Local functions must take the span explicitly — ref-like values cannot be captured.
        static int Read16(ReadOnlySpan<byte> b, bool little, int pos) => pos >= 0 && pos + 2 <= b.Length
            ? (little ? b[pos] | (b[pos + 1] << 8) : (b[pos] << 8) | b[pos + 1])
            : 0;

        // JS: LE read32 keeps the int32 sign bit; BE read32 is `>>> 0` (unsigned). The offset math
        // goes through long so the unsigned form can exceed int.MaxValue (and land in the bounds
        // check below) while the signed form can go negative — matching JS's number arithmetic.
        static long Read32(ReadOnlySpan<byte> b, bool little, int pos) => pos >= 0 && pos + 4 <= b.Length
            ? (little
                ? b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24)
                : (uint)((b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]))
            : 0;

        long ifdOffset = Read32(bytes, le, tiffStart + 4);
        long ifdStart = tiffStart + ifdOffset;

        // JS: a negative or oversized ifdStart makes every entry read undefined, so the entry loop
        // never matches and the method falls through to 1. The explicit guard is that behavior.
        if (ifdStart < 0 || ifdStart + 2 > bytes.Length)
        {
            return 1;
        }

        int entryCount = Read16(bytes, le, (int)ifdStart);
        for (int i = 0; i < entryCount; i++)
        {
            long entryPos = ifdStart + 2 + i * 12L;
            if (entryPos + 12 > bytes.Length)
            {
                return 1;
            }

            if (Read16(bytes, le, (int)entryPos) == 0x0112)
            {
                int value = Read16(bytes, le, (int)entryPos + 8);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }

    /// <summary>The TS <c>findJpegTiffOffset</c>: walk JPEG markers to the APP1 EXIF payload.</summary>
    private static int FindJpegTiffOffset(ReadOnlySpan<byte> bytes)
    {
        int offset = 2;
        while (offset < bytes.Length - 1)
        {
            if (bytes[offset] != 0xff)
            {
                return -1;
            }

            int marker = bytes[offset + 1];
            if (marker == 0xff)
            {
                offset++;
                continue;
            }

            if (marker == 0xe1)
            {
                if (offset + 4 >= bytes.Length)
                {
                    return -1;
                }

                int segmentStart = offset + 4;
                if (segmentStart + 6 > bytes.Length)
                {
                    return -1;
                }

                if (HasExifHeader(bytes, segmentStart))
                {
                    return segmentStart + 6;
                }
            }

            if (offset + 4 > bytes.Length)
            {
                return -1;
            }

            int length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            offset += 2 + length;
        }

        return -1;
    }

    /// <summary>
    /// The TS <c>findWebpTiffOffset</c>: walk RIFF chunks to the EXIF chunk. Chunk sizes are read
    /// as signed 32-bit values exactly like the original (JS bitwise ops are int32), so a corrupt
    /// file can move the cursor backwards and reproduce the original's loop behavior.
    /// </summary>
    private static int FindWebpTiffOffset(ReadOnlySpan<byte> bytes)
    {
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            bool isExif = bytes[offset] == 0x45 && bytes[offset + 1] == 0x58 &&
                          bytes[offset + 2] == 0x49 && bytes[offset + 3] == 0x46;
            int chunkSize = bytes[offset + 4] | (bytes[offset + 5] << 8) |
                            (bytes[offset + 6] << 16) | (bytes[offset + 7] << 24);
            int dataStart = offset + 8;

            if (isExif)
            {
                if (dataStart + chunkSize > bytes.Length)
                {
                    return -1;
                }

                // Some WebP files have "Exif\0\0" prefix before the TIFF header
                int tiffStart = chunkSize >= 6 && HasExifHeader(bytes, dataStart) ? dataStart + 6 : dataStart;
                return tiffStart;
            }

            // RIFF chunks are padded to even size
            offset = dataStart + chunkSize + (chunkSize % 2);
        }

        return -1;
    }

    /// <summary>The TS <c>hasExifHeader</c>: the "Exif\0\0" marker.</summary>
    private static bool HasExifHeader(ReadOnlySpan<byte> bytes, int offset) =>
        offset >= 0 && offset + 6 <= bytes.Length &&
        bytes[offset] == 0x45 && bytes[offset + 1] == 0x78 && bytes[offset + 2] == 0x69 &&
        bytes[offset + 3] == 0x66 && bytes[offset + 4] == 0x00 && bytes[offset + 5] == 0x00;

    /// <summary>
    /// The TS <c>applyExifOrientation</c>. Flip orientations mutate in place; rotations return a
    /// new bitmap (the caller must dispose the old one when it differs).
    /// </summary>
    public static SKBitmap ApplyExifOrientation(SKBitmap image, ReadOnlySpan<byte> originalBytes)
    {
        int orientation = GetExifOrientation(originalBytes);
        if (orientation == 1)
        {
            return image;
        }

        switch (orientation)
        {
            case 2:
                FlipH(image);
                return image;
            case 3:
                FlipH(image);
                FlipV(image);
                return image;
            case 4:
                FlipV(image);
                return image;
            case 5:
            {
                var rotated = Rotate90(image, (x, y, _, h) => x * h + (h - 1 - y));
                FlipH(rotated);
                return rotated;
            }
            case 6:
                return Rotate90(image, (x, y, _, h) => x * h + (h - 1 - y));
            case 7:
            {
                var rotated = Rotate90(image, (x, y, w, h) => (w - 1 - x) * h + y);
                FlipH(rotated);
                return rotated;
            }
            case 8:
                return Rotate90(image, (x, y, w, h) => (w - 1 - x) * h + y);
            default:
                return image;
        }
    }

    /// <summary>
    /// The TS <c>rotate90</c>: a literal pixel copy through the <c>dstIndex</c> mapping, with the
    /// dimensions swapped. The bitmap must be 4-bytes-per-pixel (Rgba8888 — see the type remarks).
    /// </summary>
    private static SKBitmap Rotate90(SKBitmap image, Func<int, int, int, int, int> dstIndex)
    {
        int w = image.Info.Width;
        int h = image.Info.Height;
        byte[] src = SkiaImage.GetPixels(image);
        var dst = new byte[src.Length];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcIdx = (y * w + x) * 4;
                int dstIdx = dstIndex(x, y, w, h) * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = src[srcIdx + 3];
            }
        }

        var rotated = new SKBitmap(new SKImageInfo(h, w, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        SkiaImage.SetPixels(rotated, dst);
        return rotated;
    }

    /// <summary>The TS <c>photon.fliph</c>: mirror horizontally, in place.</summary>
    private static void FlipH(SKBitmap image)
    {
        int w = image.Info.Width;
        int h = image.Info.Height;
        byte[] pixels = SkiaImage.GetPixels(image);

        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w / 2; x++)
            {
                int left = row + x * 4;
                int right = row + (w - 1 - x) * 4;
                (pixels[left], pixels[right]) = (pixels[right], pixels[left]);
                (pixels[left + 1], pixels[right + 1]) = (pixels[right + 1], pixels[left + 1]);
                (pixels[left + 2], pixels[right + 2]) = (pixels[right + 2], pixels[left + 2]);
                (pixels[left + 3], pixels[right + 3]) = (pixels[right + 3], pixels[left + 3]);
            }
        }

        SkiaImage.SetPixels(image, pixels);
    }

    /// <summary>The TS <c>photon.flipv</c>: mirror vertically, in place.</summary>
    private static void FlipV(SKBitmap image)
    {
        int w = image.Info.Width;
        int h = image.Info.Height;
        byte[] pixels = SkiaImage.GetPixels(image);

        var tmp = new byte[w * 4];
        for (int y = 0; y < h / 2; y++)
        {
            int top = y * w * 4;
            int bottom = (h - 1 - y) * w * 4;
            Array.Copy(pixels, top, tmp, 0, w * 4);
            Array.Copy(pixels, bottom, pixels, top, w * 4);
            Array.Copy(tmp, 0, pixels, bottom, w * 4);
        }

        SkiaImage.SetPixels(image, pixels);
    }
}
