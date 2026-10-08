using System.Text.Json;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Test overlay that records the input it receives (mirrors the TS test harness).</summary>
internal sealed class InputOverlay : IComponent, IFocusable
{
    public bool Focused { get; set; }

    public List<string> Inputs { get; } = [];

    public void HandleInput(string data) => Inputs.Add(data);

    public string[] Render(int width) => ["overlay"];

    public void Invalidate()
    {
    }
}

/// <summary>One observation of the renderer state after a scenario step.</summary>
internal sealed record AltScreenObservation(string[] Screen, int ViewportTop, bool Following, bool HasSelection, bool HasOverlay);

/// <summary>Everything a corpus scenario observes.</summary>
internal sealed class AltScreenReplayResult
{
    public required List<AltScreenObservation> Observations { get; init; }
    public string? StartWrites { get; init; }
    public string? StopWrites { get; init; }
    public required List<string> Clipboard { get; init; }
    public required List<string> OpenedUrls { get; init; }
    public required Dictionary<string, List<string>> OverlayInputs { get; init; }
}

/// <summary>
/// Differential tests for the <c>tui-alt-screen.ts</c> port.
/// </summary>
/// <remarks>
/// <para>
/// <c>alt-screen-corpus.json</c> holds scenario vectors captured by running the original TypeScript
/// <c>TuiAltScreen</c> (the reference) against a fake terminal: for each vector the reference was
/// started, driven through a scripted sequence of raw input bytes, resize, flash and overlay
/// operations, and after every step the rendered frame (<c>getScreenLines</c>), <c>viewportTop</c>,
/// <c>isFollowingOutput</c>, <c>hasActiveSelection</c> and <c>hasOverlay</c> were recorded. The
/// start/stop terminal escape sequences, the clipboard payloads, the opened URLs and the input each
/// overlay received are captured as well.
/// </para>
/// <para>
/// The vectors contain no machine-dependent input: capabilities are pinned to
/// <c>{ images: null, trueColor: true, hyperlinks: true }</c>, the wheel accelerator runs with a fixed
/// line count (never <c>"auto"</c>), and no vector depends on timer expiry. They are therefore
/// location independent.
/// </para>
/// </remarks>
public class AltScreenCorpusTests
{
    private static readonly TerminalCapabilities CorpusCapabilities =
        new(ImageProtocol.None, TrueColor: true, Hyperlinks: true);

    private static List<JsonElement> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "alt-screen-corpus.json");
        using var stream = File.OpenRead(path);
        var vectors = JsonSerializer.Deserialize<List<JsonElement>>(stream);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static readonly Lazy<List<JsonElement>> Data = new(LoadCorpus);

    public static TheoryData<int> Indexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static string Id(JsonElement vector) => vector.GetProperty("id").GetString() ?? "";

    // ------------------------------------------------------------------
    // Setup helpers
    // ------------------------------------------------------------------

    private static Text MakeText(JsonElement spec) => new(
        spec.GetProperty("text").GetString() ?? "",
        spec.TryGetProperty("paddingX", out var px) ? px.GetInt32() : 0,
        spec.TryGetProperty("paddingY", out var py) ? py.GetInt32() : 0);

    private static StackChild MakeRootEntry(JsonElement spec)
    {
        IComponent component = MakeText(spec);
        if (spec.TryGetProperty("scrollView", out var scrollView) && scrollView.GetBoolean())
        {
            var follow = spec.TryGetProperty("follow", out var followValue) ? followValue.GetString() : "end";
            component = new ScrollView(component, new ScrollViewOptions
            {
                Follow = follow == "none" ? null : follow,
                Primary = !spec.TryGetProperty("primary", out var primary) || primary.GetBoolean(),
            });
        }

        var options = new StackEntryOptions();
        if (spec.TryGetProperty("basis", out var basis))
        {
            options.Basis = basis.ValueKind == JsonValueKind.String ? null : basis.GetInt32();
        }
        if (spec.TryGetProperty("grow", out var grow))
        {
            options.Grow = grow.GetInt32();
        }
        if (spec.TryGetProperty("minSize", out var minSize))
        {
            options.MinSize = minSize.GetInt32();
        }

        return StackChild.Of(component, options);
    }

    private static void ApplySetup(TuiAltScreen tui, JsonElement setup)
    {
        if (setup.TryGetProperty("root", out var root))
        {
            tui.SetLayoutRoot(new VStack(root.EnumerateArray().Select(MakeRootEntry)));
            return;
        }

        if (setup.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                tui.AddChild(MakeText(child));
            }
        }
    }

    private static KeybindingsConfig? MakeUserBindings(JsonElement vector)
    {
        if (vector.GetProperty("keybindings").ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var config = new KeybindingsConfig();
        foreach (var property in vector.GetProperty("keybindings").EnumerateObject())
        {
            config[property.Name] = property.Value.GetString() ?? "";
        }

        return config;
    }

    // ------------------------------------------------------------------
    // Scenario replay
    // ------------------------------------------------------------------

    /// <summary>Replay one corpus scenario against the C# implementation.</summary>
    internal static AltScreenReplayResult Replay(JsonElement vector)
    {
        var optionsSpec = vector.GetProperty("options");
        var clipboard = new List<string>();
        var openedUrls = new List<string>();

        Func<string, Task<ClipboardCopyResult>>? copySelection = optionsSpec.GetProperty("copySelection").GetString() switch
        {
            "ok" => text =>
            {
                clipboard.Add(text);
                return Task.FromResult(ClipboardCopyResult.Ok);
            },
            "message" => text =>
            {
                clipboard.Add(text);
                return Task.FromResult(ClipboardCopyResult.Fail("clipboard offline"));
            },
            "false" => text =>
            {
                clipboard.Add(text);
                return Task.FromResult(ClipboardCopyResult.Fail());
            },
            _ => null,
        };

        var searchStyles = optionsSpec.GetProperty("searchStyles").GetBoolean();
        var options = new TuiAltScreenOptions
        {
            Mouse = optionsSpec.GetProperty("mouse").GetBoolean(),
            WheelScrollLines = (WheelScrollLines)optionsSpec.GetProperty("wheelScrollLines").GetDouble(),
            CopyOnSelect = optionsSpec.GetProperty("copyOnSelect").GetBoolean(),
            CopySelection = copySelection,
            ScrollToEndIndicator = optionsSpec.GetProperty("scrollToEndIndicator").GetBoolean()
                ? () => "\x1b[7m ↓ Jump to end \x1b[27m"
                : null,
            SearchMatchStyle = searchStyles ? text => $"<{text}>" : null,
            SearchCurrentMatchStyle = searchStyles ? text => $"[{text}]" : null,
            OpenUrl = optionsSpec.GetProperty("openUrl").GetBoolean() ? url => openedUrls.Add(url) : null,
            OnRightClickPaste = optionsSpec.GetProperty("onRightClickPaste").GetBoolean()
                ? () => openedUrls.Add("paste")
                : null,
        };

        var terminal = new StringTerminal(vector.GetProperty("columns").GetInt32(), vector.GetProperty("rows").GetInt32());
        var originalCapabilities = TerminalImage.GetCapabilities();
        var originalKeybindings = GlobalKeybindings.Get();
        var userBindings = MakeUserBindings(vector);

        string? startWrites = null;
        string? stopWrites = null;
        var overlays = new Dictionary<string, (InputOverlay Component, IOverlayHandle Handle)>();
        var observations = new List<AltScreenObservation>();

        try
        {
            TerminalImage.SetCapabilities(CorpusCapabilities);
            if (userBindings is not null)
            {
                GlobalKeybindings.Set(new KeybindingsManager(TuiKeybindings.Definitions, userBindings));
            }

            var tui = new TuiAltScreen(terminal, null, options);
            ApplySetup(tui, vector.GetProperty("setup"));

            foreach (var step in vector.GetProperty("steps").EnumerateArray())
            {
                switch (step.GetProperty("op").GetString())
                {
                    case "start":
                        terminal.ClearOutput();
                        tui.Start();
                        startWrites = terminal.Output;
                        break;
                    case "stop":
                        terminal.ClearOutput();
                        tui.Stop(new TuiStopOptions
                        {
                            PreserveScreen = step.TryGetProperty("preserveScreen", out var preserve) && preserve.GetBoolean(),
                        });
                        stopWrites = terminal.Output;
                        break;
                    case "input":
                        terminal.SendInput(step.GetProperty("data").GetString() ?? "");
                        break;
                    case "resize":
                        terminal.Resize(step.GetProperty("columns").GetInt32(), step.GetProperty("rows").GetInt32());
                        break;
                    case "flash":
                        tui.Flash(
                            step.GetProperty("message").GetString() ?? "",
                            step.TryGetProperty("durationMs", out var duration) ? duration.GetInt32() : null);
                        break;
                    case "setCopyOnSelect":
                        tui.SetCopyOnSelect(step.GetProperty("enabled").GetBoolean());
                        break;
                    case "copyActive":
                        tui.CopyActiveSelectionToClipboardAsync().GetAwaiter().GetResult();
                        break;
                    case "showOverlay":
                    {
                        var overlay = new InputOverlay();
                        var overlayOptions = step.TryGetProperty("options", out var stepOptions)
                            ? new OverlayOptions
                            {
                                NonCapturing = stepOptions.TryGetProperty("nonCapturing", out var nc) && nc.GetBoolean(),
                            }
                            : null;
                        var handle = tui.ShowOverlay(overlay, overlayOptions);
                        overlays[step.GetProperty("name").GetString() ?? ""] = (overlay, handle);
                        break;
                    }
                    case "hideOverlay":
                        overlays[step.GetProperty("name").GetString() ?? ""].Handle.Hide();
                        break;
                    case "setLayoutRoot":
                        tui.SetLayoutRoot(step.GetProperty("value").ValueKind == JsonValueKind.Null
                            ? null
                            : new VStack(step.GetProperty("entries").EnumerateArray().Select(MakeRootEntry)));
                        break;
                    default:
                        throw new InvalidOperationException($"unknown op {step.GetProperty("op").GetString()}");
                }

                tui.RenderNow();
                observations.Add(new AltScreenObservation(
                    tui.GetScreenLines(),
                    tui.ViewportTop,
                    tui.IsFollowingOutput,
                    tui.HasActiveSelection(),
                    tui.HasOverlay()));
            }

            // The reference stops with preserveScreen so the flash timers are disposed.
            tui.Stop(new TuiStopOptions { PreserveScreen = true });
        }
        finally
        {
            TerminalImage.SetCapabilities(originalCapabilities);
            GlobalKeybindings.Set(originalKeybindings);
        }

        return new AltScreenReplayResult
        {
            Observations = observations,
            StartWrites = startWrites,
            StopWrites = stopWrites,
            Clipboard = clipboard,
            OpenedUrls = openedUrls,
            OverlayInputs = overlays.ToDictionary(pair => pair.Key, pair => pair.Value.Component.Inputs),
        };
    }

    /// <summary>Compare a replay against the reference vector; returns a compact description of the first difference.</summary>
    internal static string? Compare(JsonElement vector, AltScreenReplayResult actual)
    {
        var expectedObservations = vector.GetProperty("observations").EnumerateArray().ToList();
        if (expectedObservations.Count != actual.Observations.Count)
        {
            return $"step count {expectedObservations.Count} != {actual.Observations.Count}";
        }

        for (var step = 0; step < expectedObservations.Count; step++)
        {
            var expected = expectedObservations[step];
            var got = actual.Observations[step];
            var expectedScreen = expected.GetProperty("screen").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            if (!expectedScreen.SequenceEqual(got.Screen, StringComparer.Ordinal))
            {
                var limit = Math.Min(expectedScreen.Count, got.Screen.Length);
                for (var row = 0; row < limit; row++)
                {
                    if (!string.Equals(expectedScreen[row], got.Screen[row], StringComparison.Ordinal))
                    {
                        return $"step {step} screen row {row}: expected {Quote(expectedScreen[row])} got {Quote(got.Screen[row])}";
                    }
                }
                return $"step {step} screen length {expectedScreen.Count} != {got.Screen.Length}";
            }

            if (expected.GetProperty("viewportTop").GetInt32() != got.ViewportTop)
            {
                return $"step {step} viewportTop expected {expected.GetProperty("viewportTop").GetInt32()} got {got.ViewportTop}";
            }
            if (expected.GetProperty("isFollowingOutput").GetBoolean() != got.Following)
            {
                return $"step {step} isFollowingOutput expected {expected.GetProperty("isFollowingOutput").GetBoolean()} got {got.Following}";
            }
            if (expected.GetProperty("hasActiveSelection").GetBoolean() != got.HasSelection)
            {
                return $"step {step} hasActiveSelection expected {expected.GetProperty("hasActiveSelection").GetBoolean()} got {got.HasSelection}";
            }
            if (expected.GetProperty("hasOverlay").GetBoolean() != got.HasOverlay)
            {
                return $"step {step} hasOverlay expected {expected.GetProperty("hasOverlay").GetBoolean()} got {got.HasOverlay}";
            }
        }

        var expectedStart = vector.GetProperty("startWrites");
        var start = expectedStart.ValueKind == JsonValueKind.Null ? null : expectedStart.GetString();
        if (!string.Equals(start, actual.StartWrites, StringComparison.Ordinal))
        {
            return $"startWrites expected {Quote(start)} got {Quote(actual.StartWrites)}";
        }

        var expectedStop = vector.GetProperty("stopWrites");
        var stop = expectedStop.ValueKind == JsonValueKind.Null ? null : expectedStop.GetString();
        if (!string.Equals(stop, actual.StopWrites, StringComparison.Ordinal))
        {
            return $"stopWrites expected {Quote(stop)} got {Quote(actual.StopWrites)}";
        }

        var expectedClipboard = vector.GetProperty("clipboard").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (!expectedClipboard.SequenceEqual(actual.Clipboard, StringComparer.Ordinal))
        {
            return $"clipboard expected [{string.Join(", ", expectedClipboard.Select(Quote))}] got [{string.Join(", ", actual.Clipboard.Select(Quote))}]";
        }

        var expectedUrls = vector.GetProperty("openedUrls").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (!expectedUrls.SequenceEqual(actual.OpenedUrls, StringComparer.Ordinal))
        {
            return $"openedUrls expected [{string.Join(", ", expectedUrls.Select(Quote))}] got [{string.Join(", ", actual.OpenedUrls.Select(Quote))}]";
        }

        foreach (var property in vector.GetProperty("overlayInputs").EnumerateObject())
        {
            var expectedInputs = property.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            var gotInputs = actual.OverlayInputs.TryGetValue(property.Name, out var value) ? value : [];
            if (!expectedInputs.SequenceEqual(gotInputs, StringComparer.Ordinal))
            {
                return $"overlay {property.Name} inputs expected [{string.Join(", ", expectedInputs.Select(Quote))}] got [{string.Join(", ", gotInputs.Select(Quote))}]";
            }
        }

        return null;
    }

    private static string Quote(string? value) => value is null ? "null" : "\"" + value.Replace("\x1b", "\\e", StringComparison.Ordinal) + "\"";

    [Theory]
    [MemberData(nameof(Indexes))]
    public void Scenario_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var diff = Compare(vector, Replay(vector));
        Assert.True(diff is null, $"{Id(vector)}: {diff}");
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsComplete()
    {
        var vectors = Data.Value;
        Assert.Equal(37, vectors.Count);
        Assert.Equal(vectors.Count, vectors.Select(Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(vectors, v => Id(v) == "");
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var vectors = Data.Value;

        // Terminal sizes include degenerate and wide cases.
        var sizes = vectors.Select(v => (v.GetProperty("columns").GetInt32(), v.GetProperty("rows").GetInt32())).ToHashSet();
        Assert.Contains((1, 1), sizes);
        Assert.Contains((80, 24), sizes);

        // Start/stop sequences are captured for both preserveScreen modes and with mouse capture off.
        Assert.Contains(vectors, v => v.GetProperty("stopWrites").ValueKind == JsonValueKind.String);
        var startWrites = vectors
            .Where(v => v.GetProperty("startWrites").ValueKind == JsonValueKind.String)
            .Select(v => v.GetProperty("startWrites").GetString() ?? "")
            .ToList();
        Assert.Contains(startWrites, w => w.Contains("\x1b[?1049h", StringComparison.Ordinal));
        Assert.Contains(startWrites, w => w.Contains("\x1b[?1003h", StringComparison.Ordinal));
        Assert.Contains(startWrites, w => !w.Contains("\x1b[?1003h", StringComparison.Ordinal));

        // Viewport scrolling is exercised in both directions and to both extremes.
        var viewportTops = vectors
            .SelectMany(v => v.GetProperty("observations").EnumerateArray())
            .Select(o => o.GetProperty("viewportTop").GetInt32())
            .ToHashSet();
        Assert.Contains(0, viewportTops);
        Assert.Contains(viewportTops, top => top > 0);

        // Selection state flips on and off, and the clipboard received at least one payload.
        Assert.Contains(
            vectors,
            v => v.GetProperty("observations").EnumerateArray().Any(o => o.GetProperty("hasActiveSelection").GetBoolean()));
        Assert.Contains(vectors, v => v.GetProperty("clipboard").GetArrayLength() > 0);

        // Both clipboard outcomes are covered.
        Assert.Contains(vectors, v => v.GetProperty("options").GetProperty("copySelection").GetString() == "ok");
        Assert.Contains(vectors, v => v.GetProperty("options").GetProperty("copySelection").GetString() == "message");

        // Overlays are shown and later hidden; the search overlay opens.
        Assert.Contains(vectors, v => v.GetProperty("observations").EnumerateArray().Any(o => o.GetProperty("hasOverlay").GetBoolean()));
        Assert.Contains(vectors, v => v.GetProperty("observations").EnumerateArray().Skip(1).Any(o => !o.GetProperty("hasOverlay").GetBoolean()));
        Assert.Contains(vectors, v => v.GetProperty("overlayInputs").EnumerateObject().Any(p => p.Value.GetArrayLength() > 0));

        // Hyperlink activation, scroll-to-end indicator, search styling and custom bindings are covered.
        Assert.Contains(vectors, v => v.GetProperty("openedUrls").GetArrayLength() > 0);
        Assert.Contains(vectors, v => v.GetProperty("options").GetProperty("scrollToEndIndicator").GetBoolean());
        Assert.Contains(vectors, v => v.GetProperty("options").GetProperty("searchStyles").GetBoolean());
        Assert.Contains(vectors, v => v.GetProperty("keybindings").ValueKind == JsonValueKind.Object);
    }
}
