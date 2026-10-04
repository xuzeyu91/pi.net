// ============================================================================
// PORT SKELETON - packages/mcp (3.2k lines TS): MCP client with jsonrpc,
// stdio/streamable-http/in-memory transports, and OAuth flows.
// cross-spawn maps to System.Diagnostics.Process on .NET.
// ============================================================================

namespace Pi.Mcp;

/// <summary>Standard JSON-RPC 2.0 error codes used by MCP. Mirrors TS <c>JSON_RPC_ERROR_CODES</c>.</summary>
public static class JsonRpcErrorCodes
{
    public const long ParseError = -32700;
    public const long InvalidRequest = -32600;
    public const long MethodNotFound = -32601;
    public const long InvalidParams = -32602;
    public const long InternalError = -32603;
}

/// <summary>A JSON-RPC request identifier (string or number). Mirrors TS <c>JsonRpcId</c>.</summary>
public sealed record JsonRpcId(object? Value);

/// <summary>Base class for JSON-RPC messages. Mirrors the TS <c>JsonRpcMessage</c> union.</summary>
public abstract record JsonRpcMessage
{
    private JsonRpcMessage() { }

    public sealed record Request(long Id, string Method, object? Params) : JsonRpcMessage;

    public sealed record SuccessResponse(object? Id, object? Result) : JsonRpcMessage;

    public sealed record ErrorResponse(object? Id, long Code, string Message, object? Data) : JsonRpcMessage;

    public sealed record Notification(string Method, object? Params) : JsonRpcMessage;
}

/// <summary>Base MCP error. Mirrors TS <c>McpError</c>.</summary>
public class McpError(string message) : Exception(message);

public sealed class McpTimeoutError(string message) : McpError(message);

public sealed class McpAbortError(string message) : McpError(message);

public sealed class McpConnectionClosedError(string message) : McpError(message);

/// <summary>
/// MCP client session over a transport. Mirrors TS <c>McpClient</c> from
/// packages/mcp/src/client.ts - implementation (initialize handshake, tool
/// listing/calling, notifications, cancellation) lands next session.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>Connects and performs the MCP initialize handshake.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Port of packages/mcp/src/client.ts - scheduled next session.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
