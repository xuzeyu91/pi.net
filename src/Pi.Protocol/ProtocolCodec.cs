using Pi.Protocol.Cbor;

namespace Pi.Protocol;

/// <summary>Raised when a decoded or to-be-encoded protocol message fails validation.</summary>
public sealed class ProtocolValidationError(string message) : Exception(message);

/// <summary>
/// Validates, encodes, and decodes framed CBOR protocol messages.
/// Mirrors TS <c>codec.ts</c>; the typebox schema checks are reproduced as
/// hand-written validation over the decoded CBOR value model.
/// </summary>
public static class ProtocolCodec
{
    private static readonly long DefaultMaxFrameLength = Frame.DefaultMaxFrameLength;

    // ---------- validation helpers ----------

    private static bool IsJsonCompatible(object? value) => value switch
    {
        null or bool or long or double or string => true,
        int or short or byte or sbyte or ushort or uint => true,
        List<object?> list => list.All(IsJsonCompatible),
        Dictionary<string, object?> map => map.Values.All(IsJsonCompatible),
        _ => false, // byte[] and other CLR types are not JSON values
    };

    private static bool IsId(object? value) => value is string { Length: > 0 };

    private static bool IsVersion(object? value) => value is long and >= 0;

    private static ProtocolError ParseError(object? value, string what)
    {
        if (value is not Dictionary<string, object?> map
            || map.Count != 2
            || !IsId(map.GetValueOrDefault("code"))
            || map.GetValueOrDefault("message") is not string)
            throw new ProtocolValidationError($"Invalid {what} protocol message");
        return new ProtocolError((string)map["code"]!, (string)map["message"]!);
    }

    private static RpcTarget ParseTarget(object? value, string what)
    {
        if (value is not Dictionary<string, object?> map)
            throw new ProtocolValidationError($"Invalid {what} protocol message");
        if (map.Count == 1 && ServerIds.IsServerId(map.GetValueOrDefault("serverId")))
            return new RpcTarget.ServerTarget((string)map["serverId"]!);
        if (map.Count == 3
            && ServerIds.IsServerId(map.GetValueOrDefault("serverId"))
            && IsId(map.GetValueOrDefault("sessionId"))
            && IsId(map.GetValueOrDefault("attachmentId")))
            return new RpcTarget.SessionTarget(
                (string)map["serverId"]!, (string)map["sessionId"]!, (string)map["attachmentId"]!);
        throw new ProtocolValidationError($"Invalid {what} protocol message");
    }

    private static ClientMessage ParseClientMessageValue(object? value, string what)
    {
        if (!IsJsonCompatible(value) || value is not Dictionary<string, object?> map)
            throw new ProtocolValidationError($"Invalid {what} protocol message");
        switch (map.GetValueOrDefault("type"))
        {
            case "hello" when map.Count == 2 && IsVersion(map.GetValueOrDefault("version")):
                return new ClientMessage.Hello((long)map["version"]!);
            case "request" when map.Count == 4
                && IsId(map.GetValueOrDefault("id"))
                && map.ContainsKey("target")
                && map.ContainsKey("call"):
                return new ClientMessage.Request(
                    (string)map["id"]!, ParseTarget(map["target"], what), map["call"]);
            case "cancel" when map.Count == 3
                && IsId(map.GetValueOrDefault("id"))
                && map.ContainsKey("target"):
                return new ClientMessage.Cancel((string)map["id"]!, ParseTarget(map["target"], what));
            default:
                throw new ProtocolValidationError($"Invalid {what} protocol message");
        }
    }

    /// <summary>Validates a decoded CBOR value as a client protocol message.</summary>
    public static ClientMessage ParseClientMessage(object? value)
        => ParseClientMessageValue(value, "client");

    /// <summary>Validates a decoded CBOR value as a server protocol message.</summary>
    public static ServerMessage ParseServerMessage(object? value)
    {
        const string what = "server";
        if (!IsJsonCompatible(value) || value is not Dictionary<string, object?> map)
            throw new ProtocolValidationError($"Invalid {what} protocol message");
        switch (map.GetValueOrDefault("type"))
        {
            case "hello" when map.Count == 3
                && map.GetValueOrDefault("version") is ProtocolVersion.Current
                && ServerIds.IsServerId(map.GetValueOrDefault("serverId")):
                return new ServerMessage.Hello((long)map["version"]!, (string)map["serverId"]!);
            case "hello_error" when map.Count == 2 && map.ContainsKey("error"):
                return new ServerMessage.HelloError(ParseError(map["error"], what));
            case "response" when map.GetValueOrDefault("ok") is true || map.GetValueOrDefault("ok") is false:
            {
                // ok:true → type/id/ok/result?（3 或 4 项）；ok:false → type/id/ok/error（4 项）。
                if (!IsId(map.GetValueOrDefault("id"))
                    || (map["ok"] is true && (map.Count is not 3 and not 4))
                    || (map["ok"] is false && map.Count != 4))
                    throw new ProtocolValidationError($"Invalid {what} protocol message");
                var id = (string)map["id"]!;
                if (map["ok"] is true)
                    return new ServerMessage.ResponseOk(id, map.GetValueOrDefault("result"));
                return map.ContainsKey("error")
                    ? new ServerMessage.ResponseError(id, ParseError(map["error"], what))
                    : throw new ProtocolValidationError($"Invalid {what} protocol message");
            }
            case "response":
                throw new ProtocolValidationError($"Invalid {what} protocol message");
            case "service_update" when map.Count == 3
                && IsId(map.GetValueOrDefault("subscriptionId"))
                && map.ContainsKey("update"):
                return new ServerMessage.ServiceEvent((string)map["subscriptionId"]!, map["update"]);
            case "attachment" when map.Count == 2:
            {
                var attachment = map.GetValueOrDefault("attachment");
                return new ServerMessage.Attachment(attachment is null ? null : ParseTarget(attachment, what));
            }
            default:
                throw new ProtocolValidationError($"Invalid {what} protocol message");
        }
    }

    // ---------- value conversion ----------

    private static Dictionary<string, object?> TargetValue(RpcTarget target) => target switch
    {
        RpcTarget.ServerTarget server => new Dictionary<string, object?> { ["serverId"] = server.ServerId },
        RpcTarget.SessionTarget session => new Dictionary<string, object?>
        {
            ["serverId"] = session.ServerId,
            ["sessionId"] = session.SessionId,
            ["attachmentId"] = session.AttachmentId,
        },
        _ => throw new ProtocolValidationError("Unknown RPC target"),
    };

    private static Dictionary<string, object?> ErrorValue(ProtocolError error) => new()
    {
        ["code"] = error.Code,
        ["message"] = error.Message,
    };

    private static object? ClientMessageValue(ClientMessage message) => message switch
    {
        ClientMessage.Hello hello => new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["version"] = hello.Version,
        },
        ClientMessage.Request request => new Dictionary<string, object?>
        {
            ["type"] = "request",
            ["id"] = request.Id,
            ["target"] = TargetValue(request.Target),
            ["call"] = request.Call,
        },
        ClientMessage.Cancel cancel => new Dictionary<string, object?>
        {
            ["type"] = "cancel",
            ["id"] = cancel.Id,
            ["target"] = TargetValue(cancel.Target),
        },
        _ => throw new ProtocolValidationError("Unknown client protocol message"),
    };

    private static object? ServerMessageValue(ServerMessage message) => message switch
    {
        ServerMessage.Hello hello => new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["version"] = hello.Version,
            ["serverId"] = hello.ServerId,
        },
        ServerMessage.HelloError helloError => new Dictionary<string, object?>
        {
            ["type"] = "hello_error",
            ["error"] = ErrorValue(helloError.Error),
        },
        ServerMessage.ResponseOk response => new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["id"] = response.Id,
            ["ok"] = true,
            ["result"] = response.Result,
        },
        ServerMessage.ResponseError response => new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["id"] = response.Id,
            ["ok"] = false,
            ["error"] = ErrorValue(response.Error),
        },
        ServerMessage.ServiceEvent serviceEvent => new Dictionary<string, object?>
        {
            ["type"] = "service_update",
            ["subscriptionId"] = serviceEvent.SubscriptionId,
            ["update"] = serviceEvent.Update,
        },
        ServerMessage.Attachment attachment => new Dictionary<string, object?>
        {
            ["type"] = "attachment",
            ["attachment"] = attachment.Route is null ? null : TargetValue(attachment.Route),
        },
        _ => throw new ProtocolValidationError("Unknown server protocol message"),
    };

    // ---------- framed encoding/decoding ----------

    private static string BoundedErrorMessage(Exception error)
        => error.Message.Length <= 500 ? error.Message : error.Message[..497] + "...";

    private static byte[] EncodeProtocolMessage(
        object? value,
        string kind,
        FrameDecoderOptions? options)
    {
        var maxFrameLength = options?.MaxFrameLength ?? DefaultMaxFrameLength;
        try
        {
            return Frame.EncodeFrame(CborCodec.Encode(value, new CborOptions(MaxByteLength: maxFrameLength)));
        }
        catch (Exception error) when (error is not ProtocolValidationError)
        {
            throw new ProtocolValidationError(
                $"Unable to encode {kind} protocol message: {BoundedErrorMessage(error)}");
        }
    }

    /// <summary>Validates and encodes one complete length-prefixed client message.</summary>
    public static byte[] EncodeClientMessage(ClientMessage message, FrameDecoderOptions? options = null)
        => EncodeProtocolMessage(ClientMessageValue(message), "client", options);

    /// <summary>Validates and encodes one complete length-prefixed server message.</summary>
    public static byte[] EncodeServerMessage(ServerMessage message, FrameDecoderOptions? options = null)
        => EncodeProtocolMessage(ServerMessageValue(message), "server", options);

    public abstract class ValidatedMessageDecoder<T>(string kind, Func<object?, T> parse, FrameDecoderOptions? options)
    {
        private bool _failed;
        private readonly Frame.Decoder _frames = new(options);
        private readonly long _maxFrameLength = options?.MaxFrameLength ?? DefaultMaxFrameLength;

        protected Func<object?, T> Parse { get; } = parse;
        protected string Kind { get; } = kind;

        public IReadOnlyList<T> Push(byte[] chunk)
        {
            if (_failed) throw new ProtocolValidationError($"{Kind} message decoder has failed");
            try
            {
                var messages = new List<T>();
                foreach (var frame in _frames.Push(chunk))
                    messages.Add(Parse(CborCodec.Decode(frame, new CborOptions(MaxByteLength: _maxFrameLength))));
                return messages;
            }
            catch (Exception error)
            {
                _failed = true;
                if (error is ProtocolValidationError) throw;
                throw new ProtocolValidationError($"Invalid {Kind} protocol frame: {BoundedErrorMessage(error)}");
            }
        }

        public void End()
        {
            if (_failed) throw new ProtocolValidationError($"{Kind} message decoder has failed");
            try
            {
                _frames.End();
            }
            catch (Exception error)
            {
                _failed = true;
                throw new ProtocolValidationError($"Invalid {Kind} protocol framing: {BoundedErrorMessage(error)}");
            }
        }
    }

    /// <summary>Incrementally decodes and validates framed client messages.</summary>
    public sealed class ClientMessageDecoder(FrameDecoderOptions? options = null)
        : ValidatedMessageDecoder<ClientMessage>("client", ParseClientMessage, options);

    /// <summary>Incrementally decodes and validates framed server messages.</summary>
    public sealed class ServerMessageDecoder(FrameDecoderOptions? options = null)
        : ValidatedMessageDecoder<ServerMessage>("server", ParseServerMessage, options);

    /// <summary>Mirrors TS <c>isSupportedProtocolVersion</c>.</summary>
    public static bool IsSupportedProtocolVersion(object? version)
        => version is long and ProtocolVersion.Current;
}
