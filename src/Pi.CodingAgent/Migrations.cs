using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent;

/// <summary>One-time migrations that run on startup. Port of the TS <c>src/migrations.ts</c>.</summary>
/// <remarks>
/// Difference C42: <see cref="ShowDeprecationWarningsAsync"/> reads one key from an interactive console
/// and one line when stdin is redirected, where TS would wait forever on a closed stdin. Everything else
/// is a line-by-line port.
/// </remarks>
public static class Migrations
{
    private const string MigrationGuideUrl =
        "https://github.com/earendil-works/pi/blob/main/packages/coding-agent/CHANGELOG.md#extensions-migration";

    private const string ExtensionsDocUrl =
        "https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/extensions.md";

    /// <summary>Injectable stdout; TS writes through <c>console.log</c>.</summary>
    internal static TextWriter Out { get; set; } = Console.Out;

    /// <summary>Node's <c>existsSync</c>: true for a file or a directory (including a symlink to one).</summary>
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Migrate legacy <c>oauth.json</c> and <c>settings.json</c> apiKeys to <c>auth.json</c>.
    /// </summary>
    /// <returns>Provider names that were migrated.</returns>
    public static IReadOnlyList<string> MigrateAuthToAuthJson()
    {
        var agentDir = Config.GetAgentDir();
        var authPath = NodePath.Join(agentDir, "auth.json");
        var oauthPath = NodePath.Join(agentDir, "oauth.json");
        var settingsPath = NodePath.Join(agentDir, "settings.json");

        // Skip if auth.json already exists.
        if (Exists(authPath)) return [];

        var migrated = new JsonObject();
        var providers = new List<string>();

        // Migrate oauth.json.
        if (Exists(oauthPath))
        {
            try
            {
                if (JsonNode.Parse(Text.StripBom(File.ReadAllText(oauthPath))) is JsonObject oauth)
                {
                    foreach (var (provider, credential) in oauth)
                    {
                        var entry = new JsonObject { ["type"] = "oauth" };
                        if (credential is JsonObject fields)
                        {
                            foreach (var (name, value) in fields) entry[name] = value?.DeepClone();
                        }

                        migrated[provider] = entry;
                        providers.Add(provider);
                    }
                }

                File.Move(oauthPath, $"{oauthPath}.migrated");
            }
            catch
            {
                // Skip on error.
            }
        }

        // Migrate settings.json apiKeys.
        if (Exists(settingsPath))
        {
            try
            {
                var content = File.ReadAllText(settingsPath);
                if (JsonNode.Parse(Text.StripBom(content)) is JsonObject settings
                    && settings["apiKeys"] is JsonObject apiKeys)
                {
                    foreach (var (provider, value) in apiKeys)
                    {
                        if (!migrated.ContainsKey(provider)
                            && value is JsonValue key
                            && key.TryGetValue<string>(out var keyText))
                        {
                            migrated[provider] = new JsonObject { ["type"] = "api_key", ["key"] = keyText };
                            providers.Add(provider);
                        }
                    }

                    settings.Remove("apiKeys");
                    File.WriteAllText(settingsPath, settings.ToJsonString(MigrationsJson.Options));
                }
            }
            catch
            {
                // Skip on error.
            }
        }

        if (migrated.Count > 0)
        {
            Directory.CreateDirectory(NodePath.Dirname(authPath));
            ModeFile.WriteAllText(authPath, migrated.ToJsonString(MigrationsJson.Options), ModeFile.OwnerReadWrite);
        }

        return providers;
    }

    /// <summary>
    /// Migrate sessions from <c>~/.pi/agent/*.jsonl</c> to proper session directories.
    /// <para>
    /// Bug in v0.30.0: sessions were saved to <c>~/.pi/agent/</c> instead of
    /// <c>~/.pi/agent/sessions/&lt;encoded-cwd&gt;/</c>. This migration moves them to the correct location
    /// based on the cwd in their session header.
    /// </para>
    /// </summary>
    public static void MigrateSessionsFromAgentRoot()
    {
        var agentDir = Config.GetAgentDir();

        // Find all .jsonl files directly in agentDir (not in subdirectories).
        List<string> files;
        try
        {
            files = Directory.GetFiles(agentDir)
                .Where(file => Path.GetFileName(file).EndsWith(".jsonl", StringComparison.Ordinal))
                .ToList();
        }
        catch
        {
            return;
        }

        if (files.Count == 0) return;

        foreach (var file in files)
        {
            try
            {
                // Read the first line to get the session header.
                var content = File.ReadAllText(file);
                var firstLine = content.Split('\n')[0];
                if (string.IsNullOrWhiteSpace(firstLine)) continue;

                if (JsonNode.Parse(firstLine) is not JsonObject header) continue;
                if (header["type"] is not JsonValue type || !type.TryGetValue<string>(out var typeText)
                    || typeText != "session")
                {
                    continue;
                }

                if (header["cwd"] is not JsonValue cwdValue
                    || !cwdValue.TryGetValue<string>(out var cwd)
                    || cwd.Length == 0)
                {
                    continue;
                }

                // Same encoding as session-manager.ts.
                var safePath = $"--{EncodeSessionDir(cwd)}--";
                var correctDir = NodePath.Join(agentDir, "sessions", safePath);

                if (!Exists(correctDir)) Directory.CreateDirectory(correctDir);

                var fileName = Path.GetFileName(file);
                var newPath = NodePath.Join(correctDir, fileName);

                if (Exists(newPath)) continue; // Skip if the target exists.

                File.Move(file, newPath);
            }
            catch
            {
                // Skip files that can't be migrated.
            }
        }
    }

    /// <summary>TS: <c>cwd.replace(/^[/\\]/, "").replace(/[/\\:]/g, "-")</c>.</summary>
    private static string EncodeSessionDir(string cwd)
    {
        var trimmed = cwd.Length > 0 && (cwd[0] == '/' || cwd[0] == '\\') ? cwd[1..] : cwd;
        return trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
    }

    /// <summary>Print deprecation warnings and wait for a keypress.</summary>
    public static async Task ShowDeprecationWarningsAsync(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;

        foreach (var warning in warnings)
        {
            await Out.WriteLineAsync(Chalk.Yellow($"Warning: {warning}")).ConfigureAwait(false);
        }

        await Out.WriteLineAsync(Chalk.Yellow("\nMove your extensions to the extensions/ directory."))
            .ConfigureAwait(false);
        await Out.WriteLineAsync(Chalk.Yellow($"Migration guide: {MigrationGuideUrl}")).ConfigureAwait(false);
        await Out.WriteLineAsync(Chalk.Yellow($"Documentation: {ExtensionsDocUrl}")).ConfigureAwait(false);
        await Out.WriteLineAsync(Chalk.Dim("\nPress any key to continue...")).ConfigureAwait(false);

        if (Console.IsInputRedirected)
        {
            // TS waits on a stdin "data" event; a redirected stream reaches EOF immediately instead.
            await Console.In.ReadLineAsync().ConfigureAwait(false);
        }
        else
        {
            Console.ReadKey(intercept: true);
        }

        await Out.WriteLineAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Run all migrations. Called once on startup.
    /// </summary>
    public static MigrationResult RunMigrations(string cwd)
    {
        var migratedAuthProviders = MigrateAuthToAuthJson();
        MigrateSessionsFromAgentRoot();
        MigrateToolsToBin();
        MigrateKeybindingsConfigFile();
        var deprecationWarnings = MigrateExtensionSystem(cwd);
        return new MigrationResult(migratedAuthProviders, deprecationWarnings);
    }

    /// <summary>Migrate <c>commands/</c> to <c>prompts/</c> if needed, for directories and symlinks alike.</summary>
    private static bool MigrateCommandsToPrompts(string baseDir, string label)
    {
        var commandsDir = NodePath.Join(baseDir, "commands");
        var promptsDir = NodePath.Join(baseDir, "prompts");

        if (!Exists(commandsDir) || Exists(promptsDir)) return false;

        try
        {
            if (Directory.Exists(commandsDir)) Directory.Move(commandsDir, promptsDir);
            else File.Move(commandsDir, promptsDir);
            Out.WriteLine(Chalk.Green($"Migrated {label} commands/ → prompts/"));
            return true;
        }
        catch (Exception error)
        {
            Out.WriteLine(Chalk.Yellow(
                $"Warning: Could not migrate {label} commands/ to prompts/: {error.Message}"));
        }

        return false;
    }

    private static void MigrateKeybindingsConfigFile()
    {
        var configPath = NodePath.Join(Config.GetAgentDir(), "keybindings.json");
        if (!Exists(configPath)) return;

        try
        {
            var parsed = JsonNode.Parse(Text.StripBom(File.ReadAllText(configPath)));
            if (parsed is not JsonObject rawConfig) return;

            var (config, migrated) = KeybindingsConfigFile.MigrateKeybindingsConfig(rawConfig);
            if (!migrated) return;
            File.WriteAllText(configPath, $"{config.ToJsonString(MigrationsJson.Options)}\n");
        }
        catch
        {
            // Ignore malformed files during migration.
        }
    }

    /// <summary>Move fd/rg binaries from <c>tools/</c> to <c>bin/</c> if they exist.</summary>
    private static void MigrateToolsToBin()
    {
        var agentDir = Config.GetAgentDir();
        var toolsDir = NodePath.Join(agentDir, "tools");
        var binDir = Config.GetBinDir();

        if (!Exists(toolsDir)) return;

        string[] binaries = ["fd", "rg", "fd.exe", "rg.exe"];
        var movedAny = false;

        foreach (var binary in binaries)
        {
            var oldPath = NodePath.Join(toolsDir, binary);
            var newPath = NodePath.Join(binDir, binary);
            if (!Exists(oldPath)) continue;

            if (!Exists(binDir)) Directory.CreateDirectory(binDir);

            if (!Exists(newPath))
            {
                try
                {
                    File.Move(oldPath, newPath);
                    movedAny = true;
                }
                catch
                {
                    // Ignore errors.
                }
            }
            else
            {
                // Target exists, just delete the old one.
                try
                {
                    File.Delete(oldPath);
                }
                catch
                {
                    // Ignore.
                }
            }
        }

        if (movedAny) Out.WriteLine(Chalk.Green("Migrated managed binaries tools/ → bin/"));
    }

    /// <summary>
    /// Check for deprecated <c>hooks/</c> and <c>tools/</c> directories. <c>tools/</c> may contain fd/rg
    /// binaries extracted by pi, so only warn if it has other files.
    /// </summary>
    private static List<string> CheckDeprecatedExtensionDirs(string baseDir, string label)
    {
        var hooksDir = NodePath.Join(baseDir, "hooks");
        var toolsDir = NodePath.Join(baseDir, "tools");
        var warnings = new List<string>();

        if (Exists(hooksDir)) warnings.Add($"{label} hooks/ directory found. Hooks have been renamed to extensions.");

        if (Exists(toolsDir))
        {
            try
            {
                var customTools = Directory.EnumerateFileSystemEntries(toolsDir)
                    .Select(Path.GetFileName)
                    .Where(entry => entry is not null)
                    .Select(entry => entry!)
                    .Where(entry =>
                    {
                        var lower = entry.ToLowerInvariant();
                        return lower is not ("fd" or "rg" or "fd.exe" or "rg.exe")
                            && !entry.StartsWith('.'); // Ignore .DS_Store and other hidden files.
                    })
                    .ToList();

                if (customTools.Count > 0)
                {
                    warnings.Add(
                        $"{label} tools/ directory contains custom tools. Custom tools have been merged into extensions.");
                }
            }
            catch
            {
                // Ignore read errors.
            }
        }

        return warnings;
    }

    /// <summary>Run extension system migrations (commands→prompts) and collect warnings.</summary>
    private static List<string> MigrateExtensionSystem(string cwd)
    {
        var agentDir = Config.GetAgentDir();
        var projectDir = NodePath.Join(cwd, Config.ConfigDirName);

        MigrateCommandsToPrompts(agentDir, "Global");
        MigrateCommandsToPrompts(projectDir, "Project");

        List<string> warnings =
        [
            .. CheckDeprecatedExtensionDirs(agentDir, "Global"),
            .. CheckDeprecatedExtensionDirs(projectDir, "Project"),
        ];

        return warnings;
    }
}

/// <summary>Outcome of <see cref="Migrations.RunMigrations"/>.</summary>
public sealed record MigrationResult(
    IReadOnlyList<string> MigratedAuthProviders,
    IReadOnlyList<string> DeprecationWarnings);

/// <summary>Two-space indented JSON without HTML escaping (<c>JSON.stringify(value, null, 2)</c>).</summary>
internal static class MigrationsJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
