using System.Text.RegularExpressions;

namespace Pi.Tui.Markdown;

/// <summary>Options that change tokenization (port of the marked options markdown.ts relies on).</summary>
public sealed class MarkedOptions
{
    public bool Gfm { get; set; } = true;

    public bool Pedantic { get; set; }

    public bool Breaks { get; set; }
}

/// <summary>
/// Emulates a JS global regex's <c>lastIndex</c> state, which marked's inline tokenizer depends on:
/// several loops keep matching against a string that the loop body itself rewrites.
/// </summary>
public sealed class JsGlobalRegex
{
    private readonly Regex _regex;

    public JsGlobalRegex(Regex regex) => _regex = regex;

    public int LastIndex { get; set; }

    public Match? Exec(string input)
    {
        if (LastIndex > input.Length)
        {
            LastIndex = 0;
            return null;
        }

        var match = _regex.Match(input, LastIndex);
        if (!match.Success)
        {
            // JS resets lastIndex when exec returns null.
            LastIndex = 0;
            return null;
        }

        LastIndex = match.Index + match.Length;
        return match;
    }
}

/// <summary>
/// Port of marked v18.0.5's <c>Tokenizer</c>. Only the gfm rule set is wired up, because
/// <c>components/markdown.ts</c> runs marked with its default options.
/// </summary>
public sealed class MarkedTokenizer
{
    private static readonly Regex StrictStrikethroughRegex = new(
        @"^(~~)(?=[^\s~])((?:\\.|[^\\])*?(?:\\.|[^\s~\\]))\1(?=[^~]|$)");

    public MarkedTokenizer(MarkedOptions options, MarkedRules rules)
    {
        Options = options;
        Rules = rules;
    }

    public MarkedOptions Options { get; }

    public MarkedRules Rules { get; }

    /// <summary>Set by the lexer that owns this tokenizer (marked assigns it in both constructors).</summary>
    public MarkedLexer Lexer { get; set; } = null!;

    // ------------------------------------------------------------------
    // Block level
    // ------------------------------------------------------------------

    public MarkedToken? Space(string src)
    {
        var match = Rules.Block.Newline.Match(src);
        if (match.Success && match.Value.Length > 0)
        {
            return new MarkedToken { Type = "space", Raw = match.Value };
        }

        return null;
    }

    public MarkedToken? Code(string src)
    {
        var match = Rules.Block.Code.Match(src);
        if (match.Success)
        {
            var raw = Options.Pedantic ? match.Value : MarkedText.TrimBlankLines(match.Value);
            var text = Rules.Other.CodeRemoveIndent.Replace(raw, "");
            return new MarkedToken
            {
                Type = "code",
                Raw = raw,
                CodeBlockStyle = "indented",
                Text = text,
            };
        }

        return null;
    }

    public MarkedToken? Fences(string src)
    {
        var match = Rules.Block.Fences.Match(src);
        if (match.Success)
        {
            var raw = match.Value;
            var text = IndentCodeCompensation(raw, match.Groups[3].Value, Rules);
            var langGroup = match.Groups[2].Value;
            var lang = langGroup.Length > 0
                ? Rules.Inline.AnyPunctuation.Replace(JsString.Trim(langGroup), "$1")
                : langGroup;
            return new MarkedToken { Type = "code", Raw = raw, Lang = lang, Text = text };
        }

        return null;
    }

    public MarkedToken? Heading(string src)
    {
        var match = Rules.Block.Heading.Match(src);
        if (match.Success)
        {
            var text = JsString.Trim(match.Groups[2].Value);
            if (Rules.Other.EndingHash.IsMatch(text))
            {
                var trimmed = MarkedText.TrimTrailing(text, '#');
                if (Options.Pedantic || trimmed.Length == 0 || Rules.Other.EndingSpaceChar.IsMatch(trimmed))
                {
                    text = JsString.Trim(trimmed);
                }
            }

            return new MarkedToken
            {
                Type = "heading",
                Raw = MarkedText.TrimTrailing(match.Value, '\n'),
                Depth = match.Groups[1].Value.Length,
                Text = text,
                Tokens = Lexer.Inline(text),
            };
        }

        return null;
    }

    public MarkedToken? Hr(string src)
    {
        var match = Rules.Block.Hr.Match(src);
        if (match.Success)
        {
            return new MarkedToken { Type = "hr", Raw = MarkedText.TrimTrailing(match.Value, '\n') };
        }

        return null;
    }

    public MarkedToken? Blockquote(string src)
    {
        var match = Rules.Block.Blockquote.Match(src);
        if (!match.Success)
        {
            return null;
        }

        var lines = MarkedText.TrimTrailing(match.Value, '\n').Split('\n').ToList();
        var raw = "";
        var text = "";
        var tokens = new List<MarkedToken>();

        while (lines.Count > 0)
        {
            var continuation = false;
            var kept = new List<string>();
            var consumed = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                if (Rules.Other.BlockquoteStart.IsMatch(lines[i]))
                {
                    kept.Add(lines[i]);
                    continuation = true;
                }
                else if (!continuation)
                {
                    kept.Add(lines[i]);
                }
                else
                {
                    break;
                }

                consumed = i + 1;
            }

            lines = lines[consumed..];
            var block = string.Join('\n', kept);
            var stripped = Rules.Other.BlockquoteSetextReplace.Replace(block, "\n    $1");
            stripped = Rules.Other.BlockquoteSetextReplace2.Replace(stripped, "");
            raw = raw.Length > 0 ? $"{raw}\n{block}" : block;
            text = text.Length > 0 ? $"{text}\n{stripped}" : stripped;

            var previousTop = Lexer.State.Top;
            Lexer.State.Top = true;
            Lexer.BlockTokens(stripped, tokens);
            Lexer.State.Top = previousTop;

            if (lines.Count == 0)
            {
                break;
            }

            var last = tokens.Count > 0 ? tokens[^1] : null;
            if (last?.Type == "code")
            {
                break;
            }

            if (last?.Type == "blockquote")
            {
                var merged = Blockquote(last.Raw + "\n" + string.Join('\n', lines))!;
                tokens[^1] = merged;
                raw = raw[..(raw.Length - last.Raw.Length)] + merged.Raw;
                text = text[..(text.Length - last.Text!.Length)] + merged.Text!;
                break;
            }

            if (last?.Type == "list")
            {
                var remaining = last.Raw + "\n" + string.Join('\n', lines);
                var merged = List(remaining)!;
                tokens[^1] = merged;
                raw = raw[..(raw.Length - last.Raw.Length)] + merged.Raw;
                text = text[..(text.Length - last.Raw.Length)] + merged.Raw;
                lines = remaining[merged.Raw.Length..].Split('\n').ToList();
                continue;
            }
        }

        return new MarkedToken { Type = "blockquote", Raw = raw, Tokens = tokens, Text = text };
    }

    public MarkedToken? List(string src)
    {
        var match = Rules.Block.List.Match(src);
        if (!match.Success)
        {
            return null;
        }

        var bullet = JsString.Trim(match.Groups[1].Value);
        var ordered = bullet.Length > 1;
        var list = new MarkedToken
        {
            Type = "list",
            Raw = "",
            Ordered = ordered,
            Start = ordered ? int.Parse(bullet[..^1]) : null,
            Loose = false,
            Items = [],
        };

        var bulletPattern = ordered
            ? $"\\d{{1,9}}\\${bullet[^1]}"
            : $"\\{bullet}";
        if (Options.Pedantic)
        {
            bulletPattern = ordered ? bulletPattern : "[*+-]";
        }

        var itemRegex = Rules.Other.ListItemRegex(bulletPattern);
        var previousHadBlank = false;

        while (src.Length > 0)
        {
            var atEnd = false;
            var itemRaw = "";
            var itemText = "";

            match = itemRegex.Match(src);
            if (!match.Success || Rules.Block.Hr.IsMatch(src))
            {
                break;
            }

            itemRaw = match.Value;
            src = src[itemRaw.Length..];

            var expanded = MarkedText.ExpandTabs(
                MarkedText.FirstLine(match.Groups[2].Value),
                match.Groups[1].Value.Length);
            var nextLine = MarkedText.FirstLine(src);
            var blank = JsString.Trim(expanded).Length == 0;
            var indent = 0;

            if (Options.Pedantic)
            {
                indent = 2;
                itemText = JsString.TrimStart(expanded);
            }
            else if (blank)
            {
                indent = match.Groups[1].Value.Length + 1;
            }
            else
            {
                indent = MarkedText.Search(expanded, Rules.Other.NonSpaceChar);
                indent = indent > 4 ? 1 : indent;
                itemText = expanded[indent..];
                indent += match.Groups[1].Value.Length;
            }

            if (blank && Rules.Other.BlankLine.IsMatch(nextLine))
            {
                itemRaw += nextLine + "\n";
                src = src[(nextLine.Length + 1)..];
                atEnd = true;
            }

            if (!atEnd)
            {
                var nextBullet = Rules.Other.NextBulletRegex(indent);
                var hr = Rules.Other.HrRegex(indent);
                var fencesBegin = Rules.Other.FencesBeginRegex(indent);
                var headingBegin = Rules.Other.HeadingBeginRegex(indent);
                var htmlBegin = Rules.Other.HtmlBeginRegex(indent);
                var blockquoteBegin = Rules.Other.BlockquoteBeginRegex(indent);

                while (src.Length > 0)
                {
                    var line = MarkedText.FirstLine(src);
                    nextLine = line;
                    var normalized = Options.Pedantic
                        ? Rules.Other.ListReplaceNesting.Replace(line, "  ")
                        : line.Replace("\t", "    ");

                    if (fencesBegin.IsMatch(line) || headingBegin.IsMatch(line) || htmlBegin.IsMatch(line) ||
                        blockquoteBegin.IsMatch(line) || nextBullet.IsMatch(line) || hr.IsMatch(line))
                    {
                        break;
                    }

                    if (MarkedText.Search(normalized, Rules.Other.NonSpaceChar) >= indent ||
                        JsString.Trim(line).Length == 0)
                    {
                        itemText += "\n" + normalized[indent..];
                    }
                    else
                    {
                        if (blank ||
                            MarkedText.Search(expanded.Replace("\t", "    "), Rules.Other.NonSpaceChar) >= 4 ||
                            fencesBegin.IsMatch(expanded) || headingBegin.IsMatch(expanded) || hr.IsMatch(expanded))
                        {
                            break;
                        }

                        itemText += "\n" + line;
                    }

                    blank = JsString.Trim(line).Length == 0;
                    itemRaw += line + "\n";
                    src = src[(line.Length + 1)..];
                    expanded = normalized[indent..];
                }
            }

            if (list.Loose != true)
            {
                if (previousHadBlank)
                {
                    list.Loose = true;
                }
                else if (Rules.Other.DoubleBlankLine.IsMatch(itemRaw))
                {
                    previousHadBlank = true;
                }
            }

            list.Items!.Add(new MarkedToken
            {
                Type = "list_item",
                Raw = itemRaw,
                Task = Options.Gfm && Rules.Other.ListIsTask.IsMatch(itemText),
                Loose = false,
                Text = itemText,
                Tokens = [],
            });
            list.Raw += itemRaw;
        }

        var lastItem = list.Items!.Count > 0 ? list.Items[^1] : null;
        if (lastItem is null)
        {
            return null;
        }

        lastItem.Raw = JsString.TrimEnd(lastItem.Raw);
        lastItem.Text = JsString.TrimEnd(lastItem.Text!);
        list.Raw = JsString.TrimEnd(list.Raw);

        foreach (var item in list.Items)
        {
            Lexer.State.Top = false;
            Lexer.BlockTokens(item.Text!, item.Tokens!);
            var first = item.Tokens!.Count > 0 ? item.Tokens[0] : null;

            if (item.Task == true && (first?.Type == "text" || first?.Type == "paragraph"))
            {
                item.Text = Rules.Other.ListReplaceTask.Replace(item.Text!, "");
                first!.Raw = Rules.Other.ListReplaceTask.Replace(first.Raw, "");
                first!.Text = Rules.Other.ListReplaceTask.Replace(first.Text!, "");

                for (var k = Lexer.InlineQueue.Count - 1; k >= 0; k--)
                {
                    if (Rules.Other.ListIsTask.IsMatch(Lexer.InlineQueue[k].Src))
                    {
                        Lexer.InlineQueue[k].Src = Rules.Other.ListReplaceTask.Replace(
                            Lexer.InlineQueue[k].Src,
                            "");
                        break;
                    }
                }

                var checkboxMatch = Rules.Other.ListTaskCheckbox.Match(item.Raw);
                if (checkboxMatch.Success)
                {
                    var checkbox = new MarkedToken
                    {
                        Type = "checkbox",
                        Raw = checkboxMatch.Value + " ",
                        Checked = checkboxMatch.Value != "[ ]",
                    };
                    item.Checked = checkbox.Checked;

                    if (list.Loose == true)
                    {
                        var head = item.Tokens.Count > 0 ? item.Tokens[0] : null;
                        if (head is not null &&
                            (head.Type == "paragraph" || head.Type == "text") &&
                            head.Tokens is { Count: > 0 })
                        {
                            head.Raw = checkbox.Raw + head.Raw;
                            head.Text = checkbox.Raw + head.Text;
                            head.Tokens!.Insert(0, checkbox);
                        }
                        else
                        {
                            item.Tokens.Insert(0, new MarkedToken
                            {
                                Type = "paragraph",
                                Raw = checkbox.Raw,
                                Text = checkbox.Raw,
                                Tokens = [checkbox],
                            });
                        }
                    }
                    else
                    {
                        item.Tokens.Insert(0, checkbox);
                    }
                }
            }
            else if (item.Task == true)
            {
                item.Task = false;
            }

            if (list.Loose != true)
            {
                var spaces = item.Tokens.Where(t => t.Type == "space").ToList();
                list.Loose = spaces.Count > 0 && spaces.Any(t => Rules.Other.AnyLine.IsMatch(t.Raw));
            }
        }

        if (list.Loose == true)
        {
            foreach (var item in list.Items)
            {
                item.Loose = true;
                foreach (var token in item.Tokens!)
                {
                    if (token.Type == "text")
                    {
                        token.Type = "paragraph";
                    }
                }
            }
        }

        return list;
    }

    public MarkedToken? Html(string src)
    {
        var match = Rules.Block.Html.Match(src);
        if (match.Success)
        {
            var raw = MarkedText.TrimBlankLines(match.Value);
            return new MarkedToken
            {
                Type = "html",
                Block = true,
                Raw = raw,
                Pre = match.Groups[1].Value is "pre" or "script" or "style",
                Text = raw,
            };
        }

        return null;
    }

    public MarkedToken? Def(string src)
    {
        var match = Rules.Block.Def.Match(src);
        if (match.Success)
        {
            var tag = Rules.Other.MultipleSpaceGlobal.Replace(
                JsString.ToLowerCase(match.Groups[1].Value),
                " ");
            var hrefGroup = match.Groups[2].Value;
            var href = hrefGroup.Length > 0
                ? Rules.Inline.AnyPunctuation.Replace(
                    Rules.Other.HrefBrackets.Replace(hrefGroup, "$1"),
                    "$1")
                : "";
            var titleGroup = match.Groups[3].Value;
            var title = titleGroup.Length > 0
                ? Rules.Inline.AnyPunctuation.Replace(JsString.Slice(titleGroup, 1, -1), "$1")
                : titleGroup;

            return new MarkedToken
            {
                Type = "def",
                Tag = tag,
                Raw = MarkedText.TrimTrailing(match.Value, '\n'),
                Href = href,
                Title = title,
            };
        }

        return null;
    }

    public MarkedToken? Table(string src)
    {
        var match = Rules.Block.Table.Match(src);
        if (!match.Success || !Rules.Other.TableDelimiter.IsMatch(match.Groups[2].Value))
        {
            return null;
        }

        var header = MarkedText.SplitTableRow(match.Groups[1].Value, null);
        var align = Rules.Other.TableAlignChars.Replace(match.Groups[2].Value, "").Split('|').ToList();
        var rows = JsString.Trim(match.Groups[3].Value).Length > 0
            ? Rules.Other.TableRowBlankLine.Replace(match.Groups[3].Value, "").Split('\n').ToList()
            : new List<string>();

        var table = new MarkedToken
        {
            Type = "table",
            Raw = MarkedText.TrimTrailing(match.Value, '\n'),
            Header = [],
            Align = [],
            Rows = [],
        };

        if (header.Count != align.Count)
        {
            return null;
        }

        foreach (var cell in align)
        {
            if (Rules.Other.TableAlignRight.IsMatch(cell))
            {
                table.Align!.Add("right");
            }
            else if (Rules.Other.TableAlignCenter.IsMatch(cell))
            {
                table.Align!.Add("center");
            }
            else if (Rules.Other.TableAlignLeft.IsMatch(cell))
            {
                table.Align!.Add("left");
            }
            else
            {
                table.Align!.Add(null);
            }
        }

        for (var i = 0; i < header.Count; i++)
        {
            table.Header!.Add(new MarkedToken
            {
                Type = "tablecell",
                Text = header[i],
                Tokens = Lexer.Inline(header[i]),
                CellIsHeader = true,
                CellAlign = table.Align![i],
            });
        }

        foreach (var row in rows)
        {
            var cells = MarkedText.SplitTableRow(row, table.Header!.Count);
            table.Rows!.Add(cells.Select((cell, i) => new MarkedToken
            {
                Type = "tablecell",
                Text = cell,
                Tokens = Lexer.Inline(cell),
                CellIsHeader = false,
                CellAlign = table.Align![i],
            }).ToList());
        }

        return table;
    }

    public MarkedToken? Lheading(string src)
    {
        var match = Rules.Block.Lheading.Match(src);
        if (match.Success)
        {
            var text = JsString.Trim(match.Groups[1].Value);
            return new MarkedToken
            {
                Type = "heading",
                Raw = MarkedText.TrimTrailing(match.Value, '\n'),
                Depth = match.Groups[2].Value[0] == '=' ? 1 : 2,
                Text = text,
                Tokens = Lexer.Inline(text),
            };
        }

        return null;
    }

    public MarkedToken? Paragraph(string src)
    {
        var match = Rules.Block.Paragraph.Match(src);
        if (match.Success)
        {
            var text = match.Groups[1].Value;
            if (text.Length > 0 && text[^1] == '\n')
            {
                text = text[..^1];
            }

            return new MarkedToken
            {
                Type = "paragraph",
                Raw = match.Value,
                Text = text,
                Tokens = Lexer.Inline(text),
            };
        }

        return null;
    }

    public MarkedToken? Text(string src)
    {
        var match = Rules.Block.Text.Match(src);
        if (match.Success)
        {
            return new MarkedToken
            {
                Type = "text",
                Raw = match.Value,
                Text = match.Value,
                Tokens = Lexer.Inline(match.Value),
            };
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Inline level
    // ------------------------------------------------------------------

    public MarkedToken? Escape(string src)
    {
        var match = Rules.Inline.Escape.Match(src);
        if (match.Success)
        {
            return new MarkedToken { Type = "escape", Raw = match.Value, Text = match.Groups[1].Value };
        }

        return null;
    }

    public MarkedToken? Tag(string src)
    {
        var match = Rules.Inline.Tag.Match(src);
        if (!match.Success)
        {
            return null;
        }

        if (!Lexer.State.InLink && Rules.Other.StartATag.IsMatch(match.Value))
        {
            Lexer.State.InLink = true;
        }
        else if (Lexer.State.InLink && Rules.Other.EndATag.IsMatch(match.Value))
        {
            Lexer.State.InLink = false;
        }

        if (!Lexer.State.InRawBlock && Rules.Other.StartPreScriptTag.IsMatch(match.Value))
        {
            Lexer.State.InRawBlock = true;
        }
        else if (Lexer.State.InRawBlock && Rules.Other.EndPreScriptTag.IsMatch(match.Value))
        {
            Lexer.State.InRawBlock = false;
        }

        return new MarkedToken
        {
            Type = "html",
            Raw = match.Value,
            InLink = Lexer.State.InLink,
            InRawBlock = Lexer.State.InRawBlock,
            Block = false,
            Text = match.Value,
        };
    }

    public MarkedToken? Link(string src)
    {
        var match = Rules.Inline.Link.Match(src);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Value;
        var hrefGroup = match.Groups[2].Value;
        var titleGroup = match.Groups[3].Value;
        var href = JsString.Trim(hrefGroup);

        if (!Options.Pedantic && Rules.Other.StartAngleBracket.IsMatch(href))
        {
            if (!Rules.Other.EndAngleBracket.IsMatch(href))
            {
                return null;
            }

            var unescaped = MarkedText.TrimTrailing(JsString.Slice(href, 0, -1), '\\');
            if ((href.Length - unescaped.Length) % 2 == 0)
            {
                return null;
            }
        }
        else
        {
            var closing = MarkedText.FindClosingBracket(hrefGroup, "()");
            if (closing == -2)
            {
                return null;
            }

            if (closing > -1)
            {
                var offset = (match.Value.StartsWith('!') ? 5 : 4) + match.Groups[1].Value.Length + closing;
                hrefGroup = hrefGroup[..closing];
                raw = JsString.Trim(match.Value[..offset]);
                titleGroup = "";
            }
        }

        var title = "";
        if (Options.Pedantic)
        {
            var pedanticMatch = Rules.Other.PedanticHrefTitle.Match(JsString.Trim(hrefGroup));
            if (pedanticMatch.Success)
            {
                hrefGroup = pedanticMatch.Groups[1].Value;
                title = pedanticMatch.Groups[3].Value;
            }
        }
        else
        {
            title = titleGroup.Length > 0 ? JsString.Slice(titleGroup, 1, -1) : "";
        }

        href = JsString.Trim(hrefGroup);
        if (Rules.Other.StartAngleBracket.IsMatch(href))
        {
            href = Options.Pedantic && !Rules.Other.EndAngleBracket.IsMatch(match.Groups[2].Value)
                ? href[1..]
                : JsString.Slice(href, 1, -1);
        }

        return BuildLinkToken(match.Groups[1].Value, href, title, raw);
    }

    public MarkedToken? Reflink(string src, Dictionary<string, MarkedLinkDefinition> links)
    {
        var match = Rules.Inline.Reflink.Match(src);
        match ??= Rules.Inline.Nolink.Match(src);
        if (!match.Success)
        {
            return null;
        }

        var label = Rules.Other.MultipleSpaceGlobal.Replace(
            match.Groups[2].Value.Length > 0 ? match.Groups[2].Value : match.Groups[1].Value,
            " ");
        if (!links.TryGetValue(JsString.ToLowerCase(label), out var definition))
        {
            var first = match.Value.Length > 0 ? match.Value[0].ToString() : "";
            return new MarkedToken { Type = "text", Raw = first, Text = first };
        }

        return BuildLinkToken(match.Groups[1].Value, definition.Href, definition.Title ?? "", match.Value);
    }

    public MarkedToken? EmStrong(string src, string masked, string previous = "")
    {
        var match = Rules.Inline.EmStrongLDelim.Match(src);
        if (!match.Success)
        {
            return null;
        }

        var g1 = match.Groups[1].Value;
        var g2 = match.Groups[2].Value;
        var g3 = match.Groups[3].Value;
        var g4 = match.Groups[4].Value;
        if (g1.Length == 0 && g2.Length == 0 && g3.Length == 0 && g4.Length == 0)
        {
            return null;
        }

        if (g4.Length > 0 && Rules.Other.UnicodeAlphaNumeric.IsMatch(previous))
        {
            return null;
        }

        if ((g1.Length > 0 || g3.Length > 0) && previous.Length > 0 &&
            !Rules.Inline.Punctuation.IsMatch(previous))
        {
            return null;
        }

        var length = JsString.CodePointLength(match.Value) - 1;
        var open = length;
        var skipped = 0;
        var right = match.Value[0] == '*'
            ? Rules.Inline.EmStrongRDelimAst
            : Rules.Inline.EmStrongRDelimUnd;

        // JS: t = t.slice(-1 * e.length + i)
        var search = JsString.Slice(masked, length - src.Length);
        var rightMatch = right.Match(search);
        while (rightMatch.Success)
        {
            var run = FirstGroup(rightMatch, 1, 2, 3, 4, 5, 6);
            if (run.Length == 0)
            {
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            var runLength = JsString.CodePointLength(run);
            if (rightMatch.Groups[3].Value.Length > 0 || rightMatch.Groups[4].Value.Length > 0)
            {
                open += runLength;
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            if ((rightMatch.Groups[5].Value.Length > 0 || rightMatch.Groups[6].Value.Length > 0) &&
                length % 3 != 0 && (length + runLength) % 3 == 0)
            {
                skipped += runLength;
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            open -= runLength;
            if (open > 0)
            {
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            runLength = Math.Min(runLength, runLength + open + skipped);
            var firstCharLength = MarkedText.FirstCodePointLength(rightMatch.Value);
            var whole = JsString.Slice(src, 0, length + rightMatch.Index + firstCharLength + runLength);
            if (Math.Min(length, runLength) % 2 != 0)
            {
                var inner = JsString.Slice(whole, 1, -1);
                return new MarkedToken
                {
                    Type = "em",
                    Raw = whole,
                    Text = inner,
                    Tokens = Lexer.InlineTokens(inner, []),
                };
            }

            var strongInner = JsString.Slice(whole, 2, -2);
            return new MarkedToken
            {
                Type = "strong",
                Raw = whole,
                Text = strongInner,
                Tokens = Lexer.InlineTokens(strongInner, []),
            };
        }

        return null;
    }

    public MarkedToken? Codespan(string src)
    {
        var match = Rules.Inline.Code.Match(src);
        if (match.Success)
        {
            var text = match.Groups[2].Value.Replace("\n", " ");
            var hasNonSpace = Rules.Other.NonSpaceChar.IsMatch(text);
            var surrounded = Rules.Other.StartingSpaceChar.IsMatch(text) && Rules.Other.EndingSpaceChar.IsMatch(text);
            if (hasNonSpace && surrounded)
            {
                text = JsString.Slice(text, 1, -1);
            }

            return new MarkedToken { Type = "codespan", Raw = match.Value, Text = text };
        }

        return null;
    }

    public MarkedToken? Br(string src)
    {
        var match = Rules.Inline.Br.Match(src);
        if (match.Success)
        {
            return new MarkedToken { Type = "br", Raw = match.Value };
        }

        return null;
    }

    /// <summary>
    /// Strikethrough. marked's own <c>del</c> is fully shadowed here: <c>components/markdown.ts</c>
    /// installs a <c>StrictStrikethroughTokenizer</c> whose <c>del</c> never returns <c>false</c>, and
    /// marked's <c>use()</c> only falls back to the original when an override returns exactly
    /// <c>false</c>. So this is the strict regex, not marked's.
    /// </summary>
    public MarkedToken? Del(string src, string masked, string previous = "")
    {
        var match = StrictStrikethroughRegex.Match(src);
        if (!match.Success)
        {
            return null;
        }

        if (match.Groups[1].Value.Length == 0 && previous.Length > 0 &&
            !Rules.Inline.Punctuation.IsMatch(previous))
        {
            return null;
        }

        var length = JsString.CodePointLength(match.Value) - 1;
        var open = length;

        // JS: t = t.slice(-1 * e.length + i)
        var search = JsString.Slice(masked, length - src.Length);
        var rightMatch = Rules.Inline.DelRDelim.Match(search);
        while (rightMatch.Success)
        {
            var run = FirstGroup(rightMatch, 1, 2, 3, 4, 5, 6);
            if (run.Length == 0)
            {
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            var runLength = JsString.CodePointLength(run);
            if (runLength != length)
            {
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            if (rightMatch.Groups[3].Value.Length > 0 || rightMatch.Groups[4].Value.Length > 0)
            {
                open += runLength;
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            open -= runLength;
            if (open > 0)
            {
                rightMatch = rightMatch.NextMatch();
                continue;
            }

            runLength = Math.Min(runLength, runLength + open);
            var firstCharLength = MarkedText.FirstCodePointLength(rightMatch.Value);
            var whole = JsString.Slice(src, 0, length + rightMatch.Index + firstCharLength + runLength);
            var inner = JsString.Slice(whole, length, -length);
            return new MarkedToken
            {
                Type = "del",
                Raw = whole,
                Text = inner,
                Tokens = Lexer.InlineTokens(inner, []),
            };
        }

        return null;
    }

    public MarkedToken? Autolink(string src)
    {
        var match = Rules.Inline.Autolink.Match(src);
        if (match.Success)
        {
            var text = match.Groups[1].Value;
            var href = match.Groups[2].Value == "@" ? "mailto:" + text : text;
            return new MarkedToken
            {
                Type = "link",
                Raw = match.Value,
                Text = text,
                Href = href,
                Tokens = [new MarkedToken { Type = "text", Raw = text, Text = text }],
            };
        }

        return null;
    }

    public MarkedToken? Url(string src)
    {
        var match = Rules.Inline.Url.Match(src);
        if (!match.Success)
        {
            return null;
        }

        string text;
        string href;
        if (match.Groups[2].Value == "@")
        {
            text = match.Value;
            href = "mailto:" + text;
        }
        else
        {
            var current = match.Value;
            while (true)
            {
                var previous = current;
                var backpedal = Rules.Inline.Backpedal.Match(current);
                current = backpedal.Success ? backpedal.Value : "";
                if (previous == current)
                {
                    break;
                }
            }

            text = current;
            href = match.Groups[1].Value == "www." ? "http://" + current : current;
        }

        return new MarkedToken
        {
            Type = "link",
            Raw = text,
            Text = text,
            Href = href,
            Tokens = [new MarkedToken { Type = "text", Raw = text, Text = text }],
        };
    }

    public MarkedToken? InlineText(string src)
    {
        var match = Rules.Inline.Text.Match(src);
        if (match.Success)
        {
            return new MarkedToken
            {
                Type = "text",
                Raw = match.Value,
                Text = match.Value,
                Escaped = Lexer.State.InRawBlock,
            };
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Helpers ported from marked's module scope
    // ------------------------------------------------------------------

    /// <summary>JS <c>m[1] || m[2] || ...</c> over the numbered capture groups.</summary>
    private static string FirstGroup(Match match, params int[] groups)
    {
        foreach (var group in groups)
        {
            var value = match.Groups[group].Value;
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    /// <summary>Port of marked's <c>be()</c>: builds a link or image token.</summary>
    private MarkedToken BuildLinkToken(string label, string href, string title, string raw)
    {
        var text = Rules.Other.OutputLinkReplace.Replace(label, "$1");
        Lexer.State.InLink = true;
        var token = new MarkedToken
        {
            Type = raw.Length > 0 && raw[0] == '!' ? "image" : "link",
            Raw = raw,
            Href = href,
            Title = title.Length > 0 ? title : null,
            Text = text,
            Tokens = Lexer.InlineTokens(text, []),
        };
        Lexer.State.InLink = false;
        return token;
    }

    /// <summary>Port of marked's <c>ct()</c>: removes the indent shared by a fenced block's lines.</summary>
    private static string IndentCodeCompensation(string raw, string text, MarkedRules rules)
    {
        var match = rules.Other.IndentCodeCompensation.Match(raw);
        if (!match.Success)
        {
            return text;
        }

        var indent = match.Groups[1].Value;
        return string.Join('\n', text.Split('\n').Select(line =>
        {
            var leading = rules.Other.BeginningSpace.Match(line);
            if (!leading.Success)
            {
                return line;
            }

            var spaces = leading.Value;
            return spaces.Length >= indent.Length ? line[indent.Length..] : line;
        }));
    }
}
