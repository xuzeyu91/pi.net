using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>OpenAI Responses API 测试。对应 TS openai-responses 系列测试的核心用例。</summary>
public class OpenAiResponsesTests
{
    private static ModelSpec ResponsesModel(bool reasoning = true, string provider = "openai") => new()
    {
        Id = "gpt-5.1", Name = "GPT-5.1", Api = "openai-responses", Provider = provider,
        BaseUrl = "https://api.openai.com/v1", Input = ["text", "image"], Reasoning = reasoning,
        ContextWindow = 400_000, MaxTokens = 128_000,
        Cost = new ModelCostRates(1.25, 10.00, CacheRead: 0.125),
    };

    private static TranscriptContext Context(params ChatMessage[] messages) => new(messages.ToList());

    private static ToolDefinition Tool(string name) => new(name, "do it", new ToolSchema(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
        },
        ["required"] = new List<object?> { "path" },
    }));

    // =============================================================================
    // convertResponsesMessages
    // =============================================================================

    [Fact]
    public void ConvertsUserTextAndImages()
    {
        var context = Context(
            new UserMessage([new TextContent("look"), new ImageContent("QUJD", "image/png")], 1));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet());
        Assert.Single(input);
        var item = (JsonObject)input[0]!;
        Assert.Equal("user", item.Str("role"));
        var content = item["content"]!.AsArray();
        Assert.Equal("input_text", ((JsonObject)content[0]!).Str("type"));
        Assert.Equal("input_image", ((JsonObject)content[1]!).Str("type"));
        Assert.Equal("data:image/png;base64,QUJD", ((JsonObject)content[1]!).Str("image_url"));
    }

    [Fact]
    public void CollapsesMidConvoSystemMessagesByDefault()
    {
        var context = Context(
            new SystemMessage("base", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1),
            new SystemMessage("mid update", Timestamp: 2));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet());
        Assert.Equal(2, input.Count); // system(折叠) + user；中段 system 被吸收
        var head = (JsonObject)input[0]!;
        Assert.Equal("developer", head.Str("role")); // reasoning 模型用 developer 角色
        Assert.Contains("mid update", head.Str("content"));
    }

    [Fact]
    public void KeepsMidConvoSystemMessagesWhenSupported()
    {
        var context = Context(
            new SystemMessage("base", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1),
            new SystemMessage("mid update", Timestamp: 2));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet(),
            new ConvertResponsesMessagesOptions { SupportsMidConvoSystemMessages = true });
        Assert.Equal(3, input.Count);
        Assert.Equal("mid update", ((JsonObject)input[2]!).Str("content"));
    }

    [Fact]
    public void NormalizesToolCallIdsForOpenAiProvider()
    {
        var context = Context(
            new AssistantMessage(
                [new ToolCallContent("call_abc|rs_1|fc_9", "search", new JsonObject { ["path"] = "x" })],
                StopReason.ToolUse, Model: "gpt-5.1", Api: "openai-responses", Provider: "openai", Timestamp: 1),
            new ToolResultMessage("call_abc|rs_1|fc_9", "search", [new TextContent("ok")], Timestamp: 2));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet());

        var functionCall = (JsonObject)input[0]!;
        Assert.Equal("function_call", functionCall.Str("type"));
        var callId = functionCall.Str("call_id")!;
        // 同模型消息不做 ID 归一化，且 item id 非 fc_ 前缀时整体省略（TS 一致）。
        Assert.Null(functionCall.Str("id"));
        Assert.DoesNotContain("|", callId);
        // 工具结果的 call_id 与调用对齐
        var output = (JsonObject)input[1]!;
        Assert.Equal("function_call_output", output.Str("type"));
        Assert.Equal(callId, output.Str("call_id"));
    }

    [Fact]
    public void StripsThoughtSignaturesForForeignToolCalls()
    {
        var context = Context(
            new AssistantMessage(
                [new ToolCallContent("call-1", "search", new JsonObject()) { ThoughtSignature = "sig" }],
                StopReason.ToolUse, Model: "gemini", Api: "google-generative-ai", Provider: "google", Timestamp: 1));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet());
        Assert.Equal("function_call", ((JsonObject)input[0]!).Str("type"));
    }

    [Fact]
    public void ReplaysReasoningItemsFromSignature()
    {
        var reasoningItem = new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JsonArray() };
        var context = Context(
            new AssistantMessage(
                [new ThinkingContent("") { Signature = reasoningItem.ToJsonString() },
                 new TextContent("final answer")],
                Model: "gpt-5.1", Api: "openai-responses", Provider: "openai", Timestamp: 1));
        var input = OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(), context, OpenAiToolCallProvidersSet());
        Assert.Equal("reasoning", ((JsonObject)input[0]!).Str("type"));
        Assert.Equal("message", ((JsonObject)input[1]!).Str("type"));
        // 文本兜底 id：msg_pi_<index>
        Assert.Equal("msg_pi_0", ((JsonObject)input[1]!).Str("id"));
    }

    [Fact]
    public void ConvertsToolsWithStrictMode()
    {
        var tools = OpenAiResponsesShared.ConvertResponsesTools(
            [Tool("search")], new ConvertResponsesToolsOptions { SupportsStrictMode = true, Strict = false });
        var tool = (JsonObject)tools[0]!;
        Assert.Equal("function", tool.Str("type"));
        Assert.Equal("search", tool.Str("name"));
        Assert.Equal(false, tool.Bool("strict"));
        Assert.NotNull(tool["parameters"]);
    }

    private static IReadOnlySet<string> OpenAiToolCallProvidersSet() => OpenAiResponses.ProviderIdSet;

    // =============================================================================
    // processResponsesStream
    // =============================================================================

    private static async IAsyncEnumerable<AiSseEvent> Sse(params string[] payloads)
    {
        foreach (var payload in payloads)
        {
            yield return new AiSseEvent(payload);
        }
    }

    private static JsonObject ResponseCompleted(JsonObject? usage = null, string status = "completed") => new()
    {
        ["type"] = "response.completed",
        ["response"] = new JsonObject
        {
            ["id"] = "resp_1",
            ["status"] = status,
            ["usage"] = usage ?? new JsonObject
            {
                ["input_tokens"] = 100,
                ["output_tokens"] = 20,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 40, ["cache_write_tokens"] = 0 },
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 5 },
                ["total_tokens"] = 120,
            },
        },
    };

    [Fact]
    public async Task ProcessesTextStreamToDone()
    {
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_1"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}""",
            """{"type":"response.output_text.delta","output_index":0,"delta":"Hello"}""",
            """{"type":"response.output_text.delta","output_index":0,"delta":" world"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hello world","annotations":[]}]}}""",
            ResponseCompleted().ToJsonString());

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        await OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel());
        stream.End(output.Snapshot()); // 正式门面负责 End；测试手动收尾

        var eventsSeen = new List<AssistantMessageEvent>();
        await foreach (var e in stream) eventsSeen.Add(e);
        Assert.Equal(
            ["TextStart", "TextDelta", "TextDelta", "TextEnd"],
            eventsSeen.Select(e => e.GetType().Name).ToArray());

        var final = output.Snapshot();
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal("Hello world", final.Content.OfType<TextContent>().Single().Text);
        Assert.Equal("msg_1", final.Content.OfType<TextContent>().Single().TextSignature is { } sig
            ? ((JsonNode.Parse(sig) as JsonObject)?.Str("id")) : null);
        Assert.Equal("resp_1", final.ResponseId);
        // usage：input = 100 - 40 = 60
        Assert.Equal(60, final.UsageStats!.Input);
        Assert.Equal(20, final.UsageStats.Output);
        Assert.Equal(40, final.UsageStats.CacheRead);
        Assert.Equal(5, final.UsageStats.Reasoning);
        // 成本 = 1.25/1M×60 + 10/1M×20 + 0.125/1M×40
        Assert.Equal(1.25 / 1e6 * 60 + 10 / 1e6 * 20 + 0.125 / 1e6 * 40, final.UsageStats.Cost!.Value, 12);
    }

    [Fact]
    public async Task ProcessesToolCallStreamToToolUse()
    {
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_2"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","id":"fc_1","name":"search","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"\"a.txt\"}"}""",
            """{"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"path\":\"a.txt\"}"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"call_1","id":"fc_1","name":"search","arguments":"{\"path\":\"a.txt\"}"}}""",
            ResponseCompleted().ToJsonString());

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        await OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel());
        stream.End(output.Snapshot());

        var eventsSeen = new List<AssistantMessageEvent>();
        await foreach (var e in stream) eventsSeen.Add(e);
        Assert.Equal(
            ["ToolCallStart", "ToolCallDelta", "ToolCallDelta", "ToolCallEnd"],
            eventsSeen.Select(e => e.GetType().Name).ToArray());

        var final = output.Snapshot();
        Assert.Equal(StopReason.ToolUse, final.StopReason);
        var toolCall = final.Content.OfType<ToolCallContent>().Single();
        Assert.Equal("call_1|fc_1", toolCall.Id);
        Assert.Equal("search", toolCall.Name);
        Assert.Equal("a.txt", ((JsonObject)toolCall.Arguments!).Str("path"));
    }

    [Fact]
    public async Task ReasoningStreamCarriesSignatureForReplay()
    {
        var reasoningItem = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_1",
            ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "thinking hard" }),
            ["encrypted_content"] = "enc-1",
        };
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_3"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1"}}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"thinking "}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":0,"delta":"hard"}""",
            new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = reasoningItem.DeepClone() }.ToJsonString(),
            ResponseCompleted().ToJsonString());

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        await OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel());
        stream.End(output.Snapshot());

        var eventsSeen = new List<AssistantMessageEvent>();
        await foreach (var e in stream) eventsSeen.Add(e);
        Assert.Equal(
            ["ThinkingStart", "ThinkingDelta", "ThinkingDelta", "ThinkingEnd"],
            eventsSeen.Select(e => e.GetType().Name).ToArray());

        var final = output.Snapshot();
        var thinking = final.Content.OfType<ThinkingContent>().Single();
        Assert.Equal("thinking hard", thinking.Thinking);
        // 签名即 reasoning 项 wire JSON（重放用），含 encrypted_content。
        Assert.NotNull(thinking.Signature);
        Assert.Equal("enc-1", (JsonNode.Parse(thinking.Signature!) as JsonObject)?.Str("encrypted_content"));
    }

    [Fact]
    public async Task IncompleteMaxOutputTokensMapsToLength()
    {
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_4"}}""",
            new JsonObject
            {
                ["type"] = "response.incomplete",
                ["response"] = new JsonObject
                {
                    ["id"] = "resp_4",
                    ["status"] = "incomplete",
                    ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
                },
            }.ToJsonString());

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        await OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel());

        stream.End(output.Snapshot());
        var final = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Length, final.StopReason);
        Assert.Equal("incomplete.max_output_tokens", final.RawStopReason);
    }

    [Fact]
    public async Task MissingTerminalEventThrows()
    {
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_5"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}""");
        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel()));
    }

    [Fact]
    public async Task UnfinishedToolCallBlocksDelivery()
    {
        // output_item.done 从未到达——参数可能被截断，拒绝交付。
        var events = Sse(
            """{"type":"response.created","response":{"id":"resp_6"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","id":"fc_1","name":"search","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"path\":"}""",
            ResponseCompleted().ToJsonString());

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage("gpt-5.1", "openai-responses", "openai");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiResponsesShared.ProcessResponsesStream(events, output, stream, ResponsesModel()));
        Assert.Contains("unfinished tool call", error.Message);
        Assert.Contains("search", error.Message);
    }

    // =============================================================================
    // buildParams / compat
    // =============================================================================

    [Fact]
    public void BuildParamsIncludesCoreFields()
    {
        var model = ResponsesModel();
        var context = Context(
            new SystemMessage("be brief", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1));
        var requestParams = OpenAiResponses.BuildParams(
            model, context, new OpenAiResponsesOptions { ApiKey = "sk-test", SessionId = "sess-1" });

        Assert.Equal("gpt-5.1", requestParams.Str("model"));
        Assert.True(requestParams.Bool("stream"));
        Assert.Equal(false, requestParams.Bool("store"));
        Assert.Equal("sess-1", requestParams.Str("prompt_cache_key"));
        // 默认 cacheRetention=short：不发 24h 保留。
        Assert.Null(requestParams.Str("prompt_cache_retention"));
        // reasoning 模型带 effort + encrypted_content
        Assert.Equal("none", requestParams["reasoning"] is JsonObject reasoning ? reasoning.Str("effort") : null);
        // include 只在显式 reasoningEffort 分支设置（TS 一致）。
        Assert.Null(requestParams["include"]);
        var input = requestParams["input"]!.AsArray();
        Assert.Equal("developer", ((JsonObject)input[0]!).Str("role"));
    }

    [Fact]
    public void BuildParamsClampsMaxOutputTokens()
    {
        var model = ResponsesModel();
        var context = Context(new UserMessage([new TextContent("hi")], 1));
        var requestParams = OpenAiResponses.BuildParams(
            model, context, new OpenAiResponsesOptions { ApiKey = "sk-test", MaxTokens = 5 });
        Assert.Equal(16, requestParams.Num("max_output_tokens")); // 下限 16
    }

    [Fact]
    public void CompatDefaultsDetectOpenRouterAffinity()
    {
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "openai-responses", Provider = "openrouter",
            BaseUrl = "https://openrouter.ai/api/v1", Input = ["text"],
        };
        var compat = OpenAiResponses.GetCompat(model);
        Assert.Equal("openrouter", compat.Str("sessionAffinityFormat"));
    }

    [Fact]
    public void ServiceTierPricingAppliesMultiplier()
    {
        var usage = new Usage(100, 200) { Cost = 1.0 };
        var priced = OpenAiResponses.ApplyServiceTierPricing(usage, "flex", ResponsesModel());
        Assert.Equal(0.5, priced.Cost!.Value, 12);
        var priority = OpenAiResponses.ApplyServiceTierPricing(usage, "priority", ResponsesModel());
        Assert.Equal(2.0, priority.Cost!.Value, 12);
    }
}
