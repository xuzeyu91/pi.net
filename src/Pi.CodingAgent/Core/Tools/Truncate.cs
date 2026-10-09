using System.Globalization;
using System.Text;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Which limit won: <c>lines</c>, <c>bytes</c>, or null when nothing was truncated.</summary>
/// <remarks>
/// The TS is the literal union <c>"lines" | "bytes" | null</c>; the port keeps
/// <see cref="string"/>? with these constants, the same device as <see cref="ToolExposure"/>.
/// </remarks>
public static class TruncatedBy
{
    public const string Lines = "lines";

    public const string Bytes = "bytes";
}

/// <summary>Shared truncation utilities for tool outputs. Port of <c>core/tools/truncate.ts</c>.</summary>
/// <remarks>
/// <para>
/// Truncation is based on two independent limits - whichever is hit first wins: the line limit
/// (default 2000 lines) and the byte limit (default 50KB). Partial lines are never returned,
/// except for the bash tail-truncation edge case.
/// </para>
/// <para>
/// This is the coding-agent copy of the shared truncation helpers. The durable package ports its
/// own <c>truncate.ts</c> separately (<see cref="Pi.Durable.Truncate"/>): that variant carries
/// <c>truncateHeadOf</c> and no tail/line/middle helpers, so the two are kept apart rather than
/// unified.
/// </para>
/// </remarks>
public static class Truncate
{
    public const int DefaultMaxLines = 2000;

    public const int DefaultMaxBytes = 50 * 1024; // 50KB

    public const int GrepMaxLineLength = 500; // Max chars per grep match line

    /// <summary>Port of the TS <c>TruncationResult</c>.</summary>
    public sealed record TruncationResult
    {
        /// <summary>The truncated content.</summary>
        public required string Content { get; init; }

        /// <summary>Whether truncation occurred.</summary>
        public required bool Truncated { get; init; }

        /// <summary>Which limit was hit: <see cref="TruncatedBy.Lines"/>, <see cref="TruncatedBy.Bytes"/>, or null.</summary>
        public string? TruncatedBy { get; init; }

        /// <summary>Total number of lines in the original content.</summary>
        public required int TotalLines { get; init; }

        /// <summary>Total number of bytes in the original content.</summary>
        public required int TotalBytes { get; init; }

        /// <summary>Number of complete lines in the truncated output.</summary>
        public required int OutputLines { get; init; }

        /// <summary>Number of bytes in the truncated output.</summary>
        public required int OutputBytes { get; init; }

        /// <summary>Whether the last line was partially truncated (only for tail truncation edge case).</summary>
        public required bool LastLinePartial { get; init; }

        /// <summary>Whether the first line exceeded the byte limit (for head truncation).</summary>
        public required bool FirstLineExceedsLimit { get; init; }

        /// <summary>The max lines limit that was applied.</summary>
        public required int MaxLines { get; init; }

        /// <summary>The max bytes limit that was applied.</summary>
        public required int MaxBytes { get; init; }
    }

    /// <summary>Port of the TS <c>TruncationOptions</c>.</summary>
    public sealed record TruncationOptions
    {
        /// <summary>Maximum number of lines (default 2000).</summary>
        public int? MaxLines { get; init; }

        /// <summary>Maximum number of bytes (default 50KB).</summary>
        public int? MaxBytes { get; init; }
    }

    /// <summary>Port of the TS <c>MiddleTruncationResult</c>.</summary>
    public sealed record MiddleTruncationResult
    {
        /// <summary>The start and the end of the content with a <c>…N chars truncated…</c> marker between them.</summary>
        public required string Content { get; init; }

        /// <summary>Whether the middle was cut out.</summary>
        public required bool Truncated { get; init; }

        /// <summary>Characters left out.</summary>
        public required int RemovedChars { get; init; }

        public required int TotalBytes { get; init; }

        public required int TotalLines { get; init; }
    }

    /// <summary>UTF-8 byte length. The TS <c>Buffer.byteLength(text, "utf-8")</c>.</summary>
    private static int ByteLength(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>Split on \n for counting; an empty string has no lines; one trailing newline is ignored.</summary>
    private static List<string> SplitLinesForCounting(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = new List<string>(content.Split('\n'));
        if (content.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Format bytes as human-readable size. Port of the TS <c>formatSize</c>.</summary>
    public static string FormatSize(int bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes}B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{(bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)}KB";
        }

        return $"{(bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)}MB";
    }

    /// <summary>
    /// Truncate content from the head (keep first N lines/bytes). Suitable for file reads where
    /// you want to see the beginning. Never returns partial lines; if the first line alone exceeds
    /// the byte limit, returns empty content with <see cref="TruncationResult.FirstLineExceedsLimit"/>.
    /// </summary>
    public static TruncationResult TruncateHead(string content, TruncationOptions? options = null)
    {
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;

        var totalBytes = ByteLength(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        // Check if no truncation needed.
        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult
            {
                Content = content,
                Truncated = false,
                TruncatedBy = null,
                TotalLines = totalLines,
                TotalBytes = totalBytes,
                OutputLines = totalLines,
                OutputBytes = totalBytes,
                LastLinePartial = false,
                FirstLineExceedsLimit = false,
                MaxLines = maxLines,
                MaxBytes = maxBytes,
            };
        }

        // Check if first line alone exceeds byte limit.
        var firstLineBytes = lines.Count > 0 ? ByteLength(lines[0]) : 0;
        if (firstLineBytes > maxBytes)
        {
            return new TruncationResult
            {
                Content = "",
                Truncated = true,
                TruncatedBy = TruncatedBy.Bytes,
                TotalLines = totalLines,
                TotalBytes = totalBytes,
                OutputLines = 0,
                OutputBytes = 0,
                LastLinePartial = false,
                FirstLineExceedsLimit = true,
                MaxLines = maxLines,
                MaxBytes = maxBytes,
            };
        }

        // Collect complete lines that fit.
        var outputLinesArr = new List<string>();
        var outputBytesCount = 0;
        var truncatedBy = TruncatedBy.Lines;

        for (var i = 0; i < lines.Count && i < maxLines; i++)
        {
            var line = lines[i];
            var lineBytes = ByteLength(line) + (i > 0 ? 1 : 0); // +1 for newline

            if (outputBytesCount + lineBytes > maxBytes)
            {
                truncatedBy = TruncatedBy.Bytes;
                break;
            }

            outputLinesArr.Add(line);
            outputBytesCount += lineBytes;
        }

        // If we exited due to line limit.
        if (outputLinesArr.Count >= maxLines && outputBytesCount <= maxBytes)
        {
            truncatedBy = TruncatedBy.Lines;
        }

        var outputContent = string.Join("\n", outputLinesArr);
        var finalOutputBytes = ByteLength(outputContent);

        return new TruncationResult
        {
            Content = outputContent,
            Truncated = true,
            TruncatedBy = truncatedBy,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = outputLinesArr.Count,
            OutputBytes = finalOutputBytes,
            LastLinePartial = false,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };
    }

    /// <summary>
    /// Truncate content from the tail (keep last N lines/bytes). Suitable for bash output where
    /// you want to see the end (errors, final results). May return a partial first line if the
    /// last line of the original content exceeds the byte limit.
    /// </summary>
    public static TruncationResult TruncateTail(string content, TruncationOptions? options = null)
    {
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;

        var totalBytes = ByteLength(content);
        var lines = SplitLinesForCounting(content);
        var totalLines = lines.Count;

        // Check if no truncation needed.
        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult
            {
                Content = content,
                Truncated = false,
                TruncatedBy = null,
                TotalLines = totalLines,
                TotalBytes = totalBytes,
                OutputLines = totalLines,
                OutputBytes = totalBytes,
                LastLinePartial = false,
                FirstLineExceedsLimit = false,
                MaxLines = maxLines,
                MaxBytes = maxBytes,
            };
        }

        // Work backwards from the end.
        var outputLinesArr = new List<string>();
        var outputBytesCount = 0;
        var truncatedBy = TruncatedBy.Lines;
        var lastLinePartial = false;

        for (var i = lines.Count - 1; i >= 0 && outputLinesArr.Count < maxLines; i--)
        {
            var line = lines[i];
            var lineBytes = ByteLength(line) + (outputLinesArr.Count > 0 ? 1 : 0); // +1 for newline

            if (outputBytesCount + lineBytes > maxBytes)
            {
                truncatedBy = TruncatedBy.Bytes;
                // Edge case: if we haven't added ANY lines yet and this line exceeds maxBytes,
                // take the end of the line (partial).
                if (outputLinesArr.Count == 0)
                {
                    var truncatedLine = TruncateStringToBytesFromEnd(line, maxBytes);
                    outputLinesArr.Insert(0, truncatedLine);
                    outputBytesCount = ByteLength(truncatedLine);
                    lastLinePartial = true;
                }

                break;
            }

            outputLinesArr.Insert(0, line);
            outputBytesCount += lineBytes;
        }

        // If we exited due to line limit.
        if (outputLinesArr.Count >= maxLines && outputBytesCount <= maxBytes)
        {
            truncatedBy = TruncatedBy.Lines;
        }

        var outputContent = string.Join("\n", outputLinesArr);
        var finalOutputBytes = ByteLength(outputContent);

        return new TruncationResult
        {
            Content = outputContent,
            Truncated = true,
            TruncatedBy = truncatedBy,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = outputLinesArr.Count,
            OutputBytes = finalOutputBytes,
            LastLinePartial = lastLinePartial,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };
    }

    /// <summary>Truncate a string to fit within a byte limit (from the end), cutting at UTF-8 boundaries.</summary>
    private static string TruncateStringToBytesFromEnd(string str, int maxBytes)
    {
        var buf = Encoding.UTF8.GetBytes(str);
        if (buf.Length <= maxBytes)
        {
            return str;
        }

        // Start from the end, skip maxBytes back.
        var start = buf.Length - maxBytes;

        // Find a valid UTF-8 boundary (start of a character).
        while (start < buf.Length && (buf[start] & 0xc0) == 0x80)
        {
            start++;
        }

        return Encoding.UTF8.GetString(buf, start, buf.Length - start);
    }

    /// <summary>Truncate a single line to max characters, adding a <c>... [truncated]</c> suffix. Used for grep match lines.</summary>
    public static (string Text, bool WasTruncated) TruncateLine(string line, int maxChars = GrepMaxLineLength)
    {
        if (line.Length <= maxChars)
        {
            return (line, false);
        }

        return ($"{line[..maxChars]}... [truncated]", true);
    }

    /// <summary>
    /// Keep the start and the end of <c>content</c>, half of <c>maxBytes</c> each, and replace the
    /// middle with a <c>…N chars truncated…</c> marker, like Codex does for tool output. Cuts only
    /// at character boundaries.
    /// </summary>
    public static MiddleTruncationResult TruncateMiddle(string content, int maxBytes)
    {
        var buf = Encoding.UTF8.GetBytes(content);
        var totalLines = SplitLinesForCounting(content).Count;
        if (buf.Length <= maxBytes)
        {
            return new MiddleTruncationResult
            {
                Content = content,
                Truncated = false,
                RemovedChars = 0,
                TotalBytes = buf.Length,
                TotalLines = totalLines,
            };
        }

        // Continuation bytes (10xxxxxx) are not character starts.
        static bool IsBoundary(byte[] buffer, int index) => index >= buffer.Length || (buffer[index] & 0xc0) != 0x80;

        var headEnd = maxBytes / 2; // Math.floor for non-negative integers
        while (headEnd > 0 && !IsBoundary(buf, headEnd))
        {
            headEnd--;
        }

        var tailStart = buf.Length - (maxBytes - maxBytes / 2);
        while (tailStart < buf.Length && !IsBoundary(buf, tailStart))
        {
            tailStart++;
        }

        var head = Encoding.UTF8.GetString(buf, 0, headEnd);
        var tail = Encoding.UTF8.GetString(buf, tailStart, buf.Length - tailStart);
        var removedChars = CountCodePoints(Encoding.UTF8.GetString(buf, headEnd, tailStart - headEnd));
        return new MiddleTruncationResult
        {
            Content = $"{head}…{removedChars} chars truncated…{tail}",
            Truncated = true,
            RemovedChars = removedChars,
            TotalBytes = buf.Length,
            TotalLines = totalLines,
        };
    }

    /// <summary>The JS <c>Array.from(str).length</c>: the number of code points, not UTF-16 units.</summary>
    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}
