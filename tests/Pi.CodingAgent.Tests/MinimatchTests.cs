using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Unit tests for the parts of the minimatch port that a corpus of real-package output cannot express:
/// the denial-of-service caps (which need non-default limits to reach), pattern validation, the small
/// module-level exports, and the fact that a <see cref="Minimatch"/> mutates its own pattern.
/// </summary>
/// <remarks>
/// Behavioural equivalence on the ordinary surface is pinned by <see cref="MinimatchCorpusTests"/>, which
/// replays 5,673 vectors captured from <c>minimatch@10.2.6</c>. The expectations here were taken from the
/// same package directly; the vectors are quoted in each test.
/// </remarks>
public class MinimatchTests
{
    // ---------------------------------------------------------------------
    // module-level exports
    // ---------------------------------------------------------------------

    [Fact]
    public void GlobStarIsASingletonSentinel()
    {
        // JS: `GLOBSTAR = Symbol('globstar **')`, compared by identity.
        Assert.Same(Glob.GlobStar, MatchGlobstarPart.Instance);
        Assert.NotEqual<MatchPart>(Glob.GlobStar, new MatchLiteralPart("**"));
    }

    [Fact]
    public void SeparatorFollowsTheHostPlatform()
    {
        // JS: `sep = defaultPlatform === 'win32' ? path.win32.sep : path.posix.sep`.
        Assert.Equal(OperatingSystem.IsWindows() ? "\\" : "/", Glob.Sep);
    }

    [Fact]
    public void FilterReturnsAPredicateBoundToThePattern()
    {
        var tsFiles = Glob.Filter("*.ts");
        Assert.True(tsFiles("a.ts"));
        Assert.False(tsFiles("a.js"));

        // Options are part of the binding, not per call.
        var caseInsensitive = Glob.Filter("*.TS", new GlobOptions { NoCase = true });
        Assert.True(caseInsensitive("a.ts"));
    }

    [Fact]
    public void AssertValidPatternRejectsPatternsOverSixtyFourKiB()
    {
        // JS: `if (pattern.length > MAX_PATTERN_LENGTH) throw new TypeError('pattern is too long')`.
        Glob.AssertValidPattern(new string('a', 64 * 1024));
        var error = Assert.Throws<ArgumentException>(() => Glob.AssertValidPattern(new string('a', 64 * 1024 + 1)));
        Assert.Equal("pattern is too long", error.Message);

        Assert.Throws<ArgumentException>(() => Glob.Match("a", new string('a', 64 * 1024 + 1)));
    }

    [Fact]
    public void MakeReIsNullWhenNothingCanMatch()
    {
        // JS: `makeRe()` returns `false`; the port returns null. A comment and an empty pattern both leave
        // `set` empty.
        Assert.Null(new Minimatch("#a").MakeRe());
        Assert.Null(new Minimatch("").MakeRe());
        Assert.NotNull(new Minimatch("a").MakeRe());
    }

    // ---------------------------------------------------------------------
    // hasMagic
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("abc", false)]
    [InlineData("a/b/c", false)]
    [InlineData("*", true)]
    [InlineData("*.ts", true)]
    [InlineData("a/b", false)]
    [InlineData("**", true)]
    [InlineData("[abc]", true)]
    public void HasMagicMatchesTheReference(string pattern, bool expected)
        => Assert.Equal(expected, new Minimatch(pattern).HasMagic());

    [Fact]
    public void HasMagicOnlyCountsBracesWhenMagicalBracesIsSet()
    {
        // `{a,b}` expands to two literal patterns, so without magicalBraces there is no magic at all.
        Assert.False(new Minimatch("{a,b}").HasMagic());
        Assert.True(new Minimatch("{a,b}", new GlobOptions { MagicalBraces = true }).HasMagic());

        // A single expansion leaves nothing for magicalBraces to point at.
        Assert.False(new Minimatch("{a}", new GlobOptions { MagicalBraces = true }).HasMagic());
    }

    // ---------------------------------------------------------------------
    // the drive-letter fixup mutates the instance's own pattern
    // ---------------------------------------------------------------------

    [Fact]
    public void DriveLetterFixupRewritesOnlyTheMatchingInstance()
    {
        // On Windows, matchOne folds the pattern's drive letter onto the subject's spelling:
        // `pattern[pdi] = fd`. Two instances built from the same pattern must not share that rewrite.
        var first = new Minimatch("c:/a/b.ts", new GlobOptions { Platform = "win32" });
        var second = new Minimatch("c:/a/b.ts", new GlobOptions { Platform = "win32" });

        Assert.False(first.Match("C:/a.ts"));
        Assert.Equal("C:", ((MatchLiteralPart)first.Set[0][0]).Value);

        Assert.Equal("c:", ((MatchLiteralPart)second.Set[0][0]).Value);
        Assert.True(second.Match("c:/a/b.ts"));
    }

    [Fact]
    public void DriveLetterComparisonIsCaseInsensitive()
    {
        var matcher = new Minimatch("C:/A/B.TS", new GlobOptions { Platform = "win32", NoCase = true });
        Assert.True(matcher.Match("c:/a/b.ts"));
        Assert.True(matcher.Match("C:/a/b.ts"));
    }

    // ---------------------------------------------------------------------
    // brace expansion
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("{1..5}", "1|2|3|4|5")]
    [InlineData("{01..03}", "01|02|03")]
    [InlineData("{-3..3..2}", "-3|-1|1|3")]
    [InlineData("{a..e..2}", "a|c|e")]
    [InlineData("{a,b}", "a|b")]
    [InlineData("a{b,c}d", "abd|acd")]
    [InlineData("a{b,}c", "abc|ac")]
    [InlineData("a{b,c{d,e}f}g", "abg|acdfg|acefg")]
    [InlineData("a{b,c}d{e,f}g", "abdeg|abdfg|acdeg|acdfg")]
    [InlineData("{2..}", "{2..}")]
    [InlineData("{a}", "{a}")]
    [InlineData("{}", "{}")]
    public void BraceExpansionMatchesTheReference(string pattern, string expected)
        => Assert.Equal(expected.Split('|'), BraceExpansion.Expand(pattern));

    [Fact]
    public void BraceExpansionKeepsBashsLeadingBraceQuirk()
    {
        // Bash 4.3 preserves a leading `{}` at the top level only, so `a{},b}c` really does expand to
        // `[a}c, abc]`. The `escapeBraces` pre-pass is what reproduces it.
        Assert.Equal(["a}c", "abc"], BraceExpansion.Expand("a{},b}c"));

        // A bare leading `{}` is left alone rather than dropped: upstream's comment claims it "expands to
        // nothing", but `expand('{},a}b')` returns the input verbatim. The port follows the code.
        Assert.Equal(["{},a}b"], BraceExpansion.Expand("{},a}b"));
    }

    [Fact]
    public void BraceExpansionCapsTheResultCount()
    {
        var options = new BraceExpansion.Options { Max = 1 };
        Assert.Equal(["a"], BraceExpansion.Expand("{a,b}", options));

        Assert.Equal(["a", "b"], BraceExpansion.Expand("{a,b,c}", new BraceExpansion.Options { Max = 2 }));
        Assert.Equal(
            ["ac", "ad", "bc"],
            BraceExpansion.Expand("{a,b}{c,d}", new BraceExpansion.Options { Max = 3 }));
    }

    [Fact]
    public void BraceExpansionCapsTheAccumulatedLength()
    {
        // `a1` and `a2` fit in five characters; `a3` would not.
        Assert.Equal(["a1", "a2"], BraceExpansion.Expand("a{1..100}", new BraceExpansion.Options { MaxLength = 5 }));
    }

    [Fact]
    public void BraceExpansionCapsTheNestingDepth()
    {
        // Past the limit the remaining text is kept verbatim rather than expanded.
        Assert.Equal(
            ["a", "{b,{c,d}}"],
            BraceExpansion.Expand("{a,{b,{c,d}}}", new BraceExpansion.Options { MaxDepth = 0 }));
        Assert.Equal(
            ["a", "b", "{c,d}"],
            BraceExpansion.Expand("{a,{b,{c,d}}}", new BraceExpansion.Options { MaxDepth = 1 }));
        Assert.Equal(
            ["a", "b", "c", "d"],
            BraceExpansion.Expand("{a,{b,{c,d}}}"));
    }

    [Fact]
    public void BraceExpansionCapsTheBashRestartRewrites()
    {
        // The `{a},b}` quirk absorbs one `}` and restarts; maxRewrites is what keeps that bounded.
        Assert.Equal(["{a},b}"], BraceExpansion.Expand("{a},b}", new BraceExpansion.Options { MaxRewrites = 0 }));
        Assert.Equal(["a}", "b"], BraceExpansion.Expand("{a},b}", new BraceExpansion.Options { MaxRewrites = 1 }));
    }

    [Fact]
    public void GlobBraceExpandHonoursTheBraceExpandMaxOption()
    {
        Assert.Equal(["a", "b", "c"], Glob.BraceExpand("{a,b,c}"));
        Assert.Equal(["a", "b"], Glob.BraceExpand("{a,b,c}", new GlobOptions { BraceExpandMax = 2 }));
        Assert.Equal(["{a,b,c}"], Glob.BraceExpand("{a,b,c}", new GlobOptions { NoBrace = true }));
    }

    [Fact]
    public void GlobBraceExpandShortCircuitsWhenThereIsNoCompleteBraceSet()
    {
        // Upstream's ReDoS guard is `/\{(?:(?!\{).)*\}/`: a `{` must be followed by braces-free text and a
        // `}`. Without one, the pattern is returned untouched.
        Assert.Equal(["{a"], Glob.BraceExpand("{a"));
        Assert.Equal(["a}b"], Glob.BraceExpand("a}b"));

        // A complete set is expanded, nested or not.
        Assert.Equal(["abd", "acd"], Glob.BraceExpand("a{b,c}d"));
        Assert.Equal(["a", "b", "c"], Glob.BraceExpand("{a,{b,c}}"));
    }

    // ---------------------------------------------------------------------
    // escape / unescape
    // ---------------------------------------------------------------------

    [Fact]
    public void EscapeAndUnescapeUseOppositeMagicalBracesDefaults()
    {
        // Upstream destructures `magicalBraces = false` in escape and `= true` in unescape, so the two
        // disagree about braces when the option is absent: escape leaves `{a}` alone, unescape strips it.
        Assert.Equal("{a}", Glob.Escape("{a}"));
        Assert.Equal("{a}", Glob.Unescape("\\{a\\}"));

        Assert.Equal("\\{a\\}", Glob.Escape("{a}", new GlobOptions { MagicalBraces = true }));
        Assert.Equal("\\{a\\}", Glob.Unescape("\\{a\\}", new GlobOptions { MagicalBraces = false }));

        // Without magicalBraces only the other magic characters are escaped.
        Assert.Equal("\\*", Glob.Escape("*"));
        Assert.Equal("\\[a\\]", Glob.Escape("[a]"));
        Assert.Equal("\\(a\\)", Glob.Escape("(a)"));
    }

    [Fact]
    public void EscapeAndUnescapeIgnoreAllowWindowsEscape()
    {
        // Only the Minimatch constructor reads the deprecated flag; escape/unescape destructure
        // windowsPathsNoEscape alone, so `allowWindowsEscape: false` changes nothing there.
        var options = new GlobOptions { AllowWindowsEscape = false };
        Assert.Equal("\\*", Glob.Escape("*", options));
        Assert.Equal("*", Glob.Unescape("\\*", options));

        // windowsPathsNoEscape does switch them to bracket escaping.
        var brackets = new GlobOptions { WindowsPathsNoEscape = true };
        Assert.Equal("[*]", Glob.Escape("*", brackets));
        Assert.Equal("*", Glob.Unescape("[*]", brackets));
    }

    // ---------------------------------------------------------------------
    // the actual consumer: case-insensitive model-id globs
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("anthropic/claude-sonnet-4", "anthropic/*", true)]
    [InlineData("anthropic/claude-sonnet-4", "*/CLAUDE-*", true)]
    [InlineData("openai/gpt-4o", "*/CLAUDE-*", false)]
    [InlineData("anthropic/claude-sonnet-4", "**/claude-*", true)]
    [InlineData("anthropic/claude-sonnet-4", "claude-sonnet-4", true)]
    [InlineData("anthropic/claude-sonnet-4", "anthropic/claude-?", false)]
    public void ModelIdGlobsMatchCaseInsensitively(string modelId, string glob, bool expected)
    {
        // This is exactly what core/model-resolver.ts does:
        //   minimatch(fullId, globPattern, { nocase: true }) || minimatch(m.id, globPattern, { nocase: true })
        var match = Glob.Match(modelId, glob, new GlobOptions { NoCase = true })
            || Glob.Match(modelId.Split('/')[^1], glob, new GlobOptions { NoCase = true });

        Assert.Equal(expected, match);
    }

    [Fact]
    public void ModelIdGlobsAreCaseSensitiveWithoutNoCase()
    {
        Assert.True(Glob.Match("anthropic/claude-sonnet-4", "anthropic/*"));
        Assert.False(Glob.Match("anthropic/claude-sonnet-4", "*/CLAUDE-*"));
    }

    [Fact]
    public void OptionsAreNotSharedBetweenInstances()
    {
        var options = new GlobOptions { NoCase = true };
        var first = new Minimatch("*.TS", options);
        Assert.True(first.Match("a.ts"));

        // Mutating the caller's record after construction has no effect on the built matcher.
        options = options with { NoCase = false };
        Assert.True(first.Match("a.ts"));
        Assert.False(new Minimatch("*.TS", options).Match("a.ts"));
    }
}
