using System.Text.Json;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the differential corpus captured from Node's <c>path</c> module and
/// <c>url.fileURLToPath</c>, plus <c>utils/paths.ts</c>. Regenerate with
/// <c>node tools/gen-coding-agent-core-utils-corpus.mjs</c>.
/// </summary>
/// <remarks>
/// Every vector records both the <c>win32</c> and the <c>posix</c> answer, because the TS code follows
/// <c>process.platform</c> and the port follows the host OS. The corpus also records the host that
/// produced it, so a corpus generated on another platform fails loudly instead of comparing a Windows
/// vector against POSIX behaviour.
/// </remarks>
public class PathCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "core-utils-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static bool Windows => NodePath.IsWindows;

    private static string Side(JsonElement vector) => vector.GetProperty(Windows ? "win32" : "posix").GetString()!;

    private static string Label(JsonElement vector, string name)
    {
        var input = vector.GetProperty("input").GetString()!;
        return $"{name}(\"{input}\")";
    }

    [Fact]
    public void HostPlatform_MatchesCorpus()
    {
        Assert.Equal(Corpus.GetProperty("hostPlatform").GetString(), ProcessInfo.Platform);
    }

    [Fact]
    public void NodePathNormalize_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathNormalize").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = Side(vector);
            var actual = NodePath.Normalize(input, Windows);
            if (actual != expected)
            {
                failures.Add($"{Label(vector, "normalize")}: expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathJoin_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathJoin").EnumerateArray())
        {
            var parts = vector.GetProperty("parts").EnumerateArray().Select(part => part.GetString()!).ToArray();
            var expected = Side(vector);
            var actual = NodePath.Join(Windows, parts);
            if (actual != expected)
            {
                failures.Add($"join({string.Join(", ", parts.Select(part => $"\"{part}\""))}): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathDirname_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathDirname").EnumerateArray())
        {
            var expected = Side(vector);
            var actual = NodePath.Dirname(vector.GetProperty("input").GetString()!, Windows);
            if (actual != expected)
            {
                failures.Add($"{Label(vector, "dirname")}: expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathBasename_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathBasename").EnumerateArray())
        {
            var expected = Side(vector);
            var actual = NodePath.Basename(vector.GetProperty("input").GetString()!, null, Windows);
            if (actual != expected)
            {
                failures.Add($"{Label(vector, "basename")}: expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathBasenameWithExt_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathBasenameWithExt").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var ext = vector.GetProperty("ext").GetString()!;
            var expected = Side(vector);
            var actual = NodePath.Basename(input, ext, Windows);
            if (actual != expected)
            {
                failures.Add($"basename(\"{input}\", \"{ext}\"): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathIsAbsolute_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathIsAbsolute").EnumerateArray())
        {
            var expected = vector.GetProperty(Windows ? "win32" : "posix").GetBoolean();
            var actual = NodePath.IsAbsolute(vector.GetProperty("input").GetString()!, Windows);
            if (actual != expected)
            {
                failures.Add($"{Label(vector, "isAbsolute")}: expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// <c>path.resolve</c> consults the working directory whenever its arguments do not name an
    /// absolute path, so these vectors only hold for the directory the corpus was generated in. The
    /// corpus records that directory's drive and <see cref="HostDrive_MatchesCorpus"/> checks it, so a
    /// corpus generated elsewhere fails with an explanation instead of a wall of path mismatches.
    /// </summary>
    [Fact]
    public void NodePathResolve_MatchesNode()
    {
        using var scope = CwdScope();
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathResolve").EnumerateArray())
        {
            var parts = vector.GetProperty("parts").EnumerateArray().Select(part => part.GetString()!).ToArray();
            var expected = Side(vector);
            var actual = Guard(() => NodePath.Resolve(Windows, parts));
            if (actual != expected)
            {
                var label = $"resolve({string.Join(", ", parts.Select(part => $"\"{part}\""))})";
                failures.Add($"{label}: expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void NodePathRelative_MatchesNode()
    {
        using var scope = CwdScope();
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("nodePathRelative").EnumerateArray())
        {
            var from = vector.GetProperty("from").GetString()!;
            var to = vector.GetProperty("to").GetString()!;
            var expected = Side(vector);
            var actual = Guard(() => NodePath.Relative(Windows, from, to));
            if (actual != expected)
            {
                failures.Add($"relative(\"{from}\", \"{to}\"): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// <c>fileURLToPath</c> throws for the same inputs Node throws for, so the corpus records the error
    /// code (<c>&lt;ERR_INVALID_FILE_URL_PATH&gt;</c>, <c>&lt;ERR_INVALID_FILE_URL_HOST&gt;</c>,
    /// <c>&lt;ERR_INVALID_URL&gt;</c>) or the JS error name (<c>&lt;URIError&gt;</c>) instead of a path.
    /// </summary>
    [Fact]
    public void FileUrlToPath_MatchesNode()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("fileUrlToPath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            foreach (var windows in new[] { true, false })
            {
                var expected = vector.GetProperty(windows ? "win32" : "posix").GetString()!;
                string actual;
                try
                {
                    actual = FileUrl.ToPath(input, windows);
                }
                catch (NodeIoException error)
                {
                    actual = $"<{error.Code}>";
                }
                catch (UriFormatException)
                {
                    actual = "<URIError>";
                }

                if (actual != expected)
                {
                    failures.Add($"ToPath(\"{input}\", windows: {windows}): expected \"{expected}\", got \"{actual}\"");
                }
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// Pins the deliberate <c>fileURLToPath</c> divergences recorded in the corpus.
    /// </summary>
    /// <remarks>
    /// These vectors would fail <see cref="FileUrlToPath_MatchesNode"/>, so they are kept apart and
    /// asserted from both sides: the port must produce the documented answer, <em>and</em> that answer
    /// must still differ from Node's. If a future .NET or ICU change makes the two agree, the second
    /// assertion fails and the entry is removed — the gap cannot be silently inherited or silently
    /// closed. See the remarks on <c>FileUrl</c> for why neither gap is ported.
    /// </remarks>
    [Fact]
    public void FileUrlToPath_DivergencesAreAsDocumented()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("fileUrlToPathDivergence").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var reason = vector.GetProperty("reason").GetString()!;
            foreach (var windows in new[] { true, false })
            {
                var side = windows ? "win32" : "posix";
                var expected = vector.GetProperty("port").GetProperty(side).GetString()!;
                var node = vector.GetProperty("node").GetProperty(side).GetString()!;

                string actual;
                try
                {
                    actual = FileUrl.ToPath(input, windows);
                }
                catch (NodeIoException error)
                {
                    actual = $"<{error.Code}>";
                }
                catch (UriFormatException)
                {
                    actual = "<URIError>";
                }

                if (actual != expected)
                {
                    failures.Add(
                        $"ToPath(\"{input}\", windows: {windows}): documented \"{expected}\", got \"{actual}\" ({reason})");
                }

                // Only the sides that actually differ are worth pinning: where the two agree the entry
                // is simply not a divergence, and the corpus says so by recording the same value.
                if (expected != node && actual == node)
                {
                    failures.Add(
                        $"ToPath(\"{input}\", windows: {windows}): the port now agrees with Node (\"{node}\"); " +
                        $"drop this entry from the corpus ({reason})");
                }
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void PathsNormalizeWindowsShellPath_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("pathsNormalizeWindowsShellPath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = Paths.NormalizeWindowsShellPath(input);
            if (actual != expected)
            {
                failures.Add($"normalizeWindowsShellPath(\"{input}\"): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void PathsIsLocalPath_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("pathsIsLocalPath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetBoolean();
            var actual = Paths.IsLocalPath(input);
            if (actual != expected)
            {
                failures.Add($"isLocalPath(\"{input}\"): expected {expected}, got {actual}");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void PathsNormalizePath_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("pathsNormalizePath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var options = ReadOptions(vector.GetProperty("options"));
            var actual = Guard(() => Paths.NormalizePath(input, options));
            if (actual != expected)
            {
                failures.Add($"normalizePath(\"{input}\", {Describe(options)}): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void PathsResolvePath_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("pathsResolvePath").EnumerateArray())
        {
            var input = vector.GetProperty("input").GetString()!;
            var baseDir = vector.GetProperty("baseDir").GetString()!;
            var expected = vector.GetProperty("expected").GetString()!;
            var actual = Guard(() => Forward(Paths.ResolvePath(input, baseDir)));
            if (actual != expected)
            {
                failures.Add($"resolvePath(\"{input}\", \"{baseDir}\"): expected \"{expected}\", got \"{actual}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    [Fact]
    public void PathsCwdRelative_MatchesTypeScript()
    {
        using var scope = CwdScope();
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("pathsCwdRelative").EnumerateArray())
        {
            var filePath = vector.GetProperty("filePath").GetString()!;
            var cwd = vector.GetProperty("cwd").GetString()!;
            var expectedRelative = vector.GetProperty("relative").GetString();
            var expectedFormatted = vector.GetProperty("formatted").GetString()!;

            var actualRelative = Guard(() => Paths.GetCwdRelativePath(filePath, cwd));
            var actualFormatted = Guard(() => Forward(Paths.FormatPathRelativeToCwdOrAbsolute(filePath, cwd)));

            if (actualRelative != expectedRelative)
            {
                failures.Add($"getCwdRelativePath(\"{filePath}\", \"{cwd}\"): expected \"{expectedRelative}\", got \"{actualRelative}\"");
            }

            if (actualFormatted != expectedFormatted)
            {
                failures.Add($"formatPathRelativeToCwdOrAbsolute(\"{filePath}\", \"{cwd}\"): expected \"{expectedFormatted}\", got \"{actualFormatted}\"");
            }
        }

        Assert.True(failures.Count == 0, Mismatches(failures));
    }

    /// <summary>
    /// Join the mismatches into one message. A collection assertion would truncate each item, and the
    /// interesting part of a path mismatch is usually at the end of the line.
    /// </summary>
    private static string Mismatches(List<string> failures) => "mismatches://n  " + string.Join("\n  ", failures);

    /// <summary>
    /// Mirror the corpus's error capture: the TS side records a thrown error as <c>&lt;CODE&gt;</c> or
    /// <c>&lt;Name&gt;</c>, so the replay must produce the same string rather than propagate.
    /// </summary>
    private static string? Guard(Func<string?> call)
    {
        try
        {
            return call();
        }
        catch (NodeIoException error)
        {
            return $"<{error.Code}>";
        }
        catch (UriFormatException)
        {
            return "<URIError>";
        }
    }

    private static string Forward(string value) => value.Replace('\\', '/');

    /// <summary>
    /// Replay the corpus under the working directory it was generated in.
    /// </summary>
    /// <remarks>
    /// <c>path.resolve</c> and <c>path.relative</c> read the working directory for any relative or
    /// rooted-but-driveless input, and the derived values can point <em>outside</em> that directory
    /// (<c>resolve("..")</c>, or a relative difference that encodes the directory's depth), so the
    /// directory has to be reproduced rather than substituted. Both functions are pure string work, so
    /// the injected path need not exist.
    /// </remarks>
    private static IDisposable CwdScope()
    {
        NodePath.CwdOverride = () => Corpus.GetProperty("hostCwd").GetString()!;
        return new CwdRestore();
    }

    private sealed class CwdRestore : IDisposable
    {
        public void Dispose() => NodePath.CwdOverride = null;
    }

    private static PathInputOptions ReadOptions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new PathInputOptions();
        }

        return new PathInputOptions(
            Trim: element.TryGetProperty("trim", out var trim) && trim.GetBoolean(),
            ExpandTilde: element.TryGetProperty("expandTilde", out var expandTilde) ? expandTilde.GetBoolean() : null,
            HomeDir: element.TryGetProperty("homeDir", out var homeDir) ? homeDir.GetString() : null,
            StripAtPrefix: element.TryGetProperty("stripAtPrefix", out var stripAt) && stripAt.GetBoolean(),
            NormalizeUnicodeSpaces: element.TryGetProperty("normalizeUnicodeSpaces", out var spaces) && spaces.GetBoolean());
    }

    private static string Describe(PathInputOptions options) => JsonSerializer.Serialize(options);
}
