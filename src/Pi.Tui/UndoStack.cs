namespace Pi.Tui;

/// <summary>
/// Generic undo stack with clone-on-push semantics (port of <c>undo-stack.ts</c>).
///
/// TS uses <c>structuredClone</c>; .NET has no universal deep-clone, so the caller supplies a clone
/// function. Pass <c>x =&gt; x</c> for immutable states.
/// </summary>
public sealed class UndoStack<T>
{
    private readonly List<T> _stack = new();
    private readonly Func<T, T> _clone;

    public UndoStack(Func<T, T> clone) => _clone = clone;

    /// <summary>Push a deep clone of the given state onto the stack.</summary>
    public void Push(T state) => _stack.Add(_clone(state));

    /// <summary>Pop and return the most recent snapshot, or default if empty.</summary>
    public T? Pop()
    {
        if (_stack.Count == 0)
        {
            return default;
        }
        var value = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return value;
    }

    /// <summary>Remove all snapshots.</summary>
    public void Clear() => _stack.Clear();

    public int Length => _stack.Count;
}
