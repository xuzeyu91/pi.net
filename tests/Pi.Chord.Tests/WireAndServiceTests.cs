using Path = Pi.Chord.Delta.Path;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>delta wire 编解码与服务 wire 协议测试。</summary>
public class WireAndServiceTests
{
    private static Path P(params Seg[] segments) => new(segments);

    [Fact]
    public void EncodeInternsOnSecondUseAndOmitsSamePath()
    {
        var encoder = new DeltaWire.Encoder();
        var ops = new List<DeltaOp>
        {
            new DeltaOp.Set(P(Seg.Key("a")), 1L), // 第一次使用 "a"：内联
            new DeltaOp.Set(P(Seg.Key("b")), 2L),
            new DeltaOp.Set(P(Seg.Key("a")), 3L), // "a" 第二次使用：先定义再引用
        };
        var wire = encoder.Encode(ops);

        // 内联路径是数组形式（TS Path 本身是元组）；驻留在第二次完整路径使用时发生。
        Assert.Equal(["s", new List<object?> { "a" }, 1L], wire[0].Cast<object?>());
        Assert.Equal(["s", new List<object?> { "b" }, 2L], wire[1].Cast<object?>());
        Assert.Equal(["#", 0L, new List<object?> { "a" }], wire[2].Cast<object?>());
        Assert.Equal(["s", 0L, 3L], wire[3].Cast<object?>());
    }

    [Fact]
    public void ReplaceBatchResetsIdTable()
    {
        var encoder = new DeltaWire.Encoder();
        var path = P(Seg.Key("a"));
        var first = encoder.Encode([new DeltaOp.Set(path, 1L), new DeltaOp.Set(path, 3L)]);
        // 第二条同路径：整体省略引用（批内作用域）。
        Assert.Equal(["s", new List<object?> { "a" }, 1L], first[0].Cast<object?>());
        Assert.Equal(["s", 3L], first[1].Cast<object?>());

        // r 之后：id 表清空，"a" 重新内联（而非引用上一批的定义 id）。
        var second = encoder.Encode([new DeltaOp.Replace(null), new DeltaOp.Set(path, 5L)]);
        Assert.Equal(["r", null], second[0].Cast<object?>());
        Assert.Equal(["s", new List<object?> { "a" }, 5L], second[1].Cast<object?>());
    }

    [Fact]
    public void RoundTripsThroughWire()
    {
        var encoder = new DeltaWire.Encoder();
        var decoder = new DeltaWire.Decoder();
        var ops = new List<DeltaOp>
        {
            new DeltaOp.Set(P(Seg.Key("deep"), Seg.Key("leaf")), 42L),
            new DeltaOp.Append(P(Seg.Key("deep"), Seg.Key("name")), ".net"),
            new DeltaOp.Splice(P(Seg.Key("list")), 1, 0, ["z"]),
            new DeltaOp.Move(P(Seg.Key("list")), [2L, 1L, 0L]),
            new DeltaOp.Delete(P(Seg.Key("deep"), Seg.Key("leaf"))),
        };
        var decoded = decoder.Decode(encoder.Encode(ops));
        Assert.Equal(5, decoded.Count);
        Assert.Equal(ops[0], decoded[0]);
        Assert.Equal(ops[1], decoded[1]);
        var splice = Assert.IsType<DeltaOp.Splice>(decoded[2]);
        Assert.Equal(1L, splice.Index);
        Assert.Equal(0L, splice.Remove);
        Assert.Equal(["z"], splice.Items);
        var move = Assert.IsType<DeltaOp.Move>(decoded[3]);
        Assert.Equal(new long[] { 2, 1, 0 }, move.Permutation);
        Assert.Equal(ops[4], decoded[4]);
    }

    [Fact]
    public void DecoderRejectsUnknownPathId()
    {
        var decoder = new DeltaWire.Decoder();
        Assert.Throws<KeyNotFoundException>(() => decoder.Decode([["s", 7L, 1L]]));
    }

    [Fact]
    public void RevisionValidatorDetectsCycles()
    {
        var validator = new JsonRevisionValidator();
        var map = new Dictionary<string, object?> { ["ok"] = 1L };
        validator.Validate(map); // 正常通过
        validator.Validate(map); // 已验证子树：直接跳过

        // 循环引用：值模型里用普通 object 模拟不可达容器。
        var cyclic = new Dictionary<string, object?> { ["self"] = null! };
        cyclic["self"] = cyclic;
        Assert.Throws<InvalidOperationException>(() => validator.Validate(cyclic));
    }

    [Fact]
    public void ServiceControlCallsRoundTrip()
    {
        var subscribe = ServiceWire.CreateServiceSubscribeCall("sub-1", "fs", ServiceMode.Keyed);
        var decoded = ServiceWire.DecodeServiceControlCall(subscribe);
        var parsed = Assert.IsType<ServiceControlCall.Subscribe>(decoded);
        Assert.Equal("sub-1", parsed.SubscriptionId);
        Assert.Equal("fs", parsed.ServiceId);
        Assert.Equal(ServiceMode.Keyed, parsed.Mode);

        Assert.IsType<ServiceControlCall.Catalogue>(
            ServiceWire.DecodeServiceControlCall(ServiceWire.CreateServiceCatalogueCall()));
        Assert.IsType<ServiceControlCall.Unsubscribe>(
            ServiceWire.DecodeServiceControlCall(ServiceWire.CreateServiceUnsubscribeCall("sub-1")));

        // 非控制调用 → null。
        Assert.Null(ServiceWire.DecodeServiceControlCall(new ServiceCall("other", "catalogue", [])));
    }

    [Fact]
    public void ProviderUpdateParsingValidatesShape()
    {
        var update = new Dictionary<string, object?>
        {
            ["type"] = "state",
            ["member"] = "doc",
            ["sequence"] = 3L,
            ["ops"] = new List<object?> { new List<object?> { "s", new List<object?> { "count" }, 5L } },
        };
        var parsed = Assert.IsType<WireServiceProviderUpdate.StateUpdate>(
            ServiceWire.ParseServiceProviderUpdate(update));
        Assert.Equal(3L, parsed.Sequence);

        // reset 必须只含根替换。
        var badReset = new Dictionary<string, object?>
        {
            ["type"] = "reset",
            ["snapshot"] = new Dictionary<string, object?>
            {
                ["serviceId"] = "fs",
                ["mode"] = "singleton",
                ["instances"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["members"] = new List<object?>
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "doc",
                                ["kind"] = "state",
                                ["sequence"] = 0L,
                                ["ops"] = new List<object?>
                                {
                                    new List<object?> { "s", new List<object?> { "count" }, 1L }, // 非 r 动词
                                },
                            },
                        },
                    },
                },
            },
        };
        Assert.Throws<InvalidOperationException>(() => ServiceWire.ParseServiceProviderUpdate(badReset));
    }
}
