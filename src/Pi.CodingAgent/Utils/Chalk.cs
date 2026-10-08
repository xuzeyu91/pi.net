using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// A port of the subset of <c>chalk</c> the coding agent uses. Only the single-argument, 16-colour
/// styles appear in the TS sources (no chaining), so this exposes them as static methods rather than
/// a fluent builder.
/// </summary>
/// <remarks>
/// The wrapping algorithm follows chalk's <c>applyStyle</c>: existing close codes inside the string are
/// turned back into open codes so nested styles survive, and multi-line strings are closed before and
/// re-opened after every line break (CRLF aware) so the style does not bleed past the last line.
/// </remarks>
public static class Chalk
{
    private static readonly bool Detected = DetectEnabled();
    private static bool? _enabledOverride;

    /// <summary>True when colour is enabled (chalk level &gt;= 1).</summary>
    public static bool Enabled => _enabledOverride ?? Detected;

    /// <summary>
    /// Force colour support on or off. The TS tests get this by setting <c>FORCE_COLOR</c> before chalk
    /// is imported; a static initializer cannot be re-run, so the port exposes an override instead.
    /// </summary>
    internal static bool? EnabledOverride
    {
        get => _enabledOverride;
        set => _enabledOverride = value;
    }

    public static string Red(string text) => Apply(text, "\u001b[31m", "\u001b[39m");

    public static string Green(string text) => Apply(text, "\u001b[32m", "\u001b[39m");

    public static string Yellow(string text) => Apply(text, "\u001b[33m", "\u001b[39m");

    public static string Cyan(string text) => Apply(text, "\u001b[36m", "\u001b[39m");

    public static string Dim(string text) => Apply(text, "\u001b[2m", "\u001b[22m");

    public static string Bold(string text) => Apply(text, "\u001b[1m", "\u001b[22m");

    public static string Italic(string text) => Apply(text, "\u001b[3m", "\u001b[23m");

    public static string Underline(string text) => Apply(text, "\u001b[4m", "\u001b[24m");

    public static string Inverse(string text) => Apply(text, "\u001b[7m", "\u001b[27m");

    public static string Strikethrough(string text) => Apply(text, "\u001b[9m", "\u001b[29m");

    /// <summary>Resolve the open/close pair for a chalk style name, or <see langword="null"/> if unknown.</summary>
    internal static (string Open, string Close)? CodesFor(string style) => style switch
    {
        "red" => ("\u001b[31m", "\u001b[39m"),
        "green" => ("\u001b[32m", "\u001b[39m"),
        "yellow" => ("\u001b[33m", "\u001b[39m"),
        "cyan" => ("\u001b[36m", "\u001b[39m"),
        "dim" => ("\u001b[2m", "\u001b[22m"),
        "bold" => ("\u001b[1m", "\u001b[22m"),
        "italic" => ("\u001b[3m", "\u001b[23m"),
        "underline" => ("\u001b[4m", "\u001b[24m"),
        "inverse" => ("\u001b[7m", "\u001b[27m"),
        "strikethrough" => ("\u001b[9m", "\u001b[29m"),
        _ => null,
    };

    internal static string ApplyWith(string text, string open, string close, bool enabled)
    {
        if (!enabled || text.Length == 0)
        {
            return text;
        }

        var value = text;
        if (value.Contains('\u001b'))
        {
            // chalk's stringReplaceAll keeps the match and inserts the open code after it, so an
            // already-present close becomes `close + open` — only the part before it would otherwise
            // stay coloured.
            value = ReplaceKeepingMatch(value, close, open);
        }

        var lfIndex = value.IndexOf('\n', StringComparison.Ordinal);
        if (lfIndex != -1)
        {
            value = EncaseLineBreaks(value, close, open, lfIndex);
        }

        return open + value + close;
    }

    private static string Apply(string text, string open, string close) => ApplyWith(text, open, close, Enabled);

    /// <summary>Port of chalk's <c>stringReplaceAll(string, substring, postfix)</c>.</summary>
    private static string ReplaceKeepingMatch(string value, string substring, string postfix)
    {
        var index = value.IndexOf(substring, StringComparison.Ordinal);
        if (index == -1)
        {
            return value;
        }

        var result = new StringBuilder();
        var endIndex = 0;
        do
        {
            result.Append(value, endIndex, index - endIndex);
            result.Append(substring);
            result.Append(postfix);
            endIndex = index + substring.Length;
            index = value.IndexOf(substring, endIndex, StringComparison.Ordinal);
        }
        while (index != -1);

        result.Append(value, endIndex, value.Length - endIndex);
        return result.ToString();
    }

    /// <summary>
    /// Close the style before each line break and re-open it after, so every line carries the style
    /// independently. Mirrors chalk's <c>stringEncaseCRLFWithFirstIndex</c>, including the rule that for
    /// CRLF the close goes before the <c>\r</c>.
    /// </summary>
    private static string EncaseLineBreaks(string value, string prefix, string postfix, int index)
    {
        var endIndex = 0;
        var result = new StringBuilder();
        do
        {
            var gotCr = index > 0 && value[index - 1] == '\r';
            result.Append(value, endIndex, (gotCr ? index - 1 : index) - endIndex);
            result.Append(prefix);
            result.Append(gotCr ? "\r\n" : "\n");
            result.Append(postfix);
            endIndex = index + 1;
            index = value.IndexOf('\n', endIndex);
        }
        while (index != -1);

        result.Append(value, endIndex, value.Length - endIndex);
        return result.ToString();
    }

    /// <summary>
    /// Colour support detection, following <c>supports-color</c>: explicit <c>FORCE_COLOR</c> wins, then
    /// <c>NO_COLOR</c>, then a non-TTY stdout or <c>TERM=dumb</c> disables colour.
    /// </summary>
    private static bool DetectEnabled()
    {
        var forceColor = Environment.GetEnvironmentVariable("FORCE_COLOR");
        if (!string.IsNullOrEmpty(forceColor))
        {
            return forceColor is not ("0" or "false");
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return !Console.IsOutputRedirected;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
