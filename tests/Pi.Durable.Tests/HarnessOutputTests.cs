using System.Text;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Xunit;

namespace Pi.Durable.Tests;

using OutputBuffer = Pi.Durable.Harness.ToolOutput.Buffer;
using Progress = Pi.Durable.Harness.ToolOutput.Progress;

/// <summary>
/// 移植 <c>packages/durable/test/harness-output.test.ts</c> 与 <c>harness-output-skip.test.ts</c>：
/// sanitizeOutput / boundOutput / OutputBuffer / Progress 的行为，含确定性 fuzz 与 skip 契约。
/// </summary>
public sealed class HarnessOutputTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static OutputLimits Head(long maxLines, long maxBytes = 1000) =>
        new() { MaxBytes = maxBytes, MaxLines = maxLines, Retain = OutputRetain.Head };

    private static OutputLimits Tail(long maxLines, long maxBytes = 1000) =>
        new() { MaxBytes = maxBytes, MaxLines = maxLines, Retain = OutputRetain.Tail };

    /// <summary>Buffer 的 tail 切片：按字节从尾部取，落在 UTF-8 续字节上时前移到字符边界。</summary>
    private static string BufferTail(string content, int maxBytes)
    {
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length <= maxBytes) return content;
        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>单行超过字节上限时，tail 的截断必须与 Buffer 的尾切片在字符边界上完全一致。</summary>
    private static void AssertMatchesBufferTail(string input, IReadOnlyList<int>? maxByteValues = null)
    {
        var totalBytes = Utf8.GetByteCount(input);
        var values = maxByteValues ?? Enumerable.Range(0, totalBytes + 5).ToArray();
        foreach (var maxBytes in values)
        {
            var kept = ToolOutput.BoundOutput(input, Tail(10, maxBytes)).Text;
            var expected = BufferTail(input, maxBytes);
            Assert.True(
                kept == expected,
                $"tail mismatch input={Json(input)} maxBytes={maxBytes} expected={Json(expected)} actual={Json(kept)}");
            Assert.True(Utf8.GetByteCount(kept) <= maxBytes, $"tail output exceeded {maxBytes} bytes");
        }
    }

    /// <summary>采样一批有代表性的字节上限（0/1/2/…、半程、末尾±、越界）。</summary>
    private static List<int> SampledByteLimits(string input)
    {
        var totalBytes = Utf8.GetByteCount(input);
        var candidates = new[]
        {
            0, 1, 2, 3, 4, 5, 8,
            totalBytes / 2,
            totalBytes - 4,
            totalBytes - 1,
            totalBytes,
            totalBytes + 1,
        };
        return candidates.Where(value => value >= 0).Distinct().OrderBy(value => value).ToList();
    }

    private static (string Kept, long DroppedBytes, long DroppedLines) Bound(string text, OutputLimits limits)
    {
        var slice = ToolOutput.BoundOutput(text, limits);
        return (slice.Text, slice.DroppedBytes, slice.DroppedLines);
    }

    private static string Json(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static string Describe(BoundedOutput output) =>
        $"{{ text={Json(output.Text)}, droppedBytes={output.DroppedBytes}, droppedLines={output.DroppedLines} }}";

    // ---------------------------------------------------------------- tool output bounds

    [Fact]
    public void RemovesControlCharactersButKeepsTabsNewlinesAndOtherText()
    {
        Assert.Equal("ab\tc\ndefgh\U0001F600", ToolOutput.SanitizeOutput("a\0b\tc\nd\re\u0007f\uFFF9g\uFFFBh\U0001F600"));
    }

    [Fact]
    public void KeepsOutputWithinTheLimitsUnchanged()
    {
        Assert.Equal(("a\nb\n", 0L, 0L), Bound("a\nb\n", Head(2)));
        Assert.Equal(("a\nb", 0L, 0L), Bound("a\nb", Tail(2)));
        Assert.Equal(("", 0L, 0L), Bound("", Tail(2)));
    }

    [Fact]
    public void KeepsNothingWithAZeroLimit()
    {
        Assert.Equal(("", 6L, 2L), Bound("ab\ncd\n", Head(10, 0)));
        Assert.Equal(("", 6L, 2L), Bound("ab\ncd\n", Tail(0)));
    }

    [Fact]
    public void KeepsExactSlicesOfWholeLinesTrailingNewlineIncluded()
    {
        Assert.Equal(("a\nb\n", 2L, 1L), Bound("a\nb\nc\n", Head(2)));
        Assert.Equal(("b\nc\n", 2L, 1L), Bound("a\nb\nc\n", Tail(2)));
        Assert.Equal(("b\nc", 2L, 1L), Bound("a\nb\nc", Tail(2)));
        // 空行也是行。
        Assert.Equal(("b\nc\n\n", 2L, 1L), Bound("a\nb\nc\n\n", Tail(3)));
    }

    [Fact]
    public void CutsAtTheByteLimitOnWholeLinesWhenPossible()
    {
        Assert.Equal(("aa\nbb\n", 3L, 1L), Bound("aa\nbb\ncc\n", Head(10, 7)));
        Assert.Equal(("bb\ncc\n", 3L, 1L), Bound("aa\nbb\ncc\n", Tail(10, 7)));
    }

    [Fact]
    public void CutsASingleLineLongerThanTheByteLimitOnACharacterBoundary()
    {
        // "é" 占两字节；五字节容得下两个完整字符。
        Assert.Equal(("éé", 3L, 0L), Bound("ééé\n", Head(10, 5)));
        Assert.Equal(("éé", 6L, 1L), Bound("x\néééé", Tail(10, 5)));
    }

    [Fact]
    public void CutsTailsOfSurrogateEdgeCasesExactlyLikeBuffer()
    {
        string[] inputs = ["a\ud83d", "\ude42b", "a\ude42b", "\ud83d\ud83d\ude42", "\ud83d\ude42\ude42", "👩‍💻"];
        foreach (var input in inputs) AssertMatchesBufferTail(input);
    }

    [Fact]
    public void CutsTailsExactlyLikeBufferAcrossDeterministicFuzzCases()
    {
        string[] alphabet =
        [
            "a", "\u007f", "\u0080", "é", "\u07ff", "\u0800", "中", "\ud7ff",
            "\ud800", "\ud83d", "\udc00", "\ude42", "🙂", "\ue000", "\uffff",
        ];

        void CheckExhaustive(string prefix, int depth)
        {
            AssertMatchesBufferTail(prefix, SampledByteLimits(prefix));
            if (depth == 0) return;
            foreach (var character in alphabet) CheckExhaustive(prefix + character, depth - 1);
        }

        CheckExhaustive("", 3);

        var seed = 0x12345678u;
        double Random()
        {
            seed = (seed * 1664525u + 1013904223u);
            return seed / 4294967296.0;
        }

        for (var i = 0; i < 1_000; i++)
        {
            var input = "";
            var length = (int)Math.Floor(Random() * 80);
            for (var j = 0; j < length; j++) input += alphabet[(int)Math.Floor(Random() * alphabet.Length)];
            AssertMatchesBufferTail(input, SampledByteLimits(input));
        }
    }

    // ---------------------------------------------------------------- OutputBuffer

    [Fact]
    public void KeepsExactTotalsAcrossChunksAndDecodesUtf8SplitAcrossByteChunks()
    {
        var buffer = new OutputBuffer(Tail(2));
        var bytes = Utf8.GetBytes("😀\n");
        buffer.Push("a\nb\n");
        buffer.Push(bytes[..2]);
        buffer.Push(bytes[2..]);
        Assert.Equal(new BoundedOutput("b\n😀\n", 2, 1), buffer.Snapshot());
    }

    [Fact]
    public void SanitizesTheRetainedTextButCountsTheRawStream()
    {
        var buffer = new OutputBuffer(Tail(1));
        buffer.Push("a\u0007\n");
        Assert.Equal(new BoundedOutput("a\n", 0, 0), buffer.Snapshot());
        buffer.Push("b\u001b\n");
        Assert.Equal(new BoundedOutput("b\n", 3, 1), buffer.Snapshot());
    }

    [Fact]
    public void FlushesAnIncompleteCharacterBeforeAStringChunkAndAtTheEnd()
    {
        var buffer = new OutputBuffer(Tail(10));
        var euro = Utf8.GetBytes("€");
        buffer.Push(euro[..1]);
        buffer.Push("x");
        buffer.Push(euro[..2]);
        buffer.End();
        Assert.Equal("\uFFFDx\uFFFD", buffer.Snapshot().Text);
    }

    [Fact]
    public void MatchesBoundingTheWholeStreamWhenSeveralChunksArriveBetweenSnapshots()
    {
        OutputLimits[] cases = [Head(3, 40), Tail(3, 40), Head(50, 25), Tail(50, 25)];
        foreach (var limits in cases)
        {
            var buffer = new OutputBuffer(limits);
            var stream = "";
            for (var index = 0; index < 300; index++)
            {
                var chunk = index % 7 == 0 ? new string('é', index % 30) + "\n" : $"line {index}\n";
                stream += chunk;
                buffer.Push(chunk);
                if (index % 5 != 4) continue;
                var expected = ToolOutput.BoundOutput(stream, limits);
                Assert.Equal(
                    new BoundedOutput(expected.Text, expected.DroppedBytes, expected.DroppedLines),
                    buffer.Snapshot());
            }
        }
    }

    [Fact]
    public void StopsStoringHeadOutputOnceTheWindowIsFull()
    {
        var buffer = new OutputBuffer(Head(2));
        for (var index = 0; index < 1000; index++) buffer.Push($"line {index}\n");
        Assert.True(buffer.StoredBytes < 20, $"storedBytes={buffer.StoredBytes}");
        Assert.Equal(new BoundedOutput("line 0\nline 1\n", 8876, 998), buffer.Snapshot());
    }

    [Fact]
    public void StoresOnlyTheTailWindowAfterEachSnapshot()
    {
        var buffer = new OutputBuffer(Tail(3, 100));
        var stream = "";
        for (var index = 0; index < 2000; index++)
        {
            var chunk = $"line {index}\n\n";
            stream += chunk;
            buffer.Push(chunk);
            var snapshot = buffer.Snapshot();
            Assert.True(buffer.StoredBytes <= 100, $"storedBytes={buffer.StoredBytes}");
            Assert.Equal(ToolOutput.BoundOutput(stream, Tail(3, 100)).Text, snapshot.Text);
        }
    }

    // ---------------------------------------------------------------- Progress

    [Fact]
    public async Task CommitsTheFirstChangeAtOnceThenWaitsAtLeast100MsAndTheWrittenSizeAt100KiBs()
    {
        // TS 用 vitest 假时钟把时间轴钉死；C# 没有等效的全局假计时器，改用实时轮询，
        // 断言「相对」时序（首次立刻提交、其后按 100 KiB/s 的写入量比例延迟、小提交至少等 minInterval）。
        const double minInterval = 30;
        var commits = new List<long>();
        var started = Environment.TickCount64;
        long size = 50 * 1024;
        var progress = new Progress(
            () =>
            {
                commits.Add(Environment.TickCount64 - started);
                return Task.FromResult(size);
            },
            _ => { },
            minInterval);

        progress.Mark();
        await HarnessTestSupport.WaitForAsync(() => commits.Count == 1);
        Assert.Single(commits);
        Assert.True(commits[0] < 150, $"first commit should be immediate, was {commits[0]}ms");

        // 50 KiB 按 100 KiB/s 买 500 ms 的暂停；此间的变化合并成一次提交。
        size = 10;
        progress.Mark();
        progress.Mark();
        await HarnessTestSupport.WaitForAsync(() => commits.Count == 2);
        var pause = commits[1] - commits[0];
        // 下界是硬性的（100 KiB/s），上界放宽以容忍调度抖动。
        Assert.True(pause >= 400, $"expected a byte-proportional pause ≥400ms, got {pause}ms");
        Assert.True(pause < 3000, $"50 KiB should buy ~500ms, got {pause}ms");

        // 一次小提交仍至少等 minInterval。
        var before = commits[1];
        progress.Mark();
        await HarnessTestSupport.WaitForAsync(() => commits.Count == 3);
        Assert.True(commits[2] - before >= minInterval, $"expected ≥{minInterval}ms, got {commits[2] - before}ms");

        await progress.Stop();
    }

    [Fact]
    public async Task WaitsTheConfiguredMinimumIntervalBetweenSmallCommits()
    {
        const double minInterval = 60;
        var commits = new List<long>();
        var started = Environment.TickCount64;
        var progress = new Progress(
            () =>
            {
                commits.Add(Environment.TickCount64 - started);
                return Task.FromResult(10L);
            },
            _ => { },
            minInterval);

        progress.Mark();
        await HarnessTestSupport.WaitForAsync(() => commits.Count == 1);
        var first = commits[0];
        progress.Mark();
        // 用实时间隔，无法像假时钟那样在「刚过 minInterval」处精确卡点；改用轮询捕获第二次提交的时刻并断言其下界。
        await HarnessTestSupport.WaitForAsync(() => commits.Count == 2);
        Assert.True(commits[1] - first >= minInterval, $"expected ≥{minInterval}ms, got {commits[1] - first}ms");

        await progress.Stop();
    }

    [Fact]
    public async Task RejectsTheWaitersOfAFailedCommitAndReportsItsError()
    {
        var errors = new List<Exception>();
        var failure = new InvalidOperationException("commit failed");
        var progress = new Progress(
            () => Task.FromException<long>(failure),
            error => errors.Add(error),
            100);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => progress.MarkAndWait());
        Assert.Same(failure, thrown);
        Assert.Equal([failure], errors);
    }

    [Fact]
    public async Task StopsWaitsForTheCommitInFlightAndHandsBackWaitersNoCommitCoveredYet()
    {
        var inFlight = new HarnessTestSupport.Deferred<Unit>();
        var progress = new Progress(
            async () =>
            {
                await inFlight.Task;
                return 0;
            },
            _ => { },
            100);

        var first = progress.MarkAndWait();
        var second = progress.MarkAndWait();
        var stopped = progress.Stop();
        inFlight.Resolve(default);
        var pending = await stopped;
        await first;
        Assert.Single(pending);
        pending[0].TrySetResult(null);
        await second;
    }

    // ---------------------------------------------------------------- skipped output

    /// <summary>确定性 PRNG（mulberry32），使失败可从打印的种子复现。</summary>
    private static Func<double> Random(uint seed)
    {
        var state = seed;
        return () =>
        {
            state = state + 0x6d2b79f5u;
            var t = state;
            t = (uint)((t ^ (t >> 15)) * (t | 1u));
            t ^= t + (uint)((t ^ (t >> 7)) * (t | 61u));
            return ((t ^ (t >> 14)) >> 0) / 4294967296.0;
        };
    }

    private static readonly string[] SkipAlphabet =
        ["a", "b", "z", " ", "\n", "\n", "\n", "\r\n", "\t", "é", "€", "😀", "\uFFFD", "\x01", "\x1b"];

    private static string RandomChunk(Func<double> next)
    {
        var length = (int)Math.Floor(next() * 12);
        var text = "";
        for (var index = 0; index < length; index++) text += SkipAlphabet[(int)Math.Floor(next() * SkipAlphabet.Length)];
        return text;
    }

    private static ShellOutputSkip Measure(string text)
    {
        long newlines = 0;
        foreach (var character in text) if (character == '\n') newlines++;
        return new ShellOutputSkip(Utf8.GetByteCount(text), newlines, text.EndsWith('\n'));
    }

    /// <summary>text 是否证明它之前的内容已落在 tail 窗口之外。</summary>
    private static bool ExceedsWindow(string text, OutputLimits limits)
    {
        var skip = Measure(text);
        return skip.Bytes > limits.MaxBytes || skip.Newlines > limits.MaxLines;
    }

    /// <summary>text 的码点边界，使切分永不劈开代理对。对应 TS <c>for (const character of text)</c>。</summary>
    private static List<int> Boundaries(string text)
    {
        var result = new List<int> { 0 };
        var offset = 0;
        for (var index = 0; index < text.Length; index++)
        {
            offset += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            if (offset > index + 1) index++;
            result.Add(offset);
        }
        return result;
    }

    /// <summary>
    /// 把随机输出完整喂给一个 buffer，同时按 ShellOutputInfo.skipped 契约（持有一段未投递文本，投递时若其余部分超过窗口，
    /// 可用计数替换其前缀）喂给另一个。两者见过相同输出时快照必须一致。
    /// </summary>
    private static void CheckSeed(uint seed)
    {
        var next = Random(seed);
        var limits = new OutputLimits
        {
            MaxBytes = 1 + (long)Math.Floor(next() * 40),
            MaxLines = 1 + (long)Math.Floor(next() * 5),
            Retain = OutputRetain.Tail,
        };
        var full = new OutputBuffer(limits);
        var skipping = new OutputBuffer(limits);
        var pending = "";
        var skips = 0;

        void Flush()
        {
            if (pending == "") return;
            var cuts = Boundaries(pending).Where(cut => cut > 0 && ExceedsWindow(pending[cut..], limits)).ToList();
            if (cuts.Count > 0 && next() < 0.7)
            {
                var cut = cuts[(int)Math.Floor(next() * cuts.Count)];
                skipping.Push(pending[cut..], Measure(pending[..cut]));
                skips++;
            }
            else
            {
                skipping.Push(pending);
            }

            pending = "";
            // 进度快照发生在投递之间，会压实已存的块。
            if (next() < 0.5) skipping.Snapshot();
            var skippedSnapshot = skipping.Snapshot();
            var fullSnapshot = full.Snapshot();
            Assert.True(
                Equals(skippedSnapshot, fullSnapshot),
                $"seed {seed}: skipped {Describe(skippedSnapshot)} != full {Describe(fullSnapshot)}");
        }

        var chunks = (int)Math.Floor(next() * 40);
        for (var index = 0; index < chunks; index++)
        {
            var chunk = RandomChunk(next);
            full.Push(chunk);
            if (next() < 0.3) full.Snapshot();
            pending += chunk;
            if (next() < 0.3) Flush();
        }

        Flush();
        full.End();
        skipping.End();
        var endSkipped = skipping.Snapshot();
        var endFull = full.Snapshot();
        Assert.True(
            Equals(endSkipped, endFull),
            $"seed {seed}: skipped {Describe(endSkipped)} != full {Describe(endFull)}");
        Assert.True(skips >= 0);
    }

    [Fact]
    public void KeepsTheSameTailWheneverProgressSnapshotsHappen()
    {
        for (uint seed = 1; seed <= 3000; seed++)
        {
            var next = Random(seed);
            var limits = new OutputLimits
            {
                MaxBytes = 1 + (long)Math.Floor(next() * 40),
                MaxLines = 1 + (long)Math.Floor(next() * 5),
                Retain = OutputRetain.Tail,
            };
            var plain = new OutputBuffer(limits);
            var sampled = new OutputBuffer(limits);
            var chunks = (int)Math.Floor(next() * 30);
            for (var index = 0; index < chunks; index++)
            {
                var chunk = RandomChunk(next);
                plain.Push(chunk);
                sampled.Push(chunk);
                if (next() < 0.4) sampled.Snapshot();
            }

            Assert.True(
                Equals(sampled.Snapshot(), plain.Snapshot()),
                $"seed {seed}: sampled {Json(sampled.Snapshot().Text)} != plain {Json(plain.Snapshot().Text)}");
        }
    }

    [Fact]
    public void MatchesTheFullStreamForEveryLegalSkipPattern()
    {
        for (uint seed = 1; seed <= 3000; seed++) CheckSeed(seed);
    }

    [Fact]
    public void CountsSkippedBytesLinesAndTheFinalNewlineExactly()
    {
        var limits = new OutputLimits { MaxBytes = 1000, MaxLines = 2, Retain = OutputRetain.Tail };
        var full = new OutputBuffer(limits);
        var skipping = new OutputBuffer(limits);
        // 被省略的文本不以换行结尾，因此它的最后一行在投递的文本中继续。
        const string omitted = "one\ntwo\nthr";
        const string kept = "ee\nfour\nfive\nsix";
        full.Push(omitted + kept);
        skipping.Push(kept, Measure(omitted));
        Assert.Equal(full.Snapshot(), skipping.Snapshot());
        Assert.Equal(new BoundedOutput("five\nsix", 19, 4), skipping.Snapshot());
    }

    [Fact]
    public void RefusesSkipsForHeadRetention()
    {
        var buffer = new OutputBuffer(new OutputLimits { MaxBytes = 10, MaxLines = 2, Retain = OutputRetain.Head });
        var error = Assert.Throws<InvalidOperationException>(
            () => buffer.Push("x\ny\nz\n", new ShellOutputSkip(3, 1, true)));
        Assert.Contains("tail retention", error.Message, StringComparison.Ordinal);
    }
}
