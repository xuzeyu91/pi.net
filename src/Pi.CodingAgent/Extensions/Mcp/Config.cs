// ============================================================================
// MCP server configuration — port of extensions/mcp/config.ts (4d-4a)
// ============================================================================
//
// Servers are read from `mcp.json` in the agent directory and, for trusted projects, from
// `<project>/.pi/mcp.json`. Project entries replace global entries with the same name; a project
// entry without `command`/`url`/`type` overrides only `enabled`, `exposure`, and `toolExposure`
// of the global server. `/mcp` writes changes back through update/add/remove, keeping other
// content and the file's indentation.
//
// Validation and the config shape come from the 4b port of `core/mcp-servers.ts`
// (McpServers.ValidateMcpServerConfig / McpServerConfig); this file is the file-level layer.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>One configured server. TS <c>McpServerEntry</c>.</summary>
public sealed record McpServerEntry(string Name, McpServerConfig Config, string Source)
{
    /// <summary>The global or the project <c>mcp.json</c>, or <c>extension</c> for registered servers.</summary>
    public string? Scope { get; init; }

    /// <summary>Project <c>mcp.json</c> with an override of this global server's settings.</summary>
    public string? Override { get; init; }
}

/// <summary>Result of loading the MCP configuration. TS <c>LoadedMcpConfig</c>.</summary>
public sealed record LoadedMcpConfig(IReadOnlyList<McpServerEntry> Servers, IReadOnlyList<string> Errors)
{
    /// <summary>Activate the codemode tool when <c>codemode</c> servers connect. Default: true.</summary>
    public bool? AutoEnableCodemode { get; init; }

    /// <summary>The project <c>mcp.json</c> when the project is trusted, where <c>/mcp</c> saves overrides.</summary>
    public string? ProjectConfig { get; init; }
}

/// <summary>Settings <c>/mcp</c> changes. TS <c>McpServerConfigPatch</c>.</summary>
public sealed record McpServerConfigPatch
{
    public bool? Enabled { get; init; }

    public string? Exposure { get; init; }
}

/// <summary>Port of <c>extensions/mcp/config.ts</c>.</summary>
public static class McpConfig
{
    private static readonly string[] OverrideKeys = ["enabled", "exposure", "toolExposure"];

    /// <summary>
    /// Load global and (when trusted) project MCP configuration. Disabled servers are included with
    /// <c>enabled: false</c>, so they can be enabled again.
    /// </summary>
    public static LoadedMcpConfig Load(string agentDir, string cwd, bool projectTrusted)
    {
        var state = new ConfigState();
        ReadConfigFile(Path.Combine(agentDir, "mcp.json"), "global", state);
        var projectConfig = projectTrusted ? Path.Combine(cwd, Config.ConfigDirName, "mcp.json") : null;
        if (projectConfig is not null)
        {
            ReadConfigFile(projectConfig, "project", state);
        }

        return new LoadedMcpConfig(state.Servers, state.Errors)
        {
            AutoEnableCodemode = state.AutoEnableCodemode,
            ProjectConfig = projectConfig,
        };
    }

    /// <summary>
    /// Change one server's settings in the <c>mcp.json</c> that defines or overrides it. With
    /// <paramref name="override"/> a missing entry is added as an override.
    /// </summary>
    public static void Update(string path, string name, McpServerConfigPatch patch, bool @override = false) =>
        EditMcpServers(path, (servers, parsed) =>
        {
            JsonObject? server = servers is not null && servers.ContainsKey(name) ? servers[name] as JsonObject : null;
            if (server is null && @override)
            {
                server = new JsonObject();
                if (servers is null)
                {
                    parsed["mcpServers"] = new JsonObject();
                }

                ((JsonObject)parsed["mcpServers"]!)[name] = server;
            }

            if (server is null)
            {
                throw new InvalidOperationException($"{path} does not define MCP server \"{name}\"");
            }

            var keepDefaults = IsOverride(server);
            if (patch.Enabled is bool enabled)
            {
                if (enabled && !keepDefaults)
                {
                    server.Remove("enabled");
                }
                else
                {
                    server["enabled"] = enabled;
                }
            }

            if (patch.Exposure is string exposure)
            {
                if (exposure == "codemode" && !keepDefaults)
                {
                    server.Remove("exposure");
                }
                else
                {
                    server["exposure"] = exposure;
                }
            }

            return true;
        });

    /// <summary>
    /// Add a server to an <c>mcp.json</c>, creating the file when missing. An existing entry with
    /// the same name is replaced. Returns true when an entry was replaced.
    /// </summary>
    public static bool Add(string path, string name, McpServerConfig config)
    {
        var replaced = false;
        EditMcpServers(path, (servers, parsed) =>
        {
            var target = servers ?? new JsonObject();
            replaced = target.ContainsKey(name);
            target[name] = config.Value.DeepClone();
            parsed["mcpServers"] = target;
            return true;
        });
        return replaced;
    }

    /// <summary>Remove a server from an <c>mcp.json</c>. Returns false when the file does not define it.</summary>
    public static bool Remove(string path, string name)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var removed = false;
        EditMcpServers(path, (servers, _) =>
        {
            if (servers is null || !servers.ContainsKey(name))
            {
                return false;
            }

            servers.Remove(name);
            removed = true;
            return true;
        });
        return removed;
    }

    // ------------------------------------------------------------------ reading

    private sealed class ConfigState
    {
        public List<McpServerEntry> Servers { get; } = [];

        public List<string> Errors { get; } = [];

        public bool? AutoEnableCodemode { get; set; }
    }

    private static void ReadConfigFile(string path, string scope, ConfigState state)
    {
        if (!File.Exists(path))
        {
            return;
        }

        JsonObject parsed;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            parsed = node as JsonObject
                ?? throw new NotAnObjectException();
        }
        catch (NotAnObjectException)
        {
            state.Errors.Add($"{path}: expected an object with an \"mcpServers\" object");
            return;
        }
        catch (Exception error)
        {
            state.Errors.Add($"{path}: {error.Message}");
            return;
        }

        if (parsed["mcpServers"] is JsonNode mcpServers && mcpServers is not JsonObject)
        {
            state.Errors.Add($"{path}: expected an object with an \"mcpServers\" object");
            return;
        }

        if (parsed["autoEnableCodemode"] is JsonValue autoEnable && autoEnable.TryGetValue<bool>(out var autoEnableValue))
        {
            state.AutoEnableCodemode = autoEnableValue;
        }
        else if (parsed["autoEnableCodemode"] is not null)
        {
            state.Errors.Add($"{path}: autoEnableCodemode must be a boolean");
        }

        var serversObject = parsed["mcpServers"] as JsonObject;
        foreach (var (name, value) in serversObject ?? new JsonObject())
        {
            if (scope == "project" && value is JsonObject valueObject && IsOverride(valueObject))
            {
                var baseEntry = FindServer(state.Servers, name);
                var extra = valueObject.Where(entry => !OverrideKeys.Contains(entry.Key)).Select(entry => entry.Key).ToList();
                if (baseEntry is null)
                {
                    state.Errors.Add($"{path}: server \"{name}\" needs \"command\" or \"url\", or a global server to override");
                }
                else if (extra.Count > 0)
                {
                    state.Errors.Add($"{path}: server \"{name}\": an override can only set {string.Join(", ", OverrideKeys)}");
                }
                else
                {
                    var merged = Merge(baseEntry.Config.Value, valueObject);
                    var validation = McpServers.ValidateMcpServerConfig(name, merged);
                    if (validation.Error is not null)
                    {
                        state.Errors.Add($"{path}: {validation.Error}");
                    }
                    else
                    {
                        SetServer(state.Servers, baseEntry with { Config = validation.Config!, Override = path });
                    }
                }

                continue;
            }

            var config = McpServers.ValidateMcpServerConfig(name, value);
            if (config.Error is not null)
            {
                state.Errors.Add($"{path}: {config.Error}");
                continue;
            }

            // Names that differ only in `-` and `_` would share a namespace.
            var clash = FindClash(state.Servers, name);
            if (clash is not null)
            {
                state.Errors.Add($"{path}: server \"{name}\" conflicts with \"{clash}\"");
                continue;
            }

            if (scope == "project" && config.Config!.IsHttp && config.Config!.Value["auth"] is JsonObject)
            {
                state.Errors.Add($"{path}: server \"{name}\": auth is only allowed in the global mcp.json");
                continue;
            }

            SetServer(state.Servers, new McpServerEntry(name, config.Config!, path) { Scope = scope });
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Read an <c>mcp.json</c> (an empty config when missing), let <paramref name="edit"/> change
    /// its <c>mcpServers</c>, and write it back with its indentation when it returns true.
    /// </summary>
    private static void EditMcpServers(string path, Func<JsonObject?, JsonObject, bool> edit)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : null;
        JsonObject parsed;
        if (text is null)
        {
            parsed = new JsonObject();
        }
        else
        {
            // TS: JSON.parse throws on invalid JSON; the port surfaces the same shape of failure.
            parsed = JsonNode.Parse(text) as JsonObject
                ?? throw new InvalidOperationException($"{path}: expected an object with an \"mcpServers\" object");
        }

        if (parsed["mcpServers"] is JsonNode mcpServers && mcpServers is not JsonObject)
        {
            throw new InvalidOperationException($"{path}: expected an object with an \"mcpServers\" object");
        }

        var servers = parsed["mcpServers"] as JsonObject;
        if (!edit(servers, parsed))
        {
            return;
        }

        var indent = DetectIndent(text) ?? "  ";
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = indent[0],
            IndentSize = indent.Length,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(path, $"{JsonSerializer.Serialize(parsed, options)}\n");
    }

    /// <summary>The indentation of the first indented line, as TS <c>/^([ \t]+)\S/m</c> reads it.</summary>
    private static string? DetectIndent(string? text) =>
        text is not null && Regex.Match(text, "^([ \t]+)\\S", RegexOptions.Multiline) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    // ------------------------------------------------------------------ helpers

    /// <summary>Whether an entry overrides a server defined elsewhere instead of defining one.</summary>
    private static bool IsOverride(JsonObject value) =>
        !value.ContainsKey("command") && !value.ContainsKey("url") && !value.ContainsKey("type");

    private static JsonObject Merge(JsonObject baseConfig, JsonObject overrides)
    {
        var merged = new JsonObject();
        foreach (var (key, node) in baseConfig)
        {
            merged[key] = node?.DeepClone();
        }

        foreach (var (key, node) in overrides)
        {
            merged[key] = node?.DeepClone();
        }

        return merged;
    }

    private static McpServerEntry? FindServer(List<McpServerEntry> servers, string name) =>
        servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.Ordinal));

    private static string? FindClash(List<McpServerEntry> servers, string name)
    {
        var ns = McpServers.Namespace(name);
        return servers
            .FirstOrDefault(server => !string.Equals(server.Name, name, StringComparison.Ordinal)
                && string.Equals(McpServers.Namespace(server.Name), ns, StringComparison.Ordinal))
            ?.Name;
    }

    /// <summary>TS <c>Map.set</c>: replace in place when the name exists, append otherwise.</summary>
    private static void SetServer(List<McpServerEntry> servers, McpServerEntry entry)
    {
        for (var index = 0; index < servers.Count; index++)
        {
            if (string.Equals(servers[index].Name, entry.Name, StringComparison.Ordinal))
            {
                servers[index] = entry;
                return;
            }
        }

        servers.Add(entry);
    }
}

/// <summary>Internal marker: the parsed JSON is valid but not an object (TS <c>!isRecord(parsed)</c>).</summary>
internal sealed class NotAnObjectException : Exception;
