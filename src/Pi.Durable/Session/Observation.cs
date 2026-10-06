using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Types;

namespace Pi.Durable.Session;

/// <summary>
/// Session 到 Chord 的桥，与一个附加态（文档或对话视图）一对一拥有。null 值使其退休。
/// 对应 TS <c>CommittedStateSource&lt;T&gt;</c>（实现 chord 的 <c>ReplicatedStateSource</c>）。
/// </summary>
public sealed class CommittedStateSource<T> : IReplicatedStateSource<T>
{
    /// <summary>一个不可用 watch 监听器之后保留的精确已提交帧上限。</summary>
    internal const int MaxPendingWatchFrames = 100;

    /// <summary>退役文档化身的规范终局更新。对应 TS <c>RETIREMENT_OPERATIONS = [["r", null]]</c>。</summary>
    public static readonly IReadOnlyList<DeltaOp> RetirementOperations = [new DeltaOp.Replace(null)];

    private readonly HashSet<SessionSourceAttachment<T>> _attachments = [];
    private Action? _release;

    /// <summary>dispose 时释放。</summary>
    private T? _value;
    private long _cursor;
    private bool _retired;
    private bool _closed;

    public CommittedStateSource(T value, Action release)
    {
        _value = value;
        _release = release;
    }

    /// <summary>当前快照（值 + 游标）。对应 TS <c>attach()</c> 前的源状态。</summary>
    public ReplicatedStateSourceSnapshot<T> Snapshot
    {
        get
        {
            if (_closed) throw new InvalidOperationException("State source is closed");
            return new ReplicatedStateSourceSnapshot<T>(_value!, _cursor);
        }
    }

    public IReplicatedStateSourceAttachment<T> Attach()
    {
        if (_closed) throw new InvalidOperationException("State source is closed");
        SessionSourceAttachment<T>? attachment = null;
        attachment = new SessionSourceAttachment<T>(new ReplicatedStateSourceSnapshot<T>(_value!, _cursor), () =>
        {
            _attachments.Remove(attachment!);
            if (_attachments.Count == 0) FinishDisposal();
        });
        _attachments.Add(attachment);
        return attachment;
    }

    /// <summary>推进源状态并向全部附着发布帧。对应 TS <c>advance</c>。</summary>
    public void Advance(T value, IReadOnlyList<DeltaOp> ops, Context context)
    {
        if (_closed || _retired) return;
        _value = value;
        _cursor += 1;
        if (value is null) _retired = true;
        var frame = new ReplicatedStateSourceFrame<T>(value, ops, _cursor, context);
        foreach (var attachment in _attachments.ToArray()) attachment.Publish(frame);
    }

    /// <summary>关闭会话时处置全部附着。对应 TS <c>closeSession</c>。</summary>
    public void CloseSession()
    {
        if (_closed) return;
        foreach (var attachment in _attachments.ToArray()) attachment.Dispose();
        FinishDisposal();
    }

    private void FinishDisposal()
    {
        if (_closed) return;
        _closed = true;
        _value = default;
        var release = _release;
        _release = null;
        release?.Invoke();
    }
}

/// <summary>
/// 单个授权源附着的句柄：快照 + 帧队列 + 异步投递。
/// 对应 TS <c>SessionSourceAttachment</c>。
/// </summary>
public sealed class SessionSourceAttachment<T> : IReplicatedStateSourceAttachment<T>
{
    private readonly ReplicatedStateSourceSnapshot<T> _snapshot;
    private Action? _release;
    private readonly List<ReplicatedStateSourceFrame<T>> _frames = [];
    private Action<ReplicatedStateSourceFrame<T>>? _listener;
    private bool _activated;
    private bool _disposed;
    private bool _scheduled;
    private bool _delivering;

    public SessionSourceAttachment(ReplicatedStateSourceSnapshot<T> snapshot, Action release)
    {
        _snapshot = snapshot;
        _release = release;
    }

    /// <summary>附着时的快照（值 + 游标）。对应 TS <c>snapshot</c>。</summary>
    public ReplicatedStateSourceSnapshot<T> Snapshot => _snapshot;

    /// <summary>开始接收帧（回调到 receive）。对应 TS <c>activate(listener)</c>。</summary>
    public void Activate(Action<ReplicatedStateSourceFrame<T>> receive)
    {
        if (_activated) throw new InvalidOperationException("State attachment is already active");
        if (_disposed) throw new InvalidOperationException("State attachment is disposed");
        _activated = true;
        _listener = receive;
        Drain();
    }

    /// <summary>入队一帧并调度异步投递。对应 TS <c>publish</c>（queueMicrotask）。</summary>
    public void Publish(ReplicatedStateSourceFrame<T> frame)
    {
        if (_disposed) return;
        _frames.Add(frame);
        if (!_activated || _delivering || _scheduled) return;
        _scheduled = true;
        // 设计差异：TS 用 queueMicrotask 保证「当前同步栈结束之后」投递；C# 用线程池任务近似
        // （投递仍严格晚于当前同步段、帧序不变），时序粒度不同但观察语义一致。
        _ = Task.Run(() =>
        {
            _scheduled = false;
            if (_disposed) return;
            try
            {
                Drain();
            }
            catch
            {
                // Chord 的帧监听器包含源契约失败；把监听器的意外失败同样隔离在本附着内。
                Dispose();
            }
        });
    }

    /// <summary>处置附着并释放。对应 TS <c>dispose</c>。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Clear();
        _listener = null;
        var release = _release;
        _release = null;
        release?.Invoke();
    }

    private void Drain()
    {
        var listener = _listener;
        if (listener is null || _delivering || _disposed) return;
        _delivering = true;
        try
        {
            while (!_disposed)
            {
                if (_frames.Count == 0) break;
                var frame = _frames[0];
                _frames.RemoveAt(0);
                listener(frame);
            }
        }
        finally
        {
            _delivering = false;
        }
    }
}

/// <summary>watch 的一帧（值 + ops + 上下文）。对应 TS <c>WatchFrame&lt;T&gt;</c>。</summary>
internal sealed record WatchFrame<T>(T Value, IReadOnlyList<DeltaOp> Ops, Context Context);

/// <summary>
/// 绑定到一个文档化身或对话视图的串行化精确帧 watch。null 值使其退休。
/// 对应 TS <c>CommittedWatch&lt;T&gt;</c>（实现 <c>WatchHandle&lt;T&gt;</c>）。
/// </summary>
public sealed class CommittedWatch<T> : IWatchHandle<T>
{
    private readonly Action _detach;
    private readonly Func<T>? _replace;
    private readonly TaskCompletionSource<WatchEnd> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WatchFrame<T>> _pending = [];
    private T _value;
    private Func<T, IReadOnlyList<DeltaOp>, Context, Task>? _listener;
    private bool _started;
    private bool _scheduled;
    private bool _running;
    private bool _detached;
    private bool _retired;
    private WatchEnd? _end;
    private bool _resolved;
    private CancellationTokenRegistration _cancellationRegistration;

    /// <summary><paramref name="replace"/> 给出溢出时投递的值；缺省取最新值。</summary>
    public CommittedWatch(T value, Action detach, Func<T>? replace = null)
    {
        _value = value;
        _detach = detach;
        _replace = replace;
    }

    /// <summary>获取时的修订；最新交付的不可变修订。</summary>
    public T Value => _value;

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    public Task<WatchEnd> Closed => _closed.Task;

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    public void Start(Func<T, IReadOnlyList<DeltaOp>, Context, Task> listener)
    {
        if (_started) throw new InvalidOperationException("Watch is already started");
        if (_end is not null) throw new InvalidOperationException("Watch is stopped");
        _started = true;
        _listener = listener;
        if (_pending.Count > 0) Schedule();
    }

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    public Task<WatchEnd> Stop()
    {
        Terminate(new WatchEnd.Stopped());
        return _closed.Task;
    }

    /// <summary>安装取消信号观察。对应 TS <c>observeCancellation(signal)</c>。</summary>
    public void ObserveCancellation(CancellationToken signal)
    {
        if (_end is not null) return;
        _cancellationRegistration = signal.Register(Cancel);
        if (signal.IsCancellationRequested) Cancel();
    }

    /// <summary>以 cancelled 终态终止。对应 TS <c>cancel()</c>。</summary>
    public void Cancel() => Terminate(new WatchEnd.Cancelled());

    /// <summary>以 session_closed 终态终止。对应 TS <c>closeSession()</c>。</summary>
    public void CloseSession() => Terminate(new WatchEnd.SessionClosed());

    /// <summary>推进 watch：入队一帧（超限则整批折叠）。对应 TS <c>advance</c>。</summary>
    public void Advance(T value, IReadOnlyList<DeltaOp> ops, Context context)
    {
        if (_end is not null || _retired) return;
        if (value is null) _retired = true;
        if (_pending.Count >= CommittedStateSource<object?>.MaxPendingWatchFrames)
        {
            _pending.Clear();
            var replacement = _replace is not null ? _replace() : value;
            _pending.Add(new WatchFrame<T>(replacement, [new DeltaOp.Replace(replacement)], context));
        }
        else
        {
            _pending.Add(new WatchFrame<T>(value, ops, context));
        }
        if (_started) Schedule();
    }

    private void Schedule()
    {
        if (_scheduled || _running || _end is not null) return;
        _scheduled = true;
        // 设计差异：TS 用 queueMicrotask；C# 用线程池任务近似（见 SessionSourceAttachment.Publish）。
        _ = Task.Run(async () =>
        {
            _scheduled = false;
            await DrainAsync().ConfigureAwait(false);
        });
    }

    private async Task DrainAsync()
    {
        if (_running || _end is not null || !_started)
        {
            FinishIfReady();
            return;
        }
        _running = true;
        try
        {
            while (_end is null)
            {
                if (_pending.Count == 0) break;
                var frame = _pending[0];
                _pending.RemoveAt(0);
                _value = frame.Value;
                var deliveryContext = ContextSignals.WithoutAbortSignal(frame.Context);
                try
                {
                    await _listener!(frame.Value, frame.Ops, deliveryContext).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (_end is null) Terminate(new WatchEnd.ListenerError(error));
                    break;
                }
                if (frame.Value is null)
                {
                    Terminate(new WatchEnd.Retired());
                    break;
                }
            }
        }
        finally
        {
            _running = false;
            if (_end is null && _pending.Count > 0) Schedule();
            FinishIfReady();
        }
    }

    private void Terminate(WatchEnd end)
    {
        if (_end is not null) return;
        _end = end;
        DetachNow();
        _pending.Clear();
        FinishIfReady();
    }

    private void DetachNow()
    {
        if (_detached) return;
        _detached = true;
        _detach();
    }

    private void FinishIfReady()
    {
        if (_resolved || _end is null) return;
        _resolved = true;
        _cancellationRegistration.Dispose();
        _closed.TrySetResult(_end);
    }
}
