using System.Text;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;
using Pi.Tui;
using SkiaSharp;
using Xunit;

namespace Pi.CodingAgent.Tests;

internal static class ImageTestSupport
{
    // The same fixtures the TS tests use (generated with ImageMagick; decode identically in Skia).
    public const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACAQMAAABIeJ9nAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAAGUExURf8AAP///0EdNBEAAAABYktHRAH/Ai3eAAAAB3RJTUUH6gEOADM5Ddoh/wAAAAxJREFUCNdjYGBgAAAABAABJzQnCgAAACV0RVh0ZGF0ZTpjcmVhdGUAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMOnKzHgAAAAldEVYdGRhdGU6bW9kaWZ5ADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDCYl3TEAAAAKHRFWHRkYXRlOnRpbWVzdGFtcAAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAwz4JVGwAAAABJRU5ErkJggg==";

    public const string TinyJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDAREAAhEBAxEB/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/xAAVAQEBAAAAAAAAAAAAAAAAAAAGCf/EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AD3VTB3/2Q==";

    public const string TinyJpeg2X1 =
        "/9j/4AAQSkZJRgABAgAAAQABAAD/wAARCAABAAIDAREAAhEBAxEB/9sAQwADAgIDAgIDAwMDBAMDBAUIBQUEBAUKBwcGCAwKDAwLCgsLDQ4SEA0OEQ4LCxAWEBETFBUVFQwPFxgWFBgSFBUU/9sAQwEDBAQFBAUJBQUJFA0LDRQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD4H8Q/8h/Uv+vmX/0M1/o1wJ/ySWU/9g1D/wBNRMOM/wDkp8z/AOv9b/05I//Z";

    public const string MediumPng100 =
        "iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAAAAABVicqIAAAAAmJLR0QA/4ePzL8AAAAHdElNRQfqAQ4AMzkN2iH/AAAAP0lEQVRo3u3NQQEAAAQEMASXXYrz2gqst/Lm4ZBIJBKJRCKRSCQSiUQikUgkEolEIpFIJBKJRCKRSCQSiSTsAP1cAUZeKtreAAAAJXRFWHRkYXRlOmNyZWF0ZQAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAw6crMeAAAACV0RVh0ZGF0ZTptb2RpZnkAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMJiXdMQAAAAodEVYdGRhdGU6dGltZXN0YW1wADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDDPglUbAAAAAElFTkSuQmCC";

    public const string LargePng200 =
        "iVBORw0KGgoAAAANSUhEUgAAAMgAAADIEAIAAAByquWKAAAAIGNIUk0AAHomAACAhAAA+gAAAIDoAAB1MAAA6mAAADqYAAAXcJy6UTwAAAAGYktHRP///////wlY99wAAAAHdElNRQfqAQ4AMzqU03BFAAAEEElEQVR42u3dvWqUURSF4XXMRAyIhSAWQvwnWtjaWusFx9bW1kKD/wELESxEiBjDjJdwysOC57mCXW32+zGcGbtdACpsdi+T8Xz1GABzm91xMl6sHgNgblzcT/Y+rh4DYG6cv0/2H64eA2BOEgI1fHQHaoyzbXIwVo8BMCcJgRqSEKgxft9Lrn5aPQbA3Ph1klw7Wj0GwJwkBGpYWECN8e1fcmuzegyAORcWUMPCAmqM09vJ4dfVYwDMjS9vkzuPV48BMCcJgRqeSAZqjA/nyYP91WMAzElCoIYkBGqMk8Pk6HT1GABzm+3L5JIkBApIQqCGj+5AjfHmb/Lk8uoxAOYkIVBDEgI1LCygxmZ7nFyShEABFxZQw8ICaozXf5KnV1aPATDnwgJqWFhADQsLqOFnDUANFxZQw8ICaoxXZ8mzg9VjAMy5sIAaFhZQw3tYQA1/QgHUkIRADUkI1JCEQA1JCNSQhEANFxZQwzcsoIYkBGpIQqCGJARqSEKghiQEalhYQA1/QgHUcGEBNSwsoIYkBGq4sIAaFhZQw8ICaviGBdRwYQE1LCyghiQEariwgBoWFlDDe1hADS+OAjUkIVBDEgI1JCFQQxICNSQhUEMSAjUkIVBDEgI1XFhADd+wgBqSEKghCYEakhCoIQmBGpIQqGFhATW8OArUcGEBNSwsoIYkBGq4sIAaFhZQw8ICaviGBdRwYQE1LCyghiQEariwgBoWFlDDe1hADS+OAjUkIVBDEgI1JCFQQxICNSQhUMOFBdTwDQuoIQmBGpIQqCEJgRqSEKghCYEa49lB8ups9RgAc14cBWpIQqCGhQXUkIRADRcWUMPCAmqMp1eS139WjwEw5xsWUEMSAjUsLKCGJARquLCAGhYWUGM8uZy8+bt6DIA5L44CNSQhUMMTyUCNcXSanByuHgNgThICNSQhUGM82E8+nK8eA2DOzxqAGpIQqDHuPE6+vF09BsDcOPyanN5ePQbAnCQEavgdFlBj3Nok3/6tHgNgzoujQA1JCNQYNx8l39+tHgNgbtz4nPy4u3oMgDlJCNSwsIAa4/pe8vNi9RgAc37WANSQhECNce0o+XWyegyAuXH1U/L73uoxAOYkIVDDwgJqjIORnG1XjwEw508ogBqSEKgx9h8m5+9XjwEwN/Y+Jhf3V48BMCcJgRpjPE+2x6vHAJgbSbLbrR4DYO4/GqiSgXN+ksgAAAAldEVYdGRhdGU6Y3JlYXRlADIwMjYtMDEtMTRUMDA6NTE6NTcrMDA6MDDpysx4AAAAJXRFWHRkYXRlOm1vZGlmeQAyMDI2LTAxLTE0VDAwOjUxOjU3KzAwOjAwmJd0xAAAACh0RVh0ZGF0ZTp0aW1lc3RhbXAAMjAyNi0wMS0xNFQwMDo1MTo1NyswMDowMM+CVRsAAAAASUVORK5CYII=";

    /// <summary>Bytes of a base64 constant.</summary>
    public static byte[] Bytes(string base64) => Convert.FromBase64String(base64);

    /// <summary>A JPEG APP1 segment wrapping <paramref name="payload"/> (the TS app1Segment helper).</summary>
    public static byte[] App1Segment(byte[] payload)
    {
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xff;
        segment[1] = 0xe1;
        segment[2] = (byte)((payload.Length + 2) >> 8);
        segment[3] = (byte)(payload.Length + 2);
        payload.CopyTo(segment, 4);
        return segment;
    }

    /// <summary>
    /// A little-endian TIFF blob with one IFD entry: tag 0x0112 (orientation), SHORT, value
    /// <paramref name="orientation"/> — the same 26-byte payload the TS tests assemble from hex.
    /// </summary>
    public static byte[] TiffBlob(int orientation)
    {
        byte[] blob = Convert.FromHexString("49492A0008000000010012010300010000000600000000000000");
        // Layout: II(4) IFD-offset(4) count(2) entry(12) next-IFD(4); the SHORT value sits at
        // offset 4+4+2+8 = 18, little-endian.
        blob[18] = (byte)orientation;
        return blob;
    }

    /// <summary>
    /// A JPEG whose single APP1 segment carries an EXIF TIFF with the given orientation value,
    /// over a minimal FF D8 header.
    /// </summary>
    public static byte[] JpegWithOrientation(int orientation)
    {
        byte[] payload = [.. "Exif\0\0"u8, .. TiffBlob(orientation)];
        var bytes = new List<byte> { 0xff, 0xd8 };
        bytes.AddRange(App1Segment(payload));
        return [.. bytes];
    }

    /// <summary>
    /// TINY_JPEG_2X1 with an XMP APP1 segment ahead of an EXIF APP1 segment that carries
    /// orientation 6 (the TS jpegWithXmpBeforeOrientation helper) — pinned by the port's
    /// findJpegTiffOffset, which must skip the non-EXIF APP1.
    /// </summary>
    public static byte[] JpegWithXmpBeforeOrientation()
    {
        byte[] jpeg = Bytes(TinyJpeg2X1);
        byte[] xmp = App1Segment(
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));
        byte[] orientation6 = App1Segment([.. "Exif\0\0"u8, .. TiffBlob(6)]);

        var result = new byte[2 + xmp.Length + orientation6.Length + (jpeg.Length - 2)];
        jpeg.AsSpan(0, 2).CopyTo(result);
        xmp.CopyTo(result, 2);
        orientation6.CopyTo(result, 2 + xmp.Length);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + xmp.Length + orientation6.Length));
        return result;
    }

    /// <summary>A WebP (RIFF....WEBP) whose EXIF chunk (with the "Exif\0\0" prefix) carries the orientation.</summary>
    public static byte[] WebpWithOrientation(int orientation)
    {
        byte[] payload = [.. "Exif\0\0"u8, .. TiffBlob(orientation)];

        var chunk = new List<byte>();
        chunk.AddRange(Encoding.ASCII.GetBytes("EXIF"));
        var sizeBytes = new byte[4];
        WriteUInt32Le(sizeBytes, 0, (uint)payload.Length);
        chunk.AddRange(sizeBytes);
        chunk.AddRange(payload);
        if (payload.Length % 2 == 1)
        {
            chunk.Add(0x00); // RIFF chunks are padded to even size
        }

        var file = new List<byte>();
        file.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        file.AddRange(new byte[4]); // riff size, patched below
        file.AddRange(Encoding.ASCII.GetBytes("WEBP"));
        file.AddRange(chunk);

        uint riffSize = (uint)(file.Count - 8);
        var riffSizeBytes = new byte[4];
        WriteUInt32Le(riffSizeBytes, 0, riffSize);
        for (int i = 0; i < 4; i++)
        {
            file[4 + i] = riffSizeBytes[i];
        }

        return [.. file];
    }

    /// <summary>A minimal 1×1 24bpp BMP (BGR + row padding), red pixel — the TS helper.</summary>
    public static byte[] TinyBmp1X1Red24Bpp()
    {
        var buffer = new byte[58];
        // BITMAPFILEHEADER
        buffer[0] = (byte)'B';
        buffer[1] = (byte)'M';
        WriteUInt32Le(buffer, 2, (uint)buffer.Length); // file size
        WriteUInt32Le(buffer, 10, 54); // pixel data offset
        // BITMAPINFOHEADER
        WriteUInt32Le(buffer, 14, 40); // DIB header size
        WriteInt32Le(buffer, 18, 1); // width
        WriteInt32Le(buffer, 22, 1); // height (positive = bottom-up)
        WriteUInt16Le(buffer, 26, 1); // planes
        WriteUInt16Le(buffer, 28, 24); // bits per pixel
        WriteUInt32Le(buffer, 30, 0); // compression (BI_RGB)
        WriteUInt32Le(buffer, 34, 4); // image size (incl. padding)
        // Pixel data (B, G, R) + 1 byte padding
        buffer[54] = 0x00; // B
        buffer[55] = 0x00; // G
        buffer[56] = 0xff; // R
        buffer[57] = 0x00; // padding
        return buffer;
    }

    public static void WriteUInt16Le(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    public static void WriteUInt32Le(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public static void WriteInt32Le(byte[] buffer, int offset, int value) =>
        WriteUInt32Le(buffer, offset, unchecked((uint)value));

    /// <summary>An RGBA8888/Unpremul bitmap with one distinct gray value per pixel (0,1,2,…).</summary>
    public static (SKBitmap Bitmap, byte[] Expected) OpaqueRamp(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var expected = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte value = (byte)(i % 256);
            expected[i * 4] = value;
            expected[i * 4 + 1] = value;
            expected[i * 4 + 2] = value;
            expected[i * 4 + 3] = 0xff;
        }

        SetPixels(bitmap, expected);
        return (bitmap, expected);
    }

    /// <summary>Read the raw RGBA raster of a bitmap.</summary>
    public static byte[] GetPixels(SKBitmap bitmap)
    {
        var pixels = new byte[bitmap.Info.BytesSize];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    public static void SetPixels(SKBitmap bitmap, byte[] pixels) =>
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

    /// <summary>PNG IHDR width/height (the TS readUInt32BE(16)/readUInt32BE(20) probes).</summary>
    public static (int Width, int Height) PngDimensions(byte[] png) =>
        ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19],
         (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
}

/// <summary>Ports of the byte-level expectations in the TS exif-orientation tests.</summary>
public class ExifOrientationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void GetExifOrientation_JpegApp1(int orientation)
    {
        Assert.Equal(orientation, ExifOrientation.GetExifOrientation(ImageTestSupport.JpegWithOrientation(orientation)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(8)]
    public void GetExifOrientation_WebpExifChunk(int orientation)
    {
        Assert.Equal(orientation, ExifOrientation.GetExifOrientation(ImageTestSupport.WebpWithOrientation(orientation)));
    }

    [Fact]
    public void GetExifOrientation_WithoutExifReturnsOne()
    {
        Assert.Equal(1, ExifOrientation.GetExifOrientation(ImageTestSupport.Bytes(ImageTestSupport.TinyPng)));
        Assert.Equal(1, ExifOrientation.GetExifOrientation(ImageTestSupport.Bytes(ImageTestSupport.TinyJpeg)));
    }

    [Fact]
    public void GetExifOrientation_MalformedPayloadsReturnOne()
    {
        // JPEG header, then a truncated APP1 whose EXIF header does not fit.
        Assert.Equal(1, ExifOrientation.GetExifOrientation([0xff, 0xd8, 0xff, 0xe1, 0x00, 0x02]));
        // Not a marker stream.
        Assert.Equal(1, ExifOrientation.GetExifOrientation([0xff, 0xd8, 0x00, 0x01]));
        // Too short to be anything.
        Assert.Equal(1, ExifOrientation.GetExifOrientation([0xff]));
        // WebP with a truncated EXIF chunk (declared size exceeds the file).
        var webp = ImageTestSupport.WebpWithOrientation(6);
        Assert.Equal(1, ExifOrientation.GetExifOrientation(webp[..(webp.Length - 4)]));
    }

    [Fact]
    public void GetExifOrientation_SkipsXmpApp1Segment()
    {
        Assert.Equal(6, ExifOrientation.GetExifOrientation(ImageTestSupport.JpegWithXmpBeforeOrientation()));
    }

    [Fact]
    public void ApplyExifOrientation_OrientationOneReturnsSameInstance()
    {
        var (image, _) = ImageTestSupport.OpaqueRamp(2, 3);
        using var released = image;
        Assert.Same(image, ExifOrientation.ApplyExifOrientation(image, ImageTestSupport.JpegWithOrientation(1)));
    }

    [Fact]
    public void ApplyExifOrientation_FlipHorizontalMutatesInPlace()
    {
        var (image, src) = ImageTestSupport.OpaqueRamp(2, 3);
        var result = ExifOrientation.ApplyExifOrientation(image, ImageTestSupport.JpegWithOrientation(2));
        Assert.Same(image, result);
        byte[] flipped = ImageTestSupport.GetPixels(result);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int srcIdx = (y * 2 + (1 - x)) * 4;
                int dstIdx = (y * 2 + x) * 4;
                Assert.Equal(src[srcIdx], flipped[dstIdx]);
            }
        }
    }

    [Fact]
    public void ApplyExifOrientation_FlipVerticalMutatesInPlace()
    {
        var (image, src) = ImageTestSupport.OpaqueRamp(2, 3);
        var result = ExifOrientation.ApplyExifOrientation(image, ImageTestSupport.JpegWithOrientation(4));
        Assert.Same(image, result);
        byte[] flipped = ImageTestSupport.GetPixels(result);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int srcIdx = ((2 - y) * 2 + x) * 4;
                int dstIdx = (y * 2 + x) * 4;
                Assert.Equal(src[srcIdx], flipped[dstIdx]);
            }
        }

        result.Dispose();
    }

    [Fact]
    public void ApplyExifOrientation_OrientationThreeFlipsBothInPlace()
    {
        var (image, src) = ImageTestSupport.OpaqueRamp(2, 2);
        var result = ExifOrientation.ApplyExifOrientation(image, ImageTestSupport.JpegWithOrientation(3));
        Assert.Same(image, result);
        byte[] rotated = ImageTestSupport.GetPixels(result);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int srcIdx = ((1 - y) * 2 + (1 - x)) * 4;
                int dstIdx = (y * 2 + x) * 4;
                Assert.Equal(src[srcIdx], rotated[dstIdx]);
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ApplyExifOrientation_RotationsReturnNewSwappedImage(int orientation)
    {
        var (image, src) = ImageTestSupport.OpaqueRamp(2, 3);
        var result = ExifOrientation.ApplyExifOrientation(image, ImageTestSupport.JpegWithOrientation(orientation));
        Assert.NotSame(image, result);
        Assert.Equal(3, result.Info.Width);
        Assert.Equal(2, result.Info.Height);

        // Build the expected raster: the TS dstIndex rotation mapping, then a horizontal flip for
        // orientations 5/7 (applied to the rotated raster, dst width 3).
        int dstWidth = 3;
        int dstHeight = 2;
        var expected = new byte[dstWidth * dstHeight * 4];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int dstIdx = (orientation is 5 or 6
                    ? x * 3 + (3 - 1 - y)
                    : (2 - x) * 3 + y) * 4;
                int srcIdx = (y * 2 + x) * 4;
                expected[dstIdx] = src[srcIdx];
                expected[dstIdx + 1] = src[srcIdx + 1];
                expected[dstIdx + 2] = src[srcIdx + 2];
                expected[dstIdx + 3] = src[srcIdx + 3];
            }
        }

        if (orientation is 5 or 7)
        {
            var flipped = new byte[expected.Length];
            for (int y = 0; y < dstHeight; y++)
            {
                for (int x = 0; x < dstWidth; x++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        flipped[(y * dstWidth + x) * 4 + c] = expected[(y * dstWidth + (dstWidth - 1 - x)) * 4 + c];
                    }
                }
            }

            expected = flipped;
        }

        byte[] rotated = ImageTestSupport.GetPixels(result);
        Assert.Equal<byte>(expected, rotated);

        result.Dispose();
        image.Dispose();
    }
}

/// <summary>Ports of the TS image-processing.test.ts (convertToPng / loadPngTranscoder / resizeImage).</summary>
public class ImageConvertAndResizeTests
{
    [Fact]
    public async Task ConvertToPng_PngPassesThrough()
    {
        var result = await ImageConvert.ConvertToPngAsync(ImageTestSupport.TinyPng, "image/png");
        Assert.NotNull(result);
        Assert.Equal(ImageTestSupport.TinyPng, result.Data);
        Assert.Equal("image/png", result.MimeType);
    }

    [Fact]
    public async Task ConvertToPng_JpegBecomesPng()
    {
        var result = await ImageConvert.ConvertToPngAsync(ImageTestSupport.TinyJpeg, "image/jpeg");
        Assert.NotNull(result);
        Assert.Equal("image/png", result.MimeType);
        byte[] png = Convert.FromBase64String(result.Data);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4e, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    [Fact]
    public async Task ConvertToPng_AppliesExifAfterXmpSegment()
    {
        var result = await ImageConvert.ConvertToPngAsync(
            Convert.ToBase64String(ImageTestSupport.JpegWithXmpBeforeOrientation()), "image/jpeg");
        Assert.NotNull(result);
        Assert.Equal((1, 2), ImageTestSupport.PngDimensions(Convert.FromBase64String(result.Data)));
    }

    [Fact]
    public async Task ConvertToPng_UndecodableReturnsNull()
    {
        Assert.Null(await ImageConvert.ConvertToPngAsync(
            Convert.ToBase64String("not an image"u8.ToArray()), "image/jpeg"));
    }

    [Fact]
    public async Task Transcoder_ConvertsSynchronouslyToOrientedPng()
    {
        var transcoder = await ImageConvert.LoadPngTranscoderAsync();
        Assert.NotNull(transcoder);

        byte[] png = Convert.FromBase64String(
            transcoder(Convert.ToBase64String(ImageTestSupport.JpegWithXmpBeforeOrientation()), "image/jpeg")!);
        Assert.Equal((1, 2), ImageTestSupport.PngDimensions(png));
        Assert.Null(transcoder(Convert.ToBase64String("not an image"u8.ToArray()), "image/jpeg"));
    }

    [Fact]
    public async Task ResizeImage_KeepsCallerInputBytesIntact()
    {
        byte[] input = ImageTestSupport.Bytes(ImageTestSupport.TinyPng);
        int originalByteLength = input.Length;
        byte originalFirstByte = input[0];

        var result = await ImageResize.ResizeImageAsync(input, "image/png", new ImageResizeOptions
        {
            MaxWidth = 100,
            MaxHeight = 100,
            MaxBytes = 1024 * 1024,
        });

        Assert.NotNull(result);
        Assert.Equal(originalByteLength, input.Length);
        Assert.Equal(originalFirstByte, input[0]);
    }

    [Fact]
    public async Task ResizeImage_WithinLimitsReturnsOriginal()
    {
        var result = await ImageResize.ResizeImageAsync(
            ImageTestSupport.Bytes(ImageTestSupport.TinyPng), "image/png", new ImageResizeOptions
            {
                MaxWidth = 100,
                MaxHeight = 100,
                MaxBytes = 1024 * 1024,
            });

        Assert.NotNull(result);
        Assert.False(result.WasResized);
        Assert.Equal(ImageTestSupport.TinyPng, result.Data);
        Assert.Equal((2, 2, 2, 2), (result.OriginalWidth, result.OriginalHeight, result.Width, result.Height));
    }

    [Fact]
    public async Task ResizeImage_ResizesBeyondDimensionLimits()
    {
        var result = await ImageResize.ResizeImageAsync(
            ImageTestSupport.Bytes(ImageTestSupport.MediumPng100), "image/png", new ImageResizeOptions
            {
                MaxWidth = 50,
                MaxHeight = 50,
                MaxBytes = 1024 * 1024,
            });

        Assert.NotNull(result);
        Assert.True(result.WasResized);
        Assert.Equal((100, 100), (result.OriginalWidth, result.OriginalHeight));
        Assert.True(result.Width <= 50);
        Assert.True(result.Height <= 50);
    }

    [Fact]
    public async Task ResizeImage_ResizesBeyondByteLimit()
    {
        byte[] original = ImageTestSupport.Bytes(ImageTestSupport.LargePng200);
        int originalSize = original.Length;

        var result = await ImageResize.ResizeImageAsync(original, "image/png", new ImageResizeOptions
        {
            MaxWidth = 2000,
            MaxHeight = 2000,
            MaxBytes = ImageTestSupport.LargePng200.Length * 9L / 10,
        });

        Assert.NotNull(result);
        byte[] resultBuffer = Convert.FromBase64String(result.Data);
        Assert.True(resultBuffer.Length < originalSize);
        Assert.True(result.Data.Length < ImageTestSupport.LargePng200.Length);
    }

    [Fact]
    public async Task ResizeImage_ImpossibleBudgetReturnsNull()
    {
        var result = await ImageResize.ResizeImageAsync(
            ImageTestSupport.Bytes(ImageTestSupport.LargePng200), "image/png", new ImageResizeOptions
            {
                MaxWidth = 2000,
                MaxHeight = 2000,
                MaxBytes = 1,
            });

        Assert.Null(result);
    }

    [Fact]
    public async Task ResizeImage_HandlesJpegInput()
    {
        var result = await ImageResize.ResizeImageAsync(
            ImageTestSupport.Bytes(ImageTestSupport.TinyJpeg), "image/jpeg", new ImageResizeOptions
            {
                MaxWidth = 100,
                MaxHeight = 100,
                MaxBytes = 1024 * 1024,
            });

        Assert.NotNull(result);
        Assert.False(result.WasResized);
        Assert.Equal((2, 2), (result.OriginalWidth, result.OriginalHeight));
    }

    [Fact]
    public void FormatDimensionNote_NonResizedReturnsNull()
    {
        Assert.Null(ImageResize.FormatDimensionNote(new ResizedImage
        {
            Data = "",
            MimeType = "image/png",
            OriginalWidth = 100,
            OriginalHeight = 100,
            Width = 100,
            Height = 100,
            WasResized = false,
        }));
    }

    [Fact]
    public void FormatDimensionNote_FormatsScaleWithTwoDecimals()
    {
        string note = ImageResize.FormatDimensionNote(new ResizedImage
        {
            Data = "",
            MimeType = "image/png",
            OriginalWidth = 2000,
            OriginalHeight = 1000,
            Width = 1000,
            Height = 500,
            WasResized = true,
        })!;

        Assert.Contains("original 2000x1000", note);
        Assert.Contains("displayed at 1000x500", note);
        Assert.Contains("2.00", note);
    }
}

/// <summary>Ports of the TS image-process.test.ts.</summary>
public class ImageProcessTests
{
    [Fact]
    public void DetectsGifAndBmpSignatures()
    {
        Assert.Equal("image/gif", Mime.DetectSupportedImageMimeType("GIF87a"u8));
        Assert.Equal("image/gif", Mime.DetectSupportedImageMimeType("GIF89a"u8));
        Assert.Equal("image/bmp", Mime.DetectSupportedImageMimeType(ImageTestSupport.TinyBmp1X1Red24Bpp()));
    }

    [Fact]
    public async Task ConvertsBmpWithoutAutoResize()
    {
        var result = await ImageProcess.ProcessImageAsync(
            ImageTestSupport.TinyBmp1X1Red24Bpp(), "image/bmp", new ProcessImageOptions { AutoResizeImages = false });

        var ok = Assert.IsType<ProcessImageResult.Ok>(result);
        Assert.Equal("image/png", ok.MimeType);
        Assert.Contains("[Image converted from image/bmp to image/png.]", ok.Hints);
        byte[] png = Convert.FromBase64String(ok.Data);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
    }

    [Fact]
    public async Task ConvertsBmpBeforeAutoResizing()
    {
        var result = await ImageProcess.ProcessImageAsync(ImageTestSupport.TinyBmp1X1Red24Bpp(), "image/bmp");

        var ok = Assert.IsType<ProcessImageResult.Ok>(result);
        Assert.Equal("image/png", ok.MimeType);
        Assert.Contains("[Image converted from image/bmp to image/png.]", ok.Hints);
    }

    [Fact]
    public async Task UndecodableImageIsOmitted()
    {
        var result = await ImageProcess.ProcessImageAsync("garbage"u8.ToArray(), "image/bmp");
        var fail = Assert.IsType<ProcessImageResult.Fail>(result);
        Assert.Equal("[Image omitted: could not be converted to a supported inline image format.]", fail.Message);
    }

    [Fact]
    public async Task JpgAliasNormalizesToJpeg()
    {
        var result = await ImageProcess.ProcessImageAsync(
            ImageTestSupport.Bytes(ImageTestSupport.TinyJpeg), "image/jpg", new ProcessImageOptions
            {
                AutoResizeImages = false,
            });

        var ok = Assert.IsType<ProcessImageResult.Ok>(result);
        Assert.Equal("image/jpeg", ok.MimeType);
        Assert.Empty(ok.Hints);
    }
}

/// <summary>Ports of the TS clipboard-image tests (mocks become override seams).</summary>
public class ClipboardImageTests
{
    private static byte[] Png() => ImageTestSupport.Bytes(ImageTestSupport.TinyPng);

    private static byte[] Bmp() => ImageTestSupport.TinyBmp1X1Red24Bpp();

    private static byte[] Listing(string content) => Encoding.UTF8.GetBytes(content);

    private sealed class FakeNativeClipboard(byte[]? image, bool throwOnGet = false) : INativeClipboard
    {
        public int GetImageCalls { get; private set; }

        public Task<NativeClipboardText?> GetTextAsync() => Task.FromResult<NativeClipboardText?>(null);

        public Task<byte[]?> GetImageAsync()
        {
            GetImageCalls++;
            if (throwOnGet)
            {
                throw new InvalidOperationException("Broken X11 bridge");
            }

            return Task.FromResult(image);
        }

        public Task<string[]?> GetFilePathsAsync() => Task.FromResult<string[]?>(null);

        public Task SetTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class CommandLog
    {
        public List<(string Command, string[] Args)> Calls { get; } = [];

        public Func<string, string[], byte[]?> Handler { get; set; } = (_, _) => null;

        public Task<byte[]?> RunAsync(string command, IReadOnlyList<string> args, ClipboardCommandOptions _)
        {
            var argv = (string[])args;
            Calls.Add((command, argv));
            return Task.FromResult(Handler(command, argv));
        }
    }

    public ClipboardImageTests()
    {
        ClipboardImageApi.CommandRunnerOverride = null;
        ClipboardImageApi.NativeClipboardOverride = null;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    public async Task CommandImagePresentStopsFallback(int backend, bool present)
    {
        // backend 0 = wayland (wl-paste), 1 = x11 (xclip).
        string command = backend == 0 ? "wl-paste" : "xclip";
        string[] envPairs = backend == 0 ? ["WAYLAND_DISPLAY:1", "DISPLAY::0"] : ["DISPLAY::0"];

        var native = new FakeNativeClipboard(Png());
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog
        {
            Handler = (name, args) =>
            {
                Assert.Equal(command, name);
                bool listing = args.Contains("--list-types") || args.Contains("TARGETS");
                return listing
                    ? Listing(present ? "text/plain\nimage/png\n" : "text/plain\n")
                    : Png();
            },
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(envPairs) });

        if (present)
        {
            Assert.NotNull(image);
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(2, log.Calls.Count);
            Assert.Equal(Png(), image.Bytes);
        }
        else
        {
            Assert.Null(image);
            Assert.Single(log.Calls);
        }

        Assert.Equal(0, native.GetImageCalls);
    }

    [Fact]
    public async Task X11_DoesNotProbeImageTypesWhenTargetsFails()
    {
        // Regression test for #9786.
        var native = new FakeNativeClipboard(null);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog
        {
            Handler = (_, args) => args.Contains("TARGETS") ? null : Listing("hello"),
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["DISPLAY::0"]) });

        Assert.Null(image);
        var (command, args) = Assert.Single(log.Calls);
        Assert.Equal("xclip", command);
        Assert.Equal<string[]>(["-selection", "clipboard", "-t", "TARGETS", "-o"], args);
        Assert.Equal(1, native.GetImageCalls);
    }

    [Fact]
    public async Task X11_DoesNotProbeUnadvertisedImageTypes()
    {
        var native = new FakeNativeClipboard(null);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog
        {
            Handler = (_, args) =>
            {
                if (args.Contains("TARGETS"))
                {
                    return Listing("image/png\n");
                }

                return args.Contains("image/png") ? null : Listing("hello");
            },
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["DISPLAY::0"]) });

        Assert.Null(image);
        Assert.Equal<string[][]>(
        [
            ["-selection", "clipboard", "-t", "TARGETS", "-o"],
            ["-selection", "clipboard", "-t", "image/png", "-o"],
        ], log.Calls.Select(c => c.Args).ToArray());
        Assert.Equal(1, native.GetImageCalls);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("null")]
    [InlineData("empty")]
    public async Task NativeX11ResultStopsFallback(string kind)
    {
        // TS: test.each([png, null, new Uint8Array()]).
        byte[]? nativeImage = kind switch
        {
            "png" => Png(),
            "null" => null,
            _ => [],
        };
        var native = new FakeNativeClipboard(nativeImage);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog(); // every command fails (handler → null)
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["DISPLAY::0"]) });

        if (nativeImage is { Length: > 0 })
        {
            Assert.NotNull(image);
            Assert.Equal(Png(), image.Bytes);
            Assert.Equal("image/png", image.MimeType);
        }
        else
        {
            Assert.Null(image);
        }

        Assert.Equal(1, native.GetImageCalls);
        Assert.Equal(["xclip"], log.Calls.Select(c => c.Command).ToArray());
    }

    [Theory]
    [InlineData("missing module")]
    [InlineData("unavailable display")]
    public async Task WaylandFallsBackToX11AfterNativeFailure(string failure)
    {
        var native = failure == "missing module" ? null : new FakeNativeClipboard(null);
        ClipboardImageApi.NativeClipboardOverride = native is null ? () => null : () => native;
        var log = new CommandLog
        {
            Handler = (command, args) =>
            {
                if (command == "wl-paste")
                {
                    return null;
                }

                return args.Contains("TARGETS") ? Listing("image/png\n") : Png();
            },
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["WAYLAND_DISPLAY:1"]) });

        Assert.NotNull(image);
        Assert.Equal(Png(), image.Bytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(0, native?.GetImageCalls ?? 0);
    }

    [Fact]
    public async Task Wsl_TriesPowerShellBeforeBrokenNativeBridge()
    {
        var native = new FakeNativeClipboard(null, throwOnGet: true);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        string? tmpFile = null;
        byte[] png = Png();
        var log = new CommandLog
        {
            Handler = (command, args) =>
            {
                if (command is "wl-paste" or "xclip")
                {
                    return null;
                }

                if (command == "wslpath")
                {
                    tmpFile = args[1];
                    return Listing("C:\\Users\\O'Hare\\clip.png\n");
                }

                if (command == "powershell.exe")
                {
                    Assert.Contains("$path = 'C:\\Users\\O''Hare\\clip.png'", args[2]);
                    Assert.NotNull(tmpFile);
                    File.WriteAllBytes(tmpFile, png);
                    return Listing("ok\n");
                }

                throw new InvalidOperationException($"Unexpected command: {command}");
            },
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["WSL_DISTRO_NAME:Ubuntu"]) });

        Assert.NotNull(image);
        Assert.Equal(png, image.Bytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(0, native.GetImageCalls);
    }

    [Theory]
    [InlineData("darwin")]
    [InlineData("win32")]
    public async Task NonLinuxReadsNativeImageOnce(string platform)
    {
        var native = new FakeNativeClipboard(Png());
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog();
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = platform, Env = Env([]) });

        Assert.NotNull(image);
        Assert.Equal(Png(), image.Bytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(1, native.GetImageCalls);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public async Task ReturnsNullWithoutNativeHelper()
    {
        ClipboardImageApi.NativeClipboardOverride = () => null;
        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "win32", Env = Env([]) });
        Assert.Null(image);
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("win32")]
    public async Task NativeTransferErrorsPropagateWithoutFallback(string platform)
    {
        var native = new FakeNativeClipboard(null, throwOnGet: true);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog();
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardImageApi.ReadClipboardImageAsync(new ClipboardImageReadOptions
            {
                Platform = platform,
                Env = Env(["WAYLAND_DISPLAY:1", "DISPLAY::0"]),
            }));

        Assert.Equal("Broken X11 bridge", error.Message);
        Assert.Equal(platform == "linux" ? ["wl-paste", "xclip"] : [],
            log.Calls.Select(c => c.Command).ToArray());
    }

    [Fact]
    public async Task TermuxDoesNotReadImageClipboards()
    {
        var native = new FakeNativeClipboard(Png());
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog();
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "linux", Env = Env(["TERMUX_VERSION:0.119"]) });

        Assert.Null(image);
        Assert.Equal(0, native.GetImageCalls);
        Assert.Empty(log.Calls);
    }

    [Fact]
    public async Task WaylandBmpListingIsConvertedToPng()
    {
        // Port of clipboard-image-bmp-conversion.test.ts (linux / wl-paste leg): WSL2/WSLg often
        // advertises image/bmp instead of image/png.
        var native = new FakeNativeClipboard(null);
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog
        {
            Handler = (command, args) =>
            {
                if (command == "wl-paste" && args.Contains("--list-types"))
                {
                    return Listing("image/bmp\n");
                }

                if (command == "wl-paste" && args.Contains("image/bmp"))
                {
                    return Bmp();
                }

                return null;
            },
        };
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(new ClipboardImageReadOptions
        {
            Platform = "linux",
            Env = Env(["WAYLAND_DISPLAY:wayland-0"]),
        });

        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        byte[] png = image.Bytes;
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4e, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    [Fact]
    public async Task NativeBmpIsConvertedToPng()
    {
        // Port of clipboard-image-bmp-conversion.test.ts (win32 / native leg).
        var native = new FakeNativeClipboard(Bmp());
        ClipboardImageApi.NativeClipboardOverride = () => native;
        var log = new CommandLog();
        ClipboardImageApi.CommandRunnerOverride = log.RunAsync;

        var image = await ClipboardImageApi.ReadClipboardImageAsync(
            new ClipboardImageReadOptions { Platform = "win32", Env = Env([]) });

        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        byte[] png = image.Bytes;
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4e, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/webp", "webp")]
    [InlineData("image/gif", "gif")]
    [InlineData("text/plain", null)]
    public void ExtensionForImageMimeType(string mimeType, string? expected) =>
        Assert.Equal(expected, ClipboardImageApi.ExtensionForImageMimeType(mimeType));

    [Theory]
    [InlineData(new[] { "WAYLAND_DISPLAY:1", "XDG_SESSION_TYPE:x11" }, true)]
    [InlineData(new[] { "XDG_SESSION_TYPE:wayland" }, true)]
    [InlineData(new[] { "XDG_SESSION_TYPE:x11" }, false)]
    [InlineData(new string[] { }, false)]
    public void IsWaylandSession(string[] envPairs, bool expected) =>
        Assert.Equal(expected, ClipboardImageApi.IsWaylandSession(Env(envPairs)));

    private static Func<string, string?> Env(string[] pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in pairs)
        {
            int split = pair.IndexOf(':');
            map[pair[..split]] = pair[(split + 1)..];
        }

        return key => map.GetValueOrDefault(key);
    }
}

/// <summary>Ports of the behavioral expectations for tool-result image normalization.</summary>
public class ToolResultImagesTests
{
    private static ImageContent Image(string base64, string mimeType = "image/png") => new(base64, mimeType);

    [Fact]
    public async Task TextOnlyContentReturnsSameInstance()
    {
        IReadOnlyList<ContentBlock> content = [new TextContent("hello"), new TextContent("world")];
        Assert.Same(content, await ToolResultImages.NormalizeToolResultImagesAsync(content));
    }

    [Fact]
    public async Task UndecodableImageBlockIsKeptAsIs()
    {
        // The tool produced this payload; a processing failure keeps the original block.
        var block = Image(Convert.ToBase64String("garbage"u8.ToArray()), "image/png");
        IReadOnlyList<ContentBlock> content = [block];
        Assert.Same(content, await ToolResultImages.NormalizeToolResultImagesAsync(content));
    }

    [Fact]
    public async Task SmallPngWithinLimitsIsKeptAsIs()
    {
        var block = Image(ImageTestSupport.TinyPng);
        IReadOnlyList<ContentBlock> content = [block, new TextContent("note")];
        Assert.Same(content, await ToolResultImages.NormalizeToolResultImagesAsync(content));
    }

    [Fact]
    public async Task OversizedPngIsResizedAndAnnotated()
    {
        // Force a resize with a tiny byte budget; the resized payload replaces the block and the
        // dimension note lands in a following text block.
        var block = Image(ImageTestSupport.LargePng200);
        IReadOnlyList<ContentBlock> content = [block];
        var result = await ToolResultImages.NormalizeToolResultImagesAsync(content,
            new NormalizeToolResultImagesOptions
            {
                ResizeOptions = new ModelImageResizeOptions { MaxBytes = 1024, MaxWidth = 50, MaxHeight = 50 },
            });

        Assert.NotSame(content, result);
        var replacement = Assert.IsType<ImageContent>(result[0]);
        Assert.NotEqual(block.Data, replacement.Data);
        var note = Assert.IsType<TextContent>(result[1]);
        Assert.StartsWith("[Image: original 200x200", note.Text);
        Assert.Contains("Multiply coordinates by", note.Text);
    }

    [Fact]
    public async Task ModelResizeProfileIsApplied()
    {
        // The model's resize profile must reach the resizer (the TS runtime test pins this shape).
        var block = Image(ImageTestSupport.MediumPng100);
        var result = await ToolResultImages.NormalizeToolResultImagesAsync([block],
            new NormalizeToolResultImagesOptions
            {
                ResizeOptions = new ModelImageResizeOptions
                {
                    MaxWidth = 10,
                    MaxHeight = 10,
                    MaxBytes = 1024 * 1024,
                    JpegQuality = 70,
                },
            });

        var replacement = Assert.IsType<ImageContent>(result[0]);
        byte[] png = Convert.FromBase64String(replacement.Data);
        Assert.True(ImageTestSupport.PngDimensions(png).Width <= 10);
        Assert.True(ImageTestSupport.PngDimensions(png).Height <= 10);
    }

    [Fact]
    public async Task AutoResizeDisabledLeavesOversizedImageUntouched()
    {
        var block = Image(ImageTestSupport.LargePng200);
        IReadOnlyList<ContentBlock> content = [block];
        Assert.Same(content, await ToolResultImages.NormalizeToolResultImagesAsync(
            content, new NormalizeToolResultImagesOptions { AutoResizeImages = false }));
    }
}
