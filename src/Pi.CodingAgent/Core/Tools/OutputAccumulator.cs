using System.Text;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Options for <see cref="OutputAccumulator"/>. Port of <c>OutputAccumulatorOptions</c>.</summary>
public sealed record OutputAccumulatorOptions
{
    public int? MaxLines { get; init; }

    public int? MaxBytes { get; init; }

    public string? TempFilePrefix { get; init; }
}

/// <summary>Port of the TS <c>OutputSnapshot</c>.</summary>
public sealed record OutputSnapshot
{
    public required string Content { get; init; }

    public required Truncate.TruncationResult Truncation { get; init; }

    public string? FullOutputPath { get; init; }
}

/// <summary>Port of the TS <c>FullOutput</c>.</summary>
public sealed record FullOutput
{
    public required string Content { get; init; }

    /// <summary>Whether <see cref="Content"/> omits part of the output.</summary>
    public required bool Truncated { get; init; }
}

/// <summary>
/// Incrementally tracks streaming output with bounded memory. Port of
/// <c>core/tools/output-accumulator.ts</c>.
/// </summary>
/// <remarks>
/// Appends decode chunks with a streaming UTF-8 decoder (the .NET <see cref="Decoder"/> with its
/// default U+FFFD replacement fallback matches the non-fatal WHATWG <c>TextDecoder</c>), keeps only
/// a decoded tail for display snapshots, and opens a temp file when the full output needs to be
/// preserved.
/// </remarks>
public sealed class OutputAccumulator
{
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly int _maxRollingBytes;
    private readonly string _tempFilePrefix;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    private readonly List<byte[]> _rawChunks = [];
    private string _tailText = "";
    private int _tailBytes;
    private bool _tailStartsAtLineBoundary = true;
    private int _totalRawBytes;
    private int _totalDecodedBytes;
    private int _completedLines;
    private int _totalLines;
    private int _currentLineBytes;
    private bool _hasOpenLine;
    private bool _finished;

    private string? _tempFilePath;
    private FileStream? _tempFileStream;

    public OutputAccumulator(OutputAccumulatorOptions? options = null)
    {
        _maxLines = options?.MaxLines ?? Truncate.DefaultMaxLines;
        _maxBytes = options?.MaxBytes ?? Truncate.DefaultMaxBytes;
        _maxRollingBytes = Math.Max(_maxBytes * 2, 1);
        _tempFilePrefix = options?.TempFilePrefix ?? "pi-output";
    }

    /// <summary>The TS <c>append(data: Buffer)</c>.</summary>
    public void Append(byte[] data)
    {
        if (_finished)
        {
            throw new InvalidOperationException("Cannot append to a finished output accumulator");
        }

        _totalRawBytes += data.Length;
        AppendDecodedText(DecodeStreaming(data));

        if (_tempFileStream is not null || ShouldUseTempFile())
        {
            EnsureTempFile();
            _tempFileStream?.Write(data, 0, data.Length);
        }
        else if (data.Length > 0)
        {
            _rawChunks.Add(data);
        }
    }

    /// <summary>The TS <c>finish()</c>.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        AppendDecodedText(FlushDecoder());
        if (ShouldUseTempFile())
        {
            EnsureTempFile();
        }
    }

    /// <summary>The TS <c>snapshot(options)</c>.</summary>
    public OutputSnapshot Snapshot(bool persistIfTruncated = false)
    {
        var tailTruncation = Truncate.TruncateTail(
            GetSnapshotText(),
            new Truncate.TruncationOptions { MaxLines = _maxLines, MaxBytes = _maxBytes });
        var truncated = _totalLines > _maxLines || _totalDecodedBytes > _maxBytes;
        var truncatedBy = truncated
            ? tailTruncation.TruncatedBy ?? (_totalDecodedBytes > _maxBytes ? TruncatedBy.Bytes : TruncatedBy.Lines)
            : null;
        var truncation = tailTruncation with
        {
            Truncated = truncated,
            TruncatedBy = truncatedBy,
            TotalLines = _totalLines,
            TotalBytes = _totalDecodedBytes,
            MaxLines = _maxLines,
            MaxBytes = _maxBytes,
        };

        if (persistIfTruncated && truncation.Truncated)
        {
            EnsureTempFile();
        }

        return new OutputSnapshot
        {
            Content = truncation.Content,
            Truncation = truncation,
            FullOutputPath = _tempFilePath,
        };
    }

    /// <summary>The TS <c>closeTempFile()</c>.</summary>
    public async Task CloseTempFileAsync()
    {
        if (_tempFileStream is null)
        {
            return;
        }

        var stream = _tempFileStream;
        _tempFileStream = null;

        await stream.FlushAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The complete output, for callers that can take more than the display snapshot. Call after
    /// <see cref="Finish"/> and <see cref="CloseTempFileAsync"/>. Output longer than
    /// <paramref name="maxBytes"/> raw bytes keeps its first and last <c>maxBytes / 2</c> bytes
    /// around an omission marker.
    /// </summary>
    public async Task<FullOutput> ReadFullOutputAsync(int maxBytes)
    {
        if (_tempFilePath is null)
        {
            var buffer = new byte[_rawChunks.Sum(chunk => chunk.Length)];
            var offset = 0;
            foreach (var chunk in _rawChunks)
            {
                chunk.CopyTo(buffer, offset);
                offset += chunk.Length;
            }

            return new FullOutput { Content = Encoding.UTF8.GetString(buffer), Truncated = false };
        }

        await using var file = new FileStream(_tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = file.Length;
        if (size <= maxBytes)
        {
            var buffer = new byte[size];
            await file.ReadExactlyAsync(buffer).ConfigureAwait(false);
            return new FullOutput { Content = Encoding.UTF8.GetString(buffer), Truncated = false };
        }

        var headBytes = maxBytes / 2;
        var tailBytes = maxBytes - maxBytes / 2;
        var head = new byte[headBytes];
        var tail = new byte[tailBytes];
        await file.ReadExactlyAsync(head, 0, headBytes).ConfigureAwait(false);
        file.Seek(size - tailBytes, SeekOrigin.Begin);
        await file.ReadExactlyAsync(tail, 0, tailBytes).ConfigureAwait(false);

        // Cut at character boundaries: streaming decode holds back an incomplete trailing sequence,
        // and the tail skips leading continuation bytes.
        var headText = DecodeNonFlush(head);
        var tailStart = 0;
        while (tailStart < tail.Length && (tail[tailStart] & 0xc0) == 0x80)
        {
            tailStart++;
        }

        var tailText = Encoding.UTF8.GetString(tail, tailStart, tail.Length - tailStart);
        var omitted = size - headBytes - tailBytes;
        return new FullOutput
        {
            Content = $"{headText}\n\n[... {omitted} bytes omitted ...]\n\n{tailText}",
            Truncated = true,
        };
    }

    /// <summary>The TS <c>getLastLineBytes()</c>.</summary>
    public int GetLastLineBytes() => _currentLineBytes;

    private void AppendDecodedText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(text);
        _totalDecodedBytes += bytes;
        _tailText += text;
        _tailBytes += bytes;
        if (_tailBytes > _maxRollingBytes * 2)
        {
            TrimTail();
        }

        var newlines = 0;
        var lastNewline = -1;
        for (var i = text.IndexOf('\n'); i != -1; i = text.IndexOf('\n', i + 1))
        {
            newlines++;
            lastNewline = i;
        }

        if (newlines == 0)
        {
            _currentLineBytes += bytes;
            _hasOpenLine = true;
        }
        else
        {
            _completedLines += newlines;
            var tail = text[(lastNewline + 1)..];
            _currentLineBytes = Encoding.UTF8.GetByteCount(tail);
            _hasOpenLine = tail.Length > 0;
        }

        _totalLines = _completedLines + (_hasOpenLine ? 1 : 0);
    }

    private void TrimTail()
    {
        var buffer = Encoding.UTF8.GetBytes(_tailText);
        if (buffer.Length <= _maxRollingBytes)
        {
            _tailBytes = buffer.Length;
            return;
        }

        var start = buffer.Length - _maxRollingBytes;
        while (start < buffer.Length && (buffer[start] & 0xc0) == 0x80)
        {
            start++;
        }

        _tailStartsAtLineBoundary = start == 0 ? _tailStartsAtLineBoundary : buffer[start - 1] == 0x0a;
        _tailText = Encoding.UTF8.GetString(buffer, start, buffer.Length - start);
        _tailBytes = Encoding.UTF8.GetByteCount(_tailText);
    }

    private string GetSnapshotText()
    {
        if (_tailStartsAtLineBoundary)
        {
            return _tailText;
        }

        var firstNewline = _tailText.IndexOf('\n');
        return firstNewline == -1 ? _tailText : _tailText[(firstNewline + 1)..];
    }

    private bool ShouldUseTempFile() =>
        _totalRawBytes > _maxBytes || _totalDecodedBytes > _maxBytes || _totalLines > _maxLines;

    private void EnsureTempFile()
    {
        if (_tempFilePath is not null)
        {
            return;
        }

        var (path, stream) = OutputFiles.CreateOutputFileStream(_tempFilePrefix, ".log");
        _tempFilePath = path;
        _tempFileStream = stream;
        foreach (var chunk in _rawChunks)
        {
            stream.Write(chunk, 0, chunk.Length);
        }

        _rawChunks.Clear();
    }

    /// <summary>The TS <c>decoder.decode(data, { stream: true })</c>: an incomplete trailing sequence is held back.</summary>
    private string DecodeStreaming(byte[] data)
    {
        var charBuffer = new char[Math.Max(8, data.Length)];
        var charCount = _decoder.GetChars(data, 0, data.Length, charBuffer, 0);
        return new string(charBuffer, 0, charCount);
    }

    /// <summary>The TS <c>decoder.decode()</c> flush: any held-back incomplete sequence is replaced.</summary>
    private string FlushDecoder()
    {
        var charBuffer = new char[8];
        var charCount = _decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
        return new string(charBuffer, 0, charCount);
    }

    /// <summary>The TS <c>new TextDecoder().decode(bytes, { stream: true })</c> on a fresh decoder.</summary>
    private static string DecodeNonFlush(byte[] data)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = new char[Math.Max(8, data.Length)];
        var charCount = decoder.GetChars(data, 0, data.Length, charBuffer, 0);
        return new string(charBuffer, 0, charCount);
    }
}
