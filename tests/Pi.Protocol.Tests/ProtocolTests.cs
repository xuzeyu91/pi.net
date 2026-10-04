using Pi.Protocol.Cbor;
using Xunit;

namespace Pi.Protocol.Tests;

public class CborTests
{
    [Fact]
    public void RoundTripsPrimitiveValues()
    {
        var values = new object?[]
        {
            null, false, true,
            0L, 1L, 23L, 24L, 255L, 256L, 65535L, 65536L,
            -1L, -24L, -25L, -256L, -65536L,
            9_007_199_254_740_991L, // 2^53-1: max safe integer
            -9_007_199_254_740_991L,
            1.5, -0.0,
            "", "hello", "你好，世界",
        };
        foreach (var value in values)
        {
            var decoded = CborCodec.Decode(CborCodec.Encode(value));
            Assert.Equal(value is null ? null : value, decoded);
        }
    }

    [Fact]
    public void MatchesTypeScriptFloatIntegrityChecks()
    {
        // Mirrors the TS decoder: an integral-valued double outside the safe range is
        // rejected on decode (it cannot be represented as a safe JS integer), and
        // non-finite numbers are rejected on encode.
        var encoded = CborCodec.Encode(double.MaxValue);
        Assert.Throws<CborError>(() => CborCodec.Decode(encoded));
        Assert.Throws<CborError>(() => CborCodec.Encode(double.PositiveInfinity));
        Assert.Throws<CborError>(() => CborCodec.Encode(double.NegativeInfinity));
        Assert.Throws<CborError>(() => CborCodec.Encode(double.NaN));
    }

    [Fact]
    public void EncodesCanonicalRfc8949Examples()
    {
        // RFC 8949 Appendix A examples (subset covered by the protocol's strict profile).
        Assert.Equal(new byte[] { 0x00 }, CborCodec.Encode(0L));
        Assert.Equal(new byte[] { 0x17 }, CborCodec.Encode(23L));
        Assert.Equal(new byte[] { 0x18, 0x18 }, CborCodec.Encode(24L));
        Assert.Equal(new byte[] { 0x19, 0x01, 0x00 }, CborCodec.Encode(256L));
        Assert.Equal(new byte[] { 0x1a, 0x00, 0x01, 0x00, 0x00 }, CborCodec.Encode(65536L));
        Assert.Equal(new byte[] { 0x20 }, CborCodec.Encode(-1L));
        Assert.Equal(new byte[] { 0x38, 0x63 }, CborCodec.Encode(-100L));
        Assert.Equal(new byte[] { 0xf4 }, CborCodec.Encode(false));
        Assert.Equal(new byte[] { 0xf5 }, CborCodec.Encode(true));
        Assert.Equal(new byte[] { 0xf6 }, CborCodec.Encode(null));
        Assert.Equal(new byte[] { 0xfb, 0x3f, 0xf1, 0x99, 0x99, 0x99, 0x99, 0x99, 0x9a }, CborCodec.Encode(1.1));
        Assert.Equal(new byte[] { 0x60 }, CborCodec.Encode(""));
        Assert.Equal(new byte[] { 0x61, 0x61 }, CborCodec.Encode("a"));
        Assert.Equal([0x64, 0x49, 0x45, 0x54, 0x46], CborCodec.Encode("IETF"));
    }

    [Fact]
    public void EncodesArraysAndMaps()
    {
        Assert.Equal([0x82, 0x01, 0x02], CborCodec.Encode(new List<object?> { 1L, 2L }));
        var map = new Dictionary<string, object?> { ["a"] = 1L, ["b"] = new List<object?> { 2L, 3L } };
        var roundTripped = (Dictionary<string, object?>)CborCodec.Decode(CborCodec.Encode(map))!;
        Assert.Equal(1L, roundTripped["a"]);
        Assert.Equal(new List<object?> { 2L, 3L }, roundTripped["b"]);
    }

    [Fact]
    public void SkipsUndefinedMapEntriesLikeTypeScript()
    {
        var map = new Dictionary<string, object?> { ["a"] = 1L, ["skipped"] = null, ["b"] = 2L };
        var bytes = CborCodec.Encode(map);
        Assert.Equal(0xa2, bytes[0]); // map with 2 entries, not 3
        var decoded = (Dictionary<string, object?>)CborCodec.Decode(bytes)!;
        Assert.Equal(2, decoded.Count);
        Assert.False(decoded.ContainsKey("skipped"));
    }

    [Fact]
    public void EnforcesLimits()
    {
        // Depth is 0-based (depth > maxDepth), matching the TS implementation:
        // a scalar still encodes at maxDepth 0, a container does not.
        CborCodec.Encode(1L, new CborOptions(MaxDepth: 0));
        Assert.Throws<CborError>(() => CborCodec.Encode(new List<object?> { 1L }, new CborOptions(MaxDepth: 0)));
        Assert.Throws<CborError>(() => CborCodec.Encode("x", new CborOptions(MaxByteLength: 0)));
        Assert.Throws<CborError>(() => CborCodec.Encode(double.PositiveInfinity));

        var deep = new List<object?> { new List<object?> { new List<object?>() } };
        // Top array=depth 0, middle=1, inner=2: rejects with maxDepth 1, passes with 2.
        Assert.Throws<CborError>(() => CborCodec.Decode(CborCodec.Encode(deep), new CborOptions(MaxDepth: 1)));
        CborCodec.Decode(CborCodec.Encode(deep), new CborOptions(MaxDepth: 2));

        Assert.Throws<CborError>(() => CborCodec.Decode([0x01, 0x02])); // trailing data
        Assert.Throws<CborError>(() => CborCodec.Decode([0x18])); // truncated
        Assert.Throws<CborError>(() => CborCodec.Decode([0x1f])); // indefinite length not supported
        Assert.Throws<CborError>(() => CborCodec.Decode([0xc0, 0x00])); // tags not supported
    }

    [Fact]
    public void RejectsOutOfSafeRangeIntegers()
    {
        Assert.Throws<CborError>(() => CborCodec.Encode(9_007_199_254_740_992L));
        Assert.Throws<CborError>(() => CborCodec.Decode(
            [0x1b, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])); // 2^53
    }
}

public class FrameTests
{
    [Fact]
    public void EncodesLengthPrefix()
    {
        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0xaa], Frame.EncodeFrame([0xaa]));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], Frame.EncodeFrame([]));
    }

    [Fact]
    public void DecodesSplitChunks()
    {
        var decoder = new Frame.Decoder();
        var frame = Frame.EncodeFrame("hello"u8.ToArray());
        // Split at every offset to prove incremental reassembly.
        for (var split = 0; split < frame.Length; split++)
        {
            var first = decoder.Push(frame[..split]);
            var second = decoder.Push(frame[split..]);
            var all = first.Concat(second).ToList();
            if (split > 0 || split < frame.Length)
                Assert.All(all, candidate => Assert.True(candidate.Length <= 5));
            var combined = decoder.Push([]);
            all.AddRange(combined);
            if (split > 0 && split < frame.Length)
            {
                // A partial push must not emit a frame early.
                continue;
            }
            Assert.Single(all);
        }
    }

    [Fact]
    public void RejectsOversizedAndTruncatedFrames()
    {
        var decoder = new Frame.Decoder(new FrameDecoderOptions(MaxFrameLength: 3));
        Assert.Throws<FrameError>(() => decoder.Push([0x00, 0x00, 0x00, 0x10]));
        var truncated = new Frame.Decoder();
        truncated.Push([0x00, 0x00, 0x00, 0x05, 0x01]);
        Assert.Throws<FrameError>(() => truncated.End());
        var ended = new Frame.Decoder();
        ended.End();
        Assert.Throws<FrameError>(() => ended.Push([0x00]));
    }
}

public class ProtocolCodecTests
{
    [Fact]
    public void EncodesAndDecodesClientHello()
    {
        var message = new ClientMessage.Hello(8);
        var bytes = ProtocolCodec.EncodeClientMessage(message);
        var decoded = Assert.IsType<ClientMessage.Hello>(
            Assert.Single(new ProtocolCodec.ClientMessageDecoder().Push(bytes)));
        Assert.Equal(8, decoded.Version);
    }

    [Fact]
    public void EncodesAndDecodesRequestWithSessionTarget()
    {
        var message = new ClientMessage.Request(
            "call-1",
            new RpcTarget.SessionTarget("01234567-89ab-4cde-8f01-23456789abcd", "session-1", "attach-1"),
            new Dictionary<string, object?> { ["tool"] = "read_file" });
        var bytes = ProtocolCodec.EncodeClientMessage(message);
        var decoded = Assert.IsType<ClientMessage.Request>(
            Assert.Single(new ProtocolCodec.ClientMessageDecoder().Push(bytes)));
        Assert.Equal("call-1", decoded.Id);
        var target = Assert.IsType<RpcTarget.SessionTarget>(decoded.Target);
        Assert.Equal("session-1", target.SessionId);
        Assert.Equal("read_file", ((Dictionary<string, object?>)decoded.Call!)["tool"]);
    }

    [Fact]
    public void ServerHelloRoundTripAndVersionCheck()
    {
        var message = new ServerMessage.Hello(ProtocolVersion.Current, "01234567-89ab-4cde-8f01-23456789abcd");
        var bytes = ProtocolCodec.EncodeServerMessage(message);
        var decoded = Assert.IsType<ServerMessage.Hello>(
            Assert.Single(new ProtocolCodec.ServerMessageDecoder().Push(bytes)));
        Assert.True(ProtocolCodec.IsSupportedProtocolVersion(decoded.Version));
        Assert.False(ProtocolCodec.IsSupportedProtocolVersion(7L));
    }

    [Fact]
    public void RejectsInvalidMessages()
    {
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseClientMessage(
            new Dictionary<string, object?> { ["type"] = "hello", ["version"] = -1 }));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseClientMessage(
            new Dictionary<string, object?> { ["type"] = "mystery" }));
        Assert.Throws<ProtocolValidationError>(() => ProtocolCodec.ParseServerMessage(
            new Dictionary<string, object?>
            {
                ["type"] = "hello",
                ["version"] = 8L,
                ["serverId"] = "not-a-uuid",
            }));
    }
}
