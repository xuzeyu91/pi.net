using System.Net;
using System.Text;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>google-generative-ai REST 核心测试（消息转换 + SSE 流 + stop 映射）。</summary>
public class GoogleGenerativeAiTests
{
    [Fact]
    public void MapStopReasonCoversGoogleVerbs()
    {
        Assert.Equal(StopReason.Stop, GoogleGenerativeAi.MapStopReason("STOP"));
        Assert.Equal(StopReason.Length, GoogleGenerativeAi.MapStopReason("MAX_TOKENS"));
        Assert.Equal(StopReason.ToolUse, GoogleGenerativeAi.MapStopReason("functionCall"));
        Assert.Equal(StopReason.Aborted, GoogleGenerativeAi.MapStopReason("SAFETY"));
        Assert.Equal(StopReason.Stop, GoogleGenerativeAi.MapStopReason(null));
    }

    [Fact]
    public void ConvertMessagesSeparatesSystemAndBuildsParts()
    {
        var context = new List<ChatMessage>
        {
            new SystemMessage("你是助手"),
            Messages.UserText("你好"),
            new AssistantMessage([new ThinkingContent("内部思考"), new TextContent("回答")]),
            new ToolResultMessage("call-1", "read", [new TextContent("文件内容")]),
        };
        var (systemInstruction, contents) = GoogleGenerativeAi.ConvertMessages(context);

        Assert.Equal("你是助手", systemInstruction);
        // user / model / user(functionResponse)。
        Assert.Equal(3, contents.Count);

        var modelTurn = (System.Text.Json.Nodes.JsonObject)contents[1]!;
        Assert.Equal("model", modelTurn["role"]!.GetValue<string>());
        var parts = (System.Text.Json.Nodes.JsonArray)modelTurn["parts"]!;
        // thinking part 带 thought:true。
        Assert.True(((System.Text.Json.Nodes.JsonObject)parts[0]!)["thought"]!.GetValue<bool>());
        Assert.Equal("回答", ((System.Text.Json.Nodes.JsonObject)parts[1]!)["text"]!.GetValue<string>());

        var responseTurn = (System.Text.Json.Nodes.JsonObject)contents[2]!;
        var responsePart = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)responseTurn["parts"]!)[0]!;
        Assert.Equal("read", responsePart["functionResponse"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildParamsInjectsTools()
    {
        var tools = new List<ToolDefinition>
        {
            new("read_file", "读取文件", new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" })),
        };
        var parameters = GoogleGenerativeAi.BuildParams(
            [Messages.UserText("列出文件")], tools);
        // 无 system 消息时无 systemInstruction 字段（Gemini 前导 system 才注入）。
        Assert.Null(parameters["systemInstruction"]);
        Assert.NotNull(parameters["tools"]);
        var declarations = (System.Text.Json.Nodes.JsonArray)
            ((System.Text.Json.Nodes.JsonObject)parameters["tools"]![0]!)["functionDeclarations"]!;
        Assert.Equal("read_file", ((System.Text.Json.Nodes.JsonObject)declarations[0]!)["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamSimpleParsesSseChunksAndUsage()
    {
        using var listener = new HttpListener();
        var port = 22000 + Random.Shared.Next(3000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            var chunks = new[]
            {
                """{"candidates":[{"content":{"parts":[{"text":"你"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1}}""",
                """{"candidates":[{"content":{"parts":[{"text":"好"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2}}""",
                """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"！"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":3}}""",
            };
            foreach (var chunk in chunks)
            {
                var bytes = Encoding.UTF8.GetBytes($"data: {chunk}\n\n");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                await ctx.Response.OutputStream.FlushAsync();
            }
            ctx.Response.OutputStream.Close();
        });

        var model = new Model("gemini-2.5-pro", "Gemini 2.5 Pro", "google-generative-ai", "google");
        var options = new SimpleStreamOptions(
            ApiKey: "test-key",
            BaseUrl: $"http://127.0.0.1:{port}");
        var stream = GoogleGenerativeAi.StreamSimple(model,
            new TranscriptContext([Messages.UserText("打个招呼")]), options);

        var events = new List<AssistantMessageEvent>();
        await foreach (var e in stream) events.Add(e);
        var done = events.OfType<AssistantMessageEvent.Done>().Single();
        Assert.Equal(StopReason.Stop, done.Message.StopReason);
        Assert.Equal("你好！", string.Concat(
            done.Message.Content.OfType<TextContent>().Select(t => t.Text)));
        Assert.Equal(5, done.Message.UsageStats!.Input);
        Assert.Equal(3, done.Message.UsageStats.Output);
    }
}
