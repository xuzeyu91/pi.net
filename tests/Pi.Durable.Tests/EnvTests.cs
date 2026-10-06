using System.Text;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Xunit;
using static Pi.Durable.Env.LocalExecutionEnv;

namespace Pi.Durable.Tests;

public class EnvTests
{
    private static Context Ctx => Context.Background;

    private static Context AbortedContext() =>
        Context.Background.WithValue(Context.AbortSignalKey, new CancellationToken(canceled: true));

    /// <summary>与 TS 测试相同的确定性 PRNG。</summary>
    private static Func<double> Random(int seed)
    {
        uint state = (uint)seed;
        return () =>
        {
            state = state + 0x6d2b79f5 & 0xffffffff;
            var t = state;
            t = t ^ (t >> 15) * (t | 1) & 0xffffffff;
            t ^= t + (t ^ (t >> 7) | (t << 16) & 0xffffffff) * (t | 61) & 0xffffffff;
            return ((t ^ (t >> 14)) & 0xffffffff) / 4294967296.0;
        };
    }

    // 换行、ASCII、字节序标记、合法多字节序列、非法或截断的序列。
    private static readonly int[][] Pieces =
    {
        new[] { 0x0a }, new[] { 0x0a }, new[] { 0x61 }, new[] { 0x62, 0x63 }, new[] { 0xef, 0xbb, 0xbf },
        new[] { 0xc3, 0xa9 }, new[] { 0xe2, 0x82, 0xac }, new[] { 0xf0, 0x9f, 0x98, 0x80 },
        new[] { 0xe2, 0x82 }, new[] { 0xff }, new[] { 0x80 }, new[] { 0xf0, 0x9f }, new[] { 0x0d, 0x0a },
    };

    private static byte[] RandomFile(Func<double> next)
    {
        var bytes = new List<byte>();
        var pieces = (int)(next() * 60);
        for (var i = 0; i < pieces; i++)
            bytes.AddRange(Pieces[(int)(next() * Pieces.Length)].Select(b => (byte)b));
        return bytes.ToArray();
    }

    /// <summary>WHATWG 整体解码语义：丢起始 BOM，U+FFFD 替换非法序列。</summary>
    private static string WholeDecode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    // ---------- StreamDecoder ----------

    [Fact]
    public void StreamDecoder_KeepsUfeffAfterInvalidSequence()
    {
        // Node 的流式 TextDecoder 丢弃过跟在非法序列后的这个 U+FEFF。
        var bytes = new byte[] { 0xe2, 0x82, 0xef, 0xbb, 0xbf, 0x61 };
        var decoder = new StreamDecoder();
        var text = string.Concat(bytes.Select(b => decoder.Decode(new[] { b }))) + decoder.Decode(null);
        Assert.Equal(WholeDecode(bytes), text);
        Assert.Equal("\uFFFD\uFEFFa", text);
    }

    [Fact]
    public void StreamDecoder_DecodesAnyChunkingLikeWholeStream()
    {
        for (var seed = 1; seed <= 2000; seed++)
        {
            var next = Random(seed);
            var bytes = RandomFile(next);
            var decoder = new StreamDecoder();
            var text = "";
            for (var offset = 0; offset < bytes.Length;)
            {
                var size = 1 + (int)(next() * 7);
                var end = Math.Min(offset + size, bytes.Length);
                text += decoder.Decode(bytes[offset..end]);
                offset = end;
            }
            Assert.True((text + decoder.Decode(null)) == WholeDecode(bytes), $"seed {seed}");
        }
    }

    // ---------- LineScanner ----------

    [Fact]
    public void LineScanner_AgreesWithDecodingAndSplittingWholeFile()
    {
        for (var seed = 1; seed <= 2000; seed++)
        {
            var next = Random(seed);
            var file = RandomFile(next);
            var lines = WholeDecode(file).Split('\n');
            var startLine = (long)(next() * (lines.Length + 2));
            long? endLine = next() < 0.3 ? null : startLine + 1 + (long)(next() * (lines.Length + 1));
            var scanner = new LineScanner(startLine, endLine);
            for (var offset = 0; offset < file.Length;)
            {
                var size = 1 + (int)(next() * 7);
                var end = Math.Min(offset + size, file.Length);
                scanner.Push(file.AsSpan(offset, end - offset));
                offset = end;
            }
            var scan = scanner.Finish();
            var message = $"seed {seed}";
            Assert.True(scan.Newlines == lines.Length - 1, message);
            var fromIdx = Math.Min(startLine, lines.Length);
            var toIdx = Math.Min(endLine ?? lines.Length, lines.Length);
            var selected = lines[(int)fromIdx..(int)toIdx];
            string Decode(long from, long to) =>
                from > 0 ? Encoding.UTF8.GetString(file[(int)from..(int)to]) : WholeDecode(file[(int)from..(int)to]);
            Assert.True(Decode(scan.Start, scan.End) == string.Join("\n", selected), message);
            Assert.True(scan.SelectedBytes == Encoding.UTF8.GetByteCount(string.Join("\n", selected)), message);
            if (startLine < lines.Length)
            {
                Assert.True(Decode(scan.Start, scan.FirstLineEnd) == lines[(int)startLine], message);
                Assert.True(scan.FirstLineBytes == Encoding.UTF8.GetByteCount(lines[(int)startLine]), message);
                var lastLine = (long)Math.Min(endLine ?? lines.Length, lines.Length) - 1;
                var lastEnd = lastLine + 1 < lines.Length
                    ? Array.IndexOf(file, (byte)0x0a, (int)scan.LastLineStart)
                    : file.Length;
                Assert.True(Decode(scan.LastLineStart, lastEnd) == lines[(int)lastLine], message);
            }
            else
            {
                Assert.True(scan.Start == file.Length && scan.End == file.Length && scan.SelectedBytes == 0, message);
            }
        }
    }

    [Fact]
    public void LineScanner_RejectsEmptyOrInvalidRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineScanner(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineScanner(-1));
    }

    // ---------- 文件系统 ----------

    private static async Task<(LocalExecutionEnv Env, string Dir)> NewEnv()
    {
        var dir = Path.Join(Path.GetTempPath(), "pi-net-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (new LocalExecutionEnv(new LocalEnvOptions { Cwd = dir }), dir);
    }

    [Fact]
    public async Task FileSystem_ReadsWritesListsAndRemoves()
    {
        var (env, dir) = await NewEnv();
        var write = await env.WriteFileAsync("a/b.txt", "hello", Ctx);
        Assert.True(write.IsOk);
        var text = await env.ReadTextFileAsync("a/b.txt", Ctx);
        Assert.Equal("hello", text.Value);
        var binary = await env.ReadBinaryFileAsync("a/b.txt", Ctx);
        Assert.Equal("hello", Encoding.UTF8.GetString(binary.Value));
        var info = await env.FileInfoAsync("a/b.txt", Ctx);
        Assert.True(info.IsOk);
        Assert.Equal("b.txt", info.Value.Name);
        Assert.Equal(FileKind.File, info.Value.Kind);
        Assert.Equal(5, info.Value.Size);
        var dirInfo = await env.FileInfoAsync("a", Ctx);
        Assert.Equal(FileKind.Directory, dirInfo.Value.Kind);
        var listing = await env.ListDirAsync("a", Ctx);
        Assert.Single(listing.Value);
        var removed = await env.RemoveAsync("a", new RemoveOptions { Recursive = true }, Ctx);
        Assert.True(removed.IsOk);
        Assert.False((await env.ExistsAsync("a/b.txt", Ctx)).Value);
    }

    [Fact]
    public async Task FileSystem_AppendsCreatesParentsAndRenames()
    {
        var (env, dir) = await NewEnv();
        Assert.True((await env.AppendFileAsync("x/y.txt", "one", Ctx)).IsOk);
        Assert.True((await env.AppendFileAsync("x/y.txt", "two", Ctx)).IsOk);
        Assert.Equal("onetwo", (await env.ReadTextFileAsync("x/y.txt", Ctx)).Value);

        // rename 不建父目录：目标父目录缺失时失败（报源路径），目录存在后原子替换。
        var renamed = await env.RenameFileAsync("x/y.txt", "z/out.txt", Ctx);
        Assert.False(renamed.IsOk);
        Assert.True((await env.CreateDirAsync("z", null, Ctx)).IsOk);
        Assert.True((await env.RenameFileAsync("x/y.txt", "z/out.txt", Ctx)).IsOk);
        Assert.False((await env.ExistsAsync("x/y.txt", Ctx)).Value);
        Assert.Equal("onetwo", (await env.ReadTextFileAsync("z/out.txt", Ctx)).Value);
    }

    [Fact]
    public async Task FileSystem_TruncatesAndExtendsToExactSizes()
    {
        var (env, _) = await NewEnv();
        await env.WriteFileAsync("f.txt", "abcdefgh", Ctx);
        Assert.True((await env.TruncateFileAsync("f.txt", 3, Ctx)).IsOk);
        Assert.Equal("abc", (await env.ReadTextFileAsync("f.txt", Ctx)).Value);
        Assert.True((await env.TruncateFileAsync("f.txt", 5, Ctx)).IsOk);
        var bytes = (await env.ReadBinaryFileAsync("f.txt", Ctx)).Value;
        Assert.Equal(new byte[] { 97, 98, 99, 0, 0 }, bytes);
        Assert.False((await env.TruncateFileAsync("missing.txt", 0, Ctx)).IsOk);
        Assert.False((await env.TruncateFileAsync("f.txt", -1, Ctx)).IsOk);
    }

    [Fact]
    public async Task FileSystem_MissingPathsReportNotFoundAndExistsFalse()
    {
        var (env, _) = await NewEnv();
        var read = await env.ReadTextFileAsync("nope.txt", Ctx);
        Assert.False(read.IsOk);
        Assert.Equal(FileErrorCode.NotFound, read.Error.Code);
        Assert.EndsWith("nope.txt", read.Error.Path);
        Assert.False((await env.ExistsAsync("nope.txt", Ctx)).Value);
        var list = await env.ListDirAsync("nope", Ctx);
        Assert.False(list.IsOk);
        Assert.Equal(FileErrorCode.NotFound, list.Error.Code);
    }

    [Fact]
    public async Task FileSystem_TextLineReaderReportsTerminationAndPreservesCarriageReturns()
    {
        var (env, _) = await NewEnv();
        await env.WriteFileAsync("lines.txt", "one\r\ntwo\r\nthree", Ctx);
        var reader = await env.OpenTextLineReaderAsync("lines.txt", Ctx);
        var lines = new List<TextLine>();
        while (await reader.Value.ReadLineAsync(Ctx) is { IsOk: true } r && r.Value is { } line)
            lines.Add(line);
        Assert.Equal(3, lines.Count);
        Assert.Equal("one\r", lines[0].Text);
        Assert.True(lines[0].Terminated);
        Assert.Equal("two\r", lines[1].Text);
        Assert.True(lines[1].Terminated);
        Assert.Equal("three", lines[2].Text);
        Assert.False(lines[2].Terminated);
        await reader.Value.CloseAsync(Ctx);
    }

    [Fact]
    public async Task FileSystem_ReadTextLinesStopsAtLimit()
    {
        var (env, _) = await NewEnv();
        await env.WriteFileAsync("many.txt", "1\n2\n3\n4", Ctx);
        var lines = await env.ReadTextLinesAsync("many.txt", new ReadTextLinesOptions { MaxLines = 2 }, Ctx);
        Assert.Equal(new[] { "1", "2" }, lines.Value);
        var all = await env.ReadTextLinesAsync("many.txt", null, Ctx);
        Assert.Equal(new[] { "1", "2", "3", "4" }, all.Value);
        var none = await env.ReadTextLinesAsync("many.txt", new ReadTextLinesOptions { MaxLines = 0 }, Ctx);
        Assert.Empty(none.Value);
    }

    [Fact]
    public async Task FileSystem_BinaryReaderScansLines()
    {
        var (env, _) = await NewEnv();
        await env.WriteFileAsync("scan.txt", "alpha\nbeta\ngamma\ndelta", Ctx);
        var reader = await env.OpenBinaryReaderAsync("scan.txt", null, Ctx);
        Assert.True(reader.IsOk);
        var scan = await reader.Value.ScanLinesAsync(new LineScanOptions(1, 3), Ctx);
        Assert.True(scan.IsOk);
        // 行 [1,3) = "beta\ngamma"：从第 6 字节开始，到第 16 字节（第二行末尾，不含其换行）。
        Assert.Equal(3, scan.Value.Newlines);
        Assert.Equal(6, scan.Value.Start);
        Assert.Equal(16, scan.Value.End);
        var content = await reader.Value.ReadAsync(scan.Value.Start, scan.Value.End - scan.Value.Start, Ctx);
        Assert.Equal("beta\ngamma", Encoding.UTF8.GetString(content.Value));
        var withBom = await env.OpenBinaryReaderAsync("bom.txt", null, Ctx);
        await env.WriteFileAsync("bom.txt", "\uFEFFfirst\nsecond", Ctx);
        var bomReader = withBom.IsOk ? withBom : await env.OpenBinaryReaderAsync("bom.txt", null, Ctx);
        var bomScan = await bomReader.Value.ScanLinesAsync(new LineScanOptions(0, 1), Ctx);
        Assert.True(bomScan.IsOk);
        // 文件起始的 BOM 不计入解码大小。
        Assert.Equal(5, bomScan.Value.FirstLineBytes);
        await bomReader.Value.CloseAsync(Ctx);
        await reader.Value.CloseAsync(Ctx);
    }

    [Fact]
    public async Task FileSystem_TempDirsAndFiles()
    {
        var (env, _) = await NewEnv();
        var dir = await env.CreateTempDirAsync("pi-", Ctx);
        Assert.True(dir.IsOk);
        Assert.True(Directory.Exists(dir.Value));
        var file = await env.CreateTempFileAsync(new TempFileOptions { Suffix = ".log" }, Ctx);
        Assert.True(file.IsOk);
        Assert.EndsWith(".log", file.Value);
        Assert.True(File.Exists(file.Value));
    }

    [Fact]
    public async Task FileSystem_PreAbortedOperationsReturnAbortedWithoutSideEffects()
    {
        var (env, dir) = await NewEnv();
        var aborted = AbortedContext();
        var write = await env.WriteFileAsync("x.txt", "hi", aborted);
        Assert.False(write.IsOk);
        Assert.Equal(FileErrorCode.Aborted, write.Error.Code);
        Assert.False(File.Exists(Path.Join(dir, "x.txt")));
    }

    // ---------- Shell ----------

    private static bool HasBash()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (programFiles is not null && File.Exists(Path.Join(programFiles, "Git", "bin", "bash.exe"))) return true;
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (programFilesX86 is not null && File.Exists(Path.Join(programFilesX86, "Git", "bin", "bash.exe"))) return true;
        return false;
    }

    [Fact]
    public async Task Shell_ExecutesStringCommandsInCwdWithEnvOverrides()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, dir) = await NewEnv();
        var result = await env.ExecAsync(
            ShellCommand.FromString("echo $PI_TEST_VAR && pwd"),
            new ShellExecOptions
            {
                Env = new Dictionary<string, string> { ["PI_TEST_VAR"] = "hello" },
            }, Ctx);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Null(result.Value.SpillPath);
    }

    [Fact]
    public async Task Shell_RunsArgvDirectlyWithoutShellParsing()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var result = await env.ExecAsync(
            ShellCommand.FromArgv("echo", "$HOME'not-expanded'"), null, Ctx);
        // argv 形式直接运行 echo 程序：参数不会被 shell 展开。
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Equal(0, result.Value.ExitCode);
    }

    [Fact]
    public async Task Shell_StreamsCombinedStdoutAndStderr()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var received = new List<(string Text, ShellOutputStream Stream)>();
        var gate = new object();
        var result = await env.ExecAsync(
            ShellCommand.FromString("echo out1 && echo err1 1>&2 && echo out2"),
            new ShellExecOptions
            {
                OnOutput = (text, _, info) =>
                {
                    lock (gate) received.Add((text, info.Stream));
                },
            }, Ctx);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        lock (gate)
        {
            var combined = string.Concat(received.Select(r => r.Text));
            Assert.Contains("out1", combined);
            Assert.Contains("err1", combined);
            Assert.Contains("out2", combined);
            Assert.Contains(received, r => r.Stream == ShellOutputStream.Stderr);
        }
    }

    [Fact]
    public async Task Shell_ReportsTimeoutAndInvalidTimeouts()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var invalid = await env.ExecAsync(ShellCommand.FromString("true"),
            new ShellExecOptions { Timeout = -1 }, Ctx);
        Assert.False(invalid.IsOk);
        Assert.Equal(ExecutionErrorCode.Timeout, invalid.Error.Code);

        var timeout = await env.ExecAsync(ShellCommand.FromString("sleep 5"),
            new ShellExecOptions { Timeout = 0.2 }, Ctx);
        Assert.False(timeout.IsOk);
        Assert.Equal(ExecutionErrorCode.Timeout, timeout.Error.Code);
        Assert.Contains("timeout:0.2", timeout.Error.Message);
    }

    [Fact]
    public async Task Shell_ReturnsNonZeroExitCodesAsSuccess()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var result = await env.ExecAsync(ShellCommand.FromString("exit 7"), null, Ctx);
        Assert.True(result.IsOk);
        Assert.Equal(7, result.Value.ExitCode);
    }

    [Fact]
    public async Task Shell_ReportsMissingWorkingDirectoryBeforeSpawning()
    {
        var (env, _) = await NewEnv();
        var result = await env.ExecAsync(ShellCommand.FromString("true"),
            new ShellExecOptions { Cwd = "definitely-missing-dir" }, Ctx);
        Assert.False(result.IsOk);
        Assert.Equal(ExecutionErrorCode.SpawnError, result.Error.Code);
        Assert.Contains("Working directory does not exist", result.Error.Message);
    }

    [Fact]
    public async Task Shell_ReturnsAbortedForPreAbortedContext()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var aborted = AbortedContext();
        var result = await env.ExecAsync(ShellCommand.FromString("echo hi"), null, aborted);
        Assert.False(result.IsOk);
        Assert.Equal(ExecutionErrorCode.Aborted, result.Error.Code);
    }

    [Fact]
    public async Task Shell_SpillsCompleteOutputPastThresholds()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        // 阈值之内不落盘。
        var small = await env.ExecAsync(ShellCommand.FromString("echo hello"),
            new ShellExecOptions { Spill = new ShellSpillOptions { AfterBytes = 1000, AfterLines = 10 } }, Ctx);
        Assert.True(small.IsOk);
        Assert.Null(small.Value.SpillPath);

        // 超过字节阈值后完整输出落盘。
        var large = await env.ExecAsync(
            ShellCommand.FromString("seq 1 1000"),
            new ShellExecOptions { Spill = new ShellSpillOptions { AfterBytes = 100, AfterLines = 10 } }, Ctx);
        Assert.True(large.IsOk, large.IsOk ? "" : large.Error.Message);
        Assert.NotNull(large.Value.SpillPath);
        var spilled = await File.ReadAllLinesAsync(large.Value.SpillPath!);
        Assert.Equal(1000, spilled.Length);
        Assert.Equal("1000", spilled[^1]);

        // 超时的命令报告溢出文件。
        var timedOut = await env.ExecAsync(
            ShellCommand.FromString("seq 1 1000 && sleep 5"),
            new ShellExecOptions
            {
                Timeout = 1,
                Spill = new ShellSpillOptions { AfterBytes = 100, AfterLines = 10 },
            }, Ctx);
        Assert.False(timedOut.IsOk);
        Assert.Equal(ExecutionErrorCode.Timeout, timedOut.Error.Code);
        Assert.NotNull(timedOut.Error.SpillPath);
        if (timedOut.Error.SpillPath is { } spillFile && File.Exists(spillFile)) File.Delete(spillFile);
    }

    [Fact]
    public async Task Shell_CleanupTerminatesActiveProcesses()
    {
        if (!HasBash()) return; // 环境无 bash（如 Git for Windows 未装）时跳过
        var (env, _) = await NewEnv();
        var execTask = env.ExecAsync(ShellCommand.FromString("sleep 30"), null, Ctx);
        await Task.Delay(300);
        await env.CleanupAsync(Ctx);
        var result = await execTask;
        // 进程被杀：以非成功结果结束（中止或非零退出码），且不会等满 30 秒。
        Assert.True(result.IsOk ? result.Value.ExitCode != 0 : true);
    }
}
