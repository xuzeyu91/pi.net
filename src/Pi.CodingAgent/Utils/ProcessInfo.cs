using System.Runtime.InteropServices;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Node's <c>process.platform</c> / <c>process.arch</c> expressed over .NET's runtime information.
/// The ported modules use these to build user-visible strings and to pick platform-specific
/// behaviour, so the Node spellings are preserved rather than replaced with .NET names.
/// </summary>
public static class ProcessInfo
{
    /// <summary>The Node <c>process.platform</c> value for the current OS.</summary>
    public static string Platform
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "win32";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "darwin";
            }

            if (OperatingSystem.IsLinux())
            {
                return "linux";
            }

            if (OperatingSystem.IsFreeBSD())
            {
                return "freebsd";
            }

            return RuntimeInformation.OSDescription.Contains("sunos", StringComparison.OrdinalIgnoreCase)
                ? "sunos"
                : "linux";
        }
    }

    /// <summary>The Node <c>process.arch</c> value for the current process.</summary>
    public static string Arch => ArchitectureName(RuntimeInformation.ProcessArchitecture);

    private static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X86 => "ia32",
        Architecture.X64 => "x64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        Architecture.S390x => "s390x",
        Architecture.Ppc64le => "ppc64",
        Architecture.LoongArch64 => "loong64",
        Architecture.Wasm => "wasm32",
        Architecture.Armv6 => "arm",
        _ => architecture.ToString().ToLowerInvariant(),
    };
}
