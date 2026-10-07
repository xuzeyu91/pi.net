using System.Text.RegularExpressions;

namespace Pi.Tui.Markdown;

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
    public MarkedLexer(MarkedOptions? options = null)
    {
        Options = options ?? new MarkedOptions();
        Tokens = [];
        InlineQueue = [];
        State = new MarkedLexerState();
        Tokenizer = new MarkedTokenizer(Options, MarkedRules.CreateDefault()) { Lexer = this };
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
        src = src.Replace("\r", "\n");
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

            if (token is null && State.Top)
            {
                token = Tokenizer.Paragraph(src);
                if (token is not null)
                {
                    src = src[token.Raw.Length..];
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

                    // marked tracks whether the paragraph was clipped by a startBlock extension.
                    // Without extensions this is always false, so the flag stays false.
                    lastParagraphClipped = false;
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
                        + JsString.Slice(masked, reflinkSearch.LastIndex, 0);
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
                + JsString.Slice(masked, anyPunctuation.LastIndex, 0);
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
                + JsString.Slice(masked, blockSkip.LastIndex, 0);
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

            if (token is null)
            {
                token = Tokenizer.InlineText(src);
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
