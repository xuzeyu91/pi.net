using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Path resolution helpers for the built-in tools. Port of <c>core/tools/path-utils.ts</c>.</summary>
public static partial class ToolPathUtils
{
    private const string NarrowNoBreakSpace = "\u202F";

    [GeneratedRegex(@" (AM|PM)\.", RegexOptions.IgnoreCase)]
    private static partial Regex AmPmRegex();

    private static string TryMacOsScreenshotPath(string filePath) =>
        AmPmRegex().Replace(filePath, $"{NarrowNoBreakSpace}$1.");

    // macOS stores filenames in NFD (decomposed) form, try converting user input to NFD.
    private static string TryNfdVariant(string filePath) => filePath.Normalize(NormalizationForm.FormD);

    // macOS uses U+2019 (right single quotation mark) in screenshot names like "Capture d'écran";
    // users typically type U+0027 (straight apostrophe).
    private static string TryCurlyQuoteVariant(string filePath) => filePath.Replace("'", "\u2019");

    /// <summary>
    /// The TS <c>fileExists</c>: <c>accessSync(path, F_OK)</c> succeeds for directories too, so the
    /// port must accept both files and directories (a bare <see cref="File.Exists(string)"/> would
    /// answer false for a directory, unlike Node).
    /// </summary>
    private static bool FileExists(string filePath) => File.Exists(filePath) || Directory.Exists(filePath);

    /// <summary>The TS <c>pathExists</c> (async): <c>access(path, F_OK)</c> probes existence only.</summary>
    public static Task<bool> PathExistsAsync(string filePath) =>
        Task.FromResult(File.Exists(filePath) || Directory.Exists(filePath));

    /// <summary>Expand a user-supplied path (tilde, file URL, unicode spaces, <c>@</c> prefix).</summary>
    public static string ExpandPath(string filePath) =>
        Paths.NormalizePath(filePath, new PathInputOptions(NormalizeUnicodeSpaces: true, StripAtPrefix: true));

    /// <summary>Resolve a path relative to the given cwd. Handles ~ expansion and absolute paths.</summary>
    public static string ResolveToCwd(string filePath, string cwd) =>
        Paths.ResolvePath(filePath, cwd, new PathInputOptions(NormalizeUnicodeSpaces: true, StripAtPrefix: true));

    /// <summary>
    /// Resolve a path for reading, trying the macOS screenshot variants when the literal path does
    /// not exist: AM/PM narrow no-break space, NFD, curly quote, and NFD + curly quote.
    /// </summary>
    public static string ResolveReadPath(string filePath, string cwd)
    {
        var resolved = ResolveToCwd(filePath, cwd);

        if (FileExists(resolved))
        {
            return resolved;
        }

        // Try macOS AM/PM variant (narrow no-break space before AM/PM).
        var amPmVariant = TryMacOsScreenshotPath(resolved);
        if (amPmVariant != resolved && FileExists(amPmVariant))
        {
            return amPmVariant;
        }

        // Try NFD variant (macOS stores filenames in NFD form).
        var nfdVariant = TryNfdVariant(resolved);
        if (nfdVariant != resolved && FileExists(nfdVariant))
        {
            return nfdVariant;
        }

        // Try curly quote variant (macOS uses U+2019 in screenshot names).
        var curlyVariant = TryCurlyQuoteVariant(resolved);
        if (curlyVariant != resolved && FileExists(curlyVariant))
        {
            return curlyVariant;
        }

        // Try combined NFD + curly quote (for French macOS screenshots like "Capture d'écran").
        var nfdCurlyVariant = TryCurlyQuoteVariant(nfdVariant);
        if (nfdCurlyVariant != resolved && FileExists(nfdCurlyVariant))
        {
            return nfdCurlyVariant;
        }

        return resolved;
    }

    /// <summary>Async twin of <see cref="ResolveReadPath"/>.</summary>
    public static async Task<string> ResolveReadPathAsync(string filePath, string cwd)
    {
        var resolved = ResolveToCwd(filePath, cwd);

        if (await PathExistsAsync(resolved).ConfigureAwait(false))
        {
            return resolved;
        }

        // Try macOS AM/PM variant (narrow no-break space before AM/PM).
        var amPmVariant = TryMacOsScreenshotPath(resolved);
        if (amPmVariant != resolved && await PathExistsAsync(amPmVariant).ConfigureAwait(false))
        {
            return amPmVariant;
        }

        // Try NFD variant (macOS stores filenames in NFD form).
        var nfdVariant = TryNfdVariant(resolved);
        if (nfdVariant != resolved && await PathExistsAsync(nfdVariant).ConfigureAwait(false))
        {
            return nfdVariant;
        }

        // Try curly quote variant (macOS uses U+2019 in screenshot names).
        var curlyVariant = TryCurlyQuoteVariant(resolved);
        if (curlyVariant != resolved && await PathExistsAsync(curlyVariant).ConfigureAwait(false))
        {
            return curlyVariant;
        }

        // Try combined NFD + curly quote (for French macOS screenshots like "Capture d'écran").
        var nfdCurlyVariant = TryCurlyQuoteVariant(nfdVariant);
        if (nfdCurlyVariant != resolved && await PathExistsAsync(nfdCurlyVariant).ConfigureAwait(false))
        {
            return nfdCurlyVariant;
        }

        return resolved;
    }
}
