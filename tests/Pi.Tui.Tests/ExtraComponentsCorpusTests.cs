using System.Text.Json;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for the <c>settings-list.ts</c>, <c>image.ts</c>, <c>mouse-region.ts</c> and
/// <c>alt-screen-flash.ts</c> ports.
/// </summary>
/// <remarks>
/// <para>
/// <c>extra-components-corpus.json</c> holds vectors captured by running the original TypeScript
/// components (the reference):
/// <list type="bullet">
/// <item><b>settings</b> — op-sequence replay of <c>SettingsList</c>: item sets × widths ×
/// <c>maxVisible</c> × search on/off, key navigation (wrap / confirm / cancel / search typing,
/// including the two upstream <c>settings-list.test.ts</c> scenarios), fuzzy search queries,
/// <c>updateValue</c> / <c>selectItem</c>, mouse sweeps (press→click pairing, wheel, hover,
/// out-of-range rows, search rows) and the submenu lifecycle (open / done with value / done with
/// <c>navigateTo</c> / input delegation).</item>
/// <item><b>image</b> — <c>Image.render</c> across capabilities (kitty / iterm2 / none), mime types
/// (PNG / JPEG / GIF / WebP variants, garbage, unknown), transcoder presence (absent / success /
/// failure), widths, option sweeps (<c>maxWidthCells</c> / <c>maxHeightCells</c> / filename /
/// imageId reuse), explicit dimensions, cell dimensions and the render cache.</item>
/// <item><b>mouseRegion</b> — render passthrough and <c>handleMouse</c> delegation.</item>
/// <item><b>flash</b> — <c>AltScreenFlashContainer.render</c> with 0–3 live entries across widths
/// plus <c>dispose</c>.</item>
/// </list>
/// The vectors contain no machine-dependent input, so they are location independent.
/// </para>
/// </remarks>
public class ExtraComponentsCorpusTests
{
    // ------------------------------------------------------------------
    // Corpus loading
    // ------------------------------------------------------------------

    private static List<JsonElement> LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "extra-components-corpus.json");
        using var stream = File.OpenRead(path);
        var vectors = JsonSerializer.Deserialize<List<JsonElement>>(stream);
        Assert.NotNull(vectors);
        return vectors;
    }

    private static readonly Lazy<List<JsonElement>> Data = new(LoadCorpus);

    private static string Kind(JsonElement vector) =>
        vector.GetProperty("kind").GetString() ?? "";

    private static List<string> Lines(JsonElement vector) =>
        vector.GetProperty("lines").EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    private static int? IntOrNull(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            _ => throw new InvalidOperationException($"expected number or null, got {element.ValueKind}"),
        };

    private static string SerializeResult(TuiMouseEventResult? result)
    {
        if (result is null)
        {
            return "null";
        }

        var parts = new List<string>();
        if (result.Handled == true)
        {
            parts.Add("handled");
        }

        if (result.Capture == true)
        {
            parts.Add("capture");
        }

        if (result.Focus == true)
        {
            parts.Add("focus");
        }

        if (result.Render == true)
        {
            parts.Add("render:true");
        }
        else if (result.Render == false)
        {
            parts.Add("render:false");
        }

        return string.Join(",", parts);
    }

    /// <summary>JSON object form matching <c>JSON.stringify</c> of the TS result (unset fields dropped).</summary>
    private static string SerializeResultJson(TuiMouseEventResult? result)
    {
        if (result is null)
        {
            return "null";
        }

        var parts = new List<string>();
        if (result.Handled == true)
        {
            parts.Add("\"handled\":true");
        }

        if (result.Capture == true)
        {
            parts.Add("\"capture\":true");
        }

        if (result.Focus == true)
        {
            parts.Add("\"focus\":true");
        }

        if (result.Render == true)
        {
            parts.Add("\"render\":true");
        }
        else if (result.Render == false)
        {
            parts.Add("\"render\":false");
        }

        return "{" + string.Join(",", parts) + "}";
    }

    // ------------------------------------------------------------------
    // Theory data
    // ------------------------------------------------------------------

    public static TheoryData<int> SettingsIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Kind(Data.Value[i]) == "settings")
            {
                data.Add(i);
            }
        }

        return data;
    }

    public static TheoryData<int> ImageIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Data.Value.Count; i++)
        {
            if (Kind(Data.Value[i]) == "image")
            {
                data.Add(i);
            }
        }

        return data;
    }

    // ------------------------------------------------------------------
    // settings-list.ts
    // ------------------------------------------------------------------

    private static SettingsListTheme NewSettingsTheme() => new()
    {
        Label = (text, selected) => selected ? $"\x1b[7m{text}\x1b[27m" : text,
        Value = (text, selected) => selected ? $"\x1b[4m{text}\x1b[24m" : text,
        Description = text => $"\x1b[2m{text}\x1b[22m",
        Cursor = "> ",
        Hint = text => $"\x1b[90m{text}\x1b[39m",
    };

    [Theory]
    [MemberData(nameof(SettingsIndexes))]
    public void Settings_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var ops = vector.GetProperty("ops");
        var expectedSteps = vector.GetProperty("steps");

        var changes = new List<(string Id, string Value)>();
        var cancels = 0;
        SettingsList? list = null;
        Action<string?, SettingsListSubmenuCloseOptions?>? submenuDone = null;
        var stepIndex = 0;

        void ExpectStep(JsonElement step)
        {
            Assert.True(stepIndex < expectedSteps.GetArrayLength(), $"unexpected extra step {stepIndex}");
            Assert.Equal(expectedSteps[stepIndex].GetRawText(), step.GetRawText());
            stepIndex++;
        }

        foreach (var op in ops.EnumerateArray())
        {
            var name = op.GetProperty("op").GetString();
            switch (name)
            {
                case "new":
                {
                    submenuDone = null;
                    var items = new List<SettingItem>();
                    foreach (var item in op.GetProperty("items").EnumerateArray())
                    {
                        var hasSubmenu = item.GetProperty("submenu").GetBoolean();
                        items.Add(new SettingItem
                        {
                            Id = item.GetProperty("id").GetString() ?? "",
                            Label = item.GetProperty("label").GetString() ?? "",
                            Description = item.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                                ? description.GetString()
                                : null,
                            CurrentValue = item.GetProperty("currentValue").GetString() ?? "",
                            Values = item.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
                                ? values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
                                : null,
                            Submenu = hasSubmenu
                                ? (currentValue, done) =>
                                {
                                    submenuDone = done;
                                    return new Text($"submenu:{currentValue}");
                                }
                                : null,
                        });
                    }

                    list = new SettingsList(
                        items,
                        op.GetProperty("maxVisible").GetInt32(),
                        NewSettingsTheme(),
                        (id, value) => changes.Add((id, value)),
                        () => cancels++,
                        new SettingsListOptions { EnableSearch = op.GetProperty("search").GetBoolean() });
                    break;
                }

                case "key":
                    list!.HandleInput(op.GetProperty("data").GetString() ?? "");
                    break;

                case "mouse":
                {
                    var raw = op.GetProperty("event");
                    var result = list!.HandleMouse(NewMouseEvent(raw));
                    ExpectStep(JsonDocument.Parse(
                        $"{{\"op\":\"mouse\",\"result\":{SerializeResultJson(result)}}}").RootElement.Clone());
                    break;
                }

                case "update":
                    list!.UpdateValue(op.GetProperty("id").GetString() ?? "", op.GetProperty("value").GetString() ?? "");
                    break;

                case "select":
                    list!.SelectItem(op.GetProperty("id").GetString() ?? "");
                    break;

                case "done":
                {
                    var value = op.GetProperty("value");
                    var navigateTo = op.GetProperty("navigateTo");
                    submenuDone?.Invoke(
                        value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                        navigateTo.ValueKind == JsonValueKind.String
                            ? new SettingsListSubmenuCloseOptions { NavigateTo = navigateTo.GetString() }
                            : null);
                    break;
                }

                case "render":
                {
                    var width = op.GetProperty("width").GetInt32();
                    var lines = list!.Render(width);
                    var expected = expectedSteps[stepIndex];
                    Assert.Equal("render", expected.GetProperty("op").GetString());
                    Assert.Equal(width, expected.GetProperty("width").GetInt32());
                    Assert.Equal(
                        expected.GetProperty("lines").EnumerateArray().Select(e => e.GetString() ?? ""),
                        lines);
                    stepIndex++;
                    break;
                }

                case "state":
                {
                    var expected = expectedSteps[stepIndex];
                    Assert.Equal("state", expected.GetProperty("op").GetString());
                    Assert.Equal(expected.GetProperty("selectedIndex").GetInt32(), list!.SelectedIndexForTests);
                    Assert.Equal(expected.GetProperty("cancels").GetInt32(), cancels);
                    Assert.Equal(expected.GetProperty("submenuOpen").GetBoolean(), list.SubmenuOpenForTests);
                    var expectedChanges = expected.GetProperty("changes").EnumerateArray()
                        .Select(c => (c.GetProperty("id").GetString() ?? "", c.GetProperty("value").GetString() ?? ""))
                        .ToList();
                    Assert.Equal(expectedChanges, changes);
                    stepIndex++;
                    break;
                }

                default:
                    throw new InvalidOperationException($"unknown op {name}");
            }
        }

        Assert.Equal(expectedSteps.GetArrayLength(), stepIndex);
    }

    /// <summary>TS event-type string for a C# enum value ("click", "press", ...).</summary>
    private static string MouseTypeName(TuiMouseEventType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static TuiMouseEventType MouseType(string name) => name switch
    {
        "press" => TuiMouseEventType.Press,
        "release" => TuiMouseEventType.Release,
        "move" => TuiMouseEventType.Move,
        "drag" => TuiMouseEventType.Drag,
        "wheel" => TuiMouseEventType.Wheel,
        _ => TuiMouseEventType.Click,
    };

    private static TuiMouseEvent NewMouseEvent(JsonElement raw)
    {
        // The corpus stores only the overrides handed to the reference helper; the remaining
        // fields take the helper's defaults (x/y/screenX/screenY 0, width 80, height 20,
        // modifiers false, type click, button left).
        static int IntOr(JsonElement element, string name, int fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : fallback;

        static bool BoolOr(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        var type = raw.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
            ? typeValue.GetString() ?? "click"
            : "click";
        var button = raw.TryGetProperty("button", out var buttonValue) && buttonValue.ValueKind == JsonValueKind.String
            ? buttonValue.GetString() ?? "left"
            : "left";
        var wheelDelta = raw.TryGetProperty("wheelDelta", out var delta) && delta.ValueKind == JsonValueKind.Number
            ? delta.GetInt32()
            : (int?)null;
        return new TuiMouseEvent
        {
            Type = type switch
            {
                "press" => TuiMouseEventType.Press,
                "release" => TuiMouseEventType.Release,
                "move" => TuiMouseEventType.Move,
                "drag" => TuiMouseEventType.Drag,
                "wheel" => TuiMouseEventType.Wheel,
                _ => TuiMouseEventType.Click,
            },
            Button = button switch
            {
                "middle" => TuiMouseButton.Middle,
                "right" => TuiMouseButton.Right,
                "none" => TuiMouseButton.None,
                _ => TuiMouseButton.Left,
            },
            X = IntOr(raw, "x", 0),
            Y = IntOr(raw, "y", 0),
            ScreenX = IntOr(raw, "screenX", 0),
            ScreenY = IntOr(raw, "screenY", 0),
            Width = IntOr(raw, "width", 80),
            Height = IntOr(raw, "height", 20),
            Shift = BoolOr(raw, "shift"),
            Alt = BoolOr(raw, "alt"),
            Ctrl = BoolOr(raw, "ctrl"),
            WheelDelta = wheelDelta,
        };
    }

    // ------------------------------------------------------------------
    // image.ts
    // ------------------------------------------------------------------

    private static ImageTheme NewImageTheme() => new()
    {
        FallbackColor = text => $"\x1b[31m{text}\x1b[39m",
    };

    private static ImageTranscoder? TranscoderFor(string name) => name switch
    {
        "no-transcoder" => null,
        "transcoder-ok" => (base64, mime) => $"png:{mime}:{base64.Length}",
        "transcoder-null" => (_, _) => null,
        _ => throw new InvalidOperationException($"unknown transcoder {name}"),
    };

    [Theory]
    [MemberData(nameof(ImageIndexes))]
    public void Image_MatchesTypeScriptReference(int index)
    {
        var vector = Data.Value[index];
        var caps = vector.GetProperty("caps").GetString();
        var transcoderName = vector.GetProperty("transcoder").GetString() ?? "no-transcoder";
        var width = vector.GetProperty("width").GetInt32();
        var data = vector.GetProperty("data").GetString() ?? "";
        var mimeType = vector.GetProperty("mimeType").GetString() ?? "";

        TerminalImage.ResetCapabilitiesCache();
        TerminalImage.SetCapabilities(new TerminalCapabilities(
            caps switch
            {
                "kitty" => ImageProtocol.Kitty,
                "iterm2" => ImageProtocol.Iterm2,
                _ => ImageProtocol.None,
            },
            true,
            true));
        Image.SetImageTranscoder(TranscoderFor(transcoderName));

        var cellDimensions = vector.TryGetProperty("cellDimensions", out var cells) && cells.ValueKind == JsonValueKind.Object
            ? new CellDimensions(cells.GetProperty("widthPx").GetInt32(), cells.GetProperty("heightPx").GetInt32())
            : new CellDimensions(9, 18);
        TerminalImage.SetCellDimensions(cellDimensions);

        var rawOptions = vector.GetProperty("options");
        var options = new ImageOptions
        {
            MaxWidthCells = rawOptions.TryGetProperty("maxWidthCells", out var maxWidth) && maxWidth.ValueKind == JsonValueKind.Number
                ? maxWidth.GetInt32()
                : null,
            MaxHeightCells = rawOptions.TryGetProperty("maxHeightCells", out var maxHeight) && maxHeight.ValueKind == JsonValueKind.Number
                ? maxHeight.GetInt32()
                : null,
            Filename = rawOptions.TryGetProperty("filename", out var filename) && filename.ValueKind == JsonValueKind.String
                ? filename.GetString()
                : null,
            ImageId = rawOptions.TryGetProperty("imageId", out var imageId) && imageId.ValueKind == JsonValueKind.Number
                ? imageId.GetInt64()
                : null,
        };

        ImageDimensions? dimensions = vector.TryGetProperty("dimensions", out var explicitDimensions) && explicitDimensions.ValueKind == JsonValueKind.Object
            ? new ImageDimensions(explicitDimensions.GetProperty("widthPx").GetInt32(), explicitDimensions.GetProperty("heightPx").GetInt32())
            : null;

        var image = new Image(data, mimeType, NewImageTheme(), options, dimensions);
        var lines = image.Render(width);
        Assert.Equal(Lines(vector), lines);

        var expectedImageId = vector.GetProperty("imageId");
        Assert.Equal(
            expectedImageId.ValueKind == JsonValueKind.Number ? expectedImageId.GetInt64() : (long?)null,
            image.GetImageId());

        if (vector.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
        {
            // The reference rendered the same width twice (identical), a different width (differs)
            // and re-rendered after invalidate (identical again).
            var second = image.Render(width);
            Assert.Equal(cache.GetProperty("secondIdentical").GetBoolean(), lines.SequenceEqual(second));

            var otherWidth = width == 20 ? 40 : 20;
            var other = image.Render(otherWidth);
            Assert.Equal(cache.GetProperty("otherWidthDiffers").GetBoolean(), !lines.SequenceEqual(other));

            image.Invalidate();
            var third = image.Render(width);
            Assert.Equal(cache.GetProperty("afterInvalidate").GetBoolean(), lines.SequenceEqual(third));
        }
    }

    // ------------------------------------------------------------------
    // mouse-region.ts
    // ------------------------------------------------------------------

    [Fact]
    public void MouseRegion_MatchesTypeScriptReference()
    {
        // The reference drives a single region through a press → click → release → move → drag →
        // wheel sweep, recording the cumulative handler log after each event. The corpus stores one
        // vector per event, so the region and its log must survive across consecutive
        // "handler-invoked" vectors (and only those).
        MouseRegion? region = null;
        List<(string Type, int X, int Y)>? seen = null;

        foreach (var vector in Data.Value.Where(v => Kind(v) == "mouseRegion"))
        {
            var testCase = vector.GetProperty("case").GetString();
            switch (testCase)
            {
                case "render-passthrough":
                {
                    region = null;
                    seen = null;
                    var passthrough = new MouseRegion(new Text("child"), _ => new TuiMouseEventResult { Handled = true, Render = true });
                    Assert.Equal(Lines(vector), passthrough.Render(vector.GetProperty("width").GetInt32()));
                    break;
                }

                case "handler-invoked":
                {
                    if (region is null || seen is null)
                    {
                        seen = [];
                        region = new MouseRegion(
                            new Text("child"),
                            e =>
                            {
                                // The reference records the TS event-type string, so map back to it.
                                seen!.Add((MouseTypeName(e.Type), e.X, e.Y));
                                return new TuiMouseEventResult { Handled = true, Render = true };
                            });
                    }

                    var result = region.HandleMouse(new TuiMouseEvent
                    {
                        Type = MouseType(vector.GetProperty("eventType").GetString() ?? "click"),
                        Button = TuiMouseButton.Left,
                        X = 2,
                        Y = 1,
                        ScreenX = 2,
                        ScreenY = 1,
                        Width = 80,
                        Height = 20,
                    });
                    var expectedResult = vector.GetProperty("result");
                    Assert.Equal(
                        expectedResult.ValueKind == JsonValueKind.Object
                            ? string.Join(",", expectedResult.EnumerateObject()
                                .Where(p => p.Value.ValueKind == JsonValueKind.True)
                                .Select(p => p.Name == "render" ? "render:true" : p.Name)
                                .OrderBy(p => p, StringComparer.Ordinal))
                            : "null",
                        SerializeResult(result));
                    Assert.Equal(
                        vector.GetProperty("seen").EnumerateArray()
                            .Select(s => (s.GetProperty("type").GetString() ?? "", s.GetProperty("x").GetInt32(), s.GetProperty("y").GetInt32())),
                        seen);
                    break;
                }

                case "child-result":
                {
                    region = null;
                    seen = null;
                    var handlerCalls = 0;
                    var childResultRegion = new MouseRegion(
                        new Text("child"),
                        _ =>
                        {
                            handlerCalls++;
                            return new TuiMouseEventResult { Handled = true };
                        });
                    var result = childResultRegion.HandleMouse(new TuiMouseEvent
                    {
                        Type = TuiMouseEventType.Press,
                        Button = TuiMouseButton.Left,
                        X = 0,
                        Y = 0,
                        ScreenX = 0,
                        ScreenY = 0,
                        Width = 80,
                        Height = 20,
                    });
                    Assert.Equal("handled", SerializeResult(result));
                    Assert.Equal(vector.GetProperty("handlerCalls").GetInt32(), handlerCalls);
                    break;
                }

                case "invalidate-noop":
                {
                    region = null;
                    seen = null;
                    var noopRegion = new MouseRegion(new Text("child"), _ => null);
                    noopRegion.Invalidate();
                    break;
                }

                default:
                    throw new InvalidOperationException($"unknown mouseRegion case {testCase}");
            }
        }
    }

    // ------------------------------------------------------------------
    // alt-screen-flash.ts
    // ------------------------------------------------------------------

    [Fact]
    public void Flash_MatchesTypeScriptReference()
    {
        var messages = new[] { "Saved", "Copied to clipboard", "A much longer flash message that will be truncated" };
        foreach (var vector in Data.Value.Where(v => Kind(v) == "flash"))
        {
            var requestRenders = 0;
            var container = new AltScreenFlashContainer(() => requestRenders++);
            try
            {
                var entryCount = vector.GetProperty("entryCount").GetInt32();
                var disposeMessages = vector.TryGetProperty("messages", out var rawMessages) && rawMessages.ValueKind == JsonValueKind.Array
                    ? rawMessages.EnumerateArray().Select(m => m.GetString() ?? "").ToArray()
                    : messages;
                for (var i = 0; i < entryCount; i++)
                {
                    container.Flash(disposeMessages[i % disposeMessages.Length], 100000);
                }

                var width = vector.GetProperty("width").GetInt32();
                Assert.Equal(Lines(vector), container.Render(width));
                Assert.Equal(vector.GetProperty("requestRenders").GetInt32(), requestRenders);

                if (vector.TryGetProperty("afterDispose", out var afterDispose) && afterDispose.ValueKind == JsonValueKind.Array)
                {
                    container.Dispose();
                    Assert.Equal(
                        afterDispose.EnumerateArray().Select(e => e.GetString() ?? ""),
                        container.Render(width));
                }
            }
            finally
            {
                container.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------

    [Fact]
    public void CorpusIsComplete()
    {
        var vectors = Data.Value;
        Assert.Equal(1692, vectors.Count);

        var kinds = vectors.GroupBy(Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(874, kinds["settings"]);
        Assert.Equal(784, kinds["image"]);
        Assert.Equal(9, kinds["mouseRegion"]);
        Assert.Equal(25, kinds["flash"]);
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var vectors = Data.Value;

        // --- settings ---
        var settings = vectors.Where(v => Kind(v) == "settings").ToList();

        // Empty item set, no-match search, and scrolling all render distinct paths.
        Assert.Contains(settings, v => v.GetProperty("ops")[0].GetProperty("items").GetArrayLength() == 0);
        Assert.Contains(settings, v => v.GetProperty("ops").EnumerateArray()
            .Any(op => op.GetProperty("op").GetString() == "key" && (op.GetProperty("data").GetString() ?? "").StartsWith('z')));

        // The fuzzy search path is exercised (search enabled with typed queries).
        Assert.Contains(settings, v => v.GetProperty("ops")[0].GetProperty("search").GetBoolean()
            && v.GetProperty("ops").EnumerateArray().Count(op => op.GetProperty("op").GetString() == "key") > 2);

        // Value cycling fires onChange; escape fires onCancel.
        var changes = settings.SelectMany(v => v.GetProperty("steps").EnumerateArray()
            .Where(s => s.GetProperty("op").GetString() == "state")
            .SelectMany(s => s.GetProperty("changes").EnumerateArray())).ToList();
        Assert.Contains(changes, c => c.GetProperty("id").GetString() == "a" && c.GetProperty("value").GetString() == "2");
        Assert.Contains(settings, v => v.GetProperty("steps").EnumerateArray()
            .Any(s => s.GetProperty("op").GetString() == "state" && s.GetProperty("cancels").GetInt32() > 0));

        // The submenu lifecycle is covered: open, close with value, close with navigateTo.
        Assert.Contains(settings, v => v.GetProperty("steps").EnumerateArray()
            .Any(s => s.GetProperty("op").GetString() == "state" && s.GetProperty("submenuOpen").GetBoolean()));
        Assert.Contains(settings, v => v.GetProperty("ops").EnumerateArray()
            .Any(op => op.GetProperty("op").GetString() == "done" && op.GetProperty("navigateTo").ValueKind == JsonValueKind.String));

        // Mouse press→click pairing and wheel scrolling are covered.
        Assert.Contains(settings, v => v.GetProperty("steps").EnumerateArray()
            .Any(s => s.GetProperty("op").GetString() == "mouse"
                && s.GetProperty("result").ValueKind == JsonValueKind.Object
                && s.GetProperty("result").GetProperty("handled").GetBoolean()));
        Assert.Contains(settings, v => v.GetProperty("ops").EnumerateArray()
            .Any(op => op.GetProperty("op").GetString() == "mouse" && op.GetProperty("event").GetProperty("type").GetString() == "wheel"));

        // --- image ---
        var images = vectors.Where(v => Kind(v) == "image").ToList();
        Assert.Contains(images, v => v.GetProperty("caps").GetString() == "kitty");
        Assert.Contains(images, v => v.GetProperty("caps").GetString() == "iterm2");
        Assert.Contains(images, v => v.GetProperty("caps").GetString() == "none");
        Assert.Contains(images, v => v.GetProperty("transcoder").GetString() == "transcoder-ok");
        Assert.Contains(images, v => v.GetProperty("transcoder").GetString() == "transcoder-null");
        Assert.Contains(images, v => v.GetProperty("mimeType").GetString() == "image/jpeg");
        Assert.Contains(images, v => v.GetProperty("mimeType").GetString() == "image/gif");
        Assert.Contains(images, v => v.GetProperty("mimeType").GetString() == "image/webp");
        Assert.Contains(images, v => v.GetProperty("mimeType").GetString() == "image/avif");
        Assert.Contains(images, v => v.GetProperty("name").GetString() == "cache");
        Assert.Contains(images, v => v.GetProperty("name").GetString() == "explicit-dimensions");
        Assert.Contains(images, v => v.GetProperty("name").GetString() == "cell-dimensions");
        Assert.Contains(images, v => v.GetProperty("name").GetString() == "kitty-jpeg-transcoder");

        // Both the image path (multi-line kitty/iterm2 output) and the text fallback are covered.
        Assert.Contains(images, v => v.GetProperty("lines").GetArrayLength() > 1);
        Assert.Contains(images, v => v.GetProperty("lines").GetArrayLength() == 1
            && (v.GetProperty("lines")[0].GetString() ?? "").Contains("\x1b[31m"));

        // --- flash ---
        var flashes = vectors.Where(v => Kind(v) == "flash").ToList();
        Assert.Contains(flashes, v => v.GetProperty("entryCount").GetInt32() == 0);
        Assert.Contains(flashes, v => v.GetProperty("entryCount").GetInt32() == 3);
        Assert.Contains(flashes, v => v.TryGetProperty("afterDispose", out _));

        // --- mouseRegion ---
        var regions = vectors.Where(v => Kind(v) == "mouseRegion").ToList();
        Assert.Contains(regions, v => v.GetProperty("case").GetString() == "render-passthrough");
        Assert.Contains(regions, v => v.GetProperty("case").GetString() == "handler-invoked");
        Assert.Contains(regions, v => v.GetProperty("case").GetString() == "child-result");
    }
}
