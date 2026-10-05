using System.Net;
using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>Google Generative AI / Vertex REST 核心测试（P31 全事件族实现）。</summary>
public class GoogleGenerativeAiTests
{
    private static ModelSpec Model(
        string id = "gemini-2.5-pro",
        string api = "google-generative-ai",
        bool reasoning = true,
        string baseUrl = "")
        => new()
        {
            Id = id, Name = id, Api = api, Provider = "google", BaseUrl = baseUrl,
            Reasoning = reasoning, ContextWindow = 1_000_000, MaxTokens = 8192,
        };

    private static AssistantMessage Assistant(params ContentBlock[] blocks)
        => new([.. blocks], StopReason.Stop,
            Api: "google-generative-ai", Provider: "google", Model: "gemini-2.5-pro");

    [Fact]
    public void MapStopReasonCoversGoogleVerbs()
    {
        Assert.Equal(StopReason.Stop, GoogleShared.MapStopReason("STOP"));
        Assert.Equal(StopReason.Length, GoogleShared.MapStopReason("MAX_TOKENS"));
        Assert.Equal(StopReason.Error, GoogleShared.MapStopReason("SAFETY"));
        Assert.Equal(StopReason.Error, GoogleShared.MapStopReason("MALFORMED_FUNCTION_CALL"));
        Assert.Equal(StopReason.Error, GoogleShared.MapStopReason("RECITATION"));
        Assert.Equal(StopReason.Error, GoogleShared.MapStopReasonString("unknown"));
        Assert.Equal(StopReason.Error, GoogleShared.MapStopReasonString("SAFETY"));
    }

    [Fact]
    public void ConvertMessagesSeparatesSystemAndBuildsParts()
    {
        var model = Model();
        var context = new TranscriptContext(
        [
            new SystemMessage("你是助手"),
            new UserMessage([new TextContent("你好")], 1),
            Assistant(new ThinkingContent("内部思考"), new TextContent("回答")),
            new ToolResultMessage("call-1", "read", [new TextContent("文件内容")]),
        ]);
        var (systemInstruction, contents) = GoogleShared.ConvertMessages(model, context);

        Assert.Equal("你是助手", systemInstruction);
        // user / model / user(functionResponse)。
        Assert.Equal(3, contents.Count);

        var modelTurn = (System.Text.Json.Nodes.JsonObject)contents[1]!;
        Assert.Equal("model", modelTurn.Str("role"));
        var parts = (System.Text.Json.Nodes.JsonArray)modelTurn["parts"]!;
        // thinking part 带 thought:true。
        Assert.True(((System.Text.Json.Nodes.JsonObject)parts[0]!)["thought"]!.GetValue<bool>());
        Assert.Equal("回答", ((System.Text.Json.Nodes.JsonObject)parts[1]!)["text"]!.GetValue<string>());

        var responseTurn = (System.Text.Json.Nodes.JsonObject)contents[2]!;
        var responsePart = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)responseTurn["parts"]!)[0]!;
        Assert.Equal("read", responsePart["functionResponse"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertMessagesDropsThinkingAcrossModels()
    {
        var model = Model();
        var crossModel = new AssistantMessage(
            [new ThinkingContent("别人的思考"), new TextContent("回答")],
            StopReason.Stop, Provider: "anthropic", Model: "claude-x");
        var context = new TranscriptContext(
        [
            new UserMessage([new TextContent("你好")], 1),
            crossModel,
        ]);
        var (_, contents) = GoogleShared.ConvertMessages(model, context);
        var modelTurn = (System.Text.Json.Nodes.JsonObject)contents[1]!;
        var parts = (System.Text.Json.Nodes.JsonArray)modelTurn["parts"]!;
        // 跨模型 thinking 转纯文本（无 thought 标记）。
        Assert.Null(((System.Text.Json.Nodes.JsonObject)parts[0]!)["thought"]);
        Assert.Equal("别人的思考", ((System.Text.Json.Nodes.JsonObject)parts[0]!)["text"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertMessagesRequiresToolCallIdOnGemini3()
    {
        var gemini3 = Model("gemini-3-pro-preview");
        Assert.True(GoogleShared.RequiresToolCallId("gemini-3-pro-preview"));
        Assert.False(GoogleShared.RequiresToolCallId("gemini-2.5-pro"));
        Assert.True(GoogleShared.RequiresToolCallId("claude-sonnet-4"));
        var context = new TranscriptContext(
        [
            new UserMessage([new TextContent("hi")], 1),
            new AssistantMessage(
                [new ToolCallContent("raw|id", "search", new System.Text.Json.Nodes.JsonObject())],
                StopReason.ToolUse, Provider: "google", Model: "gemini-3-pro-preview"),
        ]);
        var (_, contents) = GoogleShared.ConvertMessages(gemini3, context);
        var modelTurn = (System.Text.Json.Nodes.JsonObject)contents[1]!;
        var part = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)modelTurn["parts"]!)[0]!;
        // 归一化后的 ID 进 functionCall.id。
        Assert.Equal("raw_id", part["functionCall"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void BuildParamsProducesRestWireShape()
    {
        var model = Model();
        var tools = new List<ToolDefinition>
        {
            new("read_file", "读取文件", new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" })),
        };
        var context = new TranscriptContext(
        [
            new SystemMessage("be brief", Timestamp: 0, ToolsAdded: tools),
            new UserMessage([new TextContent("hi")], 1),
        ]);
        var parameters = GoogleGenerativeAi.BuildParams(model, context, new GoogleOptions
        {
            MaxTokens = 128,
            Temperature = 0.5,
            Thinking = new GoogleThinkingControl { Enabled = false },
        });
        // REST 形状：systemInstruction/tools/toolConfig 与命名采样字段平级。
        var generationConfig = (System.Text.Json.Nodes.JsonObject)parameters["generationConfig"]!;
        Assert.Equal("be brief", generationConfig["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(128, generationConfig["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.5, generationConfig["temperature"]!.GetValue<double>());
        Assert.Null(parameters["model"]); // REST 不带 model 字段（URL 承载）
        // 2.5-pro 非 Gemini3 → 禁用思考为 thinkingBudget:0。
        Assert.Equal(0, generationConfig["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>());
        var toolsNode = (System.Text.Json.Nodes.JsonArray)generationConfig["tools"]!;
        var declaration = (System.Text.Json.Nodes.JsonObject)
            ((System.Text.Json.Nodes.JsonObject)toolsNode[0]!)["functionDeclarations"]![0]!;
        Assert.Equal("read_file", declaration.Str("name"));
        Assert.NotNull(declaration["parametersJsonSchema"]);
    }

    [Fact]
    public async Task StreamParsesSseChunksAndUsage()
    {
        using var listener = new HttpListener();
        var port = 25000 + Random.Shared.Next(3000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            var chunks = new[]
            {
                """{"candidates":[{"content":{"parts":[{"text":"你"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":1,"thoughtsTokenCount":2},"responseId":"resp-1"}""",
                """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"好！"}],"role":"model"}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":3}}""",
            };
            foreach (var chunk in chunks)
            {
                var bytes = Encoding.UTF8.GetBytes($"data: {chunk}\n\n");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                await ctx.Response.OutputStream.FlushAsync();
            }
            ctx.Response.OutputStream.Close();
        });

        var model = Model(baseUrl: $"http://127.0.0.1:{port}");
        var options = new SimpleStreamOptions(
            ApiKey: "test-key",
            BaseUrl: $"http://127.0.0.1:{port}");
        var stream = GoogleGenerativeAi.StreamSimple(
            model,
            new TranscriptContext([new UserMessage([new TextContent("打个招呼")], 1)]),
            options);

        var final = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal("你好！", string.Concat(
            final.Content.OfType<TextContent>().Select(t => t.Text)));
        Assert.Equal(5, final.UsageStats!.Input);
        Assert.Equal(3, final.UsageStats.Output);
        Assert.Equal("resp-1", final.ResponseId);
    }

    [Fact]
    public void ThinkingBudgetsFollowModelFamilies()
    {
        // 2.5-pro 预算表。
        Assert.Equal(128, GoogleGenerativeAi.GetGoogleBudget(Model("gemini-2.5-pro"), "minimal"));
        Assert.Equal(32768, GoogleGenerativeAi.GetGoogleBudget(Model("gemini-2.5-pro"), "high"));
        // 2.5-flash。
        Assert.Equal(24576, GoogleGenerativeAi.GetGoogleBudget(Model("gemini-2.5-flash"), "high"));
        // 自定义预算优先。
        Assert.Equal(999, GoogleGenerativeAi.GetGoogleBudget(Model("gemini-2.5-pro"), "low",
            new ThinkingBudgets(Low: 999)));
        // 其余模型 = 动态（-1）。
        Assert.Equal(-1, GoogleGenerativeAi.GetGoogleBudget(Model("gemini-1.5-pro"), "medium"));
    }

    [Fact]
    public void ThinkingLevelHelpers()
    {
        Assert.True(GoogleShared.UsesGoogleThinkingLevel(Model("gemini-3-flash-preview")));
        Assert.True(GoogleShared.UsesGoogleThinkingLevel(Model("gemini-3.1-pro-preview")));
        Assert.True(GoogleShared.UsesGoogleThinkingLevel(Model("gemma-4-27b")));
        Assert.False(GoogleShared.UsesGoogleThinkingLevel(Model("gemini-2.5-pro")));
        Assert.Equal("HIGH", GoogleShared.ToGoogleThinkingLevel("high"));
        Assert.Equal("MINIMAL", GoogleShared.ToGoogleThinkingLevel("minimal"));
        // 禁用思考：非 Gemini3 → budget 0；Gemini3 且 off 被禁用 → fallback 档位。
        // 非 Gemini3：thinkingBudget 0。
        var disabled25 = GoogleShared.GetDisabledGoogleThinkingConfig(Model("gemini-2.5-pro"));
        Assert.Equal(0, disabled25["thinkingBudget"]!.GetValue<int>());
        // Gemini3 且 off 被禁用：fallback 到最接近可用档位（minimal）。
        var disabled3 = GoogleShared.GetDisabledGoogleThinkingConfig(new ModelSpec
        {
            Id = "gemini-3-pro", Name = "g3", Api = "google-vertex", Provider = "google",
            BaseUrl = "", Reasoning = true,
            ThinkingLevelMap = Pi.Ai.Models.ThinkingLevelMap.FromJsonObject(
                new System.Text.Json.Nodes.JsonObject { ["off"] = null }),
        });
        Assert.Equal("MINIMAL", disabled3["thinkingLevel"]!.GetValue<string>());
        // functionCalling 模式：strict 采样（工具声明 constrainedSampling=json_schema）要求 VALIDATED。
        var strictTool = new ToolDefinition("t", "d", new ToolSchema(new Dictionary<string, object?> { ["type"] = "object", ["properties"] = new Dictionary<string, object?>() }))
        {
            ConstrainedSampling = new System.Text.Json.Nodes.JsonObject { ["type"] = "json_schema" },
        };
        Assert.Equal("VALIDATED", GoogleShared.ResolveGoogleFunctionCallingMode(
            [strictTool], null, supportsStrictMode: true));
        Assert.Equal("ANY", GoogleShared.ResolveGoogleFunctionCallingMode([], "any", supportsStrictMode: true));
        Assert.Null(GoogleShared.ResolveGoogleFunctionCallingMode([], null, supportsStrictMode: true));
    }

    [Fact]
    public async Task RetryPolicyRetriesRetryableStatuses()
    {
        var attempts = 0;
        var result = await ProviderRetry.RetryAsync(async () =>
        {
            attempts++;
            if (attempts < 3) throw new ProviderHttpException(429, "rate limited");
            return "ok";
        }, new ProviderRetryOptions { MaxRetries = 3, MaxRetryDelayMs = 10 });
        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);

        // 不可重试状态立即抛出。
        attempts = 0;
        await Assert.ThrowsAsync<ProviderHttpException>(() => ProviderRetry.RetryAsync<object?>(async () =>
        {
            attempts++;
            throw new ProviderHttpException(400, "bad request");
        }, new ProviderRetryOptions { MaxRetries = 3 }));
        Assert.Equal(1, attempts);
    }
}
