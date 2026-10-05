namespace Pi.Server;

/// <summary>
/// 可安全跨协议边界的服务端错误码。对应 TS <c>ServerOperationErrorCode</c>
/// （errors.ts）：chord 的远程服务错误码 + 服务端自有码。
/// </summary>
public static class ServerOperationErrorCodes
{
    public const string WrongServer = "wrong_server";
    public const string SessionNotFound = "session_not_found";
    public const string SessionAmbiguous = "session_ambiguous";
    public const string SessionNotAttached = "session_not_attached";
    public const string ServerDraining = "server_draining";

    /// <summary>服务端自有码（chord 的远程服务码由 <see cref="Pi.Chord.Services.RemoteServiceErrorCodes"/> 提供）。</summary>
    public static readonly IReadOnlyList<string> Own =
        [WrongServer, SessionNotFound, SessionAmbiguous, SessionNotAttached, ServerDraining];

    public static bool IsServerOperationErrorCode(object? value)
        => value is string code
            && (Own.Contains(code) || Pi.Chord.Services.RemoteServiceErrorCodes.IsRemoteServiceErrorCode(code));
}

/// <summary>对外的通用内部错误文案（不泄漏细节）。对应 TS <c>INTERNAL_SERVER_ERROR_MESSAGE</c>。</summary>
public static class ServerErrorText
{
    public const string InternalServerErrorMessage = "Internal server error";
}

/// <summary>可安全跨协议边界的宿主/生命周期错误。对应 TS <c>ServerError</c>。</summary>
public class ServerError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>请求被发往了另一个逻辑服务器。对应 TS <c>WrongServerError</c>。</summary>
public sealed class WrongServerError()
    : ServerError(ServerOperationErrorCodes.WrongServer, "Request was addressed to another server");

/// <summary>会话不存在。对应 TS <c>SessionNotFoundError</c>。</summary>
public sealed class SessionNotFoundError(string? message = null)
    : ServerError(ServerOperationErrorCodes.SessionNotFound, message ?? "Session was not found");

/// <summary>会话 ID 命中多个会话。对应 TS <c>SessionAmbiguousError</c>。</summary>
public sealed class SessionAmbiguousError()
    : ServerError(ServerOperationErrorCodes.SessionAmbiguous, "Session ID matches more than one session");

/// <summary>会话未附加到本客户端。对应 TS <c>SessionNotAttachedError</c>。</summary>
public sealed class SessionNotAttachedError()
    : ServerError(ServerOperationErrorCodes.SessionNotAttached, "Session is not attached to this client");

/// <summary>服务端正在排空。对应 TS <c>ServerDrainingError</c>。</summary>
public sealed class ServerDrainingError()
    : ServerError(ServerOperationErrorCodes.ServerDraining, "Server is draining");
