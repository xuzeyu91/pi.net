using System.Diagnostics;
using System.Globalization;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>A message reported while ensuring a managed tool is available.</summary>
/// <param name="Type">Either <c>info</c> or <c>warning</c>; the caller picks the colour from it.</param>
/// <param name="Message">The text to show.</param>
public sealed record ToolStatus(string Type, string Message)
{
    /// <summary>An informational message.</summary>
    public static ToolStatus Info(string message) => new("info", message);

    /// <summary>A warning; the tool will not be available.</summary>
    public static ToolStatus Warning(string message) => new("warning", message);
}

/// <summary>
/// The parts of Node's <c>spawnSync</c> result this module reads.
/// </summary>
/// <param name="Status">The exit code, or <see langword="null"/> when the process never started.</param>
/// <param name="ErrorMessage">Node's <c>result.error.message</c>, e.g. ENOENT for a missing binary.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
internal sealed record SpawnResult(int? Status, string? ErrorMessage, string Stdout, string Stderr);

/// <summary>
/// Port of <c>utils/tools-manager.ts</c>: resolve <c>fd</c> and <c>ripgrep</c>, downloading a release
/// archive into the managed bin directory when the system has neither.
/// </summary>
public static class ToolsManager
{
    /// <summary>The <c>fd</c> tool key.</summary>
    public const string Fd = "fd";

    /// <summary>The <c>rg</c> tool key.</summary>
    public const string Rg = "rg";

    private const int NetworkTimeoutMs = 10_000;
    private const int DownloadTimeoutMs = 120_000;

    private sealed record ToolConfig(
        string Name,
        string Repo,
        string BinaryName,
        string[]? SystemBinaryNames,
        string TagPrefix,
        Func<string, string, string, string?> GetAssetName);

    private static readonly Dictionary<string, ToolConfig> Tools = new(StringComparer.Ordinal)
    {
        [Fd] = new(
            "fd",
            "sharkdp/fd",
            "fd",
            ["fd", "fdfind"],
            "v",
            static (version, plat, architecture) =>
            {
                var arch = architecture == "arm64" ? "aarch64" : "x86_64";
                return plat switch
                {
                    "darwin" => $"fd-v{version}-{arch}-apple-darwin.tar.gz",
                    "linux" => $"fd-v{version}-{arch}-unknown-linux-musl.tar.gz",
                    "win32" => $"fd-v{version}-{arch}-pc-windows-msvc.zip",
                    _ => null,
                };
            }),
        [Rg] = new(
            "ripgrep",
            "BurntSushi/ripgrep",
            "rg",
            null,
            string.Empty,
            static (version, plat, architecture) =>
            {
                var arch = architecture == "arm64" ? "aarch64" : "x86_64";
                return plat switch
                {
                    "darwin" => $"ripgrep-{version}-{arch}-apple-darwin.tar.gz",
                    "linux" => $"ripgrep-{version}-{arch}-unknown-linux-musl.tar.gz",
                    "win32" => $"ripgrep-{version}-{arch}-pc-windows-msvc.zip",
                    _ => null,
                };
            }),
    };

    /// <summary>Termux package names, for the platforms where a Linux binary cannot run.</summary>
    private static readonly Dictionary<string, string> TermuxPackages = new(StringComparer.Ordinal)
    {
        [Fd] = "fd",
        [Rg] = "ripgrep",
    };

    private static Func<string, string?>? _envOverride;
    private static Func<string>? _toolsDirOverride;
    private static Func<string, string[], SpawnResult>? _spawnOverride;
    private static Func<string>? _platformOverride;
    private static Func<string>? _archOverride;

    /// <summary>Environment lookup, so offline mode and <c>SystemRoot</c> can be driven from tests.</summary>
    internal static Func<string, string?>? EnvOverride
    {
        get => _envOverride;
        set => _envOverride = value;
    }

    /// <summary>
    /// The managed bin directory. The original snapshots <c>getBinDir()</c> at module load, which a static
    /// initializer would reproduce only if the override were set before the first touch; taking it from a
    /// provider keeps the seam usable and is observably identical, because the agent directory cannot
    /// change while the process runs.
    /// </summary>
    internal static Func<string>? ToolsDirOverride
    {
        get => _toolsDirOverride;
        set => _toolsDirOverride = value;
    }

    /// <summary>Process runner, so the extraction failure paths can be exercised without tar or unzip.</summary>
    internal static Func<string, string[], SpawnResult>? SpawnOverride
    {
        get => _spawnOverride;
        set => _spawnOverride = value;
    }

    /// <summary>
    /// Host platform, standing in for <c>os.platform()</c>. The seam is local to this class rather than on
    /// <see cref="ProcessInfo"/> so that overriding it cannot leak into <see cref="NodePath"/>.
    /// </summary>
    internal static Func<string>? PlatformOverride
    {
        get => _platformOverride;
        set => _platformOverride = value;
    }

    /// <summary>Host architecture, standing in for <c>os.arch()</c>.</summary>
    internal static Func<string>? ArchOverride
    {
        get => _archOverride;
        set => _archOverride = value;
    }

    private static Func<string, string?> Env => _envOverride ?? Environment.GetEnvironmentVariable;

    private static string ToolsDir => _toolsDirOverride?.Invoke() ?? Config.GetBinDir();

    private static string Platform => _platformOverride?.Invoke() ?? ProcessInfo.Platform;

    private static string Arch => _archOverride?.Invoke() ?? ProcessInfo.Arch;

    /// <summary>
    /// The path to a managed tool: the copy in the bin directory when present, otherwise the name of a
    /// system binary on <c>PATH</c>, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="tool">One of <see cref="Fd"/> or <see cref="Rg"/>.</param>
    public static string? GetToolPath(string tool)
    {
        if (!Tools.TryGetValue(tool, out var config))
        {
            return null;
        }

        var localPath = NodePath.Join(ToolsDir, config.BinaryName + (Platform == "win32" ? ".exe" : string.Empty));
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // A system binary is returned by name, because it is already on PATH.
        foreach (var systemBinaryName in config.SystemBinaryNames ?? [config.BinaryName])
        {
            if (CommandExists(systemBinaryName))
            {
                return systemBinaryName;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the latest release version from the release-page redirect.
    /// </summary>
    /// <remarks>
    /// The <c>api.github.com</c> releases endpoint counts against the anonymous quota (60 requests/hour
    /// per IP), which is permanently exhausted behind shared egress IPs such as corporate proxies and CI
    /// runners. The web endpoint answers with a redirect to the tagged release at no quota cost and lives
    /// on the same origin as the binary download itself, so redirects are deliberately <em>not</em>
    /// followed.
    /// </remarks>
    public static async Task<string> GetLatestVersionAsync(string repo, CancellationToken cancellationToken = default)
    {
        using var response = await ManagementHttp.FetchWithRetryAsync(
            $"https://github.com/{repo}/releases/latest",
            request => request.Headers.TryAddWithoutValidation("User-Agent", $"{Config.AppName}-coding-agent"),
            new FetchRetryOptions { TimeoutMs = NetworkTimeoutMs, AllowAutoRedirect = false },
            cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var location = status is >= 300 and < 400 && response.Headers.TryGetValues("Location", out var values)
            ? values.FirstOrDefault()
            : null;

        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException($"Failed to resolve latest {repo} release: HTTP {status} without redirect");
        }

        var tag = JsUrl.Resolve("https://github.com", location)?.Pathname.Split('/')[^1];
        if (string.IsNullOrEmpty(tag) || !location.Contains("/releases/tag/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Failed to resolve latest {repo} release: unexpected redirect to {location}");
        }

        var decoded = JsUri.DecodeUriComponent(tag);
        return decoded.StartsWith('v') ? decoded[1..] : decoded;
    }

    /// <summary>
    /// Ensure a tool is available, downloading it if necessary.
    /// </summary>
    /// <remarks>
    /// Progress is reported through <paramref name="onStatus"/>; the messages are otherwise silent.
    /// Returns the tool path, or <see langword="null"/> when it is unavailable.
    /// </remarks>
    public static async Task<string?> EnsureToolAsync(
        string tool,
        Action<ToolStatus>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        var existingPath = GetToolPath(tool);
        if (existingPath is not null)
        {
            return existingPath;
        }

        if (!Tools.TryGetValue(tool, out var config))
        {
            return null;
        }

        if (IsOfflineModeEnabled())
        {
            onStatus?.Invoke(ToolStatus.Warning($"{config.Name} not found. Offline mode enabled, skipping download."));
            return null;
        }

        // On Android/Termux the Linux binaries do not work because of the Bionic libc incompatibility.
        if (Platform == "android")
        {
            var packageName = TermuxPackages.TryGetValue(tool, out var mapped) ? mapped : tool;
            onStatus?.Invoke(ToolStatus.Warning($"{config.Name} not found. Install with: pkg install {packageName}"));
            return null;
        }

        onStatus?.Invoke(ToolStatus.Info($"{config.Name} not found. Downloading..."));

        try
        {
            var path = await DownloadToolAsync(tool, cancellationToken).ConfigureAwait(false);
            onStatus?.Invoke(ToolStatus.Info($"{config.Name} installed to {path}"));
            return path;
        }
        catch (Exception error)
        {
            onStatus?.Invoke(ToolStatus.Warning($"Failed to download {config.Name}: {DescribeFailure(error)}"));
            return null;
        }
    }

    /// <summary>
    /// The error chain rendered as a single message. A fetch failure surfaces as a bare "fetch failed"
    /// with the actionable detail (DNS, TLS, timeout) hidden in the cause, so the chain is walked rather
    /// than just the outermost message. The depth is capped to guard against a circular chain.
    /// </summary>
    private static string DescribeFailure(Exception error)
    {
        var messages = new List<string>();
        var current = error;
        for (var depth = 0; current is not null && depth < 5; depth++)
        {
            if (!messages.Contains(current.Message))
            {
                messages.Add(current.Message);
            }

            current = current.InnerException;
        }

        return messages.Count > 0 ? string.Join(": ", messages) : JavaScriptErrorText(error);
    }

    /// <summary>JavaScript's <c>String(error)</c>, which is the error's <em>name</em> and message.</summary>
    private static string JavaScriptErrorText(Exception error)
    {
        var name = error is AggregateException ? "AggregateError" : "Error";
        return error.Message.Length == 0 ? name : $"{name}: {error.Message}";
    }

    private static bool IsOfflineModeEnabled()
    {
        var value = Env("PI_OFFLINE");
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value == "1"
            || string.Equals(JsString.ToLowerCase(value), "true", StringComparison.Ordinal)
            || string.Equals(JsString.ToLowerCase(value), "yes", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a command exists on <c>PATH</c>. The original spawns it and only treats a <em>spawn</em>
    /// failure as "missing", so a binary that exists but exits non-zero still counts as present.
    /// </summary>
    private static bool CommandExists(string command) => Spawn(command, ["--version"]).ErrorMessage is null;

    private static SpawnResult Spawn(string command, string[] arguments) =>
        _spawnOverride?.Invoke(command, arguments) ?? RunProcess(command, arguments);

    private static SpawnResult RunProcess(string command, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new SpawnResult(null, $"Failed to start {command}", string.Empty, string.Empty);
            }

            // Start draining both pipes before waiting, so a full buffer cannot deadlock the child.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            return new SpawnResult(process.ExitCode, null, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Node reports a missing executable through `result.error` (ENOENT) rather than by throwing.
            return new SpawnResult(null, error.Message, string.Empty, string.Empty);
        }
    }

    private static string FormatSpawnFailure(SpawnResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            return result.ErrorMessage;
        }

        var stderr = result.Stderr.Trim();
        if (stderr.Length > 0)
        {
            return stderr;
        }

        var stdout = result.Stdout.Trim();
        if (stdout.Length > 0)
        {
            return stdout;
        }

        return $"exit status {result.Status?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }

    private static string? RunExtractionCommand(string command, string[] arguments)
    {
        var result = Spawn(command, arguments);
        if (result.ErrorMessage is null && result.Status == 0)
        {
            return null;
        }

        return $"{command}: {FormatSpawnFailure(result)}";
    }

    private static void ExtractTarGzArchive(string archivePath, string extractDir, string assetName)
    {
        var failure = RunExtractionCommand("tar", ["xzf", archivePath, "-C", extractDir]);
        if (failure is not null)
        {
            throw new InvalidOperationException($"Failed to extract {assetName}: {failure}");
        }
    }

    /// <summary>
    /// Windows ships bsdtar as <c>tar.exe</c>, which handles zip files, whereas Git Bash's GNU tar does
    /// not; the System32 copy is therefore preferred when it exists.
    /// </summary>
    private static string GetWindowsTarCommand()
    {
        var systemRoot = Env("SystemRoot") ?? Env("WINDIR");
        if (!string.IsNullOrEmpty(systemRoot))
        {
            var systemTar = NodePath.Join(systemRoot, "System32", "tar.exe");
            if (File.Exists(systemTar))
            {
                return systemTar;
            }
        }

        return "tar.exe";
    }

    private static void ExtractZipArchive(string archivePath, string extractDir, string assetName)
    {
        var failures = new List<string>();

        if (Platform == "win32")
        {
            var tarFailure = RunExtractionCommand(GetWindowsTarCommand(), ["xf", archivePath, "-C", extractDir]);
            if (tarFailure is null)
            {
                return;
            }

            failures.Add(tarFailure);

            const string script =
                "& { param($archive, $destination) $ErrorActionPreference = 'Stop'; Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force }";
            var powershellFailure = RunExtractionCommand("powershell.exe",
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script,
                archivePath,
                extractDir,
            ]);

            if (powershellFailure is null)
            {
                return;
            }

            failures.Add(powershellFailure);
        }
        else
        {
            var unzipFailure = RunExtractionCommand("unzip", ["-q", archivePath, "-d", extractDir]);
            if (unzipFailure is null)
            {
                return;
            }

            failures.Add(unzipFailure);

            var tarFailure = RunExtractionCommand("tar", ["xf", archivePath, "-C", extractDir]);
            if (tarFailure is null)
            {
                return;
            }

            failures.Add(tarFailure);
        }

        throw new InvalidOperationException($"Failed to extract {assetName}: {string.Join("; ", failures)}");
    }

    /// <summary>Depth-first search for a file name, because some archives nest under a versioned folder.</summary>
    private static string? FindBinaryRecursively(string rootDir, string binaryFileName)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var currentDir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                // A reparse point is neither a file nor a directory to `readdirSync`, which does not
                // follow symlinks, so it is skipped rather than descended into.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (Path.GetFileName(entry) == binaryFileName)
                    {
                        return entry;
                    }

                    continue;
                }

                stack.Push(entry);
            }
        }

        return null;
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken cancellationToken)
    {
        using var response = await ManagementHttp.FetchWithRetryAsync(
            url,
            null,
            new FetchRetryOptions { TimeoutMs = DownloadTimeoutMs },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download failed with HTTP {(int)response.StatusCode}: {url}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> DownloadToolAsync(string tool, CancellationToken cancellationToken)
    {
        if (!Tools.TryGetValue(tool, out var config))
        {
            throw new InvalidOperationException($"Unknown tool: {tool}");
        }

        var plat = Platform;
        var architecture = Arch;

        // fd is pinned on darwin/x64, so the version lookup is skipped there.
        var version = tool == Fd && plat == "darwin" && architecture == "x64"
            ? "10.3.0"
            : await GetLatestVersionAsync(config.Repo, cancellationToken).ConfigureAwait(false);

        var assetName = config.GetAssetName(version, plat, architecture);
        if (assetName is null)
        {
            throw new InvalidOperationException($"Unsupported platform: {plat}/{architecture}");
        }

        Directory.CreateDirectory(ToolsDir);

        var downloadUrl = $"https://github.com/{config.Repo}/releases/download/{config.TagPrefix}{version}/{assetName}";
        var archivePath = NodePath.Join(ToolsDir, assetName);
        var binaryExt = plat == "win32" ? ".exe" : string.Empty;
        var binaryPath = NodePath.Join(ToolsDir, config.BinaryName + binaryExt);

        await DownloadFileAsync(downloadUrl, archivePath, cancellationToken).ConfigureAwait(false);

        // A unique temp directory: fd and rg downloads can run concurrently during startup, so a shared
        // directory would race.
        var extractDir = NodePath.Join(
            ToolsDir,
            $"extract_tmp_{config.BinaryName}_{Environment.ProcessId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{RandomSuffix()}");

        Directory.CreateDirectory(extractDir);

        try
        {
            if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                ExtractTarGzArchive(archivePath, extractDir, assetName);
            }
            else if (assetName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ExtractZipArchive(archivePath, extractDir, assetName);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported archive format: {assetName}");
            }

            // Some archives hold the files at the root, others nest them under a versioned subdirectory.
            var binaryFileName = config.BinaryName + binaryExt;
            var extractedDir = NodePath.Join(extractDir, StripArchiveSuffix(assetName));
            var extractedBinary = new[] { NodePath.Join(extractedDir, binaryFileName), NodePath.Join(extractDir, binaryFileName) }
                .FirstOrDefault(File.Exists)
                ?? FindBinaryRecursively(extractDir, binaryFileName);

            if (extractedBinary is null)
            {
                throw new InvalidOperationException($"Binary not found in archive: expected {binaryFileName} under {extractDir}");
            }

            MoveIntoPlace(extractedBinary, binaryPath);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    binaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        finally
        {
            RemoveFileForced(archivePath);
            RemoveDirectoryForced(extractDir);
        }

        return binaryPath;
    }

    /// <summary>
    /// <c>renameSync</c> replaces the destination on POSIX but fails on Windows when it already exists, so
    /// the two platforms need different calls.
    /// </summary>
    private static void MoveIntoPlace(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: true);
        }
    }

    /// <summary><c>assetName.replace(/\.(tar\.gz|zip)$/, "")</c>.</summary>
    private static string StripArchiveSuffix(string assetName)
    {
        if (assetName.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return assetName[..^7];
        }

        return assetName.EndsWith(".zip", StringComparison.Ordinal) ? assetName[..^4] : assetName;
    }

    /// <summary><c>Math.random().toString(36).slice(2, 10)</c>: eight base-36 characters.</summary>
    private static string RandomSuffix()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[8];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(buffer);
    }

    /// <summary><c>rmSync(path, { force: true })</c>: a missing file is not an error.</summary>
    private static void RemoveFileForced(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary><c>rmSync(path, { recursive: true, force: true })</c>: a missing directory is not an error.</summary>
    private static void RemoveDirectoryForced(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
