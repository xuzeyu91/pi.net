namespace Pi.Tui.Components;

/// <summary>
/// Loader that can be cancelled with Escape (port of <c>components/cancellable-loader.ts</c>).
/// Extends <see cref="Loader"/> with a cancellation token for cancelling async operations.
/// </summary>
/// <example>
/// <code>
/// var loader = new CancellableLoader(tui, cyan, dim, "Working...");
/// loader.OnAbort = () => done(null);
/// await DoWorkAsync(loader.Signal);
/// </code>
/// </example>
public sealed class CancellableLoader : Loader, IDisposable
{
    private readonly CancellationTokenSource _abortController = new();

    /// <summary>Called when the user presses Escape.</summary>
    public Action? OnAbort { get; set; }

    /// <summary>Cancellation token that is cancelled when the user presses Escape.</summary>
    public CancellationToken Signal => _abortController.Token;

    /// <summary>Whether the loader was aborted.</summary>
    public bool Aborted => _abortController.IsCancellationRequested;

    public CancellableLoader(
        ITui ui,
        Func<string, string> spinnerColorFn,
        Func<string, string> messageColorFn,
        string message = "Loading...",
        LoaderIndicatorOptions? indicator = null)
        : base(ui, spinnerColorFn, messageColorFn, message, indicator)
    {
    }

    public void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();
        if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _abortController.Cancel();
            OnAbort?.Invoke();
        }
    }

    /// <summary>Stops the animation. The token is intentionally left usable afterwards, matching TS <c>dispose()</c>.</summary>
    public void Dispose() => Stop();
}
