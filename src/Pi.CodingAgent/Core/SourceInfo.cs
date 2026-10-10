// ============================================================================
// Source info — port of core/source-info.ts (4d-2b, loader subset)
// ============================================================================
//
// The extension loader stamps every extension/tool/command/renderer with where it came from.
// TS `createSourceInfo(path, metadata)` needs `PathMetadata` from `core/package-manager.ts`
// (sub-phase 4e), so only the loader-relevant half lands here: the record, the two path
// predicates, and the synthetic-path factory. `createSourceInfo` arrives with 4e.

namespace Pi.CodingAgent.Core;

/// <summary>Where a resource was loaded from. TS <c>SourceScope</c>.</summary>
public static class SourceScope
{
    /// <summary>User-level (agent dir) resource.</summary>
    public const string User = "user";

    /// <summary>Project-level (<c>.pi</c> dir) resource.</summary>
    public const string Project = "project";

    /// <summary>In-memory resource with no file behind it.</summary>
    public const string Temporary = "temporary";
}

/// <summary>How a resource entered the load set. TS <c>SourceOrigin</c>.</summary>
public static class SourceOrigin
{
    /// <summary>Declared by a package manifest.</summary>
    public const string Package = "package";

    /// <summary>A loose file or directory entry.</summary>
    public const string TopLevel = "top-level";
}

/// <summary>
/// Provenance of an extension, tool, command or renderer. Port of the TS <c>SourceInfo</c>.
/// </summary>
public sealed record SourceInfo
{
    /// <summary>Source file path (built-in extensions use <c>builtin:name</c>, inline ones <c>&lt;inline:name&gt;</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Free-form source tag: <c>local</c>, <c>builtin</c>, <c>inline</c>, a package name…</summary>
    public required string Source { get; init; }

    /// <summary>One of <see cref="SourceScope"/>.</summary>
    public required string Scope { get; init; }

    /// <summary>One of <see cref="SourceOrigin"/>.</summary>
    public required string Origin { get; init; }

    /// <summary>Directory the resource was resolved against, when it has one.</summary>
    public string? BaseDir { get; init; }
}

/// <summary>Port of <c>core/source-info.ts</c>.</summary>
public static class SourceInfos
{
    /// <summary>Prefix of built-in tool and extension paths, such as <c>builtin:read</c> or <c>builtin:mcp</c>.</summary>
    public const string BuiltinPathPrefix = "builtin:";

    /// <summary>
    /// Source of a path that names no file: <c>builtin</c> for <c>builtin:&lt;name&gt;</c>, or the prefix of an
    /// angle-bracket path such as <c>inline</c> for <c>&lt;inline:name&gt;</c>. <see langword="null"/> for file paths.
    /// </summary>
    public static string? GetSyntheticPathSource(string path)
    {
        if (path.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal))
        {
            return "builtin";
        }

        if (path.StartsWith('<') && path.EndsWith('>'))
        {
            var inner = path[1..^1];
            var separator = inner.IndexOf(':');
            var prefix = separator < 0 ? inner : inner[..separator];
            return prefix.Length > 0 ? prefix : SourceScope.Temporary;
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> names no file (a <c>builtin:</c> or <c>&lt;…&gt;</c> path).</summary>
    public static bool IsSyntheticPath(string path) =>
        path.StartsWith(BuiltinPathPrefix, StringComparison.Ordinal) || path.StartsWith('<');

    /// <summary>
    /// Source info for a synthetic path. Port of <c>createSyntheticSourceInfo</c>; scope defaults to
    /// <see cref="SourceScope.Temporary"/> and origin to <see cref="SourceOrigin.TopLevel"/>.
    /// </summary>
    public static SourceInfo CreateSynthetic(
        string path,
        string source,
        string? scope = null,
        string? origin = null,
        string? baseDir = null) =>
        new()
        {
            Path = path,
            Source = source,
            Scope = scope ?? SourceScope.Temporary,
            Origin = origin ?? SourceOrigin.TopLevel,
            BaseDir = baseDir,
        };
}
