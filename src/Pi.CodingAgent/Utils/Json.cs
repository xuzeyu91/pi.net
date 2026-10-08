using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/json.ts</c>.</summary>
public static class Json
{
    // The quoted-string alternative must come first so a `//` inside a string literal is left alone.
    private static readonly Regex CommentOrString = new(
        "\"(?:\\\\.|[^\"\\\\])*\"|//[^\\n]*",
        RegexOptions.Compiled);

    private static readonly Regex TrailingCommaOrString = new(
        "\"(?:\\\\.|[^\"\\\\])*\"|,(" + JsRegex.WhitespaceClass + "*[}\\]])",
        RegexOptions.Compiled);

    /// <summary>
    /// Strip <c>//</c> line comments and trailing commas from JSON, leaving string literals untouched.
    /// </summary>
    public static string StripJsonComments(string input)
    {
        var withoutComments = CommentOrString.Replace(
            input,
            static m => m.Value[0] == '"' ? m.Value : string.Empty);

        return TrailingCommaOrString.Replace(withoutComments, static m =>
        {
            // The alternation only has two branches: a quoted string, or `,<ws>}` / `,<ws>]`.
            if (m.Groups[1].Success)
            {
                return m.Groups[1].Value;
            }

            return m.Value[0] == '"' ? m.Value : string.Empty;
        });
    }
}
