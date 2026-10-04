using Pi.Chord.Context;
using Pi.Chord.Delta;

namespace Pi.Chord.Services;

/// <summary>交付类型：初始水合或增量更新（带序列号）。对应 TS <c>ReplicatedStateDelivery</c>。</summary>
public sealed record ReplicatedStateDelivery(ReplicatedStateDeliveryKind Kind, long Sequence);

public enum ReplicatedStateDeliveryKind { Hydrate, Update }

/// <summary>
/// 状态监听器：返回 null 表示同步完成；返回 Task 则 drain 暂停至其完成。
/// 对应 TS <c>StateListener</c>（Promise 暂停语义的 C# 形式）。
/// </summary>
public delegate Task? StateListener<T>(T value, Context.Context context, ReplicatedStateDelivery delivery);

/// <summary>发布源监听器：每次权威修订的 ops/序列号/上下文。</summary>
public delegate void SourceListener(IReadOnlyList<DeltaOp> ops, long sequence, Context.Context context);

/// <summary>内部契约：原子快照与发布源订阅（provider 层经此读取）。对应 TS <c>ReplicatedStateInternals</c>。</summary>
public interface IReplicatedStateInternals
{
    /// <summary>原子捕获不可变值与其匹配的发布序列号。</summary>
    (object? Value, long Sequence) Snapshot();

    /// <summary>订阅权威发布流。</summary>
    IDisposable SubscribeSource(SourceListener listener);
}

/// <summary>独立订阅者：生产者与其他订阅者的进度互不影响。对应 TS <c>StateSubscriber</c>。</summary>
internal sealed class StateSubscriber<T>
{
    private readonly StateListener<T> _listener;
    private readonly Action<Exception> _reportError;
    private readonly Queue<(T Value, Context.Context Context, ReplicatedStateDelivery Delivery)> _pending = new();
    private bool _running;
    private bool _started;
    private bool _closed;

    public StateSubscriber(StateListener<T> listener, Action<Exception> reportError)
        => (_listener, _reportError) = (listener, reportError);

    /// <summary>入队一帧。pending 达 100 时清空但保留首帧（冷副本在水合前可重入入队）。</summary>
    public void Push(T value, Context.Context context, ReplicatedStateDelivery delivery)
    {
        if (_closed) return;
        if (_pending.Count == 100)
        {
            var hasHydration = !_started && _pending.Count > 0;
            var hydration = hasHydration ? _pending.Peek() : default;
            _pending.Clear();
            if (hasHydration) _pending.Enqueue(hydration);
        }
        _pending.Enqueue((value, context, delivery));
    }

    /// <summary>逐帧派发；异步监听器挂起 drain，完成后由回调恢复。</summary>
    public void Drain()
    {
        if (_running || _closed) return;
        _running = true;
        while (_pending.TryDequeue(out var frame))
        {
            _started = true;
            Task? pending;
            try
            {
                pending = _listener(frame.Value, frame.Context, frame.Delivery);
            }
            catch (Exception error)
            {
                Report(error);
                continue;
            }
            if (pending is not null)
            {
                _ = pending.ContinueWith(
                    completed =>
                    {
                        if (completed.IsFaulted) Report(completed.Exception!.GetBaseException());
                        Resume();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
        }
        _running = false;
    }

    public void Clear() => _pending.Clear();

    public void Close()
    {
        _closed = true;
        Clear();
    }

    private void Resume()
    {
        _running = false;
        Drain();
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
}

/// <summary>一次发布（值 + ops + 序列号 + 上下文）。</summary>
internal sealed record Publication<T>(T Value, IReadOnlyList<DeltaOp> Ops, long Sequence, Context.Context Context);

/// <summary>
/// 本地发布序维护者：值/序列号与订阅者派发独立于修订的产生方式。
/// 对应 TS <c>ReplicatedStatePublisher</c>。
/// </summary>
internal sealed class ReplicatedStatePublisher<T>
{
    private readonly Dictionary<StateSubscriber<T>, long> _listeners = [];
    private readonly Action<Exception> _reportError;
    private readonly HashSet<SourceListener> _sourceListeners = [];
    private readonly Queue<Publication<T>> _publications = [];
    private T _value;
    private long _sequence;
    private bool _delivering;

    public ReplicatedStatePublisher(T initial, Action<Exception>? reportError = null)
    {
        _value = initial;
        _reportError = reportError ?? ReportErrorAsync;
    }

    public T Value => _value;

    /// <summary>原子捕获（值, 序列号）。</summary>
    public (T Value, long Sequence) Snapshot() => (_value, _sequence);

    /// <summary>订阅：立即水合当前快照。返回退订委托。</summary>
    public IDisposable Subscribe(StateListener<T> listener)
    {
        var (value, sequence) = Snapshot();
        var subscriber = new StateSubscriber<T>(listener, _reportError);
        _listeners.Add(subscriber, sequence);
        subscriber.Push(value, ReplicatedStates.ServiceDeliveryContext(), new ReplicatedStateDelivery(ReplicatedStateDeliveryKind.Hydrate, sequence));
        subscriber.Drain();
        return new Unsubscription(() =>
        {
            subscriber.Close();
            _listeners.Remove(subscriber);
        });
    }

    /// <summary>订阅权威发布流（ops 级）。</summary>
    public IDisposable SubscribeSource(SourceListener listener)
    {
        _sourceListeners.Add(listener);
        return new Unsubscription(() => _sourceListeners.Remove(listener));
    }

    /// <summary>发布一份已物化的不可变修订，返回隔离的监听器失败。</summary>
    public IReadOnlyList<Exception> Publish(T value, IReadOnlyList<DeltaOp> ops, Context.Context context)
    {
        _value = value;
        _sequence += 1;
        _publications.Enqueue(new Publication<T>(value, ops, _sequence, context));
        if (_delivering) return [];

        _delivering = true;
        var errors = new List<Exception>();
        try
        {
            while (_publications.TryDequeue(out var publication))
            {
                foreach (var listener in _sourceListeners.ToArray())
                {
                    try
                    {
                        listener(publication.Ops, publication.Sequence, publication.Context);
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
                var delivery = new ReplicatedStateDelivery(ReplicatedStateDeliveryKind.Update, publication.Sequence);
                foreach (var (subscriber, hydratedSequence) in _listeners.ToArray())
                {
                    if (publication.Sequence <= hydratedSequence) continue;
                    subscriber.Push(publication.Value, publication.Context, delivery);
                    subscriber.Drain();
                }
            }
        }
        finally
        {
            _delivering = false;
        }
        return errors;
    }

    private static void ReportErrorAsync(Exception error) =>
        _ = Task.Run(() => throw error);

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>杂项静态入口。</summary>
public static class ReplicatedStates
{
    /// <summary>服务交付的合成上下文（无调用者）。对应 TS <c>serviceDeliveryContext</c>。</summary>
    public static Context.Context ServiceDeliveryContext() => Context.Context.Background;

    /// <summary>上报告 listener 的失败（无上报器时的兜底：脱离当前栈抛出）。对应 TS <c>reportErrorAsync</c>。</summary>
    public static void ReportErrorAsync(Exception error) => _ = Task.Run(() => throw error);
}
