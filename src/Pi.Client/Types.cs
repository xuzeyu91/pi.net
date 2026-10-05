using Pi.Chord.Services;
using Pi.Protocol;

namespace Pi.Client;

/// <summary>连接状态。对应 TS <c>ConnectionState</c>（types.ts）。</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>连接状态变更。对应 TS <c>ConnectionStateChange</c>。</summary>
public sealed record ConnectionStateChange(ConnectionState State, Exception? Error = null);

/// <summary>退订委托。对应 TS <c>Unsubscribe</c>。</summary>
public delegate void Unsubscribe();

/// <summary>订阅者失败上报。对应 TS <c>ListenerErrorHandler</c>。</summary>
public delegate void ListenerErrorHandler(Exception error);

/// <summary>附加路由变更监听。对应 TS <c>AttachmentChangeListener</c>。</summary>
public delegate void AttachmentChangeListener(RpcTarget.SessionTarget? attachment);

/// <summary>
/// 一次服务订阅。对应 TS <c>ServiceSubscription</c>：
/// 快照已就绪后调用 <see cref="Start"/> 才开始按序投递更新。
/// </summary>
public interface IServiceSubscription
{
    string Id { get; }

    RpcTarget Target { get; }

    ServiceSubscriptionSnapshot Snapshot { get; }

    void Start();

    Task DisposeAsync();
}

/// <summary>客户端选项。对应 TS <c>ClientOptions</c>。</summary>
public sealed record ClientOptions
{
    public required ByteTransportFactory TransportFactory { get; init; }

    /// <summary>物理端点期望的逻辑服务器身份。</summary>
    public required string ServerId { get; init; }

    public int? MaxFrameLength { get; init; }

    /// <summary>上报订阅者失败，但不得污染客户端状态。</summary>
    public ListenerErrorHandler? OnListenerError { get; init; }
}
