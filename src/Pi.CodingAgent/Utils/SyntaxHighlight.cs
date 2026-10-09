using System.Text;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// A theme formatter: one line of highlighted text to the string that replaces it.
/// </summary>
/// <remarks>
/// The TS original stores bare functions in the theme record, so the formatter receives a single line
/// and is never handed the newlines — <see cref="SyntaxHighlight.RenderHighlightedHtml"/> splits first.
/// </remarks>
public delegate string HighlightFormatter(string text);

/// <summary>
/// The tokenizer behind <see cref="SyntaxHighlight.Highlight"/>, standing in for <c>highlight.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// This seam is the one deliberate reduction in this file. The TS original bundles 21 language
/// definitions eagerly and lazily imports the remaining ~180, and <c>hljs.highlight</c> is a ~1 MB
/// state machine whose output this port cannot reproduce without porting it wholesale. The part that
/// belongs to this repository — rewriting <c>hljs-*</c> spans into theme formatters — is ported
/// faithfully and covered by <c>syntax-highlight-corpus.json</c>; the tokenizer is left pluggable.
/// </para>
/// <para>
/// The default implementation (<see cref="PlainTextHighlighter"/>) emits escaped plaintext, so an
/// unconfigured build renders code without syntax colours. See divergence C23 in
/// <c>docs/coding-agent-porting-status.md</c>.
/// </para>
/// </remarks>
public interface IHighlighter
{
    /// <summary><c>hljs.highlight(code, { language, ignoreIllegals }).value</c>.</summary>
    string Highlight(string code, string language, bool ignoreIllegals);

    /// <summary><c>hljs.highlightAuto(code, languageSubset).value</c>.</summary>
    string HighlightAuto(string code, IReadOnlyList<string>? languageSubset);

    /// <summary><c>hljs.getLanguage(name) !== undefined</c>.</summary>
    bool SupportsLanguage(string name);

    /// <summary>The TS <c>loadAllHighlightLanguages()</c>: make the lazily-loaded languages available.</summary>
    Task LoadAllLanguagesAsync();
}

/// <summary>
/// The zero-configuration tokenizer: highlight.js's own plaintext output, i.e. HTML-escaped source
/// with no spans. <see cref="SupportsLanguage"/> reports <see langword="false"/> for every name, which
/// is the truthful answer for a tokenizer that recognises no languages — callers that guard on it (as
/// the TUI does) then take the <see cref="HighlightAuto"/> path and still get correct escaping.
/// </summary>
public sealed class PlainTextHighlighter : IHighlighter
{
    /// <summary>The single instance; the type is stateless.</summary>
    public static PlainTextHighlighter Instance { get; } = new();

    private PlainTextHighlighter()
    {
    }

    /// <inheritdoc/>
    public string Highlight(string code, string language, bool ignoreIllegals) => EscapeHtml(code);

    /// <inheritdoc/>
    public string HighlightAuto(string code, IReadOnlyList<string>? languageSubset) => EscapeHtml(code);

    /// <inheritdoc/>
    public bool SupportsLanguage(string name) => false;

    /// <inheritdoc/>
    public Task LoadAllLanguagesAsync() => Task.CompletedTask;

    /// <summary>highlight.js's <c>escapeHTML</c>, replacements in the same order.</summary>
    internal static string EscapeHtml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#x27;", StringComparison.Ordinal);
}

/// <summary>The options <c>highlight()</c> accepts; the TS <c>HighlightOptions</c> interface.</summary>
public sealed record HighlightOptions
{
    /// <summary>An explicit language; when null or empty the original falls through to <c>highlightAuto</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Passed straight to the tokenizer; <see langword="null"/> means <c>false</c>, as in JS.</summary>
    public bool? IgnoreIllegals { get; init; }

    /// <summary>The candidate languages for <c>highlightAuto</c>; <see langword="null"/> means all.</summary>
    public IReadOnlyList<string>? LanguageSubset { get; init; }

    /// <summary>Scope to formatter; an absent entry falls back to the <c>default</c> entry.</summary>
    public IReadOnlyDictionary<string, HighlightFormatter>? Theme { get; init; }
}

/// <summary>
/// Port of <c>utils/syntax-highlight.ts</c>: turn highlighted HTML into themed terminal text.
/// </summary>
/// <remarks>
/// The tokenizer is behind <see cref="IHighlighter"/>; everything downstream of it is a faithful port.
/// </remarks>
public static class SyntaxHighlight
{
    private const string SpanClose = "</span>";
    private const string SpanOpen = "<span";
    private const string HighlightClassPrefix = "hljs-";
    private const string DefaultScope = "default";

    private static readonly IReadOnlyDictionary<string, HighlightFormatter> NoTheme =
        new Dictionary<string, HighlightFormatter>(StringComparer.Ordinal);

    /// <summary>The JS <c>/\sclass\s*=\s*(?:"([^"]*)"|'([^']*)')/</c>, with JS's whitespace class.</summary>
    private static readonly Regex ClassAttribute = new(
        JsRegex.WhitespaceClass + "class" + JsRegex.WhitespaceClass + "*=" + JsRegex.WhitespaceClass + "*(?:\"([^\"]*)\"|'([^']*)')",
        RegexOptions.Compiled);

    /// <summary>The JS <c>/\s+/</c> used to split a class attribute into names.</summary>
    private static readonly Regex ScopeSeparator = new(JsRegex.WhitespaceClass + "+", RegexOptions.Compiled);

    private static readonly Lazy<Task> LoadAllLanguages = new(() => Highlighter.LoadAllLanguagesAsync());

    /// <summary>
    /// Swap in a real tokenizer. Mirrors the house <c>XxxOverride</c> seam pattern so tests (and a
    /// future highlight.js binding) can supply one without changing call sites.
    /// </summary>
    public static Func<IHighlighter>? HighlighterOverride { get; set; }

    private static IHighlighter Highlighter => HighlighterOverride?.Invoke() ?? PlainTextHighlighter.Instance;

    /// <summary>
    /// The TS <c>loadAllHighlightLanguages()</c>. Idempotent and cached, exactly as the original caches
    /// its promise — note that means a later <see cref="HighlighterOverride"/> does not re-trigger it.
    /// </summary>
    public static Task LoadAllLanguagesAsync() => LoadAllLanguages.Value;

    /// <summary><c>supportsLanguage(name)</c>.</summary>
    public static bool SupportsLanguage(string name) => Highlighter.SupportsLanguage(name);

    /// <summary><c>highlight(code, options)</c>: tokenize, then rewrite the spans into theme formatters.</summary>
    public static string Highlight(string code, HighlightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        options ??= new HighlightOptions();

        // JS `options.language ? … : …` is a truthiness test, so an empty string takes the auto path.
        var html = string.IsNullOrEmpty(options.Language)
            ? Highlighter.HighlightAuto(code, options.LanguageSubset)
            : Highlighter.Highlight(code, options.Language, options.IgnoreIllegals ?? false);

        return RenderHighlightedHtml(html, options.Theme);
    }

    /// <summary>
    /// <c>renderHighlightedHtml(html, theme)</c>: walk the highlighted HTML, tracking the stack of
    /// open <c>hljs-*</c> scopes, and hand each run of text to the innermost formatter that matches.
    /// </summary>
    /// <remarks>
    /// Three details are load-bearing and easy to get wrong: the text is split on <c>\n</c> and each
    /// <em>non-empty</em> line is formatted separately (so an empty line is left bare rather than
    /// wrapped); entities are decoded on the way out, which undoes highlight.js's escaping; and a
    /// <c>&lt;span</c> that is not followed by a tag delimiter is treated as ordinary text.
    /// </remarks>
    public static string RenderHighlightedHtml(string html, IReadOnlyDictionary<string, HighlightFormatter>? theme = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        theme ??= NoTheme;

        var output = new StringBuilder(html.Length);
        var text = new StringBuilder();
        var scopes = new List<string?>();

        void FlushText()
        {
            if (text.Length == 0)
            {
                return;
            }

            var formatter = GetActiveFormatter(scopes, theme);
            var buffered = text.ToString();
            if (formatter is null)
            {
                output.Append(buffered);
            }
            else
            {
                var lines = buffered.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (index > 0)
                    {
                        output.Append('\n');
                    }

                    output.Append(lines[index].Length != 0 ? formatter(lines[index]) : lines[index]);
                }
            }

            text.Clear();
        }

        var position = 0;
        while (position < html.Length)
        {
            if (IsSpanOpenTagStart(html, position))
            {
                var tagEnd = html.IndexOf('>', position + SpanOpen.Length);
                if (tagEnd != -1)
                {
                    FlushText();
                    scopes.Add(GetScopeFromSpanTag(html[position..(tagEnd + 1)]));
                    position = tagEnd + 1;
                    continue;
                }
            }

            if (html.AsSpan(position).StartsWith(SpanClose, StringComparison.Ordinal))
            {
                FlushText();
                if (scopes.Count > 0)
                {
                    scopes.RemoveAt(scopes.Count - 1);
                }

                position += SpanClose.Length;
                continue;
            }

            if (html[position] == '&'
                && Html.DecodeHtmlEntityAt(html, position) is { } decoded)
            {
                text.Append(decoded.Text);
                position += decoded.Length;
                continue;
            }

            text.Append(html[position]);
            position++;
        }

        FlushText();
        return output.ToString();
    }

    /// <summary>The <c>hljs-*</c> scope of a span's opening tag, or <see langword="null"/> if it has none.</summary>
    private static string? GetScopeFromSpanTag(string tag)
    {
        var match = ClassAttribute.Match(tag);
        var classValue = match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
        if (string.IsNullOrEmpty(classValue))
        {
            return null;
        }

        foreach (var className in ScopeSeparator.Split(classValue))
        {
            if (className.StartsWith(HighlightClassPrefix, StringComparison.Ordinal))
            {
                return className[HighlightClassPrefix.Length..];
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve a scope: an exact entry wins, otherwise a <c>.</c>-delimited prefix, otherwise a
    /// <c>-</c>-delimited one. Both fallbacks matter — highlight.js emits scopes like
    /// <c>title.function</c> and <c>title-class</c> that a theme usually only defines as <c>title</c>.
    /// </summary>
    private static HighlightFormatter? GetScopeFormatter(string scope, IReadOnlyDictionary<string, HighlightFormatter> theme)
    {
        if (theme.TryGetValue(scope, out var exact) && exact is not null)
        {
            return exact;
        }

        var dot = scope.IndexOf('.');
        if (dot != -1 && theme.TryGetValue(scope[..dot], out var dotPrefix) && dotPrefix is not null)
        {
            return dotPrefix;
        }

        var dash = scope.IndexOf('-');
        if (dash != -1 && theme.TryGetValue(scope[..dash], out var dashPrefix) && dashPrefix is not null)
        {
            return dashPrefix;
        }

        return null;
    }

    /// <summary>
    /// The formatter for the current position: the innermost scope that resolves, else the theme's
    /// <c>default</c>. Scopes are walked from the innermost outwards because a nested scope must win.
    /// </summary>
    private static HighlightFormatter? GetActiveFormatter(List<string?> scopes, IReadOnlyDictionary<string, HighlightFormatter> theme)
    {
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            var scope = scopes[index];
            if (string.IsNullOrEmpty(scope))
            {
                continue;
            }

            if (GetScopeFormatter(scope, theme) is { } formatter)
            {
                return formatter;
            }
        }

        return theme.TryGetValue(DefaultScope, out var fallback) ? fallback : null;
    }

    /// <summary>
    /// Whether a <c>&lt;span</c> at <paramref name="position"/> actually opens a tag. The next character
    /// must be a tag delimiter, so <c>&lt;spanner&gt;</c> stays text. A <c>&lt;span</c> at the very end
    /// of the string reads as <c>undefined</c> in JS and therefore does not open one.
    /// </summary>
    private static bool IsSpanOpenTagStart(string html, int position)
    {
        if (!html.AsSpan(position).StartsWith(SpanOpen, StringComparison.Ordinal))
        {
            return false;
        }

        var next = position + SpanOpen.Length;
        return next < html.Length && html[next] is '>' or ' ' or '\t' or '\n' or '\r';
    }
}
