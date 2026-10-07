using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Inline image protocol supported by a terminal.</summary>
public enum ImageProtocol
{
    /// <summary>No inline image support.</summary>
    None,

    Kitty,

    Iterm2,
}

/// <summary>Detected terminal capabilities.</summary>
public sealed record TerminalCapabilities(ImageProtocol Images, bool TrueColor, bool Hyperlinks);

/// <summary>Partial capability overrides (port of the TS <c>Partial&lt;TerminalCapabilities&gt;</c>).</summary>
public sealed record CapabilityOverrides
{
    public ImageProtocol? Images { get; init; }

    public bool? TrueColor { get; init; }

    public bool? Hyperlinks { get; init; }
}

/// <summary>Terminal cell size in pixels.</summary>
public readonly record struct CellDimensions(int WidthPx, int HeightPx);

/// <summary>Image size in pixels.</summary>
public readonly record struct ImageDimensions(int WidthPx, int HeightPx);

/// <summary>Image size in terminal cells.</summary>
public readonly record struct ImageCellSize(int Columns, int Rows);

/// <summary>Metadata recorded for a transmitted Kitty image.</summary>
public sealed record KittyImageMetadata(long ImageId, int Columns, int Rows, int WidthPx, int HeightPx);

/// <summary>Metadata plus the transmission generation, used to detect re-transmissions.</summary>
internal sealed record RegisteredKittyImageMetadata(
    long ImageId,
    int Columns,
    int Rows,
    int WidthPx,
    int HeightPx,
    long TransmissionGeneration);

/// <summary>A placement-only command rebuilt from an image line.</summary>
public sealed record KittyImagePlacement(
    long ImageId,
    long TransmissionGeneration,
    int TransmissionBytes,
    long EstimatedDecodedBytes,
    int Rows,
    string Sequence,
    string ReplacementLine);

/// <summary>Options for <see cref="TerminalImage.EncodeKitty"/>.</summary>
public sealed record KittyEncodeOptions
{
    public int? Columns { get; init; }

    public int? Rows { get; init; }

    public long? ImageId { get; init; }

    /// <summary>Whether Kitty should apply its default cursor movement after placement. Default: true.</summary>
    public bool? MoveCursor { get; init; }
}

/// <summary>
/// A number of cells or a raw value (port of the TS union <c>number | string</c>).
/// </summary>
public readonly record struct Iterm2Size
{
    private readonly string _value;

    private Iterm2Size(string value) => _value = value;

    /// <summary>Let iTerm2 derive the size from the other dimension.</summary>
    public static Iterm2Size Auto => new("auto");

    public static Iterm2Size Cells(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    public static Iterm2Size Raw(string value) => new(value);

    public static implicit operator Iterm2Size(int value) => Cells(value);

    public static implicit operator Iterm2Size(string value) => Raw(value);

    public override string ToString() => _value;
}

/// <summary>Options for <see cref="TerminalImage.EncodeITerm2"/>.</summary>
public sealed record Iterm2EncodeOptions
{
    public Iterm2Size? Width { get; init; }

    public Iterm2Size? Height { get; init; }

    public string? Name { get; init; }

    public bool? PreserveAspectRatio { get; init; }

    public bool? Inline { get; init; }
}

/// <summary>Options for <see cref="TerminalImage.RenderImage"/>.</summary>
public sealed record ImageRenderOptions
{
    public int? MaxWidthCells { get; init; }

    public int? MaxHeightCells { get; init; }

    public bool? PreserveAspectRatio { get; init; }

    /// <summary>Kitty image ID. If provided, reuses/replaces existing image with this ID.</summary>
    public long? ImageId { get; init; }

    /// <summary>Whether Kitty should apply its default cursor movement after placement.</summary>
    public bool? MoveCursor { get; init; }
}

/// <summary>The result of <see cref="TerminalImage.RenderImage"/>.</summary>
public sealed record ImageRenderResult(string Sequence, int Columns, int Rows, long? ImageId);

/// <summary>
/// Port of <c>terminal-image.ts</c>: terminal capability detection, Kitty/iTerm2 encoders, Kitty image
/// metadata registry and placement rebuilding, image dimension parsing and the text fallback.
///
/// Deviation from TS: the tmux probe shells out with <c>ProcessStartInfo</c> instead of
/// <c>execSync</c>; <c>pathToFileURL</c> becomes <see cref="Uri"/>; and image ids are <see cref="long"/>
/// because the TS generator spans the full unsigned 32-bit range (1..0xFFFFFFFE), which does not fit in
/// <see cref="int"/>.
/// </summary>
public static partial class TerminalImage
{
    private const string KittyPrefix = "\x1b_G";
    private const string Iterm2Prefix = "\x1b]1337;File=";

    private static TerminalCapabilities? _cachedCapabilities;
    private static CapabilityOverrides _capabilityOverrides = new();
    private static CellDimensions _cellDimensions = new(9, 18);

    // Kitty image metadata, kept in insertion order so the oldest entry can be evicted.
    private static readonly Dictionary<long, RegisteredKittyImageMetadata> KittyImageMetadata = new();
    private static readonly List<long> KittyImageOrder = new();
    private static long _kittyTransmissionGeneration;

    private static readonly HashSet<string> KittyPlacementControlKeys = new(StringComparer.Ordinal)
    {
        "i", "p", "x", "y", "w", "h", "X", "Y", "c", "r", "C", "U", "z", "P", "Q", "H", "V",
    };

    [GeneratedRegex(@"(?:^|,)i=([0-9]+)(?:,|$)")]
    private static partial Regex ImageIdControlPattern();

    [GeneratedRegex("\u001b_G([^;]*);")]
    private static partial Regex KittyControlsPattern();

    [GeneratedRegex(@"(?:^|,)r=([0-9]+)(?:,|$)")]
    private static partial Regex RowsControlPattern();

    [GeneratedRegex(@"(?:^|,)m=1(?:,|$)")]
    private static partial Regex MoreChunksPattern();

    [GeneratedRegex("^[yhr]=")]
    private static partial Regex CropControlPattern();

    // ------------------------------------------------------------------
    // Cell dimensions
    // ------------------------------------------------------------------

    public static CellDimensions GetCellDimensions() => _cellDimensions;

    public static void SetCellDimensions(CellDimensions dims) => _cellDimensions = dims;

    // ------------------------------------------------------------------
    // Capability detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Checks whether the attached tmux client forwards OSC 8 hyperlinks to the outer terminal. tmux
    /// only re-emits them when its <c>client_termfeatures</c> lists <c>hyperlinks</c>, and strips them
    /// otherwise. On any error falls back to <c>false</c>.
    /// </summary>
    private static bool ProbeTmuxHyperlinks()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tmux", "display-message -p '#{client_termfeatures}'")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(250);
            return output.Split(',').Select(feature => feature.Trim()).Contains("hyperlinks");
        }
        catch
        {
            return false;
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static TerminalCapabilities DetectCapabilitiesFromEnvironment(Func<bool> tmuxForwardsHyperlink)
    {
        var termProgram = JsString.ToLowerCase(Env("TERM_PROGRAM"));
        var terminalEmulator = JsString.ToLowerCase(Env("TERMINAL_EMULATOR"));
        var term = JsString.ToLowerCase(Env("TERM"));
        var colorTerm = JsString.ToLowerCase(Env("COLORTERM"));
        var hasTrueColorHint = colorTerm is "truecolor" or "24bit" || term.EndsWith("-direct", StringComparison.Ordinal);
        var isWindowsConsole = OperatingSystem.IsWindows();

        // Emit OSC 8 hyperlinks only when tmux confirms it forwards.
        // Image protocols are unreliable under tmux, so leave images off.
        if (Env("TMUX").Length > 0 || term.StartsWith("tmux", StringComparison.Ordinal))
        {
            return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, tmuxForwardsHyperlink());
        }

        // screen does not forward OSC 8 hyperlinks, so keep them off there.
        if (term.StartsWith("screen", StringComparison.Ordinal))
        {
            return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, false);
        }

        if (Env("KITTY_WINDOW_ID").Length > 0 || termProgram == "kitty")
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (termProgram == "ghostty" || term.Contains("ghostty", StringComparison.Ordinal) || Env("GHOSTTY_RESOURCES_DIR").Length > 0)
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (Env("WEZTERM_PANE").Length > 0 || termProgram == "wezterm")
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        // Warp supports the Kitty graphics protocol and OSC 8 hyperlinks.
        if (termProgram == "warpterminal" || Env("WARP_SESSION_ID").Length > 0 || Env("WARP_TERMINAL_SESSION_UUID").Length > 0)
        {
            return new TerminalCapabilities(ImageProtocol.Kitty, true, true);
        }

        if (Env("ITERM_SESSION_ID").Length > 0 || termProgram == "iterm.app")
        {
            return new TerminalCapabilities(ImageProtocol.Iterm2, true, true);
        }

        if (Env("WT_SESSION").Length > 0)
        {
            return new TerminalCapabilities(ImageProtocol.None, true, true);
        }

        if (termProgram is "alacritty" or "vscode" or "zed")
        {
            return new TerminalCapabilities(ImageProtocol.None, true, true);
        }

        if (terminalEmulator == "jetbrains-jediterm")
        {
            return new TerminalCapabilities(ImageProtocol.None, true, false);
        }

        // Windows Terminal does not always set WT_SESSION, for example when it hosts a cmd.exe
        // launched directly from Win+R. Modern Windows consoles support truecolor; keep hyperlinks off
        // unless we positively detected support above.
        if (isWindowsConsole)
        {
            return new TerminalCapabilities(ImageProtocol.None, true, false);
        }

        // Unknown terminal: be conservative. OSC 8 is rendered invisibly as "just text" on terminals
        // that swallow it, which means the URL disappears from the rendered output. Default to the
        // legacy `text (url)` behavior unless we have positively identified a hyperlink-capable
        // terminal above.
        return new TerminalCapabilities(ImageProtocol.None, hasTrueColorHint, false);
    }

    private static bool? ParseBooleanCapabilityOverride(string value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    public static TerminalCapabilities DetectCapabilities(Func<bool>? tmuxForwardsHyperlink = null)
    {
        tmuxForwardsHyperlink ??= ProbeTmuxHyperlinks;

        var hyperlinks = ParseBooleanCapabilityOverride(Env("PI_HYPERLINKS"));
        var detected = DetectCapabilitiesFromEnvironment(
            hyperlinks is null ? tmuxForwardsHyperlink : () => hyperlinks.Value);

        var imageProtocol = JsString.ToLowerCase(Env("PI_IMAGE_PROTOCOL"));
        ImageProtocol? images = imageProtocol switch
        {
            "kitty" => ImageProtocol.Kitty,
            "iterm2" => ImageProtocol.Iterm2,
            "none" or "0" => ImageProtocol.None,
            _ => null,
        };
        var trueColor = ParseBooleanCapabilityOverride(Env("PI_TRUE_COLOR"));

        return detected with
        {
            Images = images ?? detected.Images,
            TrueColor = trueColor ?? detected.TrueColor,
            Hyperlinks = hyperlinks ?? detected.Hyperlinks,
        };
    }

    public static TerminalCapabilities GetCapabilities()
    {
        if (_cachedCapabilities is null)
        {
            var hyperlinks = _capabilityOverrides.Hyperlinks;
            var detected = DetectCapabilities(hyperlinks is null ? null : () => hyperlinks.Value);
            _cachedCapabilities = detected with
            {
                Images = _capabilityOverrides.Images ?? detected.Images,
                TrueColor = _capabilityOverrides.TrueColor ?? detected.TrueColor,
                Hyperlinks = _capabilityOverrides.Hyperlinks ?? detected.Hyperlinks,
            };
        }
        return _cachedCapabilities;
    }

    public static TerminalColorMode GetTerminalColorMode(TerminalCapabilities? capabilities = null) =>
        (capabilities ?? GetCapabilities()).TrueColor ? TerminalColorMode.Truecolor : TerminalColorMode.Color256;

    public static void ResetCapabilitiesCache() => _cachedCapabilities = null;

    /// <summary>Override selected auto-detected capabilities.</summary>
    public static void SetCapabilityOverrides(CapabilityOverrides overrides)
    {
        if (_capabilityOverrides.Images == overrides.Images
            && _capabilityOverrides.TrueColor == overrides.TrueColor
            && _capabilityOverrides.Hyperlinks == overrides.Hyperlinks)
        {
            return;
        }
        _capabilityOverrides = overrides;
        _cachedCapabilities = null;
    }

    /// <summary>Override the cached capabilities. Useful in tests to exercise both code paths.</summary>
    public static void SetCapabilities(TerminalCapabilities caps) => _cachedCapabilities = caps;

    // ------------------------------------------------------------------
    // Image line detection and ids
    // ------------------------------------------------------------------

    /// <summary>Whether a rendered line carries an inline-image escape sequence.</summary>
    public static bool IsImageLine(string line)
    {
        // Fast path: sequence at line start (single-row images)
        if (line.StartsWith(KittyPrefix, StringComparison.Ordinal) || line.StartsWith(Iterm2Prefix, StringComparison.Ordinal))
        {
            return true;
        }
        // Slow path: sequence elsewhere (multi-row images have cursor-up prefix)
        return line.Contains(KittyPrefix, StringComparison.Ordinal) || line.Contains(Iterm2Prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Generate a random image ID for the Kitty graphics protocol. Uses random IDs to avoid collisions
    /// between different module instances (e.g. main app vs extensions).
    /// </summary>
    public static long AllocateImageId() => Random.Shared.NextInt64(1, 0xfffffffeL + 1);

    // ------------------------------------------------------------------
    // Encoders
    // ------------------------------------------------------------------

    private const int KittyChunkSize = 4096;

    public static string EncodeKitty(string base64Data, KittyEncodeOptions? options = null)
    {
        options ??= new KittyEncodeOptions();

        var parameters = new List<string> { "a=T", "f=100", "q=2" };

        if (options.MoveCursor == false)
        {
            parameters.Add("C=1");
        }
        if (options.Columns is { } columns)
        {
            parameters.Add($"c={columns}");
        }
        if (options.Rows is { } rows)
        {
            parameters.Add($"r={rows}");
        }
        if (options.ImageId is { } imageId)
        {
            parameters.Add($"i={imageId}");
        }

        if (base64Data.Length <= KittyChunkSize)
        {
            return $"{KittyPrefix}{string.Join(",", parameters)};{base64Data}\x1b\\";
        }

        var chunks = new List<string>();
        var offset = 0;
        var isFirst = true;

        while (offset < base64Data.Length)
        {
            var chunk = base64Data.Substring(offset, Math.Min(KittyChunkSize, base64Data.Length - offset));
            var isLast = offset + KittyChunkSize >= base64Data.Length;

            if (isFirst)
            {
                chunks.Add($"{KittyPrefix}{string.Join(",", parameters)},m=1;{chunk}\x1b\\");
                isFirst = false;
            }
            else if (isLast)
            {
                chunks.Add($"{KittyPrefix}m=0;{chunk}\x1b\\");
            }
            else
            {
                chunks.Add($"{KittyPrefix}m=1;{chunk}\x1b\\");
            }

            offset += KittyChunkSize;
        }

        return string.Concat(chunks);
    }

    /// <summary>Delete a Kitty graphics image by ID. Uses uppercase 'I' to also free the image data.</summary>
    public static string DeleteKittyImage(long imageId) => $"{KittyPrefix}a=d,d=I,i={imageId},q=2\x1b\\";

    /// <summary>Delete all visible Kitty graphics images. Uses uppercase 'A' to also free the image data.</summary>
    public static string DeleteAllKittyImages() => "\x1b_Ga=d,d=A,q=2\x1b\\";

    /// <summary>Delete all visible Kitty placements while retaining their uploaded image data.</summary>
    public static string DeleteAllKittyPlacements() => "\x1b_Ga=d,d=a,q=2\x1b\\";

    /// <summary>
    /// Number of bytes the given base64 string decodes to, matching Node's
    /// <c>Buffer.byteLength(data, "base64")</c> (whitespace ignored, padding excluded).
    /// </summary>
    internal static int Base64ByteLength(string data)
    {
        var characters = 0;
        foreach (var c in data)
        {
            if (c == '=' || char.IsWhiteSpace(c))
            {
                continue;
            }
            characters++;
        }
        var remainder = characters % 4;
        return characters / 4 * 3 + (remainder == 2 ? 1 : remainder == 3 ? 2 : 0);
    }

    public static string EncodeITerm2(string base64Data, Iterm2EncodeOptions? options = null)
    {
        options ??= new Iterm2EncodeOptions();

        var parameters = new List<string>
        {
            $"inline={(options.Inline != false ? 1 : 0)}",
            $"size={Base64ByteLength(base64Data)}",
        };

        if (options.Width is { } width)
        {
            parameters.Add($"width={width}");
        }
        if (options.Height is { } height)
        {
            parameters.Add($"height={height}");
        }
        if (!string.IsNullOrEmpty(options.Name))
        {
            var nameBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(options.Name));
            parameters.Add($"name={nameBase64}");
        }
        if (options.PreserveAspectRatio == false)
        {
            parameters.Add("preserveAspectRatio=0");
        }

        return $"\x1b]1337;File={string.Join(";", parameters)}:{base64Data}\x07";
    }

    // ------------------------------------------------------------------
    // Kitty metadata registry
    // ------------------------------------------------------------------

    public static void RegisterKittyImageMetadata(KittyImageMetadata metadata)
    {
        _kittyTransmissionGeneration += 1;
        if (KittyImageMetadata.Remove(metadata.ImageId))
        {
            KittyImageOrder.Remove(metadata.ImageId);
        }
        KittyImageMetadata[metadata.ImageId] = new RegisteredKittyImageMetadata(
            metadata.ImageId,
            metadata.Columns,
            metadata.Rows,
            metadata.WidthPx,
            metadata.HeightPx,
            _kittyTransmissionGeneration);
        KittyImageOrder.Add(metadata.ImageId);

        if (KittyImageMetadata.Count > 1000 && KittyImageOrder.Count > 0)
        {
            var oldestImageId = KittyImageOrder[0];
            KittyImageOrder.RemoveAt(0);
            KittyImageMetadata.Remove(oldestImageId);
        }
    }

    private static RegisteredKittyImageMetadata? GetRegisteredKittyImageMetadataFromControls(string controls)
    {
        var match = ImageIdControlPattern().Match(controls);
        if (!match.Success)
        {
            return null;
        }
        var imageId = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return KittyImageMetadata.TryGetValue(imageId, out var metadata) ? metadata : null;
    }

    private static RegisteredKittyImageMetadata? GetRegisteredKittyImageMetadata(string line)
    {
        var match = KittyControlsPattern().Match(line);
        return match.Success ? GetRegisteredKittyImageMetadataFromControls(match.Groups[1].Value) : null;
    }

    private static int? GetExplicitKittyImageRows(string controls)
    {
        var match = RowsControlPattern().Match(controls);
        if (!match.Success)
        {
            return null;
        }
        var rows = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return rows > 0 ? rows : null;
    }

    private static int GetKittyImageRowsFromControls(string controls, int fallbackRows) =>
        GetExplicitKittyImageRows(controls) ?? fallbackRows;

    /// <summary>The registered metadata for the image referenced by a rendered line, if any.</summary>
    public static KittyImageMetadata? GetKittyImageMetadata(string line)
    {
        var metadata = GetRegisteredKittyImageMetadata(line);
        return metadata is null
            ? null
            : new KittyImageMetadata(metadata.ImageId, metadata.Columns, metadata.Rows, metadata.WidthPx, metadata.HeightPx);
    }

    /// <summary>Read the number of rows covered by an image placement without scanning its payload.</summary>
    public static int? GetKittyImagePlacementRows(string line)
    {
        var match = KittyControlsPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }
        var explicitRows = GetExplicitKittyImageRows(match.Groups[1].Value);
        if (explicitRows is not null)
        {
            return explicitRows;
        }
        return GetRegisteredKittyImageMetadataFromControls(match.Groups[1].Value)?.Rows;
    }

    /// <summary>Build a placement-only command for an image line emitted by <see cref="RenderImage"/>.</summary>
    public static KittyImagePlacement? GetKittyImagePlacement(string line)
    {
        var match = KittyControlsPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }
        var metadata = GetRegisteredKittyImageMetadataFromControls(match.Groups[1].Value);
        if (metadata is null)
        {
            return null;
        }

        var commandStart = match.Index;
        var commandControls = match.Groups[1].Value;
        int transmissionEnd;
        while (true)
        {
            var terminator = line.IndexOf("\x1b\\", commandStart + KittyPrefix.Length, StringComparison.Ordinal);
            if (terminator == -1)
            {
                return null;
            }
            transmissionEnd = terminator + 2;
            if (!MoreChunksPattern().IsMatch(commandControls))
            {
                break;
            }
            commandStart = transmissionEnd;
            if (!line.AsSpan(commandStart).StartsWith(KittyPrefix.AsSpan(), StringComparison.Ordinal))
            {
                return null;
            }
            var controlsEnd = line.IndexOf(';', commandStart + KittyPrefix.Length);
            if (controlsEnd == -1)
            {
                return null;
            }
            commandControls = line.Substring(commandStart + KittyPrefix.Length, controlsEnd - commandStart - KittyPrefix.Length);
        }

        var controls = match.Groups[1].Value
            .Split(',')
            .Where(control => KittyPlacementControlKeys.Contains(control.Split('=', 2)[0]))
            .ToList();
        var sequence = $"{KittyPrefix}a=p,q=2,{string.Join(",", controls)}\x1b\\";
        return new KittyImagePlacement(
            metadata.ImageId,
            metadata.TransmissionGeneration,
            transmissionEnd - match.Index,
            (long)metadata.WidthPx * metadata.HeightPx * 4,
            GetKittyImageRowsFromControls(match.Groups[1].Value, metadata.Rows),
            sequence,
            string.Concat(line[..match.Index], sequence, line[transmissionEnd..]));
    }

    public static string CropKittyImageLine(string line, int hiddenRows, int visibleRows)
    {
        var metadata = GetKittyImageMetadata(line);
        var match = KittyControlsPattern().Match(line);
        if (metadata is null || !match.Success || hiddenRows < 0 || hiddenRows >= metadata.Rows || visibleRows <= 0)
        {
            return line;
        }
        var croppedRows = Math.Min(visibleRows, metadata.Rows - hiddenRows);
        if (hiddenRows == 0 && croppedRows == metadata.Rows)
        {
            return line;
        }
        var sourceY = (int)Math.Floor((double)(metadata.HeightPx * hiddenRows) / metadata.Rows);
        var sourceEnd = (int)Math.Ceiling((double)(metadata.HeightPx * (hiddenRows + croppedRows)) / metadata.Rows);
        var sourceHeight = Math.Max(1, Math.Min(metadata.HeightPx, sourceEnd) - sourceY);
        var controls = match.Groups[1].Value
            .Split(',')
            .Where(control => !CropControlPattern().IsMatch(control))
            .ToList();
        controls.Add($"y={sourceY}");
        controls.Add($"h={sourceHeight}");
        controls.Add($"r={croppedRows}");
        return string.Concat(
            line[..match.Index],
            KittyPrefix,
            string.Join(",", controls),
            ";",
            line[(match.Index + match.Length)..]);
    }

    // ------------------------------------------------------------------
    // Cell size math
    // ------------------------------------------------------------------

    private static int ChooseLessDistortedCellCount(int upperCount, double idealCount)
    {
        if (upperCount <= 1)
        {
            return upperCount;
        }

        var lowerCount = upperCount - 1;
        var upperDistortion = Math.Max(upperCount / idealCount, idealCount / upperCount);
        var lowerDistortion = Math.Max(lowerCount / idealCount, idealCount / lowerCount);
        return lowerDistortion < upperDistortion ? lowerCount : upperCount;
    }

    public static ImageCellSize CalculateImageCellSize(
        ImageDimensions imageDimensions,
        int maxWidthCells,
        int? maxHeightCells = null,
        CellDimensions? cellDimensions = null,
        bool optimizeAspectRatio = false)
    {
        var cells = cellDimensions ?? new CellDimensions(9, 18);
        var maxWidth = Math.Max(1, maxWidthCells);
        var maxHeight = maxHeightCells is { } height ? Math.Max(1, height) : (int?)null;
        var imageWidth = Math.Max(1, imageDimensions.WidthPx);
        var imageHeight = Math.Max(1, imageDimensions.HeightPx);

        var widthScale = (double)maxWidth * cells.WidthPx / imageWidth;
        var heightScale = maxHeight is null ? widthScale : (double)maxHeight.Value * cells.HeightPx / imageHeight;
        var scale = Math.Min(widthScale, heightScale);

        var scaledWidthPx = imageWidth * scale;
        var scaledHeightPx = imageHeight * scale;
        var columns = Math.Max(1, Math.Min(maxWidth, (int)Math.Ceiling(scaledWidthPx / cells.WidthPx)));
        var heightRows = scaledHeightPx / cells.HeightPx;
        var rows = Math.Max(1, (int)Math.Ceiling(heightRows));
        if (maxHeight is { } boundedHeight)
        {
            rows = Math.Min(boundedHeight, rows);
        }

        if (!optimizeAspectRatio)
        {
            return new ImageCellSize(columns, rows);
        }

        if (widthScale <= heightScale)
        {
            var idealRows = (double)columns * cells.WidthPx * imageHeight / (imageWidth * cells.HeightPx);
            rows = ChooseLessDistortedCellCount(rows, idealRows);
        }
        else
        {
            var idealColumns = (double)rows * cells.HeightPx * imageWidth / (imageHeight * cells.WidthPx);
            columns = ChooseLessDistortedCellCount(columns, idealColumns);
        }

        return new ImageCellSize(columns, rows);
    }

    public static int CalculateImageRows(
        ImageDimensions imageDimensions,
        int targetWidthCells,
        CellDimensions? cellDimensions = null) =>
        CalculateImageCellSize(imageDimensions, targetWidthCells, null, cellDimensions).Rows;

    // ------------------------------------------------------------------
    // Dimension parsers
    // ------------------------------------------------------------------

    private static uint ReadUInt32Be(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static int ReadUInt16Be(byte[] buffer, int offset) => (buffer[offset] << 8) | buffer[offset + 1];

    private static int ReadUInt16Le(byte[] buffer, int offset) => buffer[offset] | (buffer[offset + 1] << 8);

    private static uint ReadUInt32Le(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    private static byte[]? DecodeBase64(string base64Data)
    {
        try
        {
            return Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static ImageDimensions? GetPngDimensions(string base64Data)
    {
        var buffer = DecodeBase64(base64Data);
        if (buffer is null || buffer.Length < 24)
        {
            return null;
        }

        if (buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4e || buffer[3] != 0x47)
        {
            return null;
        }

        return new ImageDimensions((int)ReadUInt32Be(buffer, 16), (int)ReadUInt32Be(buffer, 20));
    }

    public static ImageDimensions? GetJpegDimensions(string base64Data)
    {
        var buffer = DecodeBase64(base64Data);
        if (buffer is null || buffer.Length < 2)
        {
            return null;
        }

        if (buffer[0] != 0xff || buffer[1] != 0xd8)
        {
            return null;
        }

        var offset = 2;
        while (offset < buffer.Length - 9)
        {
            if (buffer[offset] != 0xff)
            {
                offset++;
                continue;
            }

            var marker = buffer[offset + 1];

            if (marker is >= 0xc0 and <= 0xc2)
            {
                return new ImageDimensions(ReadUInt16Be(buffer, offset + 7), ReadUInt16Be(buffer, offset + 5));
            }

            if (offset + 3 >= buffer.Length)
            {
                return null;
            }
            var length = ReadUInt16Be(buffer, offset + 2);
            if (length < 2)
            {
                return null;
            }
            offset += 2 + length;
        }

        return null;
    }

    public static ImageDimensions? GetGifDimensions(string base64Data)
    {
        var buffer = DecodeBase64(base64Data);
        if (buffer is null || buffer.Length < 10)
        {
            return null;
        }

        var signature = System.Text.Encoding.ASCII.GetString(buffer, 0, 6);
        if (signature != "GIF87a" && signature != "GIF89a")
        {
            return null;
        }

        return new ImageDimensions(ReadUInt16Le(buffer, 6), ReadUInt16Le(buffer, 8));
    }

    public static ImageDimensions? GetWebpDimensions(string base64Data)
    {
        var buffer = DecodeBase64(base64Data);
        if (buffer is null || buffer.Length < 30)
        {
            return null;
        }

        var riff = System.Text.Encoding.ASCII.GetString(buffer, 0, 4);
        var webp = System.Text.Encoding.ASCII.GetString(buffer, 8, 4);
        if (riff != "RIFF" || webp != "WEBP")
        {
            return null;
        }

        var chunk = System.Text.Encoding.ASCII.GetString(buffer, 12, 4);
        if (chunk == "VP8 ")
        {
            if (buffer.Length < 30)
            {
                return null;
            }
            return new ImageDimensions(ReadUInt16Le(buffer, 26) & 0x3fff, ReadUInt16Le(buffer, 28) & 0x3fff);
        }
        if (chunk == "VP8L")
        {
            if (buffer.Length < 25)
            {
                return null;
            }
            var bits = ReadUInt32Le(buffer, 21);
            return new ImageDimensions((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
        }
        if (chunk == "VP8X")
        {
            if (buffer.Length < 30)
            {
                return null;
            }
            var width = (buffer[24] | (buffer[25] << 8) | (buffer[26] << 16)) + 1;
            var height = (buffer[27] | (buffer[28] << 8) | (buffer[29] << 16)) + 1;
            return new ImageDimensions(width, height);
        }

        return null;
    }

    public static ImageDimensions? GetImageDimensions(string base64Data, string mimeType) => mimeType switch
    {
        "image/png" => GetPngDimensions(base64Data),
        "image/jpeg" => GetJpegDimensions(base64Data),
        "image/gif" => GetGifDimensions(base64Data),
        "image/webp" => GetWebpDimensions(base64Data),
        _ => null,
    };

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    public static ImageRenderResult? RenderImage(
        string base64Data,
        ImageDimensions imageDimensions,
        ImageRenderOptions? options = null)
    {
        options ??= new ImageRenderOptions();
        var caps = GetCapabilities();

        if (caps.Images == ImageProtocol.None)
        {
            return null;
        }

        var maxWidth = options.MaxWidthCells ?? 80;
        // Reduce Kitty's cell-aligned distortion without shrinking iTerm2 reservations.
        var size = CalculateImageCellSize(
            imageDimensions,
            maxWidth,
            options.MaxHeightCells,
            GetCellDimensions(),
            caps.Images == ImageProtocol.Kitty);

        if (caps.Images == ImageProtocol.Kitty)
        {
            if (options.ImageId is { } imageId)
            {
                RegisterKittyImageMetadata(new KittyImageMetadata(
                    imageId,
                    size.Columns,
                    size.Rows,
                    imageDimensions.WidthPx,
                    imageDimensions.HeightPx));
            }
            var sequence = EncodeKitty(base64Data, new KittyEncodeOptions
            {
                Columns = size.Columns,
                Rows = size.Rows,
                ImageId = options.ImageId,
                MoveCursor = options.MoveCursor,
            });
            return new ImageRenderResult(sequence, size.Columns, size.Rows, options.ImageId);
        }

        if (caps.Images == ImageProtocol.Iterm2)
        {
            var sequence = EncodeITerm2(base64Data, new Iterm2EncodeOptions
            {
                Width = size.Columns,
                Height = Iterm2Size.Auto,
                PreserveAspectRatio = options.PreserveAspectRatio ?? true,
            });
            return new ImageRenderResult(sequence, size.Columns, size.Rows, null);
        }

        return null;
    }

    /// <summary>
    /// Wrap text in an OSC 8 hyperlink sequence. The text is rendered as a clickable hyperlink in
    /// terminals that support OSC 8 (Ghostty, Kitty, WezTerm, iTerm2, VSCode, and others). In terminals
    /// that do not support OSC 8, the escape sequences are ignored and only the plain text is displayed.
    /// </summary>
    public static string Hyperlink(string text, string url) => $"\x1b]8;;{url}\x1b\\{text}\x1b]8;;\x1b\\";

    /// <summary>Shorten home-prefixed absolute paths to <c>~/...</c> for compact display.</summary>
    private static string ShortenImagePath(string filename)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)
            && (filename == home || filename.StartsWith($"{home}/", StringComparison.Ordinal) || filename.StartsWith($"{home}\\", StringComparison.Ordinal)))
        {
            return "~" + filename.Substring(home.Length);
        }
        return filename;
    }

    private static string PathToFileUrl(string filename) => new Uri(Path.GetFullPath(filename)).AbsoluteUri;

    /// <summary>
    /// Text fallback when the terminal cannot render inline images. Absolute paths are shown shortened
    /// (<c>~/...</c>) and, when OSC 8 hyperlinks are available, linked to <c>file://</c> so the full path
    /// remains openable.
    /// </summary>
    public static string ImageFallback(string mimeType, ImageDimensions? dimensions = null, string? filename = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(filename))
        {
            var display = ShortenImagePath(filename);
            if (GetCapabilities().Hyperlinks && Path.IsPathFullyQualified(filename))
            {
                parts.Add(Hyperlink(display, PathToFileUrl(filename)));
            }
            else
            {
                parts.Add(display);
            }
        }
        parts.Add($"[{mimeType}]");
        if (dimensions is { } dims)
        {
            parts.Add($"{dims.WidthPx}x{dims.HeightPx}");
        }
        return $"[Image: {string.Join(" ", parts)}]";
    }
}
