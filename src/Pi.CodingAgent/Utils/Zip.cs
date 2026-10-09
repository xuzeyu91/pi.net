using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>One member of a <see cref="Zip"/> archive: a UTF-8 name and the raw bytes to store.</summary>
/// <remarks>
/// The TS original takes <c>data: string | Uint8Array</c> and narrows with <c>typeof</c>; the two
/// factories here are the same two cases made explicit (<c>Buffer.from(string)</c> is UTF-8).
/// </remarks>
public sealed record ZipEntry(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary><c>Buffer.from(data)</c> for the <c>string</c> arm — UTF-8, no BOM.</summary>
    public static ZipEntry FromText(string name, string data) => new(name, Encoding.UTF8.GetBytes(data));

    /// <summary>The <c>Uint8Array</c> arm.</summary>
    public static ZipEntry FromBytes(string name, byte[] data) => new(name, data);
}

/// <summary>
/// Port of <c>utils/zip.ts</c>: write the small, classic ZIP archives used by bug reports.
/// </summary>
/// <remarks>
/// <para>
/// The archive is assembled by hand rather than through <see cref="ZipArchive"/>, because the point of
/// the original is the exact shape it emits: version 20, the UTF-8 name flag (<c>0x0800</c>), method 8
/// (deflate), no data descriptor and no extra fields. <see cref="ZipArchive"/> writes a different
/// combination (notably a data descriptor) and would not be a faithful port.
/// </para>
/// <para>
/// The compressed bytes themselves are <em>not</em> reproduced byte-for-byte. Node's zlib and .NET's
/// zlib-ng agree on small payloads but pick different match encodings on larger ones — for 1000
/// <c>'A'</c>s Node emits 11 bytes and .NET 12 — so the compressed size and the offsets derived from it
/// differ between the two implementations. Everything else in the archive (signatures, versions, flags,
/// method, DOS date/time, CRC-32, uncompressed size, name length, name bytes, central-directory size,
/// entry counts) is identical, and the deflate stream is plain RFC 1951 on both sides, so either side
/// reads the other's archive. See <c>docs/coding-agent-porting-status.md</c> divergence C22.
/// </para>
/// </remarks>
public static class Zip
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralDirectorySignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;

    private const int LocalHeaderLength = 30;
    private const int CentralDirectoryEntryLength = 46;
    private const int EndOfCentralDirectoryLength = 22;

    /// <summary>Compression method 8: deflate (RFC 1951).</summary>
    private const int DeflateMethod = 8;

    /// <summary>General-purpose bit 11: the file name is UTF-8.</summary>
    private const int Utf8NameFlag = 0x0800;

    /// <summary>Version 2.0 — the floor for deflate.</summary>
    private const int Version = 20;

    /// <summary>The ZIP epoch: a DOS date cannot represent anything earlier.</summary>
    private const int MinimumYear = 1980;

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Build the archive bytes. <paramref name="now"/> is the clock the DOS date/time words are derived
    /// from; it defaults to the local wall clock, which is what the TS <c>new Date()</c> reads (the
    /// original is not injectable, so this parameter exists purely so the differential corpus can pin
    /// the clock).
    /// </summary>
    public static byte[] CreateArchive(IReadOnlyList<ZipEntry> entries, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var (time, day) = DosDateTime(now ?? DateTime.Now);
        var files = new List<byte[]>();
        var directory = new List<byte[]>();
        var offset = 0u;

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var name = Encoding.UTF8.GetBytes(entry.Name);
            var data = entry.Data;
            var compressed = DeflateRaw(data.Span);
            var checksum = Crc32(data.Span);

            var local = new byte[LocalHeaderLength];
            WriteUInt32(local, 0, LocalHeaderSignature);
            WriteUInt16(local, 4, Version);
            WriteUInt16(local, 6, Utf8NameFlag);
            WriteUInt16(local, 8, DeflateMethod);
            WriteUInt16(local, 10, time);
            WriteUInt16(local, 12, day);
            WriteUInt32(local, 14, checksum);
            WriteUInt32(local, 18, (uint)compressed.Length);
            WriteUInt32(local, 22, (uint)data.Length);
            WriteUInt16(local, 26, name.Length);

            var central = new byte[CentralDirectoryEntryLength];
            WriteUInt32(central, 0, CentralDirectorySignature);
            WriteUInt16(central, 4, Version);
            WriteUInt16(central, 6, Version);
            WriteUInt16(central, 8, Utf8NameFlag);
            WriteUInt16(central, 10, DeflateMethod);
            WriteUInt16(central, 12, time);
            WriteUInt16(central, 14, day);
            WriteUInt32(central, 16, checksum);
            WriteUInt32(central, 20, (uint)compressed.Length);
            WriteUInt32(central, 24, (uint)data.Length);
            WriteUInt16(central, 28, name.Length);
            WriteUInt32(central, 42, offset);

            files.Add(local);
            files.Add(name);
            files.Add(compressed);
            directory.Add(central);
            directory.Add(name);
            offset += (uint)(local.Length + name.Length + compressed.Length);
        }

        var centralDirectory = Concat(directory);
        var end = new byte[EndOfCentralDirectoryLength];
        WriteUInt32(end, 0, EndOfCentralDirectorySignature);
        WriteUInt16(end, 8, entries.Count);
        WriteUInt16(end, 10, entries.Count);
        WriteUInt32(end, 12, (uint)centralDirectory.Length);
        WriteUInt32(end, 16, offset);

        var result = new byte[files.Sum(part => part.Length) + centralDirectory.Length + end.Length];
        var position = 0;
        foreach (var part in files)
        {
            part.CopyTo(result, position);
            position += part.Length;
        }

        centralDirectory.CopyTo(result, position);
        end.CopyTo(result, position + centralDirectory.Length);
        return result;
    }

    /// <summary>The TS <c>writeZipArchive</c>: build the archive and write it to <paramref name="filePath"/>.</summary>
    public static Task WriteArchiveAsync(
        string filePath,
        IReadOnlyList<ZipEntry> entries,
        DateTime? now = null,
        CancellationToken cancellationToken = default) =>
        File.WriteAllBytesAsync(filePath, CreateArchive(entries, now), cancellationToken);

    /// <summary>
    /// The DOS date/time pair. Note the two resolutions the format imposes and the TS code inherits:
    /// seconds are stored in 2-second steps (<c>seconds &gt;&gt; 1</c>), and years are clamped up to
    /// 1980, so a 1970 timestamp reports as 1980. <c>getMonth()</c> is 0-based in JS, so the stored
    /// month is <c>getMonth() + 1</c> — which is exactly <see cref="DateTime.Month"/>.
    /// </summary>
    private static (int Time, int Day) DosDateTime(DateTime date)
    {
        var time = (date.Hour << 11) | (date.Minute << 5) | (date.Second >> 1);
        var day = ((Math.Max(MinimumYear, date.Year) - MinimumYear) << 9) | (date.Month << 5) | date.Day;
        return (time, day);
    }

    /// <summary>Raw deflate (RFC 1951), i.e. no zlib or gzip wrapper — <c>zlib.deflateRawSync</c>.</summary>
    private static byte[] DeflateRaw(ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The CRC-32 the ZIP format uses (reflected, polynomial <c>0xEDB88320</c>, inverted in and out) —
    /// the same value as <c>zlib.crc32</c>. Verified against the corpus; <c>System.IO.Hashing.Crc32</c>
    /// would need an extra package for no behavioural gain.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var position = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, position);
            position += part.Length;
        }

        return result;
    }

    /// <summary>
    /// <c>Buffer.writeUInt16LE</c>. The cast is checked, so an out-of-range value throws rather than
    /// silently truncating — <c>Buffer</c> raises <c>ERR_OUT_OF_RANGE</c> in the same situation. The
    /// only reachable case is a file name longer than 65535 UTF-8 bytes.
    /// </summary>
    private static void WriteUInt16(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), checked((ushort)value));

    /// <summary><c>Buffer.writeUInt32LE</c>.</summary>
    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
}
