using System.Text;

namespace Pi.Tui;

/// <summary>A parsed ANSI/OSC/APC escape sequence and its length in UTF-16 code units.</summary>
public readonly record struct AnsiCode(string Code, int Length);

/// <summary>
/// Port of <c>utils.ts</c>'s escape-sequence handling: extraction, stripping and normalization.
/// </summary>
public static class Ansi
{
    /// <summary>Extract an ANSI/OSC/APC escape sequence starting at <paramref name="pos"/>, or null.</summary>
    public static AnsiCode? ExtractAnsiCode(string str, int pos)
    {
        if (pos >= str.Length || str[pos] != '\x1b')
        {
            return null;
        }

        var next = pos + 1 < str.Length ? str[pos + 1] : '\0';

        // CSI sequence: ESC [ ... m/G/K/H/J
        if (next == '[')
        {
            var j = pos + 2;
            while (j < str.Length && str[j] is not ('m' or 'G' or 'K' or 'H' or 'J'))
            {
                j++;
            }
            if (j < str.Length)
            {
                return new AnsiCode(str.Substring(pos, j + 1 - pos), j + 1 - pos);
            }
            return null;
        }

        // OSC sequence: ESC ] ... BEL or ESC ] ... ST (ESC \)
        if (next == ']')
        {
            return ScanTerminated(str, pos);
        }

        // APC sequence: ESC _ ... BEL or ESC _ ... ST (ESC \)
        if (next == '_')
        {
            return ScanTerminated(str, pos);
        }

        return null;
    }

    private static AnsiCode? ScanTerminated(string str, int pos)
    {
        var j = pos + 2;
        while (j < str.Length)
        {
            if (str[j] == '\x07')
            {
                return new AnsiCode(str.Substring(pos, j + 1 - pos), j + 1 - pos);
            }
            if (str[j] == '\x1b' && j + 1 < str.Length && str[j + 1] == '\\')
            {
                return new AnsiCode(str.Substring(pos, j + 2 - pos), j + 2 - pos);
            }
            j++;
        }
        return null;
    }

    /// <summary>Return only the background color active at the end of an ANSI-styled string.</summary>
    public static string GetActiveBackgroundAnsi(string text)
    {
        var tracker = new AnsiCodeTracker();
        UpdateTrackerFromText(text, tracker);
        return tracker.GetActiveBackgroundCode();
    }

    /// <summary>Feed every escape sequence found in <paramref name="text"/> to <paramref name="tracker"/>.</summary>
    internal static void UpdateTrackerFromText(string text, AnsiCodeTracker tracker)
    {
        var i = text.IndexOf('\x1b');
        while (i != -1)
        {
            var ansi = ExtractAnsiCode(text, i);
            if (ansi is { } code && code.Length > 0)
            {
                tracker.Process(code.Code);
                i += code.Length;
            }
            else
            {
                i++;
            }
            i = text.IndexOf('\x1b', i);
        }
    }

    /// <summary>Remove ANSI, OSC, and APC control sequences while preserving visible text.</summary>
    public static string StripTerminalSequences(string str)
    {
        if (!str.Contains('\x1b'))
        {
            return str;
        }

        var result = new StringBuilder(str.Length);
        var i = 0;
        while (i < str.Length)
        {
            var ansi = ExtractAnsiCode(str, i);
            if (ansi is { } code)
            {
                i += code.Length;
                continue;
            }
            result.Append(str[i]);
            i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Normalize text for terminal output: expand Thai/Lao AM vowels to their compatibility
    /// decompositions and expand visible tabs to three spaces (tabs inside escape sequences
    /// stay untouched).
    /// </summary>
    public static string NormalizeTerminalOutput(string str)
    {
        var normalized = str;
        if (normalized.Contains('\u0e33') || normalized.Contains('\u0eb3'))
        {
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                sb.Append(ch switch
                {
                    '\u0e33' => "\u0e4d\u0e32",
                    '\u0eb3' => "\u0ecd\u0eb2",
                    _ => ch.ToString(),
                });
            }
            normalized = sb.ToString();
        }

        if (!normalized.Contains('\t'))
        {
            return normalized;
        }

        var result = new StringBuilder(normalized.Length);
        var i = 0;
        while (i < normalized.Length)
        {
            var ansi = ExtractAnsiCode(normalized, i);
            if (ansi is { } code)
            {
                result.Append(code.Code);
                i += code.Length;
                continue;
            }
            result.Append(normalized[i] == '\t' ? "   " : normalized[i].ToString());
            i++;
        }
        return result.ToString();
    }
}

/// <summary>An OSC 8 hyperlink state (open) tracked across wrapped lines.</summary>
internal sealed record ActiveHyperlink(string Params, string Url, string Terminator);

/// <summary>
/// Port of <c>AnsiCodeTracker</c>: tracks active SGR attributes and OSC 8 hyperlinks so styling
/// can be preserved across wrapped line breaks.
/// </summary>
internal sealed class AnsiCodeTracker
{
    private bool _bold;
    private bool _dim;
    private bool _italic;
    private bool _underline;
    private bool _blink;
    private bool _inverse;
    private bool _hidden;
    private bool _strikethrough;
    private string? _fgColor;
    private string? _bgColor;
    private ActiveHyperlink? _activeHyperlink;

    private const string Esc = "\x1b";
    private const string Bel = "\x07";

    public void Process(string ansiCode)
    {
        var hyperlink = ParseOsc8Hyperlink(ansiCode);
        if (hyperlink.IsOsc8)
        {
            // null Hyperlink means an explicit OSC 8 close.
            _activeHyperlink = hyperlink.Hyperlink;
            return;
        }

        if (!ansiCode.EndsWith('m'))
        {
            return;
        }

        // Extract the parameters between ESC [ and m
        var start = ansiCode.IndexOf('[');
        if (start < 0)
        {
            return;
        }
        var paramsPart = ansiCode.Substring(start + 1, ansiCode.Length - start - 2);
        foreach (var ch in paramsPart)
        {
            if (!char.IsDigit(ch) && ch != ';')
            {
                return;
            }
        }

        if (paramsPart.Length == 0 || paramsPart == "0")
        {
            Reset();
            return;
        }

        var parts = paramsPart.Split(';');
        var i = 0;
        while (i < parts.Length)
        {
            if (!int.TryParse(parts[i], out var code))
            {
                i++;
                continue;
            }

            if (code is 38 or 48)
            {
                if (i + 2 < parts.Length && parts[i + 1] == "5" && parts[i + 2].Length > 0)
                {
                    var colorCode = $"{parts[i]};{parts[i + 1]};{parts[i + 2]}";
                    if (code == 38)
                    {
                        _fgColor = colorCode;
                    }
                    else
                    {
                        _bgColor = colorCode;
                    }
                    i += 3;
                    continue;
                }
                if (i + 4 < parts.Length && parts[i + 1] == "2")
                {
                    var colorCode = $"{parts[i]};{parts[i + 1]};{parts[i + 2]};{parts[i + 3]};{parts[i + 4]}";
                    if (code == 38)
                    {
                        _fgColor = colorCode;
                    }
                    else
                    {
                        _bgColor = colorCode;
                    }
                    i += 5;
                    continue;
                }
            }

            switch (code)
            {
                case 0: Reset(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 3: _italic = true; break;
                case 4: _underline = true; break;
                case 5: _blink = true; break;
                case 7: _inverse = true; break;
                case 8: _hidden = true; break;
                case 9: _strikethrough = true; break;
                case 21: _bold = false; break;
                case 22: _bold = false; _dim = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 25: _blink = false; break;
                case 27: _inverse = false; break;
                case 28: _hidden = false; break;
                case 29: _strikethrough = false; break;
                case 39: _fgColor = null; break;
                case 49: _bgColor = null; break;
                default:
                    if ((code >= 30 && code <= 37) || (code >= 90 && code <= 97))
                    {
                        _fgColor = code.ToString();
                    }
                    else if ((code >= 40 && code <= 47) || (code >= 100 && code <= 107))
                    {
                        _bgColor = code.ToString();
                    }
                    break;
            }
            i++;
        }
    }

    private void Reset()
    {
        _bold = false;
        _dim = false;
        _italic = false;
        _underline = false;
        _blink = false;
        _inverse = false;
        _hidden = false;
        _strikethrough = false;
        _fgColor = null;
        _bgColor = null;
        // SGR reset does not affect OSC 8 hyperlink state
    }

    public void Clear()
    {
        Reset();
        _activeHyperlink = null;
    }

    public string GetActiveCodes()
    {
        var codes = new List<string>(10);
        if (_bold) codes.Add("1");
        if (_dim) codes.Add("2");
        if (_italic) codes.Add("3");
        if (_underline) codes.Add("4");
        if (_blink) codes.Add("5");
        if (_inverse) codes.Add("7");
        if (_hidden) codes.Add("8");
        if (_strikethrough) codes.Add("9");
        if (_fgColor is not null) codes.Add(_fgColor);
        if (_bgColor is not null) codes.Add(_bgColor);

        var result = codes.Count > 0 ? $"{Esc}[{string.Join(";", codes)}m" : "";
        if (_activeHyperlink is { } link)
        {
            result += FormatOsc8Hyperlink(link);
        }
        return result;
    }

    /// <summary>Return only the background color code active at the end of the processed text.</summary>
    public string GetActiveBackgroundCode() => _bgColor is not null ? $"{Esc}[{_bgColor}m" : "";

    public bool HasActiveCodes() =>
        _bold || _dim || _italic || _underline || _blink || _inverse || _hidden ||
        _strikethrough || _fgColor is not null || _bgColor is not null || _activeHyperlink is not null;

    public string GetLineEndReset()
    {
        var result = "";
        if (_underline)
        {
            result += $"{Esc}[24m";
        }
        if (_activeHyperlink is { } link)
        {
            result += FormatOsc8Close(link.Terminator);
        }
        return result;
    }

    private static string FormatOsc8Hyperlink(ActiveHyperlink h) =>
        $"{Esc}]8;{h.Params};{h.Url}{h.Terminator}";

    private static string FormatOsc8Close(string terminator) => $"{Esc}]8;;{terminator}";

    /// <summary>Parse an OSC 8 hyperlink sequence. Returns null-holder when not an OSC 8 code.</summary>
    internal static HyperlinkParse ParseOsc8Hyperlink(string ansiCode)
    {
        if (!ansiCode.StartsWith("\x1b]8;", StringComparison.Ordinal))
        {
            return default;
        }

        var terminator = ansiCode.EndsWith('\x07') ? "\x07" : "\x1b\\";
        var bodyEnd = terminator == "\x07" ? ansiCode.Length - 1 : ansiCode.Length - 2;
        var body = ansiCode.Substring(4, bodyEnd - 4);
        var sep = body.IndexOf(';');
        if (sep == -1)
        {
            return default;
        }

        var url = body.Substring(sep + 1);
        if (url.Length == 0)
        {
            // Explicit close: hyperlink cleared.
            return new HyperlinkParse(true, null);
        }
        return new HyperlinkParse(true, new ActiveHyperlink(body.Substring(0, sep), url, terminator));
    }
}

/// <summary>Result of OSC 8 parsing: <see cref="IsOsc8"/> distinguishes "not OSC 8" from "OSC 8 close".</summary>
internal readonly record struct HyperlinkParse(bool IsOsc8, ActiveHyperlink? Hyperlink);
