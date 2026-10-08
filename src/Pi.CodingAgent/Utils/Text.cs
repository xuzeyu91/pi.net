namespace Pi.CodingAgent.Utils;

/// <summary>The leading UTF-8 byte order mark split off decoded text (port of <c>utils/text.ts</c>).</summary>
/// <param name="Bom">The BOM itself, or the empty string when there was none.</param>
/// <param name="Text">The text without the BOM.</param>
public readonly record struct BomSplit(string Bom, string Text);

/// <summary>Port of <c>utils/text.ts</c>.</summary>
public static class Text
{
    private const string BomChar = "\uFEFF";

    /// <summary>Split a leading UTF-8 byte order mark from decoded text.</summary>
    public static BomSplit SplitBom(string content) =>
        content.StartsWith(BomChar, StringComparison.Ordinal)
            ? new BomSplit(BomChar, content[1..])
            : new BomSplit(string.Empty, content);

    /// <summary>Remove a leading UTF-8 byte order mark from decoded text.</summary>
    public static string StripBom(string content) => SplitBom(content).Text;
}
