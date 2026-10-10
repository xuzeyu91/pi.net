using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the 4e-1 slice of <c>core/*</c> against <c>prompt-corpus.json</c>, which was captured by
/// running the original TypeScript modules in Node.
/// </summary>
/// <remarks>
/// <para>
/// The modules covered here are the ones that carry no session dependency: prompt templates, slash
/// commands, auth guidance, the telemetry and experimental gates, the missing-cwd guard, provider
/// attribution and the crash log — plus <c>decodeURI</c>, which the crash log needs.
/// </para>
/// <para>
/// Absolute paths that differ between the two runtimes (the docs directory, the fixture directory, the
/// package version) are scrubbed to <c>&lt;DOCS&gt;</c> / <c>&lt;TMP&gt;</c> / <c>&lt;VERSION&gt;</c> on
/// both sides, so the vectors pin behaviour rather than this machine's layout.
/// </para>
/// </remarks>
public class PromptCorpusTests : IDisposable
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private readonly string _tmpRoot =
        Path.Combine(Path.GetTempPath(), "pi-prompt-corpus-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    public PromptCorpusTests()
    {
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpRoot))
            {
                Directory.Delete(_tmpRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover fixture directory is harmless.
        }
    }

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "prompt-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ prompt templates

    [Fact]
    public void ParseCommandArgs_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("parseCommandArgs").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = Strings(vector.GetProperty("args"));
            var actual = PromptTemplates.ParseCommandArgs(input).ToList();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                failures.Add($"{Escape(input)}: expected [{Join(expected)}] got [{Join(actual)}]");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void SubstituteArgs_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("substituteArgs").EnumerateArray())
        {
            var content = vector.GetProperty("content").GetString()!;
            var args = Strings(vector.GetProperty("args"));
            var expected = vector.GetProperty("result").GetString()!;
            var actual = PromptTemplates.SubstituteArgs(content, args);
            if (actual != expected)
            {
                failures.Add($"{Escape(content)} + [{Join(args)}]: expected {Escape(expected)} got {Escape(actual)}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ExpandPromptTemplate_MatchesTypeScript()
    {
        var section = Corpus.GetProperty("expandPromptTemplate");
        var templates = section.GetProperty("templates").EnumerateArray()
            .Select(template => new PromptTemplate
            {
                Name = template.GetProperty("name").GetString()!,
                Description = "",
                Content = template.GetProperty("content").GetString()!,
                SourceInfo = new SourceInfo { Path = "x", Source = "local", Scope = "project", Origin = "top-level" },
                FilePath = "x",
            })
            .ToList();

        var failures = new List<string>();
        foreach (var vector in section.GetProperty("cases").EnumerateArray())
        {
            var text = vector.GetProperty("text").GetString()!;
            var expected = vector.GetProperty("result").GetString()!;
            var actual = PromptTemplates.ExpandPromptTemplate(text, templates);
            if (actual != expected)
            {
                failures.Add($"{Escape(text)}: expected {Escape(expected)} got {Escape(actual)}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void LoadPromptTemplates_MatchesTypeScript()
    {
        CreateTemplateFixture();
        var failures = new List<string>();

        foreach (var vector in Corpus.GetProperty("loadPromptTemplates").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var result = PromptTemplates.Load(new LoadPromptTemplatesOptions
            {
                Cwd = Path.Combine(_tmpRoot, "tpl", "proj"),
                AgentDir = Path.Combine(_tmpRoot, "tpl", "agent"),
                PromptPaths = Strings(vector.GetProperty("promptPaths")).Select(Unscrub).ToList(),
                IncludeDefaults = vector.GetProperty("includeDefaults").GetBoolean(),
            });

            var actual = result.Templates
                .Select(template => Scrub(string.Join(
                    "|",
                    template.Name,
                    template.Description,
                    template.ArgumentHint ?? "-",
                    template.Content,
                    Scrub(template.FilePath),
                    template.SourceInfo.Source,
                    template.SourceInfo.Scope,
                    template.SourceInfo.Origin,
                    Scrub(template.SourceInfo.BaseDir ?? "-"))))
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();

            var expected = vector.GetProperty("templates").EnumerateArray()
                .Select(template => string.Join(
                    "|",
                    template.GetProperty("name").GetString(),
                    template.GetProperty("description").GetString(),
                    OptString(template, "argumentHint") ?? "-",
                    template.GetProperty("content").GetString(),
                    template.GetProperty("filePath").GetString(),
                    template.GetProperty("source").GetString(),
                    template.GetProperty("scope").GetString(),
                    template.GetProperty("origin").GetString(),
                    OptString(template, "baseDir") ?? "-"))
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();

            Compare(label, expected, actual, failures);

            // The corpus stores the templates sorted by name (directory enumeration order is not
            // reproducible across runtimes), so the *load* order needs its own assertion: TS appends the
            // agent dir before the project dir, and that order is what later duplicate resolution reads.
            var scopes = result.Templates.Select(template => template.SourceInfo.Scope).ToList();
            var lastUser = scopes.FindLastIndex(scope => scope == SourceScope.User);
            var firstProject = scopes.FindIndex(scope => scope == SourceScope.Project);
            if (lastUser >= 0 && firstProject >= 0 && lastUser > firstProject)
            {
                failures.Add($"{label} order: user-scoped templates must precede project-scoped ones, got [{Join(scopes)}]");
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ slash commands

    [Fact]
    public void BuiltinSlashCommands_MatchesTypeScript()
    {
        var expected = Corpus.GetProperty("builtinSlashCommands").EnumerateArray()
            .Select(command => string.Join(
                "|",
                command.GetProperty("name").GetString(),
                command.GetProperty("description").GetString(),
                OptString(command, "argumentHint") ?? "-"))
            .ToList();
        var actual = SlashCommands.BuiltinSlashCommands
            .Select(command => string.Join("|", command.Name, command.Description, command.ArgumentHint ?? "-"))
            .ToList();
        Assert.Equal(expected, actual);
    }

    // ------------------------------------------------------------------ auth guidance

    [Fact]
    public void AuthGuidance_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("authGuidance").EnumerateArray())
        {
            var kind = vector.GetProperty("kind").GetString()!;
            var expected = vector.GetProperty("result").GetString()!;
            var actual = Scrub(kind switch
            {
                "login-help" => AuthGuidance.GetProviderLoginHelp(),
                "no-models" => AuthGuidance.FormatNoModelsAvailableMessage(),
                "no-model-selected" => AuthGuidance.FormatNoModelSelectedMessage(),
                "no-api-key-unknown" => AuthGuidance.FormatNoApiKeyFoundMessage("unknown"),
                "no-api-key-anthropic" => AuthGuidance.FormatNoApiKeyFoundMessage("anthropic"),
                "no-api-key-empty" => AuthGuidance.FormatNoApiKeyFoundMessage(""),
                _ => throw new InvalidOperationException($"unknown kind {kind}"),
            });
            if (actual != expected)
            {
                failures.Add($"{kind}: expected {Escape(expected)} got {Escape(actual)}");
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ gates

    [Fact]
    public void TelemetryAndExperimentalGates_MatchTypeScript()
    {
        var previousTelemetry = Environment.GetEnvironmentVariable("PI_TELEMETRY");
        var previousExperimental = Environment.GetEnvironmentVariable("PI_EXPERIMENTAL");
        var failures = new List<string>();
        try
        {
            // The 2-arg form: a null environment value means "not set", so the setting decides.
            foreach (var vector in Corpus.GetProperty("telemetryEnvFlag").EnumerateArray())
            {
                var env = OptString(vector, "value");
                var actual = Core.Telemetry.IsInstallTelemetryEnabled(
                    SettingsManager.InMemory(new Settings { EnableInstallTelemetry = false }), env);
                Check(failures, "env-flag", Escape(env ?? "<null>"), vector.GetProperty("result").GetBoolean(), actual);
            }

            foreach (var vector in Corpus.GetProperty("telemetryEnabled").EnumerateArray())
            {
                var env = OptString(vector, "env");
                var setting = vector.GetProperty("setting").GetBoolean();
                var actual = Core.Telemetry.IsInstallTelemetryEnabled(
                    SettingsManager.InMemory(new Settings { EnableInstallTelemetry = setting }), env);
                Check(failures, "enabled", $"{Escape(env ?? "<null>")}/{setting}", vector.GetProperty("result").GetBoolean(), actual);
            }

            // The 1-arg form reads the process environment.
            foreach (var vector in Corpus.GetProperty("telemetryFromEnv").EnumerateArray())
            {
                Environment.SetEnvironmentVariable("PI_TELEMETRY", OptString(vector, "env"));
                var actual = Core.Telemetry.IsInstallTelemetryEnabled(
                    SettingsManager.InMemory(new Settings { EnableInstallTelemetry = vector.GetProperty("setting").GetBoolean() }));
                Check(failures, "from-env", Escape(OptString(vector, "env") ?? "<null>"), vector.GetProperty("result").GetBoolean(), actual);
            }

            Environment.SetEnvironmentVariable("PI_TELEMETRY", null);

            foreach (var vector in Corpus.GetProperty("experimentalEnabled").EnumerateArray())
            {
                Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", OptString(vector, "env"));
                var actual = Experimental.AreExperimentalFeaturesEnabled();
                Check(failures, "experimental", Escape(OptString(vector, "env") ?? "<null>"), vector.GetProperty("result").GetBoolean(), actual);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_TELEMETRY", previousTelemetry);
            Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", previousExperimental);
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ session cwd

    [Fact]
    public void SessionCwd_MatchesTypeScript()
    {
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "cwd-exists"));
        var failures = new List<string>();

        foreach (var vector in Corpus.GetProperty("sessionCwdIssue").EnumerateArray())
        {
            var sessionFile = vector.GetProperty("hasSessionFile").GetBoolean() ? OptString(vector, "sessionFile") : null;
            var cwd = Unscrub(vector.GetProperty("cwd").GetString()!);
            var expectedIssue =
                vector.TryGetProperty("issue", out var issueElement) && issueElement.ValueKind == JsonValueKind.Object
                    ? issueElement
                    : (JsonElement?)null;

            var issue = SessionCwd.GetMissingSessionCwdIssue(
                new StubSessionCwdSource(cwd, sessionFile), "<FALLBACK>");

            var actualText = issue is null ? null : Scrub(Canonical(issue));
            var expectedText = expectedIssue is null ? null : Canonical(expectedIssue.Value);
            if (actualText != expectedText)
            {
                failures.Add($"issue {Escape(cwd)}: expected {expectedText ?? "<null>"} got {actualText ?? "<null>"}");
            }
        }

        foreach (var vector in Corpus.GetProperty("sessionCwdFormat").EnumerateArray())
        {
            var issue = vector.GetProperty("issue");
            var sessionFile = OptString(issue, "sessionFile");
            var model = new SessionCwdIssue(
                issue.GetProperty("sessionCwd").GetString()!,
                issue.GetProperty("fallbackCwd").GetString()!)
            {
                SessionFile = sessionFile,
            };

            var expectedError = vector.GetProperty("error").GetString()!;
            var expectedPrompt = vector.GetProperty("prompt").GetString()!;
            if (SessionCwd.FormatMissingSessionCwdError(model) != expectedError)
            {
                failures.Add($"error {Escape(sessionFile ?? "-")}: {Escape(SessionCwd.FormatMissingSessionCwdError(model))}");
            }

            if (SessionCwd.FormatMissingSessionCwdPrompt(model) != expectedPrompt)
            {
                failures.Add($"prompt {Escape(sessionFile ?? "-")}: {Escape(SessionCwd.FormatMissingSessionCwdPrompt(model))}");
            }

            var thrown = Record.Exception(() => SessionCwd.AssertSessionCwdExists(
                new StubSessionCwdSource("/definitely/not/here", "/s/session.jsonl"), "/fallback"));
            var expectedName = OptString(vector, "exceptionName");
            var actualName = thrown is MissingSessionCwdException ? MissingSessionCwdException.ErrorName : thrown?.GetType().Name;
            if (actualName != expectedName)
            {
                failures.Add($"exception: expected {expectedName} got {actualName}");
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ provider attribution

    [Fact]
    public void ProviderAttribution_MatchesTypeScript()
    {
        Environment.SetEnvironmentVariable("PI_TELEMETRY", null);
        var failures = new List<string>();

        foreach (var vector in Corpus.GetProperty("providerAttribution").EnumerateArray())
        {
            var model = new ModelSpec
            {
                Id = "m",
                Name = "M",
                Api = "openai-completions",
                Provider = vector.GetProperty("provider").GetString()!,
                BaseUrl = vector.GetProperty("baseUrl").GetString()!,
            };

            var sources = vector.GetProperty("sources").EnumerateArray()
                .Select(source => source.ValueKind == JsonValueKind.Null
                    ? null
                    : (IReadOnlyDictionary<string, string?>)source.EnumerateObject()
                        .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal))
                .ToArray();

            var settings = SettingsManager.InMemory(new Settings
            {
                EnableInstallTelemetry = vector.GetProperty("telemetry").GetBoolean(),
            });
            var actual = ProviderAttribution.MergeHeaders(
                model, settings, OptString(vector, "sessionId"), sources);
            var expected = vector.GetProperty("result");

            var actualText = DescribeHeaders(actual);
            var expectedText = expected.ValueKind == JsonValueKind.Null
                ? "<null>"
                : "{" + string.Join(",", expected.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => $"{property.Name}={property.Value.GetString() ?? "<null>"}")) + "}";
            var label = $"{model.Provider}/{model.BaseUrl}/{OptString(vector, "sessionId") ?? "-"}";
            if (actualText != expectedText)
            {
                failures.Add($"{label}: expected {expectedText} got {actualText}");
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ crash log

    [Fact]
    public void CrashLogRead_MatchesTypeScript()
    {
        var failures = new List<string>();
        var index = 0;

        foreach (var vector in Corpus.GetProperty("crashLogRead").EnumerateArray())
        {
            var path = Path.Combine(_tmpRoot, "crash-read", index++.ToString(CultureInfo.InvariantCulture), "crashes.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var contents = OptString(vector, "contents");
            if (contents is not null)
            {
                File.WriteAllText(path, contents);
            }

            var actual = Canonical(CrashLog.Read(path));
            var expected = Canonical(vector.GetProperty("records"));
            if (actual != expected)
            {
                failures.Add($"{vector.GetProperty("label").GetString()}: expected {expected} got {actual}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void CrashLogRecord_MatchesTypeScript()
    {
        var failures = new List<string>();
        var index = 0;

        foreach (var vector in Corpus.GetProperty("crashLogRecord").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var path = Path.Combine(_tmpRoot, "crash-record", index++.ToString(CultureInfo.InvariantCulture), "crashes.json");
            var expectedRecord = vector.GetProperty("record");
            var now = DateTimeOffset.Parse(
                expectedRecord.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            var record = CrashLog.Record(
                new CrashInput
                {
                    Kind = vector.GetProperty("kind").GetString()!,
                    Error = ErrorValue(vector.GetProperty("error")),
                    SessionFile = OptString(vector, "sessionFile"),
                    Cwd = vector.GetProperty("cwd").GetString()!,
                },
                path,
                now);

            if (record is null)
            {
                failures.Add($"{label}: no record was written");
                continue;
            }

            var actual = Scrub(Canonical(record));
            var expected = Canonical(expectedRecord);
            if (actual != expected)
            {
                failures.Add($"{label}: expected {expected} got {actual}");
            }

            var actualFile = Scrub(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
            var expectedFile = vector.GetProperty("fileContents").GetString()!;
            if (actualFile != expectedFile)
            {
                failures.Add($"{label} file: expected {Escape(expectedFile)} got {Escape(actualFile)}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void CrashLogRecordCapAndClear_MatchTypeScript()
    {
        var cap = Corpus.GetProperty("crashLogRecordCap");
        var path = Path.Combine(_tmpRoot, "crash-cap", "crashes.json");
        for (var index = 0; index < 7; index++)
        {
            CrashLog.Record(
                new CrashInput { Kind = CrashKind.FatalError, Error = $"e{index}", Cwd = "/cwd" }, path);
        }

        var records = CrashLog.Read(path);
        Assert.Equal(cap.GetProperty("count").GetInt32(), records.Count);
        Assert.Equal(Strings(cap.GetProperty("messages")), records.Select(record => record.Message).ToList());
        Assert.Equal(
            TimestampScrubbed(cap.GetProperty("fileContents").GetString()!),
            TimestampScrubbed(Scrub(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal))));

        var clear = Corpus.GetProperty("crashLogClear");
        Assert.Equal(clear.GetProperty("existedBefore").GetBoolean(), File.Exists(path));
        CrashLog.Clear(path);
        CrashLog.Clear(path);
        Assert.Equal(clear.GetProperty("existsAfter").GetBoolean(), File.Exists(path));
    }

    [Fact]
    public void CrashLogTake_MatchesTypeScript()
    {
        var failures = new List<string>();
        var index = 0;

        foreach (var vector in Corpus.GetProperty("crashLogTake").EnumerateArray())
        {
            var label = vector.GetProperty("label").GetString()!;
            var path = Path.Combine(_tmpRoot, "crash-take", index++.ToString(CultureInfo.InvariantCulture), "crashes.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var seed = OptString(vector, "seedContents");
            if (seed is not null)
            {
                File.WriteAllText(path, seed);
            }

            var now = vector.GetProperty("now").GetInt64();
            var taken = CrashLog.TakeUnnotified(path, now);
            var expectedTaken = vector.GetProperty("taken");

            var actualTaken = taken is null ? null : Scrub(Canonical(taken));
            var expectedTakenText = expectedTaken.ValueKind == JsonValueKind.Null ? null : Canonical(expectedTaken);
            if (actualTaken != expectedTakenText)
            {
                failures.Add($"{label} taken: expected {expectedTakenText ?? "<null>"} got {actualTaken ?? "<null>"}");
            }

            var after = File.Exists(path) ? Scrub(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)) : null;
            var expectedAfter = OptString(vector, "afterContents");
            if (after != expectedAfter)
            {
                failures.Add($"{label} after: expected {Escape(expectedAfter ?? "<null>")} got {Escape(after ?? "<null>")}");
            }

            var second = CrashLog.TakeUnnotified(path, now);
            var expectedSecond = vector.GetProperty("second");
            if ((second is null) != (expectedSecond.ValueKind == JsonValueKind.Null))
            {
                failures.Add($"{label} second: expected {(expectedSecond.ValueKind == JsonValueKind.Null ? "<null>" : "a record")}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void FindExtensionStackMatches_MatchesTypeScript()
    {
        var section = Corpus.GetProperty("findExtensionStackMatches");
        var extensions = section.GetProperty("extensions").EnumerateArray()
            .Select(extension =>
            {
                var sourceInfo = extension.GetProperty("sourceInfo");
                return new ExtensionStackMetadata(
                    extension.GetProperty("path").GetString()!,
                    extension.GetProperty("resolvedPath").GetString()!,
                    new SourceInfo
                    {
                        Path = sourceInfo.GetProperty("path").GetString()!,
                        Source = sourceInfo.GetProperty("source").GetString()!,
                        Scope = sourceInfo.GetProperty("scope").GetString()!,
                        Origin = sourceInfo.GetProperty("origin").GetString()!,
                        BaseDir = OptString(sourceInfo, "baseDir"),
                    });
            })
            .ToList();

        var failures = new List<string>();
        foreach (var vector in section.GetProperty("cases").EnumerateArray())
        {
            var stack = OptString(vector, "stack");
            var expected = Strings(vector.GetProperty("result"));
            var actual = CrashLog.FindExtensionStackMatches(stack, extensions).ToList();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                failures.Add($"{Escape(stack ?? "<null>")}: expected [{Join(expected)}] got [{Join(actual)}]");
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ decodeURI

    [Fact]
    public void DecodeUri_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("decodeUri").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var threw = vector.GetProperty("threw").GetBoolean();
            try
            {
                var actual = JsUri.DecodeUri(input);
                if (threw)
                {
                    failures.Add($"{Escape(input)}: expected a URIError, got {Escape(actual)}");
                }
                else if (actual != vector.GetProperty("result").GetString())
                {
                    failures.Add($"{Escape(input)}: expected {Escape(vector.GetProperty("result").GetString()!)} got {Escape(actual)}");
                }
            }
            catch (UriFormatException)
            {
                if (!threw)
                {
                    failures.Add($"{Escape(input)}: expected {Escape(vector.GetProperty("result").GetString()!)} but threw");
                }
            }
        }

        Assert.Empty(failures);
    }

    // ------------------------------------------------------------------ corpus shape

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(22, Corpus.GetProperty("parseCommandArgs").GetArrayLength());
        Assert.Equal(37, Corpus.GetProperty("substituteArgs").GetArrayLength());
        Assert.Equal(15, Corpus.GetProperty("expandPromptTemplate").GetProperty("cases").GetArrayLength());
        Assert.Equal(24, Corpus.GetProperty("builtinSlashCommands").GetArrayLength());
        Assert.Equal(6, Corpus.GetProperty("authGuidance").GetArrayLength());
        Assert.Equal(17, Corpus.GetProperty("telemetryEnvFlag").GetArrayLength());
        Assert.Equal(9, Corpus.GetProperty("telemetryEnabled").GetArrayLength());
        Assert.Equal(5, Corpus.GetProperty("telemetryFromEnv").GetArrayLength());
        Assert.Equal(6, Corpus.GetProperty("experimentalEnabled").GetArrayLength());
        Assert.Equal(5, Corpus.GetProperty("sessionCwdIssue").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("sessionCwdFormat").GetArrayLength());
        Assert.Equal(22, Corpus.GetProperty("providerAttribution").GetArrayLength());
        Assert.Equal(25, Corpus.GetProperty("findExtensionStackMatches").GetProperty("cases").GetArrayLength());
        Assert.Equal(8, Corpus.GetProperty("crashLogRead").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("crashLogRecord").GetArrayLength());
        Assert.Equal(9, Corpus.GetProperty("crashLogTake").GetArrayLength());
        Assert.Equal(7, Corpus.GetProperty("loadPromptTemplates").GetArrayLength());
        Assert.Equal(30, Corpus.GetProperty("decodeUri").GetArrayLength());

        // Guards on the parts that make the corpus able to fail rather than merely pass.
        Assert.Contains(
            Corpus.GetProperty("crashLogRead").EnumerateArray(),
            vector => vector.GetProperty("label").GetString() == "malformed");
        // A BOM in front of an *empty* array reads as "no records" either way; only the non-empty one
        // discriminates a reader that strips the mark.
        Assert.Contains(
            Corpus.GetProperty("crashLogRead").EnumerateArray(),
            vector => vector.GetProperty("label").GetString() == "bom-with-records");
        Assert.Contains(
            Corpus.GetProperty("crashLogTake").EnumerateArray(),
            vector => vector.GetProperty("label").GetString() == "too-old");
        // TS reads `notified` for truthiness, so a string suppresses the notice and the number 0 does not.
        Assert.Contains(
            Corpus.GetProperty("crashLogTake").EnumerateArray(),
            vector => vector.GetProperty("label").GetString() == "notified-string-is-truthy");
        // `resolvePath(…, { trim: true })` is only observable through a padded path.
        Assert.Contains(
            Corpus.GetProperty("loadPromptTemplates").EnumerateArray(),
            vector => vector.GetProperty("label").GetString() == "padded-path");
        Assert.Contains(
            Corpus.GetProperty("decodeUri").EnumerateArray(),
            vector => vector.GetProperty("threw").GetBoolean());
        Assert.Contains(
            Corpus.GetProperty("decodeUri").EnumerateArray(),
            vector => vector.GetProperty("input").GetString() == "%C2%80");
        // A continuation byte above 0xBF, and a first line that is itself a frame.
        Assert.Contains(
            Corpus.GetProperty("decodeUri").EnumerateArray(),
            vector => vector.GetProperty("input").GetString() == "%E4%C0%80");
        Assert.Contains(
            Corpus.GetProperty("findExtensionStackMatches").GetProperty("cases").EnumerateArray(),
            vector => OptString(vector, "stack")?.StartsWith("    at ", StringComparison.Ordinal) == true);
        // Two extensions sharing the label `npm:dup` — the dedup guard.
        Assert.Equal(
            2,
            Corpus.GetProperty("findExtensionStackMatches").GetProperty("extensions").EnumerateArray()
                .Count(extension => OptString(extension.GetProperty("sourceInfo"), "source") == "npm:dup"));
        Assert.Contains(
            Corpus.GetProperty("providerAttribution").EnumerateArray(),
            vector => vector.GetProperty("result").ValueKind == JsonValueKind.Null);
        Assert.Contains(
            Corpus.GetProperty("findExtensionStackMatches").GetProperty("cases").EnumerateArray(),
            vector => vector.GetProperty("stack").ValueKind == JsonValueKind.Null);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class StubSessionCwdSource(string cwd, string? sessionFile) : ISessionCwdSource
    {
        public string GetCwd() => cwd;

        public string? GetSessionFile() => sessionFile;
    }

    private void CreateTemplateFixture()
    {
        var prompts = Path.Combine(_tmpRoot, "tpl", "agent", "prompts");
        var project = Path.Combine(_tmpRoot, "tpl", "proj", ".pi", "prompts");
        var extra = Path.Combine(_tmpRoot, "tpl", "extra");
        var explicitDir = Path.Combine(_tmpRoot, "tpl", "explicit");
        Directory.CreateDirectory(prompts);
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(extra);
        Directory.CreateDirectory(explicitDir);

        File.WriteAllText(Path.Combine(prompts, "alpha.md"), "---\ndescription: Alpha template\nargument-hint: <name>\n---\nDo $1 things.\n");
        File.WriteAllText(Path.Combine(prompts, "beta.md"), "Beta first line\nsecond line\n");
        File.WriteAllText(Path.Combine(prompts, "notes.txt"), "not markdown\n");
        // An empty `argument-hint` leaves the member absent (TS spreads `argumentHint && { … }`).
        File.WriteAllText(Path.Combine(prompts, "empty-hint.md"), "---\ndescription: Empty hint\nargument-hint: \"\"\n---\nBody\n");
        // A leading byte-order mark stops the front matter from matching, because the reader keeps it.
        File.WriteAllText(Path.Combine(prompts, "bom.md"), "\uFEFF---\ndescription: BOM template\n---\nBom body\n");
        File.WriteAllText(Path.Combine(project, "gamma.md"), "Gamma description\nbody\n");
        File.WriteAllText(Path.Combine(explicitDir, "delta.md"), "---\nargument-hint: <x>\n---\nDelta body\n");
        File.WriteAllText(Path.Combine(explicitDir, "readme.txt"), "ignore me\n");
        File.WriteAllText(Path.Combine(extra, "epsilon.md"), "# Epsilon heading\nrest\n");
        File.WriteAllText(Path.Combine(extra, "very-long-line.md"), new string('x', 80) + "\nrest\n");
    }

    /// <summary>Replace this machine's fixture directory, docs directory and version with placeholders.</summary>
    /// <remarks>
    /// The escaped variants are here because the crash-log and session-cwd vectors canonicalize to JSON
    /// first and scrub second, and JSON doubles every backslash — a bare <c>C:\Users\…</c> needle never
    /// matches <c>C:\\Users\\…</c>. On a path with no backslashes the escaped needle is the same string
    /// and the extra pass is a no-op.
    /// </remarks>
    private string Scrub(string value)
    {
        var resolved = Paths.ResolvePath(_tmpRoot);
        var docs = Config.GetDocsPath();
        return value
            .Replace(JsonEscape(_tmpRoot), "<TMP>", StringComparison.Ordinal)
            .Replace(_tmpRoot, "<TMP>", StringComparison.Ordinal)
            .Replace(_tmpRoot.Replace('\\', '/'), "<TMP>", StringComparison.Ordinal)
            .Replace(JsonEscape(resolved), "<TMP>", StringComparison.Ordinal)
            .Replace(resolved, "<TMP>", StringComparison.Ordinal)
            .Replace(JsonEscape(docs), "<DOCS>", StringComparison.Ordinal)
            .Replace(docs, "<DOCS>", StringComparison.Ordinal)
            .Replace(Config.Version, "<VERSION>", StringComparison.Ordinal);
    }

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    private string Unscrub(string value) =>
        value.Replace("<TMP>", _tmpRoot, StringComparison.Ordinal);

    /// <summary>Replace the timestamps in a serialized crash log so two runs can be compared.</summary>
    private static string TimestampScrubbed(string value) =>
        Regex.Replace(value, "\"timestamp\": \"[^\"]*\"", "\"timestamp\": \"<TS>\"");

    private static string Canonical(object? value) =>
        value is null
            ? "null"
            : Canonical(JsonSerializer.SerializeToElement(value, JsonOptions));

    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{JsonSerializer.Serialize(property.Name)}:{Canonical(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize<string?>(element.GetString(), JsonOptions),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The TS <c>unknown</c> error input: a string, a number or null in the recorded vectors.</summary>
    private static object? ErrorValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetInt32(),
        _ => null,
    };

    private static string? OptString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DescribeHeaders(IReadOnlyDictionary<string, string?>? headers) =>
        headers is null
            ? "<null>"
            : "{" + string.Join(",", headers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value ?? "<null>"}")) + "}";

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToList();

    private static void Compare(string label, IReadOnlyList<string> expected, IReadOnlyList<string> actual, List<string> failures)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            failures.Add($"{label}: expected [{Join(expected)}] got [{Join(actual)}]");
        }
    }

    private static void Check(List<string> failures, string label, string detail, bool expected, bool actual)
    {
        if (expected != actual)
        {
            failures.Add($"{label} {detail}: expected {expected} got {actual}");
        }
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values.Select(Escape));

    private static string Escape(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
}
