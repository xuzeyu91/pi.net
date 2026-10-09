using System.Diagnostics;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of <c>utils/windows-self-update.ts</c>: on Windows a native addon that is loaded from the package
/// directory cannot be replaced while the process runs, so it is moved aside into a quarantine directory
/// and copied back, leaving an unlocked file at the original path for the updater to overwrite.
/// </summary>
/// <remarks>
/// The two entry points are safe to call on any platform: they do nothing unless an ancestor of the
/// package directory is named <c>node_modules</c>, which is where the quarantine directory lives.
/// </remarks>
public static class WindowsSelfUpdate
{
    private const string QuarantineDirName = ".pi-native-quarantine";

    private static Func<IReadOnlyList<string>?>? _loadedSharedObjectsOverride;

    /// <summary>
    /// The native libraries the runtime has loaded, standing in for Node's
    /// <c>process.report.getReport().sharedObjects</c>.
    /// </summary>
    /// <remarks>
    /// The default reads the process module list on Windows and reports nothing elsewhere. .NET has no
    /// portable way to enumerate loaded native libraries, and the module only has work to do on Windows
    /// anyway, so the non-Windows answer is "nothing is loaded" rather than a list of <c>.so</c> files.
    /// </remarks>
    internal static Func<IReadOnlyList<string>?>? LoadedSharedObjectsOverride
    {
        get => _loadedSharedObjectsOverride;
        set => _loadedSharedObjectsOverride = value;
    }

    /// <summary>
    /// Remove a quarantine directory left behind by an earlier run.
    /// </summary>
    /// <remarks>
    /// A previous pi process may still be exiting and holding a native addon, which makes the delete fail;
    /// that is not an error here, because the next self-update will retry it.
    /// </remarks>
    public static void CleanupQuarantine(string packageDir)
    {
        var quarantineRoot = GetQuarantineRoot(packageDir);
        if (quarantineRoot is null)
        {
            return;
        }

        try
        {
            Directory.Delete(quarantineRoot, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Move the loaded native dependencies of <paramref name="packageDir"/> into quarantine.</summary>
    public static void QuarantineNativeDependencies(string packageDir)
    {
        var resolvedPackageDir = NormalizePath(packageDir);
        var quarantineRoot = GetQuarantineRoot(resolvedPackageDir);
        if (quarantineRoot is null)
        {
            return;
        }

        var loadedFiles = GetLoadedSharedObjectsInPackageDir(resolvedPackageDir);
        if (loadedFiles.Count == 0)
        {
            return;
        }

        var quarantineRunDir = NodePath.Join(
            quarantineRoot,
            $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Environment.ProcessId}-{Guid.NewGuid()}");

        foreach (var loadedFile in loadedFiles)
        {
            if (!File.Exists(loadedFile))
            {
                continue;
            }

            var quarantinePath = NodePath.Join(quarantineRunDir, NodePath.Relative(resolvedPackageDir, loadedFile));
            Directory.CreateDirectory(NodePath.Dirname(quarantinePath));

            // The run directory is unique, so the destination cannot already exist and the platform
            // difference between POSIX rename and MoveFileEx does not arise.
            File.Move(loadedFile, quarantinePath);
            File.Copy(quarantinePath, loadedFile, overwrite: true);
        }
    }

    private static string NormalizePath(string path) => NodePath.ToNamespacedPath(NodePath.Resolve(path));

    /// <summary>
    /// The quarantine directory for a package directory, found by walking up to the nearest
    /// <c>node_modules</c> ancestor, or <see langword="null"/> when there is none.
    /// </summary>
    private static string? GetQuarantineRoot(string packageDir)
    {
        var current = NodePath.Resolve(packageDir);
        while (true)
        {
            if (string.Equals(JsString.ToLowerCase(NodePath.Basename(current)), "node_modules", StringComparison.Ordinal))
            {
                return NodePath.Join(current, QuarantineDirName);
            }

            var parent = NodePath.Dirname(current);
            if (parent == current)
            {
                return null;
            }

            current = parent;
        }
    }

    /// <summary>
    /// The loaded native libraries that live under the package directory, de-duplicated by the
    /// case-folded path.
    /// </summary>
    private static IReadOnlyList<string> GetLoadedSharedObjectsInPackageDir(string packageDir)
    {
        var sharedObjects = (_loadedSharedObjectsOverride?.Invoke() ?? DefaultLoadedSharedObjects());
        if (sharedObjects is null)
        {
            return [];
        }

        var root = JsString.ToLowerCase(NormalizePath(packageDir));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var loadedFiles = new List<string>();

        foreach (var value in sharedObjects)
        {
            var filePath = NormalizePath(value);
            var comparisonPath = JsString.ToLowerCase(filePath);
            if (Paths.GetCwdRelativePath(comparisonPath, root) is null || !seen.Add(comparisonPath))
            {
                continue;
            }

            loadedFiles.Add(filePath);
        }

        return loadedFiles;
    }

    /// <summary>The process module list, or <see langword="null"/> when it cannot be read.</summary>
    private static IReadOnlyList<string>? DefaultLoadedSharedObjects()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var files = new List<string>(process.Modules.Count);
            foreach (ProcessModule module in process.Modules)
            {
                files.Add(module.FileName);
            }

            return files;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
