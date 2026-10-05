using Pi.Protocol;

namespace Pi.Client;

/// <summary>服务端返回的协议错误。对应 TS <c>ServerError</c>（client/src/errors.ts）。</summary>
public sealed class ServerError(ProtocolError error) : Exception(error.Message)
{
    public string Code { get; } = error.Code;
}

/// <summary>客户端已断开。对应 TS <c>DisconnectedError</c>。</summary>
public sealed class DisconnectedError(string message = "Client is disconnected", Exception? cause = null)
    : Exception(message, cause);

/// <summary>客户端已释放。对应 TS <c>ClientDisposedError</c>。</summary>
public sealed class ClientDisposedError() : Exception("Client is disposed");

/// <summary>错误规范化。对应 TS <c>toError</c> / <c>toDisconnectedError</c>。</summary>
public static class ClientErrors
{
    public static Exception ToError(Exception error) => error;

    public static DisconnectedError ToDisconnectedError(Exception error)
        => error is DisconnectedError disconnected ? disconnected : new DisconnectedError(error.Message, error);
}
