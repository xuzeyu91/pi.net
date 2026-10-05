namespace Pi.Client;

/// <summary>字节传输。对应 TS <c>ByteTransport</c>（transport.ts）。</summary>
public interface IByteTransport
{
    /// <summary>发送一个字节块；调用必须按调用顺序送达。对应 TS <c>send</c>。</summary>
    Task SendAsync(byte[] chunk);

    /// <summary>关闭传输；重复调用必须无害。对应 TS <c>close</c>。</summary>
    void Close();
}

/// <summary>字节传输回调。对应 TS <c>ByteTransportHandlers</c>。</summary>
public interface IByteTransportHandlers
{
    void OnData(byte[] chunk);

    void OnClose();

    void OnError(Exception error);
}

/// <summary>
/// 创建一条新的、已连接且已认证的传输；预期恰好一个终态回调。
/// 对应 TS <c>ByteTransportFactory</c>。
/// </summary>
public delegate Task<IByteTransport> ByteTransportFactory(IByteTransportHandlers handlers);
