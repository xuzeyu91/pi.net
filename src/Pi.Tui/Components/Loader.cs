namespace Pi.Tui.Components;

/// <summary>Options for the <see cref="Loader"/> spinner indicator (port of <c>LoaderIndicatorOptions</c>).</summary>
public sealed class LoaderIndicatorOptions
{
    /// <summary>Animation frames. Use an empty array to hide the indicator.</summary>
    public string[]? Frames { get; init; }

    /// <summary>Frame interval in milliseconds for animated indicators.</summary>
    public int? IntervalMs { get; init; }
}

/// <summary>
/// Loader component that updates with an optional spinning animation (port of <c>components/loader.ts</c>).
/// </summary>
/// <remarks>
/// Deviation from TS (T13): the <c>setInterval</c> animation timer becomes a
/// <see cref="System.Threading.Timer"/>, so frame advancement and <c>setText</c> can run on a thread-pool
/// thread while the render timer reads the same state on another. The tick body is guarded by a lock, and
/// <see cref="ITui.RequestRender"/> is itself thread-safe.
/// </remarks>
public class Loader : Text
{
    private static readonly string[] DefaultFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private const int DefaultIntervalMs = 80;

    private readonly object _gate = new();
    private readonly ITui? _ui;
    private readonly Func<string, string> _spinnerColorFn;
    private readonly Func<string, string> _messageColorFn;

    private string[] _frames = [.. DefaultFrames];
    private int _intervalMs = DefaultIntervalMs;
    private int _currentFrame;
    private Timer? _intervalId;
    private bool _renderIndicatorVerbatim;
    private string _message;

    public Loader(
        ITui ui,
        Func<string, string> spinnerColorFn,
        Func<string, string> messageColorFn,
        string message = "Loading...",
        LoaderIndicatorOptions? indicator = null)
        : base("", 1, 0)
    {
        _ui = ui;
        _spinnerColorFn = spinnerColorFn;
        _messageColorFn = messageColorFn;
        _message = message;
        SetIndicator(indicator);
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        var result = new string[lines.Length + 1];
        result[0] = "";
        Array.Copy(lines, 0, result, 1, lines.Length);
        return result;
    }

    public void Start()
    {
        UpdateDisplay();
        RestartAnimation();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _intervalId?.Dispose();
            _intervalId = null;
        }
    }

    public void SetMessage(string message)
    {
        _message = message;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void SetIndicator(LoaderIndicatorOptions? indicator = null)
    {
        _renderIndicatorVerbatim = indicator is not null;
        _frames = indicator?.Frames is { } frames ? [.. frames] : [.. DefaultFrames];
        _intervalMs = indicator?.IntervalMs is > 0 ? indicator.IntervalMs.Value : DefaultIntervalMs;
        _currentFrame = 0;
        Start();
    }

    private void RestartAnimation()
    {
        Stop();
        if (_frames.Length <= 1)
        {
            return;
        }

        lock (_gate)
        {
            _intervalId = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        _currentFrame = (_currentFrame + 1) % _frames.Length;
                    }
                    UpdateDisplay();
                },
                null,
                _intervalMs,
                _intervalMs);
        }
    }

    protected string GetRenderedIndicator()
    {
        var frame = _currentFrame < _frames.Length ? _frames[_currentFrame] : "";
        return _renderIndicatorVerbatim ? frame : _spinnerColorFn(frame);
    }

    private void UpdateDisplay()
    {
        var renderedFrame = GetRenderedIndicator();
        var indicator = renderedFrame.Length > 0 ? $"{renderedFrame} " : "";
        SetText($"{indicator}{_messageColorFn(_message)}");
        _ui?.RequestRender();
    }
}
