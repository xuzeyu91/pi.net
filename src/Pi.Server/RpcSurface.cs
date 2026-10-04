// ============================================================================
// PORT SKELETON - packages/server + packages/client (3.1k lines TS combined):
// the RPC server (listener, session router) and client (connection state
// machine, service subscriptions) over chord. Unix domain sockets map to
// UnixDomainSocketEndPoint / named pipes on Windows.
// ============================================================================

namespace Pi.Server;

/// <summary>Server-side error surface. Mirrors packages/server/src/errors.ts.</summary>
public sealed class ServerError(string message) : Exception(message);

/// <summary>
/// Accepts protocol connections and routes sessions. Mirrors TS <c>Listener</c> /
/// <c>Server</c> from packages/server/src - implementation lands next session.
/// </summary>
public sealed class RpcServer
{
    /// <summary>Binds the transport and starts accepting frames (hello handshake onward).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Port of packages/server/src/listener.ts + server.ts - scheduled next session.");
}
