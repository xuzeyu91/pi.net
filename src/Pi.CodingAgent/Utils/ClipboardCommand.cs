using System.Text;

namespace Pi.CodingAgent.Utils;

/// <summary>Options for <see cref="ClipboardCommand.RunAsync"/>.</summary>
public sealed record ClipboardCommandOptions
{
    /// <summary>Text written to the child's standard input; <see langword="null"/> closes it immediately.</summary>
    public string? Input { get; init; }

    /// <summary>Kill the child after this many milliseconds. Defaults to 3000.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Abort once the captured output exceeds this many bytes. Defaults to 50 MiB.</summary>
    public int? MaxBufferBytes { get; init; }
}

/// <summary>Port of <c>utils/clipboard-command.ts</c>.</summary>
/// <remarks>
/// The <c>stdio</c> tuple in the TS source is <c>[stdin, stdout, stderr]</c>:
/// <c>["pipe", input === undefined ? "pipe" : "ignore", "ignore"]</c>. Stdin is always a pipe so the
/// text can be written, and stdout is captured only for reader commands, because a writer command's
/// success is decided by its exit code alone.
/// </remarks>
public static class ClipboardCommand
{
    private const int DefaultTimeoutMs = 3000;
    private const int DefaultMaxBufferBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Run a clipboard command.
    /// </summary>
    /// <returns>
    /// The captured standard output, or <see langword="null"/> when the command failed, timed out, or
    /// overflowed the buffer. An empty array is a successful result with no output.
    /// </returns>
    public static async Task<byte[]?> RunAsync(
        string command,
        IReadOnlyList<string> args,
        ClipboardCommandOptions? options = null)
    {
        options ??= new ClipboardCommandOptions();
        var maxBufferBytes = options.MaxBufferBytes ?? DefaultMaxBufferBytes;
        var captureStdout = options.Input is null;

        // Clipboard writers can daemonize, so they must not be given output pipes to retain.
        var child = ChildProcess.SpawnProcess(command, args, new SpawnOptions
        {
            Stdio = [StdioMode.Pipe, captureStdout ? StdioMode.Pipe : StdioMode.Ignore, StdioMode.Ignore],
            WindowsHide = true,
        });

        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<byte[]>();
        var length = 0;
        var settled = false;

        void Finish(byte[]? result)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            completion.TrySetResult(result);
        }

        void Abort()
        {
            if (settled)
            {
                return;
            }

            child.Kill();
            Finish(null);
        }

        // Mirrors `setTimeout(abort, timeoutMs)`; Abort is idempotent so a late fire is harmless.
        _ = Task.Delay(options.TimeoutMs ?? DefaultTimeoutMs).ContinueWith(_ => Abort(), TaskScheduler.Default);

        child.StdoutData += chunk =>
        {
            if (settled)
            {
                return;
            }

            length += chunk.Length;
            if (length > maxBufferBytes)
            {
                Abort();
                return;
            }

            chunks.Add(chunk);
        };

        if (child.Stdin is { } stdin)
        {
            try
            {
                // A writer may exit before consuming all of its input, so a broken pipe is expected.
                await stdin.WriteAsync(Encoding.UTF8.GetBytes(options.Input ?? string.Empty)).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
                stdin.Close();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Ignore.
            }
        }

        try
        {
            var exitCode = await child.WaitAsync().ConfigureAwait(false);
            Finish(exitCode == 0 ? Concat(chunks, length) : null);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Finish(null);
        }
        finally
        {
            child.Dispose();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private static byte[] Concat(IReadOnlyList<byte[]> chunks, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(buffer, offset);
            offset += chunk.Length;
        }

        return buffer;
    }
}
