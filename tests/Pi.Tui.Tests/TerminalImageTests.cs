using System.Text;
using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Saves and restores the capability cache and cell dimensions around a test.</summary>
internal sealed class CapabilitiesScope : IDisposable
{
    private readonly TerminalCapabilities _originalCapabilities;
    private readonly CellDimensions _originalCells;

    public CapabilitiesScope(TerminalCapabilities capabilities)
    {
        _originalCapabilities = TerminalImage.GetCapabilities();
        _originalCells = TerminalImage.GetCellDimensions();
        TerminalImage.SetCapabilities(capabilities);
    }

    public void Dispose()
    {
        TerminalImage.SetCapabilities(_originalCapabilities);
        TerminalImage.SetCellDimensions(_originalCells);
    }
}

/// <summary>Sets environment variables for the duration of a test, restoring the previous values.</summary>
internal sealed class EnvScope : IDisposable
{
    private readonly List<(string Name, string? Previous)> _saved = new();

    public EnvScope(params (string Name, string? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Clears every environment variable the capability detector reads.</summary>
    public static EnvScope ClearTerminalEnvironment() => new(
        ("TERM", null),
        ("TERM_PROGRAM", null),
        ("TERMINAL_EMULATOR", null),
        ("COLORTERM", null),
        ("TMUX", null),
        ("KITTY_WINDOW_ID", null),
        ("GHOSTTY_RESOURCES_DIR", null),
        ("WEZTERM_PANE", null),
        ("WARP_SESSION_ID", null),
        ("WARP_TERMINAL_SESSION_UUID", null),
        ("ITERM_SESSION_ID", null),
        ("WT_SESSION", null),
        ("PI_HYPERLINKS", null),
        ("PI_IMAGE_PROTOCOL", null),
        ("PI_TRUE_COLOR", null));

    public void Dispose()
    {
        foreach (var (name, previous) in _saved)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}

public class TerminalImageEncodingTests
{
    [Fact]
    public void EncodeKitty_SingleChunkCarriesPlacementControls()
    {
        var result = TerminalImage.EncodeKitty("QUJD", new KittyEncodeOptions
        {
            Columns = 10,
            Rows = 5,
            ImageId = 7,
        });

        Assert.Equal("\x1b_Ga=T,f=100,q=2,c=10,r=5,i=7;QUJD\x1b\\", result);
    }

    [Fact]
    public void EncodeKitty_MoveCursorFalseAddsCursorControl()
    {
        var withDefault = TerminalImage.EncodeKitty("QUJD");
        Assert.Equal("\x1b_Ga=T,f=100,q=2;QUJD\x1b\\", withDefault);

        var pinned = TerminalImage.EncodeKitty("QUJD", new KittyEncodeOptions { MoveCursor = false });
        Assert.Equal("\x1b_Ga=T,f=100,q=2,C=1;QUJD\x1b\\", pinned);
    }

    [Fact]
    public void EncodeKitty_ChunksLargePayloads()
    {
        var payload = new string('A', 5000);
        var result = TerminalImage.EncodeKitty(payload, new KittyEncodeOptions { ImageId = 3 });

        // First chunk carries the placement controls plus m=1; the tail chunk carries m=0.
        Assert.StartsWith("\x1b_Ga=T,f=100,q=2,i=3,m=1;", result);
        Assert.Contains("\x1b_Gm=0;", result);
        Assert.EndsWith("\x1b\\", result);

        // 4096 + 904 characters of payload survive the round trip.
        var withoutEscapes = result
            .Replace("\x1b_Ga=T,f=100,q=2,i=3,m=1;", "", StringComparison.Ordinal)
            .Replace("\x1b_Gm=0;", "", StringComparison.Ordinal)
            .Replace("\x1b\\", "", StringComparison.Ordinal);
        Assert.Equal(payload, withoutEscapes);
    }

    [Fact]
    public void EncodeITerm2_EncodesSizeWidthHeightAndName()
    {
        var result = TerminalImage.EncodeITerm2("QUJD", new Iterm2EncodeOptions
        {
            Width = 10,
            Height = Iterm2Size.Auto,
            Name = "hi.png",
            PreserveAspectRatio = true,
        });

        // "QUJD" decodes to 3 bytes; "hi.png" base64-encodes to aGkucG5n.
        Assert.Equal("\x1b]1337;File=inline=1;size=3;width=10;height=auto;name=aGkucG5n:QUJD\x07", result);
    }

    [Fact]
    public void EncodeITerm2_InlineFalseAndPreserveAspectRatioFalse()
    {
        var result = TerminalImage.EncodeITerm2("QUJD", new Iterm2EncodeOptions
        {
            Inline = false,
            PreserveAspectRatio = false,
        });

        Assert.Equal("\x1b]1337;File=inline=0;size=3;preserveAspectRatio=0:QUJD\x07", result);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("QUJD", 3)]
    [InlineData("QUJDRA==", 4)]
    [InlineData("abc", 2)]
    [InlineData("QUJD\nRA==", 4)]
    public void Base64ByteLength_MatchesNodeSemantics(string data, int expected)
    {
        Assert.Equal(expected, TerminalImage.Base64ByteLength(data));
    }
}

public class TerminalImageCellSizeTests
{
    [Fact]
    public void CalculateImageCellSize_RespectsMaxWidth()
    {
        var size = TerminalImage.CalculateImageCellSize(
            new ImageDimensions(100, 50),
            10,
            null,
            new CellDimensions(10, 20));

        Assert.Equal(10, size.Columns);
        Assert.Equal(3, size.Rows);
    }

    [Fact]
    public void CalculateImageCellSize_ClampsToMaxHeight()
    {
        var size = TerminalImage.CalculateImageCellSize(
            new ImageDimensions(100, 50),
            10,
            1,
            new CellDimensions(10, 20));

        Assert.Equal(4, size.Columns);
        Assert.Equal(1, size.Rows);
    }

    [Fact]
    public void CalculateImageCellSize_NeverReturnsZeroCells()
    {
        var size = TerminalImage.CalculateImageCellSize(
            new ImageDimensions(10000, 10000),
            0,
            0,
            new CellDimensions(10, 20));

        Assert.Equal(1, size.Columns);
        Assert.Equal(1, size.Rows);
    }

    [Fact]
    public void CalculateImageCellSize_OptimizeAspectRatioPrefersLessDistortion()
    {
        var image = new ImageDimensions(100, 22);
        var cells = new CellDimensions(10, 20);

        // Without optimisation the fractional height rounds up to two rows.
        Assert.Equal(2, TerminalImage.CalculateImageCellSize(image, 10, null, cells).Rows);
        // With optimisation one row is a better fit than two.
        Assert.Equal(1, TerminalImage.CalculateImageCellSize(image, 10, null, cells, optimizeAspectRatio: true).Rows);
    }

    [Fact]
    public void CalculateImageRows_DelegatesToCellSize()
    {
        Assert.Equal(3, TerminalImage.CalculateImageRows(new ImageDimensions(100, 50), 10, new CellDimensions(10, 20)));
    }
}

public class TerminalImageDimensionTests
{
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    [Fact]
    public void GetPngDimensions_ReadsIhdr()
    {
        var png = new byte[26];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4e;
        png[3] = 0x47;
        png[4] = 0x0d;
        png[5] = 0x0a;
        png[6] = 0x1a;
        png[7] = 0x0a;
        png[11] = 13; // IHDR length
        "IHDR"u8.CopyTo(png.AsSpan(12));
        png[18] = 2;
        png[19] = 0x80; // width 640
        png[22] = 1;
        png[23] = 0xe0; // height 480

        Assert.Equal(new ImageDimensions(640, 480), TerminalImage.GetPngDimensions(Base64(png)));
    }

    [Fact]
    public void GetPngDimensions_RejectsOtherFormats()
    {
        Assert.Null(TerminalImage.GetPngDimensions(Base64(new byte[64])));
        Assert.Null(TerminalImage.GetPngDimensions("not base64!!"));
    }

    [Fact]
    public void GetJpegDimensions_ReadsSofMarker()
    {
        var jpeg = new byte[20];
        jpeg[0] = 0xff;
        jpeg[1] = 0xd8;
        jpeg[2] = 0xff;
        jpeg[3] = 0xc0; // SOF0
        jpeg[5] = 0x11; // segment length
        jpeg[6] = 0x08; // precision
        jpeg[7] = 0x01;
        jpeg[8] = 0xe0; // height 480
        jpeg[9] = 0x02;
        jpeg[10] = 0x80; // width 640

        Assert.Equal(new ImageDimensions(640, 480), TerminalImage.GetJpegDimensions(Base64(jpeg)));
    }

    [Fact]
    public void GetGifDimensions_ReadsLogicalScreenDescriptor()
    {
        var gif = new byte[10];
        "GIF89a"u8.CopyTo(gif);
        gif[6] = 0x80;
        gif[7] = 0x02; // width 640 (little endian)
        gif[8] = 0xe0;
        gif[9] = 0x01; // height 480 (little endian)

        Assert.Equal(new ImageDimensions(640, 480), TerminalImage.GetGifDimensions(Base64(gif)));
    }

    [Fact]
    public void GetWebpDimensions_ReadsVp8xChunk()
    {
        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        "VP8X"u8.CopyTo(webp.AsSpan(12));
        // width - 1 = 639, height - 1 = 479, both 24-bit little endian.
        webp[24] = 0x7f;
        webp[25] = 0x02;
        webp[27] = 0xdf;
        webp[28] = 0x01;

        Assert.Equal(new ImageDimensions(640, 480), TerminalImage.GetWebpDimensions(Base64(webp)));
    }

    [Fact]
    public void GetWebpDimensions_ReadsVp8lChunk()
    {
        // NOTE: the upstream implementation gates on `buffer.length < 30` *before* inspecting the
        // chunk tag, which makes its inner `length < 25` VP8L guard unreachable. The fixture is
        // therefore padded to 30 bytes so the VP8L branch is actually exercised — a 25-byte VP8L
        // buffer is rejected by the top-level gate in both the TS original and this port.
        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        "VP8L"u8.CopyTo(webp.AsSpan(12));
        webp[20] = 0x2f; // signature byte
        // bits = (width - 1) | ((height - 1) << 14) = 639 | (479 << 14) = 0x77C27F, little endian.
        webp[21] = 0x7f;
        webp[22] = 0xc2;
        webp[23] = 0x77;

        Assert.Equal(new ImageDimensions(640, 480), TerminalImage.GetWebpDimensions(Base64(webp)));
    }

    [Fact]
    public void GetImageDimensions_DispatchesOnMimeType()
    {
        var gif = new byte[10];
        "GIF87a"u8.CopyTo(gif);
        gif[6] = 0x02;
        gif[7] = 0x00; // width 2
        gif[8] = 0x03;
        gif[9] = 0x00; // height 3

        Assert.Equal(new ImageDimensions(2, 3), TerminalImage.GetImageDimensions(Base64(gif), "image/gif"));
        Assert.Null(TerminalImage.GetImageDimensions(Base64(gif), "image/bmp"));
    }
}

public class TerminalImageMetadataTests
{
    [Fact]
    public void RegisterAndQueryMetadata()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=3,r=2,i=777;AAAA\x1b\\";
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(777, 3, 2, 100, 50));

        var metadata = TerminalImage.GetKittyImageMetadata(line);
        Assert.NotNull(metadata);
        Assert.Equal(777, metadata!.ImageId);
        Assert.Equal(3, metadata.Columns);
        Assert.Equal(2, metadata.Rows);
        Assert.Equal(100, metadata.WidthPx);
        Assert.Equal(50, metadata.HeightPx);

        Assert.Null(TerminalImage.GetKittyImageMetadata("\x1b_Ga=T,f=100,q=2,i=424242;AAAA\x1b\\"));
        Assert.Null(TerminalImage.GetKittyImageMetadata("no image here"));
    }

    [Fact]
    public void GetKittyImagePlacementRows_PrefersExplicitRowsThenMetadata()
    {
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(781, 4, 7, 10, 10));

        Assert.Equal(3, TerminalImage.GetKittyImagePlacementRows("\x1b_Ga=T,c=2,r=3,i=781;AAAA\x1b\\"));
        Assert.Equal(7, TerminalImage.GetKittyImagePlacementRows("\x1b_Ga=T,c=2,i=781;AAAA\x1b\\"));
        Assert.Null(TerminalImage.GetKittyImagePlacementRows("\x1b_Ga=T,c=2,i=999999;AAAA\x1b\\"));
        Assert.Null(TerminalImage.GetKittyImagePlacementRows("plain text"));
    }

    [Fact]
    public void GetKittyImagePlacement_RebuildsAPlacementOnlyCommand()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=3,r=2,i=778;AAAA\x1b\\";
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(778, 3, 2, 100, 50));

        var placement = TerminalImage.GetKittyImagePlacement(line);
        Assert.NotNull(placement);
        Assert.Equal(778, placement!.ImageId);
        Assert.Equal(2, placement.Rows);
        Assert.Equal(20000L, placement.EstimatedDecodedBytes);

        // Only placement-relevant controls survive, re-emitted as a placement (a=p).
        Assert.Equal("\x1b_Ga=p,q=2,c=3,r=2,i=778\x1b\\", placement.Sequence);
        Assert.Equal(line.Length, placement.TransmissionBytes);
        Assert.Equal(placement.Sequence, placement.ReplacementLine);
    }

    [Fact]
    public void GetKittyImagePlacement_SkipsChunkedTransmission()
    {
        var first = "\x1b_Ga=T,f=100,q=2,c=3,r=2,i=779,m=1;AAAA\x1b\\";
        var second = "\x1b_Gm=0;BBBB\x1b\\";
        var line = first + second;
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(779, 3, 2, 100, 50));

        var placement = TerminalImage.GetKittyImagePlacement(line);
        Assert.NotNull(placement);
        Assert.Equal(line.Length, placement!.TransmissionBytes);
        Assert.Equal("\x1b_Ga=p,q=2,c=3,r=2,i=779\x1b\\", placement.Sequence);
        // The whole transmission is replaced, so nothing of the original remains.
        Assert.Equal(placement.Sequence, placement.ReplacementLine);
    }

    [Fact]
    public void GetKittyImagePlacement_ReturnsNullForUnregisteredImages()
    {
        Assert.Null(TerminalImage.GetKittyImagePlacement("\x1b_Ga=T,i=888888;AAAA\x1b\\"));
        Assert.Null(TerminalImage.GetKittyImagePlacement("plain text"));
    }

    [Fact]
    public void CropKittyImageLine_RewritesCropControls()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=3,r=4,i=999;AAAA\x1b\\";
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(999, 3, 4, 100, 100));

        var cropped = TerminalImage.CropKittyImageLine(line, 1, 2);

        // One of four rows hidden: source y = 25, source height = 50, two rows rendered.
        Assert.StartsWith("\x1b_Ga=T,f=100,q=2,c=3,i=999,y=25,h=50,r=2;", cropped);
        Assert.EndsWith("AAAA\x1b\\", cropped);
        Assert.DoesNotContain("r=4", cropped);
    }

    [Fact]
    public void CropKittyImageLine_IsANoOpWhenNothingIsCropped()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=3,r=4,i=1000;AAAA\x1b\\";
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(1000, 3, 4, 100, 100));

        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, 0, 4));
        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, 0, 99));
        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, 4, 1));
        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, 1, 0));
        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, -1, 1));
    }

    [Fact]
    public void CropKittyImageLine_IsANoOpForUnregisteredImages()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=3,r=4,i=1001;AAAA\x1b\\";
        Assert.Equal(line, TerminalImage.CropKittyImageLine(line, 1, 2));
    }

    [Fact]
    public void RegisterKittyImageMetadata_RefreshesTheTransmissionGeneration()
    {
        var line = "\x1b_Ga=T,c=1,r=1,i=1002;AAAA\x1b\\";
        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(1002, 1, 1, 10, 10));
        var first = TerminalImage.GetKittyImagePlacement(line)!.TransmissionGeneration;

        TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(1002, 1, 1, 10, 10));
        var second = TerminalImage.GetKittyImagePlacement(line)!.TransmissionGeneration;

        Assert.True(second > first);
    }

    [Fact]
    public void RegisterKittyImageMetadata_EvictsTheOldestEntryBeyondTheLimit()
    {
        const int firstId = 900001;
        const int count = 1001;
        for (var index = 0; index < count; index++)
        {
            var id = firstId + index;
            TerminalImage.RegisterKittyImageMetadata(new KittyImageMetadata(id, 1, 1, 10, 10));
        }

        Assert.Null(TerminalImage.GetKittyImageMetadata($"\x1b_Ga=T,c=1,r=1,i={firstId};AAAA\x1b\\"));
        Assert.NotNull(TerminalImage.GetKittyImageMetadata($"\x1b_Ga=T,c=1,r=1,i={firstId + count - 1};AAAA\x1b\\"));
    }
}

public class TerminalImageRenderTests
{
    private static readonly ImageDimensions Image640x480 = new(640, 480);

    private static string SampleBase64()
    {
        var png = new byte[26];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4e;
        png[3] = 0x47;
        png[11] = 13;
        "IHDR"u8.CopyTo(png.AsSpan(12));
        png[18] = 2;
        png[19] = 0x80;
        png[22] = 1;
        png[23] = 0xe0;
        return Convert.ToBase64String(png);
    }

    [Fact]
    public void RenderImage_ReturnsNullWhenTheTerminalHasNoImageSupport()
    {
        using var _ = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.None, true, true));
        Assert.Null(TerminalImage.RenderImage(SampleBase64(), Image640x480));
    }

    [Fact]
    public void RenderImage_EncodesKittyAndRegistersMetadata()
    {
        using var _ = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 20));

        var result = TerminalImage.RenderImage(SampleBase64(), Image640x480, new ImageRenderOptions { ImageId = 1234 });

        Assert.NotNull(result);
        Assert.Equal(80, result!.Columns);
        Assert.Equal(30, result.Rows);
        Assert.Equal(1234L, result.ImageId);
        Assert.StartsWith("\x1b_Ga=T,f=100,q=2,c=80,r=30,i=1234;", result.Sequence);
        Assert.EndsWith("\x1b\\", result.Sequence);

        // The placement metadata is queryable from the emitted line.
        var metadata = TerminalImage.GetKittyImageMetadata(result.Sequence);
        Assert.NotNull(metadata);
        Assert.Equal(1234, metadata!.ImageId);
        Assert.Equal(80, metadata.Columns);
        Assert.Equal(30, metadata.Rows);
    }

    [Fact]
    public void RenderImage_EncodesITerm2WithAutoHeight()
    {
        using var _ = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.Iterm2, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 20));

        var result = TerminalImage.RenderImage(SampleBase64(), Image640x480);

        Assert.NotNull(result);
        Assert.Null(result!.ImageId);
        Assert.Equal(80, result.Columns);
        Assert.StartsWith("\x1b]1337;File=inline=1;size=26;width=80;height=auto:", result.Sequence);
        Assert.EndsWith("\x07", result.Sequence);
    }

    [Fact]
    public void RenderImage_RespectsMaxWidthCells()
    {
        using var _ = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        TerminalImage.SetCellDimensions(new CellDimensions(10, 20));

        var result = TerminalImage.RenderImage(SampleBase64(), Image640x480, new ImageRenderOptions { MaxWidthCells = 40 });

        Assert.NotNull(result);
        Assert.Equal(40, result!.Columns);
        Assert.Equal(15, result.Rows);
    }

    [Fact]
    public void IsImageLine_DetectsBothProtocols()
    {
        Assert.True(TerminalImage.IsImageLine("\x1b_Ga=T;AAAA\x1b\\"));
        Assert.True(TerminalImage.IsImageLine("\x1b]1337;File=inline=1:AAAA\x07"));
        Assert.True(TerminalImage.IsImageLine("prefix\x1b_Ga=T;AAAA\x1b\\"));
        Assert.False(TerminalImage.IsImageLine("plain text"));
    }

    [Fact]
    public void AllocateImageId_StaysInTheProtocolRange()
    {
        for (var index = 0; index < 200; index++)
        {
            Assert.InRange(TerminalImage.AllocateImageId(), 1, 0xfffffffeL);
        }
    }

    [Fact]
    public void ImageFallback_ReportsMimeTypeAndDimensions()
    {
        using var _ = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.None, true, false));
        Assert.Equal("[Image: [image/png] 640x480]", TerminalImage.ImageFallback("image/png", Image640x480));
        Assert.Equal("[Image: [image/png]]", TerminalImage.ImageFallback("image/png"));
    }

    [Fact]
    public void ImageFallback_ShortensAndLinksAbsolutePathsWhenHyperlinksAreSupported()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var filename = Path.Combine(home, "pic.png");
        // ShortenImagePath keeps the separator, so the display is "~/pic.png" on Unix and
        // "~\\pic.png" on Windows.
        var expectedDisplay = "~" + Path.DirectorySeparatorChar + "pic.png";

        using (new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.None, true, true)))
        {
            var result = TerminalImage.ImageFallback("image/png", new ImageDimensions(2, 3), filename);
            Assert.Contains("file://", result);
            Assert.Contains("[image/png] 2x3", result);
            if (!string.IsNullOrEmpty(home))
            {
                // The filename is emitted first and wrapped in an OSC 8 hyperlink, so the result
                // begins with the escape sequence rather than the visible text.
                Assert.Contains(expectedDisplay, result);
                Assert.StartsWith("[Image: \x1b]8;;", result);
            }
        }

        using (new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.None, true, false)))
        {
            var result = TerminalImage.ImageFallback("image/png", null, filename);
            Assert.DoesNotContain("file://", result);
            Assert.Contains("[image/png]", result);
            if (!string.IsNullOrEmpty(home))
            {
                // Without hyperlinks there is no leading escape, so the display text is at the front.
                Assert.StartsWith("[Image: " + expectedDisplay, result);
            }
        }
    }
}

public class TerminalImageCapabilityTests
{
    [Fact]
    public void DetectCapabilities_RecognisesKittyAndGhostty()
    {
        using var cleared = EnvScope.ClearTerminalEnvironment();

        using (new EnvScope(("TERM_PROGRAM", "kitty")))
        {
            var caps = TerminalImage.DetectCapabilities(() => true);
            Assert.Equal(ImageProtocol.Kitty, caps.Images);
            Assert.True(caps.TrueColor);
            Assert.True(caps.Hyperlinks);
        }

        using (new EnvScope(("GHOSTTY_RESOURCES_DIR", "/usr/share/ghostty")))
        {
            Assert.Equal(ImageProtocol.Kitty, TerminalImage.DetectCapabilities(() => true).Images);
        }

        using (new EnvScope(("ITERM_SESSION_ID", "w0t0p0")))
        {
            Assert.Equal(ImageProtocol.Iterm2, TerminalImage.DetectCapabilities(() => true).Images);
        }
    }

    [Fact]
    public void DetectCapabilities_TmuxKeepsImagesOffAndAsksAboutHyperlinks()
    {
        using var cleared = EnvScope.ClearTerminalEnvironment();
        using var tmux = new EnvScope(("TMUX", "/tmp/tmux-1000/default,123,0"));

        var withoutForwarding = TerminalImage.DetectCapabilities(() => false);
        Assert.Equal(ImageProtocol.None, withoutForwarding.Images);
        Assert.False(withoutForwarding.Hyperlinks);

        var withForwarding = TerminalImage.DetectCapabilities(() => true);
        Assert.Equal(ImageProtocol.None, withForwarding.Images);
        Assert.True(withForwarding.Hyperlinks);
    }

    [Fact]
    public void DetectCapabilities_ScreenKeepsHyperlinksOffAndUsesTheColorHint()
    {
        using var cleared = EnvScope.ClearTerminalEnvironment();

        using (new EnvScope(("TERM", "screen-256color")))
        {
            var caps = TerminalImage.DetectCapabilities(() => true);
            Assert.Equal(ImageProtocol.None, caps.Images);
            Assert.False(caps.Hyperlinks);
            Assert.False(caps.TrueColor);
        }

        using (new EnvScope(("TERM", "screen-256color"), ("COLORTERM", "truecolor")))
        {
            Assert.True(TerminalImage.DetectCapabilities(() => true).TrueColor);
        }

        // A "-direct" TERM suffix is itself a truecolor hint.
        using (new EnvScope(("TERM", "screen-direct")))
        {
            Assert.True(TerminalImage.DetectCapabilities(() => true).TrueColor);
        }
    }

    [Fact]
    public void DetectCapabilities_EnvironmentOverridesWin()
    {
        using var cleared = EnvScope.ClearTerminalEnvironment();
        using var overrides = new EnvScope(
            ("TERM_PROGRAM", "kitty"),
            ("PI_IMAGE_PROTOCOL", "none"),
            ("PI_TRUE_COLOR", "0"),
            ("PI_HYPERLINKS", "0"));

        var caps = TerminalImage.DetectCapabilities(() => true);
        Assert.Equal(ImageProtocol.None, caps.Images);
        Assert.False(caps.TrueColor);
        Assert.False(caps.Hyperlinks);

        using var forced = new EnvScope(("PI_IMAGE_PROTOCOL", "iterm2"), ("PI_TRUE_COLOR", "1"), ("PI_HYPERLINKS", "1"));
        var forcedCaps = TerminalImage.DetectCapabilities(() => false);
        Assert.Equal(ImageProtocol.Iterm2, forcedCaps.Images);
        Assert.True(forcedCaps.TrueColor);
        Assert.True(forcedCaps.Hyperlinks);
    }

    [Fact]
    public void GetTerminalColorMode_FollowsTheTrueColorFlag()
    {
        Assert.Equal(
            TerminalColorMode.Truecolor,
            TerminalImage.GetTerminalColorMode(new TerminalCapabilities(ImageProtocol.Kitty, true, true)));
        Assert.Equal(
            TerminalColorMode.Color256,
            TerminalImage.GetTerminalColorMode(new TerminalCapabilities(ImageProtocol.None, false, false)));
    }

    [Fact]
    public void SetCapabilityOverrides_OverridesDetectionAndKeepsTheCacheWhenUnchanged()
    {
        using var cleared = EnvScope.ClearTerminalEnvironment();
        try
        {
            TerminalImage.ResetCapabilitiesCache();
            TerminalImage.SetCapabilityOverrides(new CapabilityOverrides
            {
                Images = ImageProtocol.Iterm2,
                Hyperlinks = true,
            });

            var caps = TerminalImage.GetCapabilities();
            Assert.Equal(ImageProtocol.Iterm2, caps.Images);
            Assert.True(caps.Hyperlinks);

            // Re-applying identical overrides must not invalidate the cache.
            TerminalImage.SetCapabilityOverrides(new CapabilityOverrides
            {
                Images = ImageProtocol.Iterm2,
                Hyperlinks = true,
            });
            Assert.Same(caps, TerminalImage.GetCapabilities());

            // A different override does invalidate it.
            TerminalImage.SetCapabilityOverrides(new CapabilityOverrides { Images = ImageProtocol.Kitty });
            Assert.NotSame(caps, TerminalImage.GetCapabilities());
            Assert.Equal(ImageProtocol.Kitty, TerminalImage.GetCapabilities().Images);
        }
        finally
        {
            TerminalImage.SetCapabilityOverrides(new CapabilityOverrides());
            TerminalImage.ResetCapabilitiesCache();
        }
    }
}
