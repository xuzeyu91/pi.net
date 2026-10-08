namespace Pi.Tui;

/// <summary>
/// One key id, or a list of key ids (port of the TS union <c>KeyId | KeyId[]</c>). Keeping the two
/// cases distinct matters because <see cref="KeybindingsManager.GetResolvedBindings"/> mirrors the TS
/// shape: a single binding round-trips as a bare id, several as an array.
/// </summary>
public readonly struct KeybindingKeys : IEquatable<KeybindingKeys>
{
    private readonly string[]? _values;

    public KeybindingKeys(string key) => _values = new[] { key };

    public KeybindingKeys(IEnumerable<string> keys) => _values = keys.ToArray();

    public IReadOnlyList<string> Values => _values ?? Array.Empty<string>();

    /// <summary>Whether this holds exactly one key id.</summary>
    public bool IsSingle => Values.Count == 1;

    /// <summary>The single key id. Only valid when <see cref="IsSingle"/>.</summary>
    public string Single => Values.Count == 1
        ? Values[0]
        : throw new InvalidOperationException($"Expected exactly one key id, got {Values.Count}");

    public static implicit operator KeybindingKeys(string key) => new(key);

    public static implicit operator KeybindingKeys(string[] keys) => new(keys);

    public bool Equals(KeybindingKeys other) => Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is KeybindingKeys other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => IsSingle ? Single : string.Join(", ", Values);
}

/// <summary>A keybinding definition: the default keys and a human-readable description.</summary>
public sealed record KeybindingDefinition(KeybindingKeys DefaultKeys, string? Description);

/// <summary>User overrides, keyed by binding id. A missing id falls back to the definition default.</summary>
public sealed class KeybindingsConfig : Dictionary<string, KeybindingKeys>
{
    public KeybindingsConfig()
    {
    }

    public KeybindingsConfig(IEnumerable<KeyValuePair<string, KeybindingKeys>> source) : base()
    {
        foreach (var (key, value) in source)
        {
            this[key] = value;
        }
    }
}

/// <summary>A key that is claimed by more than one user binding.</summary>
public sealed record KeybindingConflict(string Key, IReadOnlyList<string> Keybindings);

/// <summary>
/// The binding ids shipped with the TUI (port of the TS <c>Keybindings</c> interface, whose members
/// downstream packages extend by declaration merging). C# has no declaration merging, so the ids are
/// constants and the registry accepts arbitrary string ids.
/// </summary>
public static class TuiKeybindingIds
{
    public const string EditorCursorUp = "tui.editor.cursorUp";
    public const string EditorCursorDown = "tui.editor.cursorDown";
    public const string EditorHistoryPrevious = "tui.editor.historyPrevious";
    public const string EditorHistoryNext = "tui.editor.historyNext";
    public const string EditorCursorLeft = "tui.editor.cursorLeft";
    public const string EditorCursorRight = "tui.editor.cursorRight";
    public const string EditorCursorWordLeft = "tui.editor.cursorWordLeft";
    public const string EditorCursorWordRight = "tui.editor.cursorWordRight";
    public const string EditorCursorLineStart = "tui.editor.cursorLineStart";
    public const string EditorCursorLineEnd = "tui.editor.cursorLineEnd";
    public const string EditorJumpForward = "tui.editor.jumpForward";
    public const string EditorJumpBackward = "tui.editor.jumpBackward";
    public const string EditorPageUp = "tui.editor.pageUp";
    public const string EditorPageDown = "tui.editor.pageDown";
    public const string EditorDeleteCharBackward = "tui.editor.deleteCharBackward";
    public const string EditorDeleteCharForward = "tui.editor.deleteCharForward";
    public const string EditorDeleteWordBackward = "tui.editor.deleteWordBackward";
    public const string EditorDeleteWordForward = "tui.editor.deleteWordForward";
    public const string EditorDeleteToLineStart = "tui.editor.deleteToLineStart";
    public const string EditorDeleteToLineEnd = "tui.editor.deleteToLineEnd";
    public const string EditorYank = "tui.editor.yank";
    public const string EditorYankPop = "tui.editor.yankPop";
    public const string EditorUndo = "tui.editor.undo";
    public const string InputNewLine = "tui.input.newLine";
    public const string InputSubmit = "tui.input.submit";
    public const string InputTab = "tui.input.tab";
    public const string InputCopy = "tui.input.copy";
    public const string SelectUp = "tui.select.up";
    public const string SelectDown = "tui.select.down";
    public const string SelectPageUp = "tui.select.pageUp";
    public const string SelectPageDown = "tui.select.pageDown";
    public const string SelectConfirm = "tui.select.confirm";
    public const string SelectCancel = "tui.select.cancel";
    public const string AltScreenPageUp = "tui.altScreen.pageUp";
    public const string AltScreenPageDown = "tui.altScreen.pageDown";
    public const string AltScreenHalfPageUp = "tui.altScreen.halfPageUp";
    public const string AltScreenHalfPageDown = "tui.altScreen.halfPageDown";
    public const string AltScreenLineUp = "tui.altScreen.lineUp";
    public const string AltScreenLineDown = "tui.altScreen.lineDown";
    public const string AltScreenPreviousPrompt = "tui.altScreen.previousPrompt";
    public const string AltScreenNextPrompt = "tui.altScreen.nextPrompt";
    public const string AltScreenSearch = "tui.altScreen.search";
    public const string AltScreenSearchNext = "tui.altScreen.searchNext";
    public const string AltScreenSearchPrevious = "tui.altScreen.searchPrevious";
    public const string AltScreenSearchClose = "tui.altScreen.searchClose";
    public const string AltScreenTop = "tui.altScreen.top";
    public const string AltScreenBottom = "tui.altScreen.bottom";
}

/// <summary>The default keybinding table (port of <c>TUI_KEYBINDINGS</c> in <c>keybindings.ts</c>).</summary>
public static class TuiKeybindings
{
    /// <summary>Definition for every id in <see cref="TuiKeybindingIds"/>, in declaration order.</summary>
    public static IReadOnlyDictionary<string, KeybindingDefinition> Definitions { get; } = BuildDefinitions();

    private static Dictionary<string, KeybindingDefinition> BuildDefinitions() => new(StringComparer.Ordinal)
    {
        [TuiKeybindingIds.EditorCursorUp] = new("up", "Move cursor up"),
        [TuiKeybindingIds.EditorCursorDown] = new("down", "Move cursor down"),
        [TuiKeybindingIds.EditorHistoryPrevious] = new(Array.Empty<string>(), "Select previous prompt history entry"),
        [TuiKeybindingIds.EditorHistoryNext] = new(Array.Empty<string>(), "Select next prompt history entry"),
        [TuiKeybindingIds.EditorCursorLeft] = new(new[] { "left", "ctrl+b" }, "Move cursor left"),
        [TuiKeybindingIds.EditorCursorRight] = new(new[] { "right", "ctrl+f" }, "Move cursor right"),
        [TuiKeybindingIds.EditorCursorWordLeft] = new(new[] { "alt+left", "ctrl+left", "alt+b" }, "Move cursor word left"),
        [TuiKeybindingIds.EditorCursorWordRight] = new(new[] { "alt+right", "ctrl+right", "alt+f" }, "Move cursor word right"),
        [TuiKeybindingIds.EditorCursorLineStart] = new(new[] { "home", "ctrl+a" }, "Move to line start"),
        [TuiKeybindingIds.EditorCursorLineEnd] = new(new[] { "end", "ctrl+e" }, "Move to line end"),
        [TuiKeybindingIds.EditorJumpForward] = new("ctrl+]", "Jump forward to character"),
        [TuiKeybindingIds.EditorJumpBackward] = new("ctrl+alt+]", "Jump backward to character"),
        [TuiKeybindingIds.EditorPageUp] = new(new[] { "pageUp", "ctrl+pageUp" }, "Page up"),
        [TuiKeybindingIds.EditorPageDown] = new(new[] { "pageDown", "ctrl+pageDown" }, "Page down"),
        [TuiKeybindingIds.EditorDeleteCharBackward] = new("backspace", "Delete character backward"),
        [TuiKeybindingIds.EditorDeleteCharForward] = new(new[] { "delete", "ctrl+d" }, "Delete character forward"),
        [TuiKeybindingIds.EditorDeleteWordBackward] = new(new[] { "ctrl+w", "alt+backspace" }, "Delete word backward"),
        [TuiKeybindingIds.EditorDeleteWordForward] = new(new[] { "alt+d", "alt+delete" }, "Delete word forward"),
        [TuiKeybindingIds.EditorDeleteToLineStart] = new("ctrl+u", "Delete to line start"),
        [TuiKeybindingIds.EditorDeleteToLineEnd] = new("ctrl+k", "Delete to line end"),
        [TuiKeybindingIds.EditorYank] = new("ctrl+y", "Yank"),
        [TuiKeybindingIds.EditorYankPop] = new("alt+y", "Yank pop"),
        [TuiKeybindingIds.EditorUndo] = new("ctrl+-", "Undo"),
        [TuiKeybindingIds.InputNewLine] = new(new[] { "shift+enter", "ctrl+j" }, "Insert newline"),
        [TuiKeybindingIds.InputSubmit] = new("enter", "Submit input"),
        [TuiKeybindingIds.InputTab] = new("tab", "Tab / autocomplete"),
        [TuiKeybindingIds.InputCopy] = new("ctrl+c", "Copy selection"),
        [TuiKeybindingIds.SelectUp] = new("up", "Move selection up"),
        [TuiKeybindingIds.SelectDown] = new("down", "Move selection down"),
        [TuiKeybindingIds.SelectPageUp] = new("pageUp", "Selection page up"),
        [TuiKeybindingIds.SelectPageDown] = new("pageDown", "Selection page down"),
        [TuiKeybindingIds.SelectConfirm] = new("enter", "Confirm selection"),
        [TuiKeybindingIds.SelectCancel] = new(new[] { "escape", "ctrl+c" }, "Cancel selection"),
        // These intentionally shadow the unmodified editor bindings in fullscreen mode.
        [TuiKeybindingIds.AltScreenPageUp] = new("pageUp", "Scroll viewport up one page"),
        [TuiKeybindingIds.AltScreenPageDown] = new("pageDown", "Scroll viewport down one page"),
        [TuiKeybindingIds.AltScreenHalfPageUp] = new(Array.Empty<string>(), "Scroll viewport up half a page"),
        [TuiKeybindingIds.AltScreenHalfPageDown] = new(Array.Empty<string>(), "Scroll viewport down half a page"),
        [TuiKeybindingIds.AltScreenLineUp] = new(Array.Empty<string>(), "Scroll viewport up one line"),
        [TuiKeybindingIds.AltScreenLineDown] = new(Array.Empty<string>(), "Scroll viewport down one line"),
        [TuiKeybindingIds.AltScreenPreviousPrompt] = new(new[] { "ctrl+shift+up", "ctrl+up" }, "Jump to previous semantic prompt"),
        [TuiKeybindingIds.AltScreenNextPrompt] = new(new[] { "ctrl+shift+down", "ctrl+down" }, "Jump to next semantic prompt"),
        [TuiKeybindingIds.AltScreenSearch] = new("ctrl+shift+f", "Search the primary scroll view"),
        [TuiKeybindingIds.AltScreenSearchNext] = new(new[] { "enter", "ctrl+g" }, "Select the next search match"),
        [TuiKeybindingIds.AltScreenSearchPrevious] = new(new[] { "shift+enter", "ctrl+shift+g" }, "Select the previous search match"),
        [TuiKeybindingIds.AltScreenSearchClose] = new("escape", "Close transcript search"),
        [TuiKeybindingIds.AltScreenTop] = new("ctrl+home", "Scroll viewport to top"),
        [TuiKeybindingIds.AltScreenBottom] = new("ctrl+end", "Scroll viewport to bottom"),
    };
}

/// <summary>
/// Global keybinding registry (port of <c>keybindings.ts</c>). Downstream packages supply their own
/// definition table, mirroring the TS <c>KeybindingsManager</c> subclass in <c>coding-agent</c>.
/// </summary>
public class KeybindingsManager
{
    private readonly IReadOnlyDictionary<string, KeybindingDefinition> _definitions;
    private KeybindingsConfig _userBindings;
    private readonly Dictionary<string, string[]> _keysById = new(StringComparer.Ordinal);
    private List<KeybindingConflict> _conflicts = new();

    public KeybindingsManager(
        IReadOnlyDictionary<string, KeybindingDefinition> definitions,
        KeybindingsConfig? userBindings = null)
    {
        _definitions = definitions;
        _userBindings = userBindings ?? new KeybindingsConfig();
        Rebuild();
    }

    private static List<string> NormalizeKeys(IReadOnlyList<string>? keys)
    {
        var result = new List<string>();
        if (keys is null)
        {
            return result;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (seen.Add(key))
            {
                result.Add(key);
            }
        }
        return result;
    }

    private void Rebuild()
    {
        _keysById.Clear();
        _conflicts = new List<KeybindingConflict>();

        var userClaims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (keybinding, keys) in _userBindings)
        {
            if (!_definitions.ContainsKey(keybinding))
            {
                continue;
            }
            foreach (var key in NormalizeKeys(keys.Values))
            {
                if (!userClaims.TryGetValue(key, out var claimants))
                {
                    claimants = new List<string>();
                    userClaims[key] = claimants;
                }
                if (!claimants.Contains(keybinding, StringComparer.Ordinal))
                {
                    claimants.Add(keybinding);
                }
            }
        }

        foreach (var (key, claimants) in userClaims)
        {
            if (claimants.Count > 1)
            {
                _conflicts.Add(new KeybindingConflict(key, claimants));
            }
        }

        foreach (var (id, definition) in _definitions)
        {
            var keys = _userBindings.TryGetValue(id, out var userKeys)
                ? NormalizeKeys(userKeys.Values)
                : NormalizeKeys(definition.DefaultKeys.Values);
            _keysById[id] = keys.ToArray();
        }
    }

    /// <summary>Whether the raw input matches any key bound to <paramref name="keybinding"/>.</summary>
    public bool Matches(string data, string keybinding)
    {
        foreach (var key in _keysById.TryGetValue(keybinding, out var keys) ? keys : Array.Empty<string>())
        {
            if (Keys.MatchesKey(data, key))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The effective keys bound to <paramref name="keybinding"/>.</summary>
    public string[] GetKeys(string keybinding) => _keysById.TryGetValue(keybinding, out var keys) ? keys.ToArray() : Array.Empty<string>();

    /// <summary>The definition of <paramref name="keybinding"/>.</summary>
    public KeybindingDefinition GetDefinition(string keybinding) => _definitions[keybinding];

    /// <summary>Keys claimed by more than one user binding.</summary>
    public IReadOnlyList<KeybindingConflict> GetConflicts() => _conflicts
        .Select(conflict => new KeybindingConflict(conflict.Key, conflict.Keybindings.ToArray()))
        .ToArray();

    /// <summary>Replace the user overrides and rebuild the resolved table.</summary>
    public void SetUserBindings(KeybindingsConfig userBindings)
    {
        _userBindings = userBindings;
        Rebuild();
    }

    /// <summary>A copy of the current user overrides.</summary>
    public KeybindingsConfig GetUserBindings() => new(_userBindings);

    /// <summary>Every definition id mapped to its effective keys, using the TS single-vs-array shape.</summary>
    public KeybindingsConfig GetResolvedBindings()
    {
        var resolved = new KeybindingsConfig();
        foreach (var id in _definitions.Keys)
        {
            var keys = _keysById.TryGetValue(id, out var bound) ? bound : Array.Empty<string>();
            resolved[id] = keys.Length == 1 ? new KeybindingKeys(keys[0]) : new KeybindingKeys(keys);
        }
        return resolved;
    }
}

/// <summary>Global keybinding registry accessor (port of <c>setKeybindings</c> / <c>getKeybindings</c>).</summary>
public static class GlobalKeybindings
{
    private static KeybindingsManager? _global;

    public static void Set(KeybindingsManager keybindings) => _global = keybindings;

    public static KeybindingsManager Get() => _global ??= new KeybindingsManager(TuiKeybindings.Definitions);
}
