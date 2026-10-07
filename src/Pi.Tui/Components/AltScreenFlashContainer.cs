using System.Threading;

namespace Pi.Tui.Components;

/// <summary>Transient message composited by the alternate-screen renderer (port of <c>FlashEntry</c>).</summary>
public sealed class AltScreenFlashEntry
{
    public required int Id { get; init; }

    public required string Message { get; init; }

    public required Timer Timer { get; init; }
}

/// <summary>
/// Transient messages composited by the alternate-screen renderer
/// (port of <c>components/alt-screen-flash.ts</c>).
/// </summary>
/// <remarks>
/// Deviation from TS (T13): <c>setTimeout</c> becomes a <see cref="Timer"/>. Node's <c>unref()</c> has no
/// .NET equivalent, but <see cref="Timer"/> likewise does not keep the process alive. The callback may run
/// on a thread-pool thread while the render thread reads the entries, so mutations are guarded by a lock.
/// </remarks>
public sealed class AltScreenFlashContainer : IComponent
{
    private const int DefaultDurationMs = 1000;

    private readonly object _gate = new();
    private readonly List<AltScreenFlashEntry> _entries = [];
    private readonly Action _requestRender;
    private int _nextId;

    public AltScreenFlashContainer(Action requestRender) => _requestRender = requestRender;

    public void Flash(string message, int durationMs = DefaultDurationMs)
    {
        int id;
        AltScreenFlashEntry entry;
        lock (_gate)
        {
            id = _nextId++;
            var timer = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        var index = _entries.FindIndex(e => e.Id == id);
                        if (index == -1)
                        {
                            return;
                        }
                        _entries.RemoveAt(index);
                    }
                    _requestRender();
                },
                null,
                Math.Max(0, durationMs),
                Timeout.Infinite);
            entry = new AltScreenFlashEntry { Id = id, Message = message, Timer = timer };
            _entries.Add(entry);
        }
        _requestRender();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                entry.Timer.Dispose();
            }
            _entries.Clear();
        }
    }

    public void Invalidate()
    {
    }

    public string[] Render(int width)
    {
        AltScreenFlashEntry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        var result = new string[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var message = TextLayout.TruncateToWidth($" {snapshot[i].Message} ", width, "");
            result[i] = $"\x1b[7m{message}\x1b[27m";
        }
        return result;
    }
}
