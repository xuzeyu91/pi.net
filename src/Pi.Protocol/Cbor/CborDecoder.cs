using System.Buffers.Binary;
using System.Text;

namespace Pi.Protocol.Cbor;

public static partial class CborCodec
{
    /// <summary>
    /// Decodes exactly one item from the protocol's strict RFC 8949 subset.
    /// <para>
    /// Value mapping: major 0/1 → <see cref="long"/> (must be within the JS safe-integer range),
    /// major 2 → <c>byte[]</c>, major 3 → <see cref="string"/>, major 4 → <see cref="List{T}"/>,
    /// major 5 → <see cref="Dictionary{TKey,TValue}"/> with string keys, major 7 →
    /// <see cref="bool"/>/<see langword="null"/>/<see cref="double"/>. Tags, indefinite
    /// lengths, and non-finite floats are rejected.
    /// </para>
    /// </summary>
    public static object? Decode(byte[] bytes, CborOptions? options = null)
    {
        var resolved = Resolve(options);
        if (bytes.LongLength > resolved.MaxByteLength)
            throw new CborError($"CBOR byte length exceeds configured limit of {resolved.MaxByteLength}");
        var reader = new CborReader(bytes, resolved);
        var value = reader.ReadItem(0);
        if (reader.Offset != bytes.Length)
            throw new CborError("CBOR payload contains trailing data");
        return value;
    }

    private sealed class CborReader(byte[] bytes, ResolvedCborOptions options)
    {
        public int Offset { get; private set; }

        public object? ReadItem(int depth)
        {
            if (depth > options.MaxDepth)
                throw new CborError($"CBOR nesting depth exceeds configured limit of {options.MaxDepth}");
            var initial = ReadByte();
            var majorType = initial >> 5;
            var additionalInformation = (uint)(initial & 0x1f);

            switch (majorType)
            {
                case 0:
                    return (long)ReadArgument(additionalInformation);
                case 1:
                {
                    var value = -1L - (long)ReadArgument(additionalInformation);
                    if (Math.Abs(value) > MaxSafeInteger)
                        throw new CborError("Decoded CBOR integer is outside the safe range");
                    return value;
                }
                case 2:
                {
                    var length = ReadLength(additionalInformation, "byte string", options.MaxByteLength);
                    return ReadBytes(length).ToArray();
                }
                case 3:
                {
                    var length = ReadLength(additionalInformation, "text string", options.MaxByteLength);
                    var bytes = ReadBytes(length).ToArray();
                    try
                    {
                        return new UTF8Encoding(false, true).GetString(bytes);
                    }
                    catch (DecoderFallbackException error)
                    {
                        throw new CborError("CBOR text string contains invalid UTF-8", error);
                    }
                }
                case 4:
                {
                    var length = ReadLength(additionalInformation, "array", options.MaxContainerLength);
                    var result = new List<object?>((int)Math.Min(length, int.MaxValue));
                    for (long index = 0; index < length; index++) result.Add(ReadItem(depth + 1));
                    return result;
                }
                case 5:
                {
                    var length = ReadLength(additionalInformation, "map", options.MaxContainerLength);
                    var result = new Dictionary<string, object?>((int)Math.Min(length, int.MaxValue));
                    for (long index = 0; index < length; index++)
                    {
                        var key = ReadItem(depth + 1);
                        if (key is not string text)
                            throw new CborError("CBOR map keys must be strings");
                        if (result.ContainsKey(text))
                            throw new CborError("CBOR map contains a duplicate key");
                        result[text] = ReadItem(depth + 1);
                    }
                    return result;
                }
                case 6:
                    throw new CborError("CBOR tags are not supported");
                case 7:
                    return ReadSimple(additionalInformation);
                default:
                    throw new CborError("Malformed CBOR major type");
            }
        }

        private object? ReadSimple(uint additionalInformation) => additionalInformation switch
        {
            20 => false,
            21 => true,
            22 => null,
            27 => ReadFloat64(),
            31 => throw new CborError("CBOR break marker is not supported"),
            _ => throw new CborError("Unsupported CBOR simple value or floating-point width"),
        };

        private double ReadFloat64()
        {
            var value = BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8));
            if (!double.IsFinite(value))
                throw new CborError("Decoded CBOR number must be finite");
            if (value == Math.Truncate(value) && Math.Abs(value) > MaxSafeInteger)
                throw new CborError("Decoded CBOR integer is outside the safe range");
            return value;
        }

        private long ReadLength(uint additionalInformation, string kind, long limit)
        {
            if (additionalInformation == 31)
                throw new CborError($"Indefinite-length CBOR {kind}s are not supported");
            var length = (long)ReadArgument(additionalInformation);
            if (length > limit)
                throw new CborError($"CBOR {kind} length exceeds configured limit of {limit}");
            return length;
        }

        private ulong ReadArgument(uint additionalInformation)
        {
            if (additionalInformation < 24) return additionalInformation;
            switch (additionalInformation)
            {
                case 24:
                    return ReadByte();
                case 25:
                {
                    var bytes = ReadBytes(2);
                    return (uint)(bytes[0] * 0x100 + bytes[1]);
                }
                case 26:
                {
                    var bytes = ReadBytes(4);
                    return (ulong)bytes[0] << 24 | (ulong)bytes[1] << 16 | (ulong)bytes[2] << 8 | bytes[3];
                }
                case 27:
                {
                    var bytes = ReadBytes(8);
                    var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
                    if (value > (ulong)MaxSafeInteger)
                        throw new CborError("Decoded CBOR integer or length is outside the safe range");
                    return value;
                }
                case 31:
                    throw new CborError("Indefinite-length CBOR items are not supported");
                default:
                    throw new CborError("Malformed CBOR additional information");
            }
        }

        private byte ReadByte()
        {
            if (Offset >= bytes.Length)
                throw new CborError("Truncated CBOR payload");
            return bytes[Offset++];
        }

        private ReadOnlySpan<byte> ReadBytes(long length)
        {
            if (length > bytes.Length - Offset)
                throw new CborError("Truncated CBOR payload");
            var value = bytes.AsSpan(Offset, (int)length);
            Offset += (int)length;
            return value;
        }
    }
}
