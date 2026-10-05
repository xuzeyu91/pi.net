namespace Pi.Server;

/// <summary>
/// 监听器：完成所需的传输层认证后，把已建立的字节连接交给 accept。
/// 对应 TS <c>ServerListener</c>（listener.ts）。
/// </summary>
public interface IServerListener
{
    /// <summary>开始监听并把已授权的连接交给 <paramref name="accept"/>。对应 TS <c>start(accept)</c>。</summary>
    Task StartAsync(ByteConnectionAcceptor accept, CancellationToken cancellationToken = default);

    /// <summary>停止监听并释放资源。对应 TS <c>close()</c>。</summary>
    Task CloseAsync();
}
