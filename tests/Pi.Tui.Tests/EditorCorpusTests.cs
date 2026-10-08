using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Differential tests for <see cref="Editor"/>, driven by <c>editor-corpus.json</c>.
///
/// The corpus is produced by running the original TypeScript <c>components/editor.ts</c> in Node
/// (<c>node --experimental-strip-types</c>) and recording, after every operation, the observable
/// state: lines, cursor, autocomplete flag, padding, max-visible, and - where the scenario asks for
/// it - the full rendered output. <c>wordWrapLine</c> is covered by a separate pure-function
/// section. The unmodified upstream suites (<c>test/editor.test.ts</c>, 192 cases, and
/// <c>test/editor-history-keybindings.test.ts</c>) also pass against the same source, which is the
/// sandbox-consistency proof for this corpus.
///
/// Regeneration recipe and the deviations this corpus cannot express (T22, T24) are documented in
/// <c>docs/tui-porting-status.md</c>. Per scenario the generator also records <c>icuFrom</c>: the
/// first step whose result depends on <c>Intl.Segmenter</c>'s dictionary-based CJK word
/// segmentation, computed by re-running the scenario with a mirror of the C# segmenter and diffing
/// the steps. Everything before that index is still compared exactly.
/// </summary>
public class EditorCorpusTests
{
    // ---------------------------------------------------------------------------------------------
    // Corpus loading
    // ---------------------------------------------------------------------------------------------

    private sealed class Corpus
    {
        public required JsonElement Root { get; init; }

        public required List<JsonElement> Wrap { get; init; }

        public required List<JsonElement> Scenarios { get; init; }

        public required Dictionary<string, JsonElement> Results { get; init; }
    }

    private static readonly Lazy<Corpus> Data = new(LoadCorpus);

    private static Corpus LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "editor-corpus.json");
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        var results = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var result in root.GetProperty("results").EnumerateArray())
        {
            results[result.GetProperty("id").GetString()!] = result;
        }

        return new Corpus
        {
            Root = root,
            Wrap = [.. root.GetProperty("wrap").EnumerateArray()],
            Scenarios = [.. root.GetProperty("scenarios").EnumerateArray()],
            Results = results,
        };
    }

    /// <summary>
    /// Reads a harness flag that is emitted as <c>0</c>/<c>1</c> (the JS harness coerces with
    /// <c>!!</c>) rather than as a JSON boolean.
    /// </summary>
    private static bool JsonTruthy(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString()!.Length > 0,
            _ => false,
        };

    /// <summary>
    /// Reads an optional numeric scenario field. JS <c>Number.NaN</c> and infinities serialise to
    /// JSON <c>null</c>, so a null value means "not provided" (the TS code uses <c>?? default</c>).
    /// </summary>
    private static double? JsonNumber(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private const int ChunkSize = 64;

    public static IEnumerable<object[]> ScenarioChunks()
    {
        var count = Data.Value.Scenarios.Count;
        for (var start = 0; start < count; start += ChunkSize)
        {
            yield return [start, Math.Min(ChunkSize, count - start)];
        }
    }

    public static IEnumerable<object[]> WrapChunks()
    {
        var count = Data.Value.Wrap.Count;
        for (var start = 0; start < count; start += ChunkSize)
        {
            yield return [start, Math.Min(ChunkSize, count - start)];
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Themes (must mirror harness.mjs)
    // ---------------------------------------------------------------------------------------------

    private static EditorTheme Theme(int index) => index switch
    {
        0 => new EditorTheme
        {
            BorderColor = static text => text,
            SelectList = SelectListTheme.Plain,
        },
        1 => new EditorTheme
        {
            BorderColor = static text => $"<B>{text}</B>",
            SelectList = new SelectListTheme
            {
                SelectedPrefix = static text => $"<SP>{text}</SP>",
                SelectedText = static text => $"<ST>{text}</ST>",
                Description = static text => $"<D>{text}</D>",
                ScrollInfo = static text => $"<SI>{text}</SI>",
                NoMatch = static text => $"<NM>{text}</NM>",
            },
        },
        2 => new EditorTheme
        {
            BorderColor = static text => $"\x1b[2m{text}\x1b[0m",
            SelectList = SelectListTheme.Plain,
        },
        _ => new EditorTheme
        {
            BorderColor = static text => $"«{text}»",
            SelectList = SelectListTheme.Plain,
        },
    };

    /// <summary>Must mirror <c>BORDER_VARIANTS</c> in <c>harness.mjs</c>.</summary>
    private static Func<string, string>? BorderVariant(int index) => index switch
    {
        0 => null,
        1 => static text => $"[{text}]",
        2 => static text => text,
        3 => static text => $"\x1b[31m{text}\x1b[0m",
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "unknown border variant"),
    };

    // ---------------------------------------------------------------------------------------------
    // Test providers (must mirror makeProvider() in harness.mjs)
    // ---------------------------------------------------------------------------------------------

    private sealed class CorpusProvider : IAutocompleteProvider
    {
        private readonly JsonElement _spec;
        private readonly IReadOnlyList<AutocompleteItem> _items;
        private readonly JsMicrotaskQueue _loop;

        public CorpusProvider(JsonElement spec, JsMicrotaskQueue loop)
        {
            _spec = spec;
            _loop = loop;
            var items = new List<AutocompleteItem>();
            if (spec.TryGetProperty("items", out var itemsElement))
            {
                foreach (var item in itemsElement.EnumerateArray())
                {
                    items.Add(new AutocompleteItem
                    {
                        Value = item.GetProperty("value").GetString()!,
                        Label = item.GetProperty("label").GetString()!,
                        Description = item.TryGetProperty("description", out var description)
                            ? description.GetString()
                            : null,
                    });
                }
            }

            _items = items;
        }

        public IReadOnlyList<string>? TriggerCharacters
        {
            get
            {
                if (!_spec.TryGetProperty("triggerCharacters", out var triggers))
                {
                    return null;
                }

                return [.. triggers.EnumerateArray().Select(value => value.GetString()!)];
            }
        }

        public async Task<AutocompleteSuggestions?> GetSuggestionsAsync(
            string[] lines,
            int cursorLine,
            int cursorCol,
            AutocompleteRequest options)
        {
            if (_spec.TryGetProperty("abortAlways", out _))
            {
                // JS `setTimeout(resolve, 0)`: a timer, not a microtask, so only `sleep` fires it.
                await _loop.Delay(0);
            }

            if (options.Signal.IsCancellationRequested)
            {
                return null;
            }

            if (_spec.TryGetProperty("suggestNothing", out _))
            {
                return null;
            }

            if (_spec.TryGetProperty("emptyItems", out _))
            {
                return new AutocompleteSuggestions { Items = [], Prefix = SpecPrefix(lines, cursorLine, cursorCol) };
            }

            return new AutocompleteSuggestions
            {
                Items = _items,
                Prefix = SpecPrefix(lines, cursorLine, cursorCol),
            };
        }

        private string SpecPrefix(string[] lines, int cursorLine, int cursorCol) =>
            _spec.TryGetProperty("prefix", out var prefix)
                ? prefix.GetString()!
                : JsSlice(LineAt(lines, cursorLine), 0, cursorCol);

        public CompletionApplication ApplyCompletion(
            string[] lines,
            int cursorLine,
            int cursorCol,
            AutocompleteItem item,
            string prefix)
        {
            var line = LineAt(lines, cursorLine);
            var before = JsSlice(line, 0, cursorCol - prefix.Length);
            var after = JsSlice(line, cursorCol);
            var next = (string[])lines.Clone();
            next[cursorLine] = before + item.Value + after;
            return new CompletionApplication(next, cursorLine, cursorCol - prefix.Length + item.Value.Length);
        }

        public bool ShouldTriggerFileCompletion(string[] lines, int cursorLine, int cursorCol) =>
            !_spec.TryGetProperty("noFileCompletion", out _);
    }

    // ---------------------------------------------------------------------------------------------
    // Scenario runner (must mirror runScenario() in harness.mjs)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A deterministic stand-in for the JS microtask queue (deviation T25). Node runs the whole
    /// harness on one thread, so a continuation queued by <c>await</c> can never run while a
    /// synchronous operation is still executing. C# would otherwise run it on a thread-pool thread
    /// *concurrently*, which makes the observation racy. The editor's
    /// <see cref="Editor.AutocompleteDeferral"/> seam routes every JS-style <c>await</c> through
    /// this queue; the runner drains it once an operation has returned, exactly where the JS event
    /// loop would.
    /// </summary>
    /// <summary>
    /// A miniature JS event loop: a microtask queue plus a timer queue.
    ///
    /// Keeping them apart is what makes the corpus faithful. JS <c>flush</c> turns the event loop
    /// (two <c>setImmediate</c> ticks) but never advances the clock, so a pending <c>setTimeout</c> -
    /// such as the <c>abortAlways</c> provider's <c>setTimeout(0)</c> - stays pending across it; only
    /// <c>sleep</c> advances the clock. Modelling the provider's delay as a harness timer (instead
    /// of a real <c>Task.Delay</c>) is what lets the harness tell "the chain is between two
    /// microtasks" apart from "the chain is waiting for the clock".
    /// </summary>
    private sealed class JsMicrotaskQueue
    {
        private readonly Queue<JsMicrotaskSource> _pending = new();
        private readonly List<(int Due, JsMicrotaskSource Source)> _timers = new();
        private int _now;

        /// <summary>Queues one microtask hop; the continuation runs inside <see cref="Drain"/>.</summary>
        public JsMicrotask Defer()
        {
            var source = new JsMicrotaskSource();
            _pending.Enqueue(source);
            return source.Task;
        }

        /// <summary>Queues a <c>setTimeout(ms)</c>; only <see cref="Advance"/> fires it.</summary>
        public JsMicrotask Delay(int ms)
        {
            var source = new JsMicrotaskSource();
            _timers.Add((_now + ms, source));
            return source.Task;
        }

        public int Pending => _pending.Count;

        /// <summary>True while a harness timer is still waiting for the clock to advance.</summary>
        public bool HasPendingTimer => _timers.Count > 0;

        /// <summary>
        /// Runs every queued continuation, including the ones those continuations queue themselves -
        /// which is exactly what a JS event loop does with its microtask queue. Because
        /// <see cref="JsMicrotaskSource.Resume"/> invokes the continuation directly instead of
        /// completing a <see cref="Task"/>, every hop runs inline and the interleaving is fully
        /// deterministic.
        /// </summary>
        public int Drain()
        {
            var drained = 0;
            while (_pending.Count > 0)
            {
                _pending.Dequeue().Resume();
                drained++;
            }

            return drained;
        }

        /// <summary>Advances the clock and fires every timer that has come due, in registration order.</summary>
        public int Advance(int ms)
        {
            _now += ms;
            var due = _timers.Where(timer => timer.Due <= _now).ToList();
            _timers.RemoveAll(timer => timer.Due <= _now);
            foreach (var timer in due)
            {
                timer.Source.Resume();
            }

            return due.Count;
        }
    }

    /// <summary>
    /// Turns the event loop until the autocomplete chain has nothing left to do: every queued
    /// microtask hop runs, and every hop those hops queue.
    ///
    /// A C# continuation on an already-completed <see cref="Task"/> is posted rather than resumed
    /// inline (only the <see cref="JsMicrotask"/> hops run inline), so between hops the queue is
    /// transiently empty while a continuation is still in flight. Waiting for
    /// <see cref="Editor.IsAutocompleteRequestInFlight"/> to clear before declaring the queue empty
    /// is what makes the interleaving deterministic instead of racing the thread pool.
    ///
    /// Deliberately does not wait for the clock: a chain that is parked on a harness timer is left
    /// parked, because a JS <c>flush</c> does not advance time. Only <c>sleep</c> does.
    /// </summary>
    private static async Task SettleMicrotasksAsync(Editor editor, JsMicrotaskQueue loop)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (true)
        {
            if (loop.Pending > 0)
            {
                loop.Drain();
                continue;
            }

            if (!editor.IsAutocompleteRequestInFlight || loop.HasPendingTimer || Environment.TickCount64 > deadline)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private static string LineAt(IReadOnlyList<string> lines, int index) =>
        index >= 0 && index < lines.Count ? lines[index] : "";

    /// <summary>JS <c>String.prototype.slice(start, end)</c>.</summary>
    private static string JsSlice(string value, int start, int? end = null)
    {
        var length = value.Length;
        var from = start < 0 ? Math.Max(length + start, 0) : Math.Min(start, length);
        var to = end is null
            ? length
            : end.Value < 0
                ? Math.Max(length + end.Value, 0)
                : Math.Min(end.Value, length);
        return to <= from ? "" : value.Substring(from, to - from);
    }

    private static TuiMouseEventType MouseType(string value) => value switch
    {
        "press" => TuiMouseEventType.Press,
        "release" => TuiMouseEventType.Release,
        "move" => TuiMouseEventType.Move,
        "drag" => TuiMouseEventType.Drag,
        "click" => TuiMouseEventType.Click,
        "wheel" => TuiMouseEventType.Wheel,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown mouse type"),
    };

    private static TuiMouseButton MouseButton(string value) => value switch
    {
        "left" => TuiMouseButton.Left,
        "middle" => TuiMouseButton.Middle,
        "right" => TuiMouseButton.Right,
        "none" => TuiMouseButton.None,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown mouse button"),
    };

    private static TuiMouseEvent ToMouseEvent(JsonElement spec)
    {
        var mouse = new TuiMouseEvent
        {
            Type = MouseType(spec.GetProperty("type").GetString()!),
            Button = MouseButton(spec.GetProperty("button").GetString()!),
            X = spec.GetProperty("x").GetInt32(),
            Y = spec.GetProperty("y").GetInt32(),
            ScreenX = spec.GetProperty("screenX").GetInt32(),
            ScreenY = spec.GetProperty("screenY").GetInt32(),
            Width = spec.GetProperty("width").GetInt32(),
            Height = spec.GetProperty("height").GetInt32(),
            Shift = spec.TryGetProperty("shift", out var shift) && shift.GetBoolean(),
            Alt = spec.TryGetProperty("alt", out var alt) && alt.GetBoolean(),
            Ctrl = spec.TryGetProperty("ctrl", out var ctrl) && ctrl.GetBoolean(),
        };

        if (spec.TryGetProperty("wheelDelta", out var wheelDelta) && wheelDelta.ValueKind != JsonValueKind.Null)
        {
            mouse.WheelDelta = wheelDelta.GetInt32();
        }

        if (spec.TryGetProperty("clickCount", out var clickCount) && clickCount.ValueKind != JsonValueKind.Null)
        {
            mouse.ClickCount = clickCount.GetInt32();
        }

        return mouse;
    }

    private static JsonNode? SerializeMouseResult(TuiMouseEventResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["h"] = result.Handled == true ? 1 : 0,
            ["c"] = result.Capture == true ? 1 : 0,
            ["f"] = result.Focus == true ? 1 : 0,
            ["t"] = 0,
        };
    }

    private static JsonObject CoreSnapshot(Editor editor) => new()
    {
        ["L"] = new JsonArray([.. editor.GetLines().Select(line => (JsonNode?)JsonValue.Create(line))]),
        ["C"] = new JsonArray(editor.GetCursor().Line, editor.GetCursor().Col),
        ["A"] = editor.IsShowingAutocomplete() ? 1 : 0,
        ["P"] = editor.GetPaddingX(),
        ["M"] = editor.GetAutocompleteMaxVisible(),
    };

    /// <summary>
    /// Copy the observation into the step. <see cref="JsonNode"/> instances cannot be re-parented, so
    /// every value is round-tripped through its JSON text first.
    /// </summary>
    private static void Merge(JsonObject target, JsonNode? source)
    {
        if (source is not JsonObject obj)
        {
            return;
        }

        foreach (var (key, value) in obj)
        {
            target[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
        }
    }

    private static async Task<JsonObject> RunOpAsync(Editor editor, string kind, JsonElement op, JsMicrotaskQueue loop)
    {
        var observation = new JsonObject();

        switch (kind)
        {
            case "input":
                editor.HandleInput(op[1].GetString()!);
                break;
            case "inputs":
                foreach (var chunk in op[1].EnumerateArray())
                {
                    editor.HandleInput(chunk.GetString()!);
                }

                break;
            case "text":
                editor.SetText(op[1].GetString()!);
                break;
            case "insert":
                editor.InsertTextAtCursor(op[1].GetString()!);
                break;
            case "history":
                editor.AddToHistory(op[1].GetString()!);
                break;
            case "padding":
                // JSON null here means the TS harness passed `null` (or NaN/Infinity) to
                // setPaddingX; `Number.isFinite(null)` is false, so the value collapses to 0.
                editor.SetPaddingX(op[1].ValueKind == JsonValueKind.Number ? op[1].GetDouble() : double.NaN);
                break;
            case "maxVisible":
                editor.SetAutocompleteMaxVisible(op[1].ValueKind == JsonValueKind.Number ? op[1].GetDouble() : double.NaN);
                break;
            case "focus":
                editor.Focused = op[1].ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.Number => op[1].GetDouble() != 0,
                    _ => false,
                };
                break;
            case "disableSubmit":
                editor.DisableSubmit = op[1].GetBoolean();
                break;
            case "border":
                if (BorderVariant(op[1].GetInt32()) is { } variant)
                {
                    editor.BorderColor = variant;
                }

                break;
            case "invalidate":
                editor.Invalidate();
                break;
            case "provider":
                editor.SetAutocompleteProvider(new CorpusProvider(op[1], loop));
                break;
            case "flush":
                // JS `flush` is two `setImmediate` turns, each of which drains the microtask queue
                // to completion. It does not advance the clock, so an armed debounce timer is left
                // alone (that is what the `sleep` op is for).
                await SettleMicrotasksAsync(editor, loop);
                break;
            case "sleep":
            {
                // JS `sleep` advances the clock: the editor's debounce timer is a real one, and the
                // harness clock fires the provider's `setTimeout`.
                var slept = op[1].GetInt32();
                await Task.Delay(slept).ConfigureAwait(false);
                loop.Advance(slept);

                // The timer continuations land on the thread pool; keep turning the loop until one
                // of them has run the request to completion.
                var deadline = Environment.TickCount64 + 5_000;
                while (true)
                {
                    await SettleMicrotasksAsync(editor, loop);
                    if (editor.IsAutocompleteIdle && loop.Pending == 0)
                    {
                        break;
                    }

                    if (Environment.TickCount64 > deadline)
                    {
                        break;
                    }

                    await Task.Delay(1).ConfigureAwait(false);
                }

                break;
            }
            case "expanded":
                observation["E"] = editor.GetExpandedText();
                break;
            case "text-of":
                observation["T"] = editor.GetText();
                break;
            case "render":
            {
                var width = op[1].GetInt32();
                observation["R"] = new JsonArray([.. editor.Render(width).Select(line => (JsonNode?)JsonValue.Create(line))]);
                break;
            }
            case "mouse":
                observation["U"] = SerializeMouseResult(editor.HandleMouse(ToMouseEvent(op[1])));
                break;
            case "api":
            {
                var method = op[1].GetString()!;
                observation["V"] = method switch
                {
                    "getPaddingX" => editor.GetPaddingX(),
                    "getAutocompleteMaxVisible" => editor.GetAutocompleteMaxVisible(),
                    _ => throw new ArgumentOutOfRangeException(nameof(op), method, "unknown api method"),
                };
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown op kind");
        }

        return observation;
    }

    private static async Task<string?> RunScenarioAsync(JsonElement scenario)
    {
        var rows = scenario.TryGetProperty("rows", out var rowsElement) ? rowsElement.GetInt32() : 24;
        var tui = new FakeTui();
        ((StringTerminal)tui.Terminal).Rows = rows;

        var themeIndex = scenario.TryGetProperty("theme", out var themeElement) ? themeElement.GetInt32() : 0;
        var options = new EditorOptions
        {
            // JS `Number.NaN` / infinities serialise to JSON null and the TS constructor falls back
            // to its default for a nullish value (`options?.paddingX ?? 0`).
            PaddingX = JsonNumber(scenario, "paddingX"),
            AutocompleteMaxVisible = JsonNumber(scenario, "maxVisible"),
        };

        var editor = new Editor(tui, Theme(themeIndex), options);

        // `focus` / `disableSubmit` are emitted as 0/1 (the harness coerces with `!!`), not booleans.
        editor.Focused = JsonTruthy(scenario, "focus");
        var submitted = new List<string>();
        var changeCount = 0;
        editor.OnSubmit = text => submitted.Add(text);
        editor.OnChange = _ => changeCount++;
        editor.DisableSubmit = JsonTruthy(scenario, "disableSubmit");

        if (scenario.TryGetProperty("border", out var border) && border.ValueKind != JsonValueKind.Null &&
            BorderVariant(border.GetInt32()) is { } initialBorder)
        {
            editor.BorderColor = initialBorder;
        }

        if (scenario.TryGetProperty("history", out var history) && history.ValueKind != JsonValueKind.Null)
        {
            foreach (var entry in history.EnumerateArray())
            {
                editor.AddToHistory(entry.GetString()!);
            }
        }

        if (scenario.TryGetProperty("text", out var text))
        {
            editor.SetText(text.GetString()!);
        }

        var steps = new JsonArray();
        var loop = new JsMicrotaskQueue();
        editor.AutocompleteDeferral = loop.Defer;
        foreach (var op in scenario.GetProperty("ops").EnumerateArray())
        {
            var kind = op[0].GetString()!;
            var observation = await RunOpAsync(editor, kind, op, loop);

            // The snapshot is taken while the JS event loop is still "inside" the operation, i.e.
            // before the queued microtasks run - that is exactly what the TS harness observes.
            var step = CoreSnapshot(editor);
            Merge(step, observation);
            steps.Add(step);

            // Then the event loop keeps turning until the microtask queue is empty, which is the
            // state the next operation starts from.
            await SettleMicrotasksAsync(editor, loop);
        }

        var actual = new JsonObject
        {
            ["submitted"] = new JsonArray([.. submitted.Select(text => (JsonNode?)JsonValue.Create(text))]),
            ["changes"] = changeCount,
            ["renderRequests"] = tui.RenderRequests,
            ["steps"] = steps,
        };

        var result = Data.Value.Results[scenario.GetProperty("id").GetString()!];

        // T22: from `icuFrom` onward the TS behaviour depends on Intl.Segmenter's dictionary-based
        // CJK word segmentation, which .NET has no equivalent for. The generator computes that
        // boundary by re-running each scenario with the word segmenter swapped for a mirror of the
        // C# implementation, so every step before it is still verified exactly.
        if (result.TryGetProperty("icuFrom", out var icuFrom) && icuFrom.ValueKind == JsonValueKind.Number)
        {
            var boundary = icuFrom.GetInt32();
            var expectedSteps = JsonNode.Parse(result.GetProperty("steps").GetRawText())!.AsArray();
            if (boundary == 0)
            {
                // The whole scenario is dictionary-dependent (a lone CJK word-motion key). Nothing
                // can be compared, but the operation count must still line up.
                return steps.Count == expectedSteps.Count
                    ? null
                    : $"expected {expectedSteps.Count} steps, got {steps.Count}";
            }

            var expectedPrefix = expectedSteps.Take(boundary).ToList();
            var actualPrefix = steps.Take(boundary).ToList();
            var prefixMatches = expectedPrefix.Count == actualPrefix.Count;
            if (prefixMatches)
            {
                for (var i = 0; i < expectedPrefix.Count; i++)
                {
                    if (!JsonNode.DeepEquals(expectedPrefix[i], actualPrefix[i]))
                    {
                        prefixMatches = false;
                        break;
                    }
                }
            }

            return prefixMatches
                ? null
                : $"expected (first {boundary} of {expectedSteps.Count} steps) {new JsonArray([.. expectedPrefix]).ToJsonString()}\n" +
                  $"  actual {new JsonArray([.. actualPrefix]).ToJsonString()}";
        }

        var expected = new JsonObject
        {
            ["submitted"] = JsonNode.Parse(result.GetProperty("submitted").GetRawText()),
            ["changes"] = result.GetProperty("changes").GetInt32(),
            ["renderRequests"] = result.GetProperty("renderRequests").GetInt32(),
            ["steps"] = JsonNode.Parse(result.GetProperty("steps").GetRawText()),
        };

        return JsonNode.DeepEquals(expected, actual)
            ? null
            : $"expected {expected.ToJsonString()}\n  actual {actual.ToJsonString()}";
    }

    // ---------------------------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ScenarioChunks))]
    public async Task ScenariosMatchTheTypeScriptReference(int start, int count)
    {
        var failures = new List<string>();

        for (var i = start; i < start + count; i++)
        {
            var scenario = Data.Value.Scenarios[i];
            var id = scenario.GetProperty("id").GetString()!;
            string? failure;
            try
            {
                failure = await RunScenarioAsync(scenario);
            }
            catch (Exception error)
            {
                failure = $"threw {error.GetType().Name}: {error.Message}";
            }

            if (failure is not null)
            {
                failures.Add($"[{id}] ({scenario.GetProperty("_section").GetString()}) {failure}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Theory]
    [MemberData(nameof(WrapChunks))]
    public void WordWrapLineMatchesTheTypeScriptReference(int start, int count)
    {
        var failures = new List<string>();

        for (var i = start; i < start + count; i++)
        {
            var entry = Data.Value.Wrap[i];
            var line = entry.GetProperty("line").GetString()!;
            var width = entry.GetProperty("width").GetInt32();
            var usePreSegmented = entry.GetProperty("pre").GetInt32() != 0;

            List<(int Index, string Segment)>? preSegmented = usePreSegmented
                ? [.. UnicodeWidth.GraphemesWithIndex(line)]
                : null;

            var actual = Editor.WordWrapLine(line, width, preSegmented);
            var expected = entry.GetProperty("chunks").EnumerateArray()
                .Select(chunk => (Text: chunk[0].GetString()!, Start: chunk[1].GetInt32(), End: chunk[2].GetInt32()))
                .ToList();

            var matches = actual.Count == expected.Count;
            if (matches)
            {
                for (var c = 0; c < actual.Count; c++)
                {
                    if (actual[c].Text != expected[c].Text ||
                        actual[c].StartIndex != expected[c].Start ||
                        actual[c].EndIndex != expected[c].End)
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (!matches)
            {
                failures.Add(
                    $"[{i}] line={JsonSerializer.Serialize(line)} width={width} pre={usePreSegmented}\n" +
                    $"  expected {JsonSerializer.Serialize(expected)}\n" +
                    $"  actual   {JsonSerializer.Serialize(actual)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    /// <summary>
    /// Guards the corpus itself: a generator change that silently produces no-op vectors (the
    /// failure mode that made an earlier <c>select-list</c> key corpus 87% useless) must fail here.
    /// </summary>
    [Fact]
    public void CorpusIsFullyCovered()
    {
        var sections = Data.Value.Root.GetProperty("sections");
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["api"] = 55,
            ["render"] = 183,
            ["padding"] = 150,
            ["focus"] = 24,
            ["scroll"] = 18,
            ["mouse"] = 15,
            ["paste"] = 70,
            ["sequences"] = 78,
            ["keys"] = 3321,
            ["keysMoved"] = 1476,
            ["keysRender"] = 861,
            ["autocomplete"] = 76,
        };

        foreach (var (name, count) in expected)
        {
            Assert.Equal(count, sections.GetProperty(name).GetProperty("scenarios").GetInt32());
        }

        Assert.Equal(713, Data.Value.Wrap.Count);

        var total = expected.Values.Sum();
        Assert.Equal(total, Data.Value.Scenarios.Count);
        Assert.Equal(total, Data.Value.Results.Count);
    }

    [Fact]
    public void CorpusCoversTheInterestingOutcomes()
    {
        var coverage = Data.Value.Root.GetProperty("coverage");

        // State transitions actually happen in every family that mutates text.
        Assert.Equal(1369, coverage.GetProperty("keys").GetProperty("textChanged").GetInt32());
        Assert.Equal(611, coverage.GetProperty("keysMoved").GetProperty("textChanged").GetInt32());
        Assert.Equal(715, coverage.GetProperty("keysMoved").GetProperty("cursorChanged").GetInt32());
        Assert.Equal(110, coverage.GetProperty("paste").GetProperty("textChanged").GetInt32());
        Assert.Equal(63, coverage.GetProperty("autocomplete").GetProperty("textChanged").GetInt32());

        // Rendering is exercised, and consecutive renders differ (so the cursor/scroll output is real).
        Assert.Equal(915, coverage.GetProperty("render").GetProperty("renders").GetInt32());
        Assert.Equal(176, coverage.GetProperty("render").GetProperty("renderChanged").GetInt32());
        Assert.Equal(1722, coverage.GetProperty("keysRender").GetProperty("renders").GetInt32());
        Assert.Equal(430, coverage.GetProperty("keysRender").GetProperty("renderChanged").GetInt32());
        Assert.Equal(97, coverage.GetProperty("scroll").GetProperty("renderChanged").GetInt32());
        Assert.Equal(42, coverage.GetProperty("focus").GetProperty("renderChanged").GetInt32());
        Assert.Equal(129, coverage.GetProperty("mouse").GetProperty("renderChanged").GetInt32());

        // Mouse routing and the autocomplete menu both actually fire.
        Assert.Equal(1140, coverage.GetProperty("mouse").GetProperty("mouseResults").GetInt32());
        Assert.Equal(141, coverage.GetProperty("mouse").GetProperty("cursorChanged").GetInt32());
        Assert.Equal(233, coverage.GetProperty("autocomplete").GetProperty("acOpened").GetInt32());
        Assert.Equal(12, coverage.GetProperty("autocomplete").GetProperty("mouseResults").GetInt32());

        // Submit payloads, including paste-marker expansion.
        Assert.Equal(7, coverage.GetProperty("api").GetProperty("submits").GetInt32());
        Assert.Equal(8, coverage.GetProperty("autocomplete").GetProperty("submits").GetInt32());
        Assert.Equal(7, coverage.GetProperty("sequences").GetProperty("submits").GetInt32());
        Assert.Equal(4, coverage.GetProperty("paste").GetProperty("submits").GetInt32());
        Assert.Equal(52, coverage.GetProperty("keys").GetProperty("submits").GetInt32());
        Assert.Equal(24, coverage.GetProperty("keysMoved").GetProperty("submits").GetInt32());
        Assert.Equal(14, coverage.GetProperty("keysRender").GetProperty("submits").GetInt32());
    }

    /// <summary>
    /// Deviation T22: word segmentation of CJK text depends on <c>Intl.Segmenter</c>'s dictionary,
    /// which .NET cannot reproduce. The generator marks the exact step where the reference and the
    /// C#-equivalent segmenter first disagree; this pins that boundary so a generator regression
    /// cannot silently stop verifying (or silently start skipping) large parts of the corpus.
    /// </summary>
    [Fact]
    public void CorpusMarksTheIcuSegmentationBoundary()
    {
        var marked = Data.Value.Results.Values
            .Where(result => result.TryGetProperty("icuFrom", out var value) && value.ValueKind == JsonValueKind.Number)
            .ToList();

        Assert.Equal(23, marked.Count);

        // Five of them (all in `keys`) consist of nothing but a single CJK word-motion key, so the
        // very first step is already dictionary-dependent and no step can be compared. The other
        // eighteen still verify 23 steps before their boundary.
        var zero = marked.Count(result => result.GetProperty("icuFrom").GetInt32() == 0);
        Assert.Equal(5, zero);
        Assert.Equal(18, marked.Count - zero);
        Assert.Equal(23, marked.Sum(result => result.GetProperty("icuFrom").GetInt32()));

        // The boundary is only ever reached through a word-granularity segmentation over CJK text.
        var coverage = Data.Value.Root.GetProperty("coverage");
        Assert.Equal(2, coverage.GetProperty("sequences").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(10, coverage.GetProperty("keys").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(10, coverage.GetProperty("keysMoved").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(10, coverage.GetProperty("keysRender").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(0, coverage.GetProperty("api").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(0, coverage.GetProperty("render").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(0, coverage.GetProperty("paste").GetProperty("cjkWordOps").GetInt32());
        Assert.Equal(0, coverage.GetProperty("autocomplete").GetProperty("cjkWordOps").GetInt32());
    }

    /// <summary>
    /// Deviation T24: the upstream <c>wordWrapLine</c> recurses forever when a single grapheme is
    /// wider than <c>maxWidth</c>. Those inputs are deliberately absent from the corpus (they cannot
    /// be represented as data), so the fallback is pinned here instead.
    /// </summary>
    [Fact]
    public void WordWrapLineDoesNotRecurseOnOverWideGraphemes()
    {
        // TS: wordWrapLine("\u65e5\u672c", 1) -> RangeError: Maximum call stack size exceeded
        var wide = Editor.WordWrapLine("\u65e5\u672c", 1);
        Assert.Equal(2, wide.Count);
        Assert.Equal("\u65e5", wide[0].Text);
        Assert.Equal("\u672c", wide[1].Text);
        Assert.Equal(0, wide[0].StartIndex);
        Assert.Equal(1, wide[0].EndIndex);
        Assert.Equal(1, wide[1].StartIndex);
        Assert.Equal(2, wide[1].EndIndex);

        // TS: wordWrapLine("a\ud83c\udf89", 1) -> RangeError
        var emoji = Editor.WordWrapLine("a\ud83c\udf89", 1);
        Assert.Equal(2, emoji.Count);
        Assert.Equal("a", emoji[0].Text);
        Assert.Equal("\ud83c\udf89", emoji[1].Text);

        // Pure-narrow text at width 1 still matches the TS output exactly.
        var narrow = Editor.WordWrapLine("abc def", 1);
        Assert.Equal(["a", "b", "c", " ", "d", "e", "f"], narrow.Select(chunk => chunk.Text));
    }
}
