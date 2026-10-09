using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;
using Text = Pi.CodingAgent.Utils.Text;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Core;

/// <summary>Which settings file a scope names. Port of the TS <c>SettingsScope</c>.</summary>
public enum SettingsScope
{
    Global,
    Project,
}

/// <summary>A settings field that could not be loaded or persisted. Port of the TS <c>SettingsError</c>.</summary>
public sealed record SettingsError(SettingsScope Scope, Exception Error, string? Path = null);

/// <summary>Options for <see cref="SettingsManager.Create"/>. Port of <c>SettingsManagerCreateOptions</c>.</summary>
public sealed record SettingsManagerCreateOptions
{
    public bool? ProjectTrusted { get; init; }
}

/// <summary>
/// The settings read/write backend. Port of the TS <c>SettingsStorage</c>: <c>withLock</c> hands the
/// current file text to <paramref name="fn"/> and writes back a non-null return value.
/// </summary>
public interface ISettingsStorage
{
    void WithLock(SettingsScope scope, Func<string?, string?> fn);
}

/// <summary>Locks and reads/writes the real <c>settings.json</c> files. Port of <c>FileSettingsStorage</c>.</summary>
public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly string _globalSettingsPath;
    private readonly string _projectSettingsPath;

    public FileSettingsStorage(string cwd, string agentDir)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        var resolvedAgentDir = Paths.ResolvePath(agentDir);
        _globalSettingsPath = NodePath.Join(resolvedAgentDir, "settings.json");
        _projectSettingsPath = NodePath.Join(resolvedCwd, Config.ConfigDirName, "settings.json");
    }

    public string GlobalSettingsPath => _globalSettingsPath;

    public string ProjectSettingsPath => _projectSettingsPath;

    public void WithLock(SettingsScope scope, Func<string?, string?> fn)
    {
        var path = scope == SettingsScope.Global ? _globalSettingsPath : _projectSettingsPath;
        var directory = NodePath.Dirname(path);

        IDisposable? release = null;
        try
        {
            // Only create the directory and take the lock if the file exists or we need to write.
            var fileExists = File.Exists(path);
            if (fileExists) release = NodeLock.AcquireSync(path);
            var current = fileExists ? File.ReadAllText(path) : null;
            var next = fn(current);
            if (next is not null)
            {
                // Only create the directory when we actually need to write.
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                release ??= NodeLock.AcquireSync(path);
                File.WriteAllText(path, next);
            }
        }
        finally
        {
            release?.Dispose();
        }
    }
}

/// <summary>In-memory backend for tests and <see cref="SettingsManager.InMemory"/>. Port of <c>InMemorySettingsStorage</c>.</summary>
public sealed class InMemorySettingsStorage : ISettingsStorage
{
    private string? _global;
    private string? _project;

    public void WithLock(SettingsScope scope, Func<string?, string?> fn)
    {
        var current = scope == SettingsScope.Global ? _global : _project;
        var next = fn(current);
        if (next is null) return;
        if (scope == SettingsScope.Global) _global = next;
        else _project = next;
    }
}

/// <summary>A model identity used for per-model compaction overrides.</summary>
public sealed record CompactionModelRef(string Provider, string Id);

/// <summary>
/// The terminal capability overrides derived from settings. Port of the TS
/// <c>Partial&lt;TerminalCapabilities&gt;</c> return value, where an absent <c>images</c> means "no
/// override" and an explicit <c>null</c> means "force no images".
/// </summary>
public sealed record TerminalCapabilityOverrides
{
    public ImageProtocol? Images { get; init; }

    /// <summary>Whether <see cref="Images"/> carries a decision at all.</summary>
    public bool ImagesSpecified { get; init; }

    public bool? TrueColor { get; init; }

    public bool? Hyperlinks { get; init; }
}

/// <summary>
/// Port of <c>core/settings-manager.ts</c>: layered global/project settings with per-field dirty
/// tracking, a serialized write queue, and a lock around every file operation.
/// </summary>
/// <remarks>
/// <para>
/// Internally the three layers are kept as <see cref="JsonObject"/> rather than typed records, exactly
/// like the TS implementation keeps plain objects. That is what makes the deep merge, the "modified
/// field" replay into the on-disk file, and the permissiveness for malformed values behave identically.
/// The typed <see cref="Settings"/> record appears only at the public boundary.
/// </para>
/// <para>
/// Difference from TS: the write queue is a chain of <see cref="Task"/> continuations rather than
/// microtasks, so a queued write may run on a pool thread while the setter that enqueued it is still on
/// the stack. Every value the queued work touches is snapshotted before it is enqueued (as in TS), and
/// the modified-field sets are guarded by a lock.
/// </para>
/// </remarks>
public sealed class SettingsManager
{
    internal const long DefaultCompactionReserveTokens = 16384;
    internal const long DefaultCompactionKeepRecentTokens = 20000;
    internal const long DefaultBranchSummaryReserveTokens = 16384;
    internal const long DefaultProviderMaxRetryDelayMs = 60000;

    private static readonly string[] CacheWarmingModes = ["off", "streaming", "idle"];

    /// <summary>Tools enabled at startup when <c>defaultTools</c> does not change them.</summary>
    public static readonly IReadOnlyList<string> DefaultToolNames = ["read", "bash", "edit", "write"];

    private readonly ISettingsStorage _storage;
    private readonly Lock _stateLock = new();
    private readonly HashSet<string> _modifiedFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _modifiedNestedFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modifiedProjectFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _modifiedProjectNestedFields = new(StringComparer.Ordinal);
    private readonly List<SettingsError> _errors = [];
    private readonly Dictionary<SettingsScope, string> _settingsPaths = [];

    private JsonObject _globalSettings;
    private JsonObject _projectSettings;
    private JsonObject _settings;
    private bool _projectTrusted;
    private Exception? _globalSettingsLoadError;
    private Exception? _projectSettingsLoadError;
    private Task _writeQueue = Task.CompletedTask;

    private SettingsManager(
        ISettingsStorage storage,
        JsonObject initialGlobal,
        JsonObject initialProject,
        Exception? globalLoadError = null,
        Exception? projectLoadError = null,
        IEnumerable<SettingsError>? initialErrors = null,
        bool projectTrusted = true,
        IReadOnlyDictionary<SettingsScope, string>? settingsPaths = null)
    {
        _storage = storage;
        _globalSettings = initialGlobal;
        _projectSettings = initialProject;
        _projectTrusted = projectTrusted;
        _globalSettingsLoadError = globalLoadError;
        _projectSettingsLoadError = projectLoadError;
        if (initialErrors is not null) _errors.AddRange(initialErrors);
        if (settingsPaths is not null)
        {
            foreach (var (scope, path) in settingsPaths) _settingsPaths[scope] = path;
        }
        _settings = DeepMergeSettings(_globalSettings, _projectSettings);
    }

    /// <summary>Create a manager backed by the real settings files.</summary>
    public static SettingsManager Create(
        string cwd,
        string? agentDir = null,
        SettingsManagerCreateOptions? options = null)
    {
        options ??= new SettingsManagerCreateOptions();
        var resolvedCwd = Paths.ResolvePath(cwd);
        var resolvedAgentDir = Paths.ResolvePath(agentDir ?? Config.GetAgentDir());
        var storage = new FileSettingsStorage(resolvedCwd, resolvedAgentDir);
        return FromStorageWithPaths(storage, options, new Dictionary<SettingsScope, string>
        {
            [SettingsScope.Global] = NodePath.Join(resolvedAgentDir, "settings.json"),
            [SettingsScope.Project] = NodePath.Join(resolvedCwd, Config.ConfigDirName, "settings.json"),
        });
    }

    /// <summary>Create a manager from an arbitrary storage backend.</summary>
    public static SettingsManager FromStorage(ISettingsStorage storage, SettingsManagerCreateOptions? options = null)
        => FromStorageWithPaths(storage, options ?? new SettingsManagerCreateOptions(), null);

    /// <summary>Create an in-memory manager (no file I/O).</summary>
    public static SettingsManager InMemory(Settings? settings = null, SettingsManagerCreateOptions? options = null)
    {
        var storage = new InMemorySettingsStorage();
        var initialSettings = MigrateSettings(ToJsonObject(settings ?? new Settings()));
        storage.WithLock(SettingsScope.Global, _ => SettingsJson.Stringify(initialSettings));
        return FromStorage(storage, options);
    }

    private static SettingsManager FromStorageWithPaths(
        ISettingsStorage storage,
        SettingsManagerCreateOptions options,
        IReadOnlyDictionary<SettingsScope, string>? settingsPaths)
    {
        var projectTrusted = options.ProjectTrusted ?? true;
        var globalLoad = TryLoadFromStorage(storage, SettingsScope.Global);
        var projectLoad = TryLoadFromStorage(storage, SettingsScope.Project, projectTrusted);

        var initialErrors = new List<SettingsError>();
        if (globalLoad.Error is not null)
        {
            initialErrors.Add(ToSettingsError(SettingsScope.Global, globalLoad.Error, settingsPaths?.GetValueOrDefault(SettingsScope.Global)));
        }
        if (projectLoad.Error is not null)
        {
            initialErrors.Add(ToSettingsError(SettingsScope.Project, projectLoad.Error, settingsPaths?.GetValueOrDefault(SettingsScope.Project)));
        }

        return new SettingsManager(
            storage,
            globalLoad.Settings,
            projectLoad.Settings,
            globalLoad.Error,
            projectLoad.Error,
            initialErrors,
            projectTrusted,
            settingsPaths);
    }

    private static SettingsError ToSettingsError(SettingsScope scope, Exception error, string? path)
        => new(scope, error, path);

    private static (JsonObject Settings, Exception? Error) TryLoadFromStorage(
        ISettingsStorage storage,
        SettingsScope scope,
        bool projectTrusted = true)
    {
        try
        {
            return (LoadFromStorage(storage, scope, projectTrusted), null);
        }
        catch (Exception error)
        {
            return (new JsonObject(), error);
        }
    }

    private static JsonObject LoadFromStorage(ISettingsStorage storage, SettingsScope scope, bool projectTrusted = true)
    {
        if (scope == SettingsScope.Project && !projectTrusted) return new JsonObject();

        string? content = null;
        storage.WithLock(scope, current =>
        {
            content = current;
            return null;
        });

        if (string.IsNullOrEmpty(content)) return new JsonObject();
        return MigrateSettings(ParseObject(Text.StripBom(content)));
    }

    /// <summary>Parse settings text into an object, mirroring <c>JSON.parse</c> throwing on a non-object.</summary>
    internal static JsonObject ParseObject(string text)
    {
        var node = JsonNode.Parse(text);
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>Migrate old settings shapes to the current one. Port of <c>migrateSettings</c>.</summary>
    internal static JsonObject MigrateSettings(JsonObject settings)
    {
        // queueMode -> steeringMode
        if (settings.ContainsKey("queueMode") && !settings.ContainsKey("steeringMode"))
        {
            settings["steeringMode"] = settings["queueMode"]?.DeepClone();
            settings.Remove("queueMode");
        }

        // Legacy websockets boolean -> transport enum
        if (!settings.ContainsKey("transport")
            && settings["websockets"] is JsonValue websocketsValue
            && websocketsValue.TryGetValue(out bool websockets))
        {
            settings["transport"] = websockets ? "websocket" : "sse";
            settings.Remove("websockets");
        }

        // Old skills object format -> array format
        if (settings["skills"] is JsonObject skillsSettings)
        {
            if (skillsSettings["enableSkillCommands"] is { } enableSkillCommands
                && !settings.ContainsKey("enableSkillCommands"))
            {
                settings["enableSkillCommands"] = enableSkillCommands.DeepClone();
            }
            if (skillsSettings["customDirectories"] is JsonArray { Count: > 0 } customDirectories)
            {
                settings["skills"] = customDirectories.DeepClone();
            }
            else
            {
                settings.Remove("skills");
            }
        }

        // retry.maxDelayMs -> retry.provider.maxRetryDelayMs
        if (settings["retry"] is JsonObject retrySettings)
        {
            var providerSettings = retrySettings["provider"] as JsonObject;
            if (retrySettings["maxDelayMs"] is JsonValue maxDelayValue
                && maxDelayValue.TryGetValue(out double _)
                && (providerSettings is null || providerSettings["maxRetryDelayMs"] is null))
            {
                var merged = providerSettings is null ? new JsonObject() : (JsonObject)providerSettings.DeepClone();
                merged["maxRetryDelayMs"] = retrySettings["maxDelayMs"]!.DeepClone();
                retrySettings["provider"] = merged;
            }
            retrySettings.Remove("maxDelayMs");
        }

        return settings;
    }

    /// <summary>A deep copy of the effective settings (global and project merged).</summary>
    public Settings GetSettings() => ToSettings(_settings);

    public Settings GetGlobalSettings() => ToSettings(_globalSettings);

    public Settings GetProjectSettings() => ToSettings(_projectSettings);

    public bool IsProjectTrusted() => _projectTrusted;

    public void SetProjectTrusted(bool trusted)
    {
        if (_projectTrusted == trusted) return;

        _projectTrusted = trusted;
        lock (_stateLock)
        {
            _modifiedProjectFields.Clear();
            _modifiedProjectNestedFields.Clear();
        }

        if (!trusted)
        {
            _projectSettings = new JsonObject();
            _projectSettingsLoadError = null;
            _settings = DeepMergeSettings(_globalSettings, _projectSettings);
            return;
        }

        var projectLoad = TryLoadFromStorage(_storage, SettingsScope.Project, trusted);
        _projectSettings = projectLoad.Settings;
        _projectSettingsLoadError = projectLoad.Error;
        if (projectLoad.Error is not null) RecordError(SettingsScope.Project, projectLoad.Error);
        _settings = DeepMergeSettings(_globalSettings, _projectSettings);
    }

    /// <summary>Re-read both settings files, discarding in-session modifications.</summary>
    public async Task ReloadAsync()
    {
        await _writeQueue.ConfigureAwait(false);
        var globalLoad = TryLoadFromStorage(_storage, SettingsScope.Global);
        if (globalLoad.Error is null)
        {
            _globalSettings = globalLoad.Settings;
            _globalSettingsLoadError = null;
        }
        else
        {
            _globalSettingsLoadError = globalLoad.Error;
            RecordError(SettingsScope.Global, globalLoad.Error);
        }

        lock (_stateLock)
        {
            _modifiedFields.Clear();
            _modifiedNestedFields.Clear();
            _modifiedProjectFields.Clear();
            _modifiedProjectNestedFields.Clear();
        }

        var projectLoad = TryLoadFromStorage(_storage, SettingsScope.Project, _projectTrusted);
        if (projectLoad.Error is null)
        {
            _projectSettings = projectLoad.Settings;
            _projectSettingsLoadError = null;
        }
        else
        {
            _projectSettingsLoadError = projectLoad.Error;
            RecordError(SettingsScope.Project, projectLoad.Error);
        }

        _settings = DeepMergeSettings(_globalSettings, _projectSettings);
    }

    /// <summary>Apply additional overrides on top of the current settings.</summary>
    public void ApplyOverrides(Settings overrides)
        => _settings = DeepMergeSettings(_settings, ToJsonObject(overrides));

    /// <summary>Wait for every queued write to finish.</summary>
    public async Task FlushAsync() => await _writeQueue.ConfigureAwait(false);

    /// <summary>Take the recorded errors, clearing the list.</summary>
    public IReadOnlyList<SettingsError> DrainErrors()
    {
        lock (_stateLock)
        {
            var drained = _errors.ToArray();
            _errors.Clear();
            return drained;
        }
    }

    // ---- deep merge -------------------------------------------------------

    /// <summary>Deep merge settings: project/overrides take precedence, nested objects merge recursively.</summary>
    internal static JsonObject DeepMergeSettings(JsonObject baseSettings, JsonObject overrides)
    {
        var merged = DeepMergeObjects(baseSettings, overrides);
        if (overrides.ContainsKey("defaultTools"))
        {
            var tools = MergeDefaultTools(baseSettings["defaultTools"], overrides["defaultTools"]);
            if (tools is null) merged.Remove("defaultTools");
            else merged["defaultTools"] = tools.DeepClone();
        }
        return merged;
    }

    /// <summary>Recursive object merge. Arrays and scalars are replaced wholesale.</summary>
    internal static JsonObject DeepMergeObjects(JsonObject baseObject, JsonObject overrides)
    {
        var result = (JsonObject)baseObject.DeepClone();
        foreach (var (key, overrideValue) in overrides)
        {
            var baseValue = result[key];
            result[key] = baseValue is JsonObject baseChild && overrideValue is JsonObject overrideChild
                ? DeepMergeObjects(baseChild, overrideChild)
                : overrideValue?.DeepClone();
        }
        return result;
    }

    private static bool IsToolModifier(JsonNode? entry)
        => entry is JsonValue value && value.TryGetValue<string>(out var text)
            && text.Length > 0 && text[0] is '+' or '-';

    /// <summary>
    /// Merge two <c>defaultTools</c> lists: a list with plain tool names replaces the inherited one, a
    /// list of only <c>+name</c>/<c>-name</c> entries is appended.
    /// </summary>
    private static JsonNode? MergeDefaultTools(JsonNode? baseNode, JsonNode? overrideNode)
    {
        if (baseNode is not JsonArray baseArray
            || overrideNode is not JsonArray overrideArray
            || !overrideArray.All(IsToolModifier))
        {
            return overrideNode;
        }

        var result = new JsonArray();
        foreach (var item in baseArray) result.Add(item?.DeepClone());
        foreach (var item in overrideArray) result.Add(item?.DeepClone());
        return result;
    }

    /// <summary>
    /// Resolve a merged <c>defaultTools</c> list: plain names replace <see cref="DefaultToolNames"/>, then
    /// <c>+name</c> adds and <c>-name</c> removes a tool, in list order.
    /// </summary>
    internal static IReadOnlyList<string> ResolveDefaultTools(IReadOnlyList<string> entries)
    {
        var plain = entries.Where(entry => !IsToolModifierText(entry)).ToList();
        var tools = plain.Count > 0 || entries.Count == 0 ? plain : [.. DefaultToolNames];
        foreach (var entry in entries)
        {
            if (!IsToolModifierText(entry)) continue;
            var name = entry[1..];
            var index = tools.IndexOf(name);
            if (entry[0] == '+' && index == -1 && name.Length > 0) tools.Add(name);
            else if (entry[0] == '-' && index != -1) tools.RemoveAt(index);
        }
        return tools;
    }

    private static bool IsToolModifierText(string entry)
        => entry.Length > 0 && (entry[0] == '+' || entry[0] == '-');

    // ---- serialization boundary -------------------------------------------

    private static readonly JsonSerializerOptions DtoOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static JsonObject ToJsonObject(Settings settings)
        => JsonSerializer.SerializeToNode(settings, DtoOptions) as JsonObject ?? new JsonObject();

    internal static Settings ToSettings(JsonObject json)
        => JsonSerializer.Deserialize<Settings>(json.ToJsonString(), DtoOptions) ?? new Settings();

    // ---- typed reads ------------------------------------------------------

    private JsonNode? Raw(string key) => _settings[key];

    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool AsBool(JsonNode? node, bool fallback)
        => node is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean : fallback;

    private static bool? AsNullableBool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean : null;

    private static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        return null;
    }

    private static IReadOnlyList<string> AsStringArray(JsonNode? node)
        => node is JsonArray array
            ? array.Where(item => item is JsonValue).Select(item => AsString(item) ?? string.Empty).ToList()
            : [];

    private static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    /// <summary>JS <c>String(value)</c> for a JSON value, used in validation error messages.</summary>
    internal static string JsText(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean ? "true" : "false",
        JsonValue value when value.TryGetValue<double>(out var number) => JsNumberText(number),
        _ => node.ToJsonString(),
    };

    private static string JsNumberText(double number)
    {
        if (double.IsNaN(number)) return "NaN";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == Math.Floor(number) && Math.Abs(number) < 1e21)
        {
            return ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- modified tracking ------------------------------------------------

    private void MarkModified(string field, string? nestedKey = null)
    {
        lock (_stateLock)
        {
            _modifiedFields.Add(field);
            if (nestedKey is null) return;
            if (!_modifiedNestedFields.TryGetValue(field, out var nested))
            {
                nested = new HashSet<string>(StringComparer.Ordinal);
                _modifiedNestedFields[field] = nested;
            }
            nested.Add(nestedKey);
        }
    }

    private void MarkProjectModified(string field, string? nestedKey = null)
    {
        lock (_stateLock)
        {
            _modifiedProjectFields.Add(field);
            if (nestedKey is null) return;
            if (!_modifiedProjectNestedFields.TryGetValue(field, out var nested))
            {
                nested = new HashSet<string>(StringComparer.Ordinal);
                _modifiedProjectNestedFields[field] = nested;
            }
            nested.Add(nestedKey);
        }
    }

    private void AssertProjectTrustedForWrite()
    {
        if (!_projectTrusted)
        {
            throw new InvalidOperationException("Project is not trusted; refusing to write project settings");
        }
    }

    private void RecordError(SettingsScope scope, Exception error)
    {
        lock (_stateLock)
        {
            _errors.Add(ToSettingsError(scope, error, _settingsPaths.GetValueOrDefault(scope)));
        }
    }

    private void ClearModifiedScope(SettingsScope scope)
    {
        lock (_stateLock)
        {
            if (scope == SettingsScope.Global)
            {
                _modifiedFields.Clear();
                _modifiedNestedFields.Clear();
                return;
            }
            _modifiedProjectFields.Clear();
            _modifiedProjectNestedFields.Clear();
        }
    }

    private void EnqueueWrite(SettingsScope scope, Action task)
    {
        _writeQueue = _writeQueue.ContinueWith(
            _ =>
            {
                try
                {
                    if (scope == SettingsScope.Project) AssertProjectTrustedForWrite();
                    task();
                    ClearModifiedScope(scope);
                }
                catch (Exception error)
                {
                    RecordError(scope, error);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static Dictionary<string, HashSet<string>> SnapshotNested(IReadOnlyDictionary<string, HashSet<string>> source)
    {
        var snapshot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in source) snapshot[key] = new HashSet<string>(value, StringComparer.Ordinal);
        return snapshot;
    }

    private void PersistScopedSettings(
        SettingsScope scope,
        JsonObject snapshotSettings,
        IReadOnlyCollection<string> modifiedFields,
        IReadOnlyDictionary<string, HashSet<string>> modifiedNestedFields)
    {
        _storage.WithLock(scope, current =>
        {
            var currentFileSettings = string.IsNullOrEmpty(current)
                ? new JsonObject()
                : MigrateSettings(ParseObject(Text.StripBom(current)));
            var mergedSettings = (JsonObject)currentFileSettings.DeepClone();

            foreach (var field in modifiedFields)
            {
                if (modifiedNestedFields.TryGetValue(field, out var nestedModified)
                    && snapshotSettings[field] is JsonObject inMemoryNested)
                {
                    var mergedNested = currentFileSettings[field] is JsonObject baseNested
                        ? (JsonObject)baseNested.DeepClone()
                        : new JsonObject();
                    foreach (var nestedKey in nestedModified)
                    {
                        if (inMemoryNested.TryGetPropertyValue(nestedKey, out var node)) mergedNested[nestedKey] = node?.DeepClone();
                        else mergedNested.Remove(nestedKey);
                    }
                    mergedSettings[field] = mergedNested;
                }
                else
                {
                    if (snapshotSettings.TryGetPropertyValue(field, out var value)) mergedSettings[field] = value?.DeepClone();
                    else mergedSettings.Remove(field);
                }
            }

            return SettingsJson.Stringify(mergedSettings);
        });
    }

    private void Save()
    {
        _settings = DeepMergeSettings(_globalSettings, _projectSettings);

        if (_globalSettingsLoadError is not null) return;

        var snapshotGlobalSettings = (JsonObject)_globalSettings.DeepClone();
        List<string> modifiedFields;
        Dictionary<string, HashSet<string>> modifiedNestedFields;
        lock (_stateLock)
        {
            modifiedFields = [.. _modifiedFields];
            modifiedNestedFields = SnapshotNested(_modifiedNestedFields);
        }

        EnqueueWrite(SettingsScope.Global,
            () => PersistScopedSettings(SettingsScope.Global, snapshotGlobalSettings, modifiedFields, modifiedNestedFields));
    }

    private void SaveProjectSettings(JsonObject settings)
    {
        AssertProjectTrustedForWrite();
        _projectSettings = (JsonObject)settings.DeepClone();
        _settings = DeepMergeSettings(_globalSettings, _projectSettings);

        if (_projectSettingsLoadError is not null) return;

        var snapshotProjectSettings = (JsonObject)_projectSettings.DeepClone();
        List<string> modifiedFields;
        Dictionary<string, HashSet<string>> modifiedNestedFields;
        lock (_stateLock)
        {
            modifiedFields = [.. _modifiedProjectFields];
            modifiedNestedFields = SnapshotNested(_modifiedProjectNestedFields);
        }

        EnqueueWrite(SettingsScope.Project,
            () => PersistScopedSettings(SettingsScope.Project, snapshotProjectSettings, modifiedFields, modifiedNestedFields));
    }

    private void UpdateProjectSettings(string field, Action<JsonObject> update)
    {
        AssertProjectTrustedForWrite();
        var projectSettings = (JsonObject)_projectSettings.DeepClone();
        update(projectSettings);
        MarkProjectModified(field);
        SaveProjectSettings(projectSettings);
    }

    // ---- helpers for nested global writes ---------------------------------

    private JsonObject EnsureGlobalObject(string key)
    {
        if (_globalSettings[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        _globalSettings[key] = created;
        return created;
    }

    // ---- simple getters/setters -------------------------------------------

    public string? GetLastChangelogVersion() => AsString(Raw("lastChangelogVersion"));

    public void SetLastChangelogVersion(string version)
    {
        _globalSettings["lastChangelogVersion"] = version;
        MarkModified("lastChangelogVersion");
        Save();
    }

    public string? GetSessionDir()
    {
        var sessionDir = AsString(Raw("sessionDir"));
        return string.IsNullOrEmpty(sessionDir) ? sessionDir : Paths.NormalizePath(sessionDir);
    }

    public string? GetDefaultProvider() => AsString(Raw("defaultProvider"));

    public string? GetDefaultModel() => AsString(Raw("defaultModel"));

    public void SetDefaultProvider(string provider)
    {
        _globalSettings["defaultProvider"] = provider;
        MarkModified("defaultProvider");
        Save();
    }

    public void SetDefaultModel(string modelId)
    {
        _globalSettings["defaultModel"] = modelId;
        MarkModified("defaultModel");
        Save();
    }

    public void SetDefaultModelAndProvider(string provider, string modelId)
    {
        _globalSettings["defaultProvider"] = provider;
        _globalSettings["defaultModel"] = modelId;
        MarkModified("defaultProvider");
        MarkModified("defaultModel");
        Save();
    }

    public string GetSteeringMode() => AsString(Raw("steeringMode")) is { Length: > 0 } mode ? mode : "one-at-a-time";

    public void SetSteeringMode(string mode)
    {
        _globalSettings["steeringMode"] = mode;
        MarkModified("steeringMode");
        Save();
    }

    public string GetFollowUpMode() => AsString(Raw("followUpMode")) is { Length: > 0 } mode ? mode : "one-at-a-time";

    public void SetFollowUpMode(string mode)
    {
        _globalSettings["followUpMode"] = mode;
        MarkModified("followUpMode");
        Save();
    }

    public string? GetThemeSetting() => AsString(Raw("theme"));

    public string? GetTheme()
    {
        var theme = GetThemeSetting();
        return theme?.Contains('/') == true ? null : theme;
    }

    public void SetTheme(string theme)
    {
        _globalSettings["theme"] = theme;
        MarkModified("theme");
        Save();
    }

    public string? GetDefaultThinkingLevel() => AsString(Raw("defaultThinkingLevel"));

    public void SetDefaultThinkingLevel(ThinkingLevel level)
    {
        _globalSettings["defaultThinkingLevel"] = ThinkingLevels.ToText(level);
        MarkModified("defaultThinkingLevel");
        Save();
    }

    public string? GetModelThinkingLevel(string provider, string modelId)
        => AsString(AsObject(Raw("modelThinkingLevels"))?[$"{provider}/{modelId}"]);

    public IReadOnlyDictionary<string, string> GetAllModelThinkingLevels()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (AsObject(Raw("modelThinkingLevels")) is not { } levels) return result;
        foreach (var (key, value) in levels)
        {
            if (AsString(value) is { } text) result[key] = text;
        }
        return result;
    }

    public void SetModelThinkingLevel(string provider, string modelId, ThinkingLevel level)
    {
        EnsureGlobalObject("modelThinkingLevels")[$"{provider}/{modelId}"] = ThinkingLevels.ToText(level);
        MarkModified("modelThinkingLevels");
        Save();
    }

    public void RemoveModelThinkingLevel(string provider, string modelId)
    {
        if (_globalSettings["modelThinkingLevels"] is not JsonObject levels) return;
        levels.Remove($"{provider}/{modelId}");
        if (levels.Count == 0) _globalSettings.Remove("modelThinkingLevels");
        MarkModified("modelThinkingLevels");
        Save();
    }

    public string GetTransport() => AsString(Raw("transport")) ?? "auto";

    public void SetTransport(string transport)
    {
        _globalSettings["transport"] = transport;
        MarkModified("transport");
        Save();
    }

    // ---- compaction -------------------------------------------------------

    public bool GetCompactionEnabled() => AsBool(AsObject(Raw("compaction"))?["enabled"], true);

    public void SetCompactionEnabled(bool enabled)
    {
        EnsureGlobalObject("compaction")["enabled"] = enabled;
        MarkModified("compaction", "enabled");
        Save();
    }

    private long GetCompactionTokenSetting(string field, CompactionModelRef? model)
    {
        var compaction = AsObject(Raw("compaction"));
        var ordinary = compaction?[field];
        if (ordinary is not null && !IsNonNegativeSafeInteger(ordinary))
        {
            throw new InvalidOperationException(
                $"Invalid compaction.{field} setting: {JsText(ordinary)}. Expected a non-negative safe integer.");
        }

        var modelKey = model is null ? null : $"{model.Provider}/{model.Id}";
        var entry = modelKey is null ? null : AsObject(compaction?["modelOverrides"])?[modelKey];
        if (entry is not null && entry is not JsonObject)
        {
            throw new InvalidOperationException(
                $"Invalid compaction.modelOverrides[\"{modelKey}\"] setting: {JsText(entry)}. Expected an object.");
        }

        var overrideNode = (entry as JsonObject)?[field];
        if (overrideNode is not null && !IsNonNegativeSafeInteger(overrideNode))
        {
            throw new InvalidOperationException(
                $"Invalid compaction.modelOverrides[\"{modelKey}\"].{field} setting: {JsText(overrideNode)}. Expected a non-negative safe integer.");
        }

        if (overrideNode is not null) return (long)AsNumber(overrideNode)!.Value;
        if (ordinary is not null) return (long)AsNumber(ordinary)!.Value;
        return field == "reserveTokens" ? DefaultCompactionReserveTokens : DefaultCompactionKeepRecentTokens;
    }

    private static bool IsNonNegativeSafeInteger(JsonNode node)
    {
        var number = AsNumber(node);
        return number is not null && number.Value >= 0 && number.Value <= 9007199254740991
            && number.Value == Math.Floor(number.Value);
    }

    public long GetCompactionReserveTokens(CompactionModelRef? model = null)
        => GetCompactionTokenSetting("reserveTokens", model);

    public long GetCompactionKeepRecentTokens(CompactionModelRef? model = null)
        => GetCompactionTokenSetting("keepRecentTokens", model);

    /// <summary>Resolve each token setting through model override, ordinary setting, then built-in default.</summary>
    public (bool Enabled, long ReserveTokens, long KeepRecentTokens) GetCompactionSettings(CompactionModelRef? model = null)
        => (GetCompactionEnabled(), GetCompactionReserveTokens(model), GetCompactionKeepRecentTokens(model));

    public (long ReserveTokens, bool SkipPrompt) GetBranchSummarySettings()
    {
        var branchSummary = AsObject(Raw("branchSummary"));
        return (
            (long)(AsNumber(branchSummary?["reserveTokens"]) ?? DefaultBranchSummaryReserveTokens),
            AsBool(branchSummary?["skipPrompt"], false));
    }

    public bool GetBranchSummarySkipPrompt() => AsBool(AsObject(Raw("branchSummary"))?["skipPrompt"], false);

    // ---- retry ------------------------------------------------------------

    public bool GetRetryEnabled() => AsBool(AsObject(Raw("retry"))?["enabled"], true);

    public void SetRetryEnabled(bool enabled)
    {
        EnsureGlobalObject("retry")["enabled"] = enabled;
        MarkModified("retry", "enabled");
        Save();
    }

    public (bool Enabled, int MaxRetries, long BaseDelayMs, long MaxAgentDelayMs) GetRetrySettings()
    {
        var retry = AsObject(Raw("retry"));
        return (
            GetRetryEnabled(),
            (int)(AsNumber(retry?["maxRetries"]) ?? 3),
            (long)(AsNumber(retry?["baseDelayMs"]) ?? 2000),
            (long)(AsNumber(retry?["maxAgentDelayMs"]) ?? Retry.DefaultMaxAgentRetryDelayMs));
    }

    public (long? TimeoutMs, int? MaxRetries, long MaxRetryDelayMs) GetProviderRetrySettings()
    {
        var provider = AsObject(AsObject(Raw("retry"))?["provider"]);
        var timeout = AsNumber(provider?["timeoutMs"]);
        var maxRetries = AsNumber(provider?["maxRetries"]);
        return (
            timeout is null ? null : (long)timeout.Value,
            maxRetries is null ? null : (int)maxRetries.Value,
            (long)(AsNumber(provider?["maxRetryDelayMs"]) ?? DefaultProviderMaxRetryDelayMs));
    }

    // ---- timeouts ---------------------------------------------------------

    public long GetHttpIdleTimeoutMs()
        => ParseTimeoutSetting(Raw("httpIdleTimeoutMs"), "httpIdleTimeoutMs") ?? HttpDispatcher.DefaultHttpIdleTimeoutMs;

    public void SetHttpIdleTimeoutMs(double timeoutMs)
    {
        if (!double.IsFinite(timeoutMs) || timeoutMs < 0)
        {
            throw new InvalidOperationException($"Invalid httpIdleTimeoutMs setting: {JsNumberText(timeoutMs)}");
        }
        _globalSettings["httpIdleTimeoutMs"] = Math.Floor(timeoutMs);
        MarkModified("httpIdleTimeoutMs");
        Save();
    }

    public long? GetWebSocketConnectTimeoutMs()
        => ParseTimeoutSetting(Raw("websocketConnectTimeoutMs"), "websocketConnectTimeoutMs");

    private static long? ParseTimeoutSetting(JsonNode? value, string settingName)
    {
        var timeoutMs = HttpDispatcher.ParseHttpIdleTimeoutMs(ToPlainValue(value));
        if (timeoutMs is not null) return timeoutMs;
        if (value is not null) throw new InvalidOperationException($"Invalid {settingName} setting: {JsText(value)}");
        return null;
    }

    private static object? ToPlainValue(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<bool>(out var boolean)) return boolean;
        return null;
    }

    /// <summary>Read from global settings only because warming costs money.</summary>
    public string GetCacheWarmingMode()
    {
        var mode = AsString(_globalSettings["cacheWarming"]);
        return mode is not null && CacheWarmingModes.Contains(mode, StringComparer.Ordinal) ? mode : "streaming";
    }

    public void SetCacheWarmingMode(string mode)
    {
        _globalSettings["cacheWarming"] = mode;
        MarkModified("cacheWarming");
        Save();
    }

    // ---- misc booleans and strings ----------------------------------------

    public bool GetHideThinkingBlock() => AsBool(Raw("hideThinkingBlock"), false);

    public bool GetShowCacheMissNotices() => AsBool(Raw("showCacheMissNotices"), false);

    public string GetExternalEditorCommand()
    {
        var configuredEditor = AsString(Raw("externalEditor"));
        if (configuredEditor is not null && configuredEditor.Trim().Length > 0) return configuredEditor;

        var environmentEditor = Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrEmpty(environmentEditor)) return environmentEditor;

        return NodePath.IsWindows ? "notepad" : "nano";
    }

    public void SetHideThinkingBlock(bool hide)
    {
        _globalSettings["hideThinkingBlock"] = hide;
        MarkModified("hideThinkingBlock");
        Save();
    }

    public void SetShowCacheMissNotices(bool show)
    {
        _globalSettings["showCacheMissNotices"] = show;
        MarkModified("showCacheMissNotices");
        Save();
    }

    public string? GetShellPath()
    {
        var shellPath = AsString(Raw("shellPath"));
        return string.IsNullOrEmpty(shellPath) ? shellPath : Paths.NormalizePath(shellPath);
    }

    public void SetShellPath(string? path)
    {
        _globalSettings["shellPath"] = path;
        MarkModified("shellPath");
        Save();
    }

    public string GetQuietStartup()
    {
        var value = Raw("quietStartup");
        if (AsBool(value, false)) return "true";
        return AsString(value) == "header" ? "header" : "false";
    }

    public void SetQuietStartup(bool quiet)
    {
        _globalSettings["quietStartup"] = quiet;
        MarkModified("quietStartup");
        Save();
    }

    public void SetQuietStartupHeader()
    {
        _globalSettings["quietStartup"] = "header";
        MarkModified("quietStartup");
        Save();
    }

    public string GetDefaultProjectTrust()
    {
        var value = AsString(_globalSettings["defaultProjectTrust"]);
        return value is "always" or "never" ? value : "ask";
    }

    public void SetDefaultProjectTrust(string defaultProjectTrust)
    {
        _globalSettings["defaultProjectTrust"] = defaultProjectTrust;
        MarkModified("defaultProjectTrust");
        Save();
    }

    public string? GetShellCommandPrefix() => AsString(Raw("shellCommandPrefix"));

    public void SetShellCommandPrefix(string? prefix)
    {
        _globalSettings["shellCommandPrefix"] = prefix;
        MarkModified("shellCommandPrefix");
        Save();
    }

    public IReadOnlyList<string>? GetNpmCommand()
        => Raw("npmCommand") is JsonArray array ? [.. AsStringArray(array)] : null;

    public void SetNpmCommand(IReadOnlyList<string>? command)
    {
        if (command is null) _globalSettings.Remove("npmCommand");
        else
        {
            var array = new JsonArray();
            foreach (var item in command) array.Add(item);
            _globalSettings["npmCommand"] = array;
        }
        MarkModified("npmCommand");
        Save();
    }

    public bool GetCollapseChangelog() => AsBool(Raw("collapseChangelog"), false);

    public void SetCollapseChangelog(bool collapse)
    {
        _globalSettings["collapseChangelog"] = collapse;
        MarkModified("collapseChangelog");
        Save();
    }

    public bool GetEnableInstallTelemetry() => AsBool(Raw("enableInstallTelemetry"), true);

    public void SetEnableInstallTelemetry(bool enabled)
    {
        _globalSettings["enableInstallTelemetry"] = enabled;
        MarkModified("enableInstallTelemetry");
        Save();
    }

    public bool GetEnableAnalytics() => AsBool(Raw("enableAnalytics"), false);

    public string? GetTrackingId() => AsString(Raw("trackingId"));

    /// <summary>Set the analytics opt-in preference; generates a tracking identifier on first opt-in.</summary>
    public void SetEnableAnalytics(bool enabled)
    {
        _globalSettings["enableAnalytics"] = enabled;
        MarkModified("enableAnalytics");
        if (enabled && AsString(_globalSettings["trackingId"]) is null)
        {
            _globalSettings["trackingId"] = Guid.NewGuid().ToString();
            MarkModified("trackingId");
        }
        Save();
    }

    /// <summary>
    /// Stable ID of this installation, e.g. sent to OpenAI as its agent host ID. Created on first use.
    /// Project settings are ignored so a committed project settings file cannot give every clone the
    /// same ID.
    /// </summary>
    public string GetOrCreateDeviceId()
    {
        if (AsString(_globalSettings["deviceId"]) is not { } deviceId)
        {
            deviceId = Guid.NewGuid().ToString();
            _globalSettings["deviceId"] = deviceId;
            MarkModified("deviceId");
            Save();
        }
        return deviceId;
    }

    // ---- resource lists ---------------------------------------------------

    public IReadOnlyList<JsonNode?> GetPackages()
        => Raw("packages") is JsonArray array ? array.Select(node => node?.DeepClone()).ToList() : [];

    public void SetPackages(IReadOnlyList<JsonNode?> packages)
    {
        _globalSettings["packages"] = ToJsonArray(packages);
        MarkModified("packages");
        Save();
    }

    public void SetProjectPackages(IReadOnlyList<JsonNode?> packages)
        => UpdateProjectSettings("packages", settings => settings["packages"] = ToJsonArray(packages));

    public IReadOnlyList<string> GetExtensionPaths() => [.. AsStringArray(Raw("extensions"))];

    public void SetExtensionPaths(IReadOnlyList<string> paths)
    {
        _globalSettings["extensions"] = ToJsonArray(paths);
        MarkModified("extensions");
        Save();
    }

    public void SetProjectExtensionPaths(IReadOnlyList<string> paths)
        => UpdateProjectSettings("extensions", settings => settings["extensions"] = ToJsonArray(paths));

    public IReadOnlyList<string> GetSkillPaths() => [.. AsStringArray(Raw("skills"))];

    public void SetSkillPaths(IReadOnlyList<string> paths)
    {
        _globalSettings["skills"] = ToJsonArray(paths);
        MarkModified("skills");
        Save();
    }

    public void SetProjectSkillPaths(IReadOnlyList<string> paths)
        => UpdateProjectSettings("skills", settings => settings["skills"] = ToJsonArray(paths));

    public IReadOnlyList<string> GetPromptTemplatePaths() => [.. AsStringArray(Raw("prompts"))];

    public void SetPromptTemplatePaths(IReadOnlyList<string> paths)
    {
        _globalSettings["prompts"] = ToJsonArray(paths);
        MarkModified("prompts");
        Save();
    }

    public void SetProjectPromptTemplatePaths(IReadOnlyList<string> paths)
        => UpdateProjectSettings("prompts", settings => settings["prompts"] = ToJsonArray(paths));

    public IReadOnlyList<string> GetThemePaths() => [.. AsStringArray(Raw("themes"))];

    public void SetThemePaths(IReadOnlyList<string> paths)
    {
        _globalSettings["themes"] = ToJsonArray(paths);
        MarkModified("themes");
        Save();
    }

    public void SetProjectThemePaths(IReadOnlyList<string> paths)
        => UpdateProjectSettings("themes", settings => settings["themes"] = ToJsonArray(paths));

    public bool GetEnableSkillCommands() => AsBool(Raw("enableSkillCommands"), true);

    public void SetEnableSkillCommands(bool enabled)
    {
        _globalSettings["enableSkillCommands"] = enabled;
        MarkModified("enableSkillCommands");
        Save();
    }

    public JsonObject? GetThinkingBudgets() => AsObject(Raw("thinkingBudgets"));

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonArray ToJsonArray(IEnumerable<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value?.DeepClone());
        return array;
    }

    // ---- terminal ---------------------------------------------------------

    public TerminalCapabilityOverrides GetTerminalCapabilityOverrides()
    {
        var terminal = AsObject(Raw("terminal"));
        var images = terminal?["images"];
        var imagesProtocol = AsString(images);
        var (protocol, specified) = imagesProtocol switch
        {
            "kitty" => ((ImageProtocol?)ImageProtocol.Kitty, true),
            "iterm2" => ((ImageProtocol?)ImageProtocol.Iterm2, true),
            _ when AsNullableBool(images) == false => (null, true),
            _ => (null, false),
        };
        return new TerminalCapabilityOverrides
        {
            Images = protocol,
            ImagesSpecified = specified,
            TrueColor = AsNullableBool(terminal?["trueColor"]),
            Hyperlinks = AsNullableBool(terminal?["hyperlinks"]),
        };
    }

    public bool GetShowImages() => AsBool(AsObject(Raw("terminal"))?["showImages"], true);

    public void SetShowImages(bool show)
    {
        EnsureGlobalObject("terminal")["showImages"] = show;
        MarkModified("terminal", "showImages");
        Save();
    }

    public long GetImageWidthCells()
    {
        var width = AsNumber(AsObject(Raw("terminal"))?["imageWidthCells"]);
        if (width is null || !double.IsFinite(width.Value)) return 60;
        return Math.Max(1, (long)Math.Floor(width.Value));
    }

    public void SetImageWidthCells(double width)
    {
        EnsureGlobalObject("terminal")["imageWidthCells"] = Math.Max(1, Math.Floor(width));
        MarkModified("terminal", "imageWidthCells");
        Save();
    }

    public bool GetClearOnShrink()
    {
        // Settings takes precedence, then env var, then default false.
        if (AsNullableBool(AsObject(Raw("terminal"))?["clearOnShrink"]) is bool configured) return configured;
        return Environment.GetEnvironmentVariable("PI_CLEAR_ON_SHRINK") == "1";
    }

    public void SetClearOnShrink(bool enabled)
    {
        EnsureGlobalObject("terminal")["clearOnShrink"] = enabled;
        MarkModified("terminal", "clearOnShrink");
        Save();
    }

    public bool GetShowTerminalProgress() => AsBool(AsObject(Raw("terminal"))?["showTerminalProgress"], false);

    public void SetShowTerminalProgress(bool enabled)
    {
        EnsureGlobalObject("terminal")["showTerminalProgress"] = enabled;
        MarkModified("terminal", "showTerminalProgress");
        Save();
    }

    // ---- TUI --------------------------------------------------------------

    public TuiMode GetTuiMode() => AsString(Raw("tuiMode")) == "regular" ? TuiMode.Regular : TuiMode.Fullscreen;

    public void SetTuiMode(TuiMode mode)
    {
        _globalSettings["tuiMode"] = mode == TuiMode.Regular ? "regular" : "fullscreen";
        MarkModified("tuiMode");
        Save();
    }

    public string GetFullscreenExitOutput()
        => AsString(Raw("fullscreenExitOutput")) == "resume-hint" ? "resume-hint" : "transcript";

    public void SetFullscreenExitOutput(string output)
    {
        _globalSettings["fullscreenExitOutput"] = output;
        MarkModified("fullscreenExitOutput");
        Save();
    }

    public ScrollViewScrollbar GetFullscreenScrollbar()
    {
        var mode = AsString(Raw("fullscreenScrollbar"));
        return mode switch
        {
            "always" => ScrollViewScrollbar.Always,
            "hidden" => ScrollViewScrollbar.Hidden,
            _ => ScrollViewScrollbar.Auto,
        };
    }

    public void SetFullscreenScrollbar(ScrollViewScrollbar mode)
    {
        _globalSettings["fullscreenScrollbar"] = mode switch
        {
            ScrollViewScrollbar.Always => "always",
            ScrollViewScrollbar.Hidden => "hidden",
            _ => "auto",
        };
        MarkModified("fullscreenScrollbar");
        Save();
    }

    public bool GetFullscreenCopyOnSelect() => AsBool(Raw("fullscreenCopyOnSelect"), true);

    public void SetFullscreenCopyOnSelect(bool enabled)
    {
        _globalSettings["fullscreenCopyOnSelect"] = enabled;
        MarkModified("fullscreenCopyOnSelect");
        Save();
    }

    public WheelScrollLines GetFullscreenWheelScrollLines()
    {
        var lines = AsNumber(Raw("fullscreenWheelScrollLines"));
        return lines is not null && double.IsFinite(lines.Value)
            ? new WheelScrollLines(Math.Max(1, Math.Min(100, Math.Floor(lines.Value))))
            : WheelScrollLines.Auto;
    }

    public void SetFullscreenWheelScrollLines(WheelScrollLines lines)
    {
        _globalSettings["fullscreenWheelScrollLines"] = lines.IsAuto
            ? "auto"
            : Math.Max(1, Math.Min(100, Math.Floor(lines.Lines)));
        MarkModified("fullscreenWheelScrollLines");
        Save();
    }

    // ---- images -----------------------------------------------------------

    public bool GetImageAutoResize() => AsBool(AsObject(Raw("images"))?["autoResize"], true);

    public void SetImageAutoResize(bool enabled)
    {
        EnsureGlobalObject("images")["autoResize"] = enabled;
        MarkModified("images", "autoResize");
        Save();
    }

    public bool GetBlockImages() => AsBool(AsObject(Raw("images"))?["blockImages"], false);

    public void SetBlockImages(bool blocked)
    {
        EnsureGlobalObject("images")["blockImages"] = blocked;
        MarkModified("images", "blockImages");
        Save();
    }

    // ---- models / tools ---------------------------------------------------

    public IReadOnlyList<string>? GetEnabledModels()
        => Raw("enabledModels") is JsonArray array ? [.. AsStringArray(array)] : null;

    /// <summary>The resolved <c>defaultTools</c> selection, or null when no settings layer sets it.</summary>
    public IReadOnlyList<string>? GetDefaultTools()
    {
        var tools = Raw("defaultTools");
        if (tools is null) return null;
        var entries = tools is JsonArray array
            ? array.Where(item => item is JsonValue && AsString(item) is not null)
                .Select(item => AsString(item)!)
                .ToList()
            : [];
        return ResolveDefaultTools(entries);
    }

    public void SetEnabledModels(IReadOnlyList<string>? patterns)
    {
        if (patterns is null) _globalSettings.Remove("enabledModels");
        else _globalSettings["enabledModels"] = ToJsonArray(patterns);
        MarkModified("enabledModels");
        Save();
    }

    public string GetDoubleEscapeAction() => AsString(Raw("doubleEscapeAction")) ?? "tree";

    public void SetDoubleEscapeAction(string action)
    {
        _globalSettings["doubleEscapeAction"] = action;
        MarkModified("doubleEscapeAction");
        Save();
    }

    public string GetTreeFilterMode()
    {
        var mode = AsString(Raw("treeFilterMode"));
        string[] valid = ["default", "no-tools", "user-only", "labeled-only", "all"];
        return mode is not null && valid.Contains(mode, StringComparer.Ordinal) ? mode : "default";
    }

    public void SetTreeFilterMode(string mode)
    {
        _globalSettings["treeFilterMode"] = mode;
        MarkModified("treeFilterMode");
        Save();
    }

    public bool GetShowHardwareCursor()
        => AsBool(Raw("showHardwareCursor"), Environment.GetEnvironmentVariable("PI_HARDWARE_CURSOR") == "1");

    public void SetShowHardwareCursor(bool enabled)
    {
        _globalSettings["showHardwareCursor"] = enabled;
        MarkModified("showHardwareCursor");
        Save();
    }

    public double GetEditorPaddingX() => AsNumber(Raw("editorPaddingX")) ?? 0;

    public void SetEditorPaddingX(double padding)
    {
        _globalSettings["editorPaddingX"] = Math.Max(0, Math.Min(3, Math.Floor(padding)));
        MarkModified("editorPaddingX");
        Save();
    }

    public long GetOutputPad() => AsNumber(Raw("outputPad")) == 0 ? 0 : 1;

    public void SetOutputPad(long padding)
    {
        _globalSettings["outputPad"] = padding;
        MarkModified("outputPad");
        Save();
    }

    public double GetAutocompleteMaxVisible() => AsNumber(Raw("autocompleteMaxVisible")) ?? 5;

    public void SetAutocompleteMaxVisible(double maxVisible)
    {
        _globalSettings["autocompleteMaxVisible"] = Math.Max(3, Math.Min(20, Math.Floor(maxVisible)));
        MarkModified("autocompleteMaxVisible");
        Save();
    }

    // ---- markdown / warnings ----------------------------------------------

    public string GetCodeBlockIndent() => AsString(AsObject(Raw("markdown"))?["codeBlockIndent"]) ?? "  ";

    public string GetMermaidRenderingMode()
    {
        var mode = AsString(AsObject(Raw("markdown"))?["mermaid"]);
        return mode is "off" or "final" ? mode : "streaming";
    }

    public void SetMermaidRenderingMode(string mode)
    {
        EnsureGlobalObject("markdown")["mermaid"] = mode;
        MarkModified("markdown", "mermaid");
        Save();
    }

    public JsonObject GetWarnings()
        => AsObject(Raw("warnings")) is { } warnings ? (JsonObject)warnings.DeepClone() : new JsonObject();

    public void SetWarnings(JsonObject warnings)
    {
        _globalSettings["warnings"] = (JsonObject)warnings.DeepClone();
        MarkModified("warnings");
        Save();
    }
}
