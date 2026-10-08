using System.Runtime.InteropServices;

namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/pi-user-agent.ts</c>.</summary>
/// <remarks>
/// The TS version reports the runtime as <c>bun/&lt;version&gt;</c> or <c>node/&lt;version&gt;</c>.
/// Neither exists here, so the runtime token becomes <c>dotnet/&lt;version&gt;</c>; the rest of the
/// shape (<c>pi/&lt;version&gt; (&lt;platform&gt;; &lt;runtime&gt;; &lt;arch&gt;)</c>) is unchanged.
/// </remarks>
public static class PiUserAgent
{
    /// <summary>Build the <c>User-Agent</c> header value sent to model providers.</summary>
    public static string GetPiUserAgent(string version)
    {
        var runtime = $"dotnet/{RuntimeInformation.FrameworkDescription.Replace(".NET ", string.Empty, StringComparison.Ordinal)}";
        return $"pi/{version} ({ProcessInfo.Platform}; {runtime}; {ProcessInfo.Arch})";
    }
}
