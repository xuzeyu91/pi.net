using System.Text.RegularExpressions;

namespace Pi.Tui.Marked;

/// <summary>Port of marked's mutable lexer state, shared between the lexer and its tokenizer.</summary>
public sealed class MarkedLexerState
{
    public bool InLink { get; set; }

    public bool InRawBlock { get; set; }

    public bool Top { get; set; } = true;
}

/// <summary>One pending <c>inlineTokens</c> job (port of marked's <c>inlineQueue</c> entries).</summary>
public sealed class MarkedInlineJob
{
    public string Src { get; set; }

    public List<MarkedToken> Tokens { get; init; }

    public MarkedInlineJob(string src, List<MarkedToken> tokens)
    {
        Src = src;
        Tokens = tokens;
    }
}

/// <summary>
/// Port of marked v18.0.5's <c>Lexer</c>. Only the default rule selection is implemented, because
/// <c>components/markdown.ts</c> runs marked with its default options.
/// </summary>
public sealed class MarkedLexer
{
    /// <summary>marked's line-ending normalisation rule (<c>/\r\n|\r/g</c>).</summary>
    private static readonly Regex CarriageReturnRegex = new(@"\r\n|\r", RegexOptions.Compiled);

    public MarkedLexer(MarkedOptions? options = null)
    {
        Options = options ?? new MarkedOptions();
        Tokens = [];
        InlineQueue = [];
        State = new MarkedLexerState();
        // MarkedRules.Default is a shared instance: rebuilding the rule set per lexer would recreate
        // every RegexOptions.Compiled pattern, forcing .NET to re-JIT each one on first match (which
        // dominated render time). Regex matching and the memoised factories are thread-safe.
        Tokenizer = new MarkedTokenizer(Options, MarkedRules.Default) { Lexer = this };
    }

    public MarkedOptions Options { get; }

    public List<MarkedToken> Tokens { get; }

    /// <summary>Port of the <c>tokens.links</c> side table marked attaches to the token array.</summary>
    public Dictionary<string, MarkedLinkDefinition> Links { get; } = new(StringComparer.Ordinal);

    public List<MarkedInlineJob> InlineQueue { get; }

    public MarkedLexerState State { get; }

    public MarkedTokenizer Tokenizer { get; }

    public static List<MarkedToken> Lex(string src, MarkedOptions? options = null) =>
        new MarkedLexer(options).Lex(src);

    public static List<MarkedToken> LexInline(string src, MarkedOptions? options = null) =>
        new MarkedLexer(options).InlineTokens(src, []);

    public List<MarkedToken> Lex(string src)
    {
        // marked normalises line endings with `src.replace(/\r\n|\r/g, '\n')`; a plain
        // Replace("\r", "\n") would turn a CRLF into two newlines and split the paragraph.
        src = CarriageReturnRegex.Replace(src, "\n");
        BlockTokens(src, Tokens);
        foreach (var job in InlineQueue)
        {
            InlineTokens(job.Src, job.Tokens);
        }

        InlineQueue.Clear();
        return Tokens;
    }

    public void BlockTokens(string src, List<MarkedToken> tokens, bool lastParagraphClipped = false)
    {
        Tokenizer.Lexer = this;

        if (Options.Pedantic)
        {
            src = Tokenizer.Rules.Other.TabCharGlobal.Replace(src, "    ");
            src = Tokenizer.Rules.Other.SpaceLine.Replace(src, "");
        }

        // marked compares the remaining length against the previous one and throws when it stops
        // shrinking. Initialising with +Infinity (int.MaxValue) reproduces that, including the first
        // iteration where any length is smaller.
        var previousLength = int.MaxValue;
        while (src.Length > 0)
        {
            if (src.Length >= previousLength)
            {
                throw new InvalidOperationException($"Infinite loop on byte: {(int)src[0]}");
            }

            previousLength = src.Length;

            var last = tokens.Count > 0 ? tokens[^1] : null;
            MarkedToken? token = null;
            var extensions = Options.Extensions;

            // marked tries the registered block extensions before any built-in tokenizer:
            // extensions.block.some(o => (r = o.call({lexer}, src, tokens)) ? (src = src.substring(r.raw.length), tokens.push(r), true) : false)
            if (extensions is not null)
            {
                foreach (var extension in extensions.Block)
                {
                    token = extension(this, src, tokens);
                    if (token is not null)
                    {
                        src = src[token.Raw.Length..];
                        tokens.Add(token);
                        break;
                    }
                }

                if (token is not null)
                {
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Space(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (token.Raw.Length == 1 && last is not null)
                    {
                        // A single blank line just extends the previous token's raw text.
                        last.Raw += "\n";
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Code(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (last?.Type is "paragraph" or "text")
                    {
                        last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                        last.Text += "\n" + token.Text;
                        InlineQueue[^1].Src = last.Text!;
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Fences(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Heading(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Hr(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Blockquote(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.List(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Html(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Def(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (last?.Type is "paragraph" or "text")
                    {
                        last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                        last.Text += "\n" + token.Raw;
                        InlineQueue[^1].Src = last.Text!;
                    }
                    else if (!Links.ContainsKey(token.Tag!))
                    {
                        Links[token.Tag!] = new MarkedLinkDefinition
                        {
                            Href = token.Href ?? "",
                            Title = token.Title,
                        };
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Table(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Lheading(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            // marked truncates the source at the earliest startBlock extension hit before
            // running the paragraph tokenizer on it (the extensions themselves already had
            // their chance above and declined).
            var paragraphSrc = src;
            if (extensions is not null)
            {
                var start = int.MaxValue;
                foreach (var getStart in extensions.StartBlock)
                {
                    var index = getStart(this, JsString.Slice(src, 1));
                    if (index is >= 0)
                    {
                        start = Math.Min(start, index.Value);
                    }
                }

                if (start < int.MaxValue)
                {
                    paragraphSrc = JsString.Slice(src, 0, start + 1);
                }
            }

            if (token is null && State.Top)
            {
                token = Tokenizer.Paragraph(paragraphSrc);
                if (token is not null)
                {
                    if (lastParagraphClipped && last?.Type == "paragraph")
                    {
                        last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                        last.Text += "\n" + token.Text;
                        InlineQueue.RemoveAt(InlineQueue.Count - 1);
                        InlineQueue[^1].Src = last.Text!;
                    }
                    else
                    {
                        tokens.Add(token);
                    }

                    // marked records whether the startBlock extensions clipped the source this
                    // time; the next paragraph merges into the previous token when they did.
                    lastParagraphClipped = paragraphSrc.Length != src.Length;
                    src = src[token.Raw.Length..];
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Text(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (last?.Type == "text")
                    {
                        last.Raw += (last.Raw.EndsWith('\n') ? "" : "\n") + token.Raw;
                        last.Text += "\n" + token.Text;
                        InlineQueue.RemoveAt(InlineQueue.Count - 1);
                        InlineQueue[^1].Src = last.Text!;
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            throw new InvalidOperationException($"Infinite loop on byte: {(int)src[0]}");
        }

        State.Top = true;
    }

    public List<MarkedToken> Inline(string src)
    {
        var tokens = new List<MarkedToken>();
        InlineQueue.Add(new MarkedInlineJob(src, tokens));
        return tokens;
    }

    public List<MarkedToken> InlineTokens(string src, List<MarkedToken> tokens)
    {
        Tokenizer.Lexer = this;

        // Build the masked string: link labels and punctuation runs are replaced with filler so that
        // the em/strong delimiter search cannot match inside them.
        var masked = src;
        if (Links.Count > 0)
        {
            var reflinkSearch = new JsGlobalRegex(Tokenizer.Rules.Inline.ReflinkSearch);
            var match = reflinkSearch.Exec(masked);
            while (match is not null)
            {
                var whole = match.Value;
                var label = JsString.Slice(whole, whole.LastIndexOf('[') + 1, -1);
                if (Links.ContainsKey(label))
                {
                    masked = JsString.Slice(masked, 0, match.Index)
                        + "["
                        + new string('a', whole.Length - 2)
                        + "]"
                        + JsString.Slice(masked, reflinkSearch.LastIndex);
                }

                match = reflinkSearch.Exec(masked);
            }
        }

        var anyPunctuation = new JsGlobalRegex(Tokenizer.Rules.Inline.AnyPunctuation);
        var punct = anyPunctuation.Exec(masked);
        while (punct is not null)
        {
            masked = JsString.Slice(masked, 0, punct.Index)
                + "++"
                + JsString.Slice(masked, anyPunctuation.LastIndex);
            punct = anyPunctuation.Exec(masked);
        }

        var blockSkip = new JsGlobalRegex(Tokenizer.Rules.Inline.BlockSkip);
        var skip = blockSkip.Exec(masked);
        while (skip is not null)
        {
            var consumed = skip.Groups[2].Value.Length;
            masked = JsString.Slice(masked, 0, skip.Index + consumed)
                + "["
                + new string('a', skip.Value.Length - consumed - 2)
                + "]"
                + JsString.Slice(masked, blockSkip.LastIndex);
            skip = blockSkip.Exec(masked);
        }

        var cut = false;
        var previous = "";
        var previousLength = int.MaxValue;

        while (src.Length > 0)
        {
            if (src.Length >= previousLength)
            {
                throw new InvalidOperationException($"Infinite loop on byte: {(int)src[0]}");
            }

            previousLength = src.Length;

            if (!cut)
            {
                previous = "";
            }

            cut = false;
            MarkedToken? token = null;
            var extensions = Options.Extensions;

            // marked tries the registered inline extensions before any built-in tokenizer:
            // extensions.inline.some(p => (a = p.call({lexer}, src, tokens)) ? (src = src.substring(a.raw.length), tokens.push(a), true) : false)
            if (extensions is not null)
            {
                foreach (var extension in extensions.Inline)
                {
                    token = extension(this, src, tokens);
                    if (token is not null)
                    {
                        src = src[token.Raw.Length..];
                        tokens.Add(token);
                        break;
                    }
                }

                if (token is not null)
                {
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Escape(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Tag(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Link(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Reflink(src, Links);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (token.Type == "text" && tokens.Count > 0 && tokens[^1].Type == "text")
                    {
                        tokens[^1].Raw += token.Raw;
                        tokens[^1].Text += token.Text;
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.EmStrong(src, masked, previous);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Codespan(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Br(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Del(src, masked, previous);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null)
            {
                token = Tokenizer.Autolink(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            if (token is null && !State.InLink)
            {
                token = Tokenizer.Url(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    tokens.Add(token);
                    continue;
                }
            }

            // marked truncates the source at the earliest startInline extension hit before
            // running the inlineText tokenizer on it.
            var inlineTextSrc = src;
            if (extensions is not null)
            {
                var start = int.MaxValue;
                foreach (var getStart in extensions.StartInline)
                {
                    var index = getStart(this, JsString.Slice(src, 1));
                    if (index is >= 0)
                    {
                        start = Math.Min(start, index.Value);
                    }
                }

                if (start < int.MaxValue)
                {
                    inlineTextSrc = JsString.Slice(src, 0, start + 1);
                }
            }

            if (token is null)
            {
                token = Tokenizer.InlineText(inlineTextSrc);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
                    if (!token.Raw.EndsWith('_'))
                    {
                        previous = token.Raw[^1].ToString();
                    }

                    cut = true;
                    if (tokens.Count > 0 && tokens[^1].Type == "text")
                    {
                        tokens[^1].Raw += token.Raw;
                        tokens[^1].Text += token.Text;
                    }
                    else
                    {
                        tokens.Add(token);
                    }
                    continue;
                }
            }

            throw new InvalidOperationException($"Infinite loop on byte: {(int)src[0]}");
        }

        return tokens;
    }
}
