using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Options for <see cref="Latex.Render"/> (port of <c>RenderLatexOptions</c>).</summary>
public sealed class RenderLatexOptions
{
    /// <summary>Stack fractions and operator limits vertically for display math (default: false).</summary>
    public bool Display { get; init; }
}

/// <summary>
/// Port of <c>latex.ts</c>: renders a basic LaTeX math expression as terminal-friendly Unicode text.
/// Returns null when the expression contains unsupported or malformed syntax.
/// </summary>
/// <remarks>
/// The lookup tables live in <see cref="LatexData"/> (generated from the TS source). The parser walks the
/// input one UTF-16 code unit at a time, matching the TS implementation's string indexing, while
/// code-point-sensitive operations (character counts, per-character replacement) go through
/// <see cref="System.Text.Rune"/> the way JS <c>Array.from</c> and <c>for...of</c> do.
/// </remarks>
public static class Latex
{
    // Sentinels. U+F0000-U+F0005 are outside the BMP, so each is a surrogate pair in UTF-16;
    // the escapes keep them from being lost in review.
    private const string LayoutMarkerStart = "\uDB80\uDC00";
    private const string LayoutMarkerEnd = "\uDB80\uDC01";
    private const string ProtectedSpace = "\uDB80\uDC02";
    private const string NamedOperatorStart = "\uDB80\uDC04";
    private const string NamedOperatorEnd = "\uDB80\uDC05";

    private const string NegativeSpace = "\0";

    /// <summary>JS <c>\s</c>, which is a different set from .NET's <c>\s</c> (no U+0085, includes U+FEFF).</summary>
    private const string JsWs = @"[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";

    private static readonly Regex LayoutMarkerPattern = new(
        LayoutMarkerStart + @"([0-9]+)" + LayoutMarkerEnd, RegexOptions.CultureInvariant);

    private static readonly Regex TrailingLayoutMarkerPattern = new(
        LayoutMarkerStart + @"([0-9]+)" + LayoutMarkerEnd + @"\z", RegexOptions.CultureInvariant);

    private static readonly Regex NamedOperatorLeftSpacingPattern = new(
        @"(?<=[\p{L}\p{N}\]}]|" + LayoutMarkerEnd + ")" + NamedOperatorStart, RegexOptions.CultureInvariant);

    private static readonly Regex NamedOperatorRightSpacingPattern = new(
        NamedOperatorEnd + @"(?=[\p{L}\p{N}√]|" + LayoutMarkerStart + ")", RegexOptions.CultureInvariant);

    private static readonly Regex ScriptSpacingPattern = new(
        JsWs + @"*([=+-])" + JsWs + "*", RegexOptions.CultureInvariant);

    private static readonly Regex SimpleNamePattern = new(
        @"^[\p{L}\p{N}.]+\z", RegexOptions.CultureInvariant);

    private static readonly Regex SimpleNumberPattern = new(
        @"^[\p{N}.]+\z", RegexOptions.CultureInvariant);

    private static readonly Regex AsciiLettersPattern = new(
        @"^[A-Za-z]+\z", RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceCollapsePattern = new(
        @"[ \t]+", RegexOptions.CultureInvariant);

    private static readonly Regex EnvironmentRowSeparatorPattern = new(
        @"\\\\(?:\[[^\]\n]*\])?", RegexOptions.CultureInvariant);

    private static readonly Regex LeadingGroupPattern = new(
        "^" + JsWs + @"*\{[^}]*\}", RegexOptions.CultureInvariant);

    private static readonly Regex TrailingCommaPattern = new(
        "," + JsWs + @"*\z", RegexOptions.CultureInvariant);

    private static readonly Regex ConditionPrefixPattern = new(
        @"^(?:if|when|for|otherwise)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LimitsModifierPattern = new(
        @"^\\(limits|nolimits)(?![A-Za-z])", RegexOptions.CultureInvariant);

    private static readonly Regex LayoutMarkerFlagPattern = new(
        "[A-Z*∗]", RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------
    // Layout node model
    // ------------------------------------------------------------------

    private abstract record LayoutNode;

    private sealed record FractionNode(string Numerator, string Denominator) : LayoutNode;

    private sealed record OperatorNode(string Operator, string? Lower, string? Upper) : LayoutNode;

    private sealed record ScriptNode(string? Lower, string? Upper) : LayoutNode;

    /// <summary>Matrix rows. Mutable: the parser appends a trailing period to the last row in place.</summary>
    private sealed record MatrixNode(List<string> Lines, int Baseline) : LayoutNode;

    private sealed record LatexLayout(string[] Lines, int Width, int Baseline);

    private static List<string> CodePoints(string value)
    {
        var result = new List<string>();
        foreach (var rune in value.EnumerateRunes())
        {
            result.Add(rune.ToString());
        }
        return result;
    }

    private static string? ReplaceCharacters(string value, Dictionary<string, string> replacements)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var key = rune.ToString();
            if (!replacements.TryGetValue(key, out var replacement))
            {
                return null;
            }
            result.Append(replacement);
        }
        return result.ToString();
    }

    private static string NormalizeScriptValue(string value) =>
        ScriptSpacingPattern.Replace(JsString.Trim(value), "$1");

    private static string? FormatUnicodeScript(string value, bool sub) =>
        ReplaceCharacters(NormalizeScriptValue(value), sub ? LatexData.Subscripts : LatexData.Superscripts);

    private static string FormatScript(string value, bool sub)
    {
        value = NormalizeScriptValue(value);
        var unicode = FormatUnicodeScript(value, sub);
        if (unicode is not null)
        {
            return unicode;
        }

        var prefix = sub ? "_" : "^";
        if (JsString.CodePointLength(value) == 1 || (sub && AsciiLettersPattern.IsMatch(value)))
        {
            return prefix + value;
        }
        return $"{prefix}({value})";
    }

    private static string FormatFraction(string numerator, string denominator)
    {
        numerator = JsString.Trim(numerator);
        denominator = JsString.Trim(denominator);
        var simpleNumerator = SimpleNamePattern.IsMatch(numerator);
        var simpleDenominator = SimpleNumberPattern.IsMatch(denominator) || JsString.CodePointLength(denominator) == 1;
        return $"{(simpleNumerator ? numerator : $"({numerator})")}/{(simpleDenominator ? denominator : $"({denominator})")}";
    }

    private static string FormatRoot(string value, string symbol = "√")
    {
        value = JsString.Trim(value);
        return SimpleNamePattern.IsMatch(value) ? $"{symbol}{value}" : $"{symbol}({value})";
    }

    private static string NormalizeOutput(string value)
    {
        var text = NamedOperatorLeftSpacingPattern.Replace(value, " ");
        text = text.Replace(NamedOperatorStart, "");
        text = NamedOperatorRightSpacingPattern.Replace(text, " ");
        text = text.Replace(NamedOperatorEnd, "");

        var mapped = text
            .Split('\n')
            .Select(line => JsString.Trim(WhitespaceCollapsePattern.Replace(line, " ")))
            .ToList();

        var kept = new List<string>(mapped.Count);
        for (var index = 0; index < mapped.Count; index++)
        {
            // Keep blank lines only when they are interior (matching the TS filter).
            if (mapped[index].Length > 0 || (index > 0 && index < mapped.Count - 1))
            {
                kept.Add(mapped[index]);
            }
        }

        return JsString.Trim(string.Join("\n", kept));
    }

    // ------------------------------------------------------------------
    // Layout assembly
    // ------------------------------------------------------------------

    private static string PadLayoutLine(string line, int width, bool centered = false)
    {
        var padding = Math.Max(0, width - UnicodeWidth.VisibleWidth(line));
        var left = centered ? padding / 2 : 0;
        return new string(' ', left) + line + new string(' ', padding - left);
    }

    private static LatexLayout JoinLayouts(IReadOnlyList<LatexLayout> layouts)
    {
        if (layouts.Count == 0)
        {
            return new LatexLayout([""], 0, 0);
        }

        var baseline = layouts.Max(layout => layout.Baseline);
        var below = layouts.Max(layout => layout.Lines.Length - layout.Baseline - 1);
        var lines = new List<string>();
        for (var row = 0; row <= baseline + below; row++)
        {
            var builder = new StringBuilder();
            foreach (var layout in layouts)
            {
                var sourceRow = row - baseline + layout.Baseline;
                builder.Append(sourceRow >= 0 && sourceRow < layout.Lines.Length
                    ? PadLayoutLine(layout.Lines[sourceRow], layout.Width)
                    : new string(' ', layout.Width));
            }
            lines.Add(JsString.TrimEnd(builder.ToString()));
        }

        return new LatexLayout(lines.ToArray(), layouts.Sum(layout => layout.Width), baseline);
    }

    private static LatexLayout RenderLayout(string source, IReadOnlyList<LayoutNode> nodes)
    {
        var renderedLines = new List<string>();
        var firstBaseline = 0;

        foreach (var sourceLine in source.Split('\n'))
        {
            var layouts = new List<LatexLayout>();
            var position = 0;
            LayoutNode? previousNode = null;

            foreach (Match match in LayoutMarkerPattern.Matches(sourceLine))
            {
                var index = match.Index;
                if (!int.TryParse(match.Groups[1].Value, out var nodeIndex)
                    || nodeIndex < 0
                    || nodeIndex >= nodes.Count)
                {
                    continue;
                }

                var node = nodes[nodeIndex];
                if (index > position)
                {
                    var sliced = sourceLine.Substring(position, index - position);
                    var trimmed = JsString.TrimEnd(previousNode is not null ? JsString.TrimStart(sliced) : sliced);
                    var preserveLeadingSpace = previousNode is MatrixNode && sliced.Length > 0 && JsString.IsWhitespace(sliced[0]);
                    var preserveTrailingSpace = node is MatrixNode && sliced.Length > 0 && JsString.IsWhitespace(sliced[^1]);
                    var text = trimmed.Length > 0
                        ? $"{(preserveLeadingSpace ? " " : "")}{trimmed}{(preserveTrailingSpace ? " " : "")}"
                        : preserveLeadingSpace || preserveTrailingSpace ? " " : "";
                    layouts.Add(new LatexLayout([text], UnicodeWidth.VisibleWidth(text), 0));
                }

                if (node is FractionNode fraction)
                {
                    var numerator = RenderLayout(fraction.Numerator, nodes);
                    var denominator = RenderLayout(fraction.Denominator, nodes);
                    var contentWidth = Math.Max(Math.Max(numerator.Width, denominator.Width), 1);
                    var width = contentWidth + 2;
                    var lines = new List<string>();
                    lines.AddRange(numerator.Lines.Select(line => PadLayoutLine(line, width, true)));
                    lines.Add($" {new string('─', contentWidth)} ");
                    lines.AddRange(denominator.Lines.Select(line => PadLayoutLine(line, width, true)));
                    layouts.Add(new LatexLayout(lines.ToArray(), width, numerator.Lines.Length));
                }
                else if (node is OperatorNode op)
                {
                    var contentWidth = Math.Max(
                        Math.Max(
                            UnicodeWidth.VisibleWidth(op.Operator),
                            op.Lower is null ? 0 : UnicodeWidth.VisibleWidth(op.Lower)),
                        op.Upper is null ? 0 : UnicodeWidth.VisibleWidth(op.Upper));
                    var lines = new List<string>();
                    if (op.Upper is not null)
                    {
                        lines.Add($"{PadLayoutLine(op.Upper, contentWidth, true)} ");
                    }
                    lines.Add($"{PadLayoutLine(op.Operator, contentWidth, true)} ");
                    if (op.Lower is not null)
                    {
                        lines.Add($"{PadLayoutLine(op.Lower, contentWidth, true)} ");
                    }
                    layouts.Add(new LatexLayout(lines.ToArray(), contentWidth + 1, op.Upper is null ? 0 : 1));
                }
                else if (node is ScriptNode script)
                {
                    var upper = script.Upper is null ? null : RenderLayout(script.Upper, nodes);
                    var lower = script.Lower is null ? null : RenderLayout(script.Lower, nodes);
                    var width = Math.Max(upper?.Width ?? 0, lower?.Width ?? 0);
                    var lines = new List<string>();
                    if (upper is not null)
                    {
                        lines.AddRange(upper.Lines.Select(line => PadLayoutLine(line, width)));
                    }
                    lines.Add(new string(' ', width));
                    if (lower is not null)
                    {
                        lines.AddRange(lower.Lines.Select(line => PadLayoutLine(line, width)));
                    }
                    layouts.Add(new LatexLayout(lines.ToArray(), width, upper?.Lines.Length ?? 0));
                }
                else
                {
                    var matrix = (MatrixNode)node;
                    var width = Math.Max(0, matrix.Lines.Count == 0 ? 0 : matrix.Lines.Max(UnicodeWidth.VisibleWidth));
                    layouts.Add(new LatexLayout(
                        matrix.Lines.Select(line => PadLayoutLine(line, width)).ToArray(),
                        width,
                        matrix.Baseline));
                }

                position = index + match.Length;
                previousNode = node;
            }

            if (position < sourceLine.Length)
            {
                var sliced = sourceLine.Substring(position);
                var trimmed = previousNode is not null ? JsString.TrimStart(sliced) : sliced;
                var text = previousNode is MatrixNode && sliced.Length > 0 && JsString.IsWhitespace(sliced[0])
                    ? $" {trimmed}"
                    : trimmed;
                layouts.Add(new LatexLayout([text], UnicodeWidth.VisibleWidth(text), 0));
            }

            var lineLayout = JoinLayouts(layouts);
            if (renderedLines.Count == 0)
            {
                firstBaseline = lineLayout.Baseline;
            }
            renderedLines.AddRange(lineLayout.Lines);
        }

        return new LatexLayout(
            renderedLines.ToArray(),
            renderedLines.Count == 0 ? 0 : renderedLines.Max(UnicodeWidth.VisibleWidth),
            firstBaseline);
    }

    // ------------------------------------------------------------------
    // Public entry point
    // ------------------------------------------------------------------

    /// <summary>
    /// Render a basic LaTeX math expression as terminal-friendly Unicode text.
    /// Returns null when the expression contains unsupported or malformed syntax.
    /// </summary>
    public static string? Render(string source, RenderLatexOptions? options = null)
    {
        options ??= new RenderLatexOptions();
        var layoutNodes = new List<LayoutNode>();
        var rendered = new Parser(source, layoutNodes, options.Display).Render();
        if (rendered is null)
        {
            return null;
        }
        if (layoutNodes.Count == 0)
        {
            return rendered.Replace(ProtectedSpace, " ");
        }

        var lines = RenderLayout(rendered, layoutNodes).Lines;
        var indentation = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }
            var indent = line.Length - JsString.TrimStart(line).Length;
            indentation = Math.Min(indentation, indent);
        }

        var builder = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }
            builder.Append(JsString.TrimEnd(lines[index].Substring(Math.Min(indentation, lines[index].Length))));
        }

        return JsString.TrimEnd(builder.ToString()).Replace(ProtectedSpace, " ");
    }

    // ------------------------------------------------------------------
    // Parser
    // ------------------------------------------------------------------

    private sealed class Parser
    {
        private readonly string _source;
        private readonly List<LayoutNode> _layoutNodes;
        private readonly bool _display;
        private int _position;
        private bool _supported = true;
        private bool _stackFractions = true;
        private int _scriptDepth;

        public Parser(string source, List<LayoutNode> layoutNodes, bool display)
        {
            _source = source;
            _layoutNodes = layoutNodes;
            _display = display;
        }

        private char? CharAt(int index) => index >= 0 && index < _source.Length ? _source[index] : null;

        public string? Render()
        {
            var rendered = ParseSequence(null);
            if (!_supported || _position != _source.Length)
            {
                return null;
            }
            return NormalizeOutput(rendered);
        }

        private string ParseSequence(char? endCharacter)
        {
            var result = "";
            while (_position < _source.Length)
            {
                var character = _source[_position];
                if (endCharacter is { } end && character == end)
                {
                    _position++;
                    return result;
                }

                if (character == '}')
                {
                    _supported = false;
                    return result;
                }

                if (character == '{')
                {
                    _position++;
                    result += ParseSequence('}');
                    continue;
                }

                if (character == '\\')
                {
                    var command = ParseCommand();
                    if (command == NegativeSpace)
                    {
                        result = JsString.TrimEnd(result);
                        if (result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal))
                        {
                            result = result.Substring(0, result.Length - NamedOperatorEnd.Length);
                        }
                    }
                    else
                    {
                        result += command;
                    }
                    continue;
                }

                if (character is '^' or '_')
                {
                    _position++;
                    result = JsString.TrimEnd(result);
                    var script = ParseScripts(character);
                    if (result.EndsWith(NamedOperatorEnd, StringComparison.Ordinal))
                    {
                        result = string.Concat(
                            result.AsSpan(0, result.Length - NamedOperatorEnd.Length),
                            script,
                            NamedOperatorEnd);
                    }
                    else
                    {
                        result += script;
                    }
                    continue;
                }

                if (JsString.IsWhitespace(character))
                {
                    result += ParseWhitespace();
                    continue;
                }

                if (character is '=' or '<' or '>')
                {
                    result = $"{JsString.TrimEnd(result)} {character} ";
                    _position++;
                    continue;
                }

                if (character == '&')
                {
                    _position++;
                    continue;
                }

                if (character == '~')
                {
                    _position++;
                    result += " ";
                    continue;
                }

                if (character == '.')
                {
                    var marker = TrailingLayoutMarkerPattern.Match(result);
                    if (marker.Success
                        && int.TryParse(marker.Groups[1].Value, out var markerIndex)
                        && markerIndex >= 0
                        && markerIndex < _layoutNodes.Count
                        && _layoutNodes[markerIndex] is MatrixNode matrix)
                    {
                        var lastLine = matrix.Lines.Count - 1;
                        matrix.Lines[lastLine] = $"{matrix.Lines[lastLine]}{character}";
                        _position++;
                        continue;
                    }
                }

                result += character;
                _position++;
            }

            if (endCharacter is not null)
            {
                _supported = false;
            }
            return result;
        }

        private string ParseScripts(char initialMarker)
        {
            string? sub = null;
            string? sup = null;
            var order = new List<bool>(); // true == sup

            void Parse(char marker)
            {
                var isSub = marker == '_';
                _scriptDepth++;
                try
                {
                    var value = ParseRequiredArgument(false);
                    if (isSub)
                    {
                        sub = value;
                    }
                    else
                    {
                        sup = value;
                    }
                }
                finally
                {
                    _scriptDepth--;
                }
                order.Add(!isSub);
            }

            Parse(initialMarker);

            var nextPosition = _position;
            while (nextPosition < _source.Length && JsString.IsWhitespace(_source[nextPosition]))
            {
                nextPosition++;
            }
            var nextMarker = CharAt(nextPosition);
            if ((nextMarker == '^' || nextMarker == '_') && nextMarker != initialMarker)
            {
                _position = nextPosition + 1;
                Parse(nextMarker.Value);
            }

            var subUnicode = sub is null ? null : FormatUnicodeScript(sub, true);
            var supUnicode = sup is null ? null : FormatUnicodeScript(sup, false);

            var canUseLayout = true;
            foreach (var value in new[] { sub, sup })
            {
                if (value is null)
                {
                    continue;
                }
                if (value.Contains('/')
                    || (!value.Contains(LayoutMarkerStart, StringComparison.Ordinal)
                        && JsString.CodePointLength(value) > 1
                        && !LayoutMarkerFlagPattern.IsMatch(value)))
                {
                    canUseLayout = false;
                    break;
                }
            }

            var needsLayout = _display
                && canUseLayout
                && (_scriptDepth > 0
                    || (sub is not null && subUnicode is null)
                    || (sup is not null && supUnicode is null));

            if (!needsLayout)
            {
                var builder = new StringBuilder();
                foreach (var isSup in order)
                {
                    builder.Append(isSup
                        ? supUnicode ?? FormatScript(sup ?? "", false)
                        : subUnicode ?? FormatScript(sub ?? "", true));
                }
                return builder.ToString();
            }

            _layoutNodes.Add(new ScriptNode(
                sub is null ? null : NormalizeOutput(sub),
                sup is null ? null : NormalizeOutput(sup)));
            return $"{LayoutMarkerStart}{_layoutNodes.Count - 1}{LayoutMarkerEnd}";
        }

        private string ParseWhitespace()
        {
            while (_position < _source.Length && JsString.IsWhitespace(_source[_position]))
            {
                _position++;
            }
            return " ";
        }

        private string ParseCommand()
        {
            _position++;
            if (_position >= _source.Length)
            {
                _supported = false;
                return "";
            }

            string command;
            var first = _source[_position];
            if (first is '\n' or '\r')
            {
                _position++;
                if (first == '\r' && CharAt(_position) == '\n')
                {
                    _position++;
                }
                return " ";
            }

            if (IsAsciiLetter(first))
            {
                var start = _position;
                while (_position < _source.Length && IsAsciiLetter(_source[_position]))
                {
                    _position++;
                }
                command = _source.Substring(start, _position - start);
            }
            else
            {
                command = first.ToString();
                _position++;
            }

            if (command == "\\")
            {
                return "\n";
            }
            if (LatexData.SpacingCommands.Contains(command))
            {
                return " ";
            }
            if (LatexData.NegativeSpacingCommands.Contains(command))
            {
                return NegativeSpace;
            }
            if (LatexData.FontSwitchCommands.Contains(command))
            {
                while (_position < _source.Length && JsString.IsWhitespace(_source[_position]))
                {
                    _position++;
                }
                return "";
            }
            if (LatexData.IgnoredCommands.Contains(command))
            {
                return "";
            }
            if (command is "{" or "}" or "$" or "%" or "#" or "_" or "&")
            {
                return command;
            }
            if (command == "|")
            {
                return "‖";
            }
            if (command == "not")
            {
                var value = JsString.Trim(ParseRequiredArgument(false));
                if (LatexData.NegatedSymbols.TryGetValue(value, out var negated))
                {
                    return $" {negated} ";
                }
                var characters = CodePoints(value);
                if (characters.Count == 0)
                {
                    _supported = false;
                    return "";
                }
                return $" {characters[0]}\u0338{string.Concat(characters.Skip(1))} ";
            }
            if (LatexData.LimitOperators.Contains(command))
            {
                return ParseOperator(command, "bracket", true, true);
            }

            if (LatexData.Symbols.TryGetValue(command, out var symbol))
            {
                if (LatexData.DisplayLimitSymbols.Contains(command))
                {
                    return ParseOperator(symbol, "script", true);
                }
                return command is "cdot" or "times" || LatexData.RelationCommands.Contains(command)
                    ? $" {symbol} "
                    : symbol;
            }
            if (LatexData.NamedOperators.Contains(command))
            {
                return $"{NamedOperatorStart}{command}{NamedOperatorEnd}";
            }
            if (LatexData.SizeCommands.Contains(command))
            {
                return "";
            }
            if (command is "left" or "middle" or "right")
            {
                if (CharAt(_position) == '.')
                {
                    _position++;
                }
                return "";
            }
            if (command is "frac" or "dfrac" or "tfrac")
            {
                var shouldStack = _display && _stackFractions && command != "tfrac";
                var numerator = ParseRequiredArgument(!shouldStack);
                var denominator = ParseRequiredArgument(!shouldStack);
                if (shouldStack)
                {
                    _layoutNodes.Add(new FractionNode(NormalizeOutput(numerator), NormalizeOutput(denominator)));
                    return $"{LayoutMarkerStart}{_layoutNodes.Count - 1}{LayoutMarkerEnd}";
                }
                return FormatFraction(numerator, denominator);
            }
            if (command == "sqrt")
            {
                var degree = ParseOptionalArgument() is { } rawDegree ? JsString.Trim(rawDegree) : null;
                var value = ParseRequiredArgument();
                if (degree is null || degree == "2")
                {
                    return FormatRoot(value);
                }
                if (degree == "3")
                {
                    return FormatRoot(value, "∛");
                }
                if (degree == "4")
                {
                    return FormatRoot(value, "∜");
                }
                return $"{FormatScript(degree, false)}{FormatRoot(value)}";
            }
            if (command is "boxed" or "fbox")
            {
                return $"[{JsString.Trim(ParseRequiredArgument())}]";
            }
            if (command is "binom" or "dbinom" or "tbinom")
            {
                return $"({ParseRequiredArgument()} choose {ParseRequiredArgument()})";
            }
            if (LatexData.Accents.TryGetValue(command, out var accent))
            {
                var value = ParseRequiredArgument();
                return JsString.CodePointLength(value) == 1 ? $"{value}{accent}" : $"{command}({value})";
            }
            if (command == "mathbb")
            {
                var value = ParseRequiredArgument();
                var builder = new StringBuilder();
                foreach (var character in CodePoints(value))
                {
                    builder.Append(LatexData.Blackboard.TryGetValue(character, out var mapped) ? mapped : character);
                }
                return builder.ToString();
            }
            if (command == "operatorname")
            {
                var starred = CharAt(_position) == '*';
                if (starred)
                {
                    _position++;
                }
                var op = JsString.Trim(NormalizeOutput(ParseRequiredArgument()));
                return ParseOperator(op, "bracket", starred, true);
            }
            if (command is "mod" or "bmod")
            {
                return " mod ";
            }
            if (command is "pmod" or "pod")
            {
                var value = JsString.Trim(ParseRequiredArgument());
                return command == "pmod" ? $" (mod {value})" : $" ({value})";
            }
            if (command is "overset" or "stackrel")
            {
                var upper = ParseRequiredArgument();
                var value = JsString.Trim(ParseRequiredArgument());
                return $"{value}{FormatScript(upper, false)}";
            }
            if (command == "underset")
            {
                var lower = ParseRequiredArgument();
                var value = JsString.Trim(ParseRequiredArgument());
                return $"{value}{FormatScript(lower, true)}";
            }
            if (LatexData.PlainWrappers.Contains(command))
            {
                var value = ParseRequiredArgument();
                return command.StartsWith("text", StringComparison.Ordinal) || command == "mbox" ? value : JsString.Trim(value);
            }
            if (command == "begin")
            {
                return ParseEnvironment();
            }
            if (command == "end")
            {
                _supported = false;
                return "";
            }

            _supported = false;
            return $"\\{command}";
        }

        private string ParseOperator(string op, string inlineLowerStyle, bool displayLimits, bool spaced = false)
        {
            var useDisplayLimits = displayLimits;
            var modifierPosition = _position;
            while (modifierPosition < _source.Length && _source[modifierPosition] is ' ' or '\t')
            {
                modifierPosition++;
            }
            var modifier = LimitsModifierPattern.Match(_source.Substring(modifierPosition));
            if (modifier.Success)
            {
                useDisplayLimits = modifier.Groups[1].Value == "limits";
                _position = modifierPosition + modifier.Length;
            }

            string? lower = null;
            string? upper = null;
            while (true)
            {
                var scriptPosition = _position;
                while (scriptPosition < _source.Length && _source[scriptPosition] is ' ' or '\t')
                {
                    scriptPosition++;
                }
                var kind = CharAt(scriptPosition);
                if (kind != '_' && kind != '^')
                {
                    break;
                }
                _position = scriptPosition + 1;
                var value = NormalizeOutput(ParseRequiredArgument(false)).Replace(" ", "");
                if (kind == '_')
                {
                    if (lower is not null)
                    {
                        _supported = false;
                    }
                    lower = value;
                }
                else
                {
                    if (upper is not null)
                    {
                        _supported = false;
                    }
                    upper = value;
                }
            }

            if (_display && useDisplayLimits && (lower is not null || upper is not null))
            {
                _layoutNodes.Add(new OperatorNode(op, lower, upper));
                return $"{LayoutMarkerStart}{_layoutNodes.Count - 1}{LayoutMarkerEnd}";
            }

            var rendered = op;
            if (lower is not null)
            {
                rendered += inlineLowerStyle == "bracket" ? $"[{lower}]" : FormatScript(lower, true);
            }
            if (upper is not null)
            {
                rendered += FormatScript(upper, false);
            }
            return spaced ? $" {rendered} " : rendered;
        }

        private string ParseRequiredArgument(bool stackFractions = true)
        {
            var previousStackFractions = _stackFractions;
            _stackFractions = previousStackFractions && stackFractions;
            var value = ParseRequiredArgumentValue();
            _stackFractions = previousStackFractions;
            return value;
        }

        private string ParseRequiredArgumentValue()
        {
            while (_position < _source.Length && JsString.IsWhitespace(_source[_position]))
            {
                _position++;
            }
            if (_position >= _source.Length)
            {
                _supported = false;
                return "";
            }
            if (_source[_position] == '{')
            {
                _position++;
                return ParseSequence('}');
            }
            if (_source[_position] == '\\')
            {
                return ParseCommand();
            }
            var value = _source[_position].ToString();
            _position++;
            return value;
        }

        private string? ParseOptionalArgument()
        {
            while (_position < _source.Length && _source[_position] is ' ' or '\t')
            {
                _position++;
            }
            if (CharAt(_position) != '[')
            {
                return null;
            }
            var end = _source.IndexOf(']', _position + 1);
            if (end < 0)
            {
                _supported = false;
                return null;
            }
            var value = _source.Substring(_position + 1, end - _position - 1);
            _position = end + 1;
            return RenderNested(value);
        }

        private string? ReadRawGroup()
        {
            while (_position < _source.Length && _source[_position] is ' ' or '\t')
            {
                _position++;
            }
            if (CharAt(_position) != '{')
            {
                _supported = false;
                return null;
            }

            _position++;
            var start = _position;
            var depth = 1;
            while (_position < _source.Length)
            {
                var character = _source[_position];
                if (character == '\\')
                {
                    _position += 2;
                    continue;
                }
                if (character == '{')
                {
                    depth++;
                }
                if (character == '}')
                {
                    depth--;
                }
                if (depth == 0)
                {
                    var value = _source.Substring(start, _position - start);
                    _position++;
                    return value;
                }
                _position++;
            }

            _supported = false;
            return null;
        }

        private static string[] SplitEnvironmentRows(string body) =>
            EnvironmentRowSeparatorPattern.Split(body);

        private string ParseEnvironment()
        {
            var environment = ReadRawGroup();
            if (environment is null)
            {
                return "";
            }
            var endMarker = $"\\end{{{environment}}}";
            var end = _source.IndexOf(endMarker, _position, StringComparison.Ordinal);
            if (end < 0)
            {
                _supported = false;
                return "";
            }
            var body = _source.Substring(_position, end - _position);
            _position = end + endMarker.Length;

            if (environment is "equation" or "equation*" or "displaymath")
            {
                return JsString.Trim(RenderNested(body));
            }

            if (environment is "aligned" or "align" or "align*" or "alignedat" or "alignat" or "alignat*"
                or "gather" or "gathered" or "multline" or "multline*" or "split")
            {
                var alignedAt = environment is "alignedat" or "alignat" or "alignat*";
                var alignedBody = alignedAt ? LeadingGroupPattern.Replace(body, "") : body;
                var rows = new List<string>();
                foreach (var row in SplitEnvironmentRows(alignedBody))
                {
                    var cells = row.Split('&');
                    string source;
                    if (alignedAt)
                    {
                        var parts = new List<string>();
                        for (var index = 0; index < (cells.Length + 1) / 2; index++)
                        {
                            parts.Add(string.Concat(cells.Skip(index * 2).Take(2)));
                        }
                        source = string.Join(" ", parts);
                    }
                    else
                    {
                        source = string.Concat(cells);
                    }
                    var rendered = JsString.Trim(RenderNested(source));
                    if (rendered.Length > 0)
                    {
                        rows.Add(rendered);
                    }
                }
                return string.Join("\n", rows);
            }

            if (environment is "cases" or "cases*")
            {
                return RenderCases(body);
            }

            if (environment is "array" or "matrix" or "smallmatrix" or "pmatrix" or "bmatrix"
                or "Bmatrix" or "vmatrix" or "Vmatrix")
            {
                var matrixBody = environment == "array" ? LeadingGroupPattern.Replace(body, "") : body;
                return RenderMatrix(environment, matrixBody);
            }

            _supported = false;
            return body;
        }

        private string RenderCases(string body)
        {
            var rows = new List<List<string>>();
            foreach (var row in SplitEnvironmentRows(body))
            {
                var cells = row.Split('&').Select(cell => JsString.Trim(RenderNested(cell, false))).ToList();
                if (cells.Any(cell => cell.Length > 0))
                {
                    rows.Add(cells);
                }
            }

            var valueWidth = 0;
            foreach (var row in rows)
            {
                var value = TrailingCommaPattern.Replace(row[0], "");
                valueWidth = Math.Max(valueWidth, UnicodeWidth.VisibleWidth(value));
            }

            var contents = new List<string>();
            foreach (var row in rows)
            {
                var value = TrailingCommaPattern.Replace(row[0], "");
                var condition = row.Count > 1 ? row[1] : "";
                if (condition.Length == 0)
                {
                    contents.Add(value);
                    continue;
                }
                var conditionPrefix = ConditionPrefixPattern.IsMatch(condition) ? " " : " if ";
                contents.Add(
                    $"{value}{Repeat(ProtectedSpace, valueWidth - UnicodeWidth.VisibleWidth(value))}{conditionPrefix}{condition}");
            }

            if (contents.Count <= 1)
            {
                return contents.Count == 0 ? "" : $"⎧ {contents[0]}";
            }

            var middle = contents.Count / 2;
            var visualRows = new List<string?>(contents);
            if (contents.Count % 2 == 0)
            {
                visualRows.Insert(middle, null);
            }

            var lines = new List<string>(visualRows.Count);
            for (var index = 0; index < visualRows.Count; index++)
            {
                var delimiter = index == 0 ? "⎧" : index == visualRows.Count - 1 ? "⎩" : "⎨";
                var content = visualRows[index];
                lines.Add(content is null ? delimiter : $"{delimiter} {content}");
            }

            _layoutNodes.Add(new MatrixNode(lines, middle));
            return $"{LayoutMarkerStart}{_layoutNodes.Count - 1}{LayoutMarkerEnd}";
        }

        private string RenderMatrix(string environment, string body)
        {
            var matrix = new List<List<string>>();
            foreach (var row in SplitEnvironmentRows(body))
            {
                var cells = row.Split('&').Select(cell => JsString.Trim(RenderNested(cell, false))).ToList();
                if (cells.Any(cell => cell.Length > 0))
                {
                    matrix.Add(cells);
                }
            }

            var columnCount = 0;
            foreach (var row in matrix)
            {
                columnCount = Math.Max(columnCount, row.Count);
            }

            var columnWidths = new int[columnCount];
            for (var column = 0; column < columnCount; column++)
            {
                var width = 0;
                foreach (var row in matrix)
                {
                    width = Math.Max(width, UnicodeWidth.VisibleWidth(column < row.Count ? row[column] : ""));
                }
                columnWidths[column] = width;
            }

            var rows = new List<string>(matrix.Count);
            foreach (var row in matrix)
            {
                var cells = new List<string>(columnCount);
                for (var column = 0; column < columnCount; column++)
                {
                    var cell = column < row.Count ? row[column] : "";
                    cells.Add(cell + Repeat(ProtectedSpace, Math.Max(0, columnWidths[column] - UnicodeWidth.VisibleWidth(cell))));
                }
                rows.Add(string.Join(" │ ", cells));
            }

            List<string> lines;
            if (environment is "array" or "matrix" or "smallmatrix")
            {
                lines = rows;
            }
            else
            {
                var delimiters = new Dictionary<string, string[]>
                {
                    ["pmatrix"] = ["⎛", "⎞", "⎜", "⎟", "⎝", "⎠"],
                    ["bmatrix"] = ["⎡", "⎤", "⎢", "⎥", "⎣", "⎦"],
                    ["Bmatrix"] = ["⎧", "⎫", "⎨", "⎬", "⎩", "⎭"],
                    ["vmatrix"] = ["│", "│", "│", "│", "│", "│"],
                    ["Vmatrix"] = ["║", "║", "║", "║", "║", "║"],
                };
                if (!delimiters.TryGetValue(environment, out var delimiter))
                {
                    _supported = false;
                    return string.Join("\n", rows);
                }

                lines = new List<string>(rows.Count);
                for (var index = 0; index < rows.Count; index++)
                {
                    var left = index == 0 ? delimiter[0] : index == rows.Count - 1 ? delimiter[4] : delimiter[2];
                    var right = index == 0 ? delimiter[1] : index == rows.Count - 1 ? delimiter[5] : delimiter[3];
                    lines.Add($"{left} {rows[index]} {right}");
                }
            }

            if (lines.Count <= 1)
            {
                return lines.Count == 0 ? "" : lines[0];
            }

            _layoutNodes.Add(new MatrixNode(lines, 0));
            return $"{LayoutMarkerStart}{_layoutNodes.Count - 1}{LayoutMarkerEnd}";
        }

        private string RenderNested(string source, bool stackFractions = true)
        {
            var rendered = new Parser(source, _layoutNodes, _display && stackFractions).Render();
            if (rendered is null)
            {
                _supported = false;
                return source;
            }
            return rendered;
        }
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static string Repeat(string value, int count) => count <= 0 ? "" : string.Concat(Enumerable.Repeat(value, count));
}
