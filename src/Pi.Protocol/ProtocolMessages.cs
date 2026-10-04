using System.Text.RegularExpressions;

namespace Pi.Protocol;

/// <summary>Current wire protocol version. Mirrors TS <c>PROTOCOL_VERSION</c>.</summary>
public static class ProtocolVersion
{
    public const long Current = 8;
}

/// <summary>Server identity: lowercase UUIDv4. Mirrors TS <c>ServerId</c>.</summary>
public static partial class ServerIds
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex V4Pattern();

    public static bool IsServerId(object? value) => value is string text && V4Pattern().IsMatch(text);
}

/// <summary>Target of one RPC call. Mirrors the TS union <c>ServerTarget | SessionTarget</c>.</summary>
public abstract record RpcTarget
{
    private RpcTarget() { }

    /// <summary>A server-wide call, fenced to one logical server.</summary>
    public sealed record ServerTarget(string ServerId) : RpcTarget;

    /// <summary>A session call, fenced to one logical server, durable session, and live attachment.</summary>
    public sealed record SessionTarget(string ServerId, string SessionId, string AttachmentId) : RpcTarget;
}

/// <summary>Shared envelope error payload. Mirrors TS <c>ProtocolError</c>.</summary>
public sealed record ProtocolError(string Code, string Message);

/// <summary>
/// Messages sent by the client. Mirrors the TS union <c>ClientMessage</c>.
/// Pattern-match with <c>is ClientMessage.Hello</c>, etc.
/// </summary>
public abstract record ClientMessage
{
    private ClientMessage() { }

    /// <summary>Must be the first frame sent by a client.</summary>
    public sealed record Hello(long Version) : ClientMessage;

    public sealed record Request(string Id, RpcTarget Target, object? Call) : ClientMessage;

    public sealed record Cancel(string Id, RpcTarget Target) : ClientMessage;
}

/// <summary>Messages sent by the server. Mirrors the TS union <c>ServerMessage</c>.</summary>
public abstract record ServerMessage
{
    private ServerMessage() { }

    public sealed record Hello(long Version, string ServerId) : ServerMessage;

    public sealed record HelloError(ProtocolError Error) : ServerMessage;

    /// <summary>Successful response (<c>ok: true</c> with an optional result).</summary>
    public sealed record ResponseOk(string Id, object? Result) : ServerMessage;

    /// <summary>Failed response (<c>ok: false</c> with a required error).</summary>
    public sealed record ResponseError(string Id, ProtocolError Error) : ServerMessage;

    public sealed record ServiceEvent(string SubscriptionId, object? Update) : ServerMessage;

    /// <summary>Out-of-band update to this presentation's selected Session route.</summary>
    public sealed record Attachment(RpcTarget? Route) : ServerMessage;
}
