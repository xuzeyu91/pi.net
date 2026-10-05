using System.Text;

namespace Pi.Durable;

/// <summary>工具输出的共享截断工具。对应 TS <c>truncate.ts</c>。</summary>
/// <remarks>
/// 截断基于两个独立限制——先命中者生效：
/// 行数限制（默认 2000 行）与字节限制（默认 50KB）。
/// 绝不返回部分行；工具输出流的有界化由 harness/output 负责。
/// </remarks>
public static class Truncate
{
    /// <summary>默认最大行数。</summary>
    public const long DefaultMaxLines = 2000;

    /// <summary>默认最大字节数（50KB）。</summary>
    public const long DefaultMaxBytes = 50 * 1024;

    /// <summary>截断结果。对应 TS <c>TruncationResult</c>。</summary>
    public sealed record TruncationResult
    {
        /// <summary>截断后的内容。</summary>
        public required string Content { get; init; }

        /// <summary>是否发生截断。</summary>
        public required bool Truncated { get; init; }

        /// <summary>命中的限制：行 / 字节 / 未截断为 null。</summary>
        public TruncatedBy? TruncatedBy { get; init; }

        /// <summary>原内容总行数。</summary>
        public required long TotalLines { get; init; }

        /// <summary>原内容总字节数。</summary>
        public required long TotalBytes { get; init; }

        /// <summary>截断输出中的完整行数。</summary>
        public required long OutputLines { get; init; }

        /// <summary>截断输出的字节数。</summary>
        public required long OutputBytes { get; init; }

        /// <summary>最后一行是否被部分截断（仅尾截断边缘情况）。</summary>
        public required bool LastLinePartial { get; init; }

        /// <summary>首行是否超出字节限制（头截断）。</summary>
        public required bool FirstLineExceedsLimit { get; init; }

        /// <summary>应用的行数限制。</summary>
        public required long MaxLines { get; init; }

        /// <summary>应用的字节限制。</summary>
        public required long MaxBytes { get; init; }
    }

    /// <summary>命中的限制种类。</summary>
    public enum TruncatedBy
    {
        /// <summary>行数限制。</summary>
        Lines,

        /// <summary>字节限制。</summary>
        Bytes,
    }

    /// <summary>截断选项。对应 TS <c>TruncationOptions</c>。</summary>
    public sealed record TruncationOptions
    {
        /// <summary>最大行数（默认 2000）。</summary>
        public long? MaxLines { get; init; }

        /// <summary>最大字节数（默认 50KB）。</summary>
        public long? MaxBytes { get; init; }
    }

    /// <summary>前缀文本的已知总量（行数按 <see cref="TruncateHead"/> 计数，忽略尾随换行）。</summary>
    public sealed record PrefixTotals(long Lines, long Bytes);

    /// <summary>UTF-8 字节长度。对应 TS <c>utf8ByteLength</c>（Node Buffer / 手写回退的等价物）。</summary>
    public static long Utf8ByteLength(string content) => Encoding.UTF8.GetByteCount(content);

    /// <summary>人类可读的字节大小。对应 TS <c>formatSize</c>。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}MB";
    }

    /// <summary>从头截断（保留前 N 行 / 字节）。适合文件读取。对应 TS <c>truncateHead</c>。</summary>
    public static TruncationResult TruncateHead(string content, TruncationOptions? options = null)
        => TruncateHeadOf(content, new PrefixTotals(SplitLinesForCounting(content).Count, Utf8ByteLength(content)), options);

    /// <summary>
    /// 已知前缀与其总量的 <see cref="TruncateHead"/>。对应 TS <c>truncateHeadOf</c>：
    /// 前缀须为整个文本，或长于 <c>maxBytes + 1</c> 字节，或含至少 <c>maxLines</c> 个换行；
    /// 此时结果等于整个文本的 <see cref="TruncateHead"/>。
    /// </summary>
    public static TruncationResult TruncateHeadOf(string prefix, PrefixTotals totals, TruncationOptions? options = null)
    {
        var maxLines = options?.MaxLines ?? DefaultMaxLines;
        var maxBytes = options?.MaxBytes ?? DefaultMaxBytes;

        var totalBytes = totals.Bytes;
        var lines = SplitLinesForCounting(prefix);
        var totalLines = totals.Lines;

        // 无需截断。
        if (totalLines <= maxLines && totalBytes <= maxBytes)
        {
            return new TruncationResult
            {
                Content = prefix,
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

        // 首行即超出字节限制。
        var firstLineBytes = lines.Count > 0 ? Utf8ByteLength(lines[0]) : 0;
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

        // 收集放得下的完整行。
        var outputLines = new List<string>();
        long outputBytesCount = 0;
        var truncatedBy = TruncatedBy.Lines;

        for (var i = 0; i < lines.Count && i < maxLines; i++)
        {
            var line = lines[i];
            var lineBytes = Utf8ByteLength(line) + (i > 0 ? 1 : 0); // +1 为换行符

            if (outputBytesCount + lineBytes > maxBytes)
            {
                truncatedBy = TruncatedBy.Bytes;
                break;
            }

            outputLines.Add(line);
            outputBytesCount += lineBytes;
        }

        // 无字节中断时，只有被省略的行能证明行限制被命中；否则尾随换行超出了字节。
        if (truncatedBy != TruncatedBy.Bytes)
            truncatedBy = outputLines.Count < totalLines ? TruncatedBy.Lines : TruncatedBy.Bytes;

        var outputContent = string.Join("\n", outputLines);
        var finalOutputBytes = Utf8ByteLength(outputContent);

        return new TruncationResult
        {
            Content = outputContent,
            Truncated = true,
            TruncatedBy = truncatedBy,
            TotalLines = totalLines,
            TotalBytes = totalBytes,
            OutputLines = outputLines.Count,
            OutputBytes = finalOutputBytes,
            LastLinePartial = false,
            FirstLineExceedsLimit = false,
            MaxLines = maxLines,
            MaxBytes = maxBytes,
        };
    }

    /// <summary>按 \n 切行用于计数；空串无行；忽略一个尾随换行。对应 TS <c>splitLinesForCounting</c>。</summary>
    public static List<string> SplitLinesForCounting(string content)
    {
        if (content.Length == 0) return [];
        var lines = new List<string>(content.Split('\n'));
        if (content.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
