using Pi.Chord.Services;
using Pi.Protocol;

namespace Pi.Server;

/// <summary>已建立、已授权的有序字节连接。对应 TS <c>ByteConnection</c>。</summary>
public interface IByteConnection
{
    bool Closed { get; }

    Task SendAsync(byte[] chunk, CancellationToken cancellationToken = default);

    Task CloseAsync(byte[]? finalChunk = null);
}

/// <summary>字节连接回调。对应 TS <c>ByteConnectionHandler</c>。</summary>
public interface IByteConnectionHandler
{
    void OnData(byte[] chunk);

    void OnClose();

    void OnError(Exception error);
}

/// <summary>接受已授权连接。对应 TS <c>ByteConnectionAcceptor</c>。</summary>
public delegate IByteConnectionHandler ByteConnectionAcceptor(IByteConnection connection);

/// <summary>连接阶段。对应 TS <c>ConnectionStage</c>。</summary>
public enum ConnectionStage
{
    AwaitingHello,
    Handshaking,
    Ready,
    Closing,
    Closed,
}

/// <summary>一条活动请求（取消控制器 + 目标）。对应 TS <c>activeRequests</c> 的值。</summary>
public sealed record ActiveRequest(CancellationTokenSource Controller, RpcTarget Target);

/// <summary>连接状态。对应 TS <c>ConnectionState</c>。</summary>
public sealed class ConnectionState
{
    public required IByteConnection Connection { get; init; }

    public required ProtocolCodec.ClientMessageDecoder Decoder { get; init; }

    public required Dictionary<string, ServiceStateEncoder> ServiceStateEncoders { get; init; }

    public ConnectionStage Stage { get; set; } = ConnectionStage.AwaitingHello;

    public bool Disconnected { get; set; }

    public Task? Handshake { get; set; }

    /// <summary>握手超时定时器（C# 用可取消的延迟代替 Node 的 Timeout）。</summary>
    public CancellationTokenSource? HandshakeTimeout { get; set; }

    public IRoutedServerServiceAttachment? ServerServices { get; set; }

    public Dictionary<string, ActiveRequest> ActiveRequests { get; init; } = [];

    /// <summary>连接是否已进入终态。对应 TS <c>isTerminalConnection</c>。</summary>
    public bool IsTerminal => Disconnected || Stage is ConnectionStage.Closing or ConnectionStage.Closed;
}
