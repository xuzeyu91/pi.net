using System.Text;

namespace Pi.Durable.Env;

/// <summary>
/// 由按序喂入的文件字节计算 <see cref="LineScan"/>，环境因此能以有界内存扫描任意大小的文件。解码大小与一次性解码
/// 整个文件的 WHATWG 流式解码器一致。对应 TS <c>env/line-scan.ts</c> 的 <c>LineScanner</c>。
/// </summary>
public sealed class LineScanner
{
    private const byte Newline = 0x0a;

    private static readonly Encoding Utf8 = Encoding.UTF8;

    private readonly long _startLine;
    private readonly long _endLine; // long.MaxValue 表示到文件末尾
    private long _position;
    private long _newlines;
    private long _lineStart;
    private long? _start;
    private long? _end;
    private long? _firstLineEnd;
    private long? _lastLineStart;
    private long _selectedBytes;
    private long _firstLineBytes;
    private Decoder? _selection;
    private Decoder? _firstLine;
    /// <summary>头几个字节，扣住直到能判断是否为字节序标记。</summary>
    private List<byte>? _head = new();
    private bool _bom;

    /// <summary>startLine 与 endLine 必须是非负整数且 endLine &gt; startLine；endLine 缺省：到末尾。</summary>
    public LineScanner(long startLine, long? endLine = null)
    {
        var end = endLine ?? long.MaxValue;
        if (startLine < 0 || !(end > startLine) || (end != long.MaxValue && end < 0))
            throw new ArgumentOutOfRangeException(nameof(startLine), "Invalid line range");
        _startLine = startLine;
        _endLine = end;
        if (startLine == 0) Begin(0);
    }

    public void Push(ReadOnlySpan<byte> chunk)
    {
        if (_head is not null)
        {
            var take = Math.Min(3 - _head.Count, chunk.Length);
            _head.AddRange(chunk[..(int)take].ToArray());
            if (_head.Count < 3) return;
            var head = _head.ToArray();
            ReleaseHead(head);
            chunk = chunk[(int)take..];
        }
        Process(chunk.ToArray());
    }

    private void ReleaseHead(byte[] head)
    {
        _head = null;
        _bom = Decoding.StartsWithBom(head);
        Process(head);
    }

    private void Process(byte[] chunk)
    {
        var basePosition = _position;
        var from = 0;
        for (var index = Array.IndexOf(chunk, Newline); index != -1; index = Array.IndexOf(chunk, Newline, index + 1))
        {
            // 该换行结束行 _newlines；只在选定行范围内归属。
            Feed(chunk, basePosition, from, index);
            var line = _newlines;
            var position = basePosition + index;
            if (line == _startLine) EndFirstLine(position);
            if (line == _endLine - 1) EndSelection(position);
            Feed(chunk, basePosition, index, index + 1);
            from = index + 1;
            _newlines++;
            _lineStart = position + 1;
            if (_newlines == _startLine) Begin(_lineStart);
            if (_newlines == _endLine - 1) _lastLineStart = _lineStart;
        }
        Feed(chunk, basePosition, from, chunk.Length);
        _position += chunk.Length;
    }

    public LineScan Finish()
    {
        if (_head is not null)
        {
            var head = _head.ToArray();
            ReleaseHead(head);
        }
        var size = _position;
        if (_start is not { } start)
        {
            return new LineScan(_newlines, size, size, size, size, 0, 0);
        }
        if (_firstLineEnd is not { } firstEnd) EndFirstLine(size);
        if (_end is not { } end) EndSelection(size);
        return new LineScan(
            _newlines,
            start,
            _end ?? size,
            _firstLineEnd ?? size,
            // 越过最后一行的选择以最后一行结束。
            _lastLineStart ?? _lineStart,
            _selectedBytes,
            _firstLineBytes);
    }

    private void Begin(long start)
    {
        _start = start;
        if (_startLine == _endLine - 1) _lastLineStart = start;
        _selection = Decoding.RangeDecoder();
        _firstLine = Decoding.RangeDecoder();
    }

    private void EndFirstLine(long position)
    {
        _firstLineEnd = position;
        if (_firstLine is not null) _firstLineBytes += DecodedBytes(Flush(_firstLine));
        _firstLine = null;
    }

    private void EndSelection(long position)
    {
        _end = position;
        if (_selection is not null) _selectedBytes += DecodedBytes(Flush(_selection));
        _selection = null;
    }

    private static string Flush(Decoder decoder)
    {
        var chars = new char[4];
        decoder.Convert(Array.Empty<byte>(), 0, 0, chars, 0, chars.Length, flush: true, out _, out var used, out _);
        return new string(chars, 0, used);
    }

    /// <summary>解码文本的 UTF-8 字节长度。</summary>
    private static long DecodedBytes(string text) => text.Length == 0 ? 0 : Utf8.GetByteCount(text);

    /// <summary>把 <c>chunk[from, to)</c>（起始于文件偏移 base）喂给仍然打开的区间的解码器。</summary>
    private void Feed(byte[] chunk, long basePosition, int from, int to)
    {
        // 整体解码会丢弃起始的字节序标记。
        if (_bom && basePosition + from < 3) from = (int)Math.Min(to, 3 - basePosition);
        if (to <= from) return;
        var bytes = chunk.AsSpan(from, to - from).ToArray();
        if (_selection is not null) _selectedBytes += DecodedBytes(DecodeStreaming(_selection, bytes));
        if (_firstLine is not null) _firstLineBytes += DecodedBytes(DecodeStreaming(_firstLine, bytes));
    }

    private static string DecodeStreaming(Decoder decoder, byte[] bytes)
    {
        var chars = new char[bytes.Length + 4];
        decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: false, out _, out var used, out _);
        return new string(chars, 0, used);
    }
}
