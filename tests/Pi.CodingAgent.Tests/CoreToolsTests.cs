using System.Text;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Tools;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the 4c tool system, mirroring the assertions in the TypeScript
/// <c>packages/coding-agent/test/tools.test.ts</c> (read / write / edit / bash / grep / find / ls)
/// plus the jsdiff line-diff and unified-patch behavior the edit tool's details depend on.
/// </summary>
/// <remarks>
/// The TS suite runs against real files and a real shell; these tests replay the same inputs
/// against the port and assert the same observable output. Shell-dependent cases (signal exit
/// codes, WSL transport) are covered by the port's own seams instead of the host shell.
/// </remarks>
public class CoreToolsTests : IDisposable
{
    private readonly string _testDir;

    public CoreToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pi-core-tools-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked temp directory must not fail the test run.
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string GetText(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    private static async Task<AgentToolResult> ExecuteAsync(
        ToolDefinition definition,
        Dictionary<string, object?> args) =>
        await definition.Execute("test-call", args, CancellationToken.None, null, null);

    // ---------------------------------------------------------------------------------------------
    // read tool
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Read_FitsWithinLimits_ReturnsContentVerbatim()
    {
        var content = "Hello, world!\nLine 2\nLine 3";
        var testFile = WriteFile("test.txt", content);

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile });

        Assert.Equal(content, GetText(result));
        Assert.DoesNotContain("Use offset=", GetText(result));
        Assert.Null(result.Details);
        Assert.Equal(content, result.StructuredContent);
    }

    [Fact]
    public async Task Read_MissingFile_ThrowsEnoent()
    {
        var testFile = Path.Combine(_testDir, "nonexistent.txt");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile }));

        Assert.Contains("ENOENT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ExceedsLineLimit_TruncatesAt2000Lines()
    {
        var lines = Enumerable.Range(1, 2500).Select(i => $"Line {i}");
        var testFile = WriteFile("large.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile });

        var output = GetText(result);
        Assert.Contains("Line 1", output, StringComparison.Ordinal);
        Assert.Contains("Line 2000", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 2001", output, StringComparison.Ordinal);
        Assert.Contains("[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ExceedsByteLimit_ReportsByteLimitNotice()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"Line {i}: {new string('x', 200)}");
        var testFile = WriteFile("large-bytes.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile });

        var output = GetText(result);
        Assert.Contains("Line 1:", output, StringComparison.Ordinal);
        Assert.Matches(@"\[Showing lines 1-\d+ of 500 \(.* limit\)\. Use offset=\d+ to continue\.\]", output);
    }

    [Fact]
    public async Task Read_Offset_StartsAtRequestedLine()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        var testFile = WriteFile("offset-test.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile, ["offset"] = 51 });

        var output = GetText(result);
        Assert.DoesNotContain("Line 50", output, StringComparison.Ordinal);
        Assert.Contains("Line 51", output, StringComparison.Ordinal);
        Assert.Contains("Line 100", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Use offset=", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_Limit_CapsLinesAndReportsRemaining()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        var testFile = WriteFile("limit-test.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile, ["limit"] = 10 });

        var output = GetText(result);
        Assert.Contains("Line 1", output, StringComparison.Ordinal);
        Assert.Contains("Line 10", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 11", output, StringComparison.Ordinal);
        Assert.Contains("[90 more lines in file. Use offset=11 to continue.]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_OffsetAndLimit_Combine()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"Line {i}");
        var testFile = WriteFile("offset-limit-test.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile, ["offset"] = 41, ["limit"] = 20 });

        var output = GetText(result);
        Assert.DoesNotContain("Line 40", output, StringComparison.Ordinal);
        Assert.Contains("Line 41", output, StringComparison.Ordinal);
        Assert.Contains("Line 60", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Line 61", output, StringComparison.Ordinal);
        Assert.Contains("[40 more lines in file. Use offset=61 to continue.]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_OffsetBeyondEnd_Throws()
    {
        var testFile = WriteFile("short.txt", "Line 1\nLine 2\nLine 3");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile, ["offset"] = 100 }));

        Assert.Contains("Offset 100 is beyond end of file (3 lines total)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_Truncated_IncludesTruncationDetails()
    {
        var lines = Enumerable.Range(1, 2500).Select(i => $"Line {i}");
        var testFile = WriteFile("large-file.txt", string.Join("\n", lines));

        var result = await ExecuteAsync(
            ReadTool.CreateReadToolDefinition(_testDir),
            new() { ["path"] = testFile });

        var details = Assert.IsType<ReadToolDetails>(result.Details);
        Assert.NotNull(details.Truncation);
        Assert.True(details.Truncation!.Truncated);
        Assert.Equal("lines", details.Truncation.TruncatedBy);
        Assert.Equal(2500, details.Truncation.TotalLines);
        Assert.Equal(2000, details.Truncation.OutputLines);
    }

    // ---------------------------------------------------------------------------------------------
    // write tool
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Write_CreatesFileWithContent()
    {
        var testFile = Path.Combine(_testDir, "write-test.txt");

        var result = await ExecuteAsync(
            WriteTool.CreateWriteToolDefinition(_testDir),
            new() { ["path"] = testFile, ["content"] = "hello" });

        Assert.Contains("Successfully wrote to", GetText(result), StringComparison.Ordinal);
        Assert.Equal("hello", File.ReadAllText(testFile));
    }

    [Fact]
    public async Task Write_CreatesParentDirectories()
    {
        var testFile = Path.Combine(_testDir, "nested", "deep", "write-test.txt");

        await ExecuteAsync(
            WriteTool.CreateWriteToolDefinition(_testDir),
            new() { ["path"] = testFile, ["content"] = "hello" });

        Assert.Equal("hello", File.ReadAllText(testFile));
    }

    // ---------------------------------------------------------------------------------------------
    // edit tool
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Edit_ReplacesText_AndProducesApplyablePatch()
    {
        var testFile = WriteFile("edit-test.txt", "Hello, world!");

        var result = await ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "world", ["newText"] = "testing" } },
            });

        Assert.Contains("Successfully replaced", GetText(result), StringComparison.Ordinal);
        var details = Assert.IsType<EditToolDetails>(result.Details);
        Assert.Contains("testing", details.Diff, StringComparison.Ordinal);
        Assert.Contains("--- ", details.Patch, StringComparison.Ordinal);
        Assert.Contains("+++ ", details.Patch, StringComparison.Ordinal);
        Assert.Contains("@@", details.Patch, StringComparison.Ordinal);
        Assert.Contains("-Hello, world!", details.Patch, StringComparison.Ordinal);
        Assert.Contains("+Hello, testing!", details.Patch, StringComparison.Ordinal);
        Assert.Equal("Hello, testing!", File.ReadAllText(testFile));
    }

    [Fact]
    public async Task Edit_TextNotFound_Throws()
    {
        var testFile = WriteFile("edit-test.txt", "Hello, world!");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "nonexistent", ["newText"] = "testing" } },
            }));

        Assert.Contains("Could not find the exact text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_MissingTarget_IncludesEnoent()
    {
        var missingFile = Path.Combine(_testDir, "missing.txt");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = missingFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "hello", ["newText"] = "world" } },
            }));

        Assert.Contains($"Could not edit file: {missingFile}. Error code: ENOENT.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_DuplicateText_Throws()
    {
        var testFile = WriteFile("edit-test.txt", "foo foo foo");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "foo", ["newText"] = "bar" } },
            }));

        Assert.Contains("Found 3 occurrences", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_MultipleDisjointRegions_ReplacesAll()
    {
        var testFile = WriteFile("edit-multi.txt", "alpha\nbeta\ngamma\ndelta\n");

        var result = await ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "alpha\n", ["newText"] = "ALPHA\n" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "gamma\n", ["newText"] = "GAMMA\n" } },
            });

        Assert.Contains("Successfully replaced 2 block(s)", GetText(result), StringComparison.Ordinal);
        Assert.Equal("ALPHA\nbeta\nGAMMA\ndelta\n", File.ReadAllText(testFile));
        var details = Assert.IsType<EditToolDetails>(result.Details);
        Assert.Contains("ALPHA", details.Diff, StringComparison.Ordinal);
        Assert.Contains("GAMMA", details.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_LargeGap_CollapsesUnchangedContext()
    {
        var lines = Enumerable.Range(1, 600).Select(i => $"line {i:D3}");
        var testFile = WriteFile("edit-multi-large-gap.txt", $"{string.Join("\n", lines)}\n");

        var result = await ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "line 100\n", ["newText"] = "LINE 100\n" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "line 300\n", ["newText"] = "LINE 300\n" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "line 500\n", ["newText"] = "LINE 500\n" } },
            });

        var details = Assert.IsType<EditToolDetails>(result.Details);
        Assert.Contains("LINE 100", details.Diff, StringComparison.Ordinal);
        Assert.Contains("LINE 300", details.Diff, StringComparison.Ordinal);
        Assert.Contains("LINE 500", details.Diff, StringComparison.Ordinal);
        Assert.Contains("...", details.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 250", details.Diff, StringComparison.Ordinal);
        Assert.True(details.Diff.Split('\n').Length < 50);
    }

    [Fact]
    public async Task Edit_MatchesAgainstOriginalNotIncrementally()
    {
        var testFile = WriteFile("edit-multi-original.txt", "foo\nbar\nbaz\n");

        await ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "foo\n", ["newText"] = "foo bar\n" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "bar\n", ["newText"] = "BAR\n" } },
            });

        Assert.Equal("foo bar\nBAR\nbaz\n", File.ReadAllText(testFile));
    }

    [Fact]
    public async Task Edit_EmptyEdits_Throws()
    {
        var testFile = WriteFile("edit-empty-edits.txt", "hello\nworld\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new() { ["path"] = testFile, ["edits"] = new System.Text.Json.Nodes.JsonArray() }));

        Assert.Contains("edits must contain at least one replacement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_OverlappingEdits_Throws()
    {
        var testFile = WriteFile("edit-overlap.txt", "alpha beta gamma\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "alpha beta", ["newText"] = "ALPHA BETA" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "beta gamma", ["newText"] = "BETA GAMMA" } },
            }));

        Assert.Contains("overlap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_FailingEdit_DoesNotPartiallyApply()
    {
        var testFile = WriteFile("edit-partial.txt", "alpha\nbeta\ngamma\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(
            EditTool.CreateEditToolDefinition(_testDir),
            new()
            {
                ["path"] = testFile,
                ["edits"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["oldText"] = "alpha\n", ["newText"] = "ALPHA\n" }, new System.Text.Json.Nodes.JsonObject { ["oldText"] = "missing\n", ["newText"] = "MISSING\n" } },
            }));

        Assert.Equal("alpha\nbeta\ngamma\n", File.ReadAllText(testFile));
    }

    // ---------------------------------------------------------------------------------------------
    // edit diff / jsdiff parity
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void JsDiff_DiffLines_MatchesJsdiffShapes()
    {
        // (old, new, expected part values in order). jsdiff's line tokenizer merges each separator
        // into its line, so "a\nb\n" tokenizes to ["a\n", "b\n"] and an unchanged run collapses
        // into a single part.
        var cases = new (string Old, string New, string[] Expected)[]
        {
            ("", "", Array.Empty<string>()),
            ("a\nb\nc\n", "a\nB\nc\n", new[] { "a\n", "b\n", "B\n", "c\n" }),
            ("a\nb\nc\n", "a\nb\nc", new[] { "a\nb\n", "c\n", "c" }),
            ("a\nb\nc", "a\nb\nc\n", new[] { "a\nb\n", "c", "c\n" }),
            ("line1\nline2\nline3\n", "line1\nline3\n", new[] { "line1\n", "line2\n", "line3\n" }),
            ("same\n", "same\n", new[] { "same\n" }),
            ("x\n", "y\n", new[] { "x\n", "y\n" }),
            ("a\n\n\nb\n", "a\n\nb\n", new[] { "a\n\n", "\n", "b\n" }),
        };

        foreach (var (old, @new, expected) in cases)
        {
            var parts = JsDiff.DiffLines(old, @new);
            var values = parts.Select(part => part.Value).ToArray();
            Assert.Equal(expected, values);
        }
    }

    [Fact]
    public void JsDiff_CreateTwoFilesPatch_MatchesJsdiffFormat()
    {
        var patch = JsDiff.CreateTwoFilesPatch("f.ts", "f.ts", "a\nb\nc\n", "a\nB\nc\n");

        Assert.Equal(
            "--- f.ts\n+++ f.ts\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n",
            patch);
    }

    [Fact]
    public void JsDiff_CreateTwoFilesPatch_HandlesNoTrailingNewline()
    {
        // jsdiff counts the "\ No newline at end of file" marker as a line, so the hunk header
        // reports 4 lines for a 3-line file whose last line loses its newline. The trailing empty
        // part appended by structuredPatch also emits its own marker line.
        var patch = JsDiff.CreateTwoFilesPatch("f.ts", "f.ts", "a\nb\nc\n", "a\nb\nc");

        Assert.Equal(
            "--- f.ts\n+++ f.ts\n@@ -1,4 +1,4 @@\n a\n b\n-c\n+c\n\\ No newline at end of file\n \n\\ No newline at end of file\n",
            patch);
    }

    [Fact]
    public void JsDiff_CreateTwoFilesPatch_HandlesAddition()
    {
        var patch = JsDiff.CreateTwoFilesPatch("f.ts", "f.ts", "a\nc\n", "a\nb\nc\n");

        Assert.Equal(
            "--- f.ts\n+++ f.ts\n@@ -1,2 +1,3 @@\n a\n+b\n c\n",
            patch);
    }

    [Fact]
    public void JsDiff_CreateTwoFilesPatch_HandlesDeletion()
    {
        var patch = JsDiff.CreateTwoFilesPatch("f.ts", "f.ts", "a\nb\nc\n", "a\nc\n");

        Assert.Equal(
            "--- f.ts\n+++ f.ts\n@@ -1,3 +1,2 @@\n a\n-b\n c\n",
            patch);
    }

    [Fact]
    public void GenerateDiffString_ReportsFirstChangedLine()
    {
        var result = EditDiff.GenerateDiffString("a\nb\nc\n", "a\nB\nc\n");

        Assert.Equal(2, result.FirstChangedLine);
        Assert.Contains("+2 B", result.Diff, StringComparison.Ordinal);
        Assert.Contains("-2 b", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateDiffString_CollapsesLargeContextGaps()
    {
        var oldLines = Enumerable.Range(1, 600).Select(i => $"line {i:D3}");
        var newLines = oldLines.ToList();
        newLines[99] = "LINE 100";
        var result = EditDiff.GenerateDiffString($"{string.Join("\n", oldLines)}\n", $"{string.Join("\n", newLines)}\n");

        Assert.Contains("LINE 100", result.Diff, StringComparison.Ordinal);
        Assert.Contains("...", result.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 250", result.Diff, StringComparison.Ordinal);
        Assert.True(result.Diff.Split('\n').Length < 50);
    }

    // ---------------------------------------------------------------------------------------------
    // truncate
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TruncateHead_UnderLimits_ReturnsUnchanged()
    {
        var result = Truncate.TruncateHead("a\nb\nc");

        Assert.False(result.Truncated);
        Assert.Null(result.TruncatedBy);
        Assert.Equal("a\nb\nc", result.Content);
        Assert.Equal(3, result.TotalLines);
    }

    [Fact]
    public void TruncateHead_LineLimit_KeepsFirstLines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 2500).Select(i => $"Line {i}"));
        var result = Truncate.TruncateHead(content);

        Assert.True(result.Truncated);
        Assert.Equal("lines", result.TruncatedBy);
        Assert.Equal(2000, result.OutputLines);
        Assert.Equal(2500, result.TotalLines);
        Assert.StartsWith("Line 1\n", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("Line 2000", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateHead_FirstLineTooLarge_ReturnsEmptyWithFlag()
    {
        var content = new string('x', 60 * 1024);
        var result = Truncate.TruncateHead(content);

        Assert.True(result.Truncated);
        Assert.True(result.FirstLineExceedsLimit);
        Assert.Equal("", result.Content);
    }

    [Fact]
    public void TruncateTail_KeepsLastLines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 2500).Select(i => $"Line {i}"));
        var result = Truncate.TruncateTail(content);

        Assert.True(result.Truncated);
        Assert.Equal("lines", result.TruncatedBy);
        Assert.Equal(2000, result.OutputLines);
        Assert.StartsWith("Line 501", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("Line 2500", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateTail_UnderLimits_ReturnsUnchanged()
    {
        var result = Truncate.TruncateTail("a\nb\nc");

        Assert.False(result.Truncated);
        Assert.Equal("a\nb\nc", result.Content);
    }

    [Fact]
    public void TruncateLine_TruncatesLongLines()
    {
        var line = new string('y', 600);
        var (text, wasTruncated) = Truncate.TruncateLine(line);

        Assert.True(wasTruncated);
        Assert.Equal($"{new string('y', 500)}... [truncated]", text);
    }

    [Fact]
    public void TruncateMiddle_KeepsHeadAndTail()
    {
        var content = string.Concat(Enumerable.Range(0, 1000).Select(i => "0123456789"));
        var result = Truncate.TruncateMiddle(content, 1000);

        Assert.True(result.Truncated);
        Assert.StartsWith("0123456789", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("0123456789", result.Content, StringComparison.Ordinal);
        Assert.Contains("…9000 chars truncated…", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSize_FormatsHumanReadable()
    {
        Assert.Equal("512B", Truncate.FormatSize(512));
        Assert.Equal("1.0KB", Truncate.FormatSize(1024));
        Assert.Equal("50.0KB", Truncate.FormatSize(50 * 1024));
        Assert.Equal("1.0MB", Truncate.FormatSize(1024 * 1024));
    }

    // ---------------------------------------------------------------------------------------------
    // output accumulator
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void OutputAccumulator_StreamsUtf8AcrossChunkBoundaries()
    {
        var accumulator = new OutputAccumulator();
        var bytes = Encoding.UTF8.GetBytes("héllo wörld");
        // Split the multi-byte sequence across two appends.
        accumulator.Append(bytes[..^1]);
        accumulator.Append(bytes[^1..]);
        accumulator.Finish();

        var snapshot = accumulator.Snapshot();
        Assert.Equal("héllo wörld", snapshot.Content);
        Assert.False(snapshot.Truncation.Truncated);
    }

    [Fact]
    public void OutputAccumulator_TracksTotalsAndTruncates()
    {
        var accumulator = new OutputAccumulator(new OutputAccumulatorOptions
        {
            MaxLines = 10,
            MaxBytes = 1024,
        });
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}")) + "\n");
        accumulator.Append(payload);
        accumulator.Finish();

        var snapshot = accumulator.Snapshot();
        Assert.True(snapshot.Truncation.Truncated);
        Assert.Equal("lines", snapshot.Truncation.TruncatedBy);
        Assert.Equal(50, snapshot.Truncation.TotalLines);
        Assert.Equal(10, snapshot.Truncation.OutputLines);
        Assert.StartsWith("line 41", snapshot.Content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // file mutation queue
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileMutationQueue_SerializesSameFileOperations()
    {
        var order = new List<int>();
        async Task<int> Work(int id)
        {
            return await FileMutationQueue.WithFileMutationQueueAsync<int>(
                Path.Combine(_testDir, "queue.txt"),
                async () =>
                {
                    order.Add(id);
                    await Task.Delay(20);
                    order.Add(-id);
                    return id;
                });
        }

        await Task.WhenAll(Work(1), Work(2), Work(3));

        // Each operation completes before the next one starts.
        Assert.Equal(new[] { 1, -1, 2, -2, 3, -3 }, order);
    }

    [Fact]
    public async Task FileMutationQueue_AllowsParallelDifferentFiles()
    {
        var running = 0;
        var maxParallel = 0;
        async Task Work(string name)
        {
            await FileMutationQueue.WithFileMutationQueueAsync<object>(
                Path.Combine(_testDir, name),
                async () =>
                {
                    running++;
                    maxParallel = Math.Max(maxParallel, running);
                    await Task.Delay(30);
                    running--;
                    return new object();
                });
        }

        await Task.WhenAll(Work("a.txt"), Work("b.txt"), Work("c.txt"));

        Assert.True(maxParallel > 1, $"expected parallel execution, saw max {maxParallel}");
    }

    // ---------------------------------------------------------------------------------------------
    // path utils
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveToCwd_ExpandsTildeAndJoinsRelative()
    {
        Assert.Equal(
            Path.Combine(_testDir, "file.txt"),
            ToolPathUtils.ResolveToCwd("file.txt", _testDir));
        Assert.Equal(
            Path.Combine(_testDir, "a", "b.txt"),
            ToolPathUtils.ResolveToCwd("./a/b.txt", _testDir));
    }

    [Fact]
    public async Task PathExists_AnswersForFilesAndDirectories()
    {
        var file = WriteFile("exists.txt", "x");

        Assert.True(await ToolPathUtils.PathExistsAsync(file));
        Assert.True(await ToolPathUtils.PathExistsAsync(_testDir));
        Assert.False(await ToolPathUtils.PathExistsAsync(Path.Combine(_testDir, "nope.txt")));
    }

    // ---------------------------------------------------------------------------------------------
    // tool definition wrapper
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task WrapToolDefinition_PreservesMetadataAndBridgesExecute()
    {
        var definition = new ToolDefinition
        {
            Name = "probe",
            Label = "probe",
            Description = "probe tool",
            Parameters = new Pi.Ai.Types.ToolSchema(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
            }),
            PromptSnippet = "probe snippet",
            PromptGuidelines = new[] { "probe guideline" },
            Execute = (_, _, _, _, _) => Task.FromResult(new AgentToolResult([new TextContent("ok")])),
        };

        var tool = ToolDefinitionWrapper.WrapToolDefinition(definition);

        Assert.Equal("probe", tool.Name);
        Assert.Equal("probe tool", tool.Description);
        Assert.Equal("probe snippet", tool.PromptSnippet);
        Assert.Equal(new[] { "probe guideline" }, tool.PromptGuidelines);
        var result = await tool.Execute("call-1", new Dictionary<string, object?>(), CancellationToken.None, null);
        Assert.Equal("ok", GetText(result));
    }

    [Fact]
    public void CreateToolDefinitionFromAgentTool_RoundTripsMetadata()
    {
        var tool = new AgentTool(
            "echo",
            "echo tool",
            new Pi.Ai.Types.ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            "echo",
            (_, _, _, _) => Task.FromResult(new AgentToolResult([new TextContent("ok")])));

        var definition = ToolDefinitionWrapper.CreateToolDefinitionFromAgentTool(tool);

        Assert.Equal("echo", definition.Name);
        Assert.Equal("echo tool", definition.Description);
        Assert.Equal("echo", definition.Label);
    }

    [Fact]
    public void PrepareEditArguments_AcceptsJsonStringArray()
    {
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "f.txt",
            ["edits"] = "[{\"oldText\":\"a\",\"newText\":\"b\"}]",
        };

        var prepared = Assert.IsType<System.Text.Json.Nodes.JsonObject>(EditTool.PrepareEditArguments(args));

        Assert.IsType<System.Text.Json.Nodes.JsonArray>(prepared["edits"]);
    }

    [Fact]
    public void PrepareEditArguments_AcceptsSingleEditObject()
    {
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "f.txt",
            ["edits"] = new System.Text.Json.Nodes.JsonObject
            {
                ["oldText"] = "a",
                ["newText"] = "b",
            },
        };

        var prepared = Assert.IsType<System.Text.Json.Nodes.JsonObject>(EditTool.PrepareEditArguments(args));

        Assert.IsType<System.Text.Json.Nodes.JsonArray>(prepared["edits"]);
    }

    [Fact]
    public void PrepareEditArguments_AcceptsLegacyTopLevelFields()
    {
        var args = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "f.txt",
            ["oldText"] = "a",
            ["newText"] = "b",
        };

        var prepared = Assert.IsType<System.Text.Json.Nodes.JsonObject>(EditTool.PrepareEditArguments(args));

        var edits = Assert.IsType<System.Text.Json.Nodes.JsonArray>(prepared["edits"]);
        Assert.Single(edits);
        Assert.False(prepared.ContainsKey("oldText"));
        Assert.False(prepared.ContainsKey("newText"));
    }
}
