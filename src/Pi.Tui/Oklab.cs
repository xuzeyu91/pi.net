namespace Pi.Tui;

/// <summary>
/// Oklab and OKHSL &lt;-&gt; sRGB conversion (port of <c>oklab.ts</c>). <see cref="Colors"/> builds its
/// OKLCH, OKHSL, and color mixing on it.
///
/// Oklab and OKHSL are Björn Ottosson's color spaces; OKHSL's saturation is relative to the sRGB gamut
/// at each hue and lightness. This is a port of his reference implementation
/// (https://bottosson.github.io/posts/colorpicker/), Copyright (c) 2021 Björn Ottosson, used under
/// the MIT license.
/// </summary>
public static class Oklab
{
    private static readonly double[][] LinearSrgbToLms =
    {
        new[] { 0.4122214694707629, 0.5363325372617349, 0.0514459932675022 },
        new[] { 0.2119034958178251, 0.6806995506452344, 0.1073969535369405 },
        new[] { 0.0883024591900564, 0.2817188391361215, 0.6299787016738222 },
    };

    private static readonly double[][] LmsToLab =
    {
        new[] { 0.210454268309314, 0.793617774702305, -0.0040720430116193 },
        new[] { 1.9779985324311684, -2.42859224204858, 0.450593709617411 },
        new[] { 0.0259040424655478, 0.7827717124575296, -0.8086757549230774 },
    };

    private static readonly double[][] LabToLms =
    {
        new[] { 1.0, 0.3963377773761749, 0.2158037573099136 },
        new[] { 1.0, -0.1055613458156586, -0.0638541728258133 },
        new[] { 1.0, -0.0894841775298119, -1.2914855480194092 },
    };

    private static readonly double[][] LmsToLinearSrgb =
    {
        new[] { 4.0767416360759583, -3.3077115392580629, 0.2309699031821043 },
        new[] { -1.2684379732850315, 2.6097573492876882, -0.341319376002657 },
        new[] { -0.0041960761386756, -0.7034186179359362, 1.7076146940746117 },
    };

    /// <summary>
    /// Per sRGB channel (red, green, blue): the (a, b) half-plane where that channel clips first, and
    /// the polynomial approximating the maximum saturation there.
    /// </summary>
    private static readonly (double[] HalfPlane, double[] Poly)[] SaturationFit =
    {
        (new[] { -1.8817031, -0.80936501 }, new[] { 1.19086277, 1.76576728, 0.59662641, 0.75515197, 0.56771245 }),
        (new[] { 1.8144408, -1.19445267 }, new[] { 0.73956515, -0.45954404, 0.08285427, 0.12541073, -0.14503204 }),
        (new[] { 0.13110758, 1.81333971 }, new[] { 1.35733652, -0.00915799, -1.1513021, -0.50559606, 0.00692167 }),
    };

    private const double K1 = 0.206;
    private const double K2 = 0.03;
    private const double K3 = (1 + K1) / (1 + K2);

    private static double[] Multiply(double[][] m, double[] v) => new[]
    {
        m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
        m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
        m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
    };

    /// <summary>Oklab lightness to OKHSL lightness.</summary>
    public static double OklabToOkhslLightness(double x) =>
        0.5 * (K3 * x - K1 + Math.Sqrt((K3 * x - K1) * (K3 * x - K1) + 4 * K2 * K3 * x));

    /// <summary>OKHSL lightness to Oklab lightness.</summary>
    private static double OkhslToOklabLightness(double x) => (x * x + K1 * x) / (K3 * (x + K2));

    /// <summary>sRGB transfer function: linear to encoded channel, both 0-1.</summary>
    private static double LinearToSrgb(double value) =>
        value > 0.0031308 ? 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055 : 12.92 * value;

    /// <summary>Inverse sRGB transfer function: encoded to linear channel, both 0-1.</summary>
    private static double SrgbToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>Oklab [L, a, b] to linear sRGB [r, g, b] (0-1, may leave the gamut).</summary>
    public static double[] OklabToLinearSrgb(double[] lab)
    {
        var lms = Multiply(LabToLms, lab);
        var cubed = new[] { lms[0] * lms[0] * lms[0], lms[1] * lms[1] * lms[1], lms[2] * lms[2] * lms[2] };
        return Multiply(LmsToLinearSrgb, cubed);
    }

    /// <summary>Linear sRGB [r, g, b] (0-1) to Oklab [L, a, b].</summary>
    private static double[] LinearSrgbToOklab(double[] rgb)
    {
        var lms = Multiply(LinearSrgbToLms, rgb);
        return Multiply(LmsToLab, new[] { Math.Cbrt(lms[0]), Math.Cbrt(lms[1]), Math.Cbrt(lms[2]) });
    }

    /// <summary>sRGB channels (0-255) to Oklab [L, a, b].</summary>
    public static double[] RgbToOklab(RgbColor rgb) =>
        LinearSrgbToOklab(new[] { SrgbToLinear(rgb.R / 255.0), SrgbToLinear(rgb.G / 255.0), SrgbToLinear(rgb.B / 255.0) });

    /// <summary>Linear sRGB [r, g, b] to sRGB channels (0-255, rounded), clipping out-of-gamut channels.</summary>
    public static RgbColor LinearSrgbToRgb(double[] linear) => new(
        (int)JsMath.Round(Math.Min(1, Math.Max(0, LinearToSrgb(linear[0]))) * 255),
        (int)JsMath.Round(Math.Min(1, Math.Max(0, LinearToSrgb(linear[1]))) * 255),
        (int)JsMath.Round(Math.Min(1, Math.Max(0, LinearToSrgb(linear[2]))) * 255));

    /// <summary>Rate of change of each cube-root LMS component along a chroma direction (a, b).</summary>
    private static double[] LmsSlopes(double a, double b) => new[]
    {
        LabToLms[0][1] * a + LabToLms[0][2] * b,
        LabToLms[1][1] * a + LabToLms[1][2] * b,
        LabToLms[2][1] * a + LabToLms[2][2] * b,
    };

    /// <summary>Largest saturation (C/L) inside sRGB for hue (a, b): polynomial fit plus one Halley step.</summary>
    private static double MaxSaturation(double a, double b)
    {
        var channel = 2;
        for (var index = 0; index < 2; index++)
        {
            var (x, y) = (SaturationFit[index].HalfPlane[0], SaturationFit[index].HalfPlane[1]);
            if (x * a + y * b > 1)
            {
                channel = index;
                break;
            }
        }

        var poly = SaturationFit[channel].Poly;
        var weights = LmsToLinearSrgb[channel];
        var saturation = poly[0] + poly[1] * a + poly[2] * b + poly[3] * a * a + poly[4] * a * b;

        var slopes = LmsSlopes(a, b);
        var baseValues = new[] { 1 + saturation * slopes[0], 1 + saturation * slopes[1], 1 + saturation * slopes[2] };

        double Dot(double[] values)
        {
            var sum = 0.0;
            for (var index = 0; index < 3; index++)
            {
                sum += weights[index] * values[index];
            }
            return sum;
        }

        var f = Dot(new[] { baseValues[0] * baseValues[0] * baseValues[0], baseValues[1] * baseValues[1] * baseValues[1], baseValues[2] * baseValues[2] * baseValues[2] });
        var f1 = Dot(new[]
        {
            3 * slopes[0] * baseValues[0] * baseValues[0],
            3 * slopes[1] * baseValues[1] * baseValues[1],
            3 * slopes[2] * baseValues[2] * baseValues[2],
        });
        var f2 = Dot(new[]
        {
            6 * slopes[0] * slopes[0] * baseValues[0],
            6 * slopes[1] * slopes[1] * baseValues[1],
            6 * slopes[2] * slopes[2] * baseValues[2],
        });
        return saturation - (f * f1) / (f1 * f1 - 0.5 * f * f2);
    }

    /// <summary>Oklab lightness and chroma of the most saturated sRGB color of hue (a, b).</summary>
    private static (double L, double C) Cusp(double a, double b)
    {
        var saturation = MaxSaturation(a, b);
        var linear = OklabToLinearSrgb(new[] { 1.0, saturation * a, saturation * b });
        var lightness = Math.Cbrt(1 / Math.Max(linear[0], Math.Max(linear[1], linear[2])));
        return (lightness, lightness * saturation);
    }

    /// <summary>Chroma where the constant-lightness line at <paramref name="lightness"/> leaves the sRGB gamut.</summary>
    private static double MaxChroma(double a, double b, double lightness, double cuspL, double cuspC)
    {
        if (lightness <= cuspL)
        {
            return cuspC * lightness / cuspL;
        }

        // Upper half: triangle edge, then one Halley step against each channel reaching 1.
        var t = cuspC * (lightness - 1) / (cuspL - 1);
        var slopes = LmsSlopes(a, b);
        var lms = new[] { lightness + t * slopes[0], lightness + t * slopes[1], lightness + t * slopes[2] };
        var cubes = new[] { lms[0] * lms[0] * lms[0], lms[1] * lms[1] * lms[1], lms[2] * lms[2] * lms[2] };
        var first = new[]
        {
            3 * slopes[0] * lms[0] * lms[0],
            3 * slopes[1] * lms[1] * lms[1],
            3 * slopes[2] * lms[2] * lms[2],
        };
        var second = new[]
        {
            6 * slopes[0] * slopes[0] * lms[0],
            6 * slopes[1] * slopes[1] * lms[1],
            6 * slopes[2] * slopes[2] * lms[2],
        };

        var minStep = double.MaxValue;
        for (var index = 0; index < 3; index++)
        {
            var row = LmsToLinearSrgb[index];
            var f = row[0] * cubes[0] + row[1] * cubes[1] + row[2] * cubes[2] - 1;
            var f1 = row[0] * first[0] + row[1] * first[1] + row[2] * first[2];
            var f2 = row[0] * second[0] + row[1] * second[1] + row[2] * second[2];
            var u = f1 / (f1 * f1 - 0.5 * f * f2);
            var step = u >= 0 ? -f * u : double.MaxValue;
            minStep = Math.Min(minStep, step);
        }
        return t + minStep;
    }

    /// <summary>OKHSL's chroma reference points at lightness L and hue (a, b): [c0, cMid, cMax].</summary>
    private static (double C0, double CMid, double CMax) ChromaStops(double l, double a, double b)
    {
        var (peakL, peakC) = Cusp(a, b);
        var cMax = MaxChroma(a, b, l, peakL, peakC);
        var k = cMax / Math.Min(l * (peakC / peakL), (1 - l) * (peakC / (1 - peakL)));
        var midS =
            0.11516993 +
            1 /
            (7.4477897 +
             4.1590124 * b +
             a * (-2.19557347 + 1.75198401 * b + a * (-2.13704948 - 10.02301043 * b + a * (-4.24894561 + 5.38770819 * b + 4.69891013 * a))));
        var midT =
            0.11239642 +
            1 /
            (1.6132032 -
             0.68124379 * b +
             a * (0.40370612 + 0.90148123 * b + a * (-0.27087943 + 0.6122399 * b + a * (0.00299215 - 0.45399568 * b - 0.14661872 * a))));
        var cMid = 0.9 * k * Math.Sqrt(Math.Sqrt(1 / (Math.Pow(1 / (l * midS), 4) + Math.Pow(1 / ((1 - l) * midT), 4))));
        var c0 = Math.Sqrt(1 / (Math.Pow(1 / (l * 0.4), 2) + Math.Pow(1 / ((1 - l) * 0.8), 2)));
        return (c0, cMid, cMax);
    }

    /// <summary>
    /// Convert OKHSL to sRGB channels (0-255, rounded), clipping out-of-gamut channels.
    /// </summary>
    /// <param name="hue">Hue in degrees.</param>
    /// <param name="saturation">Saturation, 0-1.</param>
    /// <param name="lightness">Lightness, 0-1.</param>
    public static RgbColor OkhslToRgb(double hue, double saturation, double lightness)
    {
        var l = OkhslToOklabLightness(lightness);
        var lab = new[] { l, 0.0, 0.0 };
        if (l > 0 && l < 1 && saturation > 0)
        {
            var angle = 2 * Math.PI * (((hue % 360) + 360) % 360) / 360;
            var a = Math.Cos(angle);
            var b = Math.Sin(angle);
            var (c0, cMid, cMax) = ChromaStops(l, a, b);
            // Chroma rises from 0 through cMid at s = 0.8 to cMax at s = 1.
            double chroma;
            if (saturation < 0.8)
            {
                var t = 1.25 * saturation;
                var k1 = 0.8 * c0;
                chroma = t * k1 / (1 - (1 - k1 / cMid) * t);
            }
            else
            {
                var t = 5 * (saturation - 0.8);
                var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0;
                chroma = cMid + t * k1 / (1 - (1 - k1 / (cMax - cMid)) * t);
            }
            lab = new[] { l, chroma * a, chroma * b };
        }
        return LinearSrgbToRgb(OklabToLinearSrgb(lab));
    }

    /// <summary>
    /// Convert sRGB channels (0-255) to OKHSL.
    /// </summary>
    /// <returns>Hue <c>H</c> in degrees (0 for grays), saturation <c>S</c> and lightness <c>L</c> 0-1.</returns>
    public static OkhslChannels RgbToOkhsl(RgbColor rgb)
    {
        var lab = RgbToOklab(rgb);
        var (l, labA, labB) = (lab[0], lab[1], lab[2]);
        var chroma = Math.Sqrt(labA * labA + labB * labB);
        var lightness = OklabToOkhslLightness(l);
        if (chroma < 1e-9 || lightness <= 0 || lightness >= 1)
        {
            return new OkhslChannels(0, 0, lightness);
        }

        var hue = (Math.Atan2(labB, labA) * 180 / Math.PI + 360) % 360;
        var (c0, cMid, cMax) = ChromaStops(l, labA / chroma, labB / chroma);
        double saturation;
        if (chroma < cMid)
        {
            var k1 = 0.8 * c0;
            saturation = 0.8 * (chroma / (k1 + (1 - k1 / cMid) * chroma));
        }
        else
        {
            var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0;
            var offset = chroma - cMid;
            saturation = 0.8 + 0.2 * (offset / (k1 + (1 - k1 / (cMax - cMid)) * offset));
        }
        return new OkhslChannels(hue, Math.Min(1, Math.Max(0, saturation)), lightness);
    }
}
