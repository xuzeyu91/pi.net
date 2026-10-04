using System.Buffers.Binary;
using System.Text;

namespace Pi.Protocol.Cbor;

public static partial class CborCodec
{
    /// <summary>
    /// Encodes the protocol's strict, definite-length RFC 8949 subset.
    /// <para>
    /// Value mapping (mirrors the TS implementation, where numbers are doubles):
    /// <list type="bullet">
    /// <item><c>null</c>, <see cref="bool"/> → CBOR null/booleans</item>
    /// <item>integral <see cref="long"/>/<see cref="int"/> within the JS safe-integer range → major 0/1</item>
    /// <item><see cref="double"/> (or integral values outside the safe range check) → float64</item>
    /// <item><see cref="string"/> → major 3 (must round-trip valid Unicode)</item>
    /// <item><c>byte[]</c> → major 2</item>
    /// <item><see cref="System.Collections.IList"/> → major 4 (definite length)</item>
    /// <item><see cref="System.Collections.Generic.IDictionary{String, Object}"/> → major 5, string keys, null-valued entries skipped (TS undefined)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static byte[] Encode(object? value, CborOptions? options = null)
    {
        var resolved = Resolve(options);
        var writer = new CborWriter(resolved.MaxByteLength);
        EncodeValue(writer, Normalize(value), resolved, 0, new HashSet<object>());
        return writer.Finish();
    }

    /// <summary>
    /// 把 System.Text.Json 的 JsonNode 树归一化为 CBOR 值模型（JsonObject →
    /// Dictionary，JsonArray → List，JsonValue → 标量），让 RPC 层的 JSON 载荷
    /// 可以直接进 CBOR 编码。
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        System.Text.Json.Nodes.JsonObject obj => obj.ToDictionary(kv => kv.Key,
            kv => Normalize(kv.Value is System.Text.Json.Nodes.JsonNode n ? n : null)),
        System.Text.Json.Nodes.JsonArray array => array.Select(n => Normalize(n)).ToList<object?>(),
        System.Text.Json.Nodes.JsonValue jsonValue => jsonValue.TryGetValue<double>(out var d)
            ? (object)(double.IsInteger(d) ? (long)d : d)
            : jsonValue.TryGetValue<string>(out var s)
                ? s
                : jsonValue.TryGetValue<bool>(out var b) ? b : null,
        // 容器递归：普通字典/列表里也可能嵌着 JsonNode。
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        List<object?> list => list.Select(Normalize).ToList<object?>(),
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        IReadOnlyList<object?> list => list.Select(Normalize).ToList<object?>(),
        _ => value,
    };

    private static bool IsInteger(double d) => d == System.Math.Truncate(d) && !double.IsInfinity(d);

    private static void EncodeValue(
        CborWriter writer,
        object? value,
        ResolvedCborOptions options,
        int depth,
        HashSet<object> ancestors)
    {
        if (depth > options.MaxDepth)
            throw new CborError($"CBOR nesting depth exceeds configured limit of {options.MaxDepth}");

        switch (value)
        {
            case null:
                writer.WriteByte(0xf6);
                return;
            case bool flag:
                writer.WriteByte(flag ? (byte)0xf5 : (byte)0xf4);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
            {
                var integer = Convert.ToInt64(value);
                if (Math.Abs(integer) > MaxSafeInteger)
                    throw new CborError("CBOR integers must be safe JavaScript integers");
                if (integer >= 0) WriteArgument(writer, 0, (ulong)integer);
                else WriteArgument(writer, 1, (ulong)(-1 - integer));
                return;
            }
            case double:
            case float:
            case decimal:
            {
                var number = Convert.ToDouble(value);
                if (!double.IsFinite(number))
                    throw new CborError("CBOR numbers must be finite");
                writer.WriteFloat64(number);
                return;
            }
            case string text:
                EncodeText(writer, text, options);
                return;
            case byte[] bytes:
                if (bytes.Length > options.MaxByteLength)
                    throw new CborError($"CBOR byte string length exceeds configured limit of {options.MaxByteLength}");
                WriteArgument(writer, 2, (ulong)bytes.Length);
                writer.WriteBytes(bytes);
                return;
            case System.Collections.IDictionary:
            case System.Collections.IList:
            case System.Collections.IDictionaryEnumerator:
                break;
            default:
                throw new CborError($"Unsupported CBOR value type: {value.GetType().Name}");
        }

        if (!ancestors.Add(value))
            throw new CborError("CBOR values must not contain cycles");
        try
        {
            switch (value)
            {
                case System.Collections.IList list:
                {
                    if (list.Count > options.MaxContainerLength)
                        throw new CborError($"CBOR array length exceeds configured limit of {options.MaxContainerLength}");
                    WriteArgument(writer, 4, (ulong)list.Count);
                    foreach (var item in list)
                        EncodeValue(writer, item ?? throw new CborError("CBOR arrays must not contain holes or undefined values"),
                            options, depth + 1, ancestors);
                    return;
                }
                case System.Collections.IDictionary { } map:
                {
                    // Mirror TS: undefined-valued entries are filtered out BEFORE the count is written.
                    List<System.Collections.DictionaryEntry> entries = new(map.Count);
                    foreach (System.Collections.DictionaryEntry entry in map)
                    {
                        if (entry.Key is not string key)
                            throw new CborError("CBOR map keys must be strings");
                        if (entry.Value is null) continue;
                        entries.Add(entry);
                    }
                    if (entries.Count > options.MaxContainerLength)
                        throw new CborError($"CBOR map length exceeds configured limit of {options.MaxContainerLength}");
                    WriteArgument(writer, 5, (ulong)entries.Count);
                    foreach (var entry in entries)
                    {
                        EncodeText(writer, (string)entry.Key, options);
                        EncodeValue(writer, entry.Value, options, depth + 1, ancestors);
                    }
                    return;
                }
                default:
                    throw new CborError($"Unsupported CBOR value type: {value.GetType().Name}");
            }
        }
        finally
        {
            ancestors.Remove(value);
        }
    }

    private static void EncodeText(CborWriter writer, string value, ResolvedCborOptions options)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new CborError("CBOR text strings must contain valid Unicode scalar values", error);
        }

        if (bytes.Length > options.MaxByteLength)
            throw new CborError($"CBOR text string length exceeds configured limit of {options.MaxByteLength}");
        WriteArgument(writer, 3, (ulong)bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static void WriteArgument(CborWriter writer, int majorType, ulong value)
    {
        var prefix = (uint)majorType << 5;
        switch (value)
        {
            case < 24:
                writer.WriteByte((byte)(prefix | (uint)value));
                break;
            case <= 0xff:
                writer.WriteByte((byte)(prefix | 24u));
                writer.WriteByte((byte)value);
                break;
            case <= 0xffff:
                writer.WriteByte((byte)(prefix | 25u));
                writer.WriteUInt16((ushort)value);
                break;
            case <= MaxUint32:
                writer.WriteByte((byte)(prefix | 26u));
                writer.WriteUInt32((uint)value);
                break;
            default:
                writer.WriteByte((byte)(prefix | 27u));
                writer.WriteUInt64(value);
                break;
        }
    }

    private sealed class CborWriter(long maxByteLength)
    {
        private byte[] _buffer = new byte[Math.Min(256, maxByteLength)];
        private int _offset;

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_offset++] = value;
        }

        public void WriteBytes(byte[] bytes)
        {
            EnsureCapacity(bytes.Length);
            Array.Copy(bytes, 0, _buffer, _offset, bytes.Length);
            _offset += bytes.Length;
        }

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_offset, 2), value);
            _offset += 2;
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_offset, 4), value);
            _offset += 4;
        }

        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_offset, 8), value);
            _offset += 8;
        }

        public void WriteFloat64(double value)
        {
            EnsureCapacity(9);
            _buffer[_offset] = 0xfb;
            BinaryPrimitives.WriteDoubleBigEndian(_buffer.AsSpan(_offset + 1, 8), value);
            _offset += 9;
        }

        public byte[] Finish() => _buffer[.._offset];

        private void EnsureCapacity(int additionalBytes)
        {
            var required = _offset + additionalBytes;
            if (required > maxByteLength)
                throw new CborError($"CBOR byte length exceeds configured limit of {maxByteLength}");
            if (required <= _buffer.Length) return;

            var capacity = Math.Max(1, _buffer.Length);
            while (capacity < required) capacity = (int)Math.Min(maxByteLength, Math.Max(required, capacity * 2L));
            var expanded = new byte[capacity];
            Array.Copy(_buffer, expanded, _offset);
            _buffer = expanded;
        }
    }
}
