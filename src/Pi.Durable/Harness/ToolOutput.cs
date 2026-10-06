using System.Text;
using Pi.Durable.Env;

namespace Pi.Durable.Harness;

/// <summary>一个工具输出的保留上限。对应 TS <c>harness/output.ts</c> 的 <c>OutputLimits</c>。</summary>
public sealed record OutputLimits
{
    public required long MaxBytes { get; init; }

    public required long MaxLines { get; init; }

    /// <summary>head 保留开头，tail 保留末尾。</summary>
    public required OutputRetain Retain { get; init; }
}

public enum OutputRetain
{
    Head,
    Tail,
}

/// <summary>保留的输出与上限丢弃的部分。对应 TS <c>BoundedOutput</c>。</summary>
public sealed record BoundedOutput(string Text, long DroppedBytes, long DroppedLines);

/// <summary>上限之内输入的精确切片，及其省略的部分。对应 TS <c>OutputSlice</c>。</summary>
public sealed record OutputSlice(string Text, long Bytes, long DroppedBytes, long DroppedLines);

/// <summary>工具输出裁剪。对应 TS <c>harness/output.ts</c> 的自由函数。</summary>
public static class ToolOutput
{
    private const byte Newline = 0x0a;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>移除破坏显示与转录的控制字符；制表与换行保留。</summary>
    public static string SanitizeOutput(string text)
    {
        var builder = (StringBuilder?)null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var invalid = ch <= 0x08 || (ch >= 0x0b && ch <= 0x1f) || (ch >= '\uFFF9' && ch <= '\uFFFB');
            if (invalid)
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
            }
            else if (builder is not null)
            {
                builder.Append(ch);
            }
        }
        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// 把 text 按整行裁到上限之内：head 取前几行，tail 取后几行。结果是精确切片，含尾随换行。超过 maxBytes 的单行
    /// 在字符边界处按字节上限截断。
    /// </summary>
    public static OutputSlice BoundOutput(string text, OutputLimits limits)
    {
        var bytes = Utf8.GetBytes(text);
        var (from, to) = limits.Retain == OutputRetain.Head ? HeadRange(bytes, limits) : TailRange(bytes, limits);
        var keptLength = to - from;
        return new OutputSlice(
            keptLength == bytes.Length ? text : Encoding.UTF8.GetString(bytes, from, keptLength),
            keptLength,
            bytes.Length - keptLength,
            LineCount(bytes) - LineCount(bytes[from..to]));
    }

    private static (int From, int To) HeadRange(byte[] bytes, OutputLimits limits)
    {
        if (limits.MaxLines == 0 || limits.MaxBytes == 0) return (0, 0);
        var end = bytes.Length;
        long lines = 0;
        for (var index = IndexOf(bytes, Newline, 0); index != -1; index = IndexOf(bytes, Newline, index + 1))
        {
            if (++lines == limits.MaxLines)
            {
                end = index + 1;
                break;
            }
        }
        if (end > limits.MaxBytes)
        {
            var newline = LastIndexOf(bytes, Newline, (int)limits.MaxBytes - 1);
            end = newline == -1 ? CharacterEnd(bytes, (int)limits.MaxBytes) : newline + 1;
        }
        return (0, end);
    }

    private static (int From, int To) TailRange(byte[] bytes, OutputLimits limits)
    {
        if (limits.MaxLines == 0 || limits.MaxBytes == 0) return (bytes.Length, bytes.Length);
        // 尾随换行结束最后一行，而不是开始新的一行。
        var last = bytes.Length > 0 && bytes[^1] == Newline ? bytes.Length - 2 : bytes.Length - 1;
        var start = 0;
        long lines = 1;
        for (var index = last < 0 ? -1 : LastIndexOf(bytes, Newline, last); index != -1;)
        {
            if (lines == limits.MaxLines)
            {
                start = index + 1;
                break;
            }
            lines++;
            index = index == 0 ? -1 : LastIndexOf(bytes, Newline, index - 1);
        }
        if (bytes.Length - start > limits.MaxBytes)
        {
            var from = bytes.Length - (int)limits.MaxBytes;
            var newline = IndexOf(bytes, Newline, from - 1);
            // 字节窗口内开始的第一行；或最后一行单独过长时对它截断。
            start = newline != -1 && newline + 1 < bytes.Length ? newline + 1 : CharacterStart(bytes, from);
        }
        return (start, bytes.Length);
    }

    /// <summary>index 处或之前的最后一个字符边界。对应 TS <c>characterEnd</c>。</summary>
    public static int CharacterEnd(byte[] bytes, int index)
    {
        var end = index;
        while (end > 0 && (ByteAt(bytes, end) & 0xc0) == 0x80) end--;
        return end;
    }

    /// <summary>index 处或之后的第一个字符边界。对应 TS <c>characterStart</c>。</summary>
    public static int CharacterStart(byte[] bytes, int index)
    {
        var start = index;
        while (start < bytes.Length && (ByteAt(bytes, start) & 0xc0) == 0x80) start++;
        return start;
    }

    private static int ByteAt(byte[] bytes, int index) =>
        index >= 0 && index < bytes.Length ? bytes[index] : 0;

    private static int IndexOf(byte[] bytes, byte value, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i < bytes.Length; i++)
            if (bytes[i] == value) return i;
        return -1;
    }

    private static int LastIndexOf(byte[] bytes, byte value, int startIndex)
    {
        for (var i = Math.Min(startIndex, bytes.Length - 1); i >= 0; i--)
            if (bytes[i] == value) return i;
        return -1;
    }

    private static long LineCount(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        long newlines = 0;
        for (var index = IndexOf(bytes, Newline, 0); index != -1; index = IndexOf(bytes, Newline, index + 1)) newlines++;
        return newlines + (bytes[^1] == Newline ? 0 : 1);
    }

    /// <summary>
    /// 一个工具调用的有界运行中输出。接受一个块的耗时与块成正比：head 在窗口满后停止存储，tail 在快照时丢弃窗口
    /// 不再需要的已存文本。整条流的计数保持，使丢弃总量精确。对应 TS <c>OutputBuffer</c>。
    /// </summary>
    public sealed class Buffer
    {
        private sealed record Chunk(string Text, long Bytes, long Newlines);

        private readonly OutputLimits _limits;
        private readonly StreamDecoder _decoder = new();
        private readonly List<Chunk> _chunks = new();
        private long _storedBytes;
        private long _storedNewlines;
        private bool _full;
        private long _totalBytes;
        private long _totalNewlines;
        private bool _endsWithNewline = true;

        public Buffer(OutputLimits limits) => _limits = limits;

        /// <summary>当前持有的字节；上限加一个块内有界。</summary>
        public long StoredBytes
        {
            get { lock (_chunks) return _storedBytes; }
        }

        /// <summary>
        /// 接受一块；返回是否接受了内容。skipped 是紧邻此块之前被省略的输出（必比 tail 窗口多至少一字节或一行）；
        /// 只有 tail 保留接受它。
        /// </summary>
        public bool Push(string chunk, ShellOutputSkip? skipped = null)
        {
            lock (_chunks)
            {
                // 字符串块前先冲刷此前字节块扣住的不完整字符（TS 的 pending 语义）。
                var pending = _decoder.Decode(null);
                if (skipped is not { } skip) return Accept(pending + chunk);
                if (_limits.Retain != OutputRetain.Tail)
                    throw new InvalidOperationException("Skipped output requires tail retention");
                Accept(pending);
                Skip(skip);
                Accept(chunk);
                return true;
            }
        }

        public bool Push(byte[] chunk, ShellOutputSkip? skipped = null)
        {
            lock (_chunks)
            {
                var text = _decoder.Decode(chunk);
                if (skipped is not { } skip) return Accept(text);
                if (_limits.Retain != OutputRetain.Tail)
                    throw new InvalidOperationException("Skipped output requires tail retention");
                Skip(skip);
                Accept(text);
                return true;
            }
        }

        /// <summary>冲刷不完整的尾部字符为替换字符；流结束时调用。对应 TS <c>end()</c>。</summary>
        public void End()
        {
            lock (_chunks) Accept(_decoder.Decode(null));
        }

        /// <summary>计数被省略的输出；其后文本到达时，之前存储的任何内容都不会留在窗口里。</summary>
        private void Skip(ShellOutputSkip skipped)
        {
            if (skipped.Bytes == 0) return;
            _totalBytes += skipped.Bytes;
            _totalNewlines += skipped.Newlines;
            _endsWithNewline = skipped.EndsWithNewline;
            _chunks.Clear();
            _storedBytes = 0;
            _storedNewlines = 0;
        }

        private bool Accept(string text)
        {
            if (text.Length == 0) return false;
            var bytes = Utf8.GetByteCount(text);
            var newlines = CountNewlines(text);
            _totalBytes += bytes;
            _totalNewlines += newlines;
            _endsWithNewline = text.EndsWith('\n');
            if (_full) return true;
            _chunks.Add(new Chunk(text, bytes, newlines));
            _storedBytes += bytes;
            _storedNewlines += newlines;
            if (_limits.Retain == OutputRetain.Head)
            {
                // 满窗口之后的内容永远不再需要。
                _full = _storedBytes > _limits.MaxBytes || _storedNewlines >= _limits.MaxLines;
                return true;
            }
            // 其余部分仍多于一个窗口（maxBytes 字节或 maxLines 行再加一，使窗口的行首可寻）时丢弃开头块。
            while (_chunks.Count > 1)
            {
                var first = _chunks[0];
                var bytesAfter = _storedBytes - first.Bytes;
                var newlinesAfter = _storedNewlines - first.Newlines;
                if (bytesAfter <= _limits.MaxBytes + 1 && newlinesAfter <= _limits.MaxLines + 1) break;
                _chunks.RemoveAt(0);
                _storedBytes = bytesAfter;
                _storedNewlines = newlinesAfter;
            }
            return true;
        }

        /// <summary>保留的、净化后的输出，与整条流被上限丢弃的部分。对应 TS <c>snapshot()</c>。</summary>
        public BoundedOutput Snapshot()
        {
            lock (_chunks)
            {
                var stored = string.Concat(_chunks.Select(chunk => chunk.Text));
                var kept = BoundOutput(stored, _limits);
                var storedLines = Lines(_storedNewlines, stored.Length == 0 || stored.EndsWith('\n'));
                var keptLines = storedLines - kept.DroppedLines;
                // tail 窗口不会回溯到本窗口之前，但找到更晚窗口的行首需要它前面的内容：
                // 保留比窗口多一字节或一行的最短后缀，与 Accept 的做法一致。
                if (_limits.Retain == OutputRetain.Tail || _chunks.Count > 1)
                {
                    var text = _limits.Retain == OutputRetain.Tail ? TailMargin(stored, _limits) : stored;
                    var bytes = _limits.Retain == OutputRetain.Tail ? Utf8.GetByteCount(text) : _storedBytes;
                    _chunks.Clear();
                    if (text.Length > 0)
                        _chunks.Add(new Chunk(text, bytes, CountNewlines(text)));
                    _storedBytes = bytes;
                    _storedNewlines = _chunks.Count > 0 ? _chunks[0].Newlines : 0;
                }
                return new BoundedOutput(
                    SanitizeOutput(kept.Text),
                    _totalBytes - kept.Bytes,
                    Lines(_totalNewlines, _endsWithNewline) - keptLines);
            }
        }
    }

    /// <summary>
    /// text 中多于 maxBytes 字节或多于 maxLines 行的最短后缀（或全文）。任何以这个后缀结尾、后接任意内容的文本，
    /// 其 tail 窗口与 text 后接该内容相同。对应 TS <c>tailMargin</c>。
    /// </summary>
    private static string TailMargin(string text, OutputLimits limits)
    {
        var bytes = Utf8.GetBytes(text);
        var byteStart = bytes.Length > limits.MaxBytes
            ? CharacterEnd(bytes, bytes.Length - (int)limits.MaxBytes - 1)
            : 0;
        var lineStart = 0;
        long newlines = 0;
        for (var index = LastIndexOf(bytes, Newline, bytes.Length - 1); index != -1; index = LastIndexOf(bytes, Newline, index - 1))
        {
            if (++newlines > limits.MaxLines)
            {
                lineStart = index;
                break;
            }
            if (index == 0) break;
        }
        var start = Math.Max(byteStart, lineStart);
        return start == 0 ? text : Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>有 newlines 个换行的文本的行数；最后的未终止行计入。</summary>
    private static long Lines(long newlines, bool terminated) => newlines + (terminated ? 0 : 1);

    private static long CountNewlines(string text)
    {
        long count = 0;
        for (var index = text.IndexOf('\n'); index != -1; index = text.IndexOf('\n', index + 1)) count++;
        return count;
    }

    /// <summary>每次进度提交还按写入量成比例地买一段暂停。对应 TS <c>PROGRESS_BYTES_PER_SECOND</c>。</summary>
    public const double ProgressBytesPerSecond = 100 * 1024;

    /// <summary>
    /// 自适应进度提交：空闲后的第一次变化立即提交；随后每次提交把下一次至少推迟 minIntervalMs，并按 100 KiB/s 的
    /// 写入量推迟。至多一个提交在途；其间的变化合并进下一次。对应 TS <c>Progress</c>。
    /// </summary>
    public sealed class Progress
    {
        private readonly Func<Task<long>> _write;
        private readonly Action<Exception> _onError;
        private readonly double _minIntervalMs;
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource<object?>> _waiters = new();
        private Timer? _timer;
        private Task? _inFlight;
        private long _nextAt;
        private bool _dirty;
        private bool _stopped;

        public Progress(Func<Task<long>> write, Action<Exception> onError, double minIntervalMs)
        {
            _write = write;
            _onError = onError;
            _minIntervalMs = minIntervalMs;
        }

        /// <summary>安排一次提交。</summary>
        public void Mark()
        {
            lock (_gate)
            {
                _dirty = true;
            }
            Schedule();
        }

        /// <summary>安排一次提交；promise 随包含此变化的提交结算。</summary>
        public Task MarkAndWait()
        {
            var waiter = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _waiters.Enqueue(waiter);
            Mark();
            return waiter.Task;
        }

        /// <summary>
        /// 停止提交并等待在途提交；返回最终提交要结算的等待者（调用方以 TrySetResult / TrySetException 结算，
        /// 对齐 TS 的 waiter.resolve / reject）。对应 TS <c>stop()</c>。
        /// </summary>
        public async Task<IReadOnlyList<TaskCompletionSource<object?>>> Stop()
        {
            Task? inFlight;
            TaskCompletionSource<object?>[] waiters;
            lock (_gate)
            {
                _stopped = true;
                _timer?.Dispose();
                _timer = null;
                inFlight = _inFlight;
                waiters = _waiters.ToArray();
                _waiters.Clear();
            }
            if (inFlight is not null) await inFlight.ConfigureAwait(false);
            return waiters;
        }

        private void Schedule()
        {
            lock (_gate)
            {
                if (_stopped || _timer is not null || _inFlight is not null) return;
                var wait = _nextAt - Environment.TickCount64;
                if (wait <= 0)
                {
                    Flush();
                    return;
                }
                _timer = new Timer(_ =>
                {
                    lock (_gate) _timer = null;
                    Flush();
                }, null, wait, Timeout.Infinite);
            }
        }

        private void Flush()
        {
            TaskCompletionSource<object?>[] waiters;
            long started;
            lock (_gate)
            {
                if (_stopped || !_dirty) return;
                _dirty = false;
                waiters = _waiters.ToArray();
                _waiters.Clear();
                started = Environment.TickCount64;
                _inFlight = Task.Run(async () =>
                {
                    try
                    {
                        var bytes = await _write().ConfigureAwait(false);
                        lock (_gate)
                        {
                            _nextAt = started + (long)Math.Max(
                                _minIntervalMs, bytes * 1000 / ProgressBytesPerSecond);
                        }
                        foreach (var waiter in waiters) waiter.TrySetResult(null);
                    }
                    catch (Exception error)
                    {
                        lock (_gate) _nextAt = started + (long)_minIntervalMs;
                        foreach (var waiter in waiters) waiter.TrySetException(error);
                        _onError(error);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _inFlight = null;
                            if (_dirty) Schedule();
                        }
                    }
                });
            }
        }
    }
}
