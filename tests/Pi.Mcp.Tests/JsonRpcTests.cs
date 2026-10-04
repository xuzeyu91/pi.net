using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>JSON-RPC 消息解析与序列化测试（对齐 TS mcp 包 jsonrpc.ts 行为）。</summary>
public class JsonRpcTests
{
    [Fact]
    public void ParsesRequestNotificationAndResponses()
    {
        var request = JsonRpc.ParseText("""{"jsonrpc":"2.0","id":"1","method":"tools/list","params":{"cursor":null}}""");
        var req = Assert.IsType<JsonRpcMessage.Request>(request);
        Assert.Equal("tools/list", req.Method);
        Assert.Equal("1", req.Id.Value);
        Assert.NotNull(req.Params);

        var notification = JsonRpc.ParseText("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.IsType<JsonRpcMessage.Notification>(notification);

        var success = JsonRpc.ParseText("""{"jsonrpc":"2.0","id":7,"result":{"tools":[]}}""");
        var ok = Assert.IsType<JsonRpcMessage.SuccessResponse>(success);
        Assert.Equal(7L, Assert.IsType<long>(ok.Id.Value));

        var error = JsonRpc.ParseText(
            """{"jsonrpc":"2.0","id":"2","error":{"code":-32601,"message":"Method not found"}}""");
        var err = Assert.IsType<JsonRpcMessage.ErrorResponse>(error);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, err.Error.Code);
    }

    [Fact]
    public void RejectsMalformedMessages()
    {
        // 缺 method 的对象。
        Assert.Throws<McpError>(() => JsonRpc.ParseText("""{"jsonrpc":"2.0","id":"1"}"""));
        // result 与 error 并存。
        Assert.Throws<McpError>(() =>
            JsonRpc.ParseText("""{"jsonrpc":"2.0","id":1,"result":{},"error":{"code":1,"message":"x"}}"""));
        // jsonrpc 版本不符。
        Assert.Throws<McpError>(() => JsonRpc.ParseText("""{"jsonrpc":"1.0","id":1,"method":"x"}"""));
        // JSON 语法错误 → parseError。
        var error = Assert.Throws<McpError>(() => JsonRpc.ParseText("{oops"));
        Assert.Equal(JsonRpcErrorCodes.ParseError, error.Code);
    }

    [Fact]
    public void RoundTripsThroughSerialization()
    {
        var request = new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(42), "tools/call",
            JsonNode.Parse("""{"name":"read_file","arguments":{"path":"a.txt"}}"""));
        var parsed = Assert.IsType<JsonRpcMessage.Request>(JsonRpc.Parse(SerializeToJsonNode(request)));
        Assert.Equal(42L, Assert.IsType<long>(parsed.Id.Value));
        Assert.Equal("tools/call", parsed.Method);

        var errorResponse = new JsonRpcMessage.ErrorResponse(
            JsonRpcId.FromString("abc"), new JsonRpcErrorObject(JsonRpcErrorCodes.InternalError, "boom"));
        var parsedError = Assert.IsType<JsonRpcMessage.ErrorResponse>(JsonRpc.Parse(SerializeToJsonNode(errorResponse)));
        Assert.Equal("abc", parsedError.Id.Value);
        Assert.Equal("boom", parsedError.Error.Message);
    }

    [Fact]
    public void IsPredicatesDiscriminateMessages()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"m"}""");
        Assert.True(JsonRpc.IsRequest(request));
        Assert.False(JsonRpc.IsNotification(request));
        Assert.False(JsonRpc.IsResponse(request));

        var notification = JsonNode.Parse("""{"jsonrpc":"2.0","method":"m"}""");
        Assert.True(JsonRpc.IsNotification(notification));
        Assert.False(JsonRpc.IsRequest(notification));
    }

    private static JsonNode SerializeToJsonNode(JsonRpcMessage message) => JsonRpc.Serialize(message);
}
