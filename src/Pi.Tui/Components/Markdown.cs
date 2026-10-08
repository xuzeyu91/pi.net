using Pi.Tui.Marked;
using System.Text.RegularExpressions;

namespace Pi.Tui.Components;

/// <summary>
/// Default text styling for markdown content (port of <c>DefaultTextStyle</c>).
/// Applied to all text unless overridden by markdown formatting.
/// </summary>
public sealed class DefaultTextStyle
{
    /// <summary>Foreground color function.</summary>
    public Func<string, string>? Color { get; init; }

    /// <summary>Background color function (applied at the padding stage, not here).</summary>
    public Func<string, string>? BgColor { get; init; }

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public bool Strikethrough { get; init; }

    public bool Underline { get; init; }
}

/// <summary>
/// Theme functions for markdown elements (port of <c>MarkdownTheme</c>).
/// Each function takes text and returns styled text with ANSI codes.
/// </summary>
public sealed class MarkdownTheme
{
    public required Func<string, string> Heading { get; init; }

    public required Func<string, string> Link { get; init; }

    public required Func<string, string> LinkUrl { get; init; }

    public required Func<string, string> Code { get; init; }

    public required Func<string, string> CodeBlock { get; init; }

    public required Func<string, string> CodeBlockBorder { get; init; }

    public required Func<string, string> Quote { get; init; }

    public required Func<string, string> QuoteBorder { get; init; }

    public required Func<string, string> Hr { get; init; }

    public required Func<string, string> ListBullet { get; init; }

    public required Func<string, string> Bold { get; init; }

    public required Func<string, string> Italic { get; init; }

    public required Func<string, string> Strikethrough { get; init; }

    public required Func<string, string> Underline { get; init; }

    public Func<string, string?, string[]>? HighlightCode { get; init; }

    /// <summary>Prefix applied to each rendered code block line (default: "  ").</summary>
    public string? CodeBlockIndent { get; init; }
}

/// <summary>Options for the <see cref="Markdown"/> component (port of <c>MarkdownOptions</c>).</summary>
public sealed class MarkdownOptions
{
    /// <summary>Preserve source list markers instead of normalizing them.</summary>
    public bool PreserveOrderedListMarkers { get; set; }

    /// <summary>Preserve source backslash escapes instead of normalizing escaped punctuation.</summary>
    public bool PreserveBackslashEscapes { get; set; }

    /// <summary>Transform source Markdown before parsing, with the exact width available for content.</summary>
    public Func<string, int, string>? Transform { get; set; }

    /// <summary>Render supported LaTeX math expressions as Unicode text (default: true).</summary>
    public bool RenderLatex { get; set; } = true;
}

/// <summary>
/// How inline tokens apply the surrounding text style, plus the ANSI prefix that style
/// produces so nested resets can restore it (port of <c>InlineStyleContext</c>).
/// </summary>
/// <param name="ApplyText">Applies the surrounding style to a text run.</param>
/// <param name="StylePrefix">The ANSI prefix the style emits (empty when there is none).</param>
internal sealed record InlineStyleContext(Func<string, string> ApplyText, string StylePrefix);

/// <summary>
/// Markdown component - renders markdown to styled terminal lines (port of
/// <c>components/markdown.ts</c>).
/// </summary>
/// <remarks>
/// The parser is a module-level singleton in TS: a <c>Marked</c> instance with the strict
/// strikethrough tokenizer override and the latex extensions. <see cref="MarkedLexer.Lex"/>
/// builds a fresh lexer per call from those options, which is exactly what marked's
/// <c>lexer()</c> does, so one shared options instance is safe here.
/// </remarks>
public sealed class Markdown : IComponent
{
    private static readonly MarkedOptions ParserOptions = new() { Extensions = MarkedLatexExtensions.Create() };

    private static readonly Regex ClosingFenceMarkerRegex = new(@"^(`{3,}|~{3,})");

    private static readonly Regex OrderedListMarkerRegex = new(@"^(?: {0,3})([0-9]{1,9}[.)])[ \t]+");

    private static readonly Regex UnorderedListMarkerRegex = new(@"^(?: {0,3})([-+*])(?:[ \t]+|(?=\r?\n|\z))");

    private readonly string _sentinel = "\u0000";

    private string _text;
    private readonly int _paddingX;
    private readonly int _paddingY;
    private readonly DefaultTextStyle? _defaultTextStyle;
    private readonly MarkdownTheme _theme;
    private readonly MarkdownOptions _options;
    private string? _defaultStylePrefix;

    // Cache for rendered output
    private string? _cachedText;
    private int? _cachedWidth;
    private string[]? _cachedLines;

    // Parsed tokens depend only on the source, so they survive theme and width invalidation.
    // The TS original holds them in a WeakRef so the token tree (about ten times the size of
    // its source) can be collected between bursts of re-renders; a strong reference here is
    // observationally equivalent — the only difference is when the GC reclaims the tree.
    private (string Source, List<MarkedToken> Tokens)? _cachedTokens;

    public Markdown(
        string text,
        int paddingX,
        int paddingY,
        MarkdownTheme theme,
        DefaultTextStyle? defaultTextStyle = null,
        MarkdownOptions? options = null)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
        _theme = theme;
        _defaultTextStyle = defaultTextStyle;
        _options = options ?? new MarkdownOptions();
    }

    public void SetText(string text)
    {
        _text = text;
        Invalidate();
    }

    public void Invalidate()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public string[] Render(int width)
    {
        // Check cache
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == width)
        {
            return _cachedLines;
        }

        // Calculate available width for content (subtract horizontal padding)
        var contentWidth = Math.Max(1, width - _paddingX * 2);
        var text = _options.Transform?.Invoke(_text, contentWidth) ?? _text;

        // Don't render anything if there's no actual text
        if (text.Length == 0 || JsString.Trim(text).Length == 0)
        {
            var empty = Array.Empty<string>();
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = empty;
            return empty;
        }

        // Replace tabs with 3 spaces for consistent rendering
        var normalizedText = text.Replace("\t", "   ");

        // Parse markdown to HTML-like tokens
        List<MarkedToken> tokens;
        if (_cachedTokens is { Source: var cachedSource } && cachedSource == normalizedText)
        {
            tokens = _cachedTokens.Value.Tokens;
        }
        else
        {
            tokens = MarkedLexer.Lex(normalizedText, ParserOptions);
            TrimPartialClosingFences(tokens);
            _cachedTokens = (normalizedText, tokens);
        }

        // Convert tokens to styled terminal output
        var renderedLines = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var nextTokenType = i + 1 < tokens.Count ? tokens[i + 1].Type : null;
            renderedLines.AddRange(RenderToken(token, contentWidth, nextTokenType));
        }

        // Wrap lines (NO padding, NO background yet)
        var wrappedLines = new List<string>();
        foreach (var line in renderedLines)
        {
            if (TerminalImage.IsImageLine(line))
            {
                wrappedLines.Add(line);
            }
            else
            {
                wrappedLines.AddRange(TextLayout.WrapTextWithAnsi(line, contentWidth));
            }
        }

        // Add margins and background to each wrapped line
        var leftMargin = new string(' ', _paddingX);
        var rightMargin = new string(' ', _paddingX);
        var bgFn = _defaultTextStyle?.BgColor;
        var contentLines = new List<string>();

        foreach (var line in wrappedLines)
        {
            if (TerminalImage.IsImageLine(line))
            {
                contentLines.Add(line);
                continue;
            }

            var lineWithMargins = leftMargin + line + rightMargin;

            if (bgFn is not null)
            {
                contentLines.Add(TextLayout.ApplyBackgroundToLine(lineWithMargins, width, bgFn));
            }
            else
            {
                // No background - just pad to width
                var visibleLen = UnicodeWidth.VisibleWidth(lineWithMargins);
                var paddingNeeded = Math.Max(0, width - visibleLen);
                contentLines.Add(lineWithMargins + new string(' ', paddingNeeded));
            }
        }

        // Add top/bottom padding (empty lines)
        var emptyLine = new string(' ', width);
        var emptyLines = new List<string>();
        for (var i = 0; i < _paddingY; i++)
        {
            var line = bgFn is not null ? TextLayout.ApplyBackgroundToLine(emptyLine, width, bgFn) : emptyLine;
            emptyLines.Add(line);
        }

        // Combine top padding, content, and bottom padding
        var result = new List<string>(emptyLines.Count + contentLines.Count + emptyLines.Count);
        result.AddRange(emptyLines);
        result.AddRange(contentLines);
        result.AddRange(emptyLines);
        // flattenLines() is a V8 memory optimisation (it reads each string whole so V8
        // flattens the concatenation tree); it has no semantic effect in .NET.

        // Update cache
        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result.ToArray();

        return result.Count > 0 ? _cachedLines : [""];
    }

    /// <summary>
    /// Apply default text style to a string.
    /// This is the base styling applied to all text content.
    /// NOTE: Background color is NOT applied here - it's applied at the padding stage
    /// to ensure it extends to the full line width.
    /// </summary>
    private string ApplyDefaultStyle(string text)
    {
        if (_defaultTextStyle is null)
        {
            return text;
        }

        var styled = text;

        // Apply foreground color (NOT background - that's applied at padding stage)
        if (_defaultTextStyle.Color is { } color)
        {
            styled = color(styled);
        }

        // Apply text decorations using this.theme
        if (_defaultTextStyle.Bold)
        {
            styled = _theme.Bold(styled);
        }

        if (_defaultTextStyle.Italic)
        {
            styled = _theme.Italic(styled);
        }

        if (_defaultTextStyle.Strikethrough)
        {
            styled = _theme.Strikethrough(styled);
        }

        if (_defaultTextStyle.Underline)
        {
            styled = _theme.Underline(styled);
        }

        return styled;
    }

    private string GetDefaultStylePrefix()
    {
        if (_defaultTextStyle is null)
        {
            return "";
        }

        if (_defaultStylePrefix is not null)
        {
            return _defaultStylePrefix;
        }

        var styled = _sentinel;

        if (_defaultTextStyle.Color is { } color)
        {
            styled = color(styled);
        }

        if (_defaultTextStyle.Bold)
        {
            styled = _theme.Bold(styled);
        }

        if (_defaultTextStyle.Italic)
        {
            styled = _theme.Italic(styled);
        }

        if (_defaultTextStyle.Strikethrough)
        {
            styled = _theme.Strikethrough(styled);
        }

        if (_defaultTextStyle.Underline)
        {
            styled = _theme.Underline(styled);
        }

        var sentinelIndex = styled.IndexOf(_sentinel, StringComparison.Ordinal);
        _defaultStylePrefix = sentinelIndex >= 0 ? JsString.Slice(styled, 0, sentinelIndex) : "";
        return _defaultStylePrefix;
    }

    private static string GetStylePrefix(Func<string, string> styleFn)
    {
        const string sentinel = "\u0000";
        var styled = styleFn(sentinel);
        var sentinelIndex = styled.IndexOf(sentinel, StringComparison.Ordinal);
        return sentinelIndex >= 0 ? JsString.Slice(styled, 0, sentinelIndex) : "";
    }

    private InlineStyleContext GetDefaultInlineStyleContext()
    {
        return new InlineStyleContext(ApplyDefaultStyle, GetDefaultStylePrefix());
    }

    private string[] RenderToken(MarkedToken token, int width, string? nextTokenType, InlineStyleContext? styleContext = null)
    {
        var lines = new List<string>();

        switch (token.Type)
        {
            case "heading":
            {
                var headingLevel = token.Depth ?? 1;
                var headingPrefix = new string('#', headingLevel) + " ";

                // Build a heading-specific style context so inline tokens (codespan, bold, etc.)
                // restore heading styling after their own ANSI resets instead of falling back to
                // the default text style.
                Func<string, string> headingStyleFn = headingLevel == 1
                    ? text => _theme.Heading(_theme.Bold(_theme.Underline(text)))
                    : text => _theme.Heading(_theme.Bold(text));

                var headingStyleContext = new InlineStyleContext(headingStyleFn, GetStylePrefix(headingStyleFn));

                var headingText = RenderInlineTokens(token.Tokens ?? [], headingStyleContext);
                var styledHeading = headingLevel >= 3 ? headingStyleFn(headingPrefix) + headingText : headingText;
                lines.Add(styledHeading);
                if (nextTokenType is not null && nextTokenType != "space")
                {
                    lines.Add(""); // Add spacing after headings (unless space token follows)
                }

                break;
            }

            case "paragraph":
            {
                var paragraphText = RenderInlineTokens(token.Tokens ?? [], styleContext);
                lines.Add(paragraphText);
                // Don't add spacing if next token is space or list
                if (nextTokenType is not null && nextTokenType != "list" && nextTokenType != "space")
                {
                    lines.Add("");
                }

                break;
            }

            case "text":
                lines.Add(RenderInlineTokens([token], styleContext));
                break;

            case "latexBlock":
            {
                var rendered = token.Pending != true && _options.RenderLatex
                    ? Latex.Render(token.Text ?? "", new RenderLatexOptions { Display = true }) ?? JsString.Trim(token.Raw)
                    : JsString.Trim(token.Raw);
                foreach (var line in rendered.Split('\n'))
                {
                    lines.Add(ApplyDefaultStyle(line));
                }

                if (nextTokenType is not null && nextTokenType != "space")
                {
                    lines.Add("");
                }

                break;
            }

            case "code":
            {
                var indent = _theme.CodeBlockIndent ?? "  ";
                lines.Add(_theme.CodeBlockBorder($"```{token.Lang ?? ""}"));
                if (_theme.HighlightCode is { } highlightCode)
                {
                    var highlightedLines = highlightCode(token.Text ?? "", token.Lang);
                    foreach (var hlLine in highlightedLines)
                    {
                        lines.Add($"{indent}{hlLine}");
                    }
                }
                else
                {
                    // Split code by newlines and style each line
                    var codeLines = (token.Text ?? "").Split('\n');
                    foreach (var codeLine in codeLines)
                    {
                        lines.Add($"{indent}{_theme.CodeBlock(codeLine)}");
                    }
                }

                lines.Add(_theme.CodeBlockBorder("```"));
                if (nextTokenType is not null && nextTokenType != "space")
                {
                    lines.Add(""); // Add spacing after code blocks (unless space token follows)
                }

                break;
            }

            case "list":
                lines.AddRange(RenderList(token, 0, width, styleContext));
                // Don't add spacing after lists if a space token follows
                // (the space token will handle it)
                break;

            case "table":
                lines.AddRange(RenderTable(token, width, nextTokenType, styleContext));
                break;

            case "blockquote":
            {
                var quoteStyle = (string text) => _theme.Quote(_theme.Italic(text));
                var quoteStylePrefix = GetStylePrefix(quoteStyle);
                string ApplyQuoteStyle(string line)
                {
                    if (quoteStylePrefix.Length == 0)
                    {
                        return quoteStyle(line);
                    }

                    var lineWithReappliedStyle = line.Replace("\x1b[0m", "\x1b[0m" + quoteStylePrefix);
                    return quoteStyle(lineWithReappliedStyle);
                }

                // Calculate available width for quote content (subtract border "│ " = 2 chars)
                var quoteContentWidth = Math.Max(1, width - 2);

                // Blockquotes contain block-level tokens (paragraph, list, code, etc.), so render
                // children with renderToken() instead of renderInlineTokens().
                // Default message style should not apply inside blockquotes.
                var quoteInlineStyleContext = new InlineStyleContext(text => text, quoteStylePrefix);
                var quoteTokens = token.Tokens ?? [];
                var renderedQuoteLines = new List<string>();
                for (var i = 0; i < quoteTokens.Count; i++)
                {
                    var quoteToken = quoteTokens[i];
                    var nextQuoteTokenType = i + 1 < quoteTokens.Count ? quoteTokens[i + 1].Type : null;
                    renderedQuoteLines.AddRange(RenderToken(quoteToken, quoteContentWidth, nextQuoteTokenType, quoteInlineStyleContext));
                }

                // Avoid rendering an extra empty quote line before the outer blockquote spacing.
                while (renderedQuoteLines.Count > 0 && renderedQuoteLines[^1].Length == 0)
                {
                    renderedQuoteLines.RemoveAt(renderedQuoteLines.Count - 1);
                }

                foreach (var quoteLine in renderedQuoteLines)
                {
                    var styledLine = ApplyQuoteStyle(quoteLine);
                    foreach (var wrappedLine in TextLayout.WrapTextWithAnsi(styledLine, quoteContentWidth))
                    {
                        lines.Add(_theme.QuoteBorder("│ ") + wrappedLine);
                    }
                }

                if (nextTokenType is not null && nextTokenType != "space")
                {
                    lines.Add(""); // Add spacing after blockquotes (unless space token follows)
                }

                break;
            }

            case "hr":
                lines.Add(_theme.Hr(new string('─', Math.Min(width, 80))));
                if (nextTokenType is not null && nextTokenType != "space")
                {
                    lines.Add(""); // Add spacing after horizontal rules (unless space token follows)
                }

                break;

            case "html":
                // Render HTML as plain text (escaped for terminal)
                lines.Add(ApplyDefaultStyle(JsString.Trim(token.Raw)));
                break;

            case "space":
                // Space tokens represent blank lines in markdown
                lines.Add("");
                break;

            default:
                // Handle any other token types as plain text
                if (token.Text is { } plainText)
                {
                    lines.Add(plainText);
                }

                break;
        }

        return lines.ToArray();
    }

    private string RenderInlineTokens(List<MarkedToken> tokens, InlineStyleContext? styleContext = null)
    {
        var result = "";
        var resolvedStyleContext = styleContext ?? GetDefaultInlineStyleContext();
        var applyText = resolvedStyleContext.ApplyText;
        var stylePrefix = resolvedStyleContext.StylePrefix;
        string ApplyTextWithNewlines(string text)
        {
            var segments = text.Split('\n');
            return string.Join("\n", segments.Select(segment => applyText(segment)));
        }

        foreach (var token in tokens)
        {
            switch (token.Type)
            {
                case "latex":
                {
                    var rendered = token.Pending != true && _options.RenderLatex
                        ? Latex.Render(token.Text ?? "") ?? token.Raw
                        : token.Raw;
                    result += ApplyTextWithNewlines(rendered);
                    break;
                }

                case "escape":
                    result += ApplyTextWithNewlines(_options.PreserveBackslashEscapes ? token.Raw : token.Text ?? "");
                    break;

                case "text":
                    // Text tokens in list items can have nested tokens for inline formatting
                    if (token.Tokens is { Count: > 0 })
                    {
                        result += RenderInlineTokens(token.Tokens, resolvedStyleContext);
                    }
                    else
                    {
                        result += ApplyTextWithNewlines(token.Text ?? "");
                    }

                    break;

                case "paragraph":
                    // Paragraph tokens contain nested inline tokens
                    result += RenderInlineTokens(token.Tokens ?? [], resolvedStyleContext);
                    break;

                case "strong":
                {
                    var boldContent = RenderInlineTokens(token.Tokens ?? [], resolvedStyleContext);
                    result += _theme.Bold(boldContent) + stylePrefix;
                    break;
                }

                case "em":
                {
                    var italicContent = RenderInlineTokens(token.Tokens ?? [], resolvedStyleContext);
                    result += _theme.Italic(italicContent) + stylePrefix;
                    break;
                }

                case "codespan":
                    result += _theme.Code(token.Text ?? "") + stylePrefix;
                    break;

                case "link":
                {
                    var linkText = RenderInlineTokens(token.Tokens ?? [], resolvedStyleContext);
                    var styledLink = _theme.Link(_theme.Underline(linkText));
                    if (TerminalImage.GetCapabilities().Hyperlinks)
                    {
                        // OSC 8: render as a clickable hyperlink. The URL is not printed inline,
                        // so we always show only the link text regardless of whether it matches href.
                        result += TerminalImage.Hyperlink(styledLink, token.Href ?? "") + stylePrefix;
                    }
                    else
                    {
                        // Fallback: print URL in parentheses when text differs from href.
                        // Compare raw token.text (not styled) against href for the equality check.
                        // For mailto: links strip the prefix (autolinked emails use text="foo@bar.com"
                        // but href="mailto:foo@bar.com").
                        var href = token.Href ?? "";
                        var hrefForComparison = href.StartsWith("mailto:", StringComparison.Ordinal) ? href[7..] : href;
                        if (token.Text == href || token.Text == hrefForComparison)
                        {
                            result += styledLink + stylePrefix;
                        }
                        else
                        {
                            result += styledLink + _theme.LinkUrl($" ({href})") + stylePrefix;
                        }
                    }

                    break;
                }

                case "br":
                    result += "\n";
                    break;

                case "del":
                {
                    var delContent = RenderInlineTokens(token.Tokens ?? [], resolvedStyleContext);
                    result += _theme.Strikethrough(delContent) + stylePrefix;
                    break;
                }

                case "html":
                    // Render inline HTML as plain text
                    result += ApplyTextWithNewlines(token.Raw);
                    break;

                default:
                    // Handle any other inline token types as plain text
                    if (token.Text is { } text)
                    {
                        result += ApplyTextWithNewlines(text);
                    }

                    break;
            }
        }

        while (stylePrefix.Length > 0 && result.EndsWith(stylePrefix, StringComparison.Ordinal))
        {
            result = JsString.Slice(result, 0, -stylePrefix.Length);
        }

        return result;
    }

    private static string? GetOrderedListMarker(MarkedToken item)
    {
        var match = OrderedListMarkerRegex.Match(item.Raw);
        return match.Success ? $"{match.Groups[1].Value} " : null;
    }

    private static string? GetUnorderedListMarker(MarkedToken item)
    {
        var match = UnorderedListMarkerRegex.Match(item.Raw);
        return match.Success ? $"{match.Groups[1].Value} " : null;
    }

    /// <summary>
    /// Render a list with proper nesting support
    /// </summary>
    private string[] RenderList(MarkedToken token, int depth, int width, InlineStyleContext? styleContext)
    {
        var lines = new List<string>();
        var indent = new string(' ', 4 * depth);
        // Use the list's start property (defaults to 1 for ordered lists)
        var startNumber = token.Start ?? 1;
        var items = token.Items ?? [];

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var isLastItem = i == items.Count - 1;
            var bullet = token.Ordered == true
                ? _options.PreserveOrderedListMarkers
                    ? GetOrderedListMarker(item) ?? $"{startNumber + i}. "
                    : $"{startNumber + i}. "
                : _options.PreserveOrderedListMarkers
                    ? GetUnorderedListMarker(item) ?? "- "
                    : "- ";
            var taskMarker = item.Task == true ? $"[{(item.Checked == true ? "x" : " ")}] " : "";
            var marker = bullet + taskMarker;
            var firstPrefix = indent + _theme.ListBullet(marker);
            var continuationPrefix = indent + new string(' ', UnicodeWidth.VisibleWidth(marker));
            var itemWidth = Math.Max(1, width - UnicodeWidth.VisibleWidth(firstPrefix));
            var renderedAnyLine = false;

            foreach (var itemToken in item.Tokens ?? [])
            {
                if (itemToken.Type == "list")
                {
                    lines.AddRange(RenderList(itemToken, depth + 1, width, styleContext));
                    renderedAnyLine = true;
                    continue;
                }

                foreach (var line in RenderToken(itemToken, itemWidth, null, styleContext))
                {
                    foreach (var wrappedLine in TextLayout.WrapTextWithAnsi(line, itemWidth))
                    {
                        var linePrefix = renderedAnyLine ? continuationPrefix : firstPrefix;
                        lines.Add(linePrefix + wrappedLine);
                        renderedAnyLine = true;
                    }
                }
            }

            if (!renderedAnyLine)
            {
                lines.Add(firstPrefix);
            }

            if (token.Loose == true && !isLastItem)
            {
                lines.Add("");
            }
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Get the visible width of the longest word in a string.
    /// </summary>
    private static int GetLongestWordWidth(string text, int? maxWidth = null)
    {
        var longest = 0;
        foreach (var word in SplitByJsWhitespace(text))
        {
            longest = Math.Max(longest, UnicodeWidth.VisibleWidth(word));
        }

        if (maxWidth is null)
        {
            return longest;
        }

        return Math.Min(longest, maxWidth.Value);
    }

    /// <summary>Port of <c>text.split(/\s+/)</c> with the JS whitespace set (empty runs dropped).</summary>
    private static List<string> SplitByJsWhitespace(string text)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || JsString.IsWhitespace(text[i]))
            {
                if (i > start)
                {
                    words.Add(text[start..i]);
                }

                start = i + 1;
            }
        }

        return words;
    }

    /// <summary>
    /// Wrap a table cell to fit into a column.
    ///
    /// Delegates to wrapTextWithAnsi() so ANSI codes + long tokens are handled
    /// consistently with the rest of the renderer.
    /// </summary>
    private static string[] WrapCellText(string text, int maxWidth, string stylePrefix = "")
    {
        var lines = TextLayout.WrapTextWithAnsi(text, Math.Max(1, maxWidth));
        var result = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            // Reset text styles after each non-final fragment, then restore the surrounding style before padding and borders.
            var styleReset = i < lines.Count - 1 ? "\x1b[22;23;24;25;27;28;29;39m" : "";
            result[i] = $"{lines[i]}{styleReset}{stylePrefix}";
        }

        return result;
    }

    /// <summary>
    /// Render a table with width-aware cell wrapping.
    /// Cells that don't fit are wrapped to multiple lines.
    /// </summary>
    private string[] RenderTable(
        MarkedToken token,
        int availableWidth,
        string? nextTokenType,
        InlineStyleContext? styleContext)
    {
        var lines = new List<string>();
        var header = token.Header ?? [];
        var numCols = header.Count;

        if (numCols == 0)
        {
            return lines.ToArray();
        }

        // Calculate border overhead: "│ " + (n-1) * " │ " + " │"
        // = 2 + (n-1) * 3 + 2 = 3n + 1
        var borderOverhead = 3 * numCols + 1;
        var availableForCells = availableWidth - borderOverhead;
        if (availableForCells < numCols)
        {
            // Too narrow to render a stable table. Fall back to raw markdown.
            var fallbackLines = token.Raw.Length > 0
                ? TextLayout.WrapTextWithAnsi(token.Raw, availableWidth)
                : new List<string>();
            if (nextTokenType is not null && nextTokenType != "space")
            {
                fallbackLines.Add("");
            }

            return fallbackLines.ToArray();
        }

        const int maxUnbrokenWordWidth = 30;

        // Calculate natural column widths (what each column needs without constraints)
        var naturalWidths = new int[numCols];
        var minWordWidths = new int[numCols];
        for (var i = 0; i < numCols; i++)
        {
            var headerText = RenderInlineTokens(header[i].Tokens ?? [], styleContext);
            naturalWidths[i] = UnicodeWidth.VisibleWidth(headerText);
            minWordWidths[i] = Math.Max(1, GetLongestWordWidth(headerText, maxUnbrokenWordWidth));
        }

        foreach (var row in token.Rows ?? [])
        {
            for (var i = 0; i < Math.Min(row.Count, numCols); i++)
            {
                var cellText = RenderInlineTokens(row[i].Tokens ?? [], styleContext);
                naturalWidths[i] = Math.Max(naturalWidths[i], UnicodeWidth.VisibleWidth(cellText));
                minWordWidths[i] = Math.Max(minWordWidths[i], GetLongestWordWidth(cellText, maxUnbrokenWordWidth));
            }
        }

        var minColumnWidths = minWordWidths;
        var minCellsWidth = minColumnWidths.Sum();

        if (minCellsWidth > availableForCells)
        {
            minColumnWidths = new int[numCols];
            Array.Fill(minColumnWidths, 1);
            var remaining = availableForCells - numCols;

            if (remaining > 0)
            {
                var totalWeight = minWordWidths.Sum(width => Math.Max(0, width - 1));
                var growth = minWordWidths.Select(width =>
                {
                    var weight = Math.Max(0, width - 1);
                    return totalWeight > 0 ? (int)Math.Floor((double)weight / totalWeight * remaining) : 0;
                }).ToArray();

                for (var i = 0; i < numCols; i++)
                {
                    minColumnWidths[i] += i < growth.Length ? growth[i] : 0;
                }

                var allocated = growth.Sum();
                var leftover = remaining - allocated;
                for (var i = 0; leftover > 0 && i < numCols; i++)
                {
                    minColumnWidths[i]++;
                    leftover--;
                }
            }

            minCellsWidth = minColumnWidths.Sum();
        }

        // Calculate column widths that fit within available width
        var totalNaturalWidth = naturalWidths.Sum() + borderOverhead;
        int[] columnWidths;

        if (totalNaturalWidth <= availableWidth)
        {
            // Everything fits naturally
            columnWidths = naturalWidths.Select((width, index) => Math.Max(width, minColumnWidths[index])).ToArray();
        }
        else
        {
            // Need to shrink columns to fit
            var totalGrowPotential = naturalWidths.Select((width, index) => Math.Max(0, width - minColumnWidths[index])).Sum();
            var extraWidth = Math.Max(0, availableForCells - minCellsWidth);
            columnWidths = minColumnWidths.Select((minWidth, index) =>
            {
                var naturalWidth = naturalWidths[index];
                var minWidthDelta = Math.Max(0, naturalWidth - minWidth);
                var grow = 0;
                if (totalGrowPotential > 0)
                {
                    grow = (int)Math.Floor((double)minWidthDelta / totalGrowPotential * extraWidth);
                }

                return minWidth + grow;
            }).ToArray();

            // Adjust for rounding errors - distribute remaining space
            var allocated = columnWidths.Sum();
            var remaining = availableForCells - allocated;
            while (remaining > 0)
            {
                var grew = false;
                for (var i = 0; i < numCols && remaining > 0; i++)
                {
                    if (columnWidths[i] < naturalWidths[i])
                    {
                        columnWidths[i]++;
                        remaining--;
                        grew = true;
                    }
                }

                if (!grew)
                {
                    break;
                }
            }
        }

        // Render top border
        var topBorderCells = columnWidths.Select(w => new string('─', w));
        lines.Add($"┌─{string.Join("─┬─", topBorderCells)}─┐");

        // Render header with wrapping
        var headerCellLines = header.Select((cell, i) =>
        {
            var text = RenderInlineTokens(cell.Tokens ?? [], styleContext);
            return WrapCellText(text, columnWidths[i], styleContext?.StylePrefix ?? "");
        }).ToArray();
        var headerLineCount = headerCellLines.Max(c => c.Length);

        for (var lineIdx = 0; lineIdx < headerLineCount; lineIdx++)
        {
            var rowParts = headerCellLines.Select((cellLines, colIdx) =>
            {
                var text = lineIdx < cellLines.Length ? cellLines[lineIdx] : "";
                var padded = text + new string(' ', Math.Max(0, columnWidths[colIdx] - UnicodeWidth.VisibleWidth(text)));
                return _theme.Bold(padded);
            });
            lines.Add($"│ {string.Join(" │ ", rowParts)} │");
        }

        // Render separator
        var separatorCells = columnWidths.Select(w => new string('─', w));
        var separatorLine = $"├─{string.Join("─┼─", separatorCells)}─┤";
        lines.Add(separatorLine);

        // Render rows with wrapping
        var rows = token.Rows ?? [];
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowCellLines = row.Take(numCols).Select((cell, i) =>
            {
                var text = RenderInlineTokens(cell.Tokens ?? [], styleContext);
                return WrapCellText(text, columnWidths[i], styleContext?.StylePrefix ?? "");
            }).ToArray();
            var rowLineCount = rowCellLines.Length > 0 ? rowCellLines.Max(c => c.Length) : 0;

            for (var lineIdx = 0; lineIdx < rowLineCount; lineIdx++)
            {
                var rowParts = rowCellLines.Select((cellLines, colIdx) =>
                {
                    var text = lineIdx < cellLines.Length ? cellLines[lineIdx] : "";
                    return text + new string(' ', Math.Max(0, columnWidths[colIdx] - UnicodeWidth.VisibleWidth(text)));
                });
                lines.Add($"│ {string.Join(" │ ", rowParts)} │");
            }

            if (rowIndex < rows.Count - 1)
            {
                lines.Add(separatorLine);
            }
        }

        // Render bottom border
        var bottomBorderCells = columnWidths.Select(w => new string('─', w));
        lines.Add($"└─{string.Join("─┴─", bottomBorderCells)}─┘");

        if (nextTokenType is not null && nextTokenType != "space")
        {
            lines.Add(""); // Add spacing after table
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Trim streamed partial closing fences so code blocks do not shrink/flicker
    /// when the final fence character arrives. See https://github.com/earendil-works/pi/issues/5825.
    /// </summary>
    private static void TrimPartialClosingFences(List<MarkedToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        var token = tokens[^1];
        if (token.Type == "list")
        {
            var items = token.Items;
            if (items is { Count: > 0 })
            {
                TrimPartialClosingFences(items[^1].Tokens ?? []);
            }

            return;
        }

        if (token.Type == "blockquote")
        {
            TrimPartialClosingFences(token.Tokens ?? []);
            return;
        }

        if (token.Type != "code")
        {
            return;
        }

        var markerMatch = ClosingFenceMarkerRegex.Match(token.Raw);
        var marker = markerMatch.Success ? markerMatch.Groups[1].Value : null;
        var lastLine = token.Raw.Split('\n')[^1];
        if (marker is null || marker.Length == 0 || lastLine.Length >= marker.Length
            || lastLine != new string(marker[0], lastLine.Length))
        {
            return;
        }

        var text = token.Text ?? "";
        text = JsString.Slice(text, 0, -lastLine.Length);
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        token.Text = text;
    }
}
