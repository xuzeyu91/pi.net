// ============================================================================
// PORT SKELETON - packages/client (1.1k lines TS): RPC client over chord.
// ============================================================================

namespace Pi.Client;

/// <summary>Client-side connection errors. Mirrors packages/client/src/errors.ts.</summary>
public sealed class DisconnectedError(string message) : Exception(message);

public sealed class ClientDisposedError(string message) : Exception(message);

public sealed class ClientServerError(string message) : Exception(message);

/// <summary>Connection lifecycle states. Mirrors TS <c>ConnectionState</c>.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,
    Closed,
}

/// <summary>Byte-level transport abstraction. Mirrors TS <c>ByteTransport</c>.</summary>
public interface IByteTransport : IAsyncDisposable
{
    /// <summary>Frames received from the server (already length-unframed).</summary>
    IAsyncEnumerable<byte[]> Incoming { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// RPC client. Mirrors TS <c>Client</c> from packages/client/src/client.ts -
/// request/response promise map, server-initiated calls, and service
/// subscriptions land next session.
/// </summary>
public sealed class RpcClient(IByteTransport transport)
{
    public IByteTransport Transport { get; } = transport;

    public ConnectionState State => ConnectionState.Disconnected;

    /// <summary>Opens the connection and performs the hello/version handshake.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Port of packages/client/src/connection.ts - scheduled next session.");
}
