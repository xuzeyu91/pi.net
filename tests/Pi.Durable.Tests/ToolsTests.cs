using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Tools;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// P56：内建编码工具四件套（tools/*.ts）在真实本地环境上的行为，以及 edit-diff 的纯逻辑。
/// 对应 TS <c>test/tools.test.ts</c>（去掉依赖 Node 特性的 symlink / 跨文件系统序列化用例）。
/// </summary>
public class ToolsTests
{
    private static Context Ctx => Context.Background;

    // ─── 夹具 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 最小执行 API：只有环境、收集到的输出与诊断，不含任何 durable 能力。
    /// 对应 TS <c>fakeApi(env)</c>。文档面与事务面均不支持（工具不会用到）。
    /// </summary>
    private sealed class FakeToolApi(IExecutionEnv? env) : IToolExecutionApi
    {
        public List<string> Outputs { get; } = [];

        public List<ToolDiagnostic> Diagnostics { get; } = [];

        public IExecutionEnv? Env => env;

        public ShellOutputWindow? OutputWindow { get; set; }

        public TaskId<object?> TaskId => TaskId<object?>.From(1);

        public ConversationId ConversationId => ConversationId.From(1);

        public string CallId => "call";

        public IRegistrySnapshot Registry => Pi.Durable.Harness.Registry.CreateRegistry().Snapshot();

        public Task<Agent> AgentAsync(Context context) => throw new NotSupportedException();

        public void Output(string chunk, ShellOutputSkip? skipped = null) => Outputs.Add(chunk);

        public void Output(byte[] chunk, ShellOutputSkip? skipped = null) => Outputs.Add(Decode(chunk));

        public void Diagnostic(ToolDiagnostic diagnostic) => Diagnostics.Add(diagnostic);

        public Task DetailsAsync(object? value, Context context) => Task.CompletedTask;

        public Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context)
            => throw new NotSupportedException();

        public Task<object?> MemoAsync(string name, Context context) => Task.FromResult<object?>(null);

        public Task<object> MemoAsync(string name, object candidate, Context context) => Task.FromResult(candidate);

        public Task<TaskId<object?>> CreateTaskAsync(
            AnyDurableTask task, object? input, TaskOptions options, Context context)
            => throw new NotSupportedException();

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
            => throw new NotSupportedException();

        public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
            => throw new NotSupportedException();

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context)
            => throw new NotSupportedException();

        private static string Decode(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

        // ── 文档读取面（工具不使用；读写一律不支持） ──

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
            DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T>(
            DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
            DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        // ── 文档观察面 ──

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();

        public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            where T : class, IReadOnlyDictionary<string, object?> => throw new NotSupportedException();
    }

    private static readonly List<string> TempDirs = [];

    private static LocalExecutionEnv CreateEnv()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pi-durable-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        TempDirs.Add(dir);
        return new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = dir });
    }

    private sealed record RunResult(ToolExecutionResult Result, FakeToolApi Api)
    {
        public string Text => string.Join("\n", (Result.Content ?? [])
            .OfType<TextContent>().Select(part => part.Text));

        public string DiagnosticText => string.Join("\n", (Result.Diagnostics ?? [])
            .Select(diagnostic => diagnostic.Message));
    }

    private static async Task<RunResult> RunAsync(
        IToolRegistration tool, Dictionary<string, object?> args, IExecutionEnv? env, Context? context = null)
    {
        var api = new FakeToolApi(env);
        var result = await tool.ExecuteAsync(args, api, context ?? Ctx);
        return new RunResult(result, api);
    }

    private static async Task<(Exception Error, FakeToolApi Api)> RunFailingAsync(
        IToolRegistration tool, Dictionary<string, object?> args, IExecutionEnv env, Context? context = null)
    {
        var api = new FakeToolApi(env);
        try
        {
            await tool.ExecuteAsync(args, api, context ?? Ctx);
        }
        catch (Exception error)
        {
            return (error, api);
        }

        throw new InvalidOperationException("Expected the tool to throw");
    }

    // ─── 无环境 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tools_NoEnvironmentConfigured_ThrowsOrdinaryError()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(ReadTool.Create(), new Dictionary<string, object?> { ["path"] = "x" }, null));
        Assert.Contains("No execution environment", error.Message);
    }

    // ─── read ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_TrailingNewlineAtLimit_IsNotCountedAsExtraLine()
    {
        var env = CreateEnv();
        var text = string.Join("\n", Enumerable.Repeat("x", (int)Truncate.DefaultMaxLines)) + "\n";
        await WriteAsync(env, "exact.txt", text);

        var run = await RunAsync(ReadTool.Create(), new Dictionary<string, object?> { ["path"] = "exact.txt" }, env);

        Assert.Null(run.Result.Details);
        Assert.Empty(run.Result.Diagnostics ?? []);
    }

    [Fact]
    public async Task Read_TruncatesLargeTextByLineCount()
    {
        var env = CreateEnv();
        var text = string.Join("\n", Enumerable.Range(1, 2500).Select(index => $"Line {index}"));
        await WriteAsync(env, "large.txt", text);

        var run = await RunAsync(ReadTool.Create(), new Dictionary<string, object?> { ["path"] = "large.txt" }, env);

        Assert.Equal($"Showing lines 1-2000 of 2500. Use offset=2001 to continue.", run.DiagnosticText);
        Assert.Equal("truncated", run.Result.Diagnostics![0].Code);
    }

    // ─── write ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_WritesFileAndCreatesParentDirectories()
    {
        var env = CreateEnv();

        var run = await RunAsync(WriteTool.Create(), new Dictionary<string, object?>
        {
            ["path"] = "nested/dir/file.txt",
            ["content"] = "hello",
        }, env);

        Assert.Equal("Successfully wrote to nested/dir/file.txt", run.Text);
        Assert.Equal("hello", await ReadTextAsync(env, "nested/dir/file.txt"));
    }

    // ─── edit ───────────────────────────────────────────────────────────────

    [Fact]
    public void Edit_PrepareArguments_RepairsStringObjectAndLegacyShapes()
    {
        var prepare = EditTool.Create().PrepareArguments;
        var edit = new Dictionary<string, object?> { ["oldText"] = "a", ["newText"] = "b" };

        var asString = new Dictionary<string, object?>
        {
            ["path"] = "f",
            ["edits"] = "{\"oldText\":\"a\",\"newText\":\"b\"}",
        };
        var repaired = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(prepare(asString));
        Assert.Equal("a", Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsAssignableFrom<List<object?>>(repaired["edits"])))["oldText"]);

        // 调用方参数本身不被改动。
        Assert.Equal("{\"oldText\":\"a\",\"newText\":\"b\"}", asString["edits"]);
    }

    [Fact]
    public async Task Edit_PreservesBomAndCrlfLineEndings()
    {
        var env = CreateEnv();
        await WriteAsync(env, "edit.txt", "\uFEFFone\r\ntwo\r\n");

        await RunAsync(EditTool.Create(), new Dictionary<string, object?>
        {
            ["path"] = "edit.txt",
            ["edits"] = new List<object?>
            {
                new Dictionary<string, object?> { ["oldText"] = "two", ["newText"] = "TWO" },
            },
        }, env);

        Assert.Equal("\uFEFFone\r\nTWO\r\n", await ReadTextAsync(env, "edit.txt"));
    }

    // ─── bash ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bash_InvalidTimeout_ThrowsBeforeExecuting()
    {
        var env = CreateEnv();
        // timeout 校验在解析参数后立即执行，因此无需真实命令即可观察到失败。
        foreach (var invalid in new double[] { 0, -1, double.NaN })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
                BashTool.Create(),
                new Dictionary<string, object?> { ["command"] = "true", ["timeout"] = invalid },
                env));
            Assert.Contains("Invalid timeout", error.Message);
        }
    }

    [Fact]
    public async Task Bash_NonZeroExit_Throws()
    {
        var env = CreateEnv();
        var (error, _) = await RunFailingAsync(
            BashTool.Create(), new Dictionary<string, object?> { ["command"] = "exit 7" }, env);
        Assert.Contains("Command exited with code 7", error.Message);
    }

    [Fact]
    public async Task Bash_StreamsOutputThroughApiAndReturnsNoContent()
    {
        var env = CreateEnv();
        var run = await RunAsync(
            BashTool.Create(), new Dictionary<string, object?> { ["command"] = "printf out" }, env);
        Assert.Contains("out", string.Concat(run.Api.Outputs));
        Assert.Null(run.Result.Content);
        Assert.Empty(run.Result.Diagnostics ?? []);
    }

    // ─── CodingTools 扩展 ──────────────────────────────────────────────────

    [Fact]
    public void CodingTools_ExtensionExposesFourTools()
    {
        var extension = CodingTools.Extension;
        Assert.Equal("coding-tools", extension.Name);
        Assert.Equal(
            ["read", "write", "edit", "bash"],
            Assert.IsAssignableFrom<IReadOnlyList<IToolRegistration>>(extension.Tools)
                .Select(tool => tool.Name));
    }

    // ─── EditDiff 纯逻辑 ────────────────────────────────────────────────────

    [Fact]
    public void EditDiff_DetectLineEnding_PrefersFirstOccurrence()
    {
        Assert.Equal("\n", EditDiff.DetectLineEnding("one\ntwo\r\n"));
        Assert.Equal("\r\n", EditDiff.DetectLineEnding("one\r\ntwo\n"));
        Assert.Equal("\n", EditDiff.DetectLineEnding("one\ntwo"));
        Assert.Equal("\n", EditDiff.DetectLineEnding("no newline"));
    }

    [Fact]
    public void EditDiff_NormalizeAndRestore_RoundTrip()
    {
        Assert.Equal("a\nb\n", EditDiff.NormalizeToLf("a\r\nb\r"));
        Assert.Equal("a\r\nb\r\n", EditDiff.RestoreLineEndings("a\nb\n", "\r\n"));
        Assert.Equal("a\nb\n", EditDiff.RestoreLineEndings("a\nb\n", "\n"));
    }

    [Fact]
    public void EditDiff_StripBom_SplitsOnlyLeadingBom()
    {
        var (bom, text) = EditDiff.StripBom("\uFEFFone\uFEFF");
        Assert.Equal("\uFEFF", bom);
        Assert.Equal("one\uFEFF", text);

        var (none, plain) = EditDiff.StripBom("one");
        Assert.Equal("", none);
        Assert.Equal("one", plain);
    }

    [Fact]
    public void EditDiff_NormalizeForFuzzyMatch_FoldsQuotesDashesAndSpaces()
    {
        Assert.Equal("'a' \"b\" - c  d", EditDiff.NormalizeForFuzzyMatch("\u2018a\u2019 \u201Cb\u201D \u2014 c\u00A0\u3000d"));
        Assert.Equal("line", EditDiff.NormalizeForFuzzyMatch("line   "));
        // NFKC 把全角字母折成 ASCII。
        Assert.Equal("abc", EditDiff.NormalizeForFuzzyMatch("\uFF41\uFF42\uFF43"));
    }

    [Fact]
    public void EditDiff_FuzzyFindText_PrefersExactThenFuzzy()
    {
        var exact = EditDiff.FuzzyFindText("alpha\nbeta\n", "beta");
        Assert.True(exact.Found);
        Assert.False(exact.UsedFuzzyMatch);
        Assert.Equal("alpha\nbeta\n".IndexOf("beta", StringComparison.Ordinal), exact.Index);

        var fuzzy = EditDiff.FuzzyFindText("alpha\u2014beta", "alpha-beta");
        Assert.True(fuzzy.Found);
        Assert.True(fuzzy.UsedFuzzyMatch);

        var missing = EditDiff.FuzzyFindText("alpha", "gamma");
        Assert.False(missing.Found);
        Assert.Equal(-1, missing.Index);
    }

    [Fact]
    public void EditDiff_ApplyEdits_RejectsMissingDuplicateAndOverlapping()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => EditDiff.ApplyEditsToNormalizedContent(
            "one\ntwo\n", [new EditDiff.Edit("nope", "x")], "f.txt"));
        Assert.Contains("Could not find the exact text", missing.Message);

        var duplicate = Assert.Throws<InvalidOperationException>(() => EditDiff.ApplyEditsToNormalizedContent(
            "foo foo foo", [new EditDiff.Edit("foo", "bar")], "f.txt"));
        Assert.Contains("3 occurrences", duplicate.Message);

        var overlap = Assert.Throws<InvalidOperationException>(() => EditDiff.ApplyEditsToNormalizedContent(
            "one\ntwo\nthree\n",
            [new EditDiff.Edit("one\ntwo\n", "ONE\nTWO\n"), new EditDiff.Edit("two\nthree\n", "TWO\nTHREE\n")],
            "f.txt"));
        Assert.Contains("overlap", overlap.Message);
    }

    [Fact]
    public void EditDiff_ApplyEdits_AppliesDisjointEdits()
    {
        var applied = EditDiff.ApplyEditsToNormalizedContent(
            "alpha\nbeta\ngamma\ndelta\n",
            [new EditDiff.Edit("alpha\n", "ALPHA\n"), new EditDiff.Edit("gamma\n", "GAMMA\n")],
            "f.txt");
        Assert.Equal("ALPHA\nbeta\nGAMMA\ndelta\n", applied.NewContent);
    }

    [Fact]
    public void EditDiff_GenerateUnifiedPatch_HasTwoFileHeader()
    {
        var patch = EditDiff.GenerateUnifiedPatch("f.txt", "alpha\nbeta\n", "alpha\nBETA\n");
        Assert.Contains("--- f.txt", patch);
        Assert.Contains("+++ f.txt", patch);
        Assert.Contains("@@", patch);
    }

    [Fact]
    public void EditDiff_GenerateDiffString_ReportsFirstChangedLine()
    {
        var (diff, firstChangedLine) = EditDiff.GenerateDiffString("alpha\nbeta\ngamma\n", "alpha\nBETA\ngamma\n");
        Assert.Contains("BETA", diff);
        Assert.Equal(2, firstChangedLine);
    }

    // ─── Decoding.RangeDecoder 的 BOM 语义 ──────────────────────────────────

    [Fact]
    public void RangeDecoder_IsNotBomAware()
    {
        // 等价 TS new TextDecoder("utf-8", { ignoreBOM: true })：起始 BOM 被解码为 U+FEFF，而不是被吞掉。
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 0x61 };
        var decoder = Decoding.RangeDecoder();
        var chars = new char[bytes.Length + 4];
        decoder.Convert(bytes, 0, bytes.Length, chars, 0, chars.Length, flush: true, out _, out var used, out _);
        Assert.Equal("\uFEFFa", new string(chars, 0, used));
    }

    // ─── 辅助 ───────────────────────────────────────────────────────────────

    private static async Task WriteAsync(IExecutionEnv env, string path, string content)
        => (await env.WriteFileAsync(path, content, Ctx)).GetOrThrow();

    private static async Task<string> ReadTextAsync(IExecutionEnv env, string path)
        => (await env.ReadTextFileAsync(path, Ctx)).GetOrThrow();
}
