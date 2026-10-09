using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/trust-manager.ts</c> (the project-trust store and its helpers).</summary>
public static class TrustManager
{
    /// <summary>The project-local resources that require project trust before they are loaded.</summary>
    private static readonly string[] TrustRequiringProjectConfigResources =
    [
        "settings.json",
        "mcp.json",
        "extensions",
        "skills",
        "prompts",
        "themes",
        "SYSTEM.md",
        "APPEND_SYSTEM.md",
    ];

    internal static string NormalizeCwd(string cwd) => Paths.CanonicalizePath(Paths.ResolvePath(cwd));

    /// <summary>The parent directory of a normalized trust path, or null at the filesystem root.</summary>
    public static string? GetProjectTrustParentPath(string cwd)
    {
        var trustPath = NormalizeCwd(cwd);
        var parentDir = NodePath.Dirname(trustPath);
        return parentDir == trustPath ? null : parentDir;
    }

    /// <summary>
    /// The trust choices offered in the prompt. Port of <c>getProjectTrustOptions</c>.
    /// </summary>
    public static IReadOnlyList<ProjectTrustOption> GetProjectTrustOptions(string cwd, bool includeSessionOnly = false)
    {
        var trustPath = NormalizeCwd(cwd);
        var trustOptions = new List<ProjectTrustOption>
        {
            new()
            {
                Label = "Trust",
                Trusted = true,
                Updates = [new ProjectTrustUpdate(trustPath, true)],
                SavedPath = trustPath,
            },
        };

        var parentPath = GetProjectTrustParentPath(cwd);
        if (parentPath is not null)
        {
            trustOptions.Add(new ProjectTrustOption
            {
                Label = $"Trust parent folder ({parentPath})",
                Trusted = true,
                Updates = [new ProjectTrustUpdate(parentPath, true), new ProjectTrustUpdate(trustPath, null)],
                SavedPath = parentPath,
            });
        }

        if (includeSessionOnly)
        {
            trustOptions.Add(new ProjectTrustOption { Label = "Trust (this session only)", Trusted = true, Updates = [] });
        }

        trustOptions.Add(new ProjectTrustOption
        {
            Label = "Do not trust",
            Trusted = false,
            Updates = [new ProjectTrustUpdate(trustPath, false)],
            SavedPath = trustPath,
        });

        if (includeSessionOnly)
        {
            trustOptions.Add(new ProjectTrustOption
            {
                Label = "Do not trust (this session only)",
                Trusted = false,
                Updates = [],
            });
        }

        return trustOptions;
    }

    /// <summary>
    /// Whether <paramref name="cwd"/> has project-local resources that must be gated by project trust:
    /// trust-requiring entries under <c>cwd/.pi</c>, or <c>.agents/skills</c> in <c>cwd</c> or an ancestor.
    /// The user/global <c>~/.agents/skills</c> directory is always trusted and ignored here, even when
    /// <paramref name="cwd"/> is <c>$HOME</c>.
    /// </summary>
    public static bool HasTrustRequiringProjectResources(string cwd)
    {
        var homeDir = Paths.CanonicalizePath(Paths.ResolvePath(Paths.GetHomeDirectory()));
        var userAgentsSkillsDir = NodePath.Join(homeDir, ".agents", "skills");
        var currentDir = Paths.CanonicalizePath(Paths.ResolvePath(cwd));

        var configDir = NodePath.Join(currentDir, Config.ConfigDirName);
        foreach (var entry in TrustRequiringProjectConfigResources)
        {
            if (Exists(NodePath.Join(configDir, entry))) return true;
        }

        while (true)
        {
            var agentsSkillsDir = NodePath.Join(currentDir, ".agents", "skills");
            if (agentsSkillsDir != userAgentsSkillsDir && Exists(agentsSkillsDir)) return true;

            var parentDir = NodePath.Dirname(currentDir);
            if (parentDir == currentDir) return false;
            currentDir = parentDir;
        }
    }

    // Node's `existsSync` is true for directories too, and every trust-requiring entry may be a directory.
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Read the trust file, validating every entry. Port of <c>readTrustFile</c>.</summary>
    internal static Dictionary<string, bool?> ReadTrustFile(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, bool?>(StringComparer.Ordinal);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Text.StripBom(File.ReadAllText(path)));
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Failed to read trust store {path}: {error.Message}", error);
        }

        if (parsed is not JsonObject obj)
        {
            throw new InvalidOperationException($"Invalid trust store {path}: expected an object");
        }

        var data = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            var normalized = value switch
            {
                null => (bool?)null,
                JsonValue jsonValue when jsonValue.TryGetValue(out bool boolean) => boolean,
                _ => null,
            };
            if (value is not null && normalized is null)
            {
                throw new InvalidOperationException(
                    $"Invalid trust store {path}: value for {JsonSerializer.Serialize(key)} must be true, false, or null");
            }
            data[key] = normalized;
        }

        return data;
    }

    /// <summary>Write the trust file with sorted keys and a trailing newline. Port of <c>writeTrustFile</c>.</summary>
    internal static void WriteTrustFile(string path, IReadOnlyDictionary<string, bool?> data)
    {
        var sorted = new SortedDictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            sorted[key] = value;
        }

        var json = JsonSerializer.Serialize(sorted, TrustJson.Options);
        var directory = NodePath.Dirname(path);
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, json + "\n");
    }

    private static ProjectTrustStoreEntry? FindNearestTrustEntry(
        IReadOnlyDictionary<string, bool?> data,
        string cwd)
    {
        var currentDir = NormalizeCwd(cwd);
        while (true)
        {
            if (data.TryGetValue(currentDir, out var value) && value is not null)
            {
                return new ProjectTrustStoreEntry(currentDir, value.Value);
            }

            var parentDir = NodePath.Dirname(currentDir);
            if (parentDir == currentDir) return null;
            currentDir = parentDir;
        }
    }

    internal static ProjectTrustStoreEntry? FindNearestTrustEntryForTest(
        IReadOnlyDictionary<string, bool?> data,
        string cwd) => FindNearestTrustEntry(data, cwd);

    /// <summary>JSON options matching <c>JSON.stringify(data, null, 2)</c>.</summary>
    internal static class TrustJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // JSON.stringify leaves non-ASCII and <>&' alone; the default encoder escapes them.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}

/// <summary>A trust decision: <c>true</c>, <c>false</c>, or <c>null</c> for "no entry".</summary>
public sealed record ProjectTrustStoreEntry(string Path, bool Decision);

/// <summary>One trust-store update. A <c>null</c> decision deletes the entry.</summary>
public sealed record ProjectTrustUpdate(string Path, bool? Decision);

/// <summary>One selectable trust option.</summary>
public sealed record ProjectTrustOption
{
    public required string Label { get; init; }

    public required bool Trusted { get; init; }

    public required IReadOnlyList<ProjectTrustUpdate> Updates { get; init; }

    public string? SavedPath { get; init; }
}

/// <summary>
/// The persisted project-trust store (<c>&lt;agentDir&gt;/trust.json</c>). Port of <c>ProjectTrustStore</c>.
/// </summary>
public sealed class ProjectTrustStore
{
    private readonly string _trustPath;

    public ProjectTrustStore(string agentDir)
    {
        _trustPath = NodePath.Join(Paths.ResolvePath(agentDir), "trust.json");
    }

    /// <summary>The trust file path (exposed for tests and diagnostics).</summary>
    public string TrustPath => _trustPath;

    /// <summary>The nearest trust decision for <paramref name="cwd"/>, or null when none applies.</summary>
    public bool? Get(string cwd) => GetEntry(cwd)?.Decision;

    /// <summary>The nearest trust entry for <paramref name="cwd"/>, or null when none applies.</summary>
    public ProjectTrustStoreEntry? GetEntry(string cwd)
    {
        using var _ = NodeLock.AcquireSync(_trustPath);
        var data = TrustManager.ReadTrustFile(_trustPath);
        return TrustManager.FindNearestTrustEntryForTest(data, cwd);
    }

    /// <summary>Set one decision.</summary>
    public void Set(string cwd, bool? decision) => SetMany([new ProjectTrustUpdate(cwd, decision)]);

    /// <summary>Apply several decisions atomically, under the trust-file lock.</summary>
    public void SetMany(IReadOnlyList<ProjectTrustUpdate> decisions)
    {
        using var _ = NodeLock.AcquireSync(_trustPath);
        var data = TrustManager.ReadTrustFile(_trustPath);
        foreach (var (path, decision) in decisions)
        {
            var key = TrustManager.NormalizeCwd(path);
            if (decision is null) data.Remove(key);
            else data[key] = decision;
        }
        TrustManager.WriteTrustFile(_trustPath, data);
    }
}
