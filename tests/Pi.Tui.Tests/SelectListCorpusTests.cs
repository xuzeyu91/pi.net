using System.Text.Json;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>components/select-list.ts</c> port.
/// </summary>
/// <remarks>
/// <c>select-list-corpus.json</c> holds vectors captured by running the original TypeScript
/// implementation (the reference) over:
/// <list type="bullet">
/// <item>a matrix of item sets × themes × column layouts × widths for <c>render(width)</c>, plus a
/// sweep of selection positions and scroll windows (including <c>maxVisible</c> of 0 and out-of-range
/// selection indexes), a sweep of filters (including filters that match nothing), and the width
/// boundary around the <c>width &gt; 40</c> description branch,</item>
/// <item>the five scenarios of the upstream <c>packages/tui/test/select-list.test.ts</c>, so the
/// alignment assertions it makes can be re-derived,</item>
/// <item>scripted key sequences over the default binding table (navigation wrap-around, confirm,
/// cancel, unbound keys, and Kitty CSI-u arrow forms),</item>
/// <item>a sweep of <c>handleMouse</c> over event types, buttons, per-row coordinates, wheel deltas
/// and press→click pairs, and</item>
/// <item>every code point whose <c>toLowerCase</c> result differs from itself, plus 7,621 context
/// strings that pin the Final_Sigma rule, plus the <c>Cased</c> and <c>Case_Ignorable</c> code point
/// sets that rule depends on.</item>
/// </list>
/// The vectors contain no machine-dependent input, so they are location independent.
/// Regenerate with the harness described in <c>docs/tui-porting-status.md</c>.
/// </remarks>
public class SelectListCorpusTests
{
    // ------------------------------------------------------------------
    // Corpus models
    // ------------------------------------------------------------------

    private sealed record ItemSpec(string Value, string Label, string? Description);

    private sealed record ItemSet(string Id, List<ItemSpec> Items);

    private sealed record RenderCase(
        int Set,
        int Theme,
        int Layout,
        int Mv,
        string F,
        int Sel,
        int W,
        List<string> Lines,
        string? Item);

    private sealed record UpstreamCase(int I, int W, List<string> Lines);

    private sealed record KeyTuple(int Sel, string? Item, List<string> NewEvents);

    private sealed record KeyCase(
        int Set,
        int Mv,
        string F,
        int Start,
        List<string> Ops,
        List<KeyTuple> Tuples);

    private sealed record MouseEventSpec(
        string Type,
        string Button,
        int X,
        int Y,
        int ScreenX,
        int ScreenY,
        int Width,
        int Height,
        bool Shift,
        bool Alt,
        bool Ctrl,
        int? WheelDelta,
        int? ClickCount);

    private sealed record ResultSpec(bool? Handled, bool? Capture, bool? Focus, bool? Render);

    private sealed record MouseCase(
        int Set,
        int Mv,
        string F,
        int Start,
        MouseEventSpec? Press,
        ResultSpec? PressResult,
        List<string> PressEvents,
        MouseEventSpec Event,
        ResultSpec? Result,
        int Sel,
        string? Item,
        List<string> NewEvents);

    private sealed record CaseContext(string Input, string Lower);

    private sealed record CaseCase(
        List<JsonElement> Map,
        List<CaseContext> Contexts,
        List<List<int>> CasedRanges,
        List<List<int>> CaseIgnorableRanges);

    private sealed record Corpus(
        List<string> Themes,
        List<string> Layouts,
        List<ItemSet> ItemSets,
        List<RenderCase> Render,
        List<UpstreamCase> Upstream,
        List<KeyCase> Keys,
        List<MouseCase> Mouse,
        CaseCase Case);

    private static Corpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "select-list-corpus.json");
        using var stream = File.OpenRead(path);
        var corpus = JsonSerializer.Deserialize<Corpus>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(corpus);
        return corpus;
    }

    private static readonly Lazy<Corpus> Data = new(LoadCorpus);

    public SelectListCorpusTests()
    {
        // The vectors were captured against the default binding table; make sure a previous test has
        // not left a user override behind (the assembly disables parallelization, so the global
        // registry is shared).
        GlobalKeybindings.Set(new KeybindingsManager(TuiKeybindings.Definitions));
    }

    // ------------------------------------------------------------------
    // Fixture construction
    // ------------------------------------------------------------------

    private static SelectListTheme ThemeFor(int index) => index switch
    {
        1 => new SelectListTheme
        {
            SelectedPrefix = text => $"[P]{text}[/P]",
            SelectedText = text => $"[S]{text}[/S]",
            Description = text => $"[D]{text}[/D]",
            ScrollInfo = text => $"[I]{text}[/I]",
            NoMatch = text => $"[N]{text}[/N]",
        },
        2 => new SelectListTheme
        {
            SelectedPrefix = text => $"\x1b[1m{text}\x1b[22m",
            SelectedText = text => $"\x1b[7m{text}\x1b[27m",
            Description = text => $"\x1b[2m{text}\x1b[22m",
            ScrollInfo = text => $"\x1b[3m{text}\x1b[23m",
            NoMatch = text => $"\x1b[31m{text}\x1b[39m",
        },
        _ => SelectListTheme.Plain,
    };

    private static string EllipsisTruncate(SelectListTruncatePrimaryContext context) =>
        context.Text.Length <= context.MaxWidth
            ? context.Text
            : JsString.Slice(context.Text, 0, Math.Max(0, context.MaxWidth - 1)) + "…";

    private static string UpperTruncate(SelectListTruncatePrimaryContext context) =>
        $"{(context.IsSelected ? "S" : "u")}{context.ColumnWidth}/{context.MaxWidth}:" +
        JsString.Slice(context.Text, 0, Math.Max(0, context.MaxWidth - 3));

    private static SelectListLayoutOptions LayoutFor(int index) => index switch
    {
        1 => new() { MinPrimaryColumnWidth = 8, MaxPrimaryColumnWidth = 40 },
        2 => new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 20 },
        3 => new() { MinPrimaryColumnWidth = 12, MaxPrimaryColumnWidth = 12 },
        4 => new() { MaxPrimaryColumnWidth = 20 },
        5 => new() { MinPrimaryColumnWidth = 20 },
        6 => new() { MinPrimaryColumnWidth = 5, MaxPrimaryColumnWidth = 3 },
        7 => new()
        {
            MinPrimaryColumnWidth = 12,
            MaxPrimaryColumnWidth = 12,
            TruncatePrimary = EllipsisTruncate,
        },
        8 => new()
        {
            MinPrimaryColumnWidth = 6,
            MaxPrimaryColumnWidth = 6,
            TruncatePrimary = UpperTruncate,
        },
        _ => new SelectListLayoutOptions(),
    };

    private static SelectList NewList(int setIndex, int themeIndex, int layoutIndex, int maxVisible) =>
        new(
            Data.Value.ItemSets[setIndex].Items.Select(item => new SelectItem
            {
                Value = item.Value,
                Label = item.Label,
                Description = item.Description,
            }),
            maxVisible,
            ThemeFor(themeIndex),
            LayoutFor(layoutIndex));

    private static void AttachHandlers(SelectList list, List<string> events)
    {
        list.OnSelect = item => events.Add($"select:{item.Value}");
        list.OnCancel = () => events.Add("cancel");
        list.OnSelectionChange = item => events.Add($"change:{item.Value}");
    }

    private static TuiMouseEvent ToMouseEvent(MouseEventSpec spec) => new()
    {
        Type = spec.Type switch
        {
            "press" => TuiMouseEventType.Press,
            "release" => TuiMouseEventType.Release,
            "move" => TuiMouseEventType.Move,
            "drag" => TuiMouseEventType.Drag,
            "click" => TuiMouseEventType.Click,
            "wheel" => TuiMouseEventType.Wheel,
            _ => throw new InvalidOperationException($"unknown mouse event type {spec.Type}"),
        },
        Button = spec.Button switch
        {
            "left" => TuiMouseButton.Left,
            "middle" => TuiMouseButton.Middle,
            "right" => TuiMouseButton.Right,
            "none" => TuiMouseButton.None,
            _ => throw new InvalidOperationException($"unknown mouse button {spec.Button}"),
        },
        X = spec.X,
        Y = spec.Y,
        ScreenX = spec.ScreenX,
        ScreenY = spec.ScreenY,
        Width = spec.Width,
        Height = spec.Height,
        Shift = spec.Shift,
        Alt = spec.Alt,
        Ctrl = spec.Ctrl,
        WheelDelta = spec.WheelDelta,
        ClickCount = spec.ClickCount,
    };

    private static void AssertResult(ResultSpec? expected, TuiMouseEventResult? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Handled, actual.Handled);
        Assert.Equal(expected.Capture, actual.Capture);
        Assert.Equal(expected.Focus, actual.Focus);
        Assert.Equal(expected.Render, actual.Render);
    }

    private static int VisibleIndexOf(string line, string text)
    {
        var index = line.IndexOf(text, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{text}' not found in {line}");
        return UnicodeWidth.VisibleWidth(line[..index]);
    }

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

    public static TheoryData<int> UpstreamIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Upstream.Count; i++)
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
        var list = NewList(entry.Set, entry.Theme, entry.Layout, entry.Mv);
        list.SetFilter(entry.F);
        list.SetSelectedIndex(entry.Sel);

        var lines = list.Render(entry.W);

        Assert.Equal(entry.Lines, lines);
        Assert.Equal(entry.Item, list.GetSelectedItem()?.Value);
    }

    // ------------------------------------------------------------------
    // upstream select-list.test.ts scenarios
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(UpstreamIndexes))]
    public void UpstreamScenariosMatchTheTypeScriptReference(int index)
    {
        var entry = Data.Value.Upstream[index];

        // The upstream scenarios use an identity theme and the default layout except where noted;
        // they are reconstructed here through the same item-set machinery as the sweeps.
        var scenarios = new (int Set, int Layout, int Width, int MaxVisible)[]
        {
            (13, 0, 100, 5),
            (4, 0, 80, 5),
            (14, 2, 80, 5),
            (15, 2, 80, 5),
            (15, 7, 80, 5),
        };

        var scenario = scenarios[index];
        var list = NewList(scenario.Set, 0, scenario.Layout, scenario.MaxVisible);
        var lines = list.Render(scenario.Width);

        Assert.Equal(entry.Lines, lines);
        Assert.Equal(entry.W, scenario.Width);

        switch (index)
        {
            case 0:
                // "normalizes multiline descriptions to single line"
                Assert.True(lines.Length > 0);
                Assert.DoesNotContain('\n', lines[0]);
                Assert.Contains("Line one Line two Line three", lines[0], StringComparison.Ordinal);
                break;
            case 1:
                // "keeps descriptions aligned when the primary text is truncated"
                Assert.Equal(
                    VisibleIndexOf(lines[0], "short description"),
                    VisibleIndexOf(lines[1], "long description"));
                break;
            case 2:
                // "uses the configured minimum primary column width"
                Assert.Equal(14, lines[0].IndexOf("first", StringComparison.Ordinal));
                Assert.Equal(14, lines[1].IndexOf("second", StringComparison.Ordinal));
                break;
            case 3:
                // "uses the configured maximum primary column width"
                Assert.Equal(22, VisibleIndexOf(lines[0], "first"));
                Assert.Equal(22, VisibleIndexOf(lines[1], "second"));
                break;
            default:
                // "allows overriding primary truncation while preserving description alignment"
                Assert.Contains('…', lines[0]);
                Assert.Equal(
                    VisibleIndexOf(lines[0], "first"),
                    VisibleIndexOf(lines[1], "second"));
                break;
        }
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

        var list = NewList(entry.Set, 0, 0, entry.Mv);
        list.SetFilter(entry.F);
        list.SetSelectedIndex(entry.Start);

        var events = new List<string>();
        AttachHandlers(list, events);

        for (var i = 0; i < entry.Ops.Count; i++)
        {
            list.HandleInput(entry.Ops[i]);

            var expected = entry.Tuples[i];
            Assert.Equal(expected.Sel, list.SelectedIndex);
            Assert.Equal(expected.Item, list.GetSelectedItem()?.Value);
            Assert.Equal(expected.NewEvents, events);
            events.Clear();
        }
    }

    // ------------------------------------------------------------------
    // handleMouse(event)
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MouseIndexes))]
    public void MouseMatchesTheTypeScriptReference(int index)
    {
        var entry = Data.Value.Mouse[index];
        var list = NewList(entry.Set, 0, 0, entry.Mv);
        list.SetFilter(entry.F);
        list.SetSelectedIndex(entry.Start);

        var events = new List<string>();
        AttachHandlers(list, events);

        if (entry.Press is not null)
        {
            AssertResult(entry.PressResult, list.HandleMouse(ToMouseEvent(entry.Press)));
            Assert.Equal(entry.PressEvents, events);
            events.Clear();
        }

        AssertResult(entry.Result, list.HandleMouse(ToMouseEvent(entry.Event)));
        Assert.Equal(entry.Sel, list.SelectedIndex);
        Assert.Equal(entry.Item, list.GetSelectedItem()?.Value);
        Assert.Equal(entry.NewEvents, events);
    }

    // ------------------------------------------------------------------
    // JsString.ToLowerCase / the Final_Sigma predicates
    // ------------------------------------------------------------------

    [Fact]
    public void ToLowerCaseMatchesTheJavaScriptReference()
    {
        var mismatches = new List<string>();
        foreach (var entry in Data.Value.Case.Map)
        {
            var codePoint = entry[0].GetInt32();
            var expected = entry[1].GetString()!;
            var input = char.ConvertFromUtf32(codePoint);
            var actual = JsString.ToLowerCase(input);
            if (actual != expected)
            {
                mismatches.Add($"U+{codePoint:X4} -> {CodePoints(actual)} (expected {CodePoints(expected)})");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void ToLowerCaseContextsMatchTheJavaScriptReference()
    {
        var mismatches = new List<string>();
        foreach (var entry in Data.Value.Case.Contexts)
        {
            var actual = JsString.ToLowerCase(entry.Input);
            if (actual != entry.Lower)
            {
                mismatches.Add($"{CodePoints(entry.Input)} -> {CodePoints(actual)} (expected {CodePoints(entry.Lower)})");
            }
        }

        Assert.Empty(mismatches);
    }

    /// <summary>Renders a string as space-separated <c>U+XXXX</c> code points for failure messages.</summary>
    private static string CodePoints(string value)
    {
        var parts = new List<string>();
        foreach (var rune in value.EnumerateRunes())
        {
            parts.Add($"U+{rune.Value:X4}");
        }
        return string.Join(' ', parts);
    }

    [Fact]
    public void CasePredicatesMatchTheJavaScriptReference()
    {
        var cased = Expand(Data.Value.Case.CasedRanges);
        var ignorable = Expand(Data.Value.Case.CaseIgnorableRanges);

        var mismatches = new List<string>();
        for (var codePoint = 0; codePoint <= 0x10ffff; codePoint++)
        {
            if (codePoint is >= 0xd800 and <= 0xdfff)
            {
                continue;
            }

            var expectedCased = cased.Contains(codePoint);
            if (JsString.IsCased(codePoint) != expectedCased)
            {
                mismatches.Add($"Cased U+{codePoint:X4}");
            }

            var expectedIgnorable = ignorable.Contains(codePoint);
            if (JsString.IsCaseIgnorable(codePoint) != expectedIgnorable)
            {
                mismatches.Add($"Case_Ignorable U+{codePoint:X4}");
            }
        }

        Assert.Empty(mismatches);
    }

    private static HashSet<int> Expand(List<List<int>> ranges)
    {
        var result = new HashSet<int>();
        foreach (var range in ranges)
        {
            for (var codePoint = range[0]; codePoint <= range[1]; codePoint++)
            {
                result.Add(codePoint);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsFullyCovered()
    {
        Assert.Equal(3, Data.Value.Themes.Count);
        Assert.Equal(9, Data.Value.Layouts.Count);
        Assert.Equal(16, Data.Value.ItemSets.Count);
        Assert.Equal(1492, Data.Value.Render.Count);
        Assert.Equal(5, Data.Value.Upstream.Count);
        Assert.Equal(570, Data.Value.Keys.Count);
        Assert.Equal(656, Data.Value.Mouse.Count);
        Assert.Equal(1488, Data.Value.Case.Map.Count);
        Assert.Equal(7621, Data.Value.Case.Contexts.Count);
        Assert.Equal(131, Data.Value.Case.CasedRanges.Count);
        Assert.Equal(464, Data.Value.Case.CaseIgnorableRanges.Count);
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var lines = Data.Value.Render.SelectMany(entry => entry.Lines).ToList();

        // The no-match branch, the scroll indicator, and all four styling callbacks that the
        // reference actually invokes.
        Assert.True(Data.Value.Render.Count(entry => entry.Lines.Count == 1) > 100);
        Assert.True(lines.Count(line => line.Contains("No matching", StringComparison.Ordinal)) > 50);
        Assert.True(lines.Count(line => line.Contains('(', StringComparison.Ordinal)
            && line.Contains('/', StringComparison.Ordinal)) > 40);
        Assert.True(lines.Count(line => line.Contains("[S]", StringComparison.Ordinal)) > 100);
        Assert.True(lines.Count(line => line.Contains("[D]", StringComparison.Ordinal)) > 50);
        Assert.True(lines.Count(line => line.Contains("[I]", StringComparison.Ordinal)) > 20);
        Assert.True(lines.Count(line => line.Contains("[N]", StringComparison.Ordinal)) > 50);

        // theme.selectedPrefix is never called: the reference builds the row prefix from the literal
        // "→ " instead. The corpus records that faithfully, so this guard is deliberately empty.
        Assert.DoesNotContain(lines, line => line.Contains("[P]", StringComparison.Ordinal));

        // Descriptions are normalized, so no rendered line may contain a raw line break.
        Assert.DoesNotContain('\n', string.Concat(lines));
        Assert.DoesNotContain('\r', string.Concat(lines));

        // Every key binding the reference honours, plus a cancel, is exercised.
        var keyEvents = Data.Value.Keys.SelectMany(entry => entry.Tuples).SelectMany(tuple => tuple.NewEvents).ToList();
        Assert.True(keyEvents.Count(evt => evt.StartsWith("change:", StringComparison.Ordinal)) > 100);
        Assert.True(keyEvents.Count(evt => evt.StartsWith("select:", StringComparison.Ordinal)) > 20);
        Assert.True(keyEvents.Count(evt => evt == "cancel") > 20);

        // Navigation actually moves the selection (the corpus would be worthless otherwise).
        Assert.True(Data.Value.Keys.Count(entry => entry.Tuples.Any(tuple => tuple.Sel != entry.Start)) > 100);

        // Mouse: press, click and wheel all take effect, in both wheel directions.
        Assert.Equal(180, Data.Value.Mouse.Count(entry => entry.Event.Type == "press"));
        Assert.Equal(359, Data.Value.Mouse.Count(entry => entry.Event.Type == "click"));
        Assert.True(Data.Value.Mouse.Count(entry => entry.Event.Type == "wheel") > 50);
        Assert.Contains(Data.Value.Mouse, entry => entry.Event.WheelDelta < 0);
        Assert.Contains(Data.Value.Mouse, entry => entry.Event.WheelDelta > 0);
        Assert.True(Data.Value.Mouse.Count(entry => entry.Sel != entry.Start) > 50);
        Assert.True(Data.Value.Mouse.Count(entry => entry.Result?.Handled == true) > 100);

        // The case-mapping sweep must actually contain the two structural special cases.
        Assert.Contains(Data.Value.Case.Map, entry => entry[0].GetInt32() == 0x0130 && entry[1].GetString()!.Length == 2);
        Assert.Contains(Data.Value.Case.Contexts, entry => entry.Input == "ΑΣ" && entry.Lower == "ας");
        Assert.Contains(Data.Value.Case.Contexts, entry => entry.Input == "ΣΑ" && entry.Lower == "σα");
    }
}
