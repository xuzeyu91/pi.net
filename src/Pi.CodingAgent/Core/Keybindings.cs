using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>
/// The application keybinding ids and their default keys, layered on top of
/// <see cref="TuiKeybindings.Definitions"/>. Port of <c>core/keybindings.ts</c>.
/// </summary>
/// <remarks>
/// TS declares the app ids by interface declaration merging on top of the TUI's <c>Keybindings</c>
/// interface. C# has no declaration merging, so the ids are constants and the registry accepts arbitrary
/// string ids (the same approach <see cref="Pi.Tui.TuiKeybindingIds"/> already takes).
/// </remarks>
public static class AppKeybindings
{
    public const string AppInterrupt = "app.interrupt";
    public const string AppClear = "app.clear";
    public const string AppExit = "app.exit";
    public const string AppSuspend = "app.suspend";
    public const string AppThinkingCycle = "app.thinking.cycle";
    public const string AppThinkingSave = "app.thinking.save";
    public const string AppModelCycleForward = "app.model.cycleForward";
    public const string AppModelCycleBackward = "app.model.cycleBackward";
    public const string AppModelSelect = "app.model.select";
    public const string AppToolsExpand = "app.tools.expand";
    public const string AppThinkingToggle = "app.thinking.toggle";
    public const string AppSessionToggleNamedFilter = "app.session.toggleNamedFilter";
    public const string AppEditorExternal = "app.editor.external";
    public const string AppMessageCopy = "app.message.copy";
    public const string AppMessageFollowUp = "app.message.followUp";
    public const string AppMessageDequeue = "app.message.dequeue";
    public const string AppClipboardPasteImage = "app.clipboard.pasteImage";
    public const string AppSessionNew = "app.session.new";
    public const string AppSessionTree = "app.session.tree";
    public const string AppSessionFork = "app.session.fork";
    public const string AppSessionResume = "app.session.resume";
    public const string AppTreeFoldOrUp = "app.tree.foldOrUp";
    public const string AppTreeUnfoldOrDown = "app.tree.unfoldOrDown";
    public const string AppTreeEditLabel = "app.tree.editLabel";
    public const string AppTreeToggleLabelTimestamp = "app.tree.toggleLabelTimestamp";
    public const string AppSessionTogglePath = "app.session.togglePath";
    public const string AppSessionToggleSort = "app.session.toggleSort";
    public const string AppSessionRename = "app.session.rename";
    public const string AppSessionDelete = "app.session.delete";
    public const string AppSessionDeleteNoninvasive = "app.session.deleteNoninvasive";
    public const string AppModelsSave = "app.models.save";
    public const string AppModelsEnableAll = "app.models.enableAll";
    public const string AppModelsClearAll = "app.models.clearAll";
    public const string AppModelsToggleProvider = "app.models.toggleProvider";
    public const string AppModelsReorderUp = "app.models.reorderUp";
    public const string AppModelsReorderDown = "app.models.reorderDown";
    public const string AppTreeFilterDefault = "app.tree.filter.default";
    public const string AppTreeFilterNoTools = "app.tree.filter.noTools";
    public const string AppTreeFilterUserOnly = "app.tree.filter.userOnly";
    public const string AppTreeFilterLabeledOnly = "app.tree.filter.labeledOnly";
    public const string AppTreeFilterAll = "app.tree.filter.all";
    public const string AppTreeFilterCycleForward = "app.tree.filter.cycleForward";
    public const string AppTreeFilterCycleBackward = "app.tree.filter.cycleBackward";

    private static readonly string[] AppIdsInOrder =
    [
        AppInterrupt, AppClear, AppExit, AppSuspend, AppThinkingCycle, AppThinkingSave,
        AppModelCycleForward, AppModelCycleBackward, AppModelSelect, AppToolsExpand,
        AppThinkingToggle, AppSessionToggleNamedFilter, AppEditorExternal, AppMessageCopy,
        AppMessageFollowUp, AppMessageDequeue, AppClipboardPasteImage, AppSessionNew,
        AppSessionTree, AppSessionFork, AppSessionResume, AppTreeFoldOrUp, AppTreeUnfoldOrDown,
        AppTreeEditLabel, AppTreeToggleLabelTimestamp, AppSessionTogglePath, AppSessionToggleSort,
        AppSessionRename, AppSessionDelete, AppSessionDeleteNoninvasive, AppModelsSave,
        AppModelsEnableAll, AppModelsClearAll, AppModelsToggleProvider, AppModelsReorderUp,
        AppModelsReorderDown, AppTreeFilterDefault, AppTreeFilterNoTools, AppTreeFilterUserOnly,
        AppTreeFilterLabeledOnly, AppTreeFilterAll, AppTreeFilterCycleForward,
        AppTreeFilterCycleBackward,
    ];

    private static readonly IReadOnlyDictionary<string, KeybindingDefinition> BuiltDefinitions = Build();

    /// <summary>Whether the platform needs the Windows-flavored keybindings (Windows, or WSL).</summary>
    public static bool UseWindowsKeybindings(string? platform = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        platform ??= ProcessInfo.Platform;
        return platform == "win32"
            || (platform == "linux" && HasWslEnv(env));
    }

    private static bool HasWslEnv(IReadOnlyDictionary<string, string?>? env)
    {
        if (env is not null)
        {
            return !string.IsNullOrEmpty(env.GetValueOrDefault("WSL_DISTRO_NAME"))
                || !string.IsNullOrEmpty(env.GetValueOrDefault("WSL_INTEROP"));
        }
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP"));
    }

    /// <summary>The full binding table: TUI defaults with the four overrides, then the app ids.</summary>
    public static IReadOnlyDictionary<string, KeybindingDefinition> Definitions => BuiltDefinitions;

    /// <summary>Every binding id in table order, which is the order <c>orderKeybindingsConfig</c> uses.</summary>
    public static IReadOnlyList<string> OrderedIds { get; } = [.. BuiltDefinitions.Keys];

    private static Dictionary<string, KeybindingDefinition> Build()
    {
        var platform = ProcessInfo.Platform;
        var windows = UseWindowsKeybindings(platform);

        // The TS object literal spreads TUI_KEYBINDINGS first, so the four overridden ids keep their
        // original positions; a Dictionary built by insertion keeps them too.
        var definitions = new Dictionary<string, KeybindingDefinition>(StringComparer.Ordinal);
        foreach (var (id, definition) in TuiKeybindings.Definitions) definitions[id] = definition;

        definitions[TuiKeybindingIds.EditorUndo] =
            new KeybindingDefinition(Keys(platform == "win32" ? "ctrl+z" : windows ? "alt+z" : "ctrl+-"), "Undo");
        definitions[TuiKeybindingIds.AltScreenPreviousPrompt] = new KeybindingDefinition(
            windows ? Keys("ctrl+up") : Keys("ctrl+shift+up", "ctrl+up"),
            "Jump to previous semantic prompt");
        definitions[TuiKeybindingIds.AltScreenNextPrompt] = new KeybindingDefinition(
            windows ? Keys("ctrl+down") : Keys("ctrl+shift+down", "ctrl+down"),
            "Jump to next semantic prompt");
        definitions[TuiKeybindingIds.AltScreenSearch] = new KeybindingDefinition(
            Keys(windows ? "ctrl+f" : "ctrl+shift+f"),
            "Search the primary scroll view");

        definitions[AppInterrupt] = new KeybindingDefinition(Keys("escape"), "Cancel or abort");
        definitions[AppClear] = new KeybindingDefinition(Keys("ctrl+c"), "Clear editor");
        definitions[AppExit] = new KeybindingDefinition(Keys("ctrl+d"), "Exit when editor is empty");
        definitions[AppSuspend] = new KeybindingDefinition(
            platform == "win32" ? Keys() : Keys("ctrl+z"),
            "Suspend to background");
        definitions[AppThinkingCycle] = new KeybindingDefinition(Keys("shift+tab"), "Cycle thinking level");
        definitions[AppThinkingSave] = new KeybindingDefinition(Keys("ctrl+s"), "Save thinking level");
        definitions[AppModelCycleForward] = new KeybindingDefinition(Keys("ctrl+p"), "Cycle to next model");
        definitions[AppModelCycleBackward] = new KeybindingDefinition(
            Keys(windows ? "alt+p" : "shift+ctrl+p"),
            "Cycle to previous model");
        definitions[AppModelSelect] = new KeybindingDefinition(Keys("ctrl+l"), "Open model selector");
        definitions[AppToolsExpand] = new KeybindingDefinition(Keys("ctrl+o"), "Toggle tool output");
        definitions[AppThinkingToggle] = new KeybindingDefinition(Keys("ctrl+t"), "Toggle thinking blocks");
        definitions[AppSessionToggleNamedFilter] =
            new KeybindingDefinition(Keys("ctrl+n"), "Toggle named session filter");
        definitions[AppEditorExternal] = new KeybindingDefinition(Keys("ctrl+g"), "Open external editor");
        definitions[AppMessageCopy] =
            new KeybindingDefinition(Keys("ctrl+x"), "Copy selection or last assistant message");
        definitions[AppMessageFollowUp] = new KeybindingDefinition(
            Keys(windows ? "ctrl+q" : "alt+enter"),
            "Queue follow-up message");
        definitions[AppMessageDequeue] = new KeybindingDefinition(
            Keys(windows ? "alt+q" : "alt+up"),
            "Restore queued messages");
        definitions[AppClipboardPasteImage] = new KeybindingDefinition(
            Keys(windows ? "alt+v" : "ctrl+v"),
            "Paste files on macOS, images, or text from clipboard");
        definitions[AppSessionNew] = new KeybindingDefinition(Keys(), "Start a new session");
        definitions[AppSessionTree] = new KeybindingDefinition(Keys(), "Open session tree");
        definitions[AppSessionFork] = new KeybindingDefinition(Keys(), "Fork current session");
        definitions[AppSessionResume] = new KeybindingDefinition(Keys(), "Resume a session");
        definitions[AppTreeFoldOrUp] = new KeybindingDefinition(
            platform == "darwin" ? Keys("alt+left", "ctrl+left") : Keys("ctrl+left", "alt+left"),
            "Fold tree branch or move up");
        definitions[AppTreeUnfoldOrDown] = new KeybindingDefinition(
            platform == "darwin" ? Keys("alt+right", "ctrl+right") : Keys("ctrl+right", "alt+right"),
            "Unfold tree branch or move down");
        definitions[AppTreeEditLabel] = new KeybindingDefinition(Keys("shift+l"), "Edit tree label");
        definitions[AppTreeToggleLabelTimestamp] =
            new KeybindingDefinition(Keys("shift+t"), "Toggle tree label timestamps");
        definitions[AppSessionTogglePath] = new KeybindingDefinition(Keys("ctrl+p"), "Toggle session path display");
        definitions[AppSessionToggleSort] = new KeybindingDefinition(Keys("ctrl+s"), "Toggle session sort mode");
        definitions[AppSessionRename] = new KeybindingDefinition(Keys("ctrl+r"), "Rename session");
        definitions[AppSessionDelete] = new KeybindingDefinition(Keys("ctrl+d"), "Delete session");
        definitions[AppSessionDeleteNoninvasive] =
            new KeybindingDefinition(Keys("ctrl+backspace"), "Delete session when query is empty");
        definitions[AppModelsSave] = new KeybindingDefinition(Keys("ctrl+s"), "Save model selection");
        definitions[AppModelsEnableAll] = new KeybindingDefinition(Keys("ctrl+a"), "Enable all models");
        definitions[AppModelsClearAll] = new KeybindingDefinition(Keys("ctrl+x"), "Clear all models");
        definitions[AppModelsToggleProvider] =
            new KeybindingDefinition(Keys("ctrl+p"), "Toggle all models for provider");
        definitions[AppModelsReorderUp] = new KeybindingDefinition(Keys("alt+up"), "Move model up in order");
        definitions[AppModelsReorderDown] = new KeybindingDefinition(Keys("alt+down"), "Move model down in order");
        definitions[AppTreeFilterDefault] = new KeybindingDefinition(Keys("ctrl+d"), "Tree filter: default view");
        definitions[AppTreeFilterNoTools] = new KeybindingDefinition(Keys("ctrl+t"), "Tree filter: hide tool results");
        definitions[AppTreeFilterUserOnly] =
            new KeybindingDefinition(Keys("ctrl+u"), "Tree filter: user messages only");
        definitions[AppTreeFilterLabeledOnly] =
            new KeybindingDefinition(Keys("ctrl+l"), "Tree filter: labeled entries only");
        definitions[AppTreeFilterAll] = new KeybindingDefinition(Keys("ctrl+a"), "Tree filter: show all entries");
        definitions[AppTreeFilterCycleForward] =
            new KeybindingDefinition(Keys("ctrl+o"), "Tree filter: cycle forward");
        definitions[AppTreeFilterCycleBackward] =
            new KeybindingDefinition(Keys("shift+ctrl+o"), "Tree filter: cycle backward");

        return definitions;
    }

    private static KeybindingKeys Keys(params string[] keys) => new(keys);

    /// <summary>The app ids, in table order (exposed so callers can iterate them deterministically).</summary>
    public static IReadOnlyList<string> AppIds => AppIdsInOrder;
}

/// <summary>Legacy keybinding names and their current ids. Port of <c>KEYBINDING_NAME_MIGRATIONS</c>.</summary>
internal static class KeybindingNameMigrations
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cursorUp"] = "tui.editor.cursorUp",
        ["cursorDown"] = "tui.editor.cursorDown",
        ["cursorLeft"] = "tui.editor.cursorLeft",
        ["cursorRight"] = "tui.editor.cursorRight",
        ["cursorWordLeft"] = "tui.editor.cursorWordLeft",
        ["cursorWordRight"] = "tui.editor.cursorWordRight",
        ["cursorLineStart"] = "tui.editor.cursorLineStart",
        ["cursorLineEnd"] = "tui.editor.cursorLineEnd",
        ["jumpForward"] = "tui.editor.jumpForward",
        ["jumpBackward"] = "tui.editor.jumpBackward",
        ["pageUp"] = "tui.editor.pageUp",
        ["pageDown"] = "tui.editor.pageDown",
        ["deleteCharBackward"] = "tui.editor.deleteCharBackward",
        ["deleteCharForward"] = "tui.editor.deleteCharForward",
        ["deleteWordBackward"] = "tui.editor.deleteWordBackward",
        ["deleteWordForward"] = "tui.editor.deleteWordForward",
        ["deleteToLineStart"] = "tui.editor.deleteToLineStart",
        ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd",
        ["yank"] = "tui.editor.yank",
        ["yankPop"] = "tui.editor.yankPop",
        ["undo"] = "tui.editor.undo",
        ["newLine"] = "tui.input.newLine",
        ["submit"] = "tui.input.submit",
        ["tab"] = "tui.input.tab",
        ["copy"] = "tui.input.copy",
        ["selectUp"] = "tui.select.up",
        ["selectDown"] = "tui.select.down",
        ["selectPageUp"] = "tui.select.pageUp",
        ["selectPageDown"] = "tui.select.pageDown",
        ["selectConfirm"] = "tui.select.confirm",
        ["selectCancel"] = "tui.select.cancel",
        ["interrupt"] = "app.interrupt",
        ["clear"] = "app.clear",
        ["exit"] = "app.exit",
        ["suspend"] = "app.suspend",
        ["cycleThinkingLevel"] = "app.thinking.cycle",
        ["cycleModelForward"] = "app.model.cycleForward",
        ["cycleModelBackward"] = "app.model.cycleBackward",
        ["selectModel"] = "app.model.select",
        ["expandTools"] = "app.tools.expand",
        ["toggleThinking"] = "app.thinking.toggle",
        ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
        ["externalEditor"] = "app.editor.external",
        ["followUp"] = "app.message.followUp",
        ["dequeue"] = "app.message.dequeue",
        ["pasteImage"] = "app.clipboard.pasteImage",
        ["newSession"] = "app.session.new",
        ["tree"] = "app.session.tree",
        ["fork"] = "app.session.fork",
        ["resume"] = "app.session.resume",
        ["treeFoldOrUp"] = "app.tree.foldOrUp",
        ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
        ["treeEditLabel"] = "app.tree.editLabel",
        ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
        ["toggleSessionPath"] = "app.session.togglePath",
        ["toggleSessionSort"] = "app.session.toggleSort",
        ["renameSession"] = "app.session.rename",
        ["deleteSession"] = "app.session.delete",
        ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive",
    };
}

/// <summary>The outcome of migrating a raw keybindings config. Port of the TS return value.</summary>
public sealed record KeybindingsMigrationResult(JsonObject Config, bool Migrated);

/// <summary>Port of <c>core/keybindings.ts</c>.</summary>
public static class KeybindingsConfigFile
{
    /// <summary>Rewrite legacy binding names to their current ids, then order the result.</summary>
    public static KeybindingsMigrationResult MigrateKeybindingsConfig(JsonObject rawConfig)
    {
        var config = new JsonObject();
        var migrated = false;

        foreach (var (key, value) in rawConfig)
        {
            var nextKey = KeybindingNameMigrations.Map.TryGetValue(key, out var mapped) ? mapped : key;
            if (nextKey != key) migrated = true;
            if (key != nextKey && rawConfig.ContainsKey(nextKey))
            {
                migrated = true;
                continue;
            }
            config[nextKey] = value?.DeepClone();
        }

        return new KeybindingsMigrationResult(OrderKeybindingsConfig(config), migrated);
    }

    /// <summary>Reorder a config so known bindings come first in table order and extras follow, sorted.</summary>
    public static JsonObject OrderKeybindingsConfig(JsonObject config)
    {
        var ordered = new JsonObject();
        foreach (var keybinding in AppKeybindings.OrderedIds)
        {
            if (config.TryGetPropertyValue(keybinding, out var value)) ordered[keybinding] = value?.DeepClone();
        }

        var extras = config.Select(pair => pair.Key)
            .Where(key => !ordered.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal);
        foreach (var key in extras) ordered[key] = config[key]?.DeepClone();

        return ordered;
    }

    /// <summary>Read a raw config object from disk, or null when missing or unparsable.</summary>
    public static JsonObject? LoadRawConfig(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var parsed = JsonNode.Parse(Text.StripBom(File.ReadAllText(path)));
            return parsed as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keep only string and string-array entries, mirroring <c>toKeybindingsConfig</c>.</summary>
    public static KeybindingsConfig ToKeybindingsConfig(JsonObject value)
    {
        var config = new KeybindingsConfig();
        foreach (var (key, node) in value)
        {
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var single))
            {
                config[key] = new KeybindingKeys(single);
                continue;
            }
            if (node is not JsonArray array) continue;
            var keys = new List<string>(array.Count);
            var allStrings = true;
            foreach (var item in array)
            {
                if (item is JsonValue element && element.TryGetValue<string>(out var text)) keys.Add(text);
                else
                {
                    allStrings = false;
                    break;
                }
            }
            if (allStrings) config[key] = new KeybindingKeys(keys);
        }
        return config;
    }
}

/// <summary>
/// The application keybindings registry. Port of the <c>KeybindingsManager</c> subclass in
/// <c>core/keybindings.ts</c>.
/// </summary>
public class KeybindingsManager : Pi.Tui.KeybindingsManager
{
    private readonly string? _configPath;

    public KeybindingsManager(KeybindingsConfig? userBindings = null, string? configPath = null)
        : base(AppKeybindings.Definitions, userBindings ?? new KeybindingsConfig())
    {
        _configPath = configPath;
    }

    /// <summary>Create a manager backed by <c>&lt;agentDir&gt;/keybindings.json</c>.</summary>
    public static KeybindingsManager Create(string? agentDir = null)
    {
        var configPath = NodePath.Join(agentDir ?? Config.GetAgentDir(), "keybindings.json");
        return new KeybindingsManager(LoadFromFile(configPath), configPath);
    }

    /// <summary>Re-read the config file, if this manager was created with one.</summary>
    public void Reload()
    {
        if (_configPath is null) return;
        SetUserBindings(LoadFromFile(_configPath));
    }

    /// <summary>The effective config: every binding id mapped to its resolved keys.</summary>
    public KeybindingsConfig GetEffectiveConfig() => GetResolvedBindings();

    private static KeybindingsConfig LoadFromFile(string path)
    {
        var rawConfig = KeybindingsConfigFile.LoadRawConfig(path);
        if (rawConfig is null) return new KeybindingsConfig();
        return KeybindingsConfigFile.ToKeybindingsConfig(KeybindingsConfigFile.MigrateKeybindingsConfig(rawConfig).Config);
    }
}
