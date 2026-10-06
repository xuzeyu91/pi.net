using Pi.Chord.Context;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using BclTaskScheduler = System.Threading.Tasks.TaskScheduler;

/// <summary>
/// 按键组织的待完成等待。每个只结算一次：经 resolve、rejectAll 或其 context 的取消。
/// 对应 TS <c>harness/util.ts</c> 的 <c>Waiters</c>。
/// </summary>
public sealed class Waiters<K, T> where K : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<K, HashSet<TaskCompletionSource<T>>> _sets = new();

    /// <summary>为 key 添加一个等待；context 已取消时立即抛出取消。</summary>
    public Task<T> Add(K key, Context context)
    {
        var signal = context.AbortSignal;
        if (signal is { IsCancellationRequested: true })
            return Task.FromCanceled<T>(signal.GetValueOrDefault());
        var waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<TaskCompletionSource<T>> set;
        lock (_gate)
        {
            if (!_sets.TryGetValue(key, out var existing))
            {
                existing = new HashSet<TaskCompletionSource<T>>();
                _sets[key] = existing;
            }
            set = existing;
            set.Add(waiter);
        }
        if (signal is { } abortSignal)
        {
            CancellationTokenRegistration registration = default;
            void OnAbort()
            {
                lock (_gate)
                {
                    set.Remove(waiter);
                    if (set.Count == 0 && _sets.TryGetValue(key, out var current)
                        && ReferenceEquals(current, set))
                    {
                        _sets.Remove(key);
                    }
                }
                waiter.TrySetCanceled(abortSignal);
                registration.DisposeAsync();
            }
            registration = abortSignal.Register(OnAbort);
            // 已结算（含正常 resolve）时摘除取消注册。
            _ = waiter.Task.ContinueWith(
                _ => registration.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, BclTaskScheduler.Default);
        }
        return waiter.Task;
    }

    public IReadOnlyList<K> Keys()
    {
        lock (_gate) return _sets.Keys.ToList();
    }

    /// <summary>结算 key 下的所有等待并移除该组。</summary>
    public void Resolve(K key, T value)
    {
        TaskCompletionSource<T>[]? waiters;
        lock (_gate)
        {
            if (_sets.Remove(key, out var set)) waiters = set.ToArray();
            else waiters = null;
        }
        if (waiters is not null)
            foreach (var waiter in waiters) waiter.TrySetResult(value);
    }

    /// <summary>拒绝当前所有等待。</summary>
    public void RejectAll(Exception error)
    {
        TaskCompletionSource<T>[][] sets;
        lock (_gate)
        {
            sets = _sets.Values.Select(set => set.ToArray()).ToArray();
            _sets.Clear();
        }
        foreach (var set in sets)
            foreach (var waiter in set) waiter.TrySetException(error);
    }
}

/// <summary>杂项助手。对应 TS <c>harness/util.ts</c> 的其余自由函数。</summary>
public static class HarnessUtil
{
    /// <summary>按页序返回分页扫描的全部条目。对应 TS <c>scanAll</c>。</summary>
    public static async Task<System.Collections.Generic.IReadOnlyList<T>> ScanAllAsync<T>(
        Func<IReadOnlyDictionary<string, object?>?, Task<Page<T>>> scan)
    {
        var items = new List<T>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await scan(cursor).ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null);
        return items;
    }

    /// <summary>Harness 已关闭。对应 TS <c>closedError</c>。</summary>
    public static InvalidOperationException ClosedError() => new("Harness is closed");
}
