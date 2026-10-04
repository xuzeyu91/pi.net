using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// MCP 客户端端到端测试：以 InMemoryTransport 配对 + 一个脚本化的"模拟服务器"
/// 验证 initialize 握手、工具列举与调用、通知与 ping 的完整链路。
/// </summary>
public class McpClientTests
{
    /// <summary>极简模拟服务器：对入站请求按方法名应答。</summary>
    private static async Task RunFakeServer(InMemoryTransport serverTransport, CancellationToken ct)
    {
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            _ = Task.Run(async () =>
            {
                switch (message)
                {
                    case JsonRpcMessage.Request request:
                    {
                        JsonNode? result;
                        switch (request.Method)
                        {
                            case "initialize":
                                result = JsonNode.Parse("""
                                    {
                                        "protocolVersion": "2025-11-25",
                                        "capabilities": {"tools": {}},
                                        "serverInfo": {"name": "fake-server", "version": "1.0.0"}
                                    }
                                    """);
                                break;
                            case "tools/list":
                                result = JsonNode.Parse("""
                                    {"tools":[{"name":"read_file","description":"读文件","inputSchema":{"type":"object"}}]}
                                    """);
                                break;
                            case "tools/call":
                                result = JsonNode.Parse(
                                    """{"content":[{"type":"text","text":"文件内容"}],"isError":false}""");
                                break;
                            case "ping":
                                result = new JsonObject();
                                break;
                            default:
                                await serverTransport.SendAsync(new JsonRpcMessage.ErrorResponse(
                                    request.Id,
                                    new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found")));
                                return;
                        }
                        await serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id, result));
                        break;
                    }

                    case JsonRpcMessage.Notification { Method: "notifications/initialized" }:
                        // 模拟服务器收到 initialized 后主动发一条通知。
                        await serverTransport.SendAsync(new JsonRpcMessage.Notification(
                            "notifications/message", JsonNode.Parse("""{"level":"info","data":"ready"}""")));
                        break;
                }
            });
        });

        while (!ct.IsCancellationRequested)
            await Task.Delay(10, ct);
    }

    [Fact]
    public async Task FullClientLifecycleOverInMemoryTransport()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        using var serverCts = new CancellationTokenSource();
        _ = RunFakeServer(serverTransport, serverCts.Token);

        var client = new McpClient(new McpClientOptions { Name = "test-client", Version = "0.1.0" });
        var notifications = new List<string>();
        client.OnNotification("notifications/message", parameters =>
        {
            notifications.Add(parameters?["data"]?.GetValue<string>() ?? "");
            return Task.CompletedTask;
        });

        var result = await client.ConnectAsync(clientTransport);

        // 握手结果。
        Assert.Equal("2025-11-25", result.ProtocolVersion);
        Assert.Equal("fake-server", result.ServerInfo.Name);
        Assert.Equal(ClientState.Connected, client.State);
        Assert.Equal("fake-server", client.ServerInfo?.Name);

        // ping。
        await client.PingAsync();

        // 工具列举与调用。
        var tools = await client.ListToolsAsync();
        var tool = Assert.Single(tools);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("读文件", tool.Description);

        var callResult = await client.CallToolAsync("read_file",
            new JsonObject { ["path"] = "a.txt" });
        Assert.False(callResult.IsError);
        Assert.Equal("文件内容", callResult.Content[0].Raw["text"]?.GetValue<string>());

        // 模拟服务器在 initialized 通知后推送的通知被接收。
        await Task.Delay(50);
        Assert.Contains("ready", notifications);

        // 关闭后状态落定。
        await client.CloseAsync();
        Assert.Equal(ClientState.Closed, client.State);
        serverCts.Cancel();
    }

    [Fact]
    public async Task ConnectRejectsUnsupportedProtocolVersion()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        using var serverCts = new CancellationTokenSource();
        // 模拟服务器选择一个不支持的版本。
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            if (message is JsonRpcMessage.Request { Method: "initialize" } request)
            {
                _ = serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id,
                    JsonNode.Parse("""
                        {"protocolVersion":"1999-01-01","capabilities":{},"serverInfo":{"name":"old","version":"0"}}
                        """)));
            }
        });

        var client = new McpClient(new McpClientOptions { Name = "c", Version = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(clientTransport));
        // 握手失败后自动关闭。
        Assert.Equal(ClientState.Closed, client.State);
        serverCts.Cancel();
    }
}
