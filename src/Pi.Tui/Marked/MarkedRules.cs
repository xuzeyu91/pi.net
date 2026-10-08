using System.Text.RegularExpressions;

namespace Pi.Tui.Marked;

// Ported from marked v18.0.5 (packages/coding-agent/src/core/export-html/vendor/marked.min.js).
// Regex sources and flags were captured from the live module, so they are byte-exact; only the JS
// flags are mapped (g -> a Match loop, u -> implicit in .NET). Only the gfm rule sets are emitted:
// marked builds them as `{ ...normal, <gfm overrides> }`, so each one is already self-contained, and
// components/markdown.ts runs marked with the default options (gfm: true, breaks: false).

/// <summary>Block-level rules (marked `rules.block.gfm`).</summary>
public sealed class MarkedBlockRulesGfm
{
    public Regex Blockquote { get; } = new(@"^( {0,3}> ?(([^\n]+(?:\n(?! {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]+[^ \t\n]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)| +\n)[^\n]+)*)|[^\n]*)(?:\n|$))+", RegexOptions.Compiled);

    public Regex Code { get; } = new(@"^((?: {4}| {0,3}\t)[^\n]+(?:\n(?:[ \t]*(?:\n|$))*)?)+", RegexOptions.Compiled);

    public Regex Def { get; } = new(@"^ {0,3}\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]: *(?:\n[ \t]*)?([^<\s][^\s]*|<.*?>)(?:(?: +(?:\n[ \t]*)?| *\n[ \t]*)((?:""(?:\\""?|[^""\\])*""|'[^'\n]*(?:\n[^'\n]+)*\n?'|\([^()]*\))))? *(?:\n+|$)", RegexOptions.Compiled);

    public Regex Fences { get; } = new(@"^ {0,3}(`{3,}(?=[^`\n]*(?:\n|$))|~{3,})([^\n]*)(?:\n|$)(?:|([\s\S]*?)(?:\n|$))(?: {0,3}\1[~`]* *(?=\n|$)|$)", RegexOptions.Compiled);

    public Regex Heading { get; } = new(@"^ {0,3}(#{1,6})(?=\s|$)(.*)(?:\n+|$)", RegexOptions.Compiled);

    public Regex Hr { get; } = new(@"^ {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)", RegexOptions.Compiled);

    public Regex Html { get; } = new(@"^ {0,3}(?:<(script|pre|style|textarea)[\s>][\s\S]*?(?:<\/\1>[^\n]*\n+|$)|<!--(?:-?>|[\s\S]*?(?:-->|$))[^\n]*(\n+|$)|<\?[\s\S]*?(?:\?>\n*|$)|<![A-Z][\s\S]*?(?:>\n*|$)|<!\[CDATA\[[\s\S]*?(?:\]\]>\n*|$)|<\/?(address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)[\s\S]*?(?:(?:\n[ 	]*)+\n|$)|<(?!script|pre|style|textarea)([a-z][\w-]*)(?: +[a-zA-Z:_][\w.:-]*(?: *= *""[^""\n]*""| *= *'[^'\n]*'| *= *[^\s""'=<>`]+)?)*? *\/?>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ 	]*)+\n|$)|<\/(?!script|pre|style|textarea)[a-z][\w-]*\s*>(?=[ \t]*(?:\n|$))[\s\S]*?(?:(?:\n[ 	]*)+\n|$))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Regex Lheading { get; } = new(@"^(?! {0,3}(?:[*+-]|\d{1,9}[.)]) |(?: {4}| {0,3}\t)| {0,3}(?:`{3,}|~{3,})| {0,3}>| {0,3}#{1,6}| {0,3}<[^\n>]+>\n| {0,3}\|?(?:[:\- ]*\|)+[\:\- ]*\n)((?:.|\n(?!\s*?\n| {0,3}(?:[*+-]|\d{1,9}[.)]) |(?: {4}| {0,3}\t)| {0,3}(?:`{3,}|~{3,})| {0,3}>| {0,3}#{1,6}| {0,3}<[^\n>]+>\n| {0,3}\|?(?:[:\- ]*\|)+[\:\- ]*\n))+?)\n {0,3}(=+|-+) *(?:\n+|$)", RegexOptions.Compiled);

    public Regex List { get; } = new(@"^( {0,3}(?:[*+-]|\d{1,9}[.)]))([ \t][^\n]*?)?(?:\n|$)", RegexOptions.Compiled);

    public Regex Newline { get; } = new(@"^(?:[ \t]*(?:\n|$))+", RegexOptions.Compiled);

    public Regex Paragraph { get; } = new(@"^([^\n]+(?:\n(?! {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]+[^ \t\n]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)| *([^\n ].*)\n {0,3}((?:\| *)?:?-+:? *(?:\| *:?-+:? *)*(?:\| *)?)(?:\n((?:(?! *\n| {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>|(?: {4}| {0,3}	)[^\n]| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)).*(?:\n|$))*)\n*|$)| +\n)[^\n]+)*)", RegexOptions.Compiled);

    public Regex Table { get; } = new(@"^ *([^\n ].*)\n {0,3}((?:\| *)?:?-+:? *(?:\| *:?-+:? *)*(?:\| *)?)(?:\n((?:(?! *\n| {0,3}((?:-[\t ]*){3,}|(?:_[ \t]*){3,}|(?:\*[ \t]*){3,})(?:\n+|$)| {0,3}#{1,6}(?:\s|$)| {0,3}>|(?: {4}| {0,3}	)[^\n]| {0,3}(?:`{3,}(?=[^`\n]*\n)|~{3,})[^\n]*\n| {0,3}(?:[*+-]|1[.)])[ \t]|<\/?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|meta|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?: +|\n|\/?>)|<(?:script|pre|style|textarea|!--)).*(?:\n|$))*)\n*|$)", RegexOptions.Compiled);

    public Regex Text { get; } = new(@"^[^\n]+", RegexOptions.Compiled);
}

/// <summary>Inline rules (marked `rules.inline.gfm`).</summary>
public sealed class MarkedInlineRulesGfm
{
    public Regex Backpedal { get; } = new(@"(?:[^?!.,:;*_'""~()&]+|\([^)]*\)|&(?![a-zA-Z0-9]+;$)|[?!.,:;*_'""~)]+(?!$))+", RegexOptions.Compiled);

    public Regex AnyPunctuation { get; } = new(@"\\([\p{P}\p{S}])", RegexOptions.Compiled);

    public Regex Autolink { get; } = new(@"^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s\x00-\x1f<>]*|[a-zA-Z0-9.!#$%&'*+/=?_`{|}~-]+(@)[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+(?![-_]))>", RegexOptions.Compiled);

    public Regex BlockSkip { get; } = new(@"\[(?:[^\[\]`]|(?<a>`+)[^`]+\k<a>(?!`))*?\]\((?:\\[\s\S]|[^\\\(\)]|\((?:\\[\s\S]|[^\\\(\)])*\))*\)|(?<!`)()(?<b>`+)[^`]+\k<b>(?!`)|<(?! )[^<>]*?>", RegexOptions.Compiled);

    public Regex Br { get; } = new(@"^( {2,}|\\)\n(?!\s*$)", RegexOptions.Compiled);

    public Regex Code { get; } = new(@"^(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)", RegexOptions.Compiled);

    public Regex Del { get; } = new(@"^(~~?)(?=[^\s~])((?:\\[\s\S]|[^\\])*?(?:\\[\s\S]|[^\s~\\]))\1(?=[^~]|$)", RegexOptions.Compiled);

    public Regex DelLDelim { get; } = new(@"^~~?(?:((?!~)[\p{P}\p{S}])|[^\s~])", RegexOptions.Compiled);

    public Regex DelRDelim { get; } = new(@"^[^~]+(?=[^~])|(?!~)[\p{P}\p{S}](~~?)(?=[\s]|$)|[^\s\p{P}\p{S}](~~?)(?!~)(?=[\s\p{P}\p{S}]|$)|(?!~)[\s\p{P}\p{S}](~~?)(?=[^\s\p{P}\p{S}])|[\s](~~?)(?!~)(?=[\p{P}\p{S}])|(?!~)[\p{P}\p{S}](~~?)(?!~)(?=[\p{P}\p{S}])|[^\s\p{P}\p{S}](~~?)(?=[^\s\p{P}\p{S}])", RegexOptions.Compiled);

    public Regex EmStrongLDelim { get; } = new(@"^(?:\*+(?:((?!\*)(?!~)[\p{P}\p{S}])|([^\s*]))?)|^_+(?:((?!_)(?!~)[\p{P}\p{S}])|([^\s_]))?", RegexOptions.Compiled);

    public Regex EmStrongRDelimAst { get; } = new(@"^[^_*]*?__[^_*]*?\*[^_*]*?(?=__)|[^*]+(?=[^*])|(?!\*)(?!~)[\p{P}\p{S}](\*+)(?=[\s]|$)|(?:[^\s\p{P}\p{S}]|~)(\*+)(?!\*)(?=(?!~)[\s\p{P}\p{S}]|$)|(?!\*)(?!~)[\s\p{P}\p{S}](\*+)(?=(?:[^\s\p{P}\p{S}]|~))|[\s](\*+)(?!\*)(?=(?!~)[\p{P}\p{S}])|(?!\*)(?!~)[\p{P}\p{S}](\*+)(?!\*)(?=(?!~)[\p{P}\p{S}])|(?:[^\s\p{P}\p{S}]|~)(\*+)(?=(?:[^\s\p{P}\p{S}]|~))", RegexOptions.Compiled);

    public Regex EmStrongRDelimUnd { get; } = new(@"^[^_*]*?\*\*[^_*]*?_[^_*]*?(?=\*\*)|[^_]+(?=[^_])|(?!_)[\p{P}\p{S}](_+)(?=[\s]|$)|[^\s\p{P}\p{S}](_+)(?!_)(?=[\s\p{P}\p{S}]|$)|(?!_)[\s\p{P}\p{S}](_+)(?=[^\s\p{P}\p{S}])|[\s](_+)(?!_)(?=[\p{P}\p{S}])|(?!_)[\p{P}\p{S}](_+)(?!_)(?=[\p{P}\p{S}])", RegexOptions.Compiled);

    public Regex Escape { get; } = new(@"^\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", RegexOptions.Compiled);

    public Regex Link { get; } = new(@"^!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\(\s*(<(?:\\.|[^\n<>\\])+>|[^ \t\n\x00-\x1f]*)(?:(?:[ \t]+(?:\n[ \t]*)?|\n[ \t]*)(""(?:\\""?|[^""\\])*""|'(?:\\'?|[^'\\])*'|\((?:\\\)?|[^)\\])*\)))?\s*\)", RegexOptions.Compiled);

    public Regex Nolink { get; } = new(@"^!?\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\](?:\[\])?", RegexOptions.Compiled);

    public Regex Punctuation { get; } = new(@"^((?![*_])[\s\p{P}\p{S}])", RegexOptions.Compiled);

    public Regex Reflink { get; } = new(@"^!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]", RegexOptions.Compiled);

    public Regex ReflinkSearch { get; } = new(@"!?\[((?:\[(?:\\[\s\S]|[^\[\]\\])*\]|\\[\s\S]|`+(?!`)[^`]*?`+(?!`)|``+(?=\])|[^\[\]\\`])*?)\]\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\]|!?\[((?!\s*\])(?:\\[\s\S]|[^\[\]\\])+)\](?:\[\])?(?!\()", RegexOptions.Compiled);

    public Regex Tag { get; } = new(@"^<!--(?:-?>|[\s\S]*?-->)|^<\/[a-zA-Z][\w:-]*\s*>|^<[a-zA-Z][\w-]*(?:\s+[a-zA-Z:_][\w.:-]*(?:\s*=\s*""[^""]*""|\s*=\s*'[^']*'|\s*=\s*[^\s""'=<>`]+)?)*?\s*\/?>|^<\?[\s\S]*?\?>|^<![a-zA-Z]+\s[\s\S]*?>|^<!\[CDATA\[[\s\S]*?\]\]>", RegexOptions.Compiled);

    public Regex Text { get; } = new(@"^([`~]+|[^`~])(?:(?= {2,}\n)|(?=[a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-]+@)|[\s\S]*?(?:(?=[\\<!\[`*~_]|\b_|[hH][tT][tT][pP][sS]?|[fF][tT][pP]:\/\/|www\.|$)|[^ ](?= {2,}\n)|[^a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-](?=[a-zA-Z0-9.!#$%&'*+\/=?_`{\|}~-]+@)))", RegexOptions.Compiled);

    public Regex Url { get; } = new(@"^((?:[hH][tT][tT][pP][sS]?|[fF][tT][pP]):\/\/|www\.)(?:[a-zA-Z0-9\-]+\.?)+[^\s<]*|^[A-Za-z0-9._+-]+(@)[a-zA-Z0-9-_]+(?:\.[a-zA-Z0-9-_]*[a-zA-Z0-9])+(?![-_])", RegexOptions.Compiled);
}

/// <summary>Shared non-level rules (marked `rules.other`).</summary>
public sealed class MarkedOtherRules
{
    public Regex CodeRemoveIndent { get; } = new(@"^(?: {1,4}| {0,3}\t)", RegexOptions.Compiled | RegexOptions.Multiline);

    public Regex OutputLinkReplace { get; } = new(@"\\([\[\]])", RegexOptions.Compiled);

    public Regex IndentCodeCompensation { get; } = new(@"^(\s+)(?:```)", RegexOptions.Compiled);

    public Regex BeginningSpace { get; } = new(@"^\s+", RegexOptions.Compiled);

    public Regex EndingHash { get; } = new(@"#$", RegexOptions.Compiled);

    public Regex StartingSpaceChar { get; } = new(@"^ ", RegexOptions.Compiled);

    public Regex EndingSpaceChar { get; } = new(@" $", RegexOptions.Compiled);

    public Regex NonSpaceChar { get; } = new(@"[^ ]", RegexOptions.Compiled);

    public Regex NewLineCharGlobal { get; } = new(@"\n", RegexOptions.Compiled);

    public Regex TabCharGlobal { get; } = new(@"\t", RegexOptions.Compiled);

    public Regex MultipleSpaceGlobal { get; } = new(@"\s+", RegexOptions.Compiled);

    public Regex BlankLine { get; } = new(@"^[ \t]*$", RegexOptions.Compiled);

    public Regex DoubleBlankLine { get; } = new(@"\n[ \t]*\n[ \t]*$", RegexOptions.Compiled);

    public Regex BlockquoteStart { get; } = new(@"^ {0,3}>", RegexOptions.Compiled);

    public Regex BlockquoteSetextReplace { get; } = new(@"\n {0,3}((?:=+|-+) *)(?=\n|$)", RegexOptions.Compiled);

    public Regex BlockquoteSetextReplace2 { get; } = new(@"^ {0,3}>[ \t]?", RegexOptions.Compiled | RegexOptions.Multiline);

    public Regex ListReplaceNesting { get; } = new(@"^ {1,4}(?=( {4})*[^ ])", RegexOptions.Compiled);

    public Regex ListIsTask { get; } = new(@"^\[[ xX]\] +\S", RegexOptions.Compiled);

    public Regex ListReplaceTask { get; } = new(@"^\[[ xX]\] +", RegexOptions.Compiled);

    public Regex ListTaskCheckbox { get; } = new(@"\[[ xX]\]", RegexOptions.Compiled);

    public Regex AnyLine { get; } = new(@"\n.*\n", RegexOptions.Compiled);

    public Regex HrefBrackets { get; } = new(@"^<(.*)>$", RegexOptions.Compiled);

    public Regex TableDelimiter { get; } = new(@"[:|]", RegexOptions.Compiled);

    public Regex TableAlignChars { get; } = new(@"^\||\| *$", RegexOptions.Compiled);

    public Regex TableRowBlankLine { get; } = new(@"\n[ \t]*$", RegexOptions.Compiled);

    public Regex TableAlignRight { get; } = new(@"^ *-+: *$", RegexOptions.Compiled);

    public Regex TableAlignCenter { get; } = new(@"^ *:-+: *$", RegexOptions.Compiled);

    public Regex TableAlignLeft { get; } = new(@"^ *:-+ *$", RegexOptions.Compiled);

    public Regex StartATag { get; } = new(@"^<a ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Regex EndATag { get; } = new(@"^<\/a>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Regex StartPreScriptTag { get; } = new(@"^<(pre|code|kbd|script)(\s|>)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Regex EndPreScriptTag { get; } = new(@"^<\/(pre|code|kbd|script)(\s|>)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Regex StartAngleBracket { get; } = new(@"^<", RegexOptions.Compiled);

    public Regex EndAngleBracket { get; } = new(@">$", RegexOptions.Compiled);

    public Regex PedanticHrefTitle { get; } = new(@"^([^'""]*[^\s])\s+(['""])(.*)\2", RegexOptions.Compiled);

    public Regex UnicodeAlphaNumeric { get; } = new(@"[\p{L}\p{N}]", RegexOptions.Compiled);

    public Regex EscapeTest { get; } = new(@"[&<>""']", RegexOptions.Compiled);

    public Regex EscapeReplace { get; } = new(@"[&<>""']", RegexOptions.Compiled);

    public Regex EscapeTestNoEncode { get; } = new(@"[<>""']|&(?!(#\d{1,7}|#[Xx][a-fA-F0-9]{1,6}|\w+);)", RegexOptions.Compiled);

    public Regex EscapeReplaceNoEncode { get; } = new(@"[<>""']|&(?!(#\d{1,7}|#[Xx][a-fA-F0-9]{1,6}|\w+);)", RegexOptions.Compiled);

    public Regex Caret { get; } = new(@"(^|[^\[])\^", RegexOptions.Compiled);

    public Regex PercentDecode { get; } = new(@"%25", RegexOptions.Compiled);

    public Regex FindPipe { get; } = new(@"\|", RegexOptions.Compiled);

    public Regex SplitPipe { get; } = new(@" \|", RegexOptions.Compiled);

    public Regex SlashPipe { get; } = new(@"\\\|", RegexOptions.Compiled);

    public Regex CarriageReturn { get; } = new(@"\r\n|\r", RegexOptions.Compiled);

    public Regex SpaceLine { get; } = new(@"^ +$", RegexOptions.Compiled | RegexOptions.Multiline);

    public Regex NotSpaceStart { get; } = new(@"^\S*", RegexOptions.Compiled);

    public Regex EndingNewline { get; } = new(@"\n$", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Regex factories. marked memoizes these behind `E()`, which caches by
    // Math.max(0, Math.min(3, indent - 1)) - so every indent above 4 shares one entry.
    // ------------------------------------------------------------------

    /// <summary>Port of marked's <c>listItemRegex</c> (the only factory that is not memoized).</summary>
    public Regex ListItemRegex(string bullet) => new($"^( {{0,3}}{bullet})((?:[\t ][^\n]*)?(?:\n|$))");

    private readonly Regex?[] _nextBulletCache = new Regex?[4];
    private readonly Regex?[] _hrCache = new Regex?[4];
    private readonly Regex?[] _fencesBeginCache = new Regex?[4];
    private readonly Regex?[] _headingBeginCache = new Regex?[4];
    private readonly Regex?[] _htmlBeginCache = new Regex?[4];
    private readonly Regex?[] _blockquoteBeginCache = new Regex?[4];

    private static int MemoSlot(int indent) => Math.Max(0, Math.Min(3, indent - 1));

    public Regex NextBulletRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _nextBulletCache[slot] ??= new Regex($"^ {{0,{slot}}}(?:[*+-]|\\d{{1,9}}[.)])((?:[ \t][^\n]*)?(?:\n|$))");
    }

    public Regex HrRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _hrCache[slot] ??= new Regex($"^ {{0,{slot}}}((?:- *){{3,}}|(?:_ *){{3,}}|(?:\\* *){{3,}})(?:\\n+|$)");
    }

    public Regex FencesBeginRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _fencesBeginCache[slot] ??= new Regex($"^ {{0,{slot}}}(?:```|~~~)");
    }

    public Regex HeadingBeginRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _headingBeginCache[slot] ??= new Regex($"^ {{0,{slot}}}#");
    }

    public Regex HtmlBeginRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _htmlBeginCache[slot] ??= new Regex($"^ {{0,{slot}}}<(?:[a-z].*>|!--)", RegexOptions.IgnoreCase);
    }

    public Regex BlockquoteBeginRegex(int indent)
    {
        var slot = MemoSlot(indent);
        return _blockquoteBeginCache[slot] ??= new Regex($"^ {{0,{slot}}}>");
    }
}

/// <summary>The three rule sets a tokenizer uses (port of marked's <c>rules</c>).</summary>
public sealed class MarkedRules
{
    public required MarkedOtherRules Other { get; init; }

    public required MarkedBlockRulesGfm Block { get; init; }

    public required MarkedInlineRulesGfm Inline { get; init; }

    /// <summary>The rule set marked selects for the default options used by <c>components/markdown.ts</c>.</summary>
    public static MarkedRules CreateDefault() => new()
    {
        Other = new MarkedOtherRules(),
        Block = new MarkedBlockRulesGfm(),
        Inline = new MarkedInlineRulesGfm(),
    };

    /// <summary>
    /// Shared default instance. <see cref="System.Text.RegularExpressions.Regex"/> matching is thread-safe,
    /// so one instance can back every tokenizer, which also keeps marked's memoized factories warm.
    /// </summary>
    public static MarkedRules Default { get; } = CreateDefault();
}
