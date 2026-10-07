namespace Pi.Tui;

/// <summary>
/// Machine-generated code-point tables extracted from the TypeScript sources.
/// Regenerate with <c>scripts/autocomplete-corpus</c> (see docs/tui-porting-status.md).
/// </summary>
internal static class AutocompleteData
{
	/// <summary>
	/// Code points matched by the TS <c>autocompleteSeparatorRegex</c>: the JavaScript <c>\s</c>
	/// set plus CJK punctuation. .NET's <c>\s</c> and <c>char.IsWhiteSpace</c> are both different
	/// sets, and .NET character classes cannot express astral code points such as U+16FE2, so the
	/// classification is carried as a range table instead of a regex.
	/// </summary>
	public static readonly (int Start, int End)[] SeparatorRanges =
	[
		(9, 13), (32, 32), (160, 160), (183, 183), (5760, 5760), (8192, 8202), (8212, 8212), (8216, 8217), (8220, 8221), (8230, 8230), (8232, 8233), (8239, 8239), (8287, 8287), (12288, 12291), (12296, 12305), (12308, 12319), (12336, 12336), (12349, 12349), (12448, 12448), (12539, 12539), (65093, 65094), (65279, 65279), (65281, 65281), (65288, 65289), (65292, 65292), (65294, 65294), (65306, 65307), (65311, 65311), (65339, 65339), (65341, 65341), (65371, 65371), (65373, 65373), (65377, 65381), (94178, 94178),
	];

	/// <summary>Code points matched by the TS <c>cjkPunctuationRegex</c>.</summary>
	public static readonly (int Start, int End)[] CjkPunctuationRanges =
	[
		(183, 183), (12289, 12291), (12296, 12305), (12308, 12319), (12336, 12336), (12349, 12349), (12448, 12448), (12539, 12539), (65093, 65094), (65377, 65381), (94178, 94178),
	];
}
