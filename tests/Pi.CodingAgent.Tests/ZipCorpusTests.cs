using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/zip.ts</c> against <c>zip-corpus.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus records the archive Node produced for each entry set, both decoded field-by-field and as
/// raw bytes. The field-by-field half is what the port is checked against; the raw half proves the
/// port reads an archive written by a foreign encoder.
/// </para>
/// <para>
/// Compressed sizes and the offsets derived from them are deliberately <em>not</em> compared: Node's
/// zlib and .NET's zlib-ng agree on small payloads but choose different match encodings on larger ones.
/// What must agree — and what the payload actually depends on — is the CRC-32, the uncompressed size
/// and the deflate round-trip. See divergence C22 in <c>docs/coding-agent-porting-status.md</c>.
/// </para>
/// </remarks>
public class ZipCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "zip-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches:\n  " + string.Join("\n  ", failures);

    [Fact]
    public void CreateArchive_MatchesTypeScript()
    {
        var failures = new List<string>();
        var clock = DateTime.Parse(
            Corpus.GetProperty("fixedLocal").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

        var expectedDos = Corpus.GetProperty("dosDateTime");
        var (time, day) = DosDateTime(clock);
        Check(failures, "dosDateTime time", expectedDos.GetProperty("time").GetInt32(), time);
        Check(failures, "dosDateTime day", expectedDos.GetProperty("day").GetInt32(), day);

        foreach (var vector in Corpus.GetProperty("archives").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var expectedEntries = ReadEntries(vector.GetProperty("entries"));
            var archive = Zip.CreateArchive(expectedEntries, clock);
            var parsed = Parse(archive);

            Check(failures, $"{label} entry count", vector.GetProperty("entryCount").GetInt32(), parsed.Locals.Count);
            Check(failures, $"{label} central directory size", vector.GetProperty("centralDirectorySize").GetInt32(), parsed.CentralDirectorySize);
            Check(failures, $"{label} trailing bytes", 0, parsed.TrailingBytes);

            var expectedLocals = vector.GetProperty("locals").EnumerateArray().ToArray();
            if (expectedLocals.Length == parsed.Locals.Count)
            {
                for (var index = 0; index < expectedLocals.Length; index++)
                {
                    var expected = expectedLocals[index];
                    var actual = parsed.Locals[index];
                    var at = $"{label} local[{index}]";

                    Check(failures, $"{at} name", expected.GetProperty("name").GetString(), actual.Name);
                    Check(failures, $"{at} version", expected.GetProperty("version").GetInt32(), actual.Version);
                    Check(failures, $"{at} flags", expected.GetProperty("flags").GetInt32(), actual.Flags);
                    Check(failures, $"{at} method", expected.GetProperty("method").GetInt32(), actual.Method);
                    Check(failures, $"{at} time", expected.GetProperty("time").GetInt32(), actual.Time);
                    Check(failures, $"{at} day", expected.GetProperty("day").GetInt32(), actual.Day);
                    Check(failures, $"{at} crc", expected.GetProperty("crc").GetUInt32(), actual.Crc);
                    Check(failures, $"{at} uncompressedSize", expected.GetProperty("uncompressedSize").GetInt32(), actual.UncompressedSize);
                    Check(failures, $"{at} nameLength", expected.GetProperty("nameLength").GetInt32(), actual.NameLength);
                    Check(failures, $"{at} extraLength", expected.GetProperty("extraLength").GetInt32(), actual.ExtraLength);

                    // The payload itself: what the deflate stream must inflate back to.
                    var expectedContent = Convert.FromBase64String(expected.GetProperty("contentBase64").GetString()!);
                    if (!expectedContent.AsSpan().SequenceEqual(actual.Content))
                    {
                        failures.Add($"{at} content: expected {expectedContent.Length} bytes, got {actual.Content.Length}");
                    }

                    // compressedSize is encoder-specific, so it is not compared against Node's. The
                    // invariant that must hold is that the bytes it claims inflate to the declared
                    // uncompressed size — which is what the CRC was taken over.
                    Check(failures, $"{at} inflated size", actual.UncompressedSize, actual.Content.Length);
                }
            }
            else
            {
                failures.Add($"{label} local count: expected {expectedLocals.Length}, got {parsed.Locals.Count}");
            }

            var expectedCentrals = vector.GetProperty("centrals").EnumerateArray().ToArray();
            if (expectedCentrals.Length == parsed.Centrals.Count)
            {
                for (var index = 0; index < expectedCentrals.Length; index++)
                {
                    var expected = expectedCentrals[index];
                    var actual = parsed.Centrals[index];
                    var at = $"{label} central[{index}]";

                    Check(failures, $"{at} name", expected.GetProperty("name").GetString(), actual.Name);
                    Check(failures, $"{at} versionMadeBy", expected.GetProperty("versionMadeBy").GetInt32(), actual.VersionMadeBy);
                    Check(failures, $"{at} versionNeeded", expected.GetProperty("versionNeeded").GetInt32(), actual.VersionNeeded);
                    Check(failures, $"{at} flags", expected.GetProperty("flags").GetInt32(), actual.Flags);
                    Check(failures, $"{at} method", expected.GetProperty("method").GetInt32(), actual.Method);
                    Check(failures, $"{at} time", expected.GetProperty("time").GetInt32(), actual.Time);
                    Check(failures, $"{at} day", expected.GetProperty("day").GetInt32(), actual.Day);
                    Check(failures, $"{at} crc", expected.GetProperty("crc").GetUInt32(), actual.Crc);
                    Check(failures, $"{at} uncompressedSize", expected.GetProperty("uncompressedSize").GetInt32(), actual.UncompressedSize);
                    Check(failures, $"{at} nameLength", expected.GetProperty("nameLength").GetInt32(), actual.NameLength);
                    Check(failures, $"{at} extraLength", expected.GetProperty("extraLength").GetInt32(), actual.ExtraLength);
                    Check(failures, $"{at} commentLength", expected.GetProperty("commentLength").GetInt32(), actual.CommentLength);
                    Check(failures, $"{at} diskStart", expected.GetProperty("diskStart").GetInt32(), actual.DiskStart);
                    Check(failures, $"{at} internalAttributes", expected.GetProperty("internalAttributes").GetInt32(), actual.InternalAttributes);
                    Check(failures, $"{at} externalAttributes", expected.GetProperty("externalAttributes").GetUInt32(), actual.ExternalAttributes);

                    // The offset must point at the matching local record; its numeric value is
                    // encoder-dependent, so the invariant is the linkage, not the number.
                    if (!parsed.LocalOffsets.TryGetValue((int)actual.LocalOffset, out var targetIndex)
                        || parsed.Locals[targetIndex].Name != expected.GetProperty("name").GetString())
                    {
                        failures.Add($"{at} localOffset {actual.LocalOffset} does not point at {expected.GetProperty("name").GetString()}");
                    }
                }
            }
            else
            {
                failures.Add($"{label} central count: expected {expectedCentrals.Length}, got {parsed.Centrals.Count}");
            }

            var expectedEocd = vector.GetProperty("eocd");
            Check(failures, $"{label} eocd diskNumber", expectedEocd.GetProperty("diskNumber").GetInt32(), parsed.Eocd.DiskNumber);
            Check(failures, $"{label} eocd centralDirectoryDisk", expectedEocd.GetProperty("centralDirectoryDisk").GetInt32(), parsed.Eocd.CentralDirectoryDisk);
            Check(failures, $"{label} eocd entriesOnDisk", expectedEocd.GetProperty("entriesOnDisk").GetInt32(), parsed.Eocd.EntriesOnDisk);
            Check(failures, $"{label} eocd totalEntries", expectedEocd.GetProperty("totalEntries").GetInt32(), parsed.Eocd.TotalEntries);
            Check(failures, $"{label} eocd centralDirectorySize", expectedEocd.GetProperty("centralDirectorySize").GetInt32(), parsed.Eocd.CentralDirectorySize);
            Check(failures, $"{label} eocd commentLength", expectedEocd.GetProperty("commentLength").GetInt32(), parsed.Eocd.CommentLength);

            // The central directory must start immediately after the last local record.
            var lastLocal = parsed.Locals.Count == 0 ? 0 : parsed.LocalEnds[^1];
            Check(failures, $"{label} eocd centralDirectoryOffset", lastLocal, parsed.Eocd.CentralDirectoryOffset);

            // Reading our own archive back is the end-to-end guarantee.
            CheckArchiveReadsBack(failures, $"{label} (own archive)", archive, expectedEntries);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// The port must read an archive Node wrote — the shape a user's unzip tool sees, and the shape the
    /// bug-report upload path has to accept.
    /// </summary>
    [Fact]
    public void ReadsArchivesWrittenByTypeScript()
    {
        var failures = new List<string>();

        foreach (var vector in Corpus.GetProperty("archives").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var archive = Convert.FromBase64String(vector.GetProperty("archiveBase64").GetString()!);
            CheckArchiveReadsBack(failures, label, archive, ReadEntries(vector.GetProperty("entries")));
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// Both directions of interop, asserted through the BCL's own reader rather than the local parser:
    /// the entry names and contents must survive a round trip, and the CRC each side recorded must be
    /// the CRC the payload actually has.
    /// </summary>
    private static void CheckArchiveReadsBack(List<string> failures, string label, byte[] archive, List<ZipEntry> expected)
    {
        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        if (zip.Entries.Count != expected.Count)
        {
            failures.Add($"{label}: expected {expected.Count} entries, read {zip.Entries.Count}");
            return;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var entry = zip.Entries[index];
            if (entry.FullName != expected[index].Name)
            {
                failures.Add($"{label}: entry[{index}] name: expected {expected[index].Name}, read {entry.FullName}");
                continue;
            }

            using var content = new MemoryStream();
            using (var source = entry.Open())
            {
                source.CopyTo(content);
            }

            if (!content.ToArray().AsSpan().SequenceEqual(expected[index].Data.Span))
            {
                failures.Add($"{label}: entry[{index}] ({entry.FullName}) content differs ({content.Length} vs {expected[index].Data.Length} bytes)");
            }
        }
    }

    private static List<ZipEntry> ReadEntries(JsonElement entries)
    {
        var result = new List<ZipEntry>();
        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString()!;
            result.Add(entry.TryGetProperty("text", out var text)
                ? ZipEntry.FromText(name, text.GetString()!)
                : ZipEntry.FromBytes(name, Convert.FromBase64String(entry.GetProperty("base64").GetString()!)));
        }

        return result;
    }

    /// <summary>The same decode the generator performs, so only the fields that must agree are compared.</summary>
    private static ParsedArchive Parse(byte[] buffer)
    {
        var locals = new List<ParsedLocal>();
        var localEnds = new List<int>();
        var localOffsets = new Dictionary<int, int>();
        var offset = 0;

        while (offset + 4 <= buffer.Length && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)) == 0x04034b50)
        {
            var compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 18));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 26));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 28));
            var dataStart = offset + 30 + nameLength + extraLength;

            localOffsets[offset] = locals.Count;
            locals.Add(new ParsedLocal(
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 6)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 10)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 14)),
                compressedSize,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 22)),
                nameLength,
                extraLength,
                Encoding.UTF8.GetString(buffer, offset + 30, nameLength),
                Inflate(buffer.AsSpan(dataStart, compressedSize))));

            offset = dataStart + compressedSize;
            localEnds.Add(offset);
        }

        var centralDirectoryOffset = offset;
        var centrals = new List<ParsedCentral>();
        while (offset + 4 <= buffer.Length && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)) == 0x02014b50)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 28));
            centrals.Add(new ParsedCentral(
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 6)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 10)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 12)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 14)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 16)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 20)),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 24)),
                nameLength,
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 30)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 32)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 34)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 36)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 38)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 42)),
                Encoding.UTF8.GetString(buffer, offset + 46, nameLength)));

            offset += 46 + nameLength;
        }

        var centralDirectorySize = offset - centralDirectoryOffset;
        var eocd = new ParsedEocd(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 6)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 8)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 10)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 12)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 16)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 20)));

        return new ParsedArchive(locals, localEnds, localOffsets, centrals, centralDirectorySize, eocd, buffer.Length - (offset + 22));
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var inflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The DOS date/time pair, mirrored from <see cref="Zip.CreateArchive"/> for the clock check.</summary>
    private static (int Time, int Day) DosDateTime(DateTime date)
    {
        var time = (date.Hour << 11) | (date.Minute << 5) | (date.Second >> 1);
        var day = ((Math.Max(1980, date.Year) - 1980) << 9) | (date.Month << 5) | date.Day;
        return (time, day);
    }

    private static void Check(List<string> failures, string label, int expected, int actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void Check(List<string> failures, string label, uint expected, uint actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void Check(List<string> failures, string label, string? expected, string? actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}: expected {expected ?? "null"}, got {actual ?? "null"}");
        }
    }

    private sealed record ParsedLocal(
        int Version,
        int Flags,
        int Method,
        int Time,
        int Day,
        uint Crc,
        int CompressedSize,
        int UncompressedSize,
        int NameLength,
        int ExtraLength,
        string Name,
        byte[] Content);

    private sealed record ParsedCentral(
        int VersionMadeBy,
        int VersionNeeded,
        int Flags,
        int Method,
        int Time,
        int Day,
        uint Crc,
        int CompressedSize,
        int UncompressedSize,
        int NameLength,
        int ExtraLength,
        int CommentLength,
        int DiskStart,
        int InternalAttributes,
        uint ExternalAttributes,
        uint LocalOffset,
        string Name);

    private sealed record ParsedEocd(
        int DiskNumber,
        int CentralDirectoryDisk,
        int EntriesOnDisk,
        int TotalEntries,
        int CentralDirectorySize,
        int CentralDirectoryOffset,
        int CommentLength);

    private sealed record ParsedArchive(
        List<ParsedLocal> Locals,
        List<int> LocalEnds,
        Dictionary<int, int> LocalOffsets,
        List<ParsedCentral> Centrals,
        int CentralDirectorySize,
        ParsedEocd Eocd,
        int TrailingBytes);
}
