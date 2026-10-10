// ============================================================================
// Install telemetry gate — port of core/telemetry.ts (4e-1)
// ============================================================================
//
// A single boolean that decides whether the agent may attach attribution headers (see
// ProviderAttribution) and send the install ping. The environment variable, when present at all,
// always wins — including when it is present but empty, which reads as "off".

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/telemetry.ts</c>.</summary>
public static class Telemetry
{
    /// <summary>The environment variable that overrides the setting. TS <c>process.env.PI_TELEMETRY</c>.</summary>
    public const string TelemetryEnvVar = "PI_TELEMETRY";

    /// <summary>
    /// Whether install telemetry is on: the environment variable when it is set at all, otherwise the
    /// stored setting (default true).
    /// </summary>
    public static bool IsInstallTelemetryEnabled(SettingsManager settingsManager) =>
        IsInstallTelemetryEnabled(settingsManager, Environment.GetEnvironmentVariable(TelemetryEnvVar));

    /// <summary>
    /// The seam-taking overload. <paramref name="telemetryEnv"/> <see langword="null"/> means "not set",
    /// which falls back to the setting; an empty string is a set-but-falsy value, exactly like TS.
    /// </summary>
    public static bool IsInstallTelemetryEnabled(SettingsManager settingsManager, string? telemetryEnv) =>
        telemetryEnv is not null
            ? IsTruthyEnvFlag(telemetryEnv)
            : settingsManager.GetEnableInstallTelemetry();

    /// <summary>TS <c>isTruthyEnvFlag</c>: only <c>1</c>, <c>true</c> and <c>yes</c> (any case) are on.</summary>
    internal static bool IsTruthyEnvFlag(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value == "1"
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
