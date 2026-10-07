using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// An RGB color reported by the terminal. Channels are doubles (not bytes) to match the TS source:
/// colors can carry fractional values, e.g. after mixing in sRGB space.
/// </summary>
public readonly record struct RgbColor(double R, double G, double B);

/// <summary>Terminal color-scheme preference.</summary>
public enum TerminalColorScheme
{
    Dark,
    Light,
}

/// <summary>Port of <c>terminal-colors.ts</c>: OSC 11 background color and DSR color-scheme reports.</summary>
public static partial class TerminalColors
{
    [GeneratedRegex(@"^\x1b\]11;([^\x07\x1b]*)(?:\x07|\x1b\\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Osc11BackgroundRegex();

    [GeneratedRegex(@"^(?:\x1b\[\?997;(1|2)n)+$")]
    private static partial Regex ColorSchemeReportRegex();

    public static bool IsOsc11BackgroundColorResponse(string data) => Osc11BackgroundRegex().IsMatch(data);

    public static RgbColor? ParseOsc11BackgroundColor(string data)
    {
        var match = Osc11BackgroundRegex().Match(data);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value.Substring(1);
            if (Regex.IsMatch(hex, "^[0-9a-f]{6}$", RegexOptions.IgnoreCase))
            {
                return HexToRgb(value);
            }
            if (Regex.IsMatch(hex, "^[0-9a-f]{12}$", RegexOptions.IgnoreCase))
            {
                var r = ParseOscHexChannel(hex.Substring(0, 4));
                var g = ParseOscHexChannel(hex.Substring(4, 4));
                var b = ParseOscHexChannel(hex.Substring(8, 4));
                return r is not null && g is not null && b is not null ? new RgbColor(r.Value, g.Value, b.Value) : null;
            }
            return null;
        }

        var rgbValue = Regex.Replace(value, "^rgba?:", "", RegexOptions.IgnoreCase);
        var parts = rgbValue.Split('/');
        if (parts.Length < 3)
        {
            return null;
        }
        var red = ParseOscHexChannel(parts[0]);
        var green = ParseOscHexChannel(parts[1]);
        var blue = ParseOscHexChannel(parts[2]);
        return red is not null && green is not null && blue is not null
            ? new RgbColor(red.Value, green.Value, blue.Value)
            : null;
    }

    public static TerminalColorScheme? ParseTerminalColorSchemeReport(string data)
    {
        var match = ColorSchemeReportRegex().Match(data);
        if (!match.Success)
        {
            return null;
        }
        return match.Groups[1].Value == "2" ? TerminalColorScheme.Light : TerminalColorScheme.Dark;
    }

    private static RgbColor HexToRgb(string hex)
    {
        var normalized = hex.StartsWith('#') ? hex.Substring(1) : hex;
        return new RgbColor(
            Convert.ToInt32(normalized.Substring(0, 2), 16),
            Convert.ToInt32(normalized.Substring(2, 2), 16),
            Convert.ToInt32(normalized.Substring(4, 2), 16));
    }

    private static int? ParseOscHexChannel(string channel)
    {
        if (!Regex.IsMatch(channel, "^[0-9a-f]+$", RegexOptions.IgnoreCase))
        {
            return null;
        }
        var max = Math.Pow(16, channel.Length) - 1;
        if (max <= 0)
        {
            return null;
        }
        return (int)JsMath.Round(Convert.ToInt32(channel, 16) / max * 255);
    }
}
