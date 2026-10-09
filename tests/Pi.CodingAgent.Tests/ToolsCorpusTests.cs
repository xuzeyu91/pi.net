using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays <c>utils/tools-manager.ts</c> and <c>utils/windows-self-update.ts</c> — plus the
/// <c>node:path.toNamespacedPath</c> helper the latter leans on — against <c>tools-corpus.json</c>.
/// </summary>
/// <remarks>
/// The corpus is produced by <c>tools/gen-coding-agent-tools-corpus.mjs</c>, which stubs the four globals
/// the original reaches for (the agent directory, <c>spawnSync</c>, <c>os.platform</c>/<c>os.arch</c> and
/// <c>fetch</c>) and records the resulting status messages rather than the calls themselves. The matching
/// seams here are <see cref="ToolsManager.EnvOverride"/>, <see cref="ToolsManager.ToolsDirOverride"/>,
/// <see cref="ToolsManager.SpawnOverride"/>, <see cref="ToolsManager.PlatformOverride"/>,
/// <see cref="ToolsManager.ArchOverride"/> and <see cref="ManagementHttp.HandlerOverride"/>.
/// </remarks>
public class ToolsCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    /// <summary>The randomised parts of a path, folded out so a vector is comparable across runs.</summary>
    private static readonly Regex ExtractionRun = new(@"extract_tmp_[A-Za-z0-9]+_\d+_\d+_[0-9a-z]+", RegexOptions.Compiled);

    private static readonly Regex QuarantineRun = new(@"\d{13}-\d+-[0-9a-f-]{36}", RegexOptions.Compiled);

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches:\n  " + string.Join("\n  ", failures);

    [Fact]
    public void ToNamespacedPath_MatchesTypeScript()
    {
        NodePath.CwdOverride = () => Corpus.GetProperty("hostCwd").GetString()!;
        try
        {
            var failures = new List<string>();
            foreach (var vector in Corpus.GetProperty("nodePathToNamespacedPath").EnumerateArray())
            {
                var input = vector.GetProperty("input").GetString()!;
                Check(failures, $"win32 toNamespacedPath({Quote(input)})", vector.GetProperty("win32").GetString()!, NodePath.ToNamespacedPath(input, windows: true));
                Check(failures, $"posix toNamespacedPath({Quote(input)})", vector.GetProperty("posix").GetString()!, NodePath.ToNamespacedPath(input, windows: false));
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            NodePath.CwdOverride = null;
        }
    }

    [Fact]
    public async Task GetLatestVersion_MatchesTypeScript()
    {
        var failures = new List<string>();
        try
        {
            foreach (var vector in Corpus.GetProperty("toolsLatestVersion").EnumerateArray())
            {
                var status = vector.GetProperty("status").GetInt32();
                var location = vector.GetProperty("location").ValueKind == JsonValueKind.Null
                    ? null
                    : vector.GetProperty("location").GetString();

                ManagementHttp.HandlerOverride = new StubHandler(_ => Redirect(status, location));

                string? version = null;
                string? error = null;
                try
                {
                    version = await ToolsManager.GetLatestVersionAsync("sharkdp/fd");
                }
                catch (InvalidOperationException caught)
                {
                    error = "Error: " + caught.Message;
                }

                Check(failures, $"getLatestVersion(HTTP {status}, {Quote(location ?? "no location")}) version", vector.GetProperty("version").GetString(), version);
                Check(failures, $"getLatestVersion(HTTP {status}, {Quote(location ?? "no location")}) error", vector.GetProperty("error").GetString(), error);
            }
        }
        finally
        {
            ManagementHttp.HandlerOverride = null;
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void GetToolPath_MatchesTypeScript()
    {
        var toolsDir = Directory.CreateTempSubdirectory("pi-tools-path-");
        var failures = new List<string>();
        try
        {
            ToolsManager.ToolsDirOverride = () => toolsDir.FullName;

            foreach (var vector in Corpus.GetProperty("toolsToolPath").EnumerateArray())
            {
                var tool = vector.GetProperty("tool").GetString()!;
                var plat = vector.GetProperty("plat").GetString()!;
                var spawn = vector.GetProperty("spawn").GetString()!;
                var local = vector.GetProperty("local").GetBoolean();

                ToolsManager.PlatformOverride = () => plat;
                ToolsManager.ArchOverride = () => "x64";
                ToolsManager.SpawnOverride = SpawnFor(spawn);

                var binaryName = tool == ToolsManager.Fd ? "fd" : "rg";
                var localPath = Path.Combine(toolsDir.FullName, binaryName + (plat == "win32" ? ".exe" : string.Empty));
                if (local)
                {
                    File.WriteAllText(localPath, "binary");
                }

                try
                {
                    var actual = ToolsManager.GetToolPath(tool);
                    Check(
                        failures,
                        $"getToolPath({Quote(tool)}) plat={plat} spawn={spawn} local={local}",
                        vector.GetProperty("result").GetString()!,
                        Normalize(actual, toolsDir.FullName));
                }
                finally
                {
                    File.Delete(localPath);
                }
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            ResetToolsSeams();
            toolsDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnsureTool_MatchesTypeScript()
    {
        var toolsDir = Directory.CreateTempSubdirectory("pi-tools-ensure-");
        var failures = new List<string>();
        try
        {
            ToolsManager.ToolsDirOverride = () => toolsDir.FullName;

            foreach (var vector in Corpus.GetProperty("toolsEnsureTool").EnumerateArray())
            {
                var tool = vector.GetProperty("tool").GetString()!;
                var plat = vector.GetProperty("plat").GetString()!;
                var arch = vector.GetProperty("arch").GetString()!;
                var spawn = vector.GetProperty("spawn").GetString()!;
                var offline = vector.GetProperty("offline").GetBoolean();
                var fetch = vector.GetProperty("fetch");

                ToolsManager.PlatformOverride = () => plat;
                ToolsManager.ArchOverride = () => arch;
                ToolsManager.SpawnOverride = SpawnFor(spawn);
                ToolsManager.EnvOverride = name => name == "PI_OFFLINE" ? (offline ? "1" : null) : Environment.GetEnvironmentVariable(name);
                ManagementHttp.HandlerOverride = new StubHandler(url => DownloadRoute(url, fetch));

                var statuses = new List<ToolStatus>();
                var result = await ToolsManager.EnsureToolAsync(tool, statuses.Add);

                var label = $"{vector.GetProperty("label").GetString()} ({tool}/{plat}/{arch} spawn={spawn})";
                var expectedStatuses = vector.GetProperty("statuses").EnumerateArray()
                    .Select(status => $"{status.GetProperty("type").GetString()}|{status.GetProperty("message").GetString()}")
                    .ToArray();
                var actualStatuses = statuses
                    .Select(status => $"{status.Type}|{Normalize(status.Message, toolsDir.FullName)}")
                    .ToArray();

                if (!expectedStatuses.SequenceEqual(actualStatuses))
                {
                    failures.Add(
                        $"{label} statuses:\n" +
                        $"      expected [{string.Join(", ", expectedStatuses.Select(Quote))}]\n" +
                        $"      got      [{string.Join(", ", actualStatuses.Select(Quote))}]");
                }

                Check(failures, $"{label} result", vector.GetProperty("result").GetString()!, Normalize(result, toolsDir.FullName));
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            ResetToolsSeams();
            toolsDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OfflineMode_MatchesTypeScript()
    {
        var toolsDir = Directory.CreateTempSubdirectory("pi-tools-offline-");
        var failures = new List<string>();
        try
        {
            ToolsManager.ToolsDirOverride = () => toolsDir.FullName;
            ToolsManager.PlatformOverride = () => "linux";
            ToolsManager.ArchOverride = () => "x64";
            ToolsManager.SpawnOverride = SpawnFor("all-missing");

            foreach (var vector in Corpus.GetProperty("toolsOfflineMode").EnumerateArray())
            {
                var value = vector.GetProperty("value").GetString()!;
                ToolsManager.EnvOverride = name => name == "PI_OFFLINE" ? value : Environment.GetEnvironmentVariable(name);
                ManagementHttp.HandlerOverride = new StubHandler(url => url.EndsWith("/releases/latest", StringComparison.Ordinal)
                    ? Redirect(302, "https://github.com/sharkdp/fd/releases/tag/v10.3.0")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("archive") });

                var statuses = new List<ToolStatus>();
                await ToolsManager.EnsureToolAsync("fd", statuses.Add);

                var actual = statuses.Count > 0 && statuses[0].Message.Contains("Offline mode", StringComparison.Ordinal);
                if (actual != vector.GetProperty("offline").GetBoolean())
                {
                    failures.Add($"PI_OFFLINE={Quote(value)}: expected offline={vector.GetProperty("offline").GetBoolean()}, got {actual}");
                }
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            ResetToolsSeams();
            toolsDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void WindowsSelfUpdate_MatchesTypeScript()
    {
        var failures = new List<string>();
        var root = Directory.CreateTempSubdirectory("pi-self-update-");

        try
        {
            foreach (var vector in Corpus.GetProperty("windowsSelfUpdate").EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString()!;
                var fixture = Path.Combine(root.FullName, name);
                var packageDir = Path.Combine(fixture, vector.GetProperty("packageRelative").GetString()!);
                Directory.CreateDirectory(packageDir);

                var insideA = Path.Combine(packageDir, "native", "a.node");
                var insideB = Path.Combine(packageDir, "native", "b.node");
                var outside = Path.Combine(fixture, "elsewhere", "c.node");
                foreach (var file in new[] { insideA, insideB, outside })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, "content-of-" + Path.GetFileName(file));
                }

                var markers = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["<inside-a>"] = insideA,
                    ["<inside-b>"] = insideB,
                    ["<outside>"] = outside,
                };

                var sharedObjects = vector.GetProperty("sharedObjects").EnumerateArray()
                    .Select(entry => markers.TryGetValue(entry.GetString()!, out var mapped) ? mapped : Path.Combine(packageDir, entry.GetString()!))
                    .ToArray();

                WindowsSelfUpdate.LoadedSharedObjectsOverride = () => sharedObjects;
                try
                {
                    if (vector.GetProperty("cleanupFirst").GetBoolean())
                    {
                        WindowsSelfUpdate.CleanupQuarantine(packageDir);
                    }

                    WindowsSelfUpdate.QuarantineNativeDependencies(packageDir);
                    Check(failures, $"{name} after quarantine", DescribeLayout(vector.GetProperty("layoutAfterQuarantine")), DescribeLayout(fixture));

                    if (vector.GetProperty("secondPass").GetBoolean())
                    {
                        WindowsSelfUpdate.QuarantineNativeDependencies(packageDir);
                        Check(failures, $"{name} after second pass", DescribeLayout(vector.GetProperty("layoutAfterSecondPass")), DescribeLayout(fixture));
                    }

                    WindowsSelfUpdate.CleanupQuarantine(packageDir);
                }
                finally
                {
                    WindowsSelfUpdate.LoadedSharedObjectsOverride = null;
                }

                Check(failures, $"{name} after cleanup", DescribeLayout(vector.GetProperty("layoutAfterCleanup")), DescribeLayout(fixture));
            }

            Assert.True(failures.Count == 0, Mismatches(failures));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A stale quarantine directory must not make cleanup throw.</summary>
    [Fact]
    public void WindowsSelfUpdateCleanupMissing_MatchesTypeScript()
    {
        var root = Directory.CreateTempSubdirectory("pi-self-update-missing-");
        try
        {
            var packageDir = Path.Combine(root.FullName, "node_modules", "pi");
            Directory.CreateDirectory(packageDir);

            var exception = Record.Exception(() => WindowsSelfUpdate.CleanupQuarantine(packageDir));
            var expected = Corpus.GetProperty("windowsSelfUpdateCleanupMissing").GetProperty("threw");
            Assert.True(expected.ValueKind == JsonValueKind.Null, "the corpus records that cleanup must not throw");
            Assert.Null(exception);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void ResetToolsSeams()
    {
        ToolsManager.EnvOverride = null;
        ToolsManager.ToolsDirOverride = null;
        ToolsManager.SpawnOverride = null;
        ToolsManager.PlatformOverride = null;
        ToolsManager.ArchOverride = null;
        ManagementHttp.HandlerOverride = null;
    }

    /// <summary>
    /// The corpus's four spawn modes, with the same <c>result.error.message</c> text the Node stub used.
    /// </summary>
    private static Func<string, string[], SpawnResult> SpawnFor(string mode)
    {
        var extractors = new[] { "tar", "tar.exe", "unzip", "powershell.exe" };

        return (command, _) =>
        {
            var success = mode == "all-present"
                || (mode == "fdfind-only" && command == "fdfind")
                || (mode == "system-missing-extract-ok" && extractors.Contains(command));

            return success
                ? new SpawnResult(0, null, "ok", string.Empty)
                : new SpawnResult(null, $"spawnSync {command} ENOENT", string.Empty, string.Empty);
        };
    }

    /// <summary>Answer the version lookup from the vector and the download with a small body.</summary>
    private static HttpResponseMessage DownloadRoute(string url, JsonElement fetch)
    {
        if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            var latest = fetch.TryGetProperty("latest", out var nested) ? nested : fetch;
            var status = latest.TryGetProperty("status", out var code) ? code.GetInt32() : 302;
            var location = latest.TryGetProperty("location", out var value) && value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;
            return Redirect(status, location);
        }

        var download = fetch.TryGetProperty("download", out var downloadMode) ? downloadMode : default;
        if (download.ValueKind == JsonValueKind.Object && download.TryGetProperty("status", out var downloadStatus))
        {
            return new HttpResponseMessage((HttpStatusCode)downloadStatus.GetInt32());
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fake-archive-bytes") };
    }

    private static HttpResponseMessage Redirect(int status, string? location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (location is not null)
        {
            response.Headers.TryAddWithoutValidation("Location", location);
        }

        return response;
    }

    /// <summary>Every file under <paramref name="root"/>, relative to it, sorted, with runs folded out.</summary>
    private static string DescribeLayout(JsonElement expected)
    {
        var recorded = expected.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return "[" + string.Join(", ", recorded) + "]";
    }

    private static string DescribeLayout(string root)
    {
        var files = new List<string>();
        Walk(root);
        files.Sort(StringComparer.Ordinal);
        return "[" + string.Join(", ", files) + "]";

        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    Walk(entry);
                }
                else
                {
                    var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                    files.Add(QuarantineRun.Replace(relative, "<run>"));
                }
            }
        }
    }

    /// <summary>
    /// Fold the run-specific parts of a produced path into the shape the corpus records: the temporary
    /// agent directory becomes <c>&lt;tmp&gt;/agent/bin</c> and the random run suffixes become
    /// <c>&lt;run&gt;</c>.
    /// </summary>
    private static string Normalize(string? value, string toolsDir)
    {
        if (value is null)
        {
            return "null";
        }

        var agentBin = "<tmp>" + Path.DirectorySeparatorChar + "agent" + Path.DirectorySeparatorChar + "bin";
        var text = value.Replace(toolsDir, agentBin, StringComparison.Ordinal);
        return ExtractionRun.Replace(QuarantineRun.Replace(text, "<run>"), "extract_tmp_<run>");
    }

    private static void Check(List<string> failures, string label, string? expected, string? actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label}:\n      expected {Quote(expected ?? "null")}\n      got      {Quote(actual ?? "null")}");
        }
    }

    private static string Quote(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request.RequestUri!.ToString()));
    }
}
