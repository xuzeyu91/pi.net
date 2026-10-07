namespace Pi.Tui.Components;

/// <summary>
/// Converts base64 image data to base64 PNG data, or returns null if it cannot. Called synchronously
/// during rendering (port of the TS <c>ImageTranscoder</c> type).
/// </summary>
public delegate string? ImageTranscoder(string base64Data, string mimeType);

/// <summary>Theme hooks used by <see cref="Image"/> (port of <c>ImageTheme</c>).</summary>
public sealed class ImageTheme
{
    public required Func<string, string> FallbackColor { get; init; }
}

/// <summary>Construction options for <see cref="Image"/> (port of <c>ImageOptions</c>).</summary>
public sealed class ImageOptions
{
    public int? MaxWidthCells { get; init; }

    public int? MaxHeightCells { get; init; }

    public string? Filename { get; init; }

    /// <summary>Kitty image ID. If provided, reuses this ID (for animations/updates).</summary>
    public long? ImageId { get; init; }
}

/// <summary>
/// Image component that renders inline images on capable terminals and a text fallback otherwise
/// (port of <c>components/image.ts</c>).
/// </summary>
public sealed class Image : IComponent
{
    private const int DefaultMaxWidthCells = 60;

    private const int PngCacheLimit = 32;

    private static ImageTranscoder? _imageTranscoder;

    // Backstop for callers that recreate Image instances. Keyed by source data, least recently used first.
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, string?> PngCache = new(StringComparer.Ordinal);
    private static readonly List<string> PngCacheOrder = [];

    private readonly string _base64Data;
    private readonly string _mimeType;
    private readonly ImageDimensions _dimensions;
    private readonly ImageTheme _theme;
    private readonly ImageOptions _options;

    private long? _imageId;

    /// <summary>Converted PNG data for Kitty. Failures are not stored so a later transcoder can retry.</summary>
    private string? _pngData;

    private string[]? _cachedLines;
    private int? _cachedWidth;

    public Image(
        string base64Data,
        string mimeType,
        ImageTheme theme,
        ImageOptions? options = null,
        ImageDimensions? dimensions = null)
    {
        _base64Data = base64Data;
        _mimeType = mimeType;
        _theme = theme;
        _options = options ?? new ImageOptions();
        _dimensions = dimensions
            ?? TerminalImage.GetImageDimensions(base64Data, mimeType)
            ?? new ImageDimensions(800, 600);
        _imageId = _options.ImageId;
    }

    /// <summary>Get the Kitty image ID used by this image (if any).</summary>
    public long? GetImageId() => _imageId;

    /// <summary>
    /// Register the converter used for non-PNG images on Kitty-protocol terminals, which only accept PNG.
    /// Without one, such images render as text fallbacks.
    /// </summary>
    public static void SetImageTranscoder(ImageTranscoder? transcoder)
    {
        lock (CacheGate)
        {
            _imageTranscoder = transcoder;
            PngCache.Clear();
            PngCacheOrder.Clear();
        }
    }

    private static string? ToPng(string base64Data, string mimeType)
    {
        ImageTranscoder? transcoder;
        lock (CacheGate)
        {
            transcoder = _imageTranscoder;
        }
        if (transcoder is null)
        {
            return null;
        }

        lock (CacheGate)
        {
            var png = PngCache.TryGetValue(base64Data, out var cached) ? cached : transcoder(base64Data, mimeType);
            PngCache.Remove(base64Data);
            PngCacheOrder.Remove(base64Data);
            PngCache[base64Data] = png;
            PngCacheOrder.Add(base64Data);
            if (PngCache.Count > PngCacheLimit)
            {
                var oldest = PngCacheOrder[0];
                PngCacheOrder.RemoveAt(0);
                PngCache.Remove(oldest);
            }
            return png;
        }
    }

    public void Invalidate()
    {
        _cachedLines = null;
        _cachedWidth = null;
    }

    public string[] Render(int width)
    {
        if (_cachedLines is not null && _cachedWidth == width)
        {
            return _cachedLines;
        }

        var maxWidth = Math.Max(1, Math.Min(width - 2, _options.MaxWidthCells ?? DefaultMaxWidthCells));
        var cellDimensions = TerminalImage.GetCellDimensions();
        var defaultMaxHeight = Math.Max(
            1,
            (int)Math.Ceiling((double)maxWidth * cellDimensions.WidthPx / cellDimensions.HeightPx));
        var maxHeight = _options.MaxHeightCells ?? defaultMaxHeight;

        var caps = TerminalImage.GetCapabilities();
        string? data = _base64Data;
        var dimensions = _dimensions;
        if (caps.Images == ImageProtocol.Kitty && _mimeType != "image/png")
        {
            _pngData ??= ToPng(_base64Data, _mimeType);
            data = _pngData;

            // Conversion may apply EXIF rotation, so prefer the PNG's own dimensions.
            if (!string.IsNullOrEmpty(data))
            {
                dimensions = TerminalImage.GetPngDimensions(data) ?? dimensions;
            }
        }

        string[] lines;
        if (caps.Images != ImageProtocol.None && !string.IsNullOrEmpty(data))
        {
            if (caps.Images == ImageProtocol.Kitty && _imageId is null)
            {
                _imageId = TerminalImage.AllocateImageId();
            }

            var result = TerminalImage.RenderImage(
                data,
                dimensions,
                new ImageRenderOptions
                {
                    MaxWidthCells = maxWidth,
                    MaxHeightCells = maxHeight,
                    ImageId = _imageId,
                    MoveCursor = false,
                });

            if (result is not null)
            {
                // Store the image ID for later cleanup
                if (result.ImageId is > 0)
                {
                    _imageId = result.ImageId;
                }

                if (caps.Images == ImageProtocol.Kitty)
                {
                    // For Kitty: C=1 prevents cursor movement.
                    // Don't need the cursor movement.
                    var kittyLines = new List<string> { result.Sequence };

                    // Return `rows` lines so TUI accounts for image height.
                    for (var i = 0; i < result.Rows - 1; i++)
                    {
                        kittyLines.Add("");
                    }
                    lines = [.. kittyLines];
                }
                else
                {
                    // Return `rows` lines so TUI accounts for image height.
                    // First (rows-1) lines are empty and cleared before the image is drawn.
                    // Last line: move cursor back up, draw the image, then move back down
                    // so TUI cursor accounting stays inside the scroll area.
                    var itermLines = new List<string>();
                    for (var i = 0; i < result.Rows - 1; i++)
                    {
                        itermLines.Add("");
                    }
                    var rowOffset = result.Rows - 1;
                    var moveUp = rowOffset > 0 ? $"\x1b[{rowOffset}A" : "";
                    itermLines.Add(moveUp + result.Sequence);
                    lines = [.. itermLines];
                }
            }
            else
            {
                lines = RenderFallback(width);
            }
        }
        else
        {
            lines = RenderFallback(width);
        }

        _cachedLines = lines;
        _cachedWidth = width;

        return lines;
    }

    private string[] RenderFallback(int width)
    {
        var fallback = TerminalImage.ImageFallback(_mimeType, _dimensions, _options.Filename);
        return [TextLayout.TruncateToWidth(_theme.FallbackColor(fallback), width)];
    }
}
