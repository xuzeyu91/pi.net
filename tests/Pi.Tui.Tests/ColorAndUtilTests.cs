using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public class ColorTests
{
    [Fact]
    public void ParseColor_HandlesHexShorthandAndFull()
    {
        Assert.Equal(new Color.Rgb(255, 0, 0), Colors.ParseColor("#f00"));
        Assert.Equal(new Color.Rgb(255, 0, 0), Colors.ParseColor("#FF0000"));
        Assert.Equal(new Color.Rgb(255, 128, 0), Colors.ParseColor("#ff8000"));
    }

    [Fact]
    public void ParseColor_HandlesOklchAndOkhsl()
    {
        Assert.Equal(new Color.Oklch(0.5, 0.2, 30), Colors.ParseColor("oklch(50% 0.2 30)"));
        Assert.Equal(new Color.Oklch(0.25, 0.1, 200), Colors.ParseColor("OKLCH(0.25 0.1 200deg)"));

        // okhsl() converts straight to sRGB, so the result is an rgb color.
        var green = Assert.IsType<Color.Rgb>(Colors.ParseColor("okhsl(120 50% 50%)"));
        Assert.True(green.G > green.R && green.G > green.B);
    }

    [Fact]
    public void ParseColor_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => Colors.ParseColor("not-a-color"));
        Assert.Throws<ArgumentException>(() => Colors.ParseColor("#gg0000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Colors.Indexed(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => Colors.Rgb(300, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Colors.Oklch(1.5, 0, 0));
    }

    [Fact]
    public void ColorToHex_MatchesKnownPaletteEntries()
    {
        Assert.Equal("#800000", Colors.ColorToHex(Colors.Indexed(1)));
        Assert.Equal("#ff0000", Colors.ColorToHex(Colors.Indexed(196)));
        Assert.Equal("#ffffff", Colors.ColorToHex(Colors.Indexed(15)));
        Assert.Equal("#080808", Colors.ColorToHex(Colors.Indexed(232)));
        Assert.Equal("#eeeeee", Colors.ColorToHex(Colors.Indexed(255)));
        Assert.Equal("#ff0000", Colors.ColorToHex(Colors.Rgb(255, 0, 0)));
    }

    [Fact]
    public void ColorToHex_UsesJsRounding()
    {
        // JS Math.round(127.5) === 128, whereas .NET's banker's rounding would give 128 here too;
        // 126.5 is the discriminating case (JS 127, banker's 126).
        Assert.Equal("#7f7f7f", Colors.ColorToHex(Colors.Rgb(126.5, 126.5, 126.5)));
        Assert.Equal("#808080", Colors.ColorToHex(Colors.Rgb(127.5, 127.5, 127.5)));
    }

    [Fact]
    public void ColorToRgb_ConvertsIndexedAndOklch()
    {
        Assert.Equal(new RgbColor(0, 0, 0), Colors.ColorToRgb(Colors.Indexed(0)));
        Assert.Equal(new RgbColor(128, 0, 0), Colors.ColorToRgb(Colors.Indexed(1)));

        // oklch(100% 0 0) is white; out-of-gamut chroma is reduced until it fits.
        var white = Colors.ColorToRgb(Colors.Oklch(1, 0, 0));
        Assert.Equal(255, white.R);
        Assert.Equal(255, white.G);
        Assert.Equal(255, white.B);

        var clipped = Colors.ColorToRgb(Colors.Oklch(1, 0.3, 150));
        Assert.Equal(255, clipped.R);
        Assert.Equal(255, clipped.G);
        Assert.Equal(255, clipped.B);
    }

    [Fact]
    public void MixColors_InterpolatesInBothSpaces()
    {
        var srgb = Colors.MixColors(Colors.Rgb(0, 0, 0), Colors.Rgb(255, 255, 255), 0.5, ColorMixSpace.Srgb);
        Assert.Equal("#808080", Colors.ColorToHex(srgb));

        var oklch = Colors.ColorToRgb(Colors.MixColors(Colors.Rgb(0, 0, 0), Colors.Rgb(255, 255, 255), 0.5));
        Assert.Equal(oklch.R, oklch.G);
        Assert.Equal(oklch.G, oklch.B);
        Assert.True(oklch.R > 0 && oklch.R < 255);

        // Endpoints are preserved in both spaces.
        Assert.Equal("#000000", Colors.ColorToHex(Colors.MixColors(Colors.Rgb(0, 0, 0), Colors.Rgb(255, 255, 255), 0, ColorMixSpace.Srgb)));
        Assert.Equal("#ffffff", Colors.ColorToHex(Colors.MixColors(Colors.Rgb(0, 0, 0), Colors.Rgb(255, 255, 255), 1, ColorMixSpace.Srgb)));

        Assert.Throws<ArgumentOutOfRangeException>(() => Colors.MixColors(Colors.Indexed(0), Colors.Indexed(1), 1.5));
    }

    [Fact]
    public void ColorToOklch_RoundTrips()
    {
        var original = Colors.Oklch(0.6, 0.15, 120);
        var channels = Colors.ColorToOklch(original);
        Assert.Equal(0.6, channels.L, 10);
        Assert.Equal(0.15, channels.C, 10);
        Assert.Equal(120, channels.H, 10);

        // A gray has zero chroma.
        Assert.Equal(0, Colors.ColorToOklch(Colors.Rgb(128, 128, 128)).C, 6);
    }

    [Fact]
    public void ColorToOkhsl_ReportsAchromaticAsZeroSaturation()
    {
        var gray = Colors.ColorToOkhsl(Colors.Rgb(128, 128, 128));
        Assert.Equal(0, gray.S);
        Assert.Equal(0, gray.H);
        Assert.True(gray.L is > 0 and < 1);
    }

    [Fact]
    public void AnsiSequences_UseIndexedAndTruecolorForms()
    {
        Assert.Equal("\x1b[38;5;9m", Colors.ForegroundAnsi(Colors.Indexed(9), TerminalColorMode.Color256));
        Assert.Equal("\x1b[48;5;9m", Colors.BackgroundAnsi(Colors.Indexed(9), TerminalColorMode.Color256));

        Assert.Equal("\x1b[38;2;255;0;0m", Colors.ForegroundAnsi(Colors.Rgb(255, 0, 0), TerminalColorMode.Truecolor));

        // In 256-color mode a pure red maps to the 6x6x6 cube entry 196.
        Assert.Equal("\x1b[38;5;196m", Colors.ForegroundAnsi(Colors.Rgb(255, 0, 0), TerminalColorMode.Color256));

        // Indexed colors keep their index even in truecolor mode.
        Assert.Equal("\x1b[38;5;9m", Colors.ForegroundAnsi(Colors.Indexed(9), TerminalColorMode.Truecolor));
    }

    [Fact]
    public void StyleTextWithAnsi_ClosesInReverseOrder()
    {
        var result = Colors.StyleTextWithAnsi("x", "\x1b[31m", "\x1b[44m", new TextAttributes { Bold = true, Italic = true });
        Assert.Equal("\x1b[31m\x1b[44m\x1b[1m\x1b[3mx\x1b[23m\x1b[22m\x1b[49m\x1b[39m", result);
    }

    [Fact]
    public void StyleText_CombinesColorsAndAttributes()
    {
        var result = Colors.StyleText("hi", new TextStyle { Fg = Colors.Indexed(2), Bold = true }, TerminalColorMode.Color256);
        Assert.Equal("\x1b[38;5;2m\x1b[1mhi\x1b[22m\x1b[39m", result);
    }
}

public class OklabTests
{
    [Fact]
    public void RgbToOklab_WhiteAndBlack()
    {
        var white = Oklab.RgbToOklab(new RgbColor(255, 255, 255));
        Assert.Equal(1, white[0], 6);
        Assert.Equal(0, white[1], 6);
        Assert.Equal(0, white[2], 6);

        var black = Oklab.RgbToOklab(new RgbColor(0, 0, 0));
        Assert.Equal(0, black[0], 6);
    }

    [Fact]
    public void OkhslToRgb_AchromaticIsGray()
    {
        foreach (var lightness in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var rgb = Oklab.OkhslToRgb(123, 0, lightness);
            Assert.Equal(rgb.R, rgb.G);
            Assert.Equal(rgb.G, rgb.B);
        }

        Assert.Equal(new RgbColor(0, 0, 0), Oklab.OkhslToRgb(0, 0, 0));
        Assert.Equal(new RgbColor(255, 255, 255), Oklab.OkhslToRgb(0, 0, 1));
    }

    [Fact]
    public void OkhslRoundTrip_KeepsHueAndLightness()
    {
        var rgb = Oklab.OkhslToRgb(210, 0.7, 0.55);
        var back = Oklab.RgbToOkhsl(rgb);
        // Hue survives the round trip within 8-bit sRGB quantisation.
        Assert.True(Math.Abs(back.H - 210) < 0.5, $"hue was {back.H}");
        Assert.Equal(0.55, back.L, 2);
        Assert.Equal(0.7, back.S, 2);
    }

    [Fact]
    public void OklabToOkhslLightness_IsMonotonic()
    {
        Assert.Equal(0, Oklab.OklabToOkhslLightness(0), 9);
        Assert.Equal(1, Oklab.OklabToOkhslLightness(1), 9);
        Assert.True(Oklab.OklabToOkhslLightness(0.25) < Oklab.OklabToOkhslLightness(0.5));
    }
}

public class WheelScrollTests
{
    [Fact]
    public void FixedLineCount_IgnoresVelocity()
    {
        var accelerator = new WheelScrollAccelerator((WheelScrollLines)3);
        Assert.Equal(3, accelerator.Next(1, 0));
        Assert.Equal(3, accelerator.Next(1, 1));
        Assert.Equal(3, accelerator.Next(-1, 2));
    }

    [Fact]
    public void FixedLineCount_FloorsAndClampsToAtLeastOne()
    {
        Assert.Equal(4, new WheelScrollAccelerator((WheelScrollLines)4.9).Next(1, 0));
        Assert.Equal(1, new WheelScrollAccelerator((WheelScrollLines)0.5).Next(1, 0));
        Assert.Equal(1, new WheelScrollAccelerator((WheelScrollLines)(-3)).Next(1, 0));
        Assert.Equal(1, new WheelScrollAccelerator((WheelScrollLines)double.NaN).Next(1, 0));
    }

    [Fact]
    public void AutoMode_WithoutAcceleration_AlwaysMovesOneLine()
    {
        var accelerator = new WheelScrollAccelerator(WheelScrollLines.Auto, accelerate: false);
        Assert.Equal(1, accelerator.Next(1, 0));
        Assert.Equal(1, accelerator.Next(1, 20));
        Assert.Equal(1, accelerator.Next(1, 40));
    }

    [Fact]
    public void AutoMode_AcceleratesWithVelocity()
    {
        var accelerator = new WheelScrollAccelerator(WheelScrollLines.Auto, accelerate: true);

        // The first notch of a gesture always moves one line.
        Assert.Equal(1, accelerator.Next(1, 0));
        // 20 ms later the average gap is 20 ms, so 100 / 20 = 5 lines.
        Assert.Equal(5, accelerator.Next(1, 20));
        // Bursts closer than 5 ms stay at one line.
        Assert.Equal(1, accelerator.Next(1, 23));
    }

    [Fact]
    public void AutoMode_ResetsOnDirectionChangeAndGestureGap()
    {
        var accelerator = new WheelScrollAccelerator(WheelScrollLines.Auto, accelerate: true);
        Assert.Equal(1, accelerator.Next(1, 0));
        Assert.Equal(1, accelerator.Next(-1, 20)); // direction change ends the gesture

        var slow = new WheelScrollAccelerator(WheelScrollLines.Auto, accelerate: true);
        Assert.Equal(1, slow.Next(1, 0));
        Assert.Equal(1, slow.Next(1, 1000)); // longer than GESTURE_GAP_MS
    }

    [Fact]
    public void AutoMode_CarriesFractionalLines()
    {
        var accelerator = new WheelScrollAccelerator(WheelScrollLines.Auto, accelerate: true);
        Assert.Equal(1, accelerator.Next(1, 0));
        // gap 33 ms -> averageGap 33 -> 100/33 = 3.03 lines, whole = 3, carry = 0.03
        Assert.Equal(3, accelerator.Next(1, 33));
        // The carry keeps accumulating instead of being dropped.
        var total = 0;
        var now = 33.0;
        for (var index = 0; index < 10; index++)
        {
            now += 200; // each event ends the gesture, so it restarts at one line
            total += accelerator.Next(1, now);
        }
        Assert.Equal(10, total);
    }
}

public class NativePlatformTests
{
    [Fact]
    public void WithoutHelper_EverythingReportsUnavailable()
    {
        NativePlatform.SetNativePlatformHelper(null);
        Assert.False(NativePlatform.IsNativeModifierPressed(ModifierKey.Command));

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.Null(NativePlatform.GetNativePlatformHelper());
        }
        else
        {
            // Other platforms have no native helper at all.
            Assert.Null(NativePlatform.GetNativePlatformHelper());
            Assert.Null(NativePlatform.GetNativeClipboard());
        }
    }

    [Fact]
    public void InstalledHelper_IsUsedAndFailuresAreSwallowed()
    {
        NativePlatform.SetNativePlatformHelper(new ThrowingHelper());
        try
        {
            // A throwing helper must not propagate out of the query.
            Assert.False(NativePlatform.IsNativeModifierPressed(ModifierKey.Shift));
        }
        finally
        {
            NativePlatform.SetNativePlatformHelper(null);
        }
    }

    [Fact]
    public void GetNativeModuleCandidates_ProbesTheDocumentedLocations()
    {
        var candidates = NativePlatform.GetNativeModuleCandidates("native/win32/prebuilds/win32-x64/win32-platform.node", "/tmp/tui");
        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, candidate => Assert.EndsWith("win32-platform.node", candidate));
        Assert.Contains(candidates, candidate => candidate.Replace('\\', '/').EndsWith("/tmp/native/win32/prebuilds/win32-x64/win32-platform.node"));
    }

    private sealed class ThrowingHelper : INativePlatformHelper
    {
        public Task<NativeClipboardText?> GetTextAsync() => Task.FromResult<NativeClipboardText?>(null);

        public Task<byte[]?> GetImageAsync() => Task.FromResult<byte[]?>(null);

        public Task<string[]?> GetFilePathsAsync() => Task.FromResult<string[]?>(null);

        public Task SetTextAsync(string text) => Task.CompletedTask;

        public bool EnableVirtualTerminalInput() => false;

        public bool IsModifierPressed(ModifierKey key) => throw new InvalidOperationException("no display");
    }
}
