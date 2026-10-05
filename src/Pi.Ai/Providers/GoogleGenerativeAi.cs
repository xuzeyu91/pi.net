using Pi.Ai.Utils;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Google Generative AI（Gemini）REST 直调实现。对应 TS <c>api/google-generative-ai.ts</c>
/// （471 行）的核心链路：convertMessages（systemInstruction 独立、thought:true part、
/// functionCall/functionResponse）、buildParams、SSE 流解析、mapStopReason。
/// thoughtSignature 回传与 retry 机制留 TODO。
/// </summary>
public static class GoogleGenerativeAi
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>停止原因映射。对应 TS <c>mapStopReason</c>。</summary>
    public static StopReason MapStopReason(string? finish)
        => finish switch
        {
            "STOP" => StopReason.Stop,
            "MAX_TOKENS" => StopReason.Length,
            "SAFETY" or "RECITATION" => StopReason.Aborted,
            "functionCall" => StopReason.ToolUse,
            _ => StopReason.Stop,
        };

    /// <summary>toolCall part 识别（thought part 带 thought:true）。对应 TS <c>isThinkingPart</c>。</summary>
    private static bool IsThinkingPart(JsonObject part)
        => part["thought"] is JsonValue { } flag && flag.TryGetValue<bool>(out var thought) && thought;

    /// <summary>
    /// 消息转换：Gemini 无会话中段 system 消息——首个 system 作为 systemInstruction；
    /// assistant 文本/thinking/text→parts、toolCall→functionCall、toolResult→functionResponse。
    /// 对应 TS <c>convertMessages</c>。
    /// </summary>
    public static (string? SystemInstruction, JsonArray Contents) ConvertMessages(
        IReadOnlyList<ChatMessage> context)
    {
        string? systemInstruction = null;
        var contents = new JsonArray();

        var first = context.FirstOrDefault();
        if (first is SystemMessage system)
        {
            systemInstruction = system.Content;
        }

        foreach (var message in context)
        {
            switch (message)
            {
                case UserMessage user:
                {
                    var parts = new JsonArray();
                    foreach (var block in user.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                                parts.Add(new JsonObject { ["text"] = text.Text });
                                break;
                            case ImageContent image:
                                parts.Add(new JsonObject
                                {
                                    ["inlineData"] = new JsonObject
                                    {
                                        ["mimeType"] = image.MimeType ?? "image/png",
                                        ["data"] = image.Data,
                                    },
                                });
                                break;
                        }
                    }
                    if (parts.Count > 0) contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    break;
                }
                case AssistantMessage assistant:
                {
                    var parts = new JsonArray();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                                parts.Add(new JsonObject { ["text"] = text.Text });
                                break;
                            case ThinkingContent thinking when !string.IsNullOrWhiteSpace(thinking.Thinking):
                                parts.Add(new JsonObject { ["thought"] = true, ["text"] = thinking.Thinking });
                                break;
                            case ToolCallContent toolCall:
                                parts.Add(new JsonObject
                                {
                                    ["functionCall"] = new JsonObject
                                    {
                                        ["id"] = toolCall.Id,
                                        ["name"] = toolCall.Name,
                                        ["args"] = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(
                                            System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments ?? new JsonObject())),
                                    },
                                });
                                break;
                        }
                    }
                    if (parts.Count > 0) contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    // toolResult 是 user 角色的 functionResponse（与 TS 一致）。
                    var parts = new JsonArray();
                    foreach (var block in toolResult.Content)
                    {
                        if (block is TextContent text)
                        {
                            parts.Add(new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["id"] = toolResult.ToolCallId,
                                    ["name"] = toolResult.ToolName ?? "unknown",
                                    ["response"] = new JsonObject { ["output"] = text.Text },
                                },
                            });
                        }
                    }
                    if (parts.Count > 0) contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    break;
                }
            }
        }
        return (systemInstruction, contents);
    }

    /// <summary>构造 generateContent 参数。对应 TS <c>buildParams</c>（含 thinkingConfig）。</summary>
    public static JsonObject BuildParams(IReadOnlyList<ChatMessage> context,
        IReadOnlyList<ToolDefinition>? tools = null, bool thinkingEnabled = false)
    {
        var (systemInstruction, contents) = ConvertMessages(context);
        var parameters = new JsonObject();
        if (!string.IsNullOrEmpty(systemInstruction))
        {
            parameters["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = systemInstruction }),
            };
        }
        parameters["contents"] = contents;
        parameters["generationConfig"] = GoogleThinking.BuildConfig("gemini-2.5-flash", thinkingEnabled);
        if (tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var tool in tools)
            {
                declarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(
                        System.Text.Json.JsonSerializer.Serialize(tool.Parameters)) ?? new JsonObject(),
                });
            }
            parameters["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }
        return parameters;
    }

    /// <summary>
    /// 简单流式：POST :streamGenerateContent?alt=sse，解析 candidates[0].content.parts
    /// （text/thought/functionCall）与 usageMetadata/finishReason。
    /// 失败编码为 Error 终态（契约：不抛出）。
    /// </summary>
    public static IAssistantMessageEventStream StreamSimple(Types.Model model,
        TranscriptContext context, SimpleStreamOptions? options,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(stream, model, context, options, cancellationToken), cancellationToken);
        return stream;
    }

    private static async Task RunAsync(AssistantMessageEventStream stream, Types.Model model,
        TranscriptContext context, SimpleStreamOptions? options, CancellationToken cancellationToken)
    {
        var blocks = new List<ContentBlock>();
        var timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        try
        {
            var apiKey = options?.ApiKey
                ?? throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            var parameters = BuildParams(context.Messages, context.Tools);
                        var baseAddress = string.IsNullOrEmpty(options.BaseUrl) ? BaseUrl : options.BaseUrl.TrimEnd('/');
            var url = $"{baseAddress}/models/{model.Id}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(apiKey)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(parameters.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var client = new HttpClient();
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {errorBody}");
            }

            var partial = new AssistantMessage(
                blocks, StopReason.Aborted,
                Api: "google-generative-ai", Provider: model.Provider, Model: model.Id,
                UsageStats: new Usage(0, 0), Timestamp: timestamp);
            stream.Push(new AssistantMessageEvent.Start(partial));
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var dataLines = new List<string>();
            await AiSse.ConsumeAsync(body, sseEvent =>
            {
                if (sseEvent.Data.Length > 0) dataLines.Add(sseEvent.Data);
            }, cancellationToken).ConfigureAwait(false);
            long inputTokens = 0, outputTokens = 0;
            StopReason stopReason = StopReason.Aborted;

            foreach (var dataLine in dataLines)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (JsonNode.Parse(dataLine) is not JsonObject chunk) continue;
                var candidates = chunk["candidates"] as JsonArray;
                if (candidates?.FirstOrDefault() is not JsonObject candidate) continue;
                if (candidate["content"] is not JsonObject content) continue;
                if (content["parts"] is not JsonArray parts) continue;
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part) continue;
                    if (part["functionCall"] is JsonObject functionCall)
                    {
                        blocks.Add(new ToolCallContent(
                            functionCall["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                            functionCall["name"]?.GetValue<string>() ?? "",
                            functionCall["args"]?.DeepClone() ?? new JsonObject()));
                    }
                    else if (part["text"] is JsonValue { } textValue
                        && textValue.TryGetValue<string>(out var text))
                    {
                        AppendText(blocks, text, IsThinkingPart(part));
                    }
                }
                if (chunk["usageMetadata"] is JsonObject usage)
                {
                    if (usage["promptTokenCount"] is JsonValue { } p) inputTokens = p.TryGetValue<long>(out var pv) ? pv : 0;
                    if (usage["candidatesTokenCount"] is JsonValue { } c) outputTokens = c.TryGetValue<long>(out var cv) ? cv : 0;
                }
                if (candidate["finishReason"] is JsonValue { } finishValue
                    && finishValue.TryGetValue<string>(out var finish))
                {
                    stopReason = MapStopReason(finish);
                }
                stream.Push(new AssistantMessageEvent.TextDelta(0, string.Empty, DateTimeOffset.Now.ToUnixTimeMilliseconds(), partial with { }));
            }

            var final = partial with
            {
                StopReason = stopReason,
                UsageStats = new Usage(inputTokens, outputTokens),
            };
            stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
            stream.End(final);
        }
        catch (Exception error)
        {
            var failed = new AssistantMessage(
                blocks, cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error,
                ErrorMessage: error.Message,
                Api: "google-generative-ai", Provider: model.Provider, Model: model.Id,
                UsageStats: new Usage(0, 0), Timestamp: timestamp);
            stream.Push(new AssistantMessageEvent.Error(failed.StopReason, "google-error", failed));
            stream.End(failed);
        }
    }

    /// <summary>文本增量追加：thinking 与普通 text 分段聚合（对应 TS 的 currentBlock 切换）。</summary>
    private static void AppendText(List<ContentBlock> blocks, string text, bool thinking)
    {
        if (thinking)
        {
            if (blocks.Count > 0 && blocks[^1] is ThinkingContent tailThinking)
            {
                blocks[^1] = tailThinking with { Thinking = tailThinking.Thinking + text };
            }
            else
            {
                blocks.Add(new ThinkingContent(text));
            }
        }
        else
        {
            if (blocks.Count > 0 && blocks[^1] is TextContent tailText)
            {
                blocks[^1] = tailText with { Text = tailText.Text + text };
            }
            else
            {
                blocks.Add(new TextContent(text));
            }
        }
    }

    private static int LastThinkingIndex(AssistantMessage message)
    {
        for (var index = message.Content.Count - 1; index >= 0; index--)
        {
            if (message.Content[index] is ThinkingContent) return index;
        }
        return -1;
    }
}
