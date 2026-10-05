using System.Runtime.InteropServices;

namespace Pi.Ai.Utils;

/// <summary>pi 的 User-Agent。对应 TS <c>utils/pi-user-agent.ts</c>（浏览器检测不适用）。</summary>
public static class PiUserAgent
{
    public static string Get()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "pi (browser)";
        }
        return $"pi ({RuntimeInformation.OSDescription} ; {RuntimeInformation.ProcessArchitecture})";
    }
}
