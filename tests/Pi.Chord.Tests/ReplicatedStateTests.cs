using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Xunit;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>services/state 复制状态机测试。</summary>
public class ReplicatedStateTests
{
    private static Dictionary<string, object?> Doc() => new() { ["count"] = 1L };

    private static IReadOnlyList<DeltaOp> BaseBatch(Dictionary<string, object?> value) =>
        [new DeltaOp.Replace(value)];

    [Fact]
    public async Task MutableStatePublishesHydrateAndUpdates()
    {
        var state = new MutableReplicatedState<Dictionary<string, object?>>(Doc());
        var deliveries = new List<(string Kind, long Sequence)>();
        var values = new List<long>();

        using var subscription = state.Subscribe((value, _, delivery) =>
        {
            deliveries.Add((delivery.Kind.ToString(), delivery.Sequence));
            values.Add((long)value["count"]!);
            return Task.CompletedTask;
        });

        // 订阅即水合。
        Assert.Equal([("Hydrate", 0)], deliveries.Select(d => (d.Kind, d.Sequence)).ToList());

        state.Change(Context.Context.Background, change =>
        {
            change.Set(Path.Root.Append(Seg.Key("count")), 2L);
        });
        state.Replace(Context.Context.Background, new Dictionary<string, object?> { ["count"] = 9L });

        Assert.Equal(["Hydrate", "Update", "Update"], deliveries.Select(d => d.Kind));
        Assert.Equal([1L, 2L, 9L], values);
        await Task.CompletedTask;
    }

    [Fact]
    public void ChangeRejectsReentrancyAndPublishesOps()
    {
        var state = new MutableReplicatedState<Dictionary<string, object?>>(Doc());
        var opsSeen = new List<int>();

        state.GetType(); // noop
        _ = ((IReplicatedStateInternals)state).SubscribeSource((ops, _, _) => opsSeen.Add(ops.Count));

        state.Change(Context.Context.Background, change =>
        {
            change.Set(Path.Root.Append(Seg.Key("count")), 5L);
            // 变更回调内重入被拒。
            Assert.Throws<InvalidOperationException>(() =>
                state.Change(Context.Context.Background, _ => { }));
        });
        Assert.Single(opsSeen); // 一次发布一个 op
        Assert.Equal(5L, state.Value["count"]);
    }

    [Fact]
    public void ReplicaHydratesThenAppliesUpdates()
    {
        var replica = new ReplicatedStateReplica<Dictionary<string, object?>>();
        Assert.Null(replica.Value);

        replica.Hydrate(1, BaseBatch(Doc()), Context.Context.Background);
        Assert.Equal(1L, replica.Value!["count"]);

        // 未水合前 update 被拒 → 已水合后连续序列 OK。
        replica.Update(2, [new DeltaOp.Set(Path.Root.Append(Seg.Key("count")), 3L)], Context.Context.Background);
        Assert.Equal(3L, replica.Value!["count"]);

        // 断档：清空 + 抛错。
        Assert.Throws<InvalidOperationException>(() =>
            replica.Update(4, [new DeltaOp.Set(Path.Root.Append(Seg.Key("count")), 5L)], Context.Context.Background));
        Assert.Null(replica.Value);
    }

    [Fact]
    public void ReplicaHydrateRequiresBaseBatch()
    {
        var replica = new ReplicatedStateReplica<Dictionary<string, object?>>();
        Assert.Throws<InvalidOperationException>(() =>
            replica.Hydrate(1, [new DeltaOp.Set(Path.Root.Append(Seg.Key("count")), 1L)], Context.Context.Background));
        Assert.Null(replica.Value);
    }

    [Fact]
    public async Task AttachedStateValidatesCursorAndDisposesOnGap()
    {
        var disposed = false;
        var source = new FakeSource(
            new ReplicatedStateSourceSnapshot<Dictionary<string, object?>>(Doc(), 0),
            () => disposed = true);

        var attached = ReplicatedStateAttachments.AttachReplicatedStateSource(source);
        var seen = new List<long>();
        using var subscription = attached.Subscribe((value, _, delivery) =>
        {
            seen.Add(delivery.Sequence);
            return Task.CompletedTask;
        });

        // 喂一帧连续（期望游标 1）。
        source.Deliver(new ReplicatedStateSourceFrame<Dictionary<string, object?>>(
            new Dictionary<string, object?> { ["count"] = 2L },
            [new DeltaOp.Set(Path.Root.Append(Seg.Key("count")), 2L)], 1, Context.Context.Background));
        await Task.Yield();
        Assert.Equal(2L, attached.Value!["count"]);
        Assert.False(disposed);

        // 断档帧（期望 2，给 3）→ 契约违约 → dispose。
        source.Deliver(new ReplicatedStateSourceFrame<Dictionary<string, object?>>(
            new Dictionary<string, object?> { ["count"] = 9L },
            [new DeltaOp.Set(Path.Root.Append(Seg.Key("count")), 9L)], 3, Context.Context.Background));
        await Task.Yield();
        Assert.True(disposed);
    }

    /// <summary>假授权源：Activate 保存 receive，Deliver 直接投递帧。</summary>
    private sealed class FakeSource(
        ReplicatedStateSourceSnapshot<Dictionary<string, object?>> snapshot,
        Action onDispose) : IReplicatedStateSource<Dictionary<string, object?>>
    {
        private Action<ReplicatedStateSourceFrame<Dictionary<string, object?>>>? _receive;

        public ReplicatedStateSourceSnapshot<Dictionary<string, object?>> Snapshot { get; } = snapshot;

        public IReplicatedStateSourceAttachment<Dictionary<string, object?>> Attach()
            => new FakeAttachment(receive => _receive = receive, onDispose);

        public void Deliver(ReplicatedStateSourceFrame<Dictionary<string, object?>> frame) => _receive!(frame);

        private sealed class FakeAttachment(
            Action<Action<ReplicatedStateSourceFrame<Dictionary<string, object?>>>> onActivate,
            Action onDispose) : IReplicatedStateSourceAttachment<Dictionary<string, object?>>
        {
            public void Activate(Action<ReplicatedStateSourceFrame<Dictionary<string, object?>>> receive)
                => onActivate(receive);

            public void Dispose() => onDispose();
        }
    }
}
