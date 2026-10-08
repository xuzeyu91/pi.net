using System.Security.Cryptography;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Output files: files pi writes so the model can use output it was not shown in full, such as the full
/// text of truncated tool output, binary MCP resources, and images shown by codemode scripts. Every
/// output file is created here, so where they are stored can change in one place. Today they go to the
/// OS temp directory. Port of <c>utils/output-files.ts</c>.
/// </summary>
public static class OutputFiles
{
    /// <summary>Output can carry private data, so only the user may read the files.</summary>
    private const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>A new, unused path: <c>&lt;dir&gt;/&lt;prefix&gt;-&lt;random hex&gt;&lt;extension&gt;</c>.</summary>
    public static string CreateOutputFilePath(string prefix, string extension) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{RandomHex(8)}{extension}");

    /// <summary>Write <paramref name="data"/> to a new output file and return its path.</summary>
    public static async Task<string> WriteOutputFileAsync(string prefix, string extension, ReadOnlyMemory<byte> data)
    {
        var path = CreateOutputFilePath(prefix, extension);
        await using var stream = OpenNew(path);
        await stream.WriteAsync(data).ConfigureAwait(false);
        return path;
    }

    /// <summary>Write <paramref name="data"/> (UTF-8) to a new output file and return its path.</summary>
    public static Task<string> WriteOutputFileAsync(string prefix, string extension, string data) =>
        WriteOutputFileAsync(prefix, extension, System.Text.Encoding.UTF8.GetBytes(data));

    /// <summary>Open a new output file for streamed output.</summary>
    public static (string Path, FileStream Stream) CreateOutputFileStream(string prefix, string extension)
    {
        var path = CreateOutputFilePath(prefix, extension);
        return (path, OpenNew(path));
    }

    /// <summary><c>flag: "wx"</c> — create only, never following a link someone else placed at the path.</summary>
    private static FileStream OpenNew(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };

        // UnixCreateMode throws on Windows, so it is only set where it is meaningful.
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = Mode;
        }

        return new FileStream(path, options);
    }

    private static string RandomHex(int byteCount) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));
}
