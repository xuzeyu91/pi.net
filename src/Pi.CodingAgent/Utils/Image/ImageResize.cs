namespace Pi.CodingAgent.Utils.Image;

/// <summary>
/// Port of <c>utils/image-resize.ts</c>: the caller-facing resize entry.
/// </summary>
/// <remarks>
/// <para>
/// The TS runs the resize in a <c>worker_threads</c> Worker so WASM decode/resize/encode do not
/// block the TUI event loop, falling back to in-process resizing when the worker cannot be loaded
/// (Bun compiled executables). C# has no worker-file concept: the same goal is achieved by running
/// the CPU-bound core on the thread pool (<see cref="ResizeImageAsync"/>), which cannot "fail to
/// load", so the fallback branch has no failure mode to reproduce. <c>image-resize-worker.ts</c>
/// (the worker entry file) therefore has no C# counterpart — it exists only to host
/// <c>resizeImageInProcess</c> behind a message port, which <see cref="ResizeImageAsync"/> does
/// with a delegate.
/// </para>
/// </remarks>
public static class ImageResize
{
    /// <summary>
    /// The TS <c>resizeImage</c>: the in-process core off the caller's thread (worker → thread pool).
    /// </summary>
    public static Task<ResizedImage?> ResizeImageAsync(byte[] inputBytes, string mimeType, ImageResizeOptions? options) =>
        Task.Run(() => ImageResizeCore.ResizeImageInProcess(inputBytes, mimeType, options));

    /// <summary>
    /// The TS <c>formatDimensionNote</c>: a coordinate-mapping hint for the model, present only for
    /// resized images. Scale uses JS <c>toFixed(2)</c> semantics (half away from zero at the 2nd
    /// decimal — .NET's default rounding is banker's, which differs on exact ties like 0.125).
    /// </summary>
    public static string? FormatDimensionNote(ResizedImage result)
    {
        if (!result.WasResized)
        {
            return null;
        }

        double scale = (double)result.OriginalWidth / result.Width;
        string scaleText = Math.Round(scale, 2, MidpointRounding.AwayFromZero)
            .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return
            $"[Image: original {result.OriginalWidth}x{result.OriginalHeight}, displayed at {result.Width}x{result.Height}. Multiply coordinates by {scaleText} to map to original image.]";
    }
}
