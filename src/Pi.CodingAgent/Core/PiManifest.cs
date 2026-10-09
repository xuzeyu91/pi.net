using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>The <c>pi</c> manifest section of a package.json. Port of <c>core/pi-manifest.ts</c>.</summary>
public sealed record PiManifest
{
    public IReadOnlyList<string>? Extensions { get; init; }

    public IReadOnlyList<string>? Skills { get; init; }

    public IReadOnlyList<string>? Prompts { get; init; }

    public IReadOnlyList<string>? Themes { get; init; }
}

/// <summary>Port of <c>core/pi-manifest.ts</c>.</summary>
public static class PiManifestReader
{
    private static readonly string[] ResourceFields = ["extensions", "skills", "prompts", "themes"];

    /// <summary>
    /// Read the <c>pi</c> manifest from a package.json, or null when the file is unreadable, is not
    /// JSON, or has no <c>pi</c> object. Port of <c>readPiManifest</c>.
    /// </summary>
    public static PiManifest? ReadPiManifest(string packageJsonPath)
    {
        try
        {
            var pkg = JsonNode.Parse(Text.StripBom(File.ReadAllText(packageJsonPath)));
            if (pkg is not JsonObject packageObject) return null;
            if (packageObject["pi"] is not JsonObject pi) return null;

            var manifest = new PiManifest();
            foreach (var field in ResourceFields)
            {
                if (pi[field] is not JsonArray entries) continue;
                var values = new List<string>(entries.Count);
                var allStrings = true;
                foreach (var entry in entries)
                {
                    if (entry is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
                    {
                        allStrings = false;
                        break;
                    }
                    values.Add(text);
                }
                if (!allStrings) continue;
                manifest = field switch
                {
                    "extensions" => manifest with { Extensions = values },
                    "skills" => manifest with { Skills = values },
                    "prompts" => manifest with { Prompts = values },
                    _ => manifest with { Themes = values },
                };
            }
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
