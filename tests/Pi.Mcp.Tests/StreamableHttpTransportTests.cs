using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// Streamable HTTP 传输测试：用 HttpListener 充当 MCP 服务器端点，
/// 验证 JSON 应答、会话 id 捕获、SSE 应答解析与错误处理。
/// </summary>
public class StreamableHttpTransportTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();

    public StreamableHttpTransportTests()
    {
        var port = TcpPort();
        _url = $"http://127.0.0.1:{port}/mcp";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        _listener.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>服务器行为：initialize 返回 JSON + 会话头；notifications 返回 202；tools/list 返回 SSE。</summary>
    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = JsonRpc.ParseText(body);
            var response = context.Response;

            switch (message)
            {
                case JsonRpcMessage.Request { Method: "initialize" } request:
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.Headers.Add("Mcp-Session-Id", "session-123");
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                        JsonRpc.Serialize(new JsonRpcMessage.SuccessResponse(request.Id, JsonNode.Parse(
                            """{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"http","version":"1"}}"""
                        ))).ToJsonString()));
                    break;

                case JsonRpcMessage.Request { Method: "tools/list" } listRequest:
                    // 以 SSE 流应答：一个 event 块承载 JSON-RPC 响应。
                    response.StatusCode = 200;
                    response.ContentType = "text/event-stream";
                    var sse = "event: message\ndata: " + JsonRpc.Serialize(
                        new JsonRpcMessage.SuccessResponse(listRequest.Id, JsonNode.Parse(
                            """{"tools":[{"name":"ping_tool","inputSchema":{"type":"object"}}]}"""
                        ))).ToJsonString() + "\n\n";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sse));
                    break;

                case JsonRpcMessage.Notification:
                    response.StatusCode = 202;
                    break;

                default:
                    response.StatusCode = 400;
                    break;
            }
            response.Close();
        }
        catch
        {
            // 客户端提前断开等：忽略。
        }
    }

    [Fact]
    public async Task PostJsonRequestCapturesSessionAndParsesJsonResponse()
    {
        var transport = new StreamableHttpTransport(new StreamableHttpTransportOptions
        {
            Url = _url,
            OpenGetStream = false,
        });
        await transport.StartAsync();

        var messages = new List<JsonRpcMessage>();
        using var subscription = transport.OnMessage(messages.Add);

        var initialize = new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(1), "initialize", JsonNode.Parse("""{"clientInfo":{"name":"c","version":"1"}}"""));
        await transport.SendAsync(initialize);

        var response = Assert.IsType<JsonRpcMessage.SuccessResponse>(Assert.Single(messages));
        Assert.Equal("http", ((JsonObject)response.Result!)["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("session-123", transport.SessionId);
    }

    [Fact]
    public async Task SseResponseIsParsedIntoMessages()
    {
        var transport = new StreamableHttpTransport(new StreamableHttpTransportOptions
        {
            Url = _url,
            OpenGetStream = false,
        });
        await transport.StartAsync();
        var messages = new List<JsonRpcMessage>();
        using var subscription = transport.OnMessage(messages.Add);

        await transport.SendAsync(new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(2), "tools/list", null));

        var response = Assert.IsType<JsonRpcMessage.SuccessResponse>(Assert.Single(messages));
        var tools = Assert.IsType<JsonArray>(response.Result!["tools"]);
        Assert.Equal("ping_tool", tools[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task NotificationSendsExpect202Ack()
    {
        var transport = new StreamableHttpTransport(new StreamableHttpTransportOptions
        {
            Url = _url,
            OpenGetStream = false,
        });
        await transport.StartAsync();
        // 通知无应答：只要不抛错即通过（服务器回 202）。
        await transport.SendAsync(new JsonRpcMessage.Notification("notifications/initialized", null));
    }

    private static int TcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
