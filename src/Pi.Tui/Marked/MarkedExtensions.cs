using System.Text.RegularExpressions;

namespace Pi.Tui.Marked;

/// <summary>
/// One marked tokenizer extension (port of marked's <c>TokenizerExtension</c>).
/// <c>components/markdown.ts</c> registers two of them (latexBlock / latex) through
/// <c>markdownParser.use({ extensions: [...] })</c>.
/// </summary>
public sealed class MarkedExtension
{
    public required string Name { get; init; }

    /// <summary>"block" or "inline" (marked rejects anything else).</summary>
    public required string Level { get; init; }

    /// <summary>marked invokes this as <c>tokenizer.call({ lexer: this }, src, tokens)</c>.</summary>
    public Func<MarkedLexer, string, List<MarkedToken>, MarkedToken?>? Tokenizer { get; init; }

    /// <summary>
    /// marked invokes this as <c>start.call({ lexer: this }, src.slice(1))</c> and uses the
    /// returned index to truncate the source before the paragraph / inlineText fallback.
    /// A negative result (or null) means "no start here".
    /// </summary>
    public Func<MarkedLexer, string, int?>? Start { get; init; }
}

/// <summary>
/// The extension arrays marked's <c>use()</c> builds: tokenizers are unshifted (so the last
/// registered runs first) and starts are pushed. <see cref="MarkedLexer"/> dispatches them in
/// exactly that order.
/// </summary>
public sealed class MarkedExtensions
{
    public List<Func<MarkedLexer, string, List<MarkedToken>, MarkedToken?>> Block { get; } = [];

    public List<Func<MarkedLexer, string, List<MarkedToken>, MarkedToken?>> Inline { get; } = [];

    public List<Func<MarkedLexer, string, int?>> StartBlock { get; } = [];

    public List<Func<MarkedLexer, string, int?>> StartInline { get; } = [];
}

/// <summary>
/// Port of <c>components/markdown.ts</c>'s <c>LATEX_MARKDOWN_EXTENSIONS</c>: a block-level
/// <c>latexBlock</c> tokenizer (<c>$$…$$</c> / <c>\[…\]</c>) and an inline-level <c>latex</c>
/// tokenizer (<c>$…$</c> / <c>\(…\)</c>), each with the <c>start</c> probe marked uses to decide
/// where to truncate the source before falling back to the paragraph / inlineText tokenizer.
/// </summary>
public static class MarkedLatexExtensions
{
    // The JS whitespace set (see T15); spelled out because .NET's \s differs.
    private const string JsWsChars = @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    private static readonly Regex BlockStartRegex = new(@"(?:^|\n) {0,3}(?:\$\$|\\\[)");

    private static readonly Regex BlockDollarRegex = new(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*?)\$\$[ \t]*(?:\n|\z)");

    private static readonly Regex BlockBracketRegex = new(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*?)\\\][ \t]*(?:\n|\z)");

    private static readonly Regex PendingBracketRegex = new(@"^ {0,3}\\\[[ \t]*(?:\n)?([\s\S]*)\z");

    private static readonly Regex PendingDollarRegex = new(@"^ {0,3}\$\$[ \t]*(?:\n)?([\s\S]*)\z");

    private static readonly Regex PendingDollarMathRegex = new(@"\\[A-Za-z]+|[_^=+*/<>()[\]|±≤≥≠≈∈→⇒∞∫∑√-]");

    private static readonly Regex InlineDollarSpaceRegex = new(@"^\$[" + JsWsChars + "]");

    private static readonly Regex TrailingWhitespaceRegex = new("[" + JsWsChars + @"]\z");

    private static readonly Regex LeadingDigitRegex = new(@"^[0-9]");

    private static readonly Regex ShoutyWordRegex = new(@"^[A-Z_][A-Z0-9_]*(?:[^A-Za-z0-9_" + JsWsChars + @"])?\z");

    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*");

    public static MarkedExtensions Create()
    {
        var extensions = new MarkedExtensions();
        extensions.Block.Add(TokenizeBlockLatex);
        extensions.Inline.Add(TokenizeInlineLatex);
        extensions.StartBlock.Add(LatexBlockStart);
        extensions.StartInline.Add(LatexInlineStart);
        return extensions;
    }

    private static int? LatexBlockStart(MarkedLexer lexer, string source)
    {
        var match = BlockStartRegex.Match(source);
        return match.Success ? match.Index + (match.Value.StartsWith('\n') ? 1 : 0) : null;
    }

    private static int? LatexInlineStart(MarkedLexer lexer, string source)
    {
        var dollar = source.IndexOf('$');
        var paren = source.IndexOf("\\(", StringComparison.Ordinal);
        var bracket = source.IndexOf("\\[", StringComparison.Ordinal);
        var start = -1;
        foreach (var index in new[] { dollar, paren, bracket })
        {
            if (index >= 0 && (start < 0 || index < start))
            {
                start = index;
            }
        }

        return start >= 0 ? start : null;
    }

    private static bool IsEscaped(string source, int index)
    {
        var backslashes = 0;
        for (var position = index - 1; position >= 0 && source[position] == '\\'; position--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static int FindClosingDelimiter(string source, string closing, int start)
    {
        var index = source.IndexOf(closing, start, StringComparison.Ordinal);
        while (index >= 0 && IsEscaped(source, index))
        {
            index = source.IndexOf(closing, index + closing.Length, StringComparison.Ordinal);
        }

        return index;
    }

    private static bool LooksLikePendingDollarMath(string source) => PendingDollarMathRegex.IsMatch(source);

    private static MarkedToken? TokenizeInlineLatex(MarkedLexer lexer, string source, List<MarkedToken> tokens)
    {
        string opening;
        string closing;
        if (source.StartsWith("$$", StringComparison.Ordinal))
        {
            opening = "$$";
            closing = "$$";
        }
        else if (source.StartsWith("\\(", StringComparison.Ordinal))
        {
            opening = "\\(";
            closing = "\\)";
        }
        else if (source.StartsWith("\\[", StringComparison.Ordinal))
        {
            opening = "\\[";
            closing = "\\]";
        }
        else if (source.StartsWith("$", StringComparison.Ordinal) && !InlineDollarSpaceRegex.IsMatch(source))
        {
            opening = "$";
            closing = "$";
        }
        else
        {
            return null;
        }

        var closingIndex = FindClosingDelimiter(source, closing, opening.Length);
        if (closingIndex >= 0 && opening == "$")
        {
            var between = JsString.Slice(source, opening.Length, closingIndex);
            var after = JsString.Slice(source, closingIndex + 1);
            if (TrailingWhitespaceRegex.IsMatch(between)
                || LeadingDigitRegex.IsMatch(after)
                || (ShoutyWordRegex.IsMatch(between) && IdentifierRegex.IsMatch(after))
                || between.Contains('`'))
            {
                return null;
            }
        }

        if (closingIndex < 0)
        {
            var pendingSource = JsString.Slice(source, opening.Length);
            if (opening.StartsWith("\\", StringComparison.Ordinal) || LooksLikePendingDollarMath(pendingSource))
            {
                return new MarkedToken { Type = "latex", Raw = source, Text = pendingSource, Pending = true };
            }

            return null;
        }

        var text = JsString.Slice(source, opening.Length, closingIndex);
        if (text.Length == 0 || text.Contains('\n'))
        {
            return null;
        }

        var raw = JsString.Slice(source, 0, closingIndex + closing.Length);
        return new MarkedToken { Type = "latex", Raw = raw, Text = text };
    }

    private static MarkedToken? TokenizeBlockLatex(MarkedLexer lexer, string source, List<MarkedToken> tokens)
    {
        var dollarMatch = BlockDollarRegex.Match(source);
        if (dollarMatch.Success && dollarMatch.Groups[1].Value.Length > 0)
        {
            return new MarkedToken
            {
                Type = "latexBlock",
                Raw = dollarMatch.Value,
                Text = JsString.Trim(dollarMatch.Groups[1].Value),
            };
        }

        var bracketMatch = BlockBracketRegex.Match(source);
        if (bracketMatch.Success && bracketMatch.Groups[1].Value.Length > 0)
        {
            return new MarkedToken
            {
                Type = "latexBlock",
                Raw = bracketMatch.Value,
                Text = JsString.Trim(bracketMatch.Groups[1].Value),
            };
        }

        var pendingBracket = PendingBracketRegex.Match(source);
        if (pendingBracket.Success)
        {
            return new MarkedToken
            {
                Type = "latexBlock",
                Raw = pendingBracket.Value,
                Text = pendingBracket.Groups[1].Value,
                Pending = true,
            };
        }

        var pendingDollar = PendingDollarRegex.Match(source);
        if (pendingDollar.Success
            && pendingDollar.Groups[1].Value.Length > 0
            && LooksLikePendingDollarMath(pendingDollar.Groups[1].Value))
        {
            return new MarkedToken
            {
                Type = "latexBlock",
                Raw = pendingDollar.Value,
                Text = pendingDollar.Groups[1].Value,
                Pending = true,
            };
        }

        return null;
    }
}
