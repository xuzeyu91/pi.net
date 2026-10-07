using System.Text.RegularExpressions;

namespace Pi.Tui.Markdown;

/// <summary>
/// JS string/regex semantics that the marked port relies on and that .NET spells differently.
/// </summary>
public static class MarkedText
{
    /// <summary>Port of marked's <c>L(str, char, invert)</c>: trims trailing characters.</summary>
    public static string TrimTrailing(string value, char target, bool invert = false)
    {
        var length = value.Length;
        if (length == 0)
        {
            return "";
        }

        var trimmed = 0;
        while (trimmed < length)
        {
            var c = value[length - trimmed - 1];
            if (c == target && !invert)
            {
                trimmed++;
            }
            else if (c != target && invert)
            {
                trimmed++;
            }
            else
            {
                break;
            }
        }

        return value[..(length - trimmed)];
    }

    /// <summary>Port of marked's <c>te()</c>: drops trailing blank lines, keeping at most one.</summary>
    public static string TrimBlankLines(string value)
    {
        var lines = value.Split('\n');
        var last = lines.Length - 1;
        while (last >= 0 && MarkedRules.Default.Other.BlankLine.IsMatch(lines[last]))
        {
            last--;
        }

        return lines.Length - last <= 2 ? value : string.Join('\n', lines[..(last + 1)]);
    }

    /// <summary>
    /// Port of marked's <c>me()</c>: finds the index of the closing bracket that balances
    /// <paramref name="pair"/>, honouring backslash escapes. Returns -1 for "not found", -2 for
    /// "unbalanced" and the index otherwise.
    /// </summary>
    public static int FindClosingBracket(string value, string pair)
    {
        if (!value.Contains(pair[1]))
        {
            return -1;
        }

        var open = pair[0];
        var close = pair[1];
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == open)
            {
                depth++;
            }
            else if (value[i] == close)
            {
                depth--;
                if (depth < 0)
                {
                    return i;
                }
            }
        }

        return depth > 0 ? -2 : -1;
    }

    /// <summary>
    /// Port of marked's <c>xe()</c>: expands tabs to the next multiple of four columns, starting from
    /// <paramref name="column"/>. Iterates code points, like the JS <c>for...of</c> it replaces.
    /// </summary>
    public static string ExpandTabs(string value, int column)
    {
        var builder = new System.Text.StringBuilder();
        var col = column;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                var width = 4 - (col % 4);
                builder.Append(' ', width);
                col += width;
            }
            else
            {
                builder.Append(rune.ToString());
                col++;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Port of marked's <c>ee()</c>: splits one table row into trimmed cells, honouring escaped pipes.
    /// </summary>
    public static List<string> SplitTableRow(string row, int? cellCount)
    {
        var escaped = Regex.Replace(
            row,
            @"\|",
            m =>
            {
                var backslashes = false;
                for (var i = m.Index - 1; i >= 0 && row[i] == '\\'; i--)
                {
                    backslashes = !backslashes;
                }
                return backslashes ? "|" : " |";
            });

        var cells = escaped.Split(" |").ToList();
        if (cells.Count > 0 && JsString.Trim(cells[0]).Length == 0)
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && JsString.Trim(cells[^1]).Length == 0)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        if (cellCount.HasValue)
        {
            if (cells.Count > cellCount.Value)
            {
                cells.RemoveRange(cellCount.Value, cells.Count - cellCount.Value);
            }
            else
            {
                while (cells.Count < cellCount.Value)
                {
                    cells.Add("");
                }
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            cells[i] = Regex.Replace(JsString.Trim(cells[i]), @"\\\|", "|");
        }

        return cells;
    }

    /// <summary>JS <c>str.search(re)</c>: index of the first match, or -1.</summary>
    public static int Search(string value, Regex regex)
    {
        var match = regex.Match(value);
        return match.Success ? match.Index : -1;
    }

    /// <summary>JS <c>str.split(sep, 1)[0]</c>.</summary>
    public static string FirstLine(string value) => value.Split('\n')[0];

    /// <summary>JS <c>str.replace(/\n$/, "")</c>.</summary>
    public static string DropTrailingNewline(string value) =>
        value.EndsWith('\n') ? value[..^1] : value;

    /// <summary>
    /// JS <c>[...value][0].length</c>: the UTF-16 length of the first code point. marked uses this to
    /// step over the opening delimiter, which may be a surrogate pair.
    /// </summary>
    public static int FirstCodePointLength(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        return char.IsHighSurrogate(value[0]) && value.Length > 1 && char.IsLowSurrogate(value[1]) ? 2 : 1;
    }
}
