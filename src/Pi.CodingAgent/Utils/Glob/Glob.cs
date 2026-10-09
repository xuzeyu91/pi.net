using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Options accepted by <see cref="Glob"/> and <see cref="Minimatch"/>. Mirrors minimatch's
/// <c>MinimatchOptions</c>: every member keeps the upstream name (PascalCased) and its upstream default.
/// </summary>
/// <remarks>
/// Two upstream members are folded into one here. <c>windowsPathsNoEscape</c> and the deprecated
/// <c>allowWindowsEscape</c> are read together as
/// <c>WindowsPathsNoEscape || AllowWindowsEscape == false</c>, exactly like minimatch's <c>awe</c> trick.
/// </remarks>
public sealed record GlobOptions
{
    /// <summary>JS <c>nocomment</c>: treat a leading <c>#</c> as a literal instead of a comment.</summary>
    public bool NoComment { get; init; }

    /// <summary>JS <c>nobrace</c>: do not expand <c>{a,b}</c> sets.</summary>
    public bool NoBrace { get; init; }

    /// <summary>JS <c>noext</c>: do not support extglob syntax (<c>+(a|b)</c>).</summary>
    public bool NoExt { get; init; }

    /// <summary>JS <c>noglobstar</c>: treat <c>**</c> as <c>*</c>.</summary>
    public bool NoGlobStar { get; init; }

    /// <summary>JS <c>nocase</c>: case-insensitive matching.</summary>
    public bool NoCase { get; init; }

    /// <summary>JS <c>nocaseMagicOnly</c>: with <see cref="NoCase"/>, only force a regex for real magic.</summary>
    public bool NoCaseMagicOnly { get; init; }

    /// <summary>JS <c>dot</c>: let <c>*</c> and friends match a leading dot.</summary>
    public bool Dot { get; init; }

    /// <summary>
    /// JS <c>magicalBraces</c>. Nullable because the three consumers disagree on the default:
    /// <c>escape()</c> destructures it to <c>false</c>, <c>unescape()</c> to <c>true</c>, and
    /// <c>Minimatch.hasMagic()</c> reads it as a plain (falsy-when-absent) property.
    /// </summary>
    public bool? MagicalBraces { get; init; }

    /// <summary>JS <c>partial</c>: allow a path that is a prefix of the pattern to match.</summary>
    public bool Partial { get; init; }

    /// <summary>JS <c>preserveMultipleSlashes</c>: keep <c>a//b</c> as three path portions.</summary>
    public bool PreserveMultipleSlashes { get; init; }

    /// <summary>JS <c>nonegate</c>: do not interpret a leading <c>!</c> as negation.</summary>
    public bool NoNegate { get; init; }

    /// <summary>JS <c>flipNegate</c>: invert the meaning of <see cref="Minimatch.Negate"/>.</summary>
    public bool FlipNegate { get; init; }

    /// <summary>JS <c>matchBase</c>: match a single-portion pattern against the basename only.</summary>
    public bool MatchBase { get; init; }

    /// <summary>JS <c>nonull</c>: in <see cref="Glob.MatchList"/>, keep the pattern when nothing matched.</summary>
    public bool Nonull { get; init; }

    /// <summary>JS <c>optimizationLevel</c>: 0, 1 (the default) or 2. Selects the glob-part rewrite passes.</summary>
    public int OptimizationLevel { get; init; } = 1;

    /// <summary>JS <c>maxGlobstarRecursion</c> (default 200): caps the globstar backtracking.</summary>
    public int MaxGlobstarRecursion { get; init; } = 200;

    /// <summary>JS <c>maxExtglobRecursion</c>; upstream falls back to 2 when it is unset.</summary>
    public int? MaxExtglobRecursion { get; init; }

    /// <summary>JS <c>braceExpandMax</c>: forwarded to brace-expansion's result cap.</summary>
    public int? BraceExpandMax { get; init; }

    /// <summary>JS <c>windowsPathsNoEscape</c>: treat <c>\</c> as a path separator, never as an escape.</summary>
    public bool WindowsPathsNoEscape { get; init; }

    /// <summary>JS <c>windowsNoMagicRoot</c>; defaults to <c>IsWindows &amp;&amp; NoCase</c>.</summary>
    public bool? WindowsNoMagicRoot { get; init; }

    /// <summary>JS <c>allowWindowsEscape</c>, the deprecated spelling of "use backslash escapes on Windows".</summary>
    public bool? AllowWindowsEscape { get; init; }

    /// <summary>JS <c>platform</c>: <c>"win32"</c> or <c>"posix"</c>; auto-detected when unset.</summary>
    public string? Platform { get; init; }
}

/// <summary>
/// Port of the <c>minimatch</c> package entry point: the <c>minimatch(p, pattern, options)</c> function
/// plus the module's other exports (<c>escape</c>, <c>unescape</c>, <c>braceExpand</c>, <c>makeRe</c>,
/// <c>match</c>, <c>filter</c>, <c>sep</c>, <c>GLOBSTAR</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation behind the "case-insensitive globs" the CLI documents, so it is ported
/// line for line from <c>minimatch@10.2.6</c> rather than approximated. The parser lives in
/// <see cref="GlobAst"/>, the character classes in <see cref="GlobClass"/>, brace expansion in
/// <see cref="BraceExpansion"/>, and the matcher in <see cref="Minimatch"/>.
/// </para>
/// <para>
/// Differences from the JS original, all documented on the member that carries them:
/// <list type="bullet">
/// <item><c>assertValidPattern</c>'s <c>typeof pattern !== 'string'</c> check is enforced by the type
/// system and therefore absent; the length check is kept.</item>
/// <item>The <c>debug</c> callbacks and the <c>Symbol.for('nodejs.util.inspect.custom')</c> hooks are
/// diagnostics only and are omitted.</item>
/// <item><c>defaults(def)</c> is omitted. It builds a matcher whose defaults are merged under each call's
/// options with <c>Object.assign({}, def, options)</c>, and that merge turns on JS's "own key absent"
/// versus "own key present" distinction: <c>minimatch(p, pat, { nocase: true })</c> leaves the default's
/// <c>dot</c> in force, whereas a C# <see cref="GlobOptions"/> literal always carries a value for every
/// member and would overwrite it with <c>false</c>. Reproducing that faithfully would mean making every
/// option nullable, which buys nothing: no ported call site uses <c>defaults</c>, and the C# idiom is to
/// pass the options you want directly.</item>
/// <item><c>minimatch.AST</c> is not re-exported; <see cref="GlobAst"/> is internal to the port.</item>
/// </list>
/// </para>
/// </remarks>
public static class Glob
{
    /// <summary>JS <c>MAX_PATTERN_LENGTH</c>: 64 KiB.</summary>
    public const int MaxPatternLength = 1024 * 64;

    /// <summary>JS <c>/[?*]|[+@!]\(.*?\)|\[|\]/</c>: "does this path portion contain any magic at all?".</summary>
    internal static readonly Regex GlobMagic = new(@"[?*]|[+@!]\(.*?\)|\[|\]", RegexOptions.CultureInvariant);

    /// <summary>JS <c>/^[a-z]:/i</c>: a drive-letter prefix.</summary>
    internal static readonly Regex DriveLetterPrefix = new(@"^[a-z]:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>JS <c>/^[a-z]:$/i</c>: a bare drive-letter portion.</summary>
    internal static readonly Regex DriveLetter = new(@"^[a-z]:$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>JS <c>/^\/\/[^/]+/</c>: a UNC path prefix.</summary>
    internal static readonly Regex UncPrefix = new(@"^//[^/]+", RegexOptions.CultureInvariant);

    /// <summary>JS <c>/\{(?:(?!\{).)*\}/</c>: "is there a brace set that could expand?".</summary>
    private static readonly Regex BraceSet = new(@"\{(?:(?!\{).)*\}", RegexOptions.CultureInvariant);

    /// <summary>JS <c>/[-[\]{}()*+?.,\\^$|#\s]/g</c>, the regular-expression escape set.</summary>
    private static readonly Regex RegExpEscapePattern = new(@"[-[\]{}()*+?.,\\^$|#\s]", RegexOptions.CultureInvariant);

    private static readonly Regex EscapeMagicBracesBrackets = new(@"[?*()[\]{}]", RegexOptions.CultureInvariant);

    private static readonly Regex EscapeMagicBracesBackslash = new(@"[?*()[\]\\{}]", RegexOptions.CultureInvariant);

    private static readonly Regex EscapeBrackets = new(@"[?*()[\]]", RegexOptions.CultureInvariant);

    private static readonly Regex EscapeBackslash = new(@"[?*()[\]\\]", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapeBracketsMagic = new(@"\[([^/\\])\]", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapeBracketsNoMagic = new(@"\[([^/\\{}])\]", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapePrefixedMagic = new(@"((?!\\).|^)\[([^/\\])\]", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapePrefixedNoMagic = new(@"((?!\\).|^)\[([^/\\{}])\]", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapeBackslashMagic = new(@"\\([^/])", RegexOptions.CultureInvariant);

    private static readonly Regex UnescapeBackslashNoMagic = new(@"\\([^/{}])", RegexOptions.CultureInvariant);

    /// <summary>
    /// JS <c>minimatch.sep</c>: the path separator minimatch assumes for the host platform. Note this is
    /// <em>not</em> <see cref="NodePath.Separator"/>'s meaning in a glob: a glob always splits on <c>/</c>.
    /// </summary>
    public static string Sep => OperatingSystem.IsWindows() ? "\\" : "/";

    /// <summary>
    /// JS <c>defaultPlatform</c>: <c>process.platform</c>, overridable through
    /// <c>__MINIMATCH_TESTING_PLATFORM__</c> so the Windows branches can be exercised on any host.
    /// </summary>
    internal static string DefaultPlatform =>
        Environment.GetEnvironmentVariable("__MINIMATCH_TESTING_PLATFORM__")
        ?? (OperatingSystem.IsWindows() ? "win32" : "posix");

    /// <summary>
    /// JS <c>GLOBSTAR</c>: the <c>**</c> sentinel. A singleton, so it can be compared by reference the way
    /// the JS <c>Symbol</c> is.
    /// </summary>
    public static MatchGlobstarPart GlobStar => MatchGlobstarPart.Instance;

    /// <summary>
    /// JS <c>assertValidPattern(pattern)</c>. The <c>typeof</c> branch is a compile-time guarantee in C#,
    /// so only the length cap remains. Upstream throws <c>TypeError</c>; the closest BCL type is used and
    /// the message text is preserved.
    /// </summary>
    public static void AssertValidPattern(string pattern)
    {
        if (pattern.Length > MaxPatternLength) throw new ArgumentException("pattern is too long");
    }

    /// <summary>JS <c>minimatch(p, pattern, options)</c>.</summary>
    public static bool Match(string p, string pattern, GlobOptions? options = null)
    {
        AssertValidPattern(pattern);
        var opt = options ?? new GlobOptions();

        // shortcut: comments match nothing.
        if (!opt.NoComment && JsString.CharAt(pattern, 0) == '#') return false;

        return new Minimatch(pattern, opt).Match(p);
    }

    /// <summary>JS <c>minimatch.filter(pattern, options)</c>: a predicate bound to one pattern.</summary>
    public static Func<string, bool> Filter(string pattern, GlobOptions? options = null)
        => p => Match(p, pattern, options);

    /// <summary>JS <c>minimatch.makeRe(pattern, options)</c>.</summary>
    public static Regex? MakeRe(string pattern, GlobOptions? options = null)
        => new Minimatch(pattern, options).MakeRe();

    /// <summary>JS <c>minimatch.match(list, pattern, options)</c>: keep the entries that match.</summary>
    public static List<string> MatchList(IReadOnlyList<string> list, string pattern, GlobOptions? options = null)
    {
        var matcher = new Minimatch(pattern, options);
        var result = new List<string>();
        foreach (var f in list)
        {
            if (matcher.Match(f)) result.Add(f);
        }

        if (matcher.Options.Nonull && result.Count == 0) result.Add(pattern);
        return result;
    }

    /// <summary>JS <c>minimatch.braceExpand(pattern, options)</c>.</summary>
    public static IReadOnlyList<string> BraceExpand(string pattern, GlobOptions? options = null)
    {
        AssertValidPattern(pattern);
        var opt = options ?? new GlobOptions();

        // The upstream comment credits Yeting Li for the shape of this regexp: it avoids a ReDoS
        // vulnerability by refusing to consider a brace set that itself contains a `{`.
        if (opt.NoBrace || !BraceSet.IsMatch(pattern))
        {
            // shortcut. no need to expand.
            return [pattern];
        }

        // `{ max: undefined }` lets brace-expansion pick its own default, so only forward an explicit cap.
        return BraceExpansion.Expand(
            pattern,
            opt.BraceExpandMax is null ? null : new BraceExpansion.Options { Max = opt.BraceExpandMax.Value });
    }

    /// <summary>JS <c>minimatch.escape(s, options)</c>. Note <c>magicalBraces</c> defaults to false here.</summary>
    /// <remarks>
    /// Only <c>windowsPathsNoEscape</c> is read — upstream's <c>escape</c> destructures exactly that one
    /// flag and never looks at the deprecated <c>allowWindowsEscape</c>, unlike <see cref="Minimatch"/>.
    /// </remarks>
    public static string Escape(string s, GlobOptions? options = null)
    {
        var opt = options ?? new GlobOptions();
        var windowsPathsNoEscape = opt.WindowsPathsNoEscape;

        // `+@!` need no escaping: the parens that make them magic are escaped instead, and `[!]` would not
        // be a valid glob class anyway.
        if (opt.MagicalBraces == true)
        {
            return windowsPathsNoEscape
                ? EscapeMagicBracesBrackets.Replace(s, "[$&]")
                : EscapeMagicBracesBackslash.Replace(s, "\\$&");
        }

        return windowsPathsNoEscape
            ? EscapeBrackets.Replace(s, "[$&]")
            : EscapeBackslash.Replace(s, "\\$&");
    }

    /// <summary>
    /// JS <c>minimatch.unescape(s, options)</c>. Note <c>magicalBraces</c> defaults to <em>true</em> here,
    /// the opposite of <see cref="Escape"/> — that asymmetry is upstream's.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Escape"/>, only <c>windowsPathsNoEscape</c> is read; <c>allowWindowsEscape</c> is a
    /// <see cref="Minimatch"/>-only flag upstream.
    /// </remarks>
    public static string Unescape(string s, GlobOptions? options = null)
    {
        var opt = options ?? new GlobOptions();
        var windowsPathsNoEscape = opt.WindowsPathsNoEscape;

        if (opt.MagicalBraces ?? true)
        {
            return windowsPathsNoEscape
                ? UnescapeBracketsMagic.Replace(s, "$1")
                : UnescapeBackslashMagic.Replace(UnescapePrefixedMagic.Replace(s, "$1$2"), "$1");
        }

        return windowsPathsNoEscape
            ? UnescapeBracketsNoMagic.Replace(s, "$1")
            : UnescapeBackslashNoMagic.Replace(UnescapePrefixedNoMagic.Replace(s, "$1$2"), "$1");
    }

    /// <summary>JS <c>regExpEscape(s)</c>: escape every regular-expression metacharacter.</summary>
    internal static string RegExpEscape(string s) => RegExpEscapePattern.Replace(s, "\\$&");
}
