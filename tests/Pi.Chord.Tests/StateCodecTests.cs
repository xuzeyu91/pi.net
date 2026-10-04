using Path = Pi.Chord.Delta.Path;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>服务状态编解码器测试（快照/更新的 wire 往返与注册表生命周期）。</summary>
public class StateCodecTests
{
    private static readonly ServiceInstanceAddress Addr = new("inst-a", 1L);

    private static ServiceSubscriptionSnapshot SampleSnapshot() => new("fs", ServiceMode.Singleton,
    [
        new ServiceInstanceSnapshot(Addr,
        [
            new ServiceMemberSnapshot.Method("read"),
            new ServiceMemberSnapshot.State("doc", 2L,
            [
                new DeltaOp.Replace(new Dictionary<string, object?> { ["n"] = 1L }),
                new DeltaOp.Set(new Path([Seg.Key("n")]), 5L),
                new DeltaOp.Set(new Path([Seg.Key("n")]), 6L), // 同路径省略 → wire 短形式
            ]),
        ]),
    ]);

    [Fact]
    public void SnapshotRoundTrips()
    {
        var encoder = new ServiceStateEncoder();
        var decoder = new ServiceStateDecoder();

        var wire = encoder.EncodeSnapshot(SampleSnapshot());
        var decoded = decoder.DecodeSnapshot(wire);
        var original = SampleSnapshot();

        Assert.Equal(original.ServiceId, decoded.ServiceId);
        Assert.Equal(original.Mode, decoded.Mode);
        var instance = Assert.Single(decoded.Instances);
        Assert.Equal(2, instance.Members.Count);
        var state = Assert.IsType<ServiceMemberSnapshot.State>(instance.Members[1]);
        Assert.Equal(2L, state.Sequence);
        Assert.Equal(3, state.Ops.Count);
        // 同路径省略在解码后还原为完整 Op。
        var last = Assert.IsType<DeltaOp.Set>(state.Ops[2]);
        Assert.Equal(new Path([Seg.Key("n")]), last.Path);
        Assert.Equal(6L, last.Value);
    }

    [Fact]
    public void UpdateRoundTripsAndMaintainsRegistry()
    {
        var encoder = new ServiceStateEncoder();
        var decoder = new ServiceStateDecoder();

        // 先快照建立注册表。
        _ = encoder.EncodeSnapshot(SampleSnapshot());
        _ = decoder.DecodeSnapshot(new ServiceStateEncoder().EncodeSnapshot(SampleSnapshot()));

        var update = new ServiceProviderUpdate.StateUpdate(Addr, "doc", 3L,
            [new DeltaOp.Set(new Path([Seg.Key("n")]), 9L)]);
        var wireUpdate = Assert.IsType<WireServiceProviderUpdate.StateUpdate>(encoder.EncodeUpdate(update));
        var decoded = Assert.IsType<ServiceProviderUpdate.StateUpdate>(decoder.DecodeUpdate(wireUpdate));
        var set = Assert.IsType<DeltaOp.Set>(Assert.Single(decoded.Ops));
        Assert.Equal(9L, set.Value);

        // closed：编码器移除实例后，同实例的 state 更新报「未知状态」。
        _ = encoder.EncodeUpdate(new ServiceProviderUpdate.Closed(Addr));
        Assert.Throws<InvalidOperationException>(() =>
            encoder.EncodeUpdate(new ServiceProviderUpdate.StateUpdate(Addr, "doc", 4L, [])));

        // unavailable：注册表整体清空。
        _ = encoder.EncodeUpdate(new ServiceProviderUpdate.Unavailable());
    }
}
