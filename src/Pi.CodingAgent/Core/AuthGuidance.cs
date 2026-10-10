// ============================================================================
// Auth guidance — port of core/auth-guidance.ts (4e-1)
// ============================================================================
//
// The strings the CLI shows when no model can be used: the same login hint is embedded in all three
// messages, so it lives in one place.

using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/auth-guidance.ts</c>.</summary>
public static class AuthGuidance
{
    private const string UnknownProvider = "unknown";

    /// <summary>The shared login hint, pointing at the two bundled docs pages.</summary>
    public static string GetProviderLoginHelp()
    {
        var docs = Config.GetDocsPath();
        return string.Join(
            '\n',
            "Use /login to log into a provider via OAuth or API key. See:",
            $"  {NodePath.Join(docs, "providers.md")}",
            $"  {NodePath.Join(docs, "models.md")}");
    }

    /// <summary>Shown when the catalog is empty or every model is unusable.</summary>
    public static string FormatNoModelsAvailableMessage() => $"No models available. {GetProviderLoginHelp()}";

    /// <summary>Shown when a session has no model selected yet.</summary>
    public static string FormatNoModelSelectedMessage() =>
        $"No model selected.\n\n{GetProviderLoginHelp()}\n\nThen use /model to select a model.";

    /// <summary>Shown when the selected provider has no usable credential.</summary>
    public static string FormatNoApiKeyFoundMessage(string provider)
    {
        var providerDisplay = provider == UnknownProvider ? "the selected model" : provider;
        return $"No API key found for {providerDisplay}.\n\n{GetProviderLoginHelp()}";
    }
}
