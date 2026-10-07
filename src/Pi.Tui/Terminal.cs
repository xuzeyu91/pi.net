using System.Runtime.InteropServices;
using System.Text;

namespace Pi.Tui;

/// <summary>Minimal terminal interface for TUI (port of <c>terminal.ts</c>'s <c>Terminal</c>).</summary>
public interface ITerminal
{
    /// <summary>Start the terminal with input and resize handlers.</summary>
    void Start(Action<string> onInput, Action onResize);

    /// <summary>Stop the terminal and restore state.</summary>
    void Stop();

    /// <summary>Drain stdin before exiting so late key releases do not leak to the parent shell.</summary>
    Task DrainInputAsync(int maxMs = 1000, int idleMs = 50);

    /// <summary>Write output to the terminal.</summary>
    void Write(string data);

    int Columns { get; }

    int Rows { get; }

    /// <summary>Whether the Kitty keyboard protocol is active.</summary>
    bool KittyProtocolActive { get; }

    /// <summary>Move the cursor up (negative) or down (positive) by N lines.</summary>
    void MoveBy(int lines);

    void HideCursor();

    void ShowCursor();

    void ClearLine();

    void ClearFromCursor();

    void ClearScreen();

    void SetTitle(string title);

    /// <summary>Indeterminate progress indicator (OSC 9;4).</summary>
    void SetProgress(bool active);
}

/// <summary>
/// Real terminal using <see cref="Console"/> standard input/output.
///
/// Deviation from TS: Node exposes <c>process.stdin.setRawMode</c>; .NET has no cross-platform raw
/// mode API. On Unix this port shells out to <c>stty raw -echo</c> (restoring <c>stty sane</c> on
/// stop); on Windows it enables virtual-terminal input via P/Invoke. Input is read on a background
/// thread with <see cref="Console.ReadKey(bool)"/> and mapped to the escape sequences the rest of the
/// framework expects.
/// </summary>
public sealed class ProcessTerminal : ITerminal
{
    private const string ProgressActive = "\x1b]9;4;3\x07";
    private const string ProgressClear = "\x1b]9;4;0\x07";
    private const int ProgressKeepAliveMs = 1000;

    private Action<string>? _inputHandler;
    private Action? _resizeHandler;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private Timer? _progressTimer;
    private bool _started;
    private bool _rawModeSet;

    public bool KittyProtocolActive => Keys.IsKittyProtocolActive();

    public int Columns => Math.Max(1, Safe(() => Console.WindowWidth, ParseEnv("COLUMNS", 80)));

    public int Rows => Math.Max(1, Safe(() => Console.WindowHeight, ParseEnv("LINES", 24)));

    private static int ParseEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static int Safe(Func<int> get, int fallback)
    {
        try
        {
            var v = get();
            return v > 0 ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void Start(Action<string> onInput, Action onResize)
    {
        _inputHandler = onInput;
        _resizeHandler = onResize;
        _started = true;

        Console.OutputEncoding = Encoding.UTF8;
        EnableRawMode();

        // Bracketed paste mode.
        Write("\x1b[?2004h");

        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_readCts.Token));
    }

    private void ReadLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ConsoleKeyInfo keyInfo;
            try
            {
                if (Console.IsInputRedirected)
                {
                    var read = Console.In.Read();
                    if (read < 0)
                    {
                        break;
                    }
                    _inputHandler?.Invoke(char.ConvertFromUtf32(read));
                    continue;
                }
                keyInfo = Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            var sequence = MapKey(keyInfo);
            if (sequence is not null)
            {
                _inputHandler?.Invoke(sequence);
            }
        }
    }

    /// <summary>Map a <see cref="ConsoleKeyInfo"/> to the terminal escape sequence the framework expects.</summary>
    internal static string? MapKey(ConsoleKeyInfo key)
    {
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return alt ? "\x1bp" : shift ? "\x1b[a" : ctrl ? "\x1bOa" : "\x1b[A";
            case ConsoleKey.DownArrow: return alt ? "\x1bn" : shift ? "\x1b[b" : ctrl ? "\x1bOb" : "\x1b[B";
            case ConsoleKey.RightArrow: return alt ? "\x1bf" : shift ? "\x1b[c" : ctrl ? "\x1bOc" : "\x1b[C";
            case ConsoleKey.LeftArrow: return alt ? "\x1bb" : shift ? "\x1b[d" : ctrl ? "\x1bOd" : "\x1b[D";
            case ConsoleKey.Home: return "\x1b[H";
            case ConsoleKey.End: return "\x1b[F";
            case ConsoleKey.PageUp: return "\x1b[5~";
            case ConsoleKey.PageDown: return "\x1b[6~";
            case ConsoleKey.Insert: return "\x1b[2~";
            case ConsoleKey.Delete: return "\x1b[3~";
            case ConsoleKey.Escape: return "\x1b";
            case ConsoleKey.Enter: return alt ? "\x1b\r" : shift ? "\x1b[13;2u" : "\r";
            case ConsoleKey.Tab: return shift ? "\x1b[Z" : "\t";
            case ConsoleKey.Backspace: return alt ? "\x1b\x7f" : "\x7f";
            case ConsoleKey.Spacebar: return ctrl ? "\x00" : alt ? "\x1b " : " ";
            case ConsoleKey.F1: return "\x1bOP";
            case ConsoleKey.F2: return "\x1bOQ";
            case ConsoleKey.F3: return "\x1bOR";
            case ConsoleKey.F4: return "\x1bOS";
            case ConsoleKey.F5: return "\x1b[15~";
            case ConsoleKey.F6: return "\x1b[17~";
            case ConsoleKey.F7: return "\x1b[18~";
            case ConsoleKey.F8: return "\x1b[19~";
            case ConsoleKey.F9: return "\x1b[20~";
            case ConsoleKey.F10: return "\x1b[21~";
            case ConsoleKey.F11: return "\x1b[23~";
            case ConsoleKey.F12: return "\x1b[24~";
        }

        if (key.KeyChar == '\0')
        {
            return null;
        }

        if (ctrl && key.KeyChar is >= (char)1 and <= (char)26)
        {
            return alt ? $"\x1b{key.KeyChar}" : key.KeyChar.ToString();
        }

        var text = key.KeyChar.ToString();
        if (alt)
        {
            return $"\x1b{text}";
        }
        return text;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;

        if (_progressTimer is not null)
        {
            _progressTimer.Dispose();
            _progressTimer = null;
            Write(ProgressClear);
        }

        Write("\x1b[?2004l");

        try
        {
            _readCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        RestoreRawMode();
        _inputHandler = null;
        _resizeHandler = null;
    }

    public async Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        _inputHandler = null;
        var deadline = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(Math.Min(idleMs, Math.Max(0, (int)(deadline - Environment.TickCount64)))).ConfigureAwait(false);
            if (Environment.TickCount64 >= deadline)
            {
                break;
            }
        }
    }

    public void Write(string data)
    {
        Console.Out.Write(data);
        Console.Out.Flush();
    }

    public void MoveBy(int lines)
    {
        if (lines > 0)
        {
            Write($"\x1b[{lines}B");
        }
        else if (lines < 0)
        {
            Write($"\x1b[{-lines}A");
        }
    }

    public void HideCursor() => Write("\x1b[?25l");

    public void ShowCursor() => Write("\x1b[?25h");

    public void ClearLine() => Write("\x1b[K");

    public void ClearFromCursor() => Write("\x1b[J");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void SetTitle(string title) => Write($"\x1b]0;{title}\x07");

    public void SetProgress(bool active)
    {
        if (active)
        {
            Write(ProgressActive);
            _progressTimer ??= new Timer(_ => Write(ProgressActive), null, ProgressKeepAliveMs, ProgressKeepAliveMs);
        }
        else
        {
            _progressTimer?.Dispose();
            _progressTimer = null;
            Write(ProgressClear);
        }
    }

    // ------------------------------------------------------------------
    // Raw mode (best effort)
    // ------------------------------------------------------------------

    private void EnableRawMode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            EnableWindowsVirtualTerminalInput();
            return;
        }
        try
        {
            RunStty("raw -echo");
            _rawModeSet = true;
        }
        catch
        {
            // Best effort; interactive input may not work without raw mode.
        }
    }

    private void RestoreRawMode()
    {
        if (_rawModeSet)
        {
            try
            {
                RunStty("sane");
            }
            catch
            {
                // ignore
            }
            _rawModeSet = false;
        }
    }

    private static void RunStty(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stty", args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(2000);
    }

    private void EnableWindowsVirtualTerminalInput()
    {
        try
        {
            const int StdInputHandle = -10;
            const uint EnableVirtualTerminalInput = 0x0200;
            var handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return;
            }
            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, mode | EnableVirtualTerminalInput);
            }
        }
        catch
        {
            // Native helper not available.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

/// <summary>
/// In-memory terminal used by tests and headless rendering. Records all writes and lets tests push
/// input synchronously. Not present in the TS source; it fills the role of the test harnesses that
/// inject a fake <c>Terminal</c> in vitest.
/// </summary>
public sealed class StringTerminal : ITerminal
{
    private readonly StringBuilder _output = new();
    private Action<string>? _inputHandler;

    public StringTerminal(int columns = 80, int rows = 24)
    {
        Columns = columns;
        Rows = rows;
    }

    public int Columns { get; set; }

    public int Rows { get; set; }

    public bool KittyProtocolActive { get; set; }

    public bool Started { get; private set; }

    public string Output => _output.ToString();

    public void ClearOutput() => _output.Clear();

    public void Start(Action<string> onInput, Action onResize)
    {
        _inputHandler = onInput;
        Started = true;
    }

    public void Stop() => Started = false;

    public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        _inputHandler = null;
        return Task.CompletedTask;
    }

    public void Write(string data) => _output.Append(data);

    /// <summary>Simulate terminal input arriving.</summary>
    public void SendInput(string data) => _inputHandler?.Invoke(data);

    /// <summary>Simulate a terminal resize.</summary>
    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public void MoveBy(int lines) => Write(lines > 0 ? $"\x1b[{lines}B" : lines < 0 ? $"\x1b[{-lines}A" : "");

    public void HideCursor() => Write("\x1b[?25l");

    public void ShowCursor() => Write("\x1b[?25h");

    public void ClearLine() => Write("\x1b[K");

    public void ClearFromCursor() => Write("\x1b[J");

    public void ClearScreen() => Write("\x1b[2J\x1b[H");

    public void SetTitle(string title) => Write($"\x1b]0;{title}\x07");

    public void SetProgress(bool active) => Write(active ? "\x1b]9;4;3\x07" : "\x1b]9;4;0\x07");
}
