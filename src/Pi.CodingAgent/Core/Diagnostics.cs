// ============================================================================
// Resource diagnostics — port of core/diagnostics.ts (4e-1)
// ============================================================================
//
// The loader, the resource loader and the package manager report non-fatal problems through this
// shape instead of throwing, so a bad skill file never stops the agent from starting.

namespace Pi.CodingAgent.Core;

/// <summary>The four resource kinds a collision can be reported for. TS <c>ResourceCollision["resourceType"]</c>.</summary>
public static class ResourceType
{
    /// <summary>An extension.</summary>
    public const string Extension = "extension";

    /// <summary>A skill.</summary>
    public const string Skill = "skill";

    /// <summary>A prompt template.</summary>
    public const string Prompt = "prompt";

    /// <summary>A theme.</summary>
    public const string Theme = "theme";
}

/// <summary>Severity of a <see cref="ResourceDiagnostic"/>. TS <c>ResourceDiagnostic["type"]</c>.</summary>
public static class ResourceDiagnosticType
{
    /// <summary>Loadable, but something about it is off.</summary>
    public const string Warning = "warning";

    /// <summary>The resource could not be used.</summary>
    public const string Error = "error";

    /// <summary>A same-named resource won; this one was dropped.</summary>
    public const string Collision = "collision";
}

/// <summary>Two resources claimed the same name. Port of the TS <c>ResourceCollision</c>.</summary>
public sealed record ResourceCollision
{
    /// <summary>One of <see cref="ResourceType"/>.</summary>
    public required string ResourceType { get; init; }

    /// <summary>Skill name, command/tool/flag name, prompt name or theme name.</summary>
    public required string Name { get; init; }

    /// <summary>Path of the resource that was kept.</summary>
    public required string WinnerPath { get; init; }

    /// <summary>Path of the resource that was dropped.</summary>
    public required string LoserPath { get; init; }

    /// <summary>Winner's source tag, e.g. <c>npm:foo</c>, <c>git:…</c> or <c>local</c>.</summary>
    public string? WinnerSource { get; init; }

    /// <summary>Loser's source tag.</summary>
    public string? LoserSource { get; init; }
}

/// <summary>One non-fatal load diagnostic. Port of the TS <c>ResourceDiagnostic</c>.</summary>
public sealed record ResourceDiagnostic
{
    /// <summary>One of <see cref="ResourceDiagnosticType"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The human-readable problem.</summary>
    public required string Message { get; init; }

    /// <summary>The resource the diagnostic is about, when it names one.</summary>
    public string? Path { get; init; }

    /// <summary>Present only when <see cref="Type"/> is <see cref="ResourceDiagnosticType.Collision"/>.</summary>
    public ResourceCollision? Collision { get; init; }

    /// <summary>The TS <c>{ type: "warning", message, path }</c> shorthand.</summary>
    public static ResourceDiagnostic Warning(string message, string? path = null) =>
        new() { Type = ResourceDiagnosticType.Warning, Message = message, Path = path };

    /// <summary>The TS <c>{ type: "collision", message, path, collision }</c> shorthand.</summary>
    public static ResourceDiagnostic CollisionOf(string message, string? path, ResourceCollision collision) =>
        new() { Type = ResourceDiagnosticType.Collision, Message = message, Path = path, Collision = collision };
}
