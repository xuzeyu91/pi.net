using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Anthropic Messages provider 测试：HttpListener 模拟 /v1/messages 的 SSE 应答，
/// 验证 content_block 事件模型映射、tool_use 聚合与 stop_reason 映射。
/// </summary>
public class AnthropicMessagesTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();

    public AnthropicMessagesTests()
    {
        var port = TcpPort();
        _baseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/messages/");
        _listener.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>收到的请求参数（供断言消息转换）。</summary>
    public string? LastRequestBody { get; private set; }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                LastRequestBody = await reader.ReadToEndAsync();
                var response = context.Response;
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";

                var events = new[]
                {
                    "event: message_start\ndata: " + """{"type":"message_start","message":{"usage":{"input_tokens":7,"output_tokens":0}}}""" + "\n\n",
                    "event: content_block_start\ndata: " + """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""" + "\n\n",
                    "event: content_block_delta\ndata: " + """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"晚"}}""" + "\n\n",
                    "event: content_block_delta\ndata: " + """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"安"}}""" + "\n\n",
                    "event: content_block_stop\ndata: " + """{"type":"content_block_stop","index":0}""" + "\n\n",
                    "event: message_delta\ndata: " + """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}""" + "\n\n",
                    "event: message_stop\ndata: " + """{"type":"message_stop"}""" + "\n\n",
                };
                foreach (var @event in events)
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(@event));
                response.Close();
            }
            catch { /* 客户端断开 */ }
        }
    }

    [Fact]
    public async Task StreamsAnthropicContentBlocksAndMapsStopReason()
    {
        var model = new Model("claude-test", "Claude Test", "anthropic-messages", "anthropic");
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var stream = await AnthropicMessages.StreamSimple(model, context,
            new SimpleStreamOptions(ApiKey: "sk-ant", BaseUrl: _baseUrl));

        var deltas = new List<string>();
        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            switch (@event)
            {
                case AssistantMessageEvent.TextDelta delta: deltas.Add(delta.Delta); break;
                case AssistantMessageEvent.Done done: final = done.Message; break;
            }
        }

        Assert.Equal(["晚", "安"], deltas);
        Assert.NotNull(final);
        Assert.Equal("晚安", ((TextContent)final!.Content[0]).Text);
        Assert.Equal(StopReason.Stop, final.StopReason); // end_turn → Stop
        Assert.Equal(7, final.UsageStats?.Input);
        Assert.Equal(2, final.UsageStats?.Output);
        // system 字段独立于 messages（Anthropic 协议）。
        Assert.NotNull(LastRequestBody);
        Assert.DoesNotContain("\"role\":\"system\"", LastRequestBody);
    }

    [Fact]
    public void ConvertMessagesUsesToolResultBlocksInsideUserMessages()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("be brief"),
            Messages.UserText("read it"),
            new AssistantMessage(
                [new ToolCallContent("call-1", "read_file", new { path = "a.txt" })], StopReason.ToolUse),
            Messages.ToolResult("call-1", "read_file", "contents", isError: false),
        };

        var converted = AnthropicMessages.ConvertMessages(messages.Where(m => m is not SystemMessage).ToList());

        Assert.Equal(3, converted.Count);
        var assistant = (JsonObject)converted[1]!;
        var toolUse = (JsonArray)assistant["content"]!;
        Assert.Equal("tool_use", toolUse[0]!["type"]!.GetValue<string>());
        var user = (JsonObject)converted[2]!;
        var toolResult = (JsonArray)user["content"]!;
        Assert.Equal("tool_result", toolResult[0]!["type"]!.GetValue<string>());
        Assert.Equal("call-1", toolResult[0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public void MapsStopReasons()
    {
        Assert.Equal(StopReason.Stop, AnthropicMessages.MapStopReason("end_turn"));
        Assert.Equal(StopReason.Length, AnthropicMessages.MapStopReason("max_tokens"));
        Assert.Equal(StopReason.ToolUse, AnthropicMessages.MapStopReason("tool_use"));
        Assert.Equal(StopReason.Stop, AnthropicMessages.MapStopReason("stop_sequence"));
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
