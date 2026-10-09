using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of minimatch's <c>Minimatch</c> class: parse a glob into a set of path-portion matchers and test
/// paths against it.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline mirrors upstream exactly. The constructor runs <c>make()</c>: strip negation, brace-expand,
/// split each expansion on <c>/</c>, rewrite the resulting path portions (<c>preprocess</c>), then compile
/// every portion (<c>parse</c>). <see cref="Match"/> splits the subject the same way and walks the two
/// portion lists, with <c>**</c> handled by <see cref="MatchGlobstar"/>'s head/body/tail search.
/// </para>
/// <para>
/// Differences from the JS original:
/// <list type="bullet">
/// <item><c>parse()</c> could return <c>false</c> in older releases; in 10.x it cannot, so the
/// <c>set.filter(s =&gt; s.indexOf(false) === -1)</c> guard in <c>make()</c> is kept as a comment rather than
/// as dead code, and <c>matchOne</c>'s <c>p === false</c> branch is dropped with it.</item>
/// <item>The <c>debug()</c> trace calls are diagnostics only and are omitted.</item>
/// <item><c>makeRe()</c> catches <see cref="ArgumentException"/> where upstream uses a bare <c>catch</c>:
/// the only thing that can fail there is <see cref="Regex"/> construction.</item>
/// <item>The <c>u</c> regular-expression flag is not carried over; .NET compiles <c>\p{...}</c> without it.
/// See <see cref="GlobClass"/>.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class Minimatch
{
    /// <summary>JS <c>starDotExtRE</c>: <c>*</c>, <c>**</c>, or <c>*</c> followed by a literal extension.</summary>
    private static readonly Regex StarDotExtRe = new(@"^\*+([^+@!?*[(]*)$", RegexOptions.CultureInvariant);

    /// <summary>JS <c>starDotStarRE</c>.</summary>
    private static readonly Regex StarDotStarRe = new(@"^\*+\.\*+$", RegexOptions.CultureInvariant);

    /// <summary>JS <c>dotStarRE</c>.</summary>
    private static readonly Regex DotStarRe = new(@"^\.\*+$", RegexOptions.CultureInvariant);

    /// <summary>JS <c>starRE</c>.</summary>
    private static readonly Regex StarRe = new(@"^\*+$", RegexOptions.CultureInvariant);

    /// <summary>JS <c>qmarksRE</c>: one or more <c>?</c>, optionally followed by a literal extension.</summary>
    private static readonly Regex QmarksRe = new(@"^\?+([^+@!?*[(]*)?$", RegexOptions.CultureInvariant);

    /// <summary>JS <c>/\/+/</c>, used by <c>slashSplit</c>.</summary>
    private static readonly Regex MultipleSlashes = new(@"/+", RegexOptions.CultureInvariant);

    // any single thing other than /
    private const string QMark = "[^/]";

    // * => any number of characters
    private const string Star = QMark + "*?";

    // ** when dots are allowed. Anything goes, except .. and .
    private const string TwoStarDot = @"(?:(?!(?:\/|^)(?:\.{1,2})($|\/)).)*?";

    // not a ^ or / followed by a dot, followed by anything, any number of times.
    private const string TwoStarNoDot = @"(?:(?!(?:\/|^)\.).)*?";

    private readonly List<string> _globSet = [];
    private readonly List<List<string>> _globParts = [];
    private readonly List<List<MatchPart>> _set = [];

    private Regex? _regexp;
    private bool _regexpResolved;

    /// <summary>JS <c>new Minimatch(pattern, options)</c>.</summary>
    public Minimatch(string pattern, GlobOptions? options = null)
    {
        Glob.AssertValidPattern(pattern);
        Options = options ?? new GlobOptions();
        MaxGlobstarRecursion = Options.MaxGlobstarRecursion;
        Pattern = pattern;
        Platform = Options.Platform ?? Glob.DefaultPlatform;
        IsWindows = string.Equals(Platform, "win32", StringComparison.Ordinal);

        // The `allowWindowsEscape === false` spelling is deprecated but still honoured, so both are read
        // together exactly like upstream's `awe` concatenation trick.
        WindowsPathsNoEscape = Options.WindowsPathsNoEscape || Options.AllowWindowsEscape == false;
        if (WindowsPathsNoEscape) Pattern = Pattern.Replace("\\", "/", StringComparison.Ordinal);

        PreserveMultipleSlashes = Options.PreserveMultipleSlashes;
        NoNegate = Options.NoNegate;
        Partial = Options.Partial;
        NoCase = Options.NoCase;
        WindowsNoMagicRoot = Options.WindowsNoMagicRoot ?? (IsWindows && NoCase);

        Make();
    }

    /// <summary>The options this instance was built with.</summary>
    public GlobOptions Options { get; }

    /// <summary>JS <c>maxGlobstarRecursion</c>, defaulting to 200.</summary>
    public int MaxGlobstarRecursion { get; }

    /// <summary>The pattern, with any leading <c>!</c> removed by <see cref="ParseNegate"/>.</summary>
    public string Pattern { get; private set; }

    /// <summary>JS <c>platform</c>.</summary>
    public string Platform { get; }

    /// <summary>JS <c>isWindows</c>.</summary>
    public bool IsWindows { get; }

    /// <summary>JS <c>windowsPathsNoEscape</c>.</summary>
    public bool WindowsPathsNoEscape { get; }

    /// <summary>JS <c>windowsNoMagicRoot</c>.</summary>
    public bool WindowsNoMagicRoot { get; }

    /// <summary>JS <c>nonegate</c>.</summary>
    public bool NoNegate { get; }

    /// <summary>JS <c>negate</c>: whether the pattern was prefixed with an odd number of <c>!</c>.</summary>
    public bool Negate { get; private set; }

    /// <summary>JS <c>comment</c>: the pattern was a <c>#</c> comment and matches nothing.</summary>
    public bool Comment { get; private set; }

    /// <summary>JS <c>empty</c>: the pattern was empty and matches only the empty string.</summary>
    public bool Empty { get; private set; }

    /// <summary>JS <c>partial</c>.</summary>
    public bool Partial { get; }

    /// <summary>JS <c>preserveMultipleSlashes</c>.</summary>
    public bool PreserveMultipleSlashes { get; }

    /// <summary>JS <c>nocase</c>.</summary>
    public bool NoCase { get; }

    /// <summary>JS <c>globSet</c>: the brace-expanded patterns, deduplicated.</summary>
    public IReadOnlyList<string> GlobSet => _globSet;

    /// <summary>JS <c>globParts</c>: each expanded pattern split into path portions and rewritten.</summary>
    public IReadOnlyList<IReadOnlyList<string>> GlobParts => _globParts;

    /// <summary>JS <c>set</c>: the compiled path-portion matchers.</summary>
    public IReadOnlyList<IReadOnlyList<MatchPart>> Set => _set;

    /// <summary>JS <c>hasMagic()</c>.</summary>
    public bool HasMagic()
    {
        if (Options.MagicalBraces == true && _set.Count > 1) return true;

        foreach (var pattern in _set)
        {
            foreach (var part in pattern)
            {
                if (part is not MatchLiteralPart) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// JS <c>makeRe()</c>: the single regex matching every pattern in <see cref="Set"/>, or <c>null</c> when
    /// the pattern matches nothing (upstream returns <c>false</c> there). The result is memoised.
    /// </summary>
    public Regex? MakeRe()
    {
        if (_regexpResolved) return _regexp;
        _regexpResolved = true;

        if (_set.Count == 0) return _regexp = null;

        var twoStar = Options.NoGlobStar ? Star : Options.Dot ? TwoStarDot : TwoStarNoDot;
        var ignoreCase = Options.NoCase;

        var pieces = new List<string>();
        foreach (var pattern in _set)
        {
            var pp = new List<Piece>(pattern.Count);
            foreach (var part in pattern)
            {
                switch (part)
                {
                    case MatchRegexPart regexPart:
                        if (regexPart.Regex.Options.HasFlag(RegexOptions.IgnoreCase)) ignoreCase = true;
                        pp.Add(Piece.Of(regexPart.Src));
                        break;
                    case MatchGlobstarPart:
                        pp.Add(Piece.GlobStar);
                        break;
                    default:
                        pp.Add(Piece.Of(Glob.RegExpEscape(((MatchLiteralPart)part).Value)));
                        break;
                }
            }

            // Splice each `**` into its neighbours: a leading `**` swallows the next portion, a trailing one
            // attaches to the previous, and a middle one folds the portions on both sides into itself.
            for (var i = 0; i < pp.Count; i++)
            {
                if (!pp[i].IsGlobStar) continue;
                if (i > 0 && pp[i - 1].IsGlobStar) continue;

                var hasNext = i + 1 < pp.Count;
                if (i == 0)
                {
                    if (hasNext && !pp[i + 1].IsGlobStar)
                    {
                        pp[i + 1] = Piece.Of("(?:\\/|" + twoStar + "\\/)?" + pp[i + 1].Source);
                    }
                    else
                    {
                        pp[i] = Piece.Of(twoStar);
                    }
                }
                else if (!hasNext)
                {
                    pp[i - 1] = Piece.Of(pp[i - 1].Source + "(?:\\/|\\/" + twoStar + ")?");
                }
                else if (!pp[i + 1].IsGlobStar)
                {
                    pp[i - 1] = Piece.Of(pp[i - 1].Source + "(?:\\/|\\/" + twoStar + "\\/)" + pp[i + 1].Source);
                    pp[i + 1] = Piece.GlobStar;
                }
            }

            var filtered = new List<string>();
            foreach (var piece in pp)
            {
                if (!piece.IsGlobStar) filtered.Add(piece.Source);
            }

            // For partial matches the pattern has to match any prefix of the full path, so emit one
            // alternative per progressively longer prefix.
            if (Partial && filtered.Count >= 1)
            {
                var prefixes = new List<string>(filtered.Count);
                for (var n = 1; n <= filtered.Count; n++)
                {
                    prefixes.Add(string.Join("/", filtered.GetRange(0, n)));
                }

                pieces.Add("(?:" + string.Join("|", prefixes) + ")");
            }
            else
            {
                pieces.Add(string.Join("/", filtered));
            }
        }

        var re = string.Join("|", pieces);

        // Parens are needed once `|` is in play, otherwise only the first alternative is anchored to ^ and
        // the last to $.
        var open = _set.Count > 1 ? "(?:" : "";
        var close = _set.Count > 1 ? ")" : "";
        re = "^" + open + re + close + "$";

        // In partial mode `/` always matches, since it is a valid prefix of any pattern.
        if (Partial) re = "^(?:\\/|" + open + re[1..^1] + close + ")$";

        // Can match anything, as long as it is not this.
        if (Negate) re = "^(?!" + re + ").+$";

        var regexOptions = RegexOptions.CultureInvariant;
        if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;

        try
        {
            _regexp = new Regex(re, regexOptions);
        }
        catch (ArgumentException)
        {
            // should be impossible
            _regexp = null;
        }

        return _regexp;
    }

    /// <summary>JS <c>match(f, partial)</c>. Pass <c>null</c> for <paramref name="partial"/> to use the option.</summary>
    public bool Match(string f, bool? partial = null)
    {
        var usePartial = partial ?? Partial;

        // short-circuit in the case of busted things: comments, etc.
        if (Comment) return false;
        if (Empty) return f.Length == 0;
        if (f == "/" && usePartial) return true;

        // windows: need to use /, not \
        if (IsWindows) f = f.Replace("\\", "/", StringComparison.Ordinal);

        var ff = SlashSplit(f);

        // The basename is the last non-empty portion.
        var filename = ff[^1];
        if (filename.Length == 0)
        {
            for (var i = ff.Count - 2; filename.Length == 0 && i >= 0; i--) filename = ff[i];
        }

        foreach (var pattern in _set)
        {
            var file = ff;
            if (Options.MatchBase && pattern.Count == 1) file = [filename];

            if (MatchOne(file, pattern, usePartial, 0, 0))
            {
                if (Options.FlipNegate) return true;
                return !Negate;
            }
        }

        // No hits at all: success for a negative pattern, failure otherwise.
        if (Options.FlipNegate) return false;
        return Negate;
    }

    /// <summary>JS <c>make()</c>: the whole construction pipeline, run once from the constructor.</summary>
    private void Make()
    {
        // empty patterns and comments match nothing.
        if (!Options.NoComment && JsString.CharAt(Pattern, 0) == '#')
        {
            Comment = true;
            return;
        }

        if (Pattern.Length == 0)
        {
            Empty = true;
            return;
        }

        // step 1: figure out negation, etc.
        ParseNegate();

        // step 2: expand braces
        foreach (var expanded in Glob.BraceExpand(Pattern, Options))
        {
            if (!_globSet.Contains(expanded, StringComparer.Ordinal)) _globSet.Add(expanded);
        }

        // step 3: turn each expanded pattern into a series of path-portion matchers.
        var rawGlobParts = new List<List<string>>(_globSet.Count);
        foreach (var expanded in _globSet) rawGlobParts.Add(SlashSplit(expanded));

        _globParts.AddRange(Preprocess(rawGlobParts));

        var set = new List<List<MatchPart>>(_globParts.Count);
        foreach (var s in _globParts)
        {
            // On Windows with `windowsNoMagicRoot`, a UNC or drive-letter root is kept as literal portions
            // so its `?` and `:` are not treated as glob syntax.
            if (IsWindows && WindowsNoMagicRoot)
            {
                var isUnc = IsEmptyPortion(s, 0) && IsEmptyPortion(s, 1)
                    && (IsQuestionPortion(s, 2) || !HasMagic(s, 2))
                    && !HasMagic(s, 3);
                if (isUnc)
                {
                    var unc = new List<MatchPart>();
                    for (var i = 0; i < Math.Min(4, s.Count); i++) unc.Add(new MatchLiteralPart(s[i]));
                    for (var i = 4; i < s.Count; i++) unc.Add(Parse(s[i]));
                    set.Add(unc);
                    continue;
                }

                if (s.Count > 0 && Glob.DriveLetterPrefix.IsMatch(s[0]))
                {
                    var drive = new List<MatchPart> { new MatchLiteralPart(s[0]) };
                    for (var i = 1; i < s.Count; i++) drive.Add(Parse(s[i]));
                    set.Add(drive);
                    continue;
                }
            }

            var portions = new List<MatchPart>(s.Count);
            foreach (var portion in s) portions.Add(Parse(portion));
            set.Add(portions);
        }

        // Upstream filters out entries containing `false` here, which `parse()` has not been able to return
        // since 9.x. Keeping the filter would mean inventing a representation for an impossible value.
        _set.AddRange(set);

        // Do not treat the ? in UNC paths as magic.
        if (!IsWindows) return;
        for (var i = 0; i < _set.Count; i++)
        {
            var p = _set[i];
            if (p.Count <= 3
                || p[0] is not MatchLiteralPart { Value.Length: 0 }
                || p[1] is not MatchLiteralPart { Value.Length: 0 }
                || _globParts[i].Count <= 2
                || _globParts[i][2] != "?"
                || p[3] is not MatchLiteralPart drive
                || !Glob.DriveLetter.IsMatch(drive.Value))
            {
                continue;
            }

            p[2] = new MatchLiteralPart("?");
        }
    }

    /// <summary>JS <c>parseNegate()</c>: count leading <c>!</c>s and strip them.</summary>
    private void ParseNegate()
    {
        if (NoNegate) return;

        var pattern = Pattern;
        var negate = false;
        var negateOffset = 0;
        for (var i = 0; i < pattern.Length && JsString.CharAt(pattern, i) == '!'; i++)
        {
            negate = !negate;
            negateOffset++;
        }

        if (negateOffset > 0) Pattern = JsString.Slice(pattern, negateOffset);
        Negate = negate;
    }

    /// <summary>JS <c>preprocess(globParts)</c>: pick a rewrite level and run it.</summary>
    private List<List<string>> Preprocess(List<List<string>> globParts)
    {
        // if we're not in globstar mode, then turn ** into *
        if (Options.NoGlobStar)
        {
            foreach (var partset in globParts)
            {
                for (var j = 0; j < partset.Count; j++)
                {
                    if (partset[j] == "**") partset[j] = "*";
                }
            }
        }

        if (Options.OptimizationLevel >= 2)
        {
            // aggressive optimization for the purpose of fs walking
            globParts = FirstPhasePreProcess(globParts);
            globParts = SecondPhasePreProcess(globParts);
        }
        else if (Options.OptimizationLevel >= 1)
        {
            // just basic optimizations to remove some .. parts
            globParts = LevelOneOptimize(globParts);
        }
        else
        {
            // just collapse multiple ** portions into one
            globParts = AdjascentGlobstarOptimize(globParts);
        }

        return globParts;
    }

    /// <summary>JS <c>adjascentGlobstarOptimize</c>: drop the runs of <c>**</c> down to one.</summary>
    private static List<List<string>> AdjascentGlobstarOptimize(List<List<string>> globParts)
    {
        foreach (var parts in globParts)
        {
            var gs = -1;
            while ((gs = parts.IndexOf("**", gs + 1)) != -1)
            {
                var i = gs;
                while (i + 1 < parts.Count && parts[i + 1] == "**") i++;
                if (i != gs) parts.RemoveRange(gs, i - gs);
            }
        }

        return globParts;
    }

    /// <summary>JS <c>levelOneOptimize</c>: drop adjacent <c>**</c> and resolve <c>..</c> portions.</summary>
    private static List<List<string>> LevelOneOptimize(List<List<string>> globParts)
    {
        foreach (var parts in globParts)
        {
            var set = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                // JS reads `set[set.length - 1]` and then tests it for truthiness, so both "no previous
                // element" and "previous element is empty" take the same branch.
                var prev = set.Count > 0 ? set[^1] : null;
                if (part == "**" && prev == "**") continue;

                if (part == ".." && prev is { Length: > 0 } && prev != ".." && prev != "." && prev != "**")
                {
                    set.RemoveAt(set.Count - 1);
                    continue;
                }

                set.Add(part);
            }

            parts.Clear();
            if (set.Count == 0) parts.Add("");
            else parts.AddRange(set);
        }

        return globParts;
    }

    /// <summary>JS <c>levelTwoFileOptimize(parts)</c>: the same rewrites, applied to a single path.</summary>
    private List<string> LevelTwoFileOptimize(List<string> parts)
    {
        bool didSomething;
        do
        {
            didSomething = false;

            // <pre>/<e>/<rest> -> <pre>/<rest>
            if (!PreserveMultipleSlashes)
            {
                for (var i = 1; i < parts.Count - 1; i++)
                {
                    var p = parts[i];

                    // don't squeeze out UNC patterns
                    if (i == 1 && p.Length == 0 && parts[0].Length == 0) continue;

                    if (p == "." || p.Length == 0)
                    {
                        didSomething = true;
                        parts.RemoveAt(i);
                        i--;
                    }
                }

                if (parts.Count == 2 && parts[0] == "." && (parts[1] == "." || parts[1].Length == 0))
                {
                    didSomething = true;
                    parts.RemoveAt(parts.Count - 1);
                }
            }

            // <pre>/<p>/../<rest> -> <pre>/<rest>
            var dd = 0;
            while ((dd = parts.IndexOf("..", dd + 1)) != -1)
            {
                var p = dd > 0 ? parts[dd - 1] : null;
                if (p is { Length: > 0 } && p != "." && p != ".." && p != "**"
                    && !(IsWindows && Glob.DriveLetter.IsMatch(p)))
                {
                    didSomething = true;
                    parts.RemoveRange(dd - 1, 2);
                    dd -= 2;
                }
            }
        }
        while (didSomething);

        if (parts.Count == 0) parts.Add("");
        return parts;
    }

    /// <summary>
    /// JS <c>firstPhasePreProcess(globParts)</c>: the single-pattern rewrites.
    /// </summary>
    /// <remarks>
    /// The outer loop is index-based on purpose. Upstream iterates with <c>for...of</c> while the body
    /// pushes new alternatives onto the same array, so the loop keeps visiting the arrays it just created —
    /// that is the worklist. A C# <c>foreach</c> would throw on the same mutation.
    /// </remarks>
    private List<List<string>> FirstPhasePreProcess(List<List<string>> globParts)
    {
        bool didSomething;
        do
        {
            didSomething = false;

            // <pre>/**/../<p>/<p>/<rest> -> {<pre>/../<p>/<p>/<rest>, <pre>/**/<p>/<p>/<rest>}
            for (var k = 0; k < globParts.Count; k++)
            {
                var parts = globParts[k];
                var gs = -1;
                while ((gs = parts.IndexOf("**", gs + 1)) != -1)
                {
                    var gss = gs;
                    while (gss + 1 < parts.Count && parts[gss + 1] == "**") gss++;

                    // eg, if gs is 2 and gss is 4, that means we have 3 ** parts, and can remove 2 of them.
                    if (gss > gs) parts.RemoveRange(gs + 1, gss - gs);

                    var next = gs + 1 < parts.Count ? parts[gs + 1] : null;
                    var p = gs + 2 < parts.Count ? parts[gs + 2] : null;
                    var p2 = gs + 3 < parts.Count ? parts[gs + 3] : null;
                    if (next != "..") continue;
                    if (string.IsNullOrEmpty(p) || p == "." || p == ".."
                        || string.IsNullOrEmpty(p2) || p2 == "." || p2 == "..")
                    {
                        continue;
                    }

                    didSomething = true;

                    // edit parts in place, and push the new one
                    parts.RemoveAt(gs);
                    var other = new List<string>(parts);
                    other[gs] = "**";
                    globParts.Add(other);
                    gs--;
                }

                // <pre>/<e>/<rest> -> <pre>/<rest>
                if (!PreserveMultipleSlashes)
                {
                    for (var i = 1; i < parts.Count - 1; i++)
                    {
                        var p = parts[i];

                        // don't squeeze out UNC patterns
                        if (i == 1 && p.Length == 0 && parts[0].Length == 0) continue;

                        if (p == "." || p.Length == 0)
                        {
                            didSomething = true;
                            parts.RemoveAt(i);
                            i--;
                        }
                    }

                    if (parts.Count == 2 && parts[0] == "." && (parts[1] == "." || parts[1].Length == 0))
                    {
                        didSomething = true;
                        parts.RemoveAt(parts.Count - 1);
                    }
                }

                // <pre>/<p>/../<rest> -> <pre>/<rest>
                var dd = 0;
                while ((dd = parts.IndexOf("..", dd + 1)) != -1)
                {
                    var p = dd > 0 ? parts[dd - 1] : null;
                    if (p is not { Length: > 0 } || p == "." || p == ".." || p == "**") continue;

                    didSomething = true;

                    // A `<pre>/<p>/../**` has to keep a `.` so the `**` still has something to attach to.
                    var needDot = dd == 1 && dd + 1 < parts.Count && parts[dd + 1] == "**";
                    parts.RemoveRange(dd - 1, 2);
                    if (needDot) parts.Insert(dd - 1, ".");
                    if (parts.Count == 0) parts.Add("");
                    dd -= 2;
                }
            }
        }
        while (didSomething);

        return globParts;
    }

    /// <summary>
    /// JS <c>secondPhasePreProcess(globParts)</c>: fold pattern pairs that are equivalent into one.
    /// </summary>
    private List<List<string>> SecondPhasePreProcess(List<List<string>> globParts)
    {
        for (var i = 0; i < globParts.Count - 1; i++)
        {
            for (var j = i + 1; j < globParts.Count; j++)
            {
                var matched = PartsMatch(globParts[i], globParts[j], !PreserveMultipleSlashes);
                if (matched is null) continue;
                globParts[i] = [];
                globParts[j] = matched;
                break;
            }
        }

        var result = new List<List<string>>(globParts.Count);
        foreach (var gs in globParts)
        {
            if (gs.Count > 0) result.Add(gs);
        }

        return result;
    }

    /// <summary>
    /// JS <c>partsMatch(a, b, emptyGSMatch)</c>: the merged pattern when the two are equivalent, else
    /// <c>null</c> (upstream returns <c>false</c>).
    /// </summary>
    private List<string>? PartsMatch(List<string> a, List<string> b, bool emptyGsMatch)
    {
        var ai = 0;
        var bi = 0;
        var result = new List<string>();
        var which = "";
        while (ai < a.Count && bi < b.Count)
        {
            if (a[ai] == b[bi])
            {
                result.Add(which == "b" ? b[bi] : a[ai]);
                ai++;
                bi++;
            }
            else if (emptyGsMatch && a[ai] == "**" && ai + 1 < a.Count && a[ai + 1] == b[bi])
            {
                result.Add(a[ai]);
                ai++;
            }
            else if (emptyGsMatch && b[bi] == "**" && bi + 1 < b.Count && a[ai] == b[bi + 1])
            {
                result.Add(b[bi]);
                bi++;
            }
            else if (a[ai] == "*" && b[bi].Length > 0
                && (Options.Dot || !b[bi].StartsWith('.')) && b[bi] != "**")
            {
                if (which == "b") return null;
                which = "a";
                result.Add(a[ai]);
                ai++;
                bi++;
            }
            else if (b[bi] == "*" && a[ai].Length > 0
                && (Options.Dot || !a[ai].StartsWith('.')) && a[ai] != "**")
            {
                if (which == "a") return null;
                which = "b";
                result.Add(b[bi]);
                ai++;
                bi++;
            }
            else
            {
                return null;
            }
        }

        // Falling out of the loop means the two are identical, as long as their lengths match. An empty
        // `result` is still a match here, because upstream tests the array for truthiness.
        return a.Count == b.Count ? result : null;
    }

    /// <summary>JS <c>matchOne(file, pattern, partial)</c>: dispatch to the globstar search or the linear walk.</summary>
    private bool MatchOne(List<string> file, List<MatchPart> pattern, bool partial, int fileIndex, int patternIndex)
    {
        var fileStartIndex = fileIndex;
        var patternStartIndex = patternIndex;

        // UNC paths like //?/X:/... can match X:/... and vice versa. Drive letters in absolute drive or
        // UNC paths are always compared case-insensitively.
        if (IsWindows)
        {
            var fileDrive = file.Count > 0 && Glob.DriveLetter.IsMatch(file[0]);
            var fileUnc = !fileDrive && file.Count > 3
                && file[0].Length == 0 && file[1].Length == 0 && file[2] == "?" && Glob.DriveLetter.IsMatch(file[3]);
            var patternDrive = pattern.Count > 0
                && pattern[0] is MatchLiteralPart pd && Glob.DriveLetter.IsMatch(pd.Value);
            var patternUnc = !patternDrive && pattern.Count > 3
                && pattern[0] is MatchLiteralPart { Value.Length: 0 }
                && pattern[1] is MatchLiteralPart { Value.Length: 0 }
                && pattern[2] is MatchLiteralPart { Value: "?" }
                && pattern[3] is MatchLiteralPart pu && Glob.DriveLetter.IsMatch(pu.Value);

            int? fdi = fileUnc ? 3 : fileDrive ? 0 : null;
            int? pdi = patternUnc ? 3 : patternDrive ? 0 : null;
            if (fdi is not null && pdi is not null)
            {
                var fd = file[fdi.Value];
                var patternDriveLetter = ((MatchLiteralPart)pattern[pdi.Value]).Value;

                // start matching at the drive letter index of each
                if (string.Equals(
                        JsString.ToLowerCase(fd), JsString.ToLowerCase(patternDriveLetter), StringComparison.Ordinal))
                {
                    pattern[pdi.Value] = new MatchLiteralPart(fd);
                    patternStartIndex = pdi.Value;
                    fileStartIndex = fdi.Value;
                }
            }
        }

        // Resolve and reduce . and .. portions in the file as well. Only the second phase is needed,
        // because there is only one string[].
        if (Options.OptimizationLevel >= 2) file = LevelTwoFileOptimize(file);

        if (pattern.Contains(MatchGlobstarPart.Instance))
        {
            return MatchGlobstar(file, pattern, partial, fileStartIndex, patternStartIndex);
        }

        return MatchOneLinear(file, pattern, partial, fileStartIndex, patternStartIndex);
    }

    /// <summary>JS <c>#matchGlobstar</c>: split on <c>**</c> into head, body and tail, then search the body.</summary>
    private bool MatchGlobstar(List<string> file, List<MatchPart> pattern, bool partial, int fileIndex, int patternIndex)
    {
        // split the pattern into head, tail, and middle of ** delimited parts
        var firstgs = pattern.IndexOf(MatchGlobstarPart.Instance, patternIndex);
        var lastgs = pattern.LastIndexOf(MatchGlobstarPart.Instance);

        // The tail has to be at the end, and the others just have to be found in order from the head.
        List<MatchPart> head;
        List<MatchPart> body;
        List<MatchPart> tail;
        if (partial)
        {
            head = Slice(pattern, patternIndex, firstgs);
            body = Slice(pattern, firstgs + 1, pattern.Count);
            tail = [];
        }
        else
        {
            head = Slice(pattern, patternIndex, firstgs);
            body = Slice(pattern, firstgs + 1, lastgs);
            tail = Slice(pattern, lastgs + 1, pattern.Count);
        }

        // check the head, from the current file/pattern index.
        if (head.Count > 0)
        {
            var fileHead = Slice(file, fileIndex, fileIndex + head.Count);
            if (!MatchOneLinear(fileHead, head, partial, 0, 0)) return false;
            fileIndex += head.Count;
            patternIndex += head.Count;
        }

        // now we know the head matches. If the last portion is not empty, it MUST match the end.
        var fileTailMatch = 0;
        if (tail.Count > 0)
        {
            // if head + tail > file, then we cannot possibly match
            if (tail.Count + fileIndex > file.Count) return false;

            var tailStart = file.Count - tail.Count;
            if (MatchOneLinear(file, tail, partial, tailStart, 0))
            {
                fileTailMatch = tail.Count;
            }
            else
            {
                // affordance for stuff like a/**/* matching a/b/
                // if the last file portion is '', and there's more to the pattern, try without the '' bit.
                if (file[^1].Length != 0 || fileIndex + tail.Count == file.Count) return false;
                tailStart--;
                if (!MatchOneLinear(file, tail, partial, tailStart, 0)) return false;
                fileTailMatch = tail.Count + 1;
            }
        }

        // The middle is zero or more portions wrapped in **, possibly containing more ** sections, so
        // a/**/b/**/c/**/d has become **/b/**/c/**.
        if (body.Count == 0)
        {
            var sawSome = fileTailMatch != 0;
            for (var i = fileIndex; i < file.Count - fileTailMatch; i++)
            {
                var f = file[i];
                sawSome = true;
                if (f == "." || f == ".." || (!Options.Dot && f.StartsWith('.')))
                {
                    return false;
                }
            }

            // in partial mode, we just need to get past all file parts
            return partial || sawSome;
        }

        // Split the body into sections, noting the last possible position for each: the file length minus
        // the number of non-globstar portions from this section onwards.
        var bodySegments = new List<BodySegment> { new() };
        var currentBody = bodySegments[0];
        var nonGsParts = 0;
        var nonGsPartsSums = new List<int> { 0 };
        foreach (var b in body)
        {
            if (b is MatchGlobstarPart)
            {
                nonGsPartsSums.Add(nonGsParts);
                currentBody = new BodySegment();
                bodySegments.Add(currentBody);
            }
            else
            {
                currentBody.Body.Add(b);
                nonGsParts++;
            }
        }

        var index = bodySegments.Count - 1;
        var fileLength = file.Count - fileTailMatch;
        foreach (var segment in bodySegments)
        {
            segment.After = fileLength - (nonGsPartsSums[index--] + segment.Body.Count);
        }

        return MatchGlobStarBodySections(file, bodySegments, fileIndex, 0, partial, 0, fileTailMatch != 0) == true;
    }

    /// <summary>
    /// JS <c>#matchGlobStarBodySections</c>. Returns <c>null</c> for "not matching, cannot keep trying",
    /// which is upstream's <c>null</c> and distinct from <c>false</c>.
    /// </summary>
    private bool? MatchGlobStarBodySections(
        List<string> file,
        List<BodySegment> bodySegments,
        int fileIndex,
        int bodyIndex,
        bool partial,
        int globStarDepth,
        bool sawTail)
    {
        if (bodyIndex >= bodySegments.Count)
        {
            // just make sure that there's no bad dots
            for (var i = fileIndex; i < file.Count; i++)
            {
                sawTail = true;
                var f = file[i];
                if (f == "." || f == ".." || (!Options.Dot && f.StartsWith('.')))
                {
                    return false;
                }
            }

            return sawTail;
        }

        // Have a non-globstar body section to test: walk from fileIndex to its "after" value.
        var segment = bodySegments[bodyIndex];
        var body = segment.Body;
        var after = segment.After;
        while (fileIndex <= after)
        {
            var m = MatchOneLinear(Slice(file, 0, fileIndex + body.Count), body, partial, fileIndex, 0);

            // If the limit is exceeded there is no match: an intentional false negative, an acceptable
            // break in correctness for security.
            if (m && globStarDepth < MaxGlobstarRecursion)
            {
                var sub = MatchGlobStarBodySections(
                    file, bodySegments, fileIndex + body.Count, bodyIndex + 1, partial, globStarDepth + 1, sawTail);
                if (sub != false) return sub;
            }

            // `fileIndex` cannot run past the end here, because preprocessing always collapses adjacent
            // `**`, so every body section has at least one non-globstar portion. The guard keeps the port
            // total where upstream would read `undefined` and throw on `.startsWith`.
            var f = fileIndex < file.Count ? file[fileIndex] : "";
            if (f == "." || f == ".." || (!Options.Dot && f.StartsWith('.')))
            {
                return false;
            }

            fileIndex++;
        }

        // walked off. no point continuing
        return partial ? true : null;
    }

    /// <summary>JS <c>#matchOne</c>: the linear portion-by-portion walk, without globstar handling.</summary>
    private bool MatchOneLinear(List<string> file, List<MatchPart> pattern, bool partial, int fileIndex, int patternIndex)
    {
        int fi, pi, fl, pl;
        for (fi = fileIndex, pi = patternIndex, fl = file.Count, pl = pattern.Count;
             fi < fl && pi < pl;
             fi++, pi++)
        {
            var p = pattern[pi];
            var f = file[fi];

            // should be impossible: some invalid regexp stuff in the set.
            if (p is MatchGlobstarPart) return false;

            bool hit;
            if (p is MatchLiteralPart literal)
            {
                hit = string.Equals(f, literal.Value, StringComparison.Ordinal);
            }
            else
            {
                var re = (MatchRegexPart)p;
                hit = re.FastTest is not null ? re.FastTest(f) : re.Regex.IsMatch(f);
            }

            if (!hit) return false;
        }

        if (fi == fl && pi == pl)
        {
            // ran out of pattern and filename at the same time. an exact hit!
            return true;
        }

        if (fi == fl)
        {
            // ran out of file, but still had pattern left: fine during a glob fs traversal.
            return partial;
        }

        if (pi == pl)
        {
            // Ran out of pattern with file left. Only acceptable on the very last empty segment of a file
            // with a trailing slash, so that a/* matches a/b/.
            return fi == fl - 1 && file[fi].Length == 0;
        }

        throw new InvalidOperationException("wtf?");
    }

    /// <summary>JS <c>parse(pattern)</c>: compile one path portion.</summary>
    internal MatchPart Parse(string pattern)
    {
        Glob.AssertValidPattern(pattern);

        // shortcuts
        if (pattern == "**") return MatchGlobstarPart.Instance;
        if (pattern.Length == 0) return new MatchLiteralPart("");

        // Far and away the most common glob pattern parts are *, *.*, and *.<ext>, so give those a fast
        // predicate that skips the regex entirely.
        Func<string, bool>? fastTest = null;
        var match = StarRe.Match(pattern);
        if (match.Success)
        {
            fastTest = Options.Dot ? StarTestDot : StarTest;
        }
        else if ((match = StarDotExtRe.Match(pattern)).Success)
        {
            var ext = match.Groups[1].Value;
            fastTest = Options.NoCase
                ? Options.Dot ? StarDotExtTestNoCaseDot(ext) : StarDotExtTestNoCase(ext)
                : Options.Dot ? StarDotExtTestDot(ext) : StarDotExtTest(ext);
        }
        else if ((match = QmarksRe.Match(pattern)).Success)
        {
            var whole = match.Value;
            var ext = match.Groups[1].Success ? match.Groups[1].Value : "";
            fastTest = Options.NoCase
                ? Options.Dot ? QmarksTestNoCaseDot(whole, ext) : QmarksTestNoCase(whole, ext)
                : Options.Dot ? QmarksTestDot(whole, ext) : QmarksTest(whole, ext);
        }
        else if (StarDotStarRe.IsMatch(pattern))
        {
            fastTest = Options.Dot ? StarDotStarTestDot : StarDotStarTest;
        }
        else if (DotStarRe.IsMatch(pattern))
        {
            fastTest = DotStarTest;
        }

        var re = GlobAst.FromGlob(pattern, Options).ToMMPattern();

        // The fast predicate only replaces a real regex; a magic-free portion stays a plain string compare.
        if (fastTest is not null && re is MatchRegexPart regexPart) return regexPart with { FastTest = fastTest };
        return re;
    }

    /// <summary>JS <c>slashSplit(p)</c>: split on <c>/</c> runs, preserving a leading UNC prefix on Windows.</summary>
    private List<string> SlashSplit(string p)
    {
        if (PreserveMultipleSlashes) return [.. p.Split('/')];

        if (IsWindows && Glob.UncPrefix.IsMatch(p))
        {
            // add an extra '' for the one we lose
            var parts = new List<string> { "" };
            parts.AddRange(MultipleSlashes.Split(p));
            return parts;
        }

        return [.. MultipleSlashes.Split(p)];
    }

    private static bool HasMagic(List<string> parts, int index)
        => index < parts.Count && Glob.GlobMagic.IsMatch(parts[index]);

    private static bool IsEmptyPortion(List<string> parts, int index)
        => index < parts.Count && parts[index].Length == 0;

    private static bool IsQuestionPortion(List<string> parts, int index)
        => index < parts.Count && parts[index] == "?";

    /// <summary>JS <c>array.slice(start, end)</c> for the head/body/tail split.</summary>
    private static List<T> Slice<T>(List<T> list, int start, int end)
    {
        var from = Math.Clamp(start, 0, list.Count);
        var to = Math.Clamp(end, 0, list.Count);
        return to <= from ? [] : list.GetRange(from, to - from);
    }

    // ---- the fast predicates, ported from minimatch's module scope ----

    private static bool StarTest(string f) => f.Length != 0 && !f.StartsWith('.');

    private static bool StarTestDot(string f) => f.Length != 0 && f != "." && f != "..";

    private static bool StarDotStarTest(string f)
        => !f.StartsWith('.') && f.Contains('.', StringComparison.Ordinal);

    private static bool StarDotStarTestDot(string f)
        => f != "." && f != ".." && f.Contains('.', StringComparison.Ordinal);

    private static bool DotStarTest(string f)
        => f != "." && f != ".." && f.StartsWith('.');

    private static Func<string, bool> StarDotExtTest(string ext)
        => f => !f.StartsWith('.') && f.EndsWith(ext, StringComparison.Ordinal);

    private static Func<string, bool> StarDotExtTestDot(string ext)
        => f => f.EndsWith(ext, StringComparison.Ordinal);

    private static Func<string, bool> StarDotExtTestNoCase(string ext)
    {
        var lower = JsString.ToLowerCase(ext);
        return f => !f.StartsWith('.')
            && JsString.ToLowerCase(f).EndsWith(lower, StringComparison.Ordinal);
    }

    private static Func<string, bool> StarDotExtTestNoCaseDot(string ext)
    {
        var lower = JsString.ToLowerCase(ext);
        return f => JsString.ToLowerCase(f).EndsWith(lower, StringComparison.Ordinal);
    }

    private static Func<string, bool> QmarksTestNoExt(string whole)
    {
        var len = whole.Length;
        return f => f.Length == len && !f.StartsWith('.');
    }

    private static Func<string, bool> QmarksTestNoExtDot(string whole)
    {
        var len = whole.Length;
        return f => f.Length == len && f != "." && f != "..";
    }

    private static Func<string, bool> QmarksTest(string whole, string ext)
    {
        var noext = QmarksTestNoExt(whole);
        return ext.Length == 0 ? noext : f => noext(f) && f.EndsWith(ext, StringComparison.Ordinal);
    }

    private static Func<string, bool> QmarksTestDot(string whole, string ext)
    {
        var noext = QmarksTestNoExtDot(whole);
        return ext.Length == 0 ? noext : f => noext(f) && f.EndsWith(ext, StringComparison.Ordinal);
    }

    private static Func<string, bool> QmarksTestNoCase(string whole, string ext)
    {
        var noext = QmarksTestNoExt(whole);
        if (ext.Length == 0) return noext;
        var lower = JsString.ToLowerCase(ext);
        return f => noext(f) && JsString.ToLowerCase(f).EndsWith(lower, StringComparison.Ordinal);
    }

    private static Func<string, bool> QmarksTestNoCaseDot(string whole, string ext)
    {
        var noext = QmarksTestNoExtDot(whole);
        if (ext.Length == 0) return noext;
        var lower = JsString.ToLowerCase(ext);
        return f => noext(f) && JsString.ToLowerCase(f).EndsWith(lower, StringComparison.Ordinal);
    }

    /// <summary>One <c>**</c>-delimited section of a globstar body, plus the last index it may start at.</summary>
    private sealed class BodySegment
    {
        public List<MatchPart> Body { get; } = [];

        public int After { get; set; }
    }

    /// <summary>A <c>makeRe</c> element: a regex source, or the <c>**</c> marker. Replaces JS's string/array union.</summary>
    private readonly record struct Piece(string Source, bool IsGlobStar)
    {
        public static Piece GlobStar { get; } = new("", true);

        public static Piece Of(string source) => new(source, false);
    }
}
