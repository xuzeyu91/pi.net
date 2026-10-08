using System.Runtime.InteropServices;

namespace Pi.Tui;

/// <summary>Lines moved per mouse-wheel event, or <see cref="Auto"/> to accelerate fast wheel spins.</summary>
public readonly record struct WheelScrollLines(double Lines)
{
    /// <summary>Accelerate fast wheel spins (the TS <c>"auto"</c> value).</summary>
    public static WheelScrollLines Auto => new(double.NaN);

    public bool IsAuto => double.IsNaN(Lines);

    public static implicit operator WheelScrollLines(double lines) => new(lines);

    public override string ToString() => IsAuto ? "auto" : Lines.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Converts wheel events into line counts (port of <c>wheel-scroll.ts</c>).
///
/// In <see cref="WheelScrollLines.Auto"/> mode on terminals that do not accelerate wheel input, the
/// count follows event velocity: an isolated notch moves one line, while a fast spin moves up to six
/// lines per event. For example, notches 100 ms apart move 1 line each, 50 ms apart move 2, and 20 ms
/// apart move 5.
/// </summary>
public sealed class WheelScrollAccelerator
{
    // Several events closer than this belong to one physical notch (Ghostty emits them ~4 ms apart)
    // or come from a high-resolution source. They move one line each and do not accelerate.
    private const double BurstGapMs = 5;

    // A pause longer than this ends a scroll gesture.
    private const double GestureGapMs = 200;

    // Average event gap that maps to one line per event. Faster events scale up proportionally.
    private const double ReferenceGapMs = 100;

    private const int MaxAutoLines = 6;

    private WheelScrollLines _lines;
    private readonly bool _accelerate;
    private double _lastTime = double.NegativeInfinity;
    private int _lastDirection;
    private double? _averageGap;
    private double _carry;

    public WheelScrollAccelerator(WheelScrollLines? lines = null, bool? accelerate = null)
    {
        _lines = lines ?? WheelScrollLines.Auto;
        _accelerate = accelerate ?? !TerminalAcceleratesWheel();
    }

    /// <summary>
    /// Local macOS terminals receive wheel and trackpad deltas that the OS has already accelerated,
    /// and they emit one event per line. Other platforms, and SSH sessions where the client platform
    /// is unknown, usually send one event per wheel notch.
    /// </summary>
    internal static bool TerminalAcceleratesWheel() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
        Environment.GetEnvironmentVariable("SSH_CONNECTION") is null &&
        Environment.GetEnvironmentVariable("SSH_CLIENT") is null &&
        Environment.GetEnvironmentVariable("SSH_TTY") is null;

    public WheelScrollLines Lines
    {
        get => _lines;
        set
        {
            _lines = value;
            Reset();
        }
    }

    /// <summary>Return the positive line count for a wheel event in <paramref name="direction"/> at time <paramref name="now"/> (milliseconds).</summary>
    public int Next(int direction, double now)
    {
        if (!_lines.IsAuto)
        {
            return double.IsFinite(_lines.Lines) ? Math.Max(1, (int)Math.Floor(_lines.Lines)) : 1;
        }
        if (!_accelerate)
        {
            return 1;
        }

        var gap = now - _lastTime;
        var sameGesture = direction == _lastDirection && gap <= GestureGapMs;
        _lastTime = now;
        _lastDirection = direction;
        if (!sameGesture)
        {
            _averageGap = null;
            _carry = 0;
            return 1;
        }
        if (gap < BurstGapMs)
        {
            return 1;
        }

        _averageGap = _averageGap is null ? gap : (_averageGap.Value + gap) / 2;
        var lines = Math.Min(MaxAutoLines, Math.Max(1, ReferenceGapMs / _averageGap.Value)) + _carry;
        var whole = Math.Floor(lines);
        _carry = lines - whole;
        return (int)whole;
    }

    private void Reset()
    {
        _lastTime = double.NegativeInfinity;
        _lastDirection = 0;
        _averageGap = null;
        _carry = 0;
    }
}
