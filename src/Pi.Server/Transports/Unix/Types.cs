namespace Pi.Server.Transports.Unix;

/// <summary>Unix 监听器选项。对应 TS <c>UnixListenerOptions</c>（transports/unix/types.ts）。</summary>
public sealed record UnixListenerOptions
{
    public required string Path { get; init; }

    /// <summary>socket 文件权限；缺省仅属主读写（0o600）。</summary>
    public int? Mode { get; init; }

    /// <summary>单连接排队字节上限，超出则断开慢对端。</summary>
    public int? MaxPendingBytes { get; init; }

    public int? GracefulCloseTimeoutMs { get; init; }

    /// <summary>用于推导与校验 <see cref="MaxPendingBytes"/>；自定义时须与服务端一致。</summary>
    public int? MaxFrameLength { get; init; }

    public Action<Exception>? OnError { get; init; }
}

/// <summary>
/// 单 Unix socket 监听器的完整服务端选项。对应 TS <c>UnixServerOptions</c>
/// （<c>Omit&lt;ServerOptions, "listeners"&gt; &amp; UnixListenerOptions</c>）。
/// </summary>
public sealed record UnixServerOptions
{
    public required string ServerId { get; init; }

    public required string Path { get; init; }

    public int? Mode { get; init; }

    public int? MaxPendingBytes { get; init; }

    public int? GracefulCloseTimeoutMs { get; init; }

    public int? MaxFrameLength { get; init; }

    public int? HandshakeTimeoutMs { get; init; }

    public Action<int>? OnConnectionCountChanged { get; init; }

    public Action<Exception>? OnError { get; init; }
}
