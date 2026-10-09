using Pi.Ai.Auth.OAuth;

namespace Pi.CodingAgent.Core;

/// <summary>Port of <c>core/radius.ts</c>: the built-in Radius provider's gateway coordinates.</summary>
public static class Radius
{
    /// <summary>Provider id of the built-in Radius provider.</summary>
    public const string ProviderId = "radius";

    /// <summary>Environment variable overriding the gateway origin.</summary>
    public const string EnvRadiusGateway = "PI_RADIUS_GATEWAY";

    /// <summary>MCP endpoint of the gateway the built-in Radius provider signs in to.</summary>
    public static string McpUrl { get; } = $"{RadiusConfig.NormalizeGatewayUrl(RadiusConfig.DefaultRadiusGateway)}/mcp";

    /// <summary>
    /// Injectable <c>process.env</c> lookup; the TS module reads <c>process.env</c> on every call.
    /// </summary>
    public static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>Radius gateway origin, honoring the <see cref="EnvRadiusGateway"/> override.</summary>
    public static string GetGatewayUrl()
        => RadiusConfig.NormalizeGatewayUrl(Env(EnvRadiusGateway) ?? RadiusConfig.DefaultRadiusGateway);
}
