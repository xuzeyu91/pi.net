namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/mime.ts</c>: sniff the image formats the providers accept.</summary>
public static class Mime
{
    private const int ImageTypeSniffBytes = 4100;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>Detect a supported image MIME type from the leading bytes, or <see langword="null"/>.</summary>
    public static string? DetectSupportedImageMimeType(ReadOnlySpan<byte> buffer)
    {
        if (StartsWith(buffer, [0xff, 0xd8, 0xff]))
        {
            return buffer.Length > 3 && buffer[3] == 0xf7 ? null : "image/jpeg";
        }

        if (StartsWith(buffer, PngSignature))
        {
            return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        }

        if (StartsWithAscii(buffer, 0, "GIF87a") || StartsWithAscii(buffer, 0, "GIF89a"))
        {
            return "image/gif";
        }

        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP"))
        {
            return "image/webp";
        }

        if (StartsWithAscii(buffer, 0, "BM") && IsBmp(buffer))
        {
            return "image/bmp";
        }

        return null;
    }

    /// <summary>Read the leading bytes of a file and sniff its image MIME type.</summary>
    public static async Task<string?> DetectSupportedImageMimeTypeFromFileAsync(string filePath)
    {
        var buffer = new byte[ImageTypeSniffBytes];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous);
        var bytesRead = await stream.ReadAsync(buffer).ConfigureAwait(false);
        return DetectSupportedImageMimeType(buffer.AsSpan(0, bytesRead));
    }

    private static bool IsPng(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 16
        && ReadUint32Be(buffer, PngSignature.Length) == 13
        && StartsWithAscii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(ReadOnlySpan<byte> buffer)
    {
        long offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUint32Be(buffer, (int)offset);
            var chunkTypeOffset = (int)offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL"))
            {
                return true;
            }

            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT"))
            {
                return false;
            }

            // `chunkLength` can be up to 2^32-1, so the arithmetic stays in long to avoid the wraparound
            // JS numbers do not have.
            var nextOffset = offset + 8 + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > buffer.Length)
            {
                return false;
            }

            offset = nextOffset;
        }

        return false;
    }

    private static bool IsBmp(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 26)
        {
            return false;
        }

        long declaredFileSize = ReadUint32Le(buffer, 2);
        long pixelDataOffset = ReadUint32Le(buffer, 10);
        long dibHeaderSize = ReadUint32Le(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26)
        {
            return false;
        }

        if (pixelDataOffset < 14 + dibHeaderSize)
        {
            return false;
        }

        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize)
        {
            return false;
        }

        int colorPlanes;
        int bitsPerPixel;
        if (dibHeaderSize == 12)
        {
            colorPlanes = ReadUint16Le(buffer, 22);
            bitsPerPixel = ReadUint16Le(buffer, 24);
        }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30)
            {
                return false;
            }

            colorPlanes = ReadUint16Le(buffer, 26);
            bitsPerPixel = ReadUint16Le(buffer, 28);
        }
        else
        {
            return false;
        }

        return colorPlanes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    private static int ReadUint16Le(ReadOnlySpan<byte> buffer, int offset) =>
        (offset < buffer.Length ? buffer[offset] : 0)
        + ((offset + 1 < buffer.Length ? buffer[offset + 1] : 0) << 8);

    private static long ReadUint32Be(ReadOnlySpan<byte> buffer, int offset) =>
        ((offset < buffer.Length ? buffer[offset] : 0) * 0x1000000L)
        + ((offset + 1 < buffer.Length ? buffer[offset + 1] : 0) << 16)
        + ((offset + 2 < buffer.Length ? buffer[offset + 2] : 0) << 8)
        + (offset + 3 < buffer.Length ? buffer[offset + 3] : 0);

    private static long ReadUint32Le(ReadOnlySpan<byte> buffer, int offset) =>
        (offset < buffer.Length ? buffer[offset] : 0)
        + ((offset + 1 < buffer.Length ? buffer[offset + 1] : 0) << 8)
        + ((offset + 2 < buffer.Length ? buffer[offset + 2] : 0) << 16)
        + ((offset + 3 < buffer.Length ? buffer[offset + 3] : 0) * 0x1000000L);

    private static bool StartsWith(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> bytes)
    {
        if (buffer.Length < bytes.Length)
        {
            return false;
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            if (buffer[index] != bytes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (buffer[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }
}
