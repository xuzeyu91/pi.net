using Pi.Chord.Context;
using Pi.Chord.Services;

namespace Pi.Server;

/// <summary>服务端选项。对应 TS <c>ServerOptions</c>（types.ts）。</summary>
public sealed record ServerOptions
{
    public required IReadOnlyList<IServerListener> Listeners { get; init; }

    /// <summary>由安装/配置提供的稳定逻辑服务器标识。</summary>
    public required string ServerId { get; init; }

    public int? MaxFrameLength { get; init; }

    public int? HandshakeTimeoutMs { get; init; }

    public Action<int>? OnConnectionCountChanged { get; init; }

    public Action<Exception>? OnError { get; init; }
}

/// <summary>
/// 路由所需的最小持久会话元数据；应用可扩展。
/// 对应 TS <c>SessionMetadata</c>。
/// </summary>
public interface ISessionMetadata
{
    string Id { get; }
}

/// <summary>
/// 一条展示连接对某个被托管会话的实时能力。对应 TS <c>RoutedSessionAttachment</c>。
/// </summary>
public interface IRoutedSessionAttachment
{
    /// <summary>把一个与契约无关的服务操作路由到已附加的会话端点。对应 TS <c>invokeService</c>。</summary>
    Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context);

    Task ReleaseAsync(Context context);
}

/// <summary>
/// 展示域路由能力（供服务端服务实现使用）。对应 TS <c>RoutedServerPresentation</c>。
/// </summary>
public interface IRoutedServerPresentation
{
    Task AttachSessionAsync(string sessionId, Context context);

    Task DetachSessionAsync(Context context);

    /// <summary>在应用删除持久元数据前释放路由附加与句柄。对应 TS <c>prepareSessionRemoval</c>。</summary>
    Task PrepareSessionRemovalAsync(string sessionId, Context context);
}

/// <summary>一条连接的服务端域服务端点。对应 TS <c>RoutedServerServiceAttachment</c>。</summary>
public interface IRoutedServerServiceAttachment
{
    Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context);

    Task ReleaseAsync(Context context);
}

/// <summary>服务端域服务宿主。对应 TS <c>RoutedServerServiceHost</c>。</summary>
public interface IRoutedServerServiceHost
{
    Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
        Context context);
}

/// <summary>
/// 跨进程安全的会话句柄：获取展示域会话能力。对应 TS <c>RoutedSessionHandle</c>。
/// </summary>
public interface IRoutedSessionHandle
{
    Task<IRoutedSessionAttachment> AttachClientAsync(Context context);

    /// <summary>
    /// 意外终止时解析为错误；正常关闭后解析为 null。缺省为 null（不提供终止通知）。
    /// 对应 TS 可选 <c>terminated</c>。
    /// </summary>
    Task<Exception?>? Terminated => null;

    Task CloseAsync(Context context);
}

/// <summary>
/// 服务端全局管理与会话路由所需的应用能力。对应 TS <c>ServerHost&lt;TMetadata&gt;</c>。
/// </summary>
public interface IServerHost<TMetadata> where TMetadata : ISessionMetadata
{
    IRoutedServerServiceHost ServerServices { get; }

    /// <summary>解析一个持久会话 ID，或抛出有界的路由错误。对应 TS <c>resolveSession</c>。</summary>
    Task<TMetadata> ResolveSessionAsync(string sessionId, Context context);

    Task<IRoutedSessionHandle> OpenSessionAsync(TMetadata metadata, Context context);
}
