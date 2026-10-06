using System.Text;

namespace Pi.Durable.Env;

/// <summary>
/// 与「整体输入一次性 <c>new TextDecoder().decode(bytes)</c>」一致的解码。Node 的流式解码器在跨块边界的 BOM 处理
/// 上可能丢弃 U+FEFF，所以这些解码器关闭 BOM 处理并自行丢弃起始的标记。对应 TS <c>env/decode.ts</c>（C# 命名 Decoding 以避开方法名冲突）。
/// </summary>
public static class Decoding
{
    /// <summary>
    /// 字节区间的流式解码器；从文件头开始的调用者自行跳过起始标记。
    /// 等价 TS <c>new TextDecoder("utf-8", { ignoreBOM: true })</c>：<b>不</b>剥离 BOM，非法字节替换为 U+FFFD。
    /// .NET 的 <see cref="Encoding.UTF8"/>.GetDecoder() 是 BOM 感知的（会吞掉起始 BOM），因此这里显式关闭 BOM 检测。
    /// </summary>
    public static Decoder RangeDecoder() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();

    /// <summary>整体解码时输入的头三个字节是否会被当作字节序标记丢弃。</summary>
    public static bool StartsWithBom(ReadOnlySpan<byte> firstBytes) =>
        firstBytes.Length >= 3 && firstBytes[0] == 0xef && firstBytes[1] == 0xbb && firstBytes[2] == 0xbf;
}

/// <summary>
/// 像整体解码一样逐块解码一个流。对应 TS <c>StreamDecoder</c>。.NET 的 <see cref="Decoder"/> 在 Convert 调用之间
/// 自行保存末尾不完整的字节序列（等价于 WHATWG 流式解码），调用方无需回喂。
/// </summary>
public sealed class StreamDecoder
{
    private readonly Decoder _decoder = Decoding.RangeDecoder();
    private bool _started;

    /// <summary>bytes 的文本，扣住不完整的字符；不带 bytes 表示流结束（冲刷）。</summary>
    public string Decode(byte[]? bytes)
    {
        string text;
        if (bytes is null)
        {
            var flushChars = new char[4];
            _decoder.Convert(Array.Empty<byte>(), 0, 0, flushChars, 0, flushChars.Length, flush: true,
                out _, out var flushed, out _);
            text = new string(flushChars, 0, flushed);
        }
        else if (bytes.Length == 0)
        {
            text = string.Empty;
        }
        else
        {
            // UTF-8 中每个字节至多产出 1 个字符（ASCII 一对一；多字节序列每字节更少），尾部积压至多 3 字节。
            var chars = new char[bytes.Length + 4];
            _decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false,
                out _, out var charsUsed, out _);
            text = new string(chars, 0, charsUsed);
        }
        if (_started || text.Length == 0) return text;
        _started = true;
        // U+FEFF 只会编码为 EF BB BF，所以流起始的 U+FEFF 正好是一个字节序标记。
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }
}
