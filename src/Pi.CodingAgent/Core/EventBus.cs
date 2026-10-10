// ============================================================================
// Event bus — port of core/event-bus.ts (4d-2a)
// ============================================================================
//
// TS 用 Node `EventEmitter` 实现一个按字符串频道分发的共享事件总线：
//   - `on(channel, handler)` 注册后返回退订函数；handler 可以是同步或异步
//     （TS 的 safeHandler 对返回值 `await`，C# 对应 `Func<object?, Task>`）；
//   - `emit(channel, data)` 同步分发，按注册顺序调用；handler 抛出的异常被
//     safeHandler 捕获并记 `Event handler error (<channel>):`，不会传播给
//     emit 调用方，也不影响同一频道上的其他 handler；
//   - `clear()` 移除所有频道上的全部监听。
//
// 与 TS 的等价性差异（记录到 docs/coding-agent-porting-status.md）：
//   C93：TS 的 `EventBusController` 是接口（`EventBus` + `clear()`），对象字面量
//        实现；C# 落为 `EventBus` 接口 + `EventBusController` 密封类，工厂
//        `EventBusController.CreateEventBus()` 对应 TS `createEventBus()`。
//         消费方（loader）只依赖 `EventBus` 接口面，行为一致。
//   C94：分发采用「加锁快照 + 锁外调用」：Node 的 `emit` 在监听数 > 1 时会克隆
//        监听数组，因此分发过程中退订/新订不影响当次 emit——C# 快照语义相同；
//        锁只保护注册表本身，handler 内再次 `on`/退订不会死锁。

namespace Pi.CodingAgent.Core;

/// <summary>
/// Shared pub/sub bus for extension communication. TS <c>EventBus</c>.
/// </summary>
public interface EventBus
{
    /// <summary>Dispatch <paramref name="data"/> to every handler on <paramref name="channel"/>, in registration order.</summary>
    /// <remarks>
    /// Handler exceptions never propagate to the caller; they are logged as
    /// <c>Event handler error (&lt;channel&gt;): …</c> (TS <c>safeHandler</c>).
    /// </remarks>
    void Emit(string channel, object? data);

    /// <summary>
    /// Register <paramref name="handler"/> on <paramref name="channel"/> and return an idempotent
    /// unsubscribe action. TS <c>on</c>; each registration is independent even for the same handler.
    /// </summary>
    Action On(string channel, Func<object?, Task> handler);
}

/// <summary>
/// Event bus with teardown. TS <c>EventBusController extends EventBus</c>; create instances with
/// <see cref="CreateEventBus"/> (TS <c>createEventBus</c>).
/// </summary>
public sealed class EventBusController : EventBus
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<Subscription>> channels = new(StringComparer.Ordinal);

    /// <summary>Where <c>console.error</c> lines go. Defaults to <see cref="Console.Error"/>; tests substitute a writer (convention C3).</summary>
    internal static TextWriter? ErrorWriterOverride { get; set; }

    private static TextWriter ErrorWriter => ErrorWriterOverride ?? Console.Error;

    /// <summary>Create a bus. TS <c>createEventBus</c>.</summary>
    public static EventBusController CreateEventBus() => new();

    /// <inheritdoc/>
    public void Emit(string channel, object? data)
    {
        List<Subscription> snapshot;
        lock (gate)
        {
            if (!channels.TryGetValue(channel, out var subscriptions) || subscriptions.Count == 0)
            {
                return;
            }

            // Node clones the listener array when dispatching, so unsubscribing or
            // subscribing from inside a handler does not change the in-flight dispatch.
            snapshot = new List<Subscription>(subscriptions);
        }

        foreach (var subscription in snapshot)
        {
            // The wrapper catches everything, so the task never faults; discard silences CS4014.
            _ = InvokeSafely(channel, subscription, data);
        }
    }

    /// <inheritdoc/>
    public Action On(string channel, Func<object?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(handler);
        lock (gate)
        {
            if (!channels.TryGetValue(channel, out var subscriptions))
            {
                subscriptions = [];
                channels[channel] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return () =>
        {
            lock (gate)
            {
                if (!channels.TryGetValue(channel, out var subscriptions))
                {
                    return;
                }

                // Reference equality: removes only this registration. A second call is a no-op,
                // matching Node's `off` on an already-removed listener.
                subscriptions.Remove(subscription);
                if (subscriptions.Count == 0)
                {
                    channels.Remove(channel);
                }
            }
        };
    }

    /// <summary>Remove every listener on every channel. TS <c>clear</c>.</summary>
    public void Clear()
    {
        lock (gate)
        {
            channels.Clear();
        }
    }

    /// <summary>
    /// TS <c>safeHandler</c>: awaits the handler (sync handlers complete before the first await, so a
    /// synchronous throw is logged during <see cref="Emit"/>, exactly like the async TS wrapper) and
    /// logs any exception instead of letting it escape the dispatch loop.
    /// </summary>
    private static async Task InvokeSafely(string channel, Subscription subscription, object? data)
    {
        try
        {
            await subscription.Handler(data);
        }
        catch (Exception error)
        {
            ErrorWriter.WriteLine($"Event handler error ({channel}): {error}");
        }
    }

    /// <summary>One registration. Identity (not the user handler) drives unsubscribe.</summary>
    private sealed class Subscription(Func<object?, Task> handler)
    {
        public Func<object?, Task> Handler { get; } = handler;
    }
}
