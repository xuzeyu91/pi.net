using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Completion status of a candidate escape sequence.</summary>
internal enum SequenceStatus
{
    Complete,
    Incomplete,
    NotEscape,
}

/// <summary>Options for <see cref="StdinBuffer"/>.</summary>
public sealed record StdinBufferOptions
{
    /// <summary>Maximum time to wait for an incomplete sequence such as CSI or mouse (default: 50 ms).</summary>
    public int Timeout { get; init; } = StdinBuffer.DefaultSequenceTimeoutMs;

    /// <summary>
    /// Maximum time to wait after a lone ESC before treating it as Escape (default: 10 ms). Increase for
    /// high-latency Alt+key input (SSH).
    /// </summary>
    public int EscapeTimeout { get; init; } = StdinBuffer.DefaultEscapeTimeoutMs;
}

/// <summary>
/// Buffers stdin input and emits complete sequences (port of <c>stdin-buffer.ts</c>).
///
/// This is necessary because stdin data events can arrive in partial chunks, especially for escape
/// sequences like mouse events. Without buffering, partial sequences can be misinterpreted as regular
/// keypresses. For example, the mouse SGR sequence <c>\x1b[&lt;35;20;5m</c> might arrive as
/// <c>\x1b</c>, then <c>[&lt;35</c>, then <c>;20;5m</c>; the buffer accumulates these until a complete
/// sequence is detected.
///
/// Based on code from OpenTUI (https://github.com/anomalyco/opentui), MIT License,
/// Copyright (c) 2025 opentui.
///
/// Deviation from TS: <c>EventEmitter</c> becomes plain .NET events, and the <c>setTimeout</c>
/// flush timers become <see cref="Timer"/> instances.
/// </summary>
public sealed partial class StdinBuffer : IDisposable
{
    internal const int DefaultSequenceTimeoutMs = 50;
    internal const int DefaultEscapeTimeoutMs = 10;

    private const string Esc = "\x1b";
    private const string BracketedPasteStart = "\x1b[200~";
    private const string BracketedPasteEnd = "\x1b[201~";

    private readonly object _gate = new();
    private readonly int _timeoutMs;
    private readonly int _escapeTimeoutMs;

    private string _buffer = "";
    private Timer? _timeout;
    private bool _pasteMode;
    private string _pasteBuffer = "";
    private int? _pendingKittyPrintableCodepoint;

    public StdinBuffer(StdinBufferOptions? options = null)
    {
        _timeoutMs = options?.Timeout ?? DefaultSequenceTimeoutMs;
        _escapeTimeoutMs = options?.EscapeTimeout ?? DefaultEscapeTimeoutMs;
    }

    /// <summary>Raised for every complete input sequence.</summary>
    public event Action<string>? Data;

    /// <summary>Raised when a bracketed paste completes.</summary>
    public event Action<string>? Paste;

    [GeneratedRegex(@"^<[0-9]+;[0-9]+;[0-9]+[Mm]$")]
    private static partial Regex SgrMousePattern();

    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"^\x1b\[([0-9]+)(?::[0-9]*)?(?::[0-9]+)?u$")]
    private static partial Regex KittyPrintablePattern();

    /// <summary>Check if a string is a complete escape sequence or needs more data.</summary>
    private static SequenceStatus IsCompleteSequence(string data)
    {
        if (!data.StartsWith(Esc, StringComparison.Ordinal))
        {
            return SequenceStatus.NotEscape;
        }

        if (data.Length == 1)
        {
            return SequenceStatus.Incomplete;
        }

        var afterEsc = data.Substring(1);

        // CSI sequences: ESC [
        if (afterEsc.StartsWith('['))
        {
            // Check for old-style mouse sequence: ESC[M + 3 bytes
            if (afterEsc.StartsWith("[M", StringComparison.Ordinal))
            {
                // Old-style mouse needs ESC[M + 3 bytes = 6 total
                return data.Length >= 6 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
            }
            return IsCompleteCsiSequence(data);
        }

        // OSC sequences: ESC ]
        if (afterEsc.StartsWith(']'))
        {
            return IsCompleteOscSequence(data);
        }

        // DCS sequences: ESC P ... ESC \ (includes XTVersion responses)
        if (afterEsc.StartsWith('P'))
        {
            return IsCompleteDcsSequence(data);
        }

        // APC sequences: ESC _ ... ESC \ (includes Kitty graphics responses)
        if (afterEsc.StartsWith('_'))
        {
            return IsCompleteApcSequence(data);
        }

        // SS3 sequences: ESC O
        if (afterEsc.StartsWith('O'))
        {
            // ESC O followed by a single character
            return afterEsc.Length >= 2 ? SequenceStatus.Complete : SequenceStatus.Incomplete;
        }

        // Meta key sequences: ESC followed by a single character
        if (afterEsc.Length == 1)
        {
            return SequenceStatus.Complete;
        }

        // Unknown escape sequence - treat as complete
        return SequenceStatus.Complete;
    }

    /// <summary>
    /// Check if a CSI sequence is complete: ESC [ ... followed by a final byte (0x40-0x7E).
    /// </summary>
    private static SequenceStatus IsCompleteCsiSequence(string data)
    {
        if (!data.StartsWith($"{Esc}[", StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        // Need at least ESC [ and one more character
        if (data.Length < 3)
        {
            return SequenceStatus.Incomplete;
        }

        var payload = data.Substring(2);

        // CSI sequences end with a byte in the range 0x40-0x7E (@-~)
        var lastChar = payload[^1];
        var lastCharCode = (int)lastChar;

        if (lastCharCode is >= 0x40 and <= 0x7e)
        {
            // Special handling for SGR mouse sequences: ESC[<B;X;Ym or ESC[<B;X;YM
            if (payload.StartsWith('<'))
            {
                if (SgrMousePattern().IsMatch(payload))
                {
                    return SequenceStatus.Complete;
                }

                // If it ends with M or m but doesn't match the pattern, still incomplete
                if (lastChar is 'M' or 'm')
                {
                    var parts = payload.Substring(1, payload.Length - 2).Split(';');
                    if (parts.Length == 3 && parts.All(part => DigitsPattern().IsMatch(part)))
                    {
                        return SequenceStatus.Complete;
                    }
                }

                return SequenceStatus.Incomplete;
            }

            return SequenceStatus.Complete;
        }

        return SequenceStatus.Incomplete;
    }

    /// <summary>Check if an OSC sequence is complete: ESC ] ... ST (ESC \ or BEL).</summary>
    private static SequenceStatus IsCompleteOscSequence(string data)
    {
        if (!data.StartsWith($"{Esc}]", StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        return data.EndsWith($"{Esc}\\", StringComparison.Ordinal) || data.EndsWith('\x07')
            ? SequenceStatus.Complete
            : SequenceStatus.Incomplete;
    }

    /// <summary>
    /// Check if a DCS (Device Control String) sequence is complete: ESC P ... ST (ESC \). Used for
    /// XTVersion responses like <c>ESC P &gt;| ... ESC \</c>.
    /// </summary>
    private static SequenceStatus IsCompleteDcsSequence(string data)
    {
        if (!data.StartsWith($"{Esc}P", StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        return data.EndsWith($"{Esc}\\", StringComparison.Ordinal) ? SequenceStatus.Complete : SequenceStatus.Incomplete;
    }

    /// <summary>
    /// Check if an APC (Application Program Command) sequence is complete: ESC _ ... ST (ESC \). Used
    /// for Kitty graphics responses like <c>ESC _ G ... ESC \</c>.
    /// </summary>
    private static SequenceStatus IsCompleteApcSequence(string data)
    {
        if (!data.StartsWith($"{Esc}_", StringComparison.Ordinal))
        {
            return SequenceStatus.Complete;
        }

        return data.EndsWith($"{Esc}\\", StringComparison.Ordinal) ? SequenceStatus.Complete : SequenceStatus.Incomplete;
    }

    private static int? ParseUnmodifiedKittyPrintableCodepoint(string sequence)
    {
        var match = KittyPrintablePattern().Match(sequence);
        if (!match.Success)
        {
            return null;
        }

        var codepoint = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return codepoint >= 32 ? codepoint : null;
    }

    /// <summary>Split an accumulated buffer into complete sequences.</summary>
    private static (List<string> Sequences, string Remainder) ExtractCompleteSequences(string buffer)
    {
        var sequences = new List<string>();
        var pos = 0;

        while (pos < buffer.Length)
        {
            var remaining = buffer.Substring(pos);

            // Try to extract a sequence starting at this position
            if (remaining.StartsWith(Esc, StringComparison.Ordinal))
            {
                // Find the end of this escape sequence
                var seqEnd = 1;
                while (seqEnd <= remaining.Length)
                {
                    var candidate = remaining.Substring(0, seqEnd);
                    var status = IsCompleteSequence(candidate);

                    if (status == SequenceStatus.Complete)
                    {
                        // WezTerm with enable_kitty_keyboard sends the Escape key press as a raw
                        // '\x1b' byte and the release as a full Kitty CSI-u sequence. These arrive
                        // concatenated as '\x1b\x1b[27;...u'. The buffer would normally treat
                        // '\x1b\x1b' as a complete meta-key sequence (ESC + single char), leaving
                        // '[27;...u' to be typed as plain text. If the character immediately
                        // following '\x1b\x1b' would begin a new escape sequence, emit only the
                        // first ESC and restart from the second.
                        if (candidate == "\x1b\x1b")
                        {
                            var nextChar = seqEnd < remaining.Length ? remaining[seqEnd] : '\0';
                            if (nextChar is '[' or ']' or 'O' or 'P' or '_')
                            {
                                sequences.Add(Esc);
                                pos += 1;
                                break;
                            }
                        }
                        sequences.Add(candidate);
                        pos += seqEnd;
                        break;
                    }
                    else if (status == SequenceStatus.Incomplete)
                    {
                        seqEnd++;
                    }
                    else
                    {
                        // Should not happen when starting with ESC
                        sequences.Add(candidate);
                        pos += seqEnd;
                        break;
                    }
                }

                if (seqEnd > remaining.Length)
                {
                    return (sequences, remaining);
                }
            }
            else
            {
                // Not an escape sequence - take a single character
                sequences.Add(remaining[0].ToString());
                pos++;
            }
        }

        return (sequences, "");
    }

    /// <summary>Feed input data into the buffer.</summary>
    public void Process(string data)
    {
        lock (_gate)
        {
            ProcessCore(data);
        }
    }

    /// <summary>
    /// Feed raw input bytes into the buffer. A single byte above 127 is interpreted as
    /// <c>ESC</c> followed by <c>byte - 128</c>; anything else is decoded as UTF-8.
    /// </summary>
    public void Process(byte[] data)
    {
        string str;
        if (data.Length == 1 && data[0] > 127)
        {
            str = $"{Esc}{(char)(data[0] - 128)}";
        }
        else
        {
            str = Encoding.UTF8.GetString(data);
        }
        Process(str);
    }

    private void ProcessCore(string str)
    {
        // Clear any pending timeout
        ClearTimeout();

        if (str.Length == 0 && _buffer.Length == 0)
        {
            EmitDataSequence("");
            return;
        }

        _buffer += str;

        if (_pasteMode)
        {
            _pasteBuffer += _buffer;
            _buffer = "";

            var endIndex = _pasteBuffer.IndexOf(BracketedPasteEnd, StringComparison.Ordinal);
            if (endIndex != -1)
            {
                var pastedContent = _pasteBuffer.Substring(0, endIndex);
                var remaining = _pasteBuffer.Substring(endIndex + BracketedPasteEnd.Length);

                _pasteMode = false;
                _pasteBuffer = "";
                _pendingKittyPrintableCodepoint = null;

                Paste?.Invoke(pastedContent);

                if (remaining.Length > 0)
                {
                    ProcessCore(remaining);
                }
            }
            return;
        }

        var startIndex = _buffer.IndexOf(BracketedPasteStart, StringComparison.Ordinal);
        if (startIndex != -1)
        {
            if (startIndex > 0)
            {
                var beforePaste = _buffer.Substring(0, startIndex);
                var before = ExtractCompleteSequences(beforePaste);
                foreach (var sequence in before.Sequences)
                {
                    EmitDataSequence(sequence);
                }
            }

            _pendingKittyPrintableCodepoint = null;
            _buffer = _buffer.Substring(startIndex + BracketedPasteStart.Length);
            _pasteMode = true;
            _pasteBuffer = _buffer;
            _buffer = "";

            var endIndex = _pasteBuffer.IndexOf(BracketedPasteEnd, StringComparison.Ordinal);
            if (endIndex != -1)
            {
                var pastedContent = _pasteBuffer.Substring(0, endIndex);
                var remaining = _pasteBuffer.Substring(endIndex + BracketedPasteEnd.Length);

                _pasteMode = false;
                _pasteBuffer = "";
                _pendingKittyPrintableCodepoint = null;

                Paste?.Invoke(pastedContent);

                if (remaining.Length > 0)
                {
                    ProcessCore(remaining);
                }
            }
            return;
        }

        var result = ExtractCompleteSequences(_buffer);
        _buffer = result.Remainder;

        foreach (var sequence in result.Sequences)
        {
            EmitDataSequence(sequence);
        }

        if (_buffer.Length > 0)
        {
            var timeoutMs = _buffer == Esc ? _escapeTimeoutMs : _timeoutMs;
            _timeout = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        foreach (var sequence in Flush())
                        {
                            EmitDataSequence(sequence);
                        }
                    }
                },
                null,
                timeoutMs,
                Timeout.Infinite);
        }
    }

    private void EmitDataSequence(string sequence)
    {
        int? rawCodepoint = sequence.Length == 1 ? (int)sequence[0] : null;
        if (rawCodepoint is not null && rawCodepoint == _pendingKittyPrintableCodepoint)
        {
            _pendingKittyPrintableCodepoint = null;
            return;
        }

        _pendingKittyPrintableCodepoint = ParseUnmodifiedKittyPrintableCodepoint(sequence);
        Data?.Invoke(sequence);
    }

    /// <summary>Flush the pending buffer immediately, returning whatever was held back.</summary>
    public string[] Flush()
    {
        lock (_gate)
        {
            ClearTimeout();

            if (_buffer.Length == 0)
            {
                return Array.Empty<string>();
            }

            var sequences = new[] { _buffer };
            _buffer = "";
            _pendingKittyPrintableCodepoint = null;
            return sequences;
        }
    }

    /// <summary>Drop all buffered state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            ClearTimeout();
            _buffer = "";
            _pasteMode = false;
            _pasteBuffer = "";
            _pendingKittyPrintableCodepoint = null;
        }
    }

    /// <summary>The currently buffered (incomplete) input.</summary>
    public string GetBuffer()
    {
        lock (_gate)
        {
            return _buffer;
        }
    }

    /// <summary>Whether a bracketed paste is currently being accumulated.</summary>
    public bool InPasteMode
    {
        get
        {
            lock (_gate)
            {
                return _pasteMode;
            }
        }
    }

    /// <summary>Release resources; equivalent to <see cref="Clear"/> plus timer disposal.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            ClearTimeout();
            _buffer = "";
            _pasteMode = false;
            _pasteBuffer = "";
            _pendingKittyPrintableCodepoint = null;
        }
    }

    private void ClearTimeout()
    {
        if (_timeout is null)
        {
            return;
        }
        _timeout.Dispose();
        _timeout = null;
    }
}
