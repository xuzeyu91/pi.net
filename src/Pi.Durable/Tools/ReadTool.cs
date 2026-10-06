using System.Text;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using EnvFileInfo = Pi.Durable.Env.FileInfo;

namespace Pi.Durable.Tools;

/// <summary>
/// <c>read</c> 工具：读取文本文件（带截断与续读诊断）。对应 TS <c>tools/read.ts</c>。
/// </summary>
/// <remarks>
/// 结果内容只有文件文本；关于截断与续读的说明都是诊断。读取按「一趟扫描定位行区间 + 只解码头部」实现，
/// 因此大文件也只读一份扫描量加一个头部。
/// </remarks>
public static class ReadTool
{
    private const int ReadChunk = 64 * 1024;

    /// <summary><c>read</c> 的参数 schema。对应 TS <c>readSchema</c>。</summary>
    public static ToolSchema Schema { get; } = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["path"] = ToolSchemaBuilder.String("Path to the file to read (relative or absolute)"),
            ["offset"] = ToolSchemaBuilder.Optional(
                ToolSchemaBuilder.Number("Line number to start reading from (1-indexed)")),
            ["limit"] = ToolSchemaBuilder.Optional(
                ToolSchemaBuilder.Number("Maximum number of lines to read")),
        },
        "path");

    /// <summary><c>read</c> 的 details：展示文本被如何截断（文本本身是结果内容）。对应 TS <c>ReadToolDetails</c>。</summary>
    public sealed record Details
    {
        public Truncate.TruncationResult? Truncation { get; init; }
    }

    /// <summary><c>Array.prototype.slice</c> 对索引的转换：NaN 为 0，其他值向零截断。</summary>
    private static long SliceIndex(double value)
        => double.IsNaN(value) ? 0 : (long)Math.Truncate(value);

    /// <summary><c>Number.isSafeInteger</c> 的等价物。</summary>
    private static bool IsSafeInteger(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Truncate(value) == value
           && Math.Abs(value) <= 9007199254740991d;

    /// <summary>
    /// 文件字节 <c>[start, end)</c> 的解码头部，按「整体文件的一部分」解码：要么全部，要么足够
    /// <c>truncateHeadOf</c>（多于 <c>DEFAULT_MAX_BYTES + 1</c> 字节，或 <c>DEFAULT_MAX_LINES</c> 个换行）。
    /// 对应 TS <c>readHead</c>。
    /// </summary>
    private static async Task<string> ReadHeadAsync(
        IBinaryReader reader, long start, long end, bool skipBom, Context context)
    {
        var decoder = Decoding.RangeDecoder();
        var text = new StringBuilder();
        var newlines = 0L;
        for (var position = skipBom && start == 0 ? 3L : start; position < end;)
        {
            var length = (int)Math.Min(ReadChunk, end - position);
            var bytes = (await reader.ReadAsync(position, length, context).ConfigureAwait(false)).GetOrThrow();
            if (bytes.Length == 0) break;
            position += bytes.Length;
            var decoded = DecodeStreaming(decoder, bytes);
            text.Append(decoded);
            for (var index = decoded.IndexOf('\n'); index != -1; index = decoded.IndexOf('\n', index + 1)) newlines++;
            if (newlines >= Truncate.DefaultMaxLines || Truncate.Utf8ByteLength(text.ToString()) > Truncate.DefaultMaxBytes + 1)
                return text.ToString();
        }

        return text + Flush(decoder);
    }

    private static string DecodeStreaming(Decoder decoder, byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var chars = new char[bytes.Length + 4];
        decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false, out _, out var used, out _);
        return new string(chars, 0, used);
    }

    private static string Flush(Decoder decoder)
    {
        var chars = new char[4];
        decoder.Convert([], 0, 0, chars, 0, chars.Length, flush: true, out _, out var used, out _);
        return new string(chars, 0, used);
    }

    /// <summary>
    /// 构建 <c>read</c> 工具注册。对应 TS <c>createReadTool()</c>。
    /// </summary>
    public static IToolRegistration Create() => new Registration();

    private sealed class Registration : IToolRegistration
    {
        public string Name => "read";

        public string Description =>
            $"Read the contents of a text file. Output is truncated to {Truncate.DefaultMaxLines} lines or " +
            $"{Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Use offset/limit for large files. " +
            "When you need the full file, continue with offset until complete.";

        public ToolSchema Parameters => Schema;

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var arguments = ToolArgs.Object(args);
            var path = ToolArgs.RequiredString(arguments, "path");
            var offset = ToolArgs.OptionalNumber(arguments, "offset");
            var limit = ToolArgs.OptionalNumber(arguments, "limit");

            var env = ToolsEnv.RequireEnv(api);
            var absolutePath = await ToolPaths.ResolveReadToolPathAsync(env, path, context).ConfigureAwait(false);
            var reader = (await env.OpenBinaryReaderAsync(absolutePath, null, context).ConfigureAwait(false)).GetOrThrow();
            try
            {
                // 并发的写者可能在扫描与读取之间改动文件；从一趟新的扫描重试一次。
                for (var attempt = 0; ; attempt++)
                {
                    var before = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                    var result = await ReadTextAsync(reader, before, path, offset, limit, context).ConfigureAwait(false);
                    var after = (await reader.InfoAsync(context).ConfigureAwait(false)).GetOrThrow();
                    if (after.Size == before.Size && after.MtimeMs == before.MtimeMs) return result;
                    if (attempt == 1) throw new InvalidOperationException($"{path} changed while it was read");
                }
            }
            finally
            {
                await reader.CloseAsync(context).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 已打开文件的读取结果。等价于「用 <c>TextDecoder</c> 解码整个文件、按 <c>\n</c> 切行、再用
    /// <c>truncateHead</c> 界定选中行」，但只读一份扫描量加一个头部。对应 TS <c>readText</c>。
    /// </summary>
    private static async Task<ToolExecutionResult> ReadTextAsync(
        IBinaryReader reader, EnvFileInfo info, string path, double? offset, double? limit, Context context)
    {
        var mimeType = await ImageMime.DetectSupportedImageMimeTypeOfAsync(
                new ReaderByteSource(reader, context, info.Size))
            .ConfigureAwait(false);
        if (mimeType is not null)
        {
            // 目前还不支持图片内容。
            return new ToolExecutionResult
            {
                Content = [],
                IsError = true,
                Diagnostics =
                [
                    new ToolDiagnostic
                    {
                        Severity = "error",
                        Code = "unsupported_image",
                        Message = $"{path} is an image ({mimeType}); reading images is not supported",
                    },
                ],
            };
        }

        var startLine = offset is { } o ? Math.Max(0, o - 1) : 0;
        var startLineDisplay = startLine + 1;
        // 行按 allLines.slice(startLine, endLine) 选取，索引的小数部分被截断。
        var sliceStart = SliceIndex(startLine);
        // 一趟扫描同时得到行数与选区；越过最后一行的选区以最后一行结束（同 slice），空选区（limit 为 0 或负）
        // 按一行扫描后被忽略。起点越过整个文件时从 0 开始扫描只为计数，随后的 offset 检查照旧失败。
        var scanStart = IsSafeInteger(sliceStart) ? sliceStart : 0;
        var requestedEnd = limit is { } l ? Math.Max(scanStart + 1, SliceIndex(startLine + l)) : (long?)null;
        var scanEnd = requestedEnd is { } re && IsSafeInteger(re) ? re : (long?)null;
        async Task<LineScan> ScanOfAsync(long? endLine)
            => (await reader.ScanLinesAsync(
                new LineScanOptions(scanStart, endLine), context).ConfigureAwait(false)).GetOrThrow();

        var scan = await ScanOfAsync(scanEnd).ConfigureAwait(false);
        var totalFileLines = scan.Newlines + 1;
        if (startLine >= totalFileLines)
            throw new InvalidOperationException(
                $"Offset {offset} is beyond end of file ({totalFileLines} lines total)");

        long? userLimitedLines = null;
        var selectedLineCount = totalFileLines - sliceStart;
        if (limit is { } lim)
        {
            var endLine = Math.Min(startLine + lim, totalFileLines);
            userLimitedLines = (long)(endLine - startLine);
            // slice 会把负的 end 从行尾倒着数，只有行数能告知；为此重新扫描。
            var relativeEnd = SliceIndex(endLine);
            var sliceEnd = relativeEnd < 0 ? Math.Max(totalFileLines + relativeEnd, 0) : relativeEnd;
            selectedLineCount = Math.Max(0, sliceEnd - sliceStart);
            if (selectedLineCount > 0 && relativeEnd < 0) scan = await ScanOfAsync(sliceEnd).ConfigureAwait(false);
        }

        var empty = selectedLineCount == 0;
        // 与 truncateHead 同样计数：尾随换行不增加行；空文本没有行。
        var endsWithNewline = !empty && scan.LastLineStart == scan.End && scan.LastLineStart > scan.Start;
        var totals = new Truncate.PrefixTotals(
            empty || scan.SelectedBytes == 0 ? 0 : selectedLineCount - (endsWithNewline ? 1 : 0),
            empty ? 0 : scan.SelectedBytes);

        var firstBytes = (await reader.ReadAsync(0, 3, context).ConfigureAwait(false)).GetOrThrow();
        var head = empty
            ? ""
            : await ReadHeadAsync(reader, scan.Start, scan.End, Decoding.StartsWithBom(firstBytes), context)
                .ConfigureAwait(false);

        var truncation = Truncate.TruncateHeadOf(head, totals);
        var headText = truncation.Content;
        var diagnostics = new List<ToolDiagnostic>();
        var outputText = headText;
        Details? details = null;

        if (truncation.FirstLineExceedsLimit)
        {
            // 展示该行的开头，在字节上限处按字符边界切。如同 allLines[startLine]，小数起始行不指向任何行。
            var integral = startLine == Math.Truncate(startLine);
            var firstLine = head.Split('\n')[0];
            var lineBytes = integral ? Encoding.UTF8.GetBytes(firstLine) : [];
            var lineSize = integral ? scan.FirstLineBytes : 0;
            var end = ToolOutput.CharacterEnd(lineBytes, (int)Truncate.DefaultMaxBytes);
            outputText = Encoding.UTF8.GetString(lineBytes, 0, end);
            diagnostics.Add(new ToolDiagnostic
            {
                Severity = "warn",
                Code = "truncated",
                Message = $"Line {startLineDisplay} is {Truncate.FormatSize(lineSize)}, exceeds the " +
                          $"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit; showing its first " +
                          $"{Truncate.FormatSize(end)}. Use bash: sed -n '{startLineDisplay}p' {path} | tail -c +{end + 1}",
            });
            details = new Details
            {
                Truncation = truncation with { OutputBytes = end, OutputLines = 1 },
            };
        }
        else if (truncation.Truncated)
        {
            var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
            var nextOffset = endLineDisplay + 1;
            var limitText = truncation.TruncatedBy == Truncate.TruncatedBy.Lines
                ? ""
                : $" ({Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit)";
            diagnostics.Add(new ToolDiagnostic
            {
                Severity = "info",
                Code = "truncated",
                Message = $"Showing lines {startLineDisplay}-{endLineDisplay} of {totalFileLines}{limitText}. " +
                          $"Use offset={nextOffset} to continue.",
            });
            details = new Details { Truncation = truncation };
        }
        else if (userLimitedLines is { } ull && startLine + ull < totalFileLines)
        {
            var remaining = totalFileLines - (startLine + ull);
            var nextOffset = startLine + ull + 1;
            diagnostics.Add(new ToolDiagnostic
            {
                Severity = "info",
                Message = $"{remaining} more lines in file. Use offset={nextOffset} to continue.",
            });
        }

        return new ToolExecutionResult
        {
            Content = outputText == "" ? [] : ToolContent.Text(outputText),
            Details = details,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>把 <see cref="IBinaryReader"/> 适配为图片探测的字节来源（大小取自打开时的文件元数据）。</summary>
    private sealed class ReaderByteSource(IBinaryReader reader, Context context, long size) : ImageMime.IByteSource
    {
        public long Size => size;

        public async Task<byte[]> ReadAsync(long offset, int length)
            => (await reader.ReadAsync(offset, length, context).ConfigureAwait(false)).GetOrThrow();
    }
}
