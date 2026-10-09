using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Core;

/// <summary>How the process was started. Port of <c>AppMode</c> in <c>core/project-trust.ts</c>.</summary>
public static class AppModes
{
    public const string Interactive = "interactive";
    public const string Print = "print";
    public const string Json = "json";
    public const string Rpc = "rpc";
}

/// <summary>
/// The UI surface the trust prompt needs. Port of the <c>ui</c> member of the TS
/// <c>ProjectTrustContext</c>.
/// </summary>
public interface IProjectTrustUi
{
    /// <summary>Whether a UI is available at all; without one the trust prompt is skipped.</summary>
    bool HasUI { get; }

    /// <summary>Ask the user to pick one label; null when the prompt is dismissed.</summary>
    Task<string?> SelectAsync(
        string prompt,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken = default);
}

/// <summary>Port of the TS <c>ProjectTrustContext</c>.</summary>
public sealed record ProjectTrustContext(IProjectTrustUi Ui)
{
    public bool HasUI => Ui.HasUI;
}

/// <summary>
/// Marker for the loaded-extensions handle the trust gate consults. The TS signature takes
/// <c>LoadExtensionsResult</c>, which belongs to the extension system (sub-phase 4d); the coding agent
/// declares the seam here so the trust gate can land before it, and 4d's result type implements this.
/// </summary>
public interface IProjectTrustExtensions
{
}

/// <summary>An extension error surfaced while emitting <c>project_trust</c>. Port of the TS error entry.</summary>
public sealed record ProjectTrustExtensionError(string ExtensionPath, string Error);

/// <summary>What an extension decided for the <c>project_trust</c> event.</summary>
public sealed record ProjectTrustEventOutcome(bool Trusted, bool Remember);

/// <summary>
/// Emits the <c>project_trust</c> event to the loaded extensions and reports their outcome. The real
/// implementation is <c>core/extensions/runner.ts</c>, which lands in sub-phase 4d; until then the seam
/// is left unset and the trust gate falls through to the stored decision.
/// </summary>
public delegate Task<(ProjectTrustEventOutcome? Result, IReadOnlyList<ProjectTrustExtensionError> Errors)>
    ProjectTrustEventEmitter(
        IProjectTrustExtensions extensionsResult,
        string cwd,
        ProjectTrustContext context,
        CancellationToken cancellationToken);

/// <summary>Options for <see cref="ProjectTrust.ResolveProjectTrustedAsync"/>. Port of <c>ResolveProjectTrustedOptions</c>.</summary>
public sealed record ResolveProjectTrustedOptions
{
    public required string Cwd { get; init; }

    public required ProjectTrustStore TrustStore { get; init; }

    /// <summary>An explicit <c>--trust</c> / <c>--no-trust</c> override; wins over everything else.</summary>
    public bool? TrustOverride { get; init; }

    public string? DefaultProjectTrust { get; init; }

    public IProjectTrustExtensions? ExtensionsResult { get; init; }

    public required ProjectTrustContext ProjectTrustContext { get; init; }

    public Action<string>? OnExtensionError { get; init; }

    /// <summary>Emits the <c>project_trust</c> extension event. Unset until the extension system lands (4d).</summary>
    public ProjectTrustEventEmitter? EmitProjectTrustEvent { get; init; }
}

/// <summary>Port of <c>core/project-trust.ts</c>.</summary>
public static class ProjectTrust
{
    /// <summary>The trust prompt text. Port of <c>formatProjectTrustPrompt</c>.</summary>
    internal static string FormatProjectTrustPrompt(string cwd) =>
        $"Trust project folder?\n{cwd}\n\nThis allows {Config.AppName} to load {Config.ConfigDirName} settings and resources, install missing project packages, and execute project extensions.";

    /// <summary>
    /// Decide whether the project at <paramref name="options"/>.<see cref="ResolveProjectTrustedOptions.Cwd"/>
    /// is trusted: explicit override, then extensions, then the stored decision, then the default policy,
    /// then the interactive prompt. Port of <c>resolveProjectTrusted</c>.
    /// </summary>
    public static async Task<bool> ResolveProjectTrustedAsync(
        ResolveProjectTrustedOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.TrustOverride is bool overrideValue) return overrideValue;
        if (!TrustManager.HasTrustRequiringProjectResources(options.Cwd)) return true;

        if (options.ExtensionsResult is not null && options.EmitProjectTrustEvent is not null)
        {
            var (result, errors) = await options.EmitProjectTrustEvent(
                options.ExtensionsResult,
                options.Cwd,
                options.ProjectTrustContext,
                cancellationToken).ConfigureAwait(false);
            foreach (var error in errors)
            {
                options.OnExtensionError?.Invoke(
                    $"Extension \"{error.ExtensionPath}\" project_trust error: {error.Error}");
            }

            if (result is not null)
            {
                if (result.Remember) options.TrustStore.Set(options.Cwd, result.Trusted);
                return result.Trusted;
            }
        }

        var decision = options.TrustStore.Get(options.Cwd);
        if (decision is not null) return decision.Value;

        switch (options.DefaultProjectTrust ?? "ask")
        {
            case "always":
                return true;
            case "never":
                return false;
            case "ask":
                break;
        }

        if (!options.ProjectTrustContext.HasUI) return false;

        var selected = await SelectProjectTrustOptionAsync(options.Cwd, options.ProjectTrustContext, cancellationToken)
            .ConfigureAwait(false);
        if (selected is not null)
        {
            SaveProjectTrustPromptResult(options.TrustStore, selected);
            return selected.Trusted;
        }
        return false;
    }

    private static async Task<ProjectTrustOption?> SelectProjectTrustOptionAsync(
        string cwd,
        ProjectTrustContext context,
        CancellationToken cancellationToken)
    {
        var options = TrustManager.GetProjectTrustOptions(cwd, includeSessionOnly: true);
        var labels = options.Select(option => option.Label).ToList();
        var selected = await context.Ui.SelectAsync(FormatProjectTrustPrompt(cwd), labels, cancellationToken)
            .ConfigureAwait(false);
        return selected is null ? null : options.FirstOrDefault(option => option.Label == selected);
    }

    private static void SaveProjectTrustPromptResult(ProjectTrustStore trustStore, ProjectTrustOption result)
    {
        if (result.Updates.Count > 0) trustStore.SetMany(result.Updates);
    }
}
