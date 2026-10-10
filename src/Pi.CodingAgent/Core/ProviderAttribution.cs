// ============================================================================
// Provider attribution headers — port of core/provider-attribution.ts (4e-1)
// ============================================================================
//
// Some providers want to know which client is calling: OpenRouter asks for a referer and a title,
// NVIDIA NIM for a billing origin, Cloudflare for a user agent, and OpenCode for the session id. The
// attribution headers are attached only while install telemetry is enabled; the session headers are not
// gated at all.

using Pi.Ai.Models;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/provider-attribution.ts</c>.</summary>
public static class ProviderAttribution
{
    private const string OpenRouterHost = "openrouter.ai";
    private const string NvidiaNimHost = "integrate.api.nvidia.com";
    private const string CloudflareApiHost = "api.cloudflare.com";
    private const string CloudflareAiGatewayHost = "gateway.ai.cloudflare.com";
    private const string OpencodeHost = "opencode.ai";

    /// <summary>
    /// The session and attribution headers for <paramref name="model"/>, with each later source in
    /// <paramref name="headerSources"/> overwriting an earlier one. <see langword="null"/> when the result
    /// would be empty.
    /// </summary>
    public static IReadOnlyDictionary<string, string?>? MergeHeaders(
        ModelSpec model,
        SettingsManager settingsManager,
        string? sessionId,
        params IReadOnlyDictionary<string, string?>?[] headerSources)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in new[] { GetSessionHeaders(model, sessionId), GetDefaultAttributionHeaders(model, settingsManager) })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var (name, value) in source)
            {
                merged[name] = value;
            }
        }

        foreach (var headers in headerSources)
        {
            if (headers is null)
            {
                continue;
            }

            foreach (var (name, value) in headers)
            {
                merged[name] = value;
            }
        }

        return merged.Count > 0 ? merged : null;
    }

    /// <summary>The OpenCode session headers, or <see langword="null"/> for any other provider.</summary>
    private static IReadOnlyDictionary<string, string?>? GetSessionHeaders(ModelSpec model, string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        if (model.Provider is not ("opencode" or "opencode-go") && !MatchesHost(model.BaseUrl, OpencodeHost))
        {
            return null;
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["x-opencode-session"] = sessionId,
            ["x-opencode-client"] = "pi",
        };
    }

    /// <summary>The attribution headers, or <see langword="null"/> when telemetry is off or the host is unknown.</summary>
    private static IReadOnlyDictionary<string, string?>? GetDefaultAttributionHeaders(
        ModelSpec model,
        SettingsManager settingsManager)
    {
        if (!Telemetry.IsInstallTelemetryEnabled(settingsManager))
        {
            return null;
        }

        if (IsOpenRouterModel(model))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HTTP-Referer"] = "https://pi.dev",
                ["X-OpenRouter-Title"] = "pi",
                ["X-OpenRouter-Categories"] = "cli-agent",
            };
        }

        if (IsNvidiaNimModel(model))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["X-BILLING-INVOKE-ORIGIN"] = "Pi",
            };
        }

        if (IsCloudflareModel(model))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["User-Agent"] = "pi-coding-agent",
            };
        }

        return null;
    }

    private static bool IsOpenRouterModel(ModelSpec model) =>
        model.Provider == "openrouter" || model.BaseUrl.Contains(OpenRouterHost, StringComparison.Ordinal);

    private static bool IsNvidiaNimModel(ModelSpec model) =>
        model.Provider == "nvidia" || MatchesHost(model.BaseUrl, NvidiaNimHost);

    private static bool IsCloudflareModel(ModelSpec model) =>
        model.Provider is "cloudflare-workers-ai" or "cloudflare-ai-gateway" ||
        MatchesHost(model.BaseUrl, CloudflareApiHost) ||
        MatchesHost(model.BaseUrl, CloudflareAiGatewayHost);

    /// <summary>TS <c>new URL(baseUrl).hostname === expectedHost</c>; an unparsable URL never matches.</summary>
    private static bool MatchesHost(string baseUrl, string expectedHost) =>
        JsUrl.TryParse(baseUrl) is { } url && url.Hostname == expectedHost;
}
