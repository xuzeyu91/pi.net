namespace Pi.Tui;

/// <summary>Options for <see cref="KillRing.Push"/>.</summary>
public sealed class KillRingPushOptions
{
    /// <summary>If accumulating, prepend (backward deletion) or append (forward deletion).</summary>
    public bool Prepend { get; set; }

    /// <summary>Merge with the most recent entry instead of creating a new one.</summary>
    public bool Accumulate { get; set; }
}

/// <summary>Ring buffer for Emacs-style kill/yank operations (port of <c>kill-ring.ts</c>).</summary>
public sealed class KillRing
{
    private readonly List<string> _ring = new();

    /// <summary>Add text to the kill ring.</summary>
    public void Push(string text, KillRingPushOptions options)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (options.Accumulate && _ring.Count > 0)
        {
            var last = _ring[^1];
            _ring.RemoveAt(_ring.Count - 1);
            _ring.Add(options.Prepend ? text + last : last + text);
        }
        else
        {
            _ring.Add(text);
        }
    }

    /// <summary>Get the most recent entry without modifying the ring.</summary>
    public string? Peek() => _ring.Count > 0 ? _ring[^1] : null;

    /// <summary>Move the last entry to the front (for yank-pop cycling).</summary>
    public void Rotate()
    {
        if (_ring.Count > 1)
        {
            var last = _ring[^1];
            _ring.RemoveAt(_ring.Count - 1);
            _ring.Insert(0, last);
        }
    }

    public int Length => _ring.Count;
}
