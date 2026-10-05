using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// OpenAI Chat Completions provider 测试：HttpListener 模拟 /v1/chat/completions
/// 的 SSE 流式应答，验证 delta 事件映射、工具调用聚合与错误编码。
/// </summary>
public class OpenAiCompletionsTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();

    public OpenAiCompletionsTests()
    {
        var port = TcpPort();
        _baseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/chat/completions/");
        _listener.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        GC.SuppressFinalize(this);
    }

    /// <summary>模拟服务器：按脚本回放 SSE chunk 序列。</summary>
    private readonly List<string[]> _scripts = [];

    public OpenAiCompletionsTests AddScript(params string[] chunks)
    {
        _scripts.Add(chunks);
        return this;
    }

    private async Task ServeAsync()
    {
        var scriptIndex = 0;
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }
            try
            {
                var chunks = scriptIndex < _scripts.Count ? _scripts[scriptIndex++] : ["[DONE]"];
                var response = context.Response;
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                foreach (var chunk in chunks)
                {
                    var payload = chunk == "[DONE]" ? "[DONE]" : $"data: {chunk}";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n\n"));
                }
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                response.Close();
            }
            catch { /* 客户端断开 */ }
        }
    }

    private static string Chunk(int id, string? content = null, string? finish = null, JsonObject? usage = null)
    {
        var delta = new JsonObject();
        if (content is not null) delta["content"] = content;
        var chunk = new JsonObject
        {
            ["id"] = $"chatcmpl-{id}",
            ["object"] = "chat.completion.chunk",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finish,
            }),
        };
        if (usage is not null) chunk["usage"] = usage;
        return chunk.ToJsonString();
    }

    private static string ToolCallChunk(int id, int index, string? callId, string? name, string? args, string? finish = null)
    {
        var function = new JsonObject();
        if (name is not null) function["name"] = name;
        if (args is not null) function["arguments"] = args;
        var delta = new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["index"] = index,
                ["id"] = callId,
                ["type"] = "function",
                ["function"] = function,
            }),
        };
        return new JsonObject
        {
            ["id"] = $"chatcmpl-{id}",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finish,
            }),
        }.ToJsonString();
    }

    [Fact]
    public async Task StreamsTextDeltasAndCompletesWithUsage()
    {
        AddScript(
            Chunk(1, content: "你"),
            Chunk(1, content: "好"),
            new JsonObject
            {
                ["id"] = "chatcmpl-1",
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 2 },
            }.ToJsonString(),
            "[DONE]");

        var model = new Model("gpt-test", "GPT Test", "openai-completions", "openai");
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var stream = await OpenAiCompletions.StreamSimple(model, context,
            new SimpleStreamOptions(ApiKey: "sk-test", BaseUrl: _baseUrl));

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

        Assert.Equal(["你", "好"], deltas);
        Assert.NotNull(final);
        Assert.Equal("你好", ((TextContent)final!.Content[0]).Text);
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal(10, final.UsageStats?.Input);
        Assert.Equal(2, final.UsageStats?.Output);
    }

    [Fact]
    public async Task AggregatesToolCallArgumentFragments()
    {
        AddScript(
            ToolCallChunk(1, 0, "call-1", "read", "{\"pa"),
            ToolCallChunk(2, 0, null, null, "th\": \"a.txt\"}"),
            Chunk(2, finish: "tool_calls"));

        var model = new Model("gpt-test", "GPT Test", "openai-completions", "openai");
        var context = new TranscriptContext([Messages.UserText("read it")], null);
        var stream = await OpenAiCompletions.StreamSimple(model, context,
            new SimpleStreamOptions(ApiKey: "sk-test", BaseUrl: _baseUrl));

        AssistantMessage? final = null;
        await foreach (var @event in stream)
            if (@event is AssistantMessageEvent.Done done) final = done.Message;

        Assert.NotNull(final);
        Assert.Equal(StopReason.ToolUse, final!.StopReason);
        var call = Assert.IsType<ToolCallContent>(final.Content[0]);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read", call.Name);
        Assert.Equal("a.txt", ((System.Text.Json.Nodes.JsonObject)call.Arguments!)["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task HttpFailureEncodesErrorInStream()
    {
        // 无脚本 → 服务器返回 [DONE]（空流）；这里换成构造非法场景：
        // 直接用一个错误端口验证网络失败路径。
        var model = new Model("gpt-test", "GPT Test", "openai-completions", "openai");
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var stream = await OpenAiCompletions.StreamSimple(model, context,
            new SimpleStreamOptions(ApiKey: "sk", BaseUrl: "http://127.0.0.1:9/v1"));

        AssistantMessage? final = null;
        await foreach (var @event in stream)
            if (@event is AssistantMessageEvent.Done done) final = done.Message;

        Assert.NotNull(final);
        Assert.Equal(StopReason.Error, final!.StopReason);
        Assert.NotNull(final.ErrorMessage);
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
