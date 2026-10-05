namespace Pi.Server.Transports.Unix;

/// <summary>
/// 组合 <see cref="Server{TMetadata}"/> 与单个 Unix 域套接字监听器。
/// 对应 TS <c>createUnixServer</c>（transports/unix/preset.ts）。
/// </summary>
public static class UnixServer
{
    public static Server<TMetadata> Create<TMetadata>(IServerHost<TMetadata> host, UnixServerOptions options)
        where TMetadata : ISessionMetadata
    {
        var listener = new UnixListener(new UnixListenerOptions
        {
            Path = options.Path,
            Mode = options.Mode,
            MaxFrameLength = options.MaxFrameLength,
            MaxPendingBytes = options.MaxPendingBytes,
            GracefulCloseTimeoutMs = options.GracefulCloseTimeoutMs,
            OnError = options.OnError,
        });
        return new Server<TMetadata>(host, new ServerOptions
        {
            Listeners = [listener],
            MaxFrameLength = options.MaxFrameLength,
            HandshakeTimeoutMs = options.HandshakeTimeoutMs,
            OnConnectionCountChanged = options.OnConnectionCountChanged,
            ServerId = options.ServerId,
            OnError = options.OnError,
        });
    }
}
