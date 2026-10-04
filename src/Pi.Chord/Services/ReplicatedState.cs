using Pi.Chord.Context;
using Pi.Chord.Delta;

namespace Pi.Chord.Services;

/// <summary>可复制状态公共契约（只读视图 + 订阅）。对应 TS <c>ReplicatedState</c>。</summary>
public interface IReplicatedState<T>
{
    /// <summary>当前值（消费者副本在水合前为 default）。</summary>
    T? Value { get; }

    /// <summary>订阅状态变更；立即水合当前值。返回退订 IDisposable。</summary>
    IDisposable Subscribe(StateListener<T> listener);
}

/// <summary>可变复制状态：本地权威，变更即发布。对应 TS <c>MutableReplicatedState</c>。</summary>
public interface IMutableReplicatedState<T> : IReplicatedState<T> where T : class
{
    /// <summary>事务性变更：mutate 在草稿上记录动词，成功后物化并发布。</summary>
    void Change(Context.Context context, Action<Tracker<T>.Change> mutate);

    /// <summary>整值替换。</summary>
    void Replace(Context.Context context, T value);
}

/// <summary>
/// 本地权威可变复制状态。对应 TS <c>MutableReplicatedStateImpl</c>：
/// Tracker 事务 + Publisher 发布；变更回调内禁止重入。
/// </summary>
public sealed class MutableReplicatedState<T> : IMutableReplicatedState<T>, IReplicatedStateInternals
    where T : class
{
    private readonly Tracker<T> _tracker;
    private readonly ReplicatedStatePublisher<T> _publisher;
    private bool _changing;

    public MutableReplicatedState(T initial)
    {
        _tracker = new Tracker<T>(initial);
        _publisher = new ReplicatedStatePublisher<T>(_tracker.Value);
    }

    public T Value => _tracker.Value;

    (object? Value, long Sequence) IReplicatedStateInternals.Snapshot()
    {
        var (value, sequence) = _publisher.Snapshot();
        return ((object?)value, sequence);
    }

    IDisposable IReplicatedStateInternals.SubscribeSource(SourceListener listener)
        => _publisher.SubscribeSource(listener);

    /// <summary>获取内部契约（供 provider 层经状态对象读取发布流）。对应 TS <c>getReplicatedStateInternals</c>。</summary>
    public static IReplicatedStateInternals? GetInternals(object? value)
        => value is IReplicatedStateInternals internals ? internals : null;

    public void Change(Context.Context context, Action<Tracker<T>.Change> mutate)
    {
        if (_changing)
            throw new InvalidOperationException("Replicated state cannot be changed reentrantly from a change callback");
        _changing = true;
        Tracker<T>.Prepared prepared;
        try
        {
            var change = _tracker.BeginChange();
            try
            {
                // C# 的 mutate 委托是同步的——TS 的 Promise 拒绝由编译期类型保证不发生。
                mutate(change);
                prepared = change.Prepare();
            }
            catch
            {
                change.Abort();
                throw;
            }
        }
        finally
        {
            _changing = false;
        }

        _tracker.Adopt(prepared);
        if (prepared.Ops.Count == 0) return;
        ThrowCollectedErrors(_publisher.Publish((T)prepared.Value!, prepared.Ops, context),
            "Replicated state listeners failed");
    }

    public void Replace(Context.Context context, T value)
    {
        if (_changing)
            throw new InvalidOperationException("Replicated state cannot be replaced from a change callback");
        var prepared = _tracker.PrepareReplace(value);
        _tracker.Adopt(prepared);
        if (prepared.Ops.Count == 0) return;
        ThrowCollectedErrors(_publisher.Publish((T)prepared.Value!, prepared.Ops, context),
            "Replicated state listeners failed");
    }

    public IDisposable Subscribe(StateListener<T> listener) => _publisher.Subscribe(listener);

    private static void ThrowCollectedErrors(IReadOnlyList<Exception> errors, string message)
    {
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException(message, errors);
    }
}

/// <summary>授权源流的单帧（值 + ops + 游标）。对应 TS <c>ReplicatedStateSourceFrame</c>。</summary>
public sealed record ReplicatedStateSourceFrame<T>(T Value, IReadOnlyList<DeltaOp> Ops, long Cursor, Context.Context Context);

/// <summary>授权源快照（起始游标 + 当前值）。</summary>
public sealed record ReplicatedStateSourceSnapshot<T>(T Value, long Cursor);

/// <summary>授权源的附着句柄。对应 TS <c>ReplicatedStateSourceAttachment</c>。</summary>
public interface IReplicatedStateSourceAttachment<T> : IDisposable
{
    /// <summary>开始接收帧（回调到 receive）。</summary>
    void Activate(Action<ReplicatedStateSourceFrame<T>> receive);
}

/// <summary>授权不可变源流。对应 TS <c>ReplicatedStateSource</c>。</summary>
public interface IReplicatedStateSource<T>
{
    ReplicatedStateSourceSnapshot<T> Snapshot { get; }

    IReplicatedStateSourceAttachment<T> Attach();
}

/// <summary>附加态选项。对应 TS <c>ReplicatedStateSourceOptions</c>。</summary>
public sealed record ReplicatedStateSourceOptions
{
    /// <summary>失败上报器（默认脱离当前栈抛出）。</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>
/// 附加到授权源的发布态：游标连续性校验，违约即 dispose。
/// 对应 TS <c>AttachedReplicatedStateImpl</c>。
/// </summary>
public sealed class AttachedReplicatedState<T> : IReplicatedState<T>, IReplicatedStateInternals, IDisposable
{
    private readonly IReplicatedStateSourceAttachment<T> _attachment;
    private readonly ReplicatedStatePublisher<T> _publisher;
    private readonly Action<Exception> _reportError;
    private long _cursor;
    private bool _disposed;

    internal AttachedReplicatedState(
        IReplicatedStateSourceAttachment<T> attachment,
        ReplicatedStateSourceSnapshot<T> snapshot,
        ReplicatedStateSourceOptions options)
    {
        AssertCursor(snapshot.Cursor, "snapshot");
        _attachment = attachment;
        _cursor = snapshot.Cursor;
        _reportError = options.OnError ?? ReplicatedStates.ReportErrorAsync;
        _publisher = new ReplicatedStatePublisher<T>(snapshot.Value, error => Report(error));
    }

    public T? Value => _publisher.Value;

    public IDisposable Subscribe(StateListener<T> listener) => _publisher.Subscribe(listener);

    (object? Value, long Sequence) IReplicatedStateInternals.Snapshot()
    {
        var (value, sequence) = _publisher.Snapshot();
        return (value, sequence);
    }

    IDisposable IReplicatedStateInternals.SubscribeSource(SourceListener listener)
        => _publisher.SubscribeSource(listener);

    /// <summary>开始接收源帧。</summary>
    public void Activate() => _attachment.Activate(Receive);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _attachment.Dispose();
    }

    private void Receive(ReplicatedStateSourceFrame<T> frame)
    {
        if (_disposed) return;
        try
        {
            AssertCursor(frame.Cursor, "frame");
            var expected = _cursor + 1;
            if (frame.Cursor != expected)
            {
                throw new InvalidOperationException(
                    $"Replicated state source cursor has a gap: expected {expected}, received {frame.Cursor}");
            }
            _cursor = frame.Cursor;
            var errors = _publisher.Publish(frame.Value, frame.Ops, frame.Context);
            if (errors.Count == 1) Report(errors[0]);
            else if (errors.Count > 1) Report(new AggregateException("Replicated state listeners failed", errors));
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _attachment.Dispose();
        }
        catch (Exception disposeError)
        {
            Report(new AggregateException("Replicated state source contract failed", error, disposeError));
            return;
        }
        Report(error);
    }

    private void Report(Exception error)
    {
        try
        {
            _reportError(error);
        }
        catch (Exception reportError)
        {
            ReplicatedStates.ReportErrorAsync(reportError);
        }
    }

    private static void AssertCursor(long cursor, string kind)
    {
        if (cursor < long.MinValue || cursor > long.MaxValue || cursor != Math.Truncate((double)cursor))
            throw new ArgumentException($"Replicated state source {kind} cursor must be a safe integer");
    }
}

/// <summary>附加/工厂入口。对应 TS <c>attachReplicatedStateSource</c>。</summary>
public static class ReplicatedStateAttachments
{
    /// <summary>把发布态附加到授权源流。</summary>
    public static AttachedReplicatedState<T> AttachReplicatedStateSource<T>(
        IReplicatedStateSource<T> source, ReplicatedStateSourceOptions? options = null)
    {
        options ??= new ReplicatedStateSourceOptions();
        var attachment = source.Attach();
        try
        {
            var state = new AttachedReplicatedState<T>(attachment, source.Snapshot, options);
            state.Activate();
            return state;
        }
        catch (Exception error)
        {
            try
            {
                attachment.Dispose();
            }
            catch (Exception disposeError)
            {
                throw new AggregateException("Failed to attach replicated state source", error, disposeError);
            }
            throw;
        }
    }
}

/// <summary>
/// 消费者的冷只读副本：水合前无值；水合必须 base 批并过修订验证；
/// 更新必须序列号连续，违约即清空。对应 TS <c>ReplicatedStateReplica</c>。
/// </summary>
public sealed class ReplicatedStateReplica<T> : IReplicatedState<T>
{
    private readonly Dictionary<StateSubscriber<T>, byte> _listeners = [];
    private readonly Action<Exception> _reportError;
    private readonly JsonRevisionValidator _validator = new();
    private T? _value;
    private long? _sequence;

    public ReplicatedStateReplica(Action<Exception>? reportError = null)
        => _reportError = reportError ?? ReplicatedStates.ReportErrorAsync;

    public T? Value => _value;

    public IDisposable Subscribe(StateListener<T> listener)
    {
        var subscriber = new StateSubscriber<T>(listener, _reportError);
        _listeners.Add(subscriber, 0);
        if (_value is not null)
        {
            subscriber.Push(_value, ReplicatedStates.ServiceDeliveryContext(),
                new ReplicatedStateDelivery(ReplicatedStateDeliveryKind.Hydrate, _sequence!.Value));
            subscriber.Drain();
        }
        return new Unsubscription(() =>
        {
            subscriber.Close();
            _listeners.Remove(subscriber);
        });
    }

    /// <summary>水合：ops 必须以 r 开头（base 批），结果过修订验证。对应 TS <c>hydrate</c>。</summary>
    public void Hydrate(long sequence, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        T next;
        try
        {
            if (!DeltaWire.IsBase(ops))
                throw new InvalidOperationException("Replicated state snapshot is not a base operation batch");
            next = (T)_validator.Validate(DeltaApply.Apply(ops, null))!;
        }
        catch (Exception)
        {
            Clear();
            throw;
        }
        _sequence = sequence;
        _value = next;
        DeliverAll(context, new ReplicatedStateDelivery(ReplicatedStateDeliveryKind.Hydrate, sequence));
    }

    /// <summary>增量更新：序列号必须紧接当前。对应 TS <c>update</c>。</summary>
    public void Update(long sequence, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        if (_sequence is null || _value is null)
            throw new InvalidOperationException("Replicated state received an update before hydration");
        if (sequence != _sequence + 1)
        {
            Clear();
            throw new InvalidOperationException("Replicated state update sequence has a gap");
        }
        T next;
        try
        {
            next = (T)_validator.Validate(DeltaApply.Apply(ops, _value))!;
        }
        catch (Exception)
        {
            Clear();
            throw;
        }
        _sequence = sequence;
        _value = next;
        DeliverAll(context, new ReplicatedStateDelivery(ReplicatedStateDeliveryKind.Update, sequence));
    }

    /// <summary>清空（校验失败/序列断档后的防御性复位）。</summary>
    public void Clear()
    {
        _value = default;
        _sequence = null;
        foreach (var subscriber in _listeners.Keys) subscriber.Clear();
    }

    private void DeliverAll(Context.Context context, ReplicatedStateDelivery delivery)
    {
        if (_value is null) return;
        var subscribers = _listeners.Keys.ToArray();
        // 用户代码可重入发布——先为所有人入队，再逐个 drain。
        foreach (var subscriber in subscribers) subscriber.Push(_value, context, delivery);
        foreach (var subscriber in subscribers) subscriber.Drain();
    }

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
