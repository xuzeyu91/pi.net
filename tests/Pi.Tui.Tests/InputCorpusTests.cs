using System.Text.Json;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>components/input.ts</c> port.
/// </summary>
/// <remarks>
/// <c>input-corpus.json</c> holds vectors captured by running the original TypeScript implementation
/// (the reference) over:
/// <list type="bullet">
/// <item>every scenario of the upstream <c>packages/tui/test/input.test.ts</c>, replayed step by step
/// (the upstream assertions are captured as <c>asserts</c> entries),</item>
/// <item>a systematic sweep of scripted key sequences: each navigation and deletion binding from
/// several cursor positions and several scripts, kill/yank chains, undo, submission, bracketed paste
/// chunking, control-character rejection and Kitty CSI-u printable decoding,</item>
/// <item>a sweep of <c>render(width)</c> over prompts, placeholders, styles, widths, focus and every
/// grapheme boundary as the cursor position (including the horizontal-scrolling window),</item>
/// <item>a sweep of <c>handleMouse</c> over event types, buttons, coordinates, modifier flags and
/// scroll offsets, and</item>
/// <item>the code points matched by <c>isWhitespaceChar</c> (<c>/\s/</c>) over U+0000–U+3000.</item>
/// </list>
/// The vectors contain no machine-dependent input, so they are location independent.
/// Regenerate with the harness described in <c>docs/tui-porting-status.md</c>.
/// </remarks>
public class InputCorpusTests
{
    // ------------------------------------------------------------------
    // Corpus models
    // ------------------------------------------------------------------

    private sealed record RenderCase(
        int P,
        int Ph,
        int S,
        int F,
        int W,
        int V,
        int C,
        List<string> Lines,
        int Start);

    private sealed record ScriptedOp(string Op, string Value);

    private sealed record StepObs(string Value, int Cursor, int SubmittedCount, int EscapeCount);

    private sealed record Expectation(int After, string Expected, string Kind);

    private sealed record KeyCase(
        string Name,
        List<ScriptedOp> Ops,
        List<StepObs> Tuples,
        List<Expectation>? Asserts,
        List<string> Submitted,
        int EscapeCount,
        int? IcuFrom);

    private sealed record ResultSpec(bool? Handled, bool? Capture, bool? Focus, bool? Render);

    private sealed record MouseCase(
        int V,
        int W,
        int? C,
        int Start,
        string Type,
        string Button,
        int X,
        int Y,
        int? Wd,
        int? Cc,
        int Sh,
        int Al,
        int Ct,
        string Value,
        int Cursor,
        ResultSpec? Result);

    private sealed record WsCase(int SweepEnd, List<int> True);

    private sealed record Corpus(
        List<string> Prompts,
        List<string> Placeholders,
        List<string> Styles,
        List<string> Values,
        List<string> MouseValues,
        List<RenderCase> Render,
        List<KeyCase> Keys,
        List<MouseCase> Mouse,
        WsCase Ws);

    private static Corpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "input-corpus.json");
        using var stream = File.OpenRead(path);
        var corpus = JsonSerializer.Deserialize<Corpus>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(corpus);
        return corpus;
    }

    private static readonly Lazy<Corpus> Data = new(LoadCorpus);

    public InputCorpusTests()
    {
        // The vectors were captured against the default binding table; make sure a previous test has
        // not left a user override behind (the assembly disables parallelization, so the global
        // registry is shared).
        GlobalKeybindings.Set(new KeybindingsManager(TuiKeybindings.Definitions));
    }

    private static Func<string, string> StyleFor(int index) => index switch
    {
        1 => text => $"\x1b[2m{text}\x1b[22m",
        2 => text => $"\x1b[3m{text}\x1b[23m",
        _ => text => text,
    };

    private static Input NewInput(RenderCase entry) => new(new InputOptions
    {
        Prompt = Data.Value.Prompts[entry.P],
        Placeholder = Data.Value.Placeholders[entry.Ph],
        PlaceholderStyle = StyleFor(entry.S),
    });

    // ------------------------------------------------------------------
    // Theory data
    // ------------------------------------------------------------------

    public static TheoryData<int> RenderIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Render.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    public static TheoryData<int> KeyIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Keys.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    public static TheoryData<int> MouseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Mouse.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    // ------------------------------------------------------------------
    // render(width)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RenderIndexes))]
    public void RenderMatchesTheTypeScriptReference(int index)
    {
        var entry = Data.Value.Render[index];
        var input = NewInput(entry);
        input.SetValue(Data.Value.Values[entry.V]);
        input.Cursor = entry.C;
        input.Focused = entry.F != 0;

        var lines = input.Render(entry.W);

        Assert.Equal(entry.Lines, lines);
        Assert.Equal(entry.Start, input.RenderedStartColumn);
    }

    // ------------------------------------------------------------------
    // handleInput(data)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KeyIndexes))]
    public void KeySequencesMatchTheTypeScriptReference(int index)
    {
        var entry = Data.Value.Keys[index];
        Assert.Equal(entry.Ops.Count, entry.Tuples.Count);

        var input = new Input();
        var submitted = new List<string>();
        var escapes = 0;

        void AttachHandlers(Input target)
        {
            target.OnSubmit = value => submitted.Add(value);
            target.OnEscape = () => escapes++;
        }

        AttachHandlers(input);

        var expectations = (entry.Asserts ?? []).ToLookup(expectation => expectation.After);

        for (var i = 0; i < entry.Ops.Count; i++)
        {
            // Steps from here on depend on ICU's dictionary-based word segmentation for CJK
            // (see the class remarks and T22); everything before the divergence is still checked.
            if (entry.IcuFrom is { } divergence && i >= divergence)
            {
                break;
            }

            var op = entry.Ops[i];
            switch (op.Op)
            {
                case "new":
                    // The reference built a fresh instance here.
                    input = new Input();
                    AttachHandlers(input);
                    break;
                case "set":
                    input.SetValue(op.Value);
                    break;
                default:
                    Assert.Equal("in", op.Op);
                    input.HandleInput(op.Value);
                    break;
            }

            var expected = entry.Tuples[i];
            Assert.Equal(expected.Value, input.GetValue());
            Assert.Equal(expected.Cursor, input.Cursor);
            Assert.Equal(expected.SubmittedCount, submitted.Count);
            Assert.Equal(expected.EscapeCount, escapes);

            // Expectations captured from the upstream test suite, checked at the step they were made.
            foreach (var expectation in expectations[i + 1])
            {
                var actual = expectation.Kind == "submitted"
                    ? submitted[^1]
                    : input.GetValue();
                Assert.Equal(expectation.Expected, actual);
            }
        }

        if (entry.IcuFrom is null)
        {
            Assert.Equal(entry.Submitted, submitted);
            Assert.Equal(entry.EscapeCount, escapes);
        }
    }

    // ------------------------------------------------------------------
    // handleMouse(event)
    // ------------------------------------------------------------------

    private static TuiMouseEventType ParseEventType(string value) => value switch
    {
        "press" => TuiMouseEventType.Press,
        "release" => TuiMouseEventType.Release,
        "move" => TuiMouseEventType.Move,
        "drag" => TuiMouseEventType.Drag,
        "click" => TuiMouseEventType.Click,
        "wheel" => TuiMouseEventType.Wheel,
        _ => throw new InvalidOperationException($"Unknown mouse event type '{value}'"),
    };

    private static TuiMouseButton ParseButton(string value) => value switch
    {
        "left" => TuiMouseButton.Left,
        "middle" => TuiMouseButton.Middle,
        "right" => TuiMouseButton.Right,
        "none" => TuiMouseButton.None,
        _ => throw new InvalidOperationException($"Unknown mouse button '{value}'"),
    };

    [Theory]
    [MemberData(nameof(MouseIndexes))]
    public void MouseMatchesTheTypeScriptReference(int index)
    {
        var entry = Data.Value.Mouse[index];

        var input = new Input();
        input.SetValue(Data.Value.MouseValues[entry.V]);
        if (entry.C is { } cursor)
        {
            input.Cursor = cursor;
        }
        input.Focused = true;

        // The reference rendered first so that renderedStartColumn was populated.
        input.Render(entry.W);
        Assert.Equal(entry.Start, input.RenderedStartColumn);

        var result = input.HandleMouse(new TuiMouseEvent
        {
            Type = ParseEventType(entry.Type),
            Button = ParseButton(entry.Button),
            X = entry.X,
            Y = entry.Y,
            ScreenX = entry.X,
            ScreenY = entry.Y,
            Width = entry.W,
            Height = 1,
            Shift = entry.Sh != 0,
            Alt = entry.Al != 0,
            Ctrl = entry.Ct != 0,
            WheelDelta = entry.Wd,
            ClickCount = entry.Cc,
        });

        Assert.Equal(entry.Value, input.GetValue());
        Assert.Equal(entry.Cursor, input.Cursor);

        if (entry.Result is null)
        {
            Assert.Null(result);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(entry.Result.Handled, result.Handled);
        Assert.Equal(entry.Result.Capture, result.Capture);
        Assert.Equal(entry.Result.Focus, result.Focus);
        Assert.Equal(entry.Result.Render, result.Render);
    }

    // ------------------------------------------------------------------
    // isWhitespaceChar  (JS /\s/)
    // ------------------------------------------------------------------

    [Fact]
    public void WhitespaceCharMatchesTheJavaScriptReference()
    {
        var ws = Data.Value.Ws;
        var expected = ws.True.ToHashSet();
        var mismatches = new List<string>();

        for (var codePoint = 0; codePoint < ws.SweepEnd; codePoint++)
        {
            var text = char.ConvertFromUtf32(codePoint);
            var actual = TextLayout.IsWhitespaceChar(text);
            if (actual != expected.Contains(codePoint))
            {
                mismatches.Add($"U+{codePoint:X4} expected {expected.Contains(codePoint)} got {actual}");
            }
        }

        Assert.Empty(mismatches);
        Assert.NotEmpty(expected);

        // The JS whitespace set differs from .NET's char.IsWhiteSpace in both directions.
        Assert.True(TextLayout.IsWhitespaceChar("\ufeff"));
        Assert.False(TextLayout.IsWhitespaceChar("\u0085"));
        // Unanchored /\s/.test: a multi-character argument only needs one match.
        Assert.True(TextLayout.IsWhitespaceChar(" a"));
        Assert.False(TextLayout.IsWhitespaceChar("ab"));
        Assert.False(TextLayout.IsWhitespaceChar(""));
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsFullyCovered()
    {
        Assert.True(Data.Value.Render.Count >= 3300, $"render has only {Data.Value.Render.Count} cases");
        Assert.True(Data.Value.Keys.Count >= 470, $"keys has only {Data.Value.Keys.Count} cases");
        Assert.True(Data.Value.Mouse.Count >= 1600, $"mouse has only {Data.Value.Mouse.Count} cases");
        Assert.True(Data.Value.Ws.True.Count >= 25, $"ws has only {Data.Value.Ws.True.Count} entries");
        // Vectors whose expectations depend on ICU's dictionary word segmentation. This must not grow
        // silently: every additional entry is coverage that the port cannot verify.
        Assert.Equal(7, Data.Value.Keys.Count(entry => entry.IcuFrom is not null));
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        // The horizontal-scrolling window must actually be exercised.
        Assert.Contains(Data.Value.Render, entry => entry.Start > 0);
        // ... and the prompt-wider-than-viewport early return.
        Assert.Contains(Data.Value.Render, entry => entry.W < UnicodeWidth.VisibleWidth(Data.Value.Prompts[entry.P]));
        // Both focus states, a styled placeholder and a wide-character value.
        Assert.Contains(Data.Value.Render, entry => entry.F != 0);
        Assert.Contains(Data.Value.Render, entry => entry.F == 0);
        Assert.Contains(Data.Value.Render, entry => entry.Ph > 0 && entry.S > 0);
        Assert.Contains(Data.Value.Render, entry => Data.Value.Values[entry.V].Length == 0);

        // Every scripted sequence must be replayable and at least one must submit / escape.
        Assert.All(Data.Value.Keys, entry => Assert.Equal(entry.Ops.Count, entry.Tuples.Count));
        Assert.Contains(Data.Value.Keys, entry => entry.Submitted.Count > 0);
        Assert.Contains(Data.Value.Keys, entry => entry.EscapeCount > 0);
        Assert.Contains(Data.Value.Keys, entry => entry.Ops.Exists(op => op.Op == "set"));
        // The upstream suite's own assertions survived the capture.
        Assert.True(
            Data.Value.Keys.Sum(entry => entry.Asserts?.Count ?? 0) >= 80,
            "upstream-derived assertions shrank");
        Assert.Contains(Data.Value.Keys, entry => entry.Asserts?.Exists(a => a.Kind == "submitted") == true);

        // Mouse: every non-left/non-press/non-zero-row combination is a no-op, and presses land.
        Assert.Contains(Data.Value.Mouse, entry => entry.Result is null);
        Assert.Contains(Data.Value.Mouse, entry => entry.Result is not null);
        Assert.Contains(Data.Value.Mouse, entry => entry.Result?.Focus == true);
        Assert.Contains(Data.Value.Mouse, entry => entry.Y != 0);
        Assert.Contains(Data.Value.Mouse, entry => entry.C != null);
        Assert.Contains(Data.Value.Mouse, entry => entry.Start > 0);
        foreach (var type in new[] { "press", "release", "move", "drag", "click", "wheel" })
        {
            Assert.Contains(Data.Value.Mouse, entry => entry.Type == type);
        }
        foreach (var button in new[] { "left", "middle", "right", "none" })
        {
            Assert.Contains(Data.Value.Mouse, entry => entry.Button == button);
        }
    }
}
