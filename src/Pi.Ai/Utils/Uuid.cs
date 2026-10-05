using System.Security.Cryptography;

namespace Pi.Ai.Utils;

/// <summary>
/// 时间有序 UUIDv7 生成器（含同毫秒序列推进）。对应 TS <c>utils/uuid.ts</c> 的
/// <c>uuidv7</c>——单调性语义一致：普通调用取「当前时间与上次时间较大者」，同毫秒复用
/// 41 位序列；显式时间戳（follower id）保留原值且不推进状态。
/// </summary>
public static class Uuid
{
    private const long MaxTimestamp = 0xffffffffffffL;
    private const long MaxSequence = (1L << 41) - 1;

    private static readonly object Gate = new();
    private static long _lastOrdinaryTimestamp = -1;
    private static long _sequence = -1; // -1 表示未初始化（首次用随机数播种）

    /// <summary>生成 UUIDv7；<paramref name="timestampMs"/> 提供时保留该时间戳。</summary>
    public static string Uuidv7(long? timestampMs = null)
    {
        var requested = timestampMs ?? DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (requested < 0 || requested > MaxTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampMs),
                $"UUIDv7 timestamp must be an integer between 0 and {MaxTimestamp}");
        }

        lock (Gate)
        {
            long effective;
            if (timestampMs is null)
            {
                effective = Math.Max(requested, _lastOrdinaryTimestamp);
                _lastOrdinaryTimestamp = effective;
            }
            else
            {
                effective = requested;
            }

            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);

            if (_sequence < 0)
            {
                // 首次：以随机字节播种 41 位序列（TS 用 bytes[1..6]）。
                _sequence = ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
                    | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
            }
            else
            {
                if (_sequence == MaxSequence)
                {
                    throw new InvalidOperationException("UUIDv7 generator sequence exhausted");
                }
                _sequence++;
            }

            for (var index = 5; index >= 0; index--)
            {
                bytes[index] = (byte)(effective >> ((5 - index) * 8) & 0xff);
            }
            bytes[6] = (byte)(0x70 | (_sequence >> 37 & 0x0f));
            bytes[7] = (byte)(_sequence >> 29 & 0xff);
            bytes[8] = (byte)(0x80 | (_sequence >> 23 & 0x3f));
            bytes[9] = (byte)(_sequence >> 15 & 0xff);
            bytes[10] = (byte)(_sequence >> 7 & 0xff);
            bytes[11] = (byte)(((uint)(_sequence & 0x7f) << 1) | (uint)(bytes[11] & 0x01));

            return ToHex(bytes);
        }
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        Span<char> hex = stackalloc char[32];
        const string digits = "0123456789abcdef";
        for (var index = 0; index < bytes.Length; index++)
        {
            hex[index * 2] = digits[bytes[index] >> 4];
            hex[index * 2 + 1] = digits[bytes[index] & 0x0f];
        }
        return $"{hex[0..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
