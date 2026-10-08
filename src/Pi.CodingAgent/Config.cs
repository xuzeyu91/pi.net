using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent;

/// <summary>How this installation of the coding agent was installed (port of <c>InstallMethod</c>).</summary>
public enum InstallMethod
{
    /// <summary>A Bun compiled binary.</summary>
    BunBinary,

    /// <summary>npm, or an npm-compatible manager such as bun used as npm.</summary>
    Npm,

    /// <summary>pnpm.</summary>
    Pnpm,

    /// <summary>yarn.</summary>
    Yarn,

    /// <summary>bun.</summary>
    Bun,

    /// <summary>Not recognised.</summary>
    Unknown,
}

/// <summary>One step of a self-update command.</summary>
/// <param name="Command">The executable to run.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="Display">The human-readable form, with arguments containing whitespace quoted.</param>
public sealed record SelfUpdateCommandStep(string Command, IReadOnlyList<string> Args, string Display);

/// <summary>A self-update command, optionally with an uninstall step before the install step.</summary>
/// <param name="Command">The install executable.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="Display">The full display form, joined with <c>&amp;&amp;</c> when there are two steps.</param>
/// <param name="Steps">The ordered steps, when the package was renamed and the old name must be removed.</param>
public sealed record SelfUpdateCommand(
    string Command,
    IReadOnlyList<string> Args,
    string Display,
    IReadOnlyList<SelfUpdateCommandStep>? Steps = null);

/// <summary>What to install, and optionally the package name currently installed.</summary>
/// <param name="PackageName">The package to install.</param>
/// <param name="InstallSpec">The install specifier; defaults to <paramref name="PackageName"/>.</param>
public sealed record SelfUpdatePackageTarget(string PackageName, string? InstallSpec = null)
{
    /// <summary>A bare package name installs itself.</summary>
    public static implicit operator SelfUpdatePackageTarget(string packageName) => new(packageName);
}

/// <summary>A detected change to the installed package (port of <c>InstallChange</c>).</summary>
/// <param name="Kind">Either <c>"updated"</c> or <c>"removed"</c>.</param>
/// <param name="Version">The new version, for <c>"updated"</c>.</param>
public sealed record InstallChange(string Kind, string? Version = null);

/// <summary>
/// Test seams for the parts of <see cref="Config"/> that read the environment, spawn package managers,
/// or probe the install directory. Production callers pass nothing and get the real behaviour; the
/// seam-taking overloads are <see langword="internal"/> so this never widens the public surface.
/// </summary>
internal sealed record ConfigSeams
{
    /// <summary>Environment lookup; defaults to the process environment.</summary>
    public Func<string, string?>? Env { get; init; }

    /// <summary>Replacement for running a command and reading its output.</summary>
    public Func<string, IReadOnlyList<string>, string?>? ReadCommandOutput { get; init; }

    /// <summary>Replacement for <see cref="Config.GetPackageDir()"/>.</summary>
    public string? PackageDir { get; init; }

    /// <summary>Replacement for the directory the module lives in.</summary>
    public string? DirName { get; init; }

    /// <summary>Replacement for <c>process.execPath</c>.</summary>
    public string? ExecPath { get; init; }

    /// <summary>Replacement for <c>process.argv[1]</c>.</summary>
    public string? Entrypoint { get; init; }

    /// <summary>Replacement for the writability probe on the install directory.</summary>
    public bool? PathWritable { get; init; }

    /// <summary>Replacement for the Bun-binary detection.</summary>
    public bool? IsBunBinary { get; init; }

    /// <summary>Replacement for the Bun-runtime detection.</summary>
    public bool? IsBunRuntime { get; init; }
}

/// <summary>
/// Port of <c>src/config.ts</c>: where the package and its assets live, and how this installation was
/// installed so it can update itself.
/// </summary>
/// <remarks>
/// <para>
/// Deviation from TS: the TS module derives the package directory by walking up from its own file
/// looking for a <c>package.json</c>, and reads <c>package.json</c> at import time for the app name,
/// config directory and version. There is no <c>package.json</c> in a .NET deployment, so the walk
/// starts at <see cref="AppContext.BaseDirectory"/> (where the assembly lives) and the package metadata
/// falls back to the same defaults the TS module uses when the file is missing. Setting
/// <c>PI_PACKAGE_DIR</c> still overrides everything, which is the supported escape hatch for a host that
/// ships its assets elsewhere.
/// </para>
/// <para>
/// Deviation from TS: <c>getQuickJSWasmPath</c>, <c>resolveCodemodeWorkerSpecifier</c> and
/// <c>getCodemodeWorkerSpecifier</c> locate the QuickJS wasm module and the codemode worker entrypoint so
/// they can be handed to a Node <c>Worker</c>. The .NET port runs codemode through the injected
/// <c>ICodemodeJsEngine</c>, so there is nothing to resolve and those functions are not ported.
/// </para>
/// </remarks>
public static partial class Config
{
    private const string DefaultPackageName = "@earendil-works/pi-coding-agent";
    private const string DefaultAppName = "pi";
    private const string DefaultConfigDirName = ".pi";
    private const string DefaultVersion = "0.0.0";
    private const string DefaultShareViewerUrl = "https://pi.dev/session/";

    /// <summary>PowerShell arguments used by the <c>powershell</c> tool.</summary>
    public static readonly IReadOnlyList<string> PowerShellArgs =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"];

    // A Windows `.pnpm` install root: `<prefix>/global/<name>/.pnpm/...`
    private static readonly Regex PnpmGlobalRoot = new(@"^(.*[\\/]global[\\/][^\\/]+)[\\/]\.pnpm[\\/]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceArgument = new(@"\s", RegexOptions.Compiled);

    private static readonly Lazy<PackageMetadata> Metadata = new(ReadPackageMetadata);

    /// <summary>The package name, from <c>package.json</c> or the built-in default.</summary>
    public static string PackageName => Metadata.Value.Name;

    /// <summary>The app name, from <c>piConfig.name</c> or <c>"pi"</c>.</summary>
    public static string AppName => Metadata.Value.AppName;

    /// <summary>The app title: the configured name when set, otherwise the <c>π</c> glyph.</summary>
    public static string AppTitle => Metadata.Value.AppTitle;

    /// <summary>The configuration directory name, <c>".pi"</c> by default.</summary>
    public static string ConfigDirName => Metadata.Value.ConfigDirName;

    /// <summary>The version, from <c>package.json</c> or <c>"0.0.0"</c>.</summary>
    public static string Version => Metadata.Value.Version;

    /// <summary>Environment variable naming the agent config directory, e.g. <c>PI_CODING_AGENT_DIR</c>.</summary>
    public static string EnvAgentDir => AppName.ToUpperInvariant() + "_CODING_AGENT_DIR";

    /// <summary>Environment variable naming the session directory, e.g. <c>PI_CODING_AGENT_SESSION_DIR</c>.</summary>
    public static string EnvSessionDir => AppName.ToUpperInvariant() + "_CODING_AGENT_SESSION_DIR";

    /// <summary>Whether the process is a Bun compiled binary. Always <see langword="false"/> in the port.</summary>
    public static bool IsBunBinary => false;

    /// <summary>Whether Bun is the runtime. Always <see langword="false"/> in the port.</summary>
    public static bool IsBunRuntime => false;

    /// <summary>Whether this is the esbuild-bundled Node distribution. Always <see langword="false"/> in the port.</summary>
    public static bool IsBundledNode => false;

    private static string? StartupPackageJsonPath => Metadata.Value.PackageJsonPath;

    // ---------------------------------------------------------------------------------------------
    // Install method detection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Classify an install from the combined <c>dir + "\0" + execPath</c> probe string. Extracted from
    /// <see cref="DetectInstallMethod()"/> so the classification can be exercised directly.
    /// </summary>
    internal static InstallMethod ClassifyInstallMethod(string resolvedPath, bool isBunRuntime)
    {
        if (resolvedPath.Contains("/pnpm/", StringComparison.Ordinal) ||
            resolvedPath.Contains("/.pnpm/", StringComparison.Ordinal))
        {
            return InstallMethod.Pnpm;
        }

        if (resolvedPath.Contains("/yarn/", StringComparison.Ordinal) ||
            resolvedPath.Contains("/.yarn/", StringComparison.Ordinal))
        {
            return InstallMethod.Yarn;
        }

        if (isBunRuntime || resolvedPath.Contains("/install/global/node_modules/", StringComparison.Ordinal))
        {
            return InstallMethod.Bun;
        }

        if (resolvedPath.Contains("/npm/", StringComparison.Ordinal) ||
            resolvedPath.Contains("/node_modules/", StringComparison.Ordinal))
        {
            return InstallMethod.Npm;
        }

        return InstallMethod.Unknown;
    }

    /// <summary>Detect how this installation was installed.</summary>
    public static InstallMethod DetectInstallMethod() => DetectInstallMethod(null);

    internal static InstallMethod DetectInstallMethod(ConfigSeams? seams)
    {
        if (seams?.IsBunBinary ?? IsBunBinary)
        {
            return InstallMethod.BunBinary;
        }

        var dirName = seams?.DirName ?? AppContext.BaseDirectory;
        var execPath = seams?.ExecPath ?? Environment.ProcessPath ?? string.Empty;
        var resolvedPath = (dirName + "\0" + execPath).ToLowerInvariant().Replace('\\', '/');
        return ClassifyInstallMethod(resolvedPath, seams?.IsBunRuntime ?? IsBunRuntime);
    }

    // ---------------------------------------------------------------------------------------------
    // Self-update commands
    // ---------------------------------------------------------------------------------------------

    /// <summary>Resolve a target to its package name and install specifier.</summary>
    internal static SelfUpdatePackageTarget NormalizeSelfUpdatePackageTarget(SelfUpdatePackageTarget target)
        => new(target.PackageName, target.InstallSpec ?? target.PackageName);

    /// <summary>Build a command step, quoting arguments that contain whitespace for display.</summary>
    internal static SelfUpdateCommandStep MakeSelfUpdateCommandStep(string command, IReadOnlyList<string> args)
    {
        var display = string.Join(' ', new[] { command }.Concat(
            args.Select(arg => WhitespaceArgument.IsMatch(arg) ? "\"" + arg + "\"" : arg)));
        return new SelfUpdateCommandStep(command, args, display);
    }

    /// <summary>Combine an install step with an optional preceding uninstall step.</summary>
    internal static SelfUpdateCommand MakeSelfUpdateCommand(SelfUpdateCommandStep installStep, SelfUpdateCommandStep? uninstallStep)
    {
        if (uninstallStep is null)
        {
            return new SelfUpdateCommand(installStep.Command, installStep.Args, installStep.Display);
        }

        return new SelfUpdateCommand(
            installStep.Command,
            installStep.Args,
            uninstallStep.Display + " && " + installStep.Display,
            [uninstallStep, installStep]);
    }

    /// <summary>
    /// The self-update command for a known install method, or <see langword="null"/>. The package
    /// directory and command runner are parameters so the corpus can drive this deterministically.
    /// </summary>
    internal static SelfUpdateCommand? GetSelfUpdateCommandForMethod(
        InstallMethod method,
        string installedPackageName,
        SelfUpdatePackageTarget updatePackageTarget,
        IReadOnlyList<string>? npmCommand,
        string packageDir,
        Func<string, string?> env,
        Func<string, IReadOnlyList<string>, string?> readCommandOutput)
    {
        var target = NormalizeSelfUpdatePackageTarget(updatePackageTarget);

        switch (method)
        {
            case InstallMethod.BunBinary:
                return null;

            case InstallMethod.Pnpm:
            {
                var root = readCommandOutput("pnpm", ["root", "-g"]);
                var match = root is not null ? null : PnpmGlobalRoot.Match(packageDir);
                var binDirArgs = match is { Success: true }
                    ? new[] { "--config.global-bin-dir=" + (env("PNPM_HOME") ?? NodePath.Dirname(NodePath.Dirname(match.Groups[1].Value))) }
                    : [];
                var install = MakeSelfUpdateCommandStep(
                    "pnpm",
                    ["install", "-g", "--ignore-scripts", "--config.minimumReleaseAge=0", .. binDirArgs, target.InstallSpec!]);
                var uninstall = target.PackageName == installedPackageName
                    ? null
                    : MakeSelfUpdateCommandStep("pnpm", ["remove", "-g", .. binDirArgs, installedPackageName]);
                return MakeSelfUpdateCommand(install, uninstall);
            }

            case InstallMethod.Yarn:
            {
                var install = MakeSelfUpdateCommandStep("yarn", ["global", "add", "--ignore-scripts", target.InstallSpec!]);
                var uninstall = target.PackageName == installedPackageName
                    ? null
                    : MakeSelfUpdateCommandStep("yarn", ["global", "remove", installedPackageName]);
                return MakeSelfUpdateCommand(install, uninstall);
            }

            case InstallMethod.Bun:
            {
                var install = MakeSelfUpdateCommandStep(
                    "bun",
                    ["install", "-g", "--ignore-scripts", "--minimum-release-age=0", target.InstallSpec!]);
                var uninstall = target.PackageName == installedPackageName
                    ? null
                    : MakeSelfUpdateCommandStep("bun", ["uninstall", "-g", installedPackageName]);
                return MakeSelfUpdateCommand(install, uninstall);
            }

            case InstallMethod.Npm:
            {
                var configured = npmCommand is { Count: > 0 };
                var command = configured ? npmCommand![0] : "npm";
                var npmArgs = configured ? npmCommand!.Skip(1).ToList() : [];
                var inferred = configured ? null : GetInferredNpmInstall(packageDir);
                var prefixArgs = new List<string>(npmArgs);
                if (inferred is { } install)
                {
                    prefixArgs.Add("--prefix");
                    prefixArgs.Add(install.Prefix);
                }

                // pi.dev advertises releases immediately, so a configured npm age gate would block the
                // update. npm has no per-package age gate, so this also lets new transitive dependency
                // releases through. Managed installs avoid this.
                var installStep = MakeSelfUpdateCommandStep(
                    command,
                    [.. prefixArgs, "install", "-g", "--ignore-scripts", "--min-release-age=0", target.InstallSpec!]);
                var uninstallStep = target.PackageName == installedPackageName
                    ? null
                    : MakeSelfUpdateCommandStep(command, [.. prefixArgs, "uninstall", "-g", installedPackageName]);
                return MakeSelfUpdateCommand(installStep, uninstallStep);
            }

            default:
                return null;
        }
    }

    /// <summary>The self-update command, or <see langword="null"/> when this install cannot self-update.</summary>
    public static SelfUpdateCommand? GetSelfUpdateCommand(
        string packageName,
        IReadOnlyList<string>? npmCommand = null,
        SelfUpdatePackageTarget? updatePackageTarget = null)
        => GetSelfUpdateCommand(packageName, npmCommand, updatePackageTarget, null);

    internal static SelfUpdateCommand? GetSelfUpdateCommand(
        string packageName,
        IReadOnlyList<string>? npmCommand,
        SelfUpdatePackageTarget? updatePackageTarget,
        ConfigSeams? seams)
    {
        var target = updatePackageTarget ?? new SelfUpdatePackageTarget(packageName);
        var method = DetectInstallMethod(seams);
        var command = BuildSelfUpdateCommandForMethod(method, packageName, target, npmCommand, seams);
        if (command is null ||
            !IsManagedByGlobalPackageManager(method, packageName, npmCommand, seams) ||
            !IsSelfUpdatePathWritable(seams))
        {
            return null;
        }

        return command;
    }

    /// <summary>Explain why this installation cannot self-update.</summary>
    public static string GetSelfUpdateUnavailableInstruction(
        string packageName,
        IReadOnlyList<string>? npmCommand = null,
        SelfUpdatePackageTarget? updatePackageTarget = null)
        => GetSelfUpdateUnavailableInstruction(packageName, npmCommand, updatePackageTarget, null);

    internal static string GetSelfUpdateUnavailableInstruction(
        string packageName,
        IReadOnlyList<string>? npmCommand,
        SelfUpdatePackageTarget? updatePackageTarget,
        ConfigSeams? seams)
    {
        var method = DetectInstallMethod(seams);
        var target = NormalizeSelfUpdatePackageTarget(updatePackageTarget ?? new SelfUpdatePackageTarget(packageName));
        if (method == InstallMethod.BunBinary)
        {
            return "Download from: https://github.com/earendil-works/pi/releases/latest";
        }

        var command = BuildSelfUpdateCommandForMethod(method, packageName, target, npmCommand, seams);
        if (command is not null)
        {
            if (IsManagedByGlobalPackageManager(method, packageName, npmCommand, seams) && !IsSelfUpdatePathWritable(seams))
            {
                return $"This installation is managed by a global {MethodName(method)} install, but the install path is not writable. Update it yourself with: {command.Display}";
            }

            return $"This installation is not managed by a global {MethodName(method)} install. Update it with the package manager, wrapper, or source checkout that provides it.";
        }

        return $"Update {target.InstallSpec} using the package manager, wrapper, or source checkout that provides this installation.";
    }

    /// <summary>The user-facing update instruction.</summary>
    public static string GetUpdateInstruction(string packageName) => GetUpdateInstruction(packageName, null);

    internal static string GetUpdateInstruction(string packageName, ConfigSeams? seams)
    {
        var method = DetectInstallMethod(seams);
        var command = BuildSelfUpdateCommandForMethod(method, packageName, new SelfUpdatePackageTarget(packageName), null, seams);
        return command is not null
            ? $"Run: {command.Display}"
            : GetSelfUpdateUnavailableInstruction(packageName, null, null, seams);
    }

    private static string MethodName(InstallMethod method) => method switch
    {
        InstallMethod.BunBinary => "bun-binary",
        InstallMethod.Npm => "npm",
        InstallMethod.Pnpm => "pnpm",
        InstallMethod.Yarn => "yarn",
        InstallMethod.Bun => "bun",
        _ => "unknown",
    };

    /// <summary>Build the command, wiring the real command runner unless a seam supplies one.</summary>
    private static SelfUpdateCommand? BuildSelfUpdateCommandForMethod(
        InstallMethod method,
        string installedPackageName,
        SelfUpdatePackageTarget target,
        IReadOnlyList<string>? npmCommand,
        ConfigSeams? seams)
    {
        var packageDir = seams?.PackageDir ?? GetPackageDir(seams);
        var env = seams?.Env ?? Environment.GetEnvironmentVariable;
        var reader = seams?.ReadCommandOutput
                     ?? ((command, args) => ReadCommandOutput(command, args, requireSuccess: false, seams));
        return GetSelfUpdateCommandForMethod(method, installedPackageName, target, npmCommand, packageDir, env, reader);
    }

    /// <summary>Run a command and return its trimmed standard output, or <see langword="null"/>.</summary>
    private static string? ReadCommandOutput(
        string command,
        IReadOnlyList<string> args,
        bool requireSuccess,
        ConfigSeams? seams)
    {
        if (seams?.ReadCommandOutput is { } reader)
        {
            return reader(command, args);
        }

        var result = ChildProcess.SpawnSync(command, args, new SpawnSyncOptions
        {
            Encoding = "utf-8",
            Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
        });

        if (result.Status == 0)
        {
            var trimmed = result.Stdout.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        if (requireSuccess)
        {
            var reason = result.Error?.Message is { Length: > 0 } message
                ? message
                : result.Stderr.Trim() is { Length: > 0 } stderr
                    ? stderr
                    : $"exit code {result.Status?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
            throw new InvalidOperationException(
                $"Failed to run {string.Join(' ', new[] { command }.Concat(args))}: {reason}");
        }

        return null;
    }

    private static (string Root, string Prefix)? GetInferredNpmInstall(string packageDir)
    {
        var parent = NodePath.Dirname(packageDir);
        string? root = null;
        if (NodePath.Basename(parent).StartsWith('@') && NodePath.Basename(NodePath.Dirname(parent)) == "node_modules")
        {
            root = NodePath.Dirname(parent);
        }
        else if (NodePath.Basename(parent) == "node_modules")
        {
            root = parent;
        }

        if (root is null)
        {
            return null;
        }

        var rootParent = NodePath.Dirname(root);
        // Windows global npm prefixes use `<prefix>\node_modules`, which is indistinguishable from a
        // local project install by path shape alone, so only the Unix `<prefix>/lib/node_modules` shape
        // is inferred without `npm root -g` evidence.
        return NodePath.Basename(rootParent) == "lib" ? (root, NodePath.Dirname(rootParent)) : null;
    }

    private static IReadOnlyList<string> GetGlobalPackageRoots(
        InstallMethod method,
        IReadOnlyList<string>? npmCommand,
        ConfigSeams? seams)
    {
        var packageDir = seams?.PackageDir ?? GetPackageDir(seams);
        var home = Paths.GetHomeDirectory();

        switch (method)
        {
            case InstallMethod.Npm:
            {
                var configured = npmCommand is { Count: > 0 };
                var command = configured ? npmCommand![0] : "npm";
                var npmArgs = configured ? npmCommand!.Skip(1).ToList() : [];
                if (configured && command == "bun")
                {
                    var bunBin = ReadCommandOutput(command, [.. npmArgs, "pm", "bin", "-g"], requireSuccess: true, seams);
                    var roots = new List<string> { NodePath.Join(home, ".bun", "install", "global", "node_modules") };
                    if (!string.IsNullOrEmpty(bunBin))
                    {
                        roots.Add(NodePath.Join(NodePath.Dirname(bunBin), "install", "global", "node_modules"));
                    }

                    return roots;
                }

                var root = ReadCommandOutput(command, [.. npmArgs, "root", "-g"], requireSuccess: configured, seams);
                var inferred = configured ? null : GetInferredNpmInstall(packageDir);
                var all = new List<string>();
                if (root is not null)
                {
                    all.Add(root);
                }

                if (inferred is { } install)
                {
                    all.Add(install.Root);
                }

                return all;
            }

            case InstallMethod.Pnpm:
            {
                var root = ReadCommandOutput("pnpm", ["root", "-g"], requireSuccess: false, seams);
                if (root is not null)
                {
                    return [root, NodePath.Dirname(root)];
                }

                var match = PnpmGlobalRoot.Match(packageDir);
                return match.Success ? [match.Groups[1].Value] : [];
            }

            case InstallMethod.Yarn:
            {
                var dir = ReadCommandOutput("yarn", ["global", "dir"], requireSuccess: false, seams);
                return dir is not null ? [dir, NodePath.Join(dir, "node_modules")] : [];
            }

            case InstallMethod.Bun:
            {
                var bunBin = ReadCommandOutput("bun", ["pm", "bin", "-g"], requireSuccess: false, seams);
                var roots = new List<string> { NodePath.Join(home, ".bun", "install", "global", "node_modules") };
                if (!string.IsNullOrEmpty(bunBin))
                {
                    roots.Add(NodePath.Join(NodePath.Dirname(bunBin), "install", "global", "node_modules"));
                }

                return roots;
            }

            default:
                return [];
        }
    }

    private static string? NormalizeExistingPathForComparison(string path, bool resolveSymlinks)
    {
        var resolvedPath = NodePath.Resolve(path);
        if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
        {
            return null;
        }

        var normalizedPath = resolveSymlinks ? Paths.CanonicalizePath(resolvedPath) : resolvedPath;
        return NodePath.IsWindows ? normalizedPath.ToLowerInvariant() : normalizedPath;
    }

    private static IReadOnlyList<string> GetPathComparisonCandidates(string path)
    {
        var candidates = new List<string>();
        foreach (var candidate in new[]
                 {
                     NormalizeExistingPathForComparison(path, resolveSymlinks: false),
                     NormalizeExistingPathForComparison(path, resolveSymlinks: true),
                 })
        {
            if (candidate is not null && !candidates.Contains(candidate, StringComparer.Ordinal))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static string? GetEntrypointPackageDir(ConfigSeams? seams)
    {
        var entrypoint = seams?.Entrypoint ?? Environment.GetCommandLineArgs().FirstOrDefault();
        if (string.IsNullOrEmpty(entrypoint))
        {
            return null;
        }

        var dir = NodePath.Dirname(entrypoint);
        while (dir != NodePath.Dirname(dir))
        {
            if (File.Exists(NodePath.Join(dir, "package.json")))
            {
                return dir;
            }

            dir = NodePath.Dirname(dir);
        }

        return null;
    }

    private static bool IsSelfUpdatePathWritable(ConfigSeams? seams)
    {
        if (seams?.PathWritable is bool writable)
        {
            return writable;
        }

        var packageDir = GetPackageDir(seams);
        return IsWritable(packageDir) && IsWritable(NodePath.Dirname(packageDir));
    }

    /// <summary>
    /// Node's <c>accessSync(path, W_OK)</c>. .NET has no equivalent, so this asks the OS directly:
    /// <c>access(2)</c> on Unix, and on Windows a write-open of the directory handle (directories need
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c>), which runs the real ACL check without creating anything.
    /// </summary>
    private static bool IsWritable(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        if (!NodePath.IsWindows)
        {
            return UnixAccess(path, 2) == 0;
        }

        const uint GenericWrite = 0x40000000;
        const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
        const uint OpenExisting = 3;
        const uint FlagBackupSemantics = 0x02000000;

        var handle = CreateFileW(path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return false;
        }

        CloseHandle(handle);
        return true;
    }

    private static bool IsManagedByGlobalPackageManager(
        InstallMethod method,
        string packageName,
        IReadOnlyList<string>? npmCommand,
        ConfigSeams? seams)
    {
        var packageDirs = new List<string> { seams?.PackageDir ?? GetPackageDir(seams) };
        if (GetEntrypointPackageDir(seams) is string entrypointDir)
        {
            packageDirs.Add(entrypointDir);
        }

        var packageDirCandidates = packageDirs.SelectMany(GetPathComparisonCandidates).ToList();
        var separator = NodePath.Separator;

        return GetGlobalPackageRoots(method, npmCommand, seams).Any(root =>
            GetPathComparisonCandidates(root).Any(normalizedRoot =>
            {
                var rootPrefix = normalizedRoot.EndsWith(separator) ? normalizedRoot : normalizedRoot + separator;
                return packageDirCandidates.Any(candidate => candidate.StartsWith(rootPrefix, StringComparison.Ordinal));
            }));
    }

    // ---------------------------------------------------------------------------------------------
    // Package asset paths
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The nearest directory at or above <paramref name="startDir"/> that holds a <c>package.json</c>,
    /// preferring the package root when the search lands on a <c>dist</c> directory.
    /// </summary>
    /// <remarks>
    /// <c>build:binary</c> places Bun's metadata inside <c>dist/</c>; the package root is returned so
    /// Node's dist-relative asset paths do not become <c>dist/dist/</c>.
    /// </remarks>
    public static string FindNodePackageDir(string startDir)
    {
        var dir = startDir;
        while (dir != NodePath.Dirname(dir))
        {
            if (File.Exists(NodePath.Join(dir, "package.json")))
            {
                var parent = NodePath.Dirname(dir);
                if (NodePath.Basename(dir) == "dist" && File.Exists(NodePath.Join(parent, "package.json")))
                {
                    return parent;
                }

                return dir;
            }

            dir = NodePath.Dirname(dir);
        }

        return startDir;
    }

    /// <summary>The base directory for package assets.</summary>
    public static string GetPackageDir() => GetPackageDir(null);

    internal static string GetPackageDir(ConfigSeams? seams)
    {
        if (seams?.PackageDir is string overrideDir)
        {
            return overrideDir;
        }

        var envDir = (seams?.Env ?? Environment.GetEnvironmentVariable)("PI_PACKAGE_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            return Paths.NormalizePath(envDir);
        }

        var isBunBinary = seams?.IsBunBinary ?? IsBunBinary;
        if (isBunBinary)
        {
            return NodePath.Dirname(seams?.ExecPath ?? Environment.ProcessPath ?? AppContext.BaseDirectory);
        }

        return FindNodePackageDir(seams?.DirName ?? AppContext.BaseDirectory);
    }

    /// <summary>The directory holding the built-in interactive themes.</summary>
    public static string GetThemesDir() => GetThemesDir(null);

    internal static string GetThemesDir(ConfigSeams? seams) => AssetDir(seams, "theme", "modes", "interactive", "theme");

    /// <summary>The directory holding the HTML export template.</summary>
    public static string GetExportTemplateDir() => GetExportTemplateDir(null);

    internal static string GetExportTemplateDir(ConfigSeams? seams) => AssetDir(seams, "export-html", "core", "export-html");

    /// <summary>The directory holding the built-in interactive assets.</summary>
    public static string GetInteractiveAssetsDir() => GetInteractiveAssetsDir(null);

    internal static string GetInteractiveAssetsDir(ConfigSeams? seams) => AssetDir(seams, "assets", "modes", "interactive", "assets");

    /// <summary>The path of a bundled interactive asset.</summary>
    public static string GetBundledInteractiveAssetPath(string name) => GetBundledInteractiveAssetPath(name, null);

    internal static string GetBundledInteractiveAssetPath(string name, ConfigSeams? seams)
        => NodePath.Join(GetInteractiveAssetsDir(seams), name);

    private static string AssetDir(ConfigSeams? seams, string binarySubdirectory, params string[] sourceParts)
    {
        var packageDir = GetPackageDir(seams);
        if (seams?.IsBunBinary ?? IsBunBinary)
        {
            return NodePath.Join(packageDir, binarySubdirectory);
        }

        var srcOrDist = Directory.Exists(NodePath.Join(packageDir, "src")) ? "src" : "dist";
        return NodePath.Join([packageDir, srcOrDist, .. sourceParts]);
    }

    /// <summary>Path to <c>package.json</c>.</summary>
    public static string GetPackageJsonPath() => NodePath.Join(GetPackageDir(), "package.json");

    /// <summary>Path to <c>README.md</c>.</summary>
    public static string GetReadmePath() => NodePath.Resolve(NodePath.Join(GetPackageDir(), "README.md"));

    /// <summary>Path to the <c>docs</c> directory.</summary>
    public static string GetDocsPath() => NodePath.Resolve(NodePath.Join(GetPackageDir(), "docs"));

    /// <summary>Path to the <c>examples</c> directory.</summary>
    public static string GetExamplesPath() => NodePath.Resolve(NodePath.Join(GetPackageDir(), "examples"));

    /// <summary>Path to <c>CHANGELOG.md</c>.</summary>
    public static string GetChangelogPath() => NodePath.Resolve(NodePath.Join(GetPackageDir(), "CHANGELOG.md"));

    /// <summary>Expand a leading <c>~</c> and normalise a path.</summary>
    public static string ExpandTildePath(string path) => Paths.NormalizePath(path);

    /// <summary>The share viewer URL for a gist id.</summary>
    public static string GetShareViewerUrl(string gistId) => GetShareViewerUrl(gistId, null);

    internal static string GetShareViewerUrl(string gistId, ConfigSeams? seams)
    {
        var baseUrl = (seams?.Env ?? Environment.GetEnvironmentVariable)("PI_SHARE_VIEWER_URL");
        return (string.IsNullOrEmpty(baseUrl) ? DefaultShareViewerUrl : baseUrl) + "#" + gistId;
    }

    // ---------------------------------------------------------------------------------------------
    // User config paths (~/.pi/agent/*)
    // ---------------------------------------------------------------------------------------------

    /// <summary>The agent config directory, e.g. <c>~/.pi/agent/</c>.</summary>
    public static string GetAgentDir() => GetAgentDir(null);

    internal static string GetAgentDir(ConfigSeams? seams)
    {
        var envDir = (seams?.Env ?? Environment.GetEnvironmentVariable)(EnvAgentDir);
        if (!string.IsNullOrEmpty(envDir))
        {
            return ExpandTildePath(envDir);
        }

        return NodePath.Join(Paths.GetHomeDirectory(), ConfigDirName, "agent");
    }

    /// <summary>The user's custom themes directory.</summary>
    public static string GetCustomThemesDir() => NodePath.Join(GetAgentDir(), "themes");

    /// <summary>Path to <c>models.json</c>.</summary>
    public static string GetModelsPath() => NodePath.Join(GetAgentDir(), "models.json");

    /// <summary>Path to <c>auth.json</c>.</summary>
    public static string GetAuthPath() => NodePath.Join(GetAgentDir(), "auth.json");

    /// <summary>Path to <c>settings.json</c>.</summary>
    public static string GetSettingsPath() => NodePath.Join(GetAgentDir(), "settings.json");

    /// <summary>The tools directory.</summary>
    public static string GetToolsDir() => NodePath.Join(GetAgentDir(), "tools");

    /// <summary>The managed binaries directory (fd, rg).</summary>
    public static string GetBinDir() => NodePath.Join(GetAgentDir(), "bin");

    /// <summary>The prompt templates directory.</summary>
    public static string GetPromptsDir() => NodePath.Join(GetAgentDir(), "prompts");

    /// <summary>The sessions directory.</summary>
    public static string GetSessionsDir() => NodePath.Join(GetAgentDir(), "sessions");

    /// <summary>The debug log file.</summary>
    public static string GetDebugLogPath() => NodePath.Join(GetAgentDir(), AppName + "-debug.log");

    // ---------------------------------------------------------------------------------------------
    // Install-change detection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Detect that the package this process runs from changed on disk, for example after <c>pi update</c>
    /// in another terminal, so code loaded on demand can be missing or from another version.
    /// </summary>
    /// <remarks>
    /// The <c>package.json</c> read at startup is re-read here. Resolving it again would walk up past a
    /// deleted install and could find an unrelated <c>package.json</c>, such as one in the home
    /// directory.
    /// </remarks>
    public static InstallChange? DetectInstallChange(string? packageJsonPath = null)
        => DetectInstallChange(packageJsonPath, null);

    internal static InstallChange? DetectInstallChange(string? packageJsonPath, ConfigSeams? seams)
    {
        // The Bun binary embeds its code, so replacing the executable does not affect this process.
        if (seams?.IsBunBinary ?? IsBunBinary)
        {
            return null;
        }

        var path = packageJsonPath ?? StartupPackageJsonPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new InstallChange("removed");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var version = ReadVersionFromJson(Text.StripBom(content));
        return version is not null && version != Version ? new InstallChange("updated", version) : null;
    }

    // ---------------------------------------------------------------------------------------------
    // package.json metadata
    // ---------------------------------------------------------------------------------------------

    private sealed record PackageMetadata(
        string Name,
        string AppName,
        string AppTitle,
        string ConfigDirName,
        string Version,
        string? PackageJsonPath);

    private static PackageMetadata ReadPackageMetadata()
    {
        string? path = null;
        var name = DefaultPackageName;
        var version = DefaultVersion;
        string? piConfigName = null;
        string? piConfigDir = null;

        try
        {
            path = GetPackageJsonPath();
            using var document = JsonDocument.Parse(Text.StripBom(File.ReadAllText(path)));
            var root = document.RootElement;
            if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString() ?? DefaultPackageName;
            }

            if (root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString() ?? DefaultVersion;
            }

            if (root.TryGetProperty("piConfig", out var configElement) && configElement.ValueKind == JsonValueKind.Object)
            {
                if (configElement.TryGetProperty("name", out var configuredName) && configuredName.ValueKind == JsonValueKind.String)
                {
                    piConfigName = configuredName.GetString();
                }

                if (configElement.TryGetProperty("configDir", out var configuredDir) && configuredDir.ValueKind == JsonValueKind.String)
                {
                    piConfigDir = configuredDir.GetString();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // No readable package.json next to the assembly: fall back to the TS defaults.
            path = null;
        }

        var appName = string.IsNullOrEmpty(piConfigName) ? DefaultAppName : piConfigName;
        return new PackageMetadata(
            string.IsNullOrEmpty(name) ? DefaultPackageName : name,
            appName,
            string.IsNullOrEmpty(piConfigName) ? "π" : appName,
            string.IsNullOrEmpty(piConfigDir) ? DefaultConfigDirName : piConfigDir,
            string.IsNullOrEmpty(version) ? DefaultVersion : version,
            path);
    }

    private static string? ReadVersionFromJson(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("version", out var version) &&
                   version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "access")]
    private static extern int UnixAccess(string path, int mode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
