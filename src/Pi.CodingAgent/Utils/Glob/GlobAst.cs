using System.Text;
using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// One compiled piece of a glob path portion: the JS union <c>string | RegExp | GLOBSTAR</c>.
/// </summary>
/// <remarks>
/// Public because it is minimatch's <c>set</c> element type and <c>GLOBSTAR</c> is a documented export;
/// the parser that produces it (<see cref="GlobAst"/>) stays internal.
/// </remarks>
public abstract record MatchPart;

/// <summary>A segment with no magic at all — matches by string equality. Replaces the JS bare string.</summary>
public sealed record MatchLiteralPart(string Value) : MatchPart;

/// <summary>
/// A compiled segment. <see cref="Src"/> is the regex source makeRe splices together; <see cref="Glob"/>
/// is the reconstructed glob text the parser settled on (after <c>flatten</c> and <c>fillNegs</c>);
/// <see cref="FastTest"/> replaces the regex for the handful of shapes minimatch special-cases.
/// </summary>
public sealed record MatchRegexPart(Regex Regex, string Src, string Glob, Func<string, bool>? FastTest = null)
    : MatchPart;

/// <summary>The <c>**</c> sentinel. A singleton so it can be compared by reference like the JS <c>Symbol</c>.</summary>
public sealed record MatchGlobstarPart : MatchPart
{
    public static readonly MatchGlobstarPart Instance = new();
}

/// <summary>
/// Port of minimatch's <c>ast.js</c>: parse a glob path portion into a tree and compile it to a
/// regular-expression source.
/// </summary>
/// <remarks>
/// <para>
/// The tree has two node kinds. A node with <see cref="Type"/> <c>null</c> is a plain sequence of literal
/// segments and child nodes; a node with a type from <c>! ? + * @</c> is an extglob whose children are the
/// alternatives. Children are stored as <see cref="object"/> holding either a <see cref="string"/> or a
/// <see cref="GlobAst"/>, exactly like the JS <c>string | AST</c> union.
/// </para>
/// <para>
/// The parser also carries minimatch's two tree-rewriting passes: <c>flatten</c> (extglobs adopting or
/// usurping a single nested extglob child, up to ten passes) and <c>fillNegs</c> (copying the tail of a
/// negated extglob into every following sibling), which is what makes <c>!(a)b</c> behave like
/// <c>!(a|ab)</c>. Both are ported verbatim.
/// </para>
/// <para>
/// Differences from the JS original: the debug-only <c>toJSON</c> / Node inspect hooks are omitted, and
/// <c>maxExtglobRecursion</c> defaults to 2 exactly as upstream.
/// </para>
/// </remarks>
internal sealed class GlobAst
{
    private static readonly HashSet<string> ExtglobTypes = ["!", "?", "+", "*", "@"];

    private static readonly Dictionary<string, string[]> AdoptionMap = new(StringComparer.Ordinal)
    {
        ["!"] = ["@"],
        ["?"] = ["?", "@"],
        ["@"] = ["@"],
        ["*"] = ["*", "+", "?", "@"],
        ["+"] = ["+", "@"],
    };

    private static readonly Dictionary<string, string[]> AdoptionWithSpaceMap = new(StringComparer.Ordinal)
    {
        ["!"] = ["?"],
        ["@"] = ["?"],
        ["+"] = ["?", "*"],
    };

    private static readonly Dictionary<string, string[]> AdoptionAnyMap = new(StringComparer.Ordinal)
    {
        ["!"] = ["?", "@"],
        ["?"] = ["?", "@"],
        ["@"] = ["?", "@"],
        ["*"] = ["*", "+", "?", "@"],
        ["+"] = ["+", "@", "?", "*"],
    };

    private static readonly Dictionary<string, Dictionary<string, string>> UsurpMap = new(StringComparer.Ordinal)
    {
        ["!"] = new(StringComparer.Ordinal) { ["!"] = "@" },
        ["?"] = new(StringComparer.Ordinal) { ["*"] = "*", ["+"] = "*" },
        ["@"] = new(StringComparer.Ordinal) { ["!"] = "!", ["?"] = "?", ["@"] = "@", ["*"] = "*", ["+"] = "+" },
        ["+"] = new(StringComparer.Ordinal) { ["?"] = "*", ["*"] = "*" },
    };

    private const string StartNoTraversal = @"(?!(?:^|/)\.\.?(?:$|/))";

    private const string StartNoDot = @"(?!\.)";

    private static readonly HashSet<string> AddPatternStart = ["[", "."];

    private static readonly HashSet<string> JustDots = ["..", "."];

    private static readonly HashSet<string> ReSpecials = ["(", ")", ".", "*", "{", "}", "+", "?", "[", "]", "^", "$", "\\", "!"];

    internal const string QMark = "[^/]";

    internal const string Star = "[^/]*?";

    internal const string StarNoEmpty = "[^/]+?";

    private static readonly Regex AllStars = new(@"^[*]+$", RegexOptions.CultureInvariant);

    private GlobAst? _parent;
    private readonly GlobAst _root;
    private readonly List<GlobAst> _negs;
    private readonly int _parentIndex;
    private readonly GlobOptions _options;

    private bool? _hasMagic;
    private bool _uflag;
    private List<object> _parts = [];
    private bool _filledNegs;
    private string? _toString;
    private bool _emptyExt;

    public GlobAst(string? type, GlobAst? parent, GlobOptions? options = null)
    {
        Type = type;
        // Extglobs are inherently magic.
        if (type is not null) _hasMagic = true;
        _parent = parent;
        _root = parent?._root ?? this;
        _options = ReferenceEquals(_root, this) ? options ?? new GlobOptions() : _root._options;
        _negs = ReferenceEquals(_root, this) ? [] : _root._negs;
        if (type == "!" && !_root._filledNegs) _negs.Add(this);
        _parentIndex = parent?._parts.Count ?? 0;
    }

    public string? Type { get; set; }

    public int Depth => (_parent?.Depth ?? -1) + 1;

    public bool HasMagic
    {
        get
        {
            if (_hasMagic is not null) return _hasMagic.Value;
            foreach (var part in _parts)
            {
                if (part is not GlobAst ast) continue;
                if (ast.Type is not null || ast.HasMagic) return (_hasMagic = true).Value;
            }

            return false;
        }
    }

    public override string ToString()
    {
        if (_toString is not null) return _toString;
        _toString = Type is null
            ? string.Concat(_parts.Select(PartText))
            : Type + "(" + string.Join("|", _parts.Select(PartText)) + ")";
        return _toString;
    }

    private static string PartText(object part) => part is string s ? s : part.ToString() ?? "";

    private static bool IsExtglobType(string? c) => c is not null && ExtglobTypes.Contains(c);

    private static bool IsExtglobAst(GlobAst ast) => IsExtglobType(ast.Type);

    /// <summary>JS <c>#fillNegs</c>: copy the tail of every <c>!</c> extglob into its following siblings.</summary>
    public GlobAst FillNegs()
    {
        if (!ReferenceEquals(this, _root)) throw new InvalidOperationException("should only call on root");
        if (_filledNegs) return this;

        // Calling ToString() once fills the cached text the walk below relies on.
        ToString();
        _filledNegs = true;
        while (_negs.Count > 0)
        {
            var n = _negs[^1];
            _negs.RemoveAt(_negs.Count - 1);
            if (n.Type != "!") continue;

            var p = n;
            var pp = p._parent;
            while (pp is not null)
            {
                for (var i = p._parentIndex + 1; pp.Type is null && i < pp._parts.Count; i++)
                {
                    foreach (var part in n._parts)
                    {
                        if (part is not GlobAst partAst)
                        {
                            throw new InvalidOperationException("string part in extglob AST??");
                        }

                        partAst.CopyIn(pp._parts[i]);
                    }
                }

                p = pp;
                pp = p._parent;
            }
        }

        return this;
    }

    public void Push(params object[] parts)
    {
        foreach (var part in parts)
        {
            if (part is string s)
            {
                if (s.Length == 0) continue;
            }
            else if (part is not GlobAst ast || !ReferenceEquals(ast._parent, this))
            {
                throw new InvalidOperationException("invalid part: " + part);
            }

            _parts.Add(part);
        }
    }

    public bool IsStart()
    {
        if (ReferenceEquals(_root, this)) return true;
        if (_parent is null || !_parent.IsStart()) return false;
        if (_parentIndex == 0) return true;

        // If everything ahead of this is a negation, it is still the "start".
        var parent = _parent;
        for (var i = 0; i < _parentIndex; i++)
        {
            if (parent._parts[i] is not GlobAst pp || pp.Type != "!") return false;
        }

        return true;
    }

    public bool IsEnd()
    {
        if (ReferenceEquals(_root, this)) return true;
        if (_parent?.Type == "!") return true;
        if (_parent is null || !_parent.IsEnd()) return false;
        if (Type is null) return _parent.IsEnd();
        return _parentIndex == _parent._parts.Count - 1;
    }

    public void CopyIn(object part)
    {
        if (part is string s) Push(s);
        else Push(((GlobAst)part).Clone(this));
    }

    public GlobAst Clone(GlobAst parent)
    {
        var clone = new GlobAst(Type, parent);
        foreach (var part in _parts) clone.CopyIn(part);
        return clone;
    }

    /// <summary>JS <c>AST.fromGlob</c>.</summary>
    public static GlobAst FromGlob(string pattern, GlobOptions? options = null)
    {
        var opt = options ?? new GlobOptions();
        var ast = new GlobAst(null, null, opt);
        ParseAst(pattern, ast, 0, opt, 0);
        return ast;
    }

    /// <summary>JS <c>#parseAST</c>.</summary>
    private static int ParseAst(string str, GlobAst ast, int pos, GlobOptions opt, int extDepth)
    {
        var maxDepth = opt.MaxExtglobRecursion ?? 2;
        var escaping = false;
        var inBrace = false;
        var braceStart = -1;
        var braceNeg = false;

        if (ast.Type is null)
        {
            // Outside an extglob: append until a start is found.
            var i = pos;
            var acc = new StringBuilder();
            while (i < str.Length)
            {
                var c = JsString.Slice(str, i, i + 1);
                i++;
                // Escapes still accumulate here, but escaped starts are ignored.
                if (escaping || c == "\\")
                {
                    escaping = !escaping;
                    acc.Append(c);
                    continue;
                }

                if (inBrace)
                {
                    if (i == braceStart + 1)
                    {
                        if (c == "^" || c == "!") braceNeg = true;
                    }
                    else if (c == "]" && !(i == braceStart + 2 && braceNeg))
                    {
                        inBrace = false;
                    }

                    acc.Append(c);
                    continue;
                }

                if (c == "[")
                {
                    inBrace = true;
                    braceStart = i;
                    braceNeg = false;
                    acc.Append(c);
                    continue;
                }

                // Adoption is not checked here; that happens at the other recursion point.
                var doRecurse = !opt.NoExt && IsExtglobType(c)
                    && JsString.CharAt(str, i) == '(' && extDepth <= maxDepth;
                if (doRecurse)
                {
                    ast.Push(acc.ToString());
                    acc.Clear();
                    var ext = new GlobAst(c, ast);
                    i = ParseAst(str, ext, i, opt, extDepth + 1);
                    ast.Push(ext);
                    continue;
                }

                acc.Append(c);
            }

            ast.Push(acc.ToString());
            return i;
        }

        // Inside an extglob: pos sits at the '('.
        var j = pos + 1;
        var part = new GlobAst(null, ast);
        var parts = new List<GlobAst>();
        var body = new StringBuilder();
        while (j < str.Length)
        {
            var c = JsString.Slice(str, j, j + 1);
            j++;
            if (escaping || c == "\\")
            {
                escaping = !escaping;
                body.Append(c);
                continue;
            }

            if (inBrace)
            {
                if (j == braceStart + 1)
                {
                    if (c == "^" || c == "!") braceNeg = true;
                }
                else if (c == "]" && !(j == braceStart + 2 && braceNeg))
                {
                    inBrace = false;
                }

                body.Append(c);
                continue;
            }

            if (c == "[")
            {
                inBrace = true;
                braceStart = j;
                braceNeg = false;
                body.Append(c);
                continue;
            }

            var canAdopt = ast.CanAdoptType(c);
            var doRecurse = !opt.NoExt && IsExtglobType(c)
                && JsString.CharAt(str, j) == '(' && (extDepth <= maxDepth || canAdopt);
            if (doRecurse)
            {
                var depthAdd = canAdopt ? 0 : 1;
                part.Push(body.ToString());
                body.Clear();
                var ext = new GlobAst(c, part);
                part.Push(ext);
                j = ParseAst(str, ext, j, opt, extDepth + depthAdd);
                continue;
            }

            if (c == "|")
            {
                part.Push(body.ToString());
                body.Clear();
                parts.Add(part);
                part = new GlobAst(null, ast);
                continue;
            }

            if (c == ")")
            {
                if (body.Length == 0 && ast.PartsCount == 0) ast._emptyExt = true;
                part.Push(body.ToString());
                body.Clear();
                var toPush = new List<object>();
                foreach (var p in parts) toPush.Add(p);
                toPush.Add(part);
                ast.Push([.. toPush]);
                return j;
            }

            body.Append(c);
        }

        // Unfinished extglob: a malformed extglob is not an extglob after all.
        ast.Type = null;
        ast._hasMagic = null;
        ast._parts = [JsString.Slice(str, pos - 1)];
        return j;
    }

    internal int PartsCount => _parts.Count;

    private bool CanAdoptWithSpace(GlobAst? child) => CanAdopt(child, AdoptionWithSpaceMap);

    private bool CanAdopt(GlobAst? child, Dictionary<string, string[]>? map = null)
    {
        map ??= AdoptionMap;
        if (child is null || child.Type is not null || child._parts.Count != 1 || Type is null) return false;
        if (child._parts[0] is not GlobAst gc || gc.Type is null) return false;
        return CanAdoptType(gc.Type, map);
    }

    private bool CanAdoptType(string c, Dictionary<string, string[]>? map = null)
    {
        map ??= AdoptionAnyMap;
        return Type is not null && map.TryGetValue(Type, out var list) && list.Contains(c);
    }

    private void AdoptWithSpace(GlobAst child, int index)
    {
        var gc = (GlobAst)child._parts[0];
        var blank = new GlobAst(null, gc, _options);
        blank._parts.Add("");
        gc.Push(blank);
        Adopt(child, index);
    }

    private void Adopt(GlobAst child, int index)
    {
        var gc = (GlobAst)child._parts[0];
        _parts.RemoveAt(index);
        _parts.InsertRange(index, gc._parts);
        foreach (var part in gc._parts)
        {
            if (part is GlobAst ast) ast._parent = this;
        }

        _toString = null;
    }

    private bool CanUsurpType(string c) => UsurpMap.TryGetValue(Type ?? "", out var m) && m.ContainsKey(c);

    private bool CanUsurp(GlobAst? child)
    {
        if (child is null || child.Type is not null || child._parts.Count != 1 || Type is null) return false;
        if (_parts.Count != 1) return false;
        if (child._parts[0] is not GlobAst gc || gc.Type is null) return false;
        return CanUsurpType(gc.Type);
    }

    private void Usurp(GlobAst child)
    {
        if (!UsurpMap.TryGetValue(Type ?? "", out var m)) return;
        var gc = (GlobAst)child._parts[0];
        if (!m.TryGetValue(gc.Type ?? "", out var nt)) return;
        _parts = gc._parts;
        foreach (var part in _parts)
        {
            if (part is GlobAst ast) ast._parent = this;
        }

        Type = nt;
        _toString = null;
        _emptyExt = false;
    }

    /// <summary>JS <c>toMMPattern</c>: the unescaped literal when there is no magic, else a compiled regex.</summary>
    public MatchPart ToMMPattern()
    {
        if (!ReferenceEquals(_root, this)) return _root.ToMMPattern();

        var glob = ToString();
        var src = ToRegExpSource(null);
        var anyMagic = src.HasMagic || (_hasMagic ?? false)
            || (_options.NoCase && !_options.NoCaseMagicOnly
                && !string.Equals(JsString.ToUpperCase(glob), JsString.ToLowerCase(glob), StringComparison.Ordinal));
        if (!anyMagic) return new MatchLiteralPart(src.Body);

        var options = RegexOptions.CultureInvariant;
        if (_options.NoCase) options |= RegexOptions.IgnoreCase;

        // Upstream also sets the `u` flag when a POSIX class asked for it. .NET handles `\p{...}` without a
        // flag, so only the case-insensitivity survives; see GlobClass's remarks.
        return new MatchRegexPart(new Regex("^" + src.Source + "$", options), src.Source, glob);
    }

    /// <summary>JS <c>toRegExpSource</c>: <c>[re, body, hasMagic, uflag]</c>.</summary>
    public RegExpSource ToRegExpSource(bool? allowDot)
    {
        var dot = allowDot ?? _options.Dot;
        if (ReferenceEquals(_root, this))
        {
            Flatten();
            FillNegs();
        }

        if (!IsExtglobAst(this))
        {
            var noEmpty = IsStart() && IsEnd() && _parts.All(part => part is string);
            var src = new StringBuilder();
            foreach (var part in _parts)
            {
                string re;
                bool hasMagic;
                bool uflag;
                if (part is string s)
                {
                    var parsed = ParseGlob(s, _hasMagic == true, noEmpty);
                    re = parsed.Re;
                    hasMagic = parsed.HasMagic;
                    uflag = parsed.UFlag;
                }
                else
                {
                    var nested = ((GlobAst)part).ToRegExpSource(allowDot);
                    re = nested.Source;
                    hasMagic = nested.HasMagic;
                    uflag = nested.UFlag;
                }

                _hasMagic = (_hasMagic == true) || hasMagic;
                _uflag = _uflag || uflag;
                src.Append(re);
            }

            var srcText = src.ToString();
            var start = "";
            if (IsStart() && _parts.Count > 0 && _parts[0] is string first)
            {
                // `'.'` and `'..'` may only match when the pattern is exactly that, even with dot: true.
                var dotTravAllowed = _parts.Count == 1 && JustDots.Contains(first);
                if (!dotTravAllowed)
                {
                    var needNoTrav = (dot && AddPatternStart.Contains(JsString.Slice(srcText, 0, 1)))
                        || (JsString.StartsWith(srcText, "\\.") && AddPatternStart.Contains(JsString.Slice(srcText, 2, 3)))
                        || (JsString.StartsWith(srcText, "\\.\\.") && AddPatternStart.Contains(JsString.Slice(srcText, 4, 5)));
                    var needNoDot = !dot && allowDot != true && AddPatternStart.Contains(JsString.Slice(srcText, 0, 1));
                    start = needNoTrav ? StartNoTraversal : needNoDot ? StartNoDot : "";
                }
            }

            // Append the "end of path portion" pattern to negation tails.
            var end = IsEnd() && _root._filledNegs && _parent?.Type == "!" ? "(?:$|\\/)" : "";
            var final = start + srcText + end;
            _hasMagic = _hasMagic == true;
            return new RegExpSource(final, Glob.Unescape(srcText), _hasMagic.Value, _uflag);
        }

        // An extglob.
        // The body has to be computed twice for a repeat pattern at the start, once in nodot mode and again
        // in dot mode, so a pattern like `*(?)` can match 'x.y'.
        var repeated = Type == "*" || Type == "+";
        var extStart = Type == "!" ? "(?:(?!(?:" : "(?:";
        var bodySrc = PartsToRegExp(dot);
        if (IsStart() && IsEnd() && bodySrc.Length == 0 && Type != "!")
        {
            // Invalid extglob: it has to be *something* when it is the whole path portion.
            var s = ToString();
            _parts = [s];
            Type = null;
            _hasMagic = null;
            return new RegExpSource(s, Glob.Unescape(ToString()), false, false);
        }

        // `!startNoDot` is a constant-false term in the original (startNoDot is a non-empty literal);
        // it is preserved here only so the expression still reads like the source.
        var bodyDotAllowed = !repeated || allowDot == true || dot || !IsStartNoDot ? "" : PartsToRegExp(true);
        if (string.Equals(bodyDotAllowed, bodySrc, StringComparison.Ordinal)) bodyDotAllowed = "";
        if (bodyDotAllowed.Length > 0) bodySrc = "(?:" + bodySrc + ")(?:" + bodyDotAllowed + ")*?";

        string finalSrc;
        if (Type == "!" && _emptyExt)
        {
            // An empty `!()` is exactly equivalent to starNoEmpty.
            finalSrc = (IsStart() && !dot ? StartNoDot : "") + StarNoEmpty;
        }
        else
        {
            var close = Type == "!"
                ? "))" + (IsStart() && !dot && allowDot != true ? StartNoDot : "") + Star + ")"
                : Type == "@" ? ")"
                : Type == "?" ? ")?"
                : Type == "+" && bodyDotAllowed.Length > 0 ? ")"
                : Type == "*" && bodyDotAllowed.Length > 0 ? ")?"
                : ")" + Type;
            finalSrc = extStart + bodySrc + close;
        }

        _hasMagic = _hasMagic == true;
        return new RegExpSource(finalSrc, Glob.Unescape(bodySrc), _hasMagic.Value, _uflag);
    }

    private const bool IsStartNoDot = true;

    private void Flatten()
    {
        if (!IsExtglobAst(this))
        {
            foreach (var part in _parts)
            {
                if (part is GlobAst ast) ast.Flatten();
            }
        }
        else
        {
            // Up to ten passes, to flatten as much as possible.
            var iterations = 0;
            var done = false;
            do
            {
                done = true;
                for (var i = 0; i < _parts.Count; i++)
                {
                    if (_parts[i] is not GlobAst child) continue;
                    child.Flatten();
                    if (CanAdopt(child))
                    {
                        done = false;
                        Adopt(child, i);
                    }
                    else if (CanAdoptWithSpace(child))
                    {
                        done = false;
                        AdoptWithSpace(child, i);
                    }
                    else if (CanUsurp(child))
                    {
                        done = false;
                        Usurp(child);
                    }
                }
            }
            while (!done && ++iterations < 10);
        }

        _toString = null;
    }

    private string PartsToRegExp(bool dot)
    {
        var pieces = new List<string>();
        foreach (var part in _parts)
        {
            if (part is not GlobAst ast) throw new InvalidOperationException("string type in extglob ast??");
            // hasMagic can be ignored here: extglobs are always magic.
            var nested = ast.ToRegExpSource(dot);
            _uflag = _uflag || nested.UFlag;
            pieces.Add(nested.Source);
        }

        var startAndEnd = IsStart() && IsEnd();
        return string.Join("|", pieces.Where(piece => !startAndEnd || piece.Length > 0));
    }

    /// <summary>JS <c>#parseGlob</c>: compile one literal segment of a path portion.</summary>
    private static (string Re, string Body, bool HasMagic, bool UFlag) ParseGlob(
        string glob, bool hasMagic, bool noEmpty = false)
    {
        var escaping = false;
        var re = new StringBuilder();
        var uflag = false;
        // Consecutive stars that are not globstars coalesce into one.
        var inStar = false;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = JsString.Slice(glob, i, i + 1);
            if (escaping)
            {
                escaping = false;
                re.Append(ReSpecials.Contains(c) ? "\\" : "").Append(c);
                continue;
            }

            if (c == "*")
            {
                if (inStar) continue;
                inStar = true;
                re.Append(noEmpty && AllStars.IsMatch(glob) ? StarNoEmpty : Star);
                hasMagic = true;
                continue;
            }

            inStar = false;
            if (c == "\\")
            {
                if (i == glob.Length - 1) re.Append("\\\\");
                else escaping = true;
                continue;
            }

            if (c == "[")
            {
                var parsed = GlobClass.ParseClass(glob, i);
                if (parsed.Consumed != 0)
                {
                    re.Append(parsed.Src);
                    uflag = uflag || parsed.NeedUFlag;
                    i += parsed.Consumed - 1;
                    hasMagic = hasMagic || parsed.Magic;
                    continue;
                }
            }

            if (c == "?")
            {
                re.Append(QMark);
                hasMagic = true;
                continue;
            }

            re.Append(GlobClass.RegExpEscape(c));
        }

        return (re.ToString(), Glob.Unescape(glob), hasMagic, uflag);
    }
}

/// <summary>Result of <see cref="GlobAst.ToRegExpSource"/>: <c>[re, body, hasMagic, uflag]</c>.</summary>
internal readonly record struct RegExpSource(string Source, string Body, bool HasMagic, bool UFlag);
