using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>JavaScript-compatible numeric helpers (the TS sources use <c>Math.round</c> semantics).</summary>
public static class JsMath
{
    /// <summary>
    /// Equivalent of JS <c>Math.round</c>: rounds half towards positive infinity. .NET's
    /// <see cref="Math.Round(double)"/> defaults to banker's rounding, which differs on exact .5
    /// values (JS <c>Math.round(2.5) == 3</c>, .NET <c>Math.Round(2.5) == 2</c>).
    /// </summary>
    public static double Round(double value) => Math.Floor(value + 0.5);
}

/// <summary>
/// A concrete color (port of <c>colors.ts</c>'s <c>Color</c> discriminated union). Every color can be
/// converted to sRGB, so color math never fails.
/// </summary>
public abstract record Color
{
    private protected Color()
    {
    }

    /// <summary>An ANSI 256-color palette index (0-255).</summary>
    public sealed record Indexed(int Index) : Color;

    /// <summary>Direct sRGB channels (0-255).</summary>
    public sealed record Rgb(double R, double G, double B) : Color;

    /// <summary>OKLCH: perceptual lightness 0-1, chroma, hue in degrees.</summary>
    public sealed record Oklch(double L, double C, double H) : Color;
}

/// <summary>Terminal color capability.</summary>
public enum TerminalColorMode
{
    /// <summary>256-color mode (<c>256color</c>).</summary>
    Color256,

    /// <summary>24-bit direct color (<c>truecolor</c>).</summary>
    Truecolor,
}

/// <summary>Color space used by <see cref="Colors.MixColors"/>.</summary>
public enum ColorMixSpace
{
    Oklch,
    Srgb,
}

/// <summary>OKLCH channels.</summary>
public readonly record struct OklchChannels(double L, double C, double H);

/// <summary>
/// OKHSL channels: hue in degrees, saturation and lightness 0-1. Saturation is relative to the most
/// the sRGB gamut allows at that hue and lightness, so every value is in gamut.
/// </summary>
public readonly record struct OkhslChannels(double H, double S, double L);

/// <summary>Text attributes that can be applied without a color.</summary>
public record TextAttributes
{
    public bool Bold { get; init; }
    public bool Dim { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Inverse { get; init; }
    public bool Strikethrough { get; init; }
}

/// <summary>Text attributes plus optional foreground/background colors.</summary>
public sealed record TextStyle : TextAttributes
{
    public Color? Fg { get; init; }
    public Color? Bg { get; init; }
}

/// <summary>Port of <c>colors.ts</c>: color values, parsing, conversion and ANSI styling.</summary>
public static partial class Colors
{
    // Note: [0-9] rather than \d — .NET's \d matches every Unicode decimal digit while JS's does not.
    private const string NumberPattern = @"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:e[+-]?[0-9]+)?";

    [GeneratedRegex(@"^#([0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^oklch\(\s*(" + NumberPattern + @")(%)?\s+(" + NumberPattern + @")\s+(" + NumberPattern + @")(?:deg)?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex OklchPattern();

    [GeneratedRegex(@"^okhsl\(\s*(" + NumberPattern + @")(?:deg)?\s+(" + NumberPattern + @")(%)?\s+(" + NumberPattern + @")(%)?\s*\)$", RegexOptions.IgnoreCase)]
    private static partial Regex OkhslPattern();

    private static void RequireFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException($"{name} must be finite", nameof(value));
        }
    }

    /// <summary>Create an indexed (ANSI 256) color.</summary>
    public static Color Indexed(int index)
    {
        if (index < 0 || index > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"ANSI color index must be an integer from 0 to 255: {index}");
        }
        return new Color.Indexed(index);
    }

    /// <summary>Create an sRGB color from channels 0-255.</summary>
    public static Color Rgb(double r, double g, double b)
    {
        foreach (var (name, value) in new[] { ("r", r), ("g", g), ("b", b) })
        {
            RequireFinite(value, name);
            if (value < 0 || value > 255)
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name} must be between 0 and 255: {value}");
            }
        }
        return new Color.Rgb(r, g, b);
    }

    /// <summary>Create an OKLCH color (lightness 0-1, chroma &gt;= 0, hue in degrees).</summary>
    public static Color Oklch(double l, double c, double h)
    {
        RequireFinite(l, "l");
        RequireFinite(c, "c");
        RequireFinite(h, "h");
        if (l < 0 || l > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(l), l, $"l must be between 0 and 1: {l}");
        }
        if (c < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(c), c, $"c must not be negative: {c}");
        }
        return new Color.Oklch(l, c, ((h % 360) + 360) % 360);
    }

    /// <summary>
    /// An OKHSL color, converted to sRGB. Saturation is relative to the sRGB gamut at the hue and
    /// lightness, so equal saturation looks equally colorful across hues and lightness.
    /// </summary>
    /// <param name="h">Hue in degrees.</param>
    /// <param name="s">Saturation, 0-1.</param>
    /// <param name="l">Lightness, 0-1.</param>
    public static Color Okhsl(double h, double s, double l)
    {
        RequireFinite(h, "h");
        RequireFinite(s, "s");
        RequireFinite(l, "l");
        if (s < 0 || s > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(s), s, $"s must be between 0 and 1: {s}");
        }
        if (l < 0 || l > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(l), l, $"l must be between 0 and 1: {l}");
        }
        var rgb = Oklab.OkhslToRgb(h, s, l);
        return new Color.Rgb(rgb.R, rgb.G, rgb.B);
    }

    /// <summary>OKHSL channels of a color.</summary>
    public static OkhslChannels ColorToOkhsl(Color color) => Oklab.RgbToOkhsl(ColorToRgb(color));

    /// <summary>Parse a color from an ANSI index, <c>#rgb</c>/<c>#rrggbb</c> hex, <c>oklch()</c> or <c>okhsl()</c>.</summary>
    public static Color ParseColor(int value) => Indexed(value);

    /// <summary>Parse a color from <c>#rgb</c>/<c>#rrggbb</c> hex, <c>oklch()</c> or <c>okhsl()</c>.</summary>
    public static Color ParseColor(string value)
    {
        var hex = HexPattern().Match(value);
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value;
            if (digits.Length == 3)
            {
                digits = string.Concat(digits.Select(digit => new string(digit, 2)));
            }
            return Rgb(
                Convert.ToInt32(digits.Substring(0, 2), 16),
                Convert.ToInt32(digits.Substring(2, 2), 16),
                Convert.ToInt32(digits.Substring(4, 2), 16));
        }

        var oklch = OklchPattern().Match(value);
        if (oklch.Success)
        {
            var lightness = double.Parse(oklch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / (oklch.Groups[2].Success ? 100 : 1);
            return Oklch(
                lightness,
                double.Parse(oklch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(oklch.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        var okhsl = OkhslPattern().Match(value);
        if (okhsl.Success)
        {
            var saturation = double.Parse(okhsl.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) / (okhsl.Groups[3].Success ? 100 : 1);
            var lightness = double.Parse(okhsl.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) / (okhsl.Groups[5].Success ? 100 : 1);
            return Okhsl(
                double.Parse(okhsl.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                saturation,
                lightness);
        }

        throw new ArgumentException($"Invalid color value: {value}", nameof(value));
    }

    private static readonly RgbColor[] BasicColors =
    {
        new(0, 0, 0),
        new(128, 0, 0),
        new(0, 128, 0),
        new(128, 128, 0),
        new(0, 0, 128),
        new(128, 0, 128),
        new(0, 128, 128),
        new(192, 192, 192),
        new(128, 128, 128),
        new(255, 0, 0),
        new(0, 255, 0),
        new(255, 255, 0),
        new(0, 0, 255),
        new(255, 0, 255),
        new(0, 255, 255),
        new(255, 255, 255),
    };

    private static readonly int[] CubeValues = { 0, 95, 135, 175, 215, 255 };

    private static readonly int[] GrayValues = Enumerable.Range(0, 24).Select(index => 8 + index * 10).ToArray();

    private static RgbColor IndexedToRgb(int index)
    {
        if (index < 16)
        {
            return BasicColors[index];
        }
        if (index < 232)
        {
            var cubeIndex = index - 16;
            return new RgbColor(
                CubeValues[cubeIndex / 36],
                CubeValues[cubeIndex % 36 / 6],
                CubeValues[cubeIndex % 6]);
        }
        var gray = 8 + (index - 232) * 10;
        return new RgbColor(gray, gray, gray);
    }

    private static bool IsInSrgbGamut(double[] linear)
    {
        const double epsilon = 1e-7;
        return linear.All(channel => channel >= -epsilon && channel <= 1 + epsilon);
    }

    private static RgbColor OklchToRgb(OklchChannels channels)
    {
        var (l, c, h) = (channels.L, channels.C, channels.H);
        // Gamut mapping keeps the hue fixed, so its direction is computed once and scaled by chroma.
        var radians = h * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        double[] AtChroma(double chroma) => Oklab.OklabToLinearSrgb(new[] { l, chroma * cos, chroma * sin });

        var direct = AtChroma(c);
        if (IsInSrgbGamut(direct))
        {
            return Oklab.LinearSrgbToRgb(direct);
        }

        // Reduce chroma until the color fits. The achromatic color is always in gamut, so it is the
        // fallback when no bisection step fits, e.g. `oklch(100% 0.3 150)` must map to white.
        var linear = AtChroma(0);
        var low = 0.0;
        var high = c;
        for (var index = 0; index < 20; index++)
        {
            var chroma = (low + high) / 2;
            var candidate = AtChroma(chroma);
            if (IsInSrgbGamut(candidate))
            {
                low = chroma;
                linear = candidate;
            }
            else
            {
                high = chroma;
            }
        }
        return Oklab.LinearSrgbToRgb(linear);
    }

    /// <summary>Convert any color to sRGB channels.</summary>
    public static RgbColor ColorToRgb(Color color) => color switch
    {
        Color.Indexed indexed => IndexedToRgb(indexed.Index),
        Color.Rgb rgb => new RgbColor(rgb.R, rgb.G, rgb.B),
        Color.Oklch oklch => OklchToRgb(new OklchChannels(oklch.L, oklch.C, oklch.H)),
        _ => throw new ArgumentException($"Unknown color kind: {color}", nameof(color)),
    };

    /// <summary>Convert any color to OKLCH channels.</summary>
    public static OklchChannels ColorToOklch(Color color)
    {
        if (color is Color.Oklch oklch)
        {
            return new OklchChannels(oklch.L, oklch.C, oklch.H);
        }
        var lab = Oklab.RgbToOklab(ColorToRgb(color));
        return new OklchChannels(lab[0], Math.Sqrt(lab[1] * lab[1] + lab[2] * lab[2]), (Math.Atan2(lab[2], lab[1]) * 180 / Math.PI + 360) % 360);
    }

    /// <summary>Convert any color to a <c>#rrggbb</c> hex string.</summary>
    public static string ColorToHex(Color color)
    {
        var rgb = ColorToRgb(color);
        return $"#{Channel(rgb.R)}{Channel(rgb.G)}{Channel(rgb.B)}";

        static string Channel(double value) => ((int)JsMath.Round(value)).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Blend two colors.</summary>
    /// <param name="first">Start color.</param>
    /// <param name="second">End color.</param>
    /// <param name="amount">Blend amount 0-1 (0 = first, 1 = second).</param>
    /// <param name="space">Color space to blend in; defaults to OKLCH for perceptually even mixing.</param>
    public static Color MixColors(Color first, Color second, double amount, ColorMixSpace space = ColorMixSpace.Oklch)
    {
        RequireFinite(amount, "amount");
        if (amount < 0 || amount > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, $"amount must be between 0 and 1: {amount}");
        }

        if (space == ColorMixSpace.Srgb)
        {
            var a = ColorToRgb(first);
            var b = ColorToRgb(second);
            return Rgb(
                a.R + (b.R - a.R) * amount,
                a.G + (b.G - a.G) * amount,
                a.B + (b.B - a.B) * amount);
        }

        var firstOklch = ColorToOklch(first);
        var secondOklch = ColorToOklch(second);
        var firstHue = firstOklch.C < 1e-7 ? secondOklch.H : firstOklch.H;
        var secondHue = secondOklch.C < 1e-7 ? firstHue : secondOklch.H;
        var hueDelta = (secondHue - firstHue + 540) % 360 - 180;
        return Oklch(
            firstOklch.L + (secondOklch.L - firstOklch.L) * amount,
            firstOklch.C + (secondOklch.C - firstOklch.C) * amount,
            firstHue + hueDelta * amount);
    }

    private static int FindClosest(int[] values, double target)
    {
        var closestIndex = 0;
        var closestDistance = double.PositiveInfinity;
        for (var index = 0; index < values.Length; index++)
        {
            var distance = Math.Abs(target - values[index]);
            if (distance < closestDistance)
            {
                closestIndex = index;
                closestDistance = distance;
            }
        }
        return closestIndex;
    }

    private static double ColorDistance(RgbColor first, RgbColor second)
    {
        var dr = first.R - second.R;
        var dg = first.G - second.G;
        var db = first.B - second.B;
        return dr * dr * 0.299 + dg * dg * 0.587 + db * db * 0.114;
    }

    private static int RgbToAnsi256(RgbColor color)
    {
        var rIndex = FindClosest(CubeValues, color.R);
        var gIndex = FindClosest(CubeValues, color.G);
        var bIndex = FindClosest(CubeValues, color.B);
        var cubeColor = new RgbColor(CubeValues[rIndex], CubeValues[gIndex], CubeValues[bIndex]);
        var cubeIndex = 16 + 36 * rIndex + 6 * gIndex + bIndex;

        var gray = (int)JsMath.Round(0.299 * color.R + 0.587 * color.G + 0.114 * color.B);
        var grayOffset = FindClosest(GrayValues, gray);
        var grayValue = GrayValues[grayOffset];
        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        if (spread < 10 && ColorDistance(color, new RgbColor(grayValue, grayValue, grayValue)) < ColorDistance(color, cubeColor))
        {
            return 232 + grayOffset;
        }
        return cubeIndex;
    }

    private static string ColorAnsi(Color color, TerminalColorMode mode, bool background)
    {
        var selector = background ? 48 : 38;
        if (color is Color.Indexed indexed)
        {
            return $"\x1b[{selector};5;{indexed.Index}m";
        }

        var rgb = ColorToRgb(color);
        if (mode == TerminalColorMode.Truecolor)
        {
            return $"\x1b[{selector};2;{(int)JsMath.Round(rgb.R)};{(int)JsMath.Round(rgb.G)};{(int)JsMath.Round(rgb.B)}m";
        }
        return $"\x1b[{selector};5;{RgbToAnsi256(rgb)}m";
    }

    /// <summary>ANSI foreground escape sequence for a color.</summary>
    public static string ForegroundAnsi(Color color, TerminalColorMode mode) => ColorAnsi(color, mode, false);

    /// <summary>ANSI background escape sequence for a color.</summary>
    public static string BackgroundAnsi(Color color, TerminalColorMode mode) => ColorAnsi(color, mode, true);

    /// <summary>Wrap text in the given style's colors and attributes.</summary>
    public static string StyleText(string text, TextStyle options, TerminalColorMode mode) => StyleTextWithAnsi(
        text,
        options.Fg is { } fg ? ForegroundAnsi(fg, mode) : null,
        options.Bg is { } bg ? BackgroundAnsi(bg, mode) : null,
        options);

    /// <summary>
    /// Like <see cref="StyleText"/>, but with precomputed color escape sequences, e.g. cached theme
    /// colors. Colors in <paramref name="options"/> are ignored.
    /// </summary>
    public static string StyleTextWithAnsi(string text, string? fgAnsi, string? bgAnsi, TextAttributes options)
    {
        // Resets are prepended so they close in reverse order of the opening sequences.
        var prefix = "";
        var suffix = "";
        if (!string.IsNullOrEmpty(fgAnsi))
        {
            prefix += fgAnsi;
            suffix = "\x1b[39m";
        }
        if (!string.IsNullOrEmpty(bgAnsi))
        {
            prefix += bgAnsi;
            suffix = $"\x1b[49m{suffix}";
        }
        if (options.Bold)
        {
            prefix += "\x1b[1m";
        }
        if (options.Dim)
        {
            prefix += "\x1b[2m";
        }
        if (options.Bold || options.Dim)
        {
            suffix = $"\x1b[22m{suffix}";
        }
        if (options.Italic)
        {
            prefix += "\x1b[3m";
            suffix = $"\x1b[23m{suffix}";
        }
        if (options.Underline)
        {
            prefix += "\x1b[4m";
            suffix = $"\x1b[24m{suffix}";
        }
        if (options.Inverse)
        {
            prefix += "\x1b[7m";
            suffix = $"\x1b[27m{suffix}";
        }
        if (options.Strikethrough)
        {
            prefix += "\x1b[9m";
            suffix = $"\x1b[29m{suffix}";
        }
        return $"{prefix}{text}{suffix}";
    }
}
