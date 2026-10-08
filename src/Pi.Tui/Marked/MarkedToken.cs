namespace Pi.Tui.Marked;

/// <summary>
/// A single markdown token (port of marked's structural token objects).
///
/// marked tokens are plain structural objects whose shape depends on <see cref="Type"/>, and
/// <c>components/markdown.ts</c> probes them with <c>"text" in token</c> style checks. One class with
/// optional members mirrors that directly; a class hierarchy would have to invent a common base for
/// every combination the renderer touches.
/// </summary>
public sealed class MarkedToken
{
    public required string Type { get; set; }

    public string Raw { get; set; } = "";

    public string? Text { get; set; }

    /// <summary>Heading depth (1-6).</summary>
    public int? Depth { get; set; }

    /// <summary>Fenced code block language.</summary>
    public string? Lang { get; set; }

    /// <summary>Set to "indented" for indented code blocks; fenced blocks leave it null.</summary>
    public string? CodeBlockStyle { get; set; }

    public List<MarkedToken>? Tokens { get; set; }

    // list

    public bool? Ordered { get; set; }

    /// <summary>
    /// First number of an ordered list. Null means "not a number", which is what an unordered list
    /// carries in TS (<c>start: ""</c>).
    /// </summary>
    public int? Start { get; set; }

    public bool? Loose { get; set; }

    public List<MarkedToken>? Items { get; set; }

    // list item

    public bool? Task { get; set; }

    public bool? Checked { get; set; }

    // html

    public bool? Block { get; set; }

    public bool? Pre { get; set; }

    public bool? InLink { get; set; }

    public bool? InRawBlock { get; set; }

    // definition / link / image

    public string? Tag { get; set; }

    public string? Href { get; set; }

    public string? Title { get; set; }

    // table

    /// <summary>Table header cells (only on the table token).</summary>
    public List<MarkedToken>? Header { get; set; }

    /// <summary>
    /// Per-column alignment; entries are null when the column is unaligned. Only on the table token.
    /// </summary>
    public List<string?>? Align { get; set; }

    /// <summary>Table body rows (only on the table token).</summary>
    public List<List<MarkedToken>>? Rows { get; set; }

    /// <summary>
    /// Set on a tablecell: true for header cells, false for body cells (marked's
    /// <c>token.header</c>).
    /// </summary>
    public bool? CellIsHeader { get; set; }

    /// <summary>
    /// Per-cell alignment string, copied from the table's <see cref="Align"/> (marked's
    /// <c>token.align</c> on a tablecell).
    /// </summary>
    public string? CellAlign { get; set; }

    // text

    public bool? Escaped { get; set; }

    // latex extension

    /// <summary>True when the source ends inside an unclosed math delimiter.</summary>
    public bool? Pending { get; set; }
}

/// <summary>A link definition collected by the <c>def</c> tokenizer (port of marked's <c>tokens.links</c>).</summary>
public sealed class MarkedLinkDefinition
{
    public required string Href { get; init; }

    public string? Title { get; init; }
}
