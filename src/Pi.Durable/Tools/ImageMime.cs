namespace Pi.Durable.Tools;

/// <summary>
/// 受支持图片格式的探测。对应 TS <c>tools/image.ts</c>。
/// </summary>
/// <remarks>
/// 只读文件头部即可判定：JPEG / PNG（排除 APNG）/ GIF / WebP / BMP。PNG 需要沿 chunk 链走到第一个
/// <c>acTL</c> 或 <c>IDAT</c>，因此对整文件提供「按块读取」的来源类型，不必把文件读进内存。
/// </remarks>
public static class ImageMime
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>除 APNG chunk 遍历外各检查所需的字节数：BMP 读到偏移 29。</summary>
    private const int HeaderBytes = 32;

    private const int BlockBytes = 64 * 1024;

    /// <summary>「<paramref name="size"/> 字节文件」的位置读来源。对应 TS <c>ByteSource</c>。</summary>
    public interface IByteSource
    {
        long Size { get; }

        Task<byte[]> ReadAsync(long offset, int length);
    }

    /// <summary>
    /// 整文件的 <see cref="DetectSupportedImageMimeType"/>：只读头部，PNG 时再读 chunk 头直到首个
    /// <c>acTL</c> 或 <c>IDAT</c>。对应 TS <c>detectSupportedImageMimeTypeOf</c>。
    /// </summary>
    public static async Task<string?> DetectSupportedImageMimeTypeOfAsync(IByteSource source)
    {
        var header = await source.ReadAsync(0, HeaderBytes).ConfigureAwait(false);
        if (!StartsWith(header, PngSignature)) return DetectSupportedImageMimeType(header);
        return IsPng(header) && !await IsAnimatedPngOfAsync(source).ConfigureAwait(false)
            ? "image/png"
            : null;
    }

    /// <summary>按块读取的 <c>isAnimatedPng</c>。对应 TS <c>isAnimatedPngOf</c>。</summary>
    private static async Task<bool> IsAnimatedPngOfAsync(IByteSource source)
    {
        byte[] block = [];
        long blockStart = 0;
        async Task<byte[]> BytesAtAsync(long offset, int length)
        {
            if (offset < blockStart || offset + length > blockStart + block.Length)
            {
                blockStart = offset;
                block = await source.ReadAsync(offset, BlockBytes).ConfigureAwait(false);
            }
            var from = (int)(offset - blockStart);
            var to = Math.Min(block.Length, from + length);
            if (from >= block.Length) return [];
            return block[from..to];
        }

        var offset = (long)PngSignature.Length;
        while (offset + 8 <= source.Size)
        {
            var chunkHeader = await BytesAtAsync(offset, 8).ConfigureAwait(false);
            if (chunkHeader.Length < 8) return false;
            var chunkLength = ReadUint32Be(chunkHeader, 0);
            if (StartsWithAscii(chunkHeader, 4, "acTL")) return true;
            if (StartsWithAscii(chunkHeader, 4, "IDAT")) return false;
            var nextOffset = offset + 8 + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > source.Size) return false;
            offset = nextOffset;
        }

        return false;
    }

    /// <summary>从字节缓冲区判定受支持的图片 MIME；不属于受支持格式时为 null。对应 TS <c>detectSupportedImageMimeType</c>。</summary>
    public static string? DetectSupportedImageMimeType(byte[] buffer)
    {
        if (StartsWith(buffer, [0xff, 0xd8, 0xff])) return ByteAt(buffer, 3) == 0xf7 ? null : "image/jpeg";
        if (StartsWith(buffer, PngSignature)) return IsPng(buffer) && !IsAnimatedPng(buffer) ? "image/png" : null;
        if (StartsWithAscii(buffer, 0, "GIF87a") || StartsWithAscii(buffer, 0, "GIF89a")) return "image/gif";
        if (StartsWithAscii(buffer, 0, "RIFF") && StartsWithAscii(buffer, 8, "WEBP")) return "image/webp";
        if (StartsWithAscii(buffer, 0, "BM") && IsBmp(buffer)) return "image/bmp";
        return null;
    }

    private static bool IsPng(byte[] buffer)
        => buffer.Length >= 16
           && ReadUint32Be(buffer, PngSignature.Length) == 13
           && StartsWithAscii(buffer, 12, "IHDR");

    private static bool IsAnimatedPng(byte[] buffer)
    {
        var offset = PngSignature.Length;
        while (offset + 8 <= buffer.Length)
        {
            var chunkLength = ReadUint32Be(buffer, offset);
            var chunkTypeOffset = offset + 4;
            if (StartsWithAscii(buffer, chunkTypeOffset, "acTL")) return true;
            if (StartsWithAscii(buffer, chunkTypeOffset, "IDAT")) return false;
            var nextOffset = offset + 8L + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > buffer.Length) return false;
            offset = (int)nextOffset;
        }

        return false;
    }

    private static bool IsBmp(byte[] buffer)
    {
        if (buffer.Length < 26) return false;
        var declaredFileSize = ReadUint32Le(buffer, 2);
        var pixelDataOffset = ReadUint32Le(buffer, 10);
        var dibHeaderSize = ReadUint32Le(buffer, 14);
        if (declaredFileSize != 0 && declaredFileSize < 26) return false;
        if (pixelDataOffset < 14 + dibHeaderSize) return false;
        if (declaredFileSize != 0 && pixelDataOffset >= declaredFileSize) return false;

        long colorPlanes;
        long bitsPerPixel;
        if (dibHeaderSize == 12)
        {
            colorPlanes = ReadUint16Le(buffer, 22);
            bitsPerPixel = ReadUint16Le(buffer, 24);
        }
        else if (dibHeaderSize is >= 40 and <= 124)
        {
            if (buffer.Length < 30) return false;
            colorPlanes = ReadUint16Le(buffer, 26);
            bitsPerPixel = ReadUint16Le(buffer, 28);
        }
        else
        {
            return false;
        }

        return colorPlanes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    private static int ByteAt(byte[] buffer, int index) => index >= 0 && index < buffer.Length ? buffer[index] : 0;

    private static long ReadUint16Le(byte[] buffer, int offset)
        => ByteAt(buffer, offset) + ((long)ByteAt(buffer, offset + 1) << 8);

    private static long ReadUint32Be(byte[] buffer, int offset)
        => (long)ByteAt(buffer, offset) * 0x1000000
           + ((long)ByteAt(buffer, offset + 1) << 16)
           + ((long)ByteAt(buffer, offset + 2) << 8)
           + ByteAt(buffer, offset + 3);

    private static long ReadUint32Le(byte[] buffer, int offset)
        => ByteAt(buffer, offset)
           + ((long)ByteAt(buffer, offset + 1) << 8)
           + ((long)ByteAt(buffer, offset + 2) << 16)
           + (long)ByteAt(buffer, offset + 3) * 0x1000000;

    private static bool StartsWith(byte[] buffer, byte[] bytes)
    {
        if (buffer.Length < bytes.Length) return false;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (buffer[i] != bytes[i]) return false;
        }

        return true;
    }

    private static bool StartsWithAscii(byte[] buffer, int offset, string text)
    {
        if (buffer.Length < offset + text.Length) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != text[i]) return false;
        }

        return true;
    }
}
