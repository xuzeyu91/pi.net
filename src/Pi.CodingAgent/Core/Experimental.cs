// ============================================================================
// Experimental feature gate — port of core/experimental.ts (4e-1)
// ============================================================================

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/experimental.ts</c>.</summary>
public static class Experimental
{
    /// <summary>The environment variable that turns experimental features on. TS <c>process.env.PI_EXPERIMENTAL</c>.</summary>
    public const string ExperimentalEnvVar = "PI_EXPERIMENTAL";

    /// <summary>Whether experimental features are enabled. Only the exact value <c>"1"</c> counts.</summary>
    public static bool AreExperimentalFeaturesEnabled() =>
        Environment.GetEnvironmentVariable(ExperimentalEnvVar) == "1";
}
