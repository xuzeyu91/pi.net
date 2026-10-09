using System.Net;
using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the differential corpus sections that are not path handling — semver, the version check,
/// the clipboard, config's self-update helpers and the shell/escape helpers. Regenerate with
/// <c>node tools/gen-coding-agent-core-utils-corpus.mjs</c>.
/// </summary>
/// <remarks>
/// The corpus was captured from the TypeScript originals with their module-level dependencies stubbed
/// (fetch, <c>spawnProcessSync</c>, the clipboard command runner, <c>os.platform</c>, the native
/// clipboard). The port exposes one injection point per stubbed dependency, so each vector is replayed
/// against the same seam rather than against a real server or a real clipboard.
/// </remarks>
public class CoreUtilsCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "core-utils-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static string Mismatches(List<string> failures) => "mismatches://n  " + string.Join("\n  ", failures);

    private static string Quote(string value) => value
        .Replace("\u001b", "\\e", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    // ---------------------------------------------------------------------------------------------
    // semver
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SemverValid_MatchesSemver()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("semverValid").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("expected").GetString();
            var actual = Semver.Valid(input);
            if (actual != expected)
            {
                failures.Add($"valid(\"{Quote(input)}\"): expected {Describe(expected)}, got {Describe(actual)}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void SemverCompare_MatchesSemver()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("semverCompare").EnumerateArray())
        {
            var left = vector.GetProperty("left").GetString()!;
            var right = vector.GetProperty("right").GetString()!;
            var expected = ReadNullableInt(vector.GetProperty("expected"));
            var actual = Semver.Compare(left.Trim(), right.Trim());
            if (actual != expected)
            {
                failures.Add($"compare(\"{Quote(left)}\", \"{Quote(right)}\"): expected {Describe(expected)}, got {Describe(actual)}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    // ---------------------------------------------------------------------------------------------
    // utils/version-check.ts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void VersionCheckCompare_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("versionCheckCompare").EnumerateArray())
        {
            var left = vector.GetProperty("left").GetString()!;
            var right = vector.GetProperty("right").GetString()!;
            var expected = ReadNullableInt(vector.GetProperty("expected"));
            var actual = VersionCheck.ComparePackageVersions(left, right);
            if (actual != expected)
            {
                failures.Add($"comparePackageVersions(\"{Quote(left)}\", \"{Quote(right)}\"): expected {Describe(expected)}, got {Describe(actual)}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void VersionCheckIsNewer_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("versionCheckIsNewer").EnumerateArray())
        {
            var candidate = vector.GetProperty("candidate").GetString()!;
            var current = vector.GetProperty("current").GetString()!;
            var expected = vector.GetProperty("expected").GetBoolean();
            var actual = VersionCheck.IsNewerPackageVersion(candidate, current);
            if (actual != expected)
            {
                failures.Add($"isNewerPackageVersion(\"{Quote(candidate)}\", \"{Quote(current)}\"): expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// The corpus records the JS error tree as <c>{ message, causes, aggregate }</c>; the port reads
    /// <see cref="Exception.InnerException"/> for a single cause and
    /// <see cref="AggregateException.InnerExceptions"/> for an <c>AggregateError</c>. A cause carries an
    /// errno only when it implements <see cref="INodeError"/>, which is the port's stand-in for the
    /// <c>code</c> property.
    /// </summary>
    [Fact]
    public void VersionCheckFormatError_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("versionCheckFormatError").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var message = vector.GetProperty("message").GetString()!;
            var aggregate = vector.GetProperty("aggregate").GetBoolean();
            var causes = vector.GetProperty("causes").EnumerateArray().Select(ReadCause).ToArray();
            var expected = vector.GetProperty("expected").GetString()!;

            Exception error = aggregate
                ? new AggregateException(message, causes)
                : new JsError(message, causes.Length > 0 ? causes[0] : null);

            var actual = VersionCheck.FormatVersionCheckError(error);
            if (actual != expected)
            {
                failures.Add($"{name}: expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static Exception ReadCause(JsonElement element)
    {
        var message = element.GetProperty("message").GetString()!;
        return element.TryGetProperty("code", out var code)
            ? new NodeIoException(message, code.GetString())
            : new Exception(message);
    }

    /// <summary>
    /// A stand-in for JavaScript's <c>Error</c>. The CLR type name is deliberately irrelevant: when the
    /// message is empty <c>formatVersionCheckError</c> has to report the JavaScript name <c>"Error"</c>,
    /// not the .NET one, so the port maps the exception type rather than reading
    /// <see cref="Type.Name"/>.
    /// </summary>
    private sealed class JsError : Exception
    {
        public JsError(string message, Exception? cause)
            : base(message, cause)
        {
        }
    }

    [Fact]
    public async Task VersionCheckRelease_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("versionCheckRelease").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var status = vector.GetProperty("status").GetInt32();
            var body = vector.GetProperty("body").GetString()!;
            var retry = vector.GetProperty("retry").GetBoolean();
            var env = ReadEnv(vector);
            var handler = new StubHandler(status, body);

            try
            {
                ManagementHttp.HandlerOverride = handler;
                VersionCheck.EnvOverride = Lookup(env);
                var release = await VersionCheck.GetLatestPiReleaseAsync(
                    "1.0.0",
                    new VersionCheckOptions { Retry = retry });

                var actual = DescribeRelease(release);
                var expected = Describe(vector.GetProperty("result"));
                if (actual != expected)
                {
                    failures.Add($"{name}: expected {expected}, got {actual}");
                }
            }
            finally
            {
                VersionCheck.EnvOverride = null;
                ManagementHttp.HandlerOverride = null;
            }

            var expectedCalls = vector.GetProperty("fetchCalls").GetInt32();
            if (handler.Calls != expectedCalls)
            {
                failures.Add($"{name}: expected {expectedCalls} fetch calls, got {handler.Calls}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public async Task VersionCheckCheck_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("versionCheckCheck").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var status = vector.GetProperty("status").GetInt32();
            var body = vector.GetProperty("body").GetString()!;
            var current = vector.GetProperty("currentVersion").GetString()!;
            var env = ReadEnv(vector);
            var handler = new StubHandler(status, body);

            try
            {
                ManagementHttp.HandlerOverride = handler;
                VersionCheck.EnvOverride = Lookup(env);
                var release = await VersionCheck.CheckForNewPiVersionAsync(current);

                var actual = DescribeRelease(release);
                var expected = Describe(vector.GetProperty("result"));
                if (actual != expected)
                {
                    failures.Add($"{name}: expected {expected}, got {actual}");
                }
            }
            finally
            {
                VersionCheck.EnvOverride = null;
                ManagementHttp.HandlerOverride = null;
            }

            var expectedCalls = vector.GetProperty("fetchCalls").GetInt32();
            if (handler.Calls != expectedCalls)
            {
                failures.Add($"{name}: expected {expectedCalls} fetch calls, got {handler.Calls}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static Dictionary<string, string> ReadEnv(JsonElement vector) =>
        vector.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
            ? env.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal)
            : [];

    private static Func<string, string?> Lookup(Dictionary<string, string> env) =>
        name => env.TryGetValue(name, out var value) ? value : null;

    private static string DescribeRelease(LatestPiRelease? release) =>
        release is null ? "null" : $"{release.Version}|{release.PackageName ?? "-"}|{release.Note ?? "-"}";

    private static string Describe(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null)
        {
            return "null";
        }

        if (result.ValueKind == JsonValueKind.String)
        {
            return result.GetString()!;
        }

        var version = result.GetProperty("version").GetString()!;
        var packageName = result.GetProperty("packageName").ValueKind == JsonValueKind.Null
            ? "-"
            : result.GetProperty("packageName").GetString()!;
        var note = result.GetProperty("note").ValueKind == JsonValueKind.Null
            ? "-"
            : result.GetProperty("note").GetString()!;
        return $"{version}|{packageName}|{note}";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly int _status;
        private readonly string _body;

        public StubHandler(int status, string body)
        {
            _status = status;
            _body = body;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // utils/clipboard.ts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClipboardRead_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("clipboardRead").EnumerateArray())
        {
            var name = DescribeVector(vector);
            var platform = vector.GetProperty("platform").GetString()!;
            var env = ReadEnv(vector);
            var results = ReadResults(vector);
            var native = ReadNative(vector);
            var calls = new List<RecordedCall>();

            var actual = await WithClipboard(
                platform,
                env,
                results,
                calls,
                native,
                () => Clipboard.ReadClipboardTextAsync());

            var expected = vector.GetProperty("text").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("text").GetString();

            if (actual != expected)
            {
                failures.Add($"{name}: expected {Describe(expected)}, got {Describe(actual)}");
            }

            CompareCalls(failures, name, vector.GetProperty("calls"), calls);
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public async Task ClipboardCopy_MatchesTypeScript()
    {
        var text = Corpus.GetProperty("clipboardCopyText").GetString()!;
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("clipboardCopy").EnumerateArray())
        {
            var name = DescribeVector(vector);
            var platform = vector.GetProperty("platform").GetString()!;
            var env = ReadEnv(vector);
            var results = ReadResults(vector);
            var native = ReadNative(vector);
            var calls = new List<RecordedCall>();
            var osc52 = new StringWriter();

            string outcome = "ok";
            try
            {
                await WithClipboard(
                    platform,
                    env,
                    results,
                    calls,
                    native,
                    async () =>
                    {
                        await Clipboard.CopyToClipboardAsync(text);
                        return null;
                    },
                    osc52);
            }
            catch (InvalidOperationException error)
            {
                // JavaScript has a single Error type, so the corpus records the name "Error"; the port
                // throws the exception that fits, and only the message is comparable.
                outcome = $"<Error: {error.Message}>";
            }

            var expected = vector.GetProperty("outcome").GetString()!;
            if (outcome != expected)
            {
                failures.Add($"{name}: expected outcome {Quote(expected)}, got {Quote(outcome)}");
            }

            CompareCalls(failures, name, vector.GetProperty("calls"), calls);

            var expectedOsc52 = vector.GetProperty("osc52").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var actualOsc52 = osc52.ToString().Length == 0 ? Array.Empty<int>() : [osc52.ToString().Length];
            if (!expectedOsc52.SequenceEqual(actualOsc52))
            {
                failures.Add($"{name}: expected OSC 52 payload lengths [{string.Join(", ", expectedOsc52)}], got [{string.Join(", ", actualOsc52)}]");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static async Task<string?> WithClipboard(
        string platform,
        Dictionary<string, string> env,
        Dictionary<string, byte[]> results,
        List<RecordedCall> calls,
        NativeStub native,
        Func<Task<string?>> body,
        TextWriter? osc52 = null)
    {
        try
        {
            Clipboard.PlatformOverride = () => platform;
            Clipboard.EnvOverride = Lookup(env);
            Clipboard.Osc52WriterOverride = osc52;
            Clipboard.CommandRunnerOverride = (command, args, options) =>
            {
                calls.Add(new RecordedCall(command, [.. args], options.Input, options.TimeoutMs));
                var key = string.Join(' ', new[] { command }.Concat(args));
                return Task.FromResult(results.TryGetValue(key, out var value) ? value : null);
            };
            Clipboard.NativeClipboardOverride = native.Present
                ? () => new StubClipboard(calls, native.Text)
                : null;

            return await body();
        }
        finally
        {
            Clipboard.PlatformOverride = null;
            Clipboard.EnvOverride = null;
            Clipboard.Osc52WriterOverride = null;
            Clipboard.CommandRunnerOverride = null;
            Clipboard.NativeClipboardOverride = null;
        }
    }

    private static Dictionary<string, byte[]> ReadResults(JsonElement vector)
    {
        var results = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (vector.TryGetProperty("results", out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                results[property.Name] = Convert.FromBase64String(property.Value.GetString()!);
            }
        }

        return results;
    }

    /// <summary>
    /// The TS stub distinguishes three states, which the corpus records as three different JSON values:
    /// <c>false</c> for "no native clipboard at all", <c>null</c> for "a native clipboard whose
    /// <c>getText</c> yields nothing", and a string for a clipboard with text. The copy vectors use
    /// <c>true</c>, because their stub only has to exist — its text is fixed.
    /// </summary>
    private static NativeStub ReadNative(JsonElement vector)
    {
        if (!vector.TryGetProperty("native", out var native))
        {
            return new NativeStub(false, null);
        }

        return native.ValueKind switch
        {
            JsonValueKind.False => new NativeStub(false, null),
            JsonValueKind.Null => new NativeStub(true, null),
            JsonValueKind.True => new NativeStub(true, "native-text"),
            _ => new NativeStub(true, native.GetString() ?? string.Empty),
        };
    }

    /// <summary>A native clipboard the corpus can ask for: whether one exists, and the text it reports.</summary>
    private readonly record struct NativeStub(bool Present, string? Text);

    private sealed record RecordedCall(string Command, IReadOnlyList<string> Args, string? Input, int? TimeoutMs);

    private sealed class StubClipboard : INativeClipboard
    {
        private readonly List<RecordedCall> _calls;
        private readonly string? _text;

        public StubClipboard(List<RecordedCall> calls, string? text)
        {
            _calls = calls;
            _text = text;
        }

        public Task<NativeClipboardText?> GetTextAsync() =>
            Task.FromResult<NativeClipboardText?>(_text is null ? null : new NativeClipboardText(_text));

        public Task<byte[]?> GetImageAsync() => Task.FromResult<byte[]?>(null);

        public Task<string[]?> GetFilePathsAsync() => Task.FromResult<string[]?>(null);

        public Task SetTextAsync(string text)
        {
            _calls.Add(new RecordedCall("<native>", [], text, null));
            return Task.CompletedTask;
        }
    }

    private static void CompareCalls(List<string> failures, string name, JsonElement expected, List<RecordedCall> actual)
    {
        var expectedCalls = expected.EnumerateArray().ToArray();
        if (expectedCalls.Length != actual.Count)
        {
            failures.Add(
                $"{name}: expected {expectedCalls.Length} clipboard calls " +
                $"[{string.Join(", ", expectedCalls.Select(call => call.GetProperty("command").GetString()))}], " +
                $"got {actual.Count} [{string.Join(", ", actual.Select(call => call.Command))}]");
            return;
        }

        for (var index = 0; index < expectedCalls.Length; index++)
        {
            var expectedCall = expectedCalls[index];
            var actualCall = actual[index];
            var command = expectedCall.GetProperty("command").GetString()!;
            var args = expectedCall.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var input = expectedCall.GetProperty("input").ValueKind == JsonValueKind.Null
                ? null
                : expectedCall.GetProperty("input").GetString();
            var timeout = ReadNullableInt(expectedCall.GetProperty("timeoutMs"));

            if (command != actualCall.Command ||
                !args.SequenceEqual(actualCall.Args) ||
                input != actualCall.Input ||
                timeout != actualCall.TimeoutMs)
            {
                failures.Add(
                    $"{name}: call {index} expected {DescribeCall(command, args, input, timeout)}, " +
                    $"got {DescribeCall(actualCall.Command, [.. actualCall.Args], actualCall.Input, actualCall.TimeoutMs)}");
            }
        }
    }

    private static string DescribeCall(string command, string[] args, string? input, int? timeout) =>
        $"{{ {command} [{string.Join(" ", args.Select(Quote))}] input={Describe(input)} timeout={Describe(timeout)} }}";

    /// <summary>
    /// Prefer the vector's own <c>name</c>. Vectors without one still need a label that does not blow up
    /// the failure message, so fall back to the raw JSON rather than to an unrelated overload.
    /// </summary>
    private static string DescribeVector(JsonElement vector)
    {
        if (vector.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString()!;
        }

        var raw = vector.GetRawText().Replace('\n', ' ');
        return raw.Length <= 140 ? raw : raw[..140] + "…";
    }

    // ---------------------------------------------------------------------------------------------
    // src/config.ts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigClassify_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("configClassify").EnumerateArray())
        {
            var probe = vector.GetProperty("probe").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;

            // The TS composes the probe as `dirName + "\0" + execPath` and lowercases it, replacing
            // backslashes. The port takes the composed probe directly, so the same normalization is
            // applied here to keep the corpus vector the single source of truth.
            var resolvedPath = probe.ToLowerInvariant().Replace('\\', '/');
            var actual = MethodName(Config.ClassifyInstallMethod(resolvedPath, isBunRuntime: false));
            if (actual != expected)
            {
                failures.Add($"classify(\"{Quote(probe)}\"): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
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

    [Fact]
    public void ConfigCommandStep_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("configCommandStep").EnumerateArray())
        {
            var command = vector.GetProperty("command").GetString()!;
            var args = vector.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var expected = vector.GetProperty("expected");
            var actual = Config.MakeSelfUpdateCommandStep(command, args);

            var expectedDisplay = expected.GetProperty("display").GetString()!;
            if (actual.Command != expected.GetProperty("command").GetString() ||
                !actual.Args.SequenceEqual(expected.GetProperty("args").EnumerateArray().Select(item => item.GetString()!)) ||
                actual.Display != expectedDisplay)
            {
                failures.Add(
                    $"makeSelfUpdateCommandStep(\"{Quote(command)}\", [{string.Join(", ", args.Select(Quote))}]): " +
                    $"expected \"{Quote(expectedDisplay)}\", got \"{Quote(actual.Display)}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ConfigNormalizeTarget_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("configNormalizeTarget").EnumerateArray())
        {
            var packageName = vector.GetProperty("packageName").GetString()!;
            var installSpec = vector.TryGetProperty("installSpec", out var spec) ? spec.GetString() : null;
            var expected = vector.GetProperty("expected");

            var actual = Config.NormalizeSelfUpdatePackageTarget(new SelfUpdatePackageTarget(packageName, installSpec));
            if (actual.PackageName != expected.GetProperty("packageName").GetString() ||
                actual.InstallSpec != expected.GetProperty("installSpec").GetString())
            {
                failures.Add(
                    $"normalizeSelfUpdatePackageTarget(\"{Quote(packageName)}\", {Describe(installSpec)}): " +
                    $"expected {Describe(expected.GetProperty("packageName").GetString())}/{Describe(expected.GetProperty("installSpec").GetString())}, " +
                    $"got {Describe(actual.PackageName)}/{Describe(actual.InstallSpec)}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ConfigShareUrl_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("configShareUrl").EnumerateArray())
        {
            var gistId = vector.GetProperty("gistId").GetString()!;
            var env = ReadEnv(vector);
            var expected = vector.GetProperty("expected").GetString()!;

            var actual = Config.GetShareViewerUrl(gistId, new ConfigSeams { Env = Lookup(env) });
            if (actual != expected)
            {
                failures.Add($"getShareViewerUrl(\"{gistId}\", env={Describe(env.Count)}): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    // ---------------------------------------------------------------------------------------------
    // utils/shell.ts
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ShellSanitizeBinaryOutput_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("shellSanitizeBinaryOutput").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = Shell.SanitizeBinaryOutput(input);
            if (actual != expected)
            {
                failures.Add($"sanitizeBinaryOutput(\"{Quote(input)}\"): expected \"{Quote(expected)}\", got \"{Quote(actual)}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void ShellIsLegacyWslBashPath_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("shellIsLegacyWslBashPath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetBoolean();
            var actual = Shell.IsLegacyWslBashPath(input);
            if (actual != expected)
            {
                failures.Add($"isLegacyWslBashPath(\"{Quote(input)}\"): expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    // ---------------------------------------------------------------------------------------------
    // cross-spawn's cmd.exe escaping
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void EscapeCmdCommand_MatchesCrossSpawn()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("escapeCmdCommand").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = ChildProcess.EscapeCmdCommand(input);
            if (actual != expected)
            {
                failures.Add($"escapeCommand(\"{Quote(input)}\"): expected \"{Quote(expected)}\", got \"{Quote(actual)}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void EscapeCmdArgument_MatchesCrossSpawn()
    {
        var failures = new List<string>();
        CheckEscapeArguments("escapeCmdArgument", failures);
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// The hand-picked vectors above only cover short backslash runs. This sweep covers every run length
    /// up to eight plus 400 pseudo-random strings, because the rule the original regexes actually
    /// implement (only the final backslash of a run is doubled) is not the rule the regexes read like.
    /// </summary>
    [Fact]
    public void EscapeCmdArgumentRandom_MatchesCrossSpawn()
    {
        var failures = new List<string>();
        CheckEscapeArguments("escapeCmdArgumentRandom", failures);
        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    private static void CheckEscapeArguments(string section, List<string> failures)
    {
        foreach (var vector in Corpus.GetProperty(section).EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            foreach (var (field, doubleEscape) in new[] { ("single", false), ("double", true) })
            {
                var expected = vector.GetProperty(field).GetString()!;
                var actual = ChildProcess.EscapeCmdArgument(input, doubleEscape);
                if (actual != expected)
                {
                    failures.Add(
                        $"escapeArgument(\"{Quote(input)}\", doubleEscapeMetaChars: {doubleEscape}): " +
                        $"expected \"{Quote(expected)}\", got \"{Quote(actual)}\"");
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------

    private static int? ReadNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    private static string Describe(string? value) => value is null ? "null" : $"\"{Quote(value)}\"";

    private static string Describe(int? value) => value?.ToString() ?? "null";
}
