using System.Text.Json;

namespace Pi.CodingAgent.Utils;

/// <summary>The latest advertised release (port of <c>LatestPiRelease</c>).</summary>
/// <param name="Version">The advertised version.</param>
/// <param name="PackageName">The package to install, when the release renamed it.</param>
/// <param name="Note">An optional release note.</param>
public sealed record LatestPiRelease(string Version, string? PackageName = null, string? Note = null);

/// <summary>Options for the version check.</summary>
public sealed record VersionCheckOptions
{
    /// <summary>Overall timeout; defaults to <see cref="VersionCheck.DefaultVersionCheckTimeoutMs"/>.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Allow two retries on a transient failure.</summary>
    public bool Retry { get; init; }
}

/// <summary>Port of <c>utils/version-check.ts</c>.</summary>
public static class VersionCheck
{
    /// <summary>The endpoint that advertises the latest release.</summary>
    public const string LatestVersionUrl = "https://pi.dev/api/latest-version";

    /// <summary>Default timeout for a version check.</summary>
    public const int DefaultVersionCheckTimeoutMs = 10000;

    private static Func<string, string?>? _envOverride;

    /// <summary>
    /// Replace the environment lookup. The TS reads <c>process.env</c> at call time; the port exposes an
    /// override instead of mutating process-wide state from tests.
    /// </summary>
    internal static Func<string, string?>? EnvOverride
    {
        get => _envOverride;
        set => _envOverride = value;
    }

    private static Func<string, string?> Env => _envOverride ?? Environment.GetEnvironmentVariable;

    /// <summary>
    /// Include useful errno details hidden behind a generic "fetch failed" error.
    /// </summary>
    /// <remarks>
    /// Node wraps a transport failure as <c>Error("fetch failed", { cause })</c>, where the cause is
    /// either a single error or an <c>AggregateError</c> over the addresses that were tried. The errno
    /// codes are what actually identify the problem, so they are pulled to the front; failing that, the
    /// cause's own message is appended.
    /// </remarks>
    public static string FormatVersionCheckError(Exception error)
    {
        var rootMessage = RootMessage(error);

        IReadOnlyList<Exception> causes = error switch
        {
            AggregateException aggregate => [.. aggregate.InnerExceptions],
            _ when error.InnerException is { } inner => [inner],
            _ => [],
        };

        var codes = new List<string>();
        foreach (var cause in causes)
        {
            if (cause is INodeError { Code: { Length: > 0 } code } && !codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        if (codes.Count > 0)
        {
            return $"{rootMessage} ({string.Join(", ", codes)})";
        }

        var causeMessage = causes.FirstOrDefault(cause => !string.IsNullOrEmpty(cause.Message))?.Message;
        return causeMessage is null ? rootMessage : $"{rootMessage} (cause: {causeMessage})";
    }

    /// <summary>
    /// The text JavaScript would read as <c>error.message</c>, or the error's name when the message is
    /// empty — which is what <c>String(error)</c> produces for a bare <c>Error</c>.
    /// </summary>
    /// <remarks>
    /// Two .NET behaviours have to be undone here, because neither exists in JavaScript:
    /// <list type="bullet">
    /// <item>
    /// <see cref="AggregateException.Message"/> is not the message handed to the constructor. The
    /// runtime appends every inner message as <c>" (m1) (m2)"</c>, so the outer text has to be recovered
    /// by removing that suffix again.
    /// </item>
    /// <item>
    /// A .NET type name is not a JavaScript error name, so the fallback is mapped explicitly.
    /// </item>
    /// </list>
    /// </remarks>
    private static string RootMessage(Exception error)
    {
        if (string.IsNullOrEmpty(error.Message))
        {
            return JavaScriptErrorName(error);
        }

        if (error is not AggregateException aggregate || aggregate.InnerExceptions.Count == 0)
        {
            return error.Message;
        }

        var suffix = " (" + string.Join(") (", aggregate.InnerExceptions.Select(inner => inner.Message)) + ")";
        return aggregate.Message.EndsWith(suffix, StringComparison.Ordinal)
            ? aggregate.Message[..^suffix.Length]
            : aggregate.Message;
    }

    /// <summary>
    /// The name JavaScript reports for an error. Node only ever throws <c>Error</c> and
    /// <c>AggregateError</c> from a failed fetch, so those are the only names that can appear here.
    /// </summary>
    private static string JavaScriptErrorName(Exception error) =>
        error is AggregateException ? "AggregateError" : "Error";

    /// <summary>Compare two package versions, or <see langword="null"/> when either is not valid semver.</summary>
    public static int? ComparePackageVersions(string leftVersion, string rightVersion)
        => Semver.Compare(leftVersion.Trim(), rightVersion.Trim());

    /// <summary>
    /// Whether <paramref name="candidateVersion"/> is newer than <paramref name="currentVersion"/>.
    /// Unparseable versions fall back to a plain string inequality, so a hand-written version string is
    /// still reported as an update.
    /// </summary>
    public static bool IsNewerPackageVersion(string candidateVersion, string currentVersion)
    {
        var comparison = ComparePackageVersions(candidateVersion, currentVersion);
        return comparison is not null
            ? comparison > 0
            : candidateVersion.Trim() != currentVersion.Trim();
    }

    /// <summary>
    /// Validate the advertised-release payload. Extracted from <see cref="GetLatestPiReleaseAsync"/> so
    /// the shape rules can be exercised without a server.
    /// </summary>
    internal static LatestPiRelease? ParseLatestRelease(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var version = versionElement.GetString();
        if (string.IsNullOrEmpty(version) || version.Trim().Length == 0)
        {
            return null;
        }

        var packageName = data.TryGetProperty("packageName", out var packageElement) &&
                          packageElement.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrEmpty(packageElement.GetString()?.Trim())
            ? packageElement.GetString()!.Trim()
            : null;

        var note = data.TryGetProperty("note", out var noteElement) &&
                   noteElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrEmpty(noteElement.GetString()?.Trim())
            ? noteElement.GetString()!.Trim()
            : null;

        return new LatestPiRelease(version.Trim(), packageName, note);
    }

    /// <summary>The latest advertised release, or <see langword="null"/> when unavailable.</summary>
    public static async Task<LatestPiRelease?> GetLatestPiReleaseAsync(
        string currentVersion,
        VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new VersionCheckOptions();
        if (!string.IsNullOrEmpty(Env("PI_OFFLINE")))
        {
            return null;
        }

        var userAgent = PiUserAgent.GetPiUserAgent(currentVersion);
        using var response = await ManagementHttp.FetchWithRetryAsync(
            LatestVersionUrl,
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAddWithoutValidation("accept", "application/json");
            },
            new FetchRetryOptions
            {
                MaxRetries = options.Retry ? 2 : 0,
                TimeoutMs = options.TimeoutMs ?? DefaultVersionCheckTimeoutMs,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLatestRelease(document.RootElement);
    }

    /// <summary>The latest advertised version string, or <see langword="null"/>.</summary>
    public static async Task<string?> GetLatestPiVersionAsync(
        string currentVersion,
        VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default)
        => (await GetLatestPiReleaseAsync(currentVersion, options, cancellationToken).ConfigureAwait(false))?.Version;

    /// <summary>
    /// The latest release, but only when it is newer than the running version. Never throws: a version
    /// check is a courtesy, so any failure reports "no update".
    /// </summary>
    public static async Task<LatestPiRelease?> CheckForNewPiVersionAsync(
        string currentVersion,
        VersionCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(Env("PI_SKIP_VERSION_CHECK")))
        {
            return null;
        }

        try
        {
            var latest = await GetLatestPiReleaseAsync(currentVersion, options, cancellationToken).ConfigureAwait(false);
            return latest is not null && IsNewerPackageVersion(latest.Version, currentVersion) ? latest : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
