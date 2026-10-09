using System.Text;
using Pi.Tui;
using SkiaSharp;

namespace Pi.CodingAgent.Utils.Image;

/// <summary>An image payload read from the clipboard. Port of the TS <c>ClipboardImage</c>.</summary>
public sealed record ClipboardImage(byte[] Bytes, string MimeType);

/// <summary>Options for <see cref="ClipboardImageApi.ReadClipboardImageAsync"/> (the TS options bag).</summary>
public sealed record ClipboardImageReadOptions
{
    /// <summary>Environment lookup. Defaults to the process environment (TS <c>process.env</c>).</summary>
    public Func<string, string?>? Env { get; init; }

    /// <summary>Platform id in the Node spelling (<c>linux</c> / <c>darwin</c> / <c>win32</c>).</summary>
    public string? Platform { get; init; }
}

/// <summary>
/// Port of <c>utils/clipboard-image.ts</c>: read an image from the system clipboard, trying the
/// platform-native route after the command-line backends.
/// </summary>
/// <remarks>
/// <para>
/// The backend helpers reproduce the TS tri-state: <see cref="ImageProbe.Undefined"/> means "the
/// backend itself failed, keep trying others" (TS <c>undefined</c>), a null <see cref="ImageProbe.Image"/>
/// means "this backend answered and there is no image, stop" (TS <c>null</c>). That distinction is
/// load-bearing: an empty Wayland clipboard must not fall through to stale X11 clipboard contents.
/// </para>
/// <para>
/// The TS mocks (<c>runClipboardCommand</c>, pi-tui's <c>getNativeClipboard</c>) become internal
/// override seams; the environment/platform come in through <see cref="ClipboardImageReadOptions"/>,
/// which the TS tests pass explicitly anyway. Unsupported formats are converted with SkiaSharp via
/// <see cref="SkiaImage"/> — note this path, like the TS photon call, does <em>not</em> apply EXIF
/// orientation (only <see cref="ImageConvert"/> does).
/// </para>
/// </remarks>
public static class ClipboardImageApi
{
    private const int DefaultListTimeoutMs = 1000;
    private const int DefaultPowerShellTimeoutMs = 5000;

    private static readonly string[] SupportedImageMimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    private static Func<string, IReadOnlyList<string>, ClipboardCommandOptions, Task<byte[]?>>? _commandRunnerOverride;
    private static Func<INativeClipboard?>? _nativeClipboardOverride;

    /// <summary>Replace the clipboard command runner (TS mocks the <c>clipboard-command</c> module).</summary>
    internal static Func<string, IReadOnlyList<string>, ClipboardCommandOptions, Task<byte[]?>>? CommandRunnerOverride
    {
        get => _commandRunnerOverride;
        set => _commandRunnerOverride = value;
    }

    /// <summary>Replace the native clipboard lookup (TS stubs pi-tui's <c>getNativeClipboard</c>).</summary>
    internal static Func<INativeClipboard?>? NativeClipboardOverride
    {
        get => _nativeClipboardOverride;
        set => _nativeClipboardOverride = value;
    }

    /// <summary>The TS backend result: <see cref="Image"/> null plus <see cref="IsUndefined"/> = backend failed.</summary>
    private readonly record struct ImageProbe(ClipboardImage? Image, bool IsUndefined)
    {
        public static readonly ImageProbe Undefined = default;
        public static ImageProbe NoImage => new(null, false);
        public static ImageProbe Found(ClipboardImage image) => new(image, false);
    }

    /// <summary>The TS <c>isWaylandSession</c>.</summary>
    public static bool IsWaylandSession(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return !string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) || env("XDG_SESSION_TYPE") == "wayland";
    }

    /// <summary>The TS <c>extensionForImageMimeType</c>.</summary>
    public static string? ExtensionForImageMimeType(string mimeType) =>
        BaseMimeType(mimeType) switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            _ => null,
        };

    /// <summary>The TS <c>baseMimeType</c> (module-local in clipboard-image.ts).</summary>
    internal static string BaseMimeType(string mimeType)
    {
        string? first = mimeType.Split(';')[0];
        return first is null ? mimeType.ToLowerInvariant() : first.Trim().ToLowerInvariant();
    }

    /// <summary>The TS <c>selectPreferredImageMimeType</c>: first supported, else any image/*.</summary>
    internal static string? SelectPreferredImageMimeType(IEnumerable<string> mimeTypes)
    {
        var normalized = mimeTypes
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t => (Raw: t, Base: BaseMimeType(t)))
            .ToList();

        foreach (string preferred in SupportedImageMimeTypes)
        {
            string? match = normalized.FirstOrDefault(t => t.Base == preferred).Raw;
            if (match is not null)
            {
                return match;
            }
        }

        return normalized.FirstOrDefault(t => t.Base.StartsWith("image/", StringComparison.Ordinal)).Raw;
    }

    private static bool IsSupportedImageMimeType(string mimeType) =>
        SupportedImageMimeTypes.Any(t => t == BaseMimeType(mimeType));

    /// <summary>
    /// The TS <c>convertToPng</c>: re-encode an unsupported payload as PNG. Null when conversion is
    /// unavailable or fails. Like the TS photon call, this does not apply EXIF orientation.
    /// </summary>
    private static byte[]? ConvertToPng(byte[] bytes)
    {
        try
        {
            SKBitmap? image = SkiaImage.Decode(bytes);
            return image is null ? null : SkiaImage.EncodePng(image);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The TS <c>readClipboardImageViaWlPaste</c>.</summary>
    private static async Task<ImageProbe> ReadViaWlPasteAsync()
    {
        byte[]? list = await RunCommand("wl-paste", ["--list-types"],
            new ClipboardCommandOptions { TimeoutMs = DefaultListTimeoutMs }).ConfigureAwait(false);
        if (list is null)
        {
            return ImageProbe.Undefined;
        }

        string selectedType = SelectPreferredImageMimeType(SplitLines(list)) is { } type ? type : "";
        if (selectedType.Length == 0)
        {
            return ImageProbe.NoImage;
        }

        byte[]? data = await RunCommand("wl-paste", ["--type", selectedType, "--no-newline"],
            new ClipboardCommandOptions()).ConfigureAwait(false);
        if (data is null)
        {
            return ImageProbe.Undefined;
        }

        if (data.Length == 0)
        {
            return ImageProbe.NoImage;
        }

        return ImageProbe.Found(new ClipboardImage(data, BaseMimeType(selectedType)));
    }

    /// <summary>
    /// The TS <c>readClipboardImageViaPowerShell</c>: on WSL, the Linux clipboard does not receive
    /// image data from Windows screenshots, so ask PowerShell for the Windows clipboard directly.
    /// </summary>
    private static async Task<ClipboardImage?> ReadViaPowerShellAsync()
    {
        string tmpFile = Path.Combine(Path.GetTempPath(), $"pi-wsl-clip-{Guid.NewGuid():D}.png");

        try
        {
            byte[]? winPathResult = await RunCommand("wslpath", ["-w", tmpFile],
                new ClipboardCommandOptions { TimeoutMs = DefaultListTimeoutMs }).ConfigureAwait(false);
            if (winPathResult is null)
            {
                return null;
            }

            string winPath = Encoding.UTF8.GetString(winPathResult).Trim();
            if (winPath.Length == 0)
            {
                return null;
            }

            string psQuotedWinPath = winPath.Replace("'", "''", StringComparison.Ordinal);
            string psScript = string.Join("; ",
            [
                "Add-Type -AssemblyName System.Windows.Forms",
                "Add-Type -AssemblyName System.Drawing",
                $"$path = '{psQuotedWinPath}'",
                "$img = [System.Windows.Forms.Clipboard]::GetImage()",
                "if ($img) { $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); " +
                "Write-Output 'ok' } else { Write-Output 'empty' }",
            ]);

            byte[]? result = await RunCommand("powershell.exe", ["-NoProfile", "-Command", psScript],
                new ClipboardCommandOptions { TimeoutMs = DefaultPowerShellTimeoutMs }).ConfigureAwait(false);
            if (result is null)
            {
                return null;
            }

            string output = Encoding.UTF8.GetString(result).Trim();
            if (output != "ok")
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(tmpFile).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            return new ClipboardImage(bytes, "image/png");
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(tmpFile);
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    /// <summary>The TS <c>readClipboardImageViaXclip</c>.</summary>
    private static async Task<ImageProbe> ReadViaXclipAsync()
    {
        byte[]? targets = await RunCommand("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"],
            new ClipboardCommandOptions { TimeoutMs = DefaultListTimeoutMs }).ConfigureAwait(false);
        if (targets is null)
        {
            return ImageProbe.Undefined;
        }

        string preferred = SelectPreferredImageMimeType(SplitLines(targets)) is { } type ? type : "";
        if (preferred.Length == 0)
        {
            return ImageProbe.NoImage;
        }

        byte[]? data = await RunCommand("xclip", ["-selection", "clipboard", "-t", preferred, "-o"],
            new ClipboardCommandOptions()).ConfigureAwait(false);
        if (data is null)
        {
            return ImageProbe.Undefined;
        }

        if (data.Length == 0)
        {
            return ImageProbe.NoImage;
        }

        return ImageProbe.Found(new ClipboardImage(data, BaseMimeType(preferred)));
    }

    /// <summary>The TS <c>readClipboardImageViaNativeClipboard</c>.</summary>
    private static async Task<ImageProbe> ReadViaNativeAsync()
    {
        INativeClipboard? clipboard = Native();
        if (clipboard is null)
        {
            return ImageProbe.Undefined;
        }

        // Transfer failures throw, like the TS rejection — and propagate out of
        // ReadClipboardImageAsync without fallback, as the original tests pin.
        byte[]? bytes = await clipboard.GetImageAsync().ConfigureAwait(false);

        // The C# native interface collapses TS null/undefined into null ("unavailable"); an empty
        // array is "no image". Native is always the last backend, so the collapse is unobservable.
        if (bytes is null)
        {
            return ImageProbe.Undefined;
        }

        if (bytes.Length == 0)
        {
            return ImageProbe.NoImage;
        }

        return ImageProbe.Found(
            new ClipboardImage(bytes, Mime.DetectSupportedImageMimeType(bytes) ?? "application/octet-stream"));
    }

    /// <summary>The TS <c>readClipboardImage</c>: the platform dispatch over the backends above.</summary>
    public static async Task<ClipboardImage?> ReadClipboardImageAsync(ClipboardImageReadOptions? options = null)
    {
        var env = options?.Env ?? Environment.GetEnvironmentVariable;
        string platform = options?.Platform ?? ProcessInfo.Platform;

        if (!string.IsNullOrEmpty(env("TERMUX_VERSION")))
        {
            return null;
        }

        ImageProbe image = ImageProbe.Undefined;

        if (platform == "linux")
        {
            bool wsl = Wsl.IsWSL(env);
            if (IsWaylandSession(env) || wsl)
            {
                image = await ReadViaWlPasteAsync().ConfigureAwait(false);
            }

            if (image.IsUndefined)
            {
                image = await ReadViaXclipAsync().ConfigureAwait(false);
            }

            // Preserve Linux's empty/unavailable distinction if Windows has no image.
            if (image.Image is null && wsl)
            {
                image = await ReadViaPowerShellAsync().ConfigureAwait(false) is { } psImage
                    ? ImageProbe.Found(psImage)
                    : image;
            }

            if (image.IsUndefined)
            {
                image = await ReadViaNativeAsync().ConfigureAwait(false);
            }
        }
        else
        {
            image = await ReadViaNativeAsync().ConfigureAwait(false);
        }

        if (image.Image is null)
        {
            return null;
        }

        // Convert unsupported formats (e.g., Windows DIB data wrapped as BMP) to PNG
        if (!IsSupportedImageMimeType(image.Image.MimeType))
        {
            byte[]? pngBytes = ConvertToPng(image.Image.Bytes);
            if (pngBytes is null)
            {
                return null;
            }

            return new ClipboardImage(pngBytes, "image/png");
        }

        return image.Image;
    }

    private static Task<byte[]?> RunCommand(
        string command, IReadOnlyList<string> args, ClipboardCommandOptions options) =>
        _commandRunnerOverride is { } runner
            ? runner(command, args, options)
            : ClipboardCommand.RunAsync(command, args, options);

    private static INativeClipboard? Native() =>
        _nativeClipboardOverride is { } lookup ? lookup() : NativePlatform.GetNativeClipboard();

    /// <summary>The TS <c>list.toString("utf-8").split(/\r?\n/)</c> → trim → drop empties.</summary>
    private static string[] SplitLines(byte[] buffer) =>
        Encoding.UTF8.GetString(buffer)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();
}
