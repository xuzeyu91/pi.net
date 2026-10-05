using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// Anthropic Messages provider。对应 TS <c>api/anthropic-messages.ts</c> 核心链路：
/// system 独立字段、content 块协议（tool_use / tool_result / image source.base64）、
/// POST /v1/messages SSE（content_block_* 事件模型）→ 统一流事件。
/// 直接以 HttpClient 调 REST；thinking 配置与 cache_control 兼容层随后续会话。
/// </summary>
public static class AnthropicMessages
{
    private const string ApiVersion = "2023-06-01";

    /// <summary>构建请求参数。对应 TS <c>buildParams</c> 的核心字段（max_tokens 必填）。</summary>
    public static JsonObject BuildParams(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        SimpleStreamOptions options)
    {
        var systemText = string.Join("\n\n", messages
            .OfType<SystemMessage>()
            .Select(m => m.Content ?? ""));
        var conversation = messages
            .Where(m => m is not SystemMessage)
            .ToList();

        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["max_tokens"] = options.MaxTokens ?? 4096,
            ["stream"] = true,
            ["messages"] = ConvertMessages(conversation),
        };
        if (systemText.Length > 0) parameters["system"] = systemText;
        if (options.Temperature is not null) parameters["temperature"] = options.Temperature;
        if (options.StopSequences is { Count: > 0 })
            parameters["stop_sequences"] = new JsonArray(options.StopSequences.Select(s => (JsonNode)s).ToArray());
        if (tools is { Count: > 0 })
            parameters["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonObject.Create(
                    System.Text.Json.JsonSerializer.SerializeToElement(tool.Parameters.JsonSchema)),
            }).ToArray());
        return parameters;
    }

    /// <summary>
    /// 消息转换。对应 TS 侧 Anthropic content 块协议：
    /// user 图片 → source.base64；assistant 工具调用 → tool_use；
    /// toolResult → user 消息内的 tool_result 块。
    /// </summary>
    public static JsonArray ConvertMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                {
                    var content = new JsonArray();
                    foreach (var block in user.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                                break;
                            case ImageContent image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = image.MimeType ?? "image/png",
                                        ["data"] = image.Data,
                                    },
                                });
                                break;
                        }
                    }
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }

                case AssistantMessage assistant:
                {
                    var content = new JsonArray();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                                break;
                            case ThinkingContent thinking:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = thinking.Thinking,
                                    ["signature"] = thinking.Signature,
                                });
                                break;
                            case ToolCallContent call:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = call.Id,
                                    ["name"] = call.Name,
                                    ["input"] = JsonObject.Create(
                                        System.Text.Json.JsonSerializer.SerializeToElement(call.Arguments ?? new { })),
                                });
                                break;
                        }
                    }
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }

                case ToolResultMessage toolResult:
                {
                    // tool_result 是 user 消息内的块。
                    var content = new JsonArray();
                    foreach (var block in toolResult.Content)
                    {
                        if (block is TextContent text)
                            content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    }
                    result.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolResult.ToolCallId,
                            ["content"] = content,
                            ["is_error"] = toolResult.IsError,
                        }),
                    });
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 流式调用。SSE 事件模型：message_start / content_block_start /
    /// content_block_delta（text_delta | input_json_delta | thinking_delta）/
    /// content_block_stop / message_delta（stop_reason + usage）/ message_stop。
    /// 失败编码为 StopReason=Error 终态（契约：不抛出）。
    /// </summary>
    public static Task<IAssistantMessageEventStream> StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        _ = Task.Run(() => RunAsync(stream, model, context, options, cancellationToken), cancellationToken);
        return Task.FromResult<IAssistantMessageEventStream>(stream);
    }

    private static async Task RunAsync(
        AssistantMessageEventStream stream,
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var effectiveOptions = options ?? new SimpleStreamOptions();
        var partial = new AssistantMessage([], Timestamp: timestamp);
        try
        {
            stream.Push(new AssistantMessageEvent.Start(partial));

            var baseUrl = effectiveOptions.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com/v1";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages");
            request.Content = new StringContent(
                BuildParams(model, context.Messages, context.Tools, effectiveOptions).ToJsonString(),
                Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("x-api-key", effectiveOptions.ApiKey ?? "");
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

            using var http = new HttpClient();
            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Finalize(stream, partial, StopReason.Error,
                    $"Anthropic request failed with status {(int)response.StatusCode}: {body}", timestamp);
                return;
            }

            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ContinueWith(async (sst, _) =>
                {
                    var sseStream = sst.Result;
                    long sequence = 0;
                    long? inputTokens = null;
                    long? outputTokens = null;
                    StopReason? stopReason = null;

                    // 按 index 聚合内容块：text 拼接、tool_use 的 input_json_delta 拼接。
                    var blockTypes = new Dictionary<int, string>();
                    var toolIds = new Dictionary<int, string>();
                    var toolNames = new Dictionary<int, string>();
                    var toolArgs = new Dictionary<int, StringBuilder>();
                    var texts = new Dictionary<int, StringBuilder>();
                    var thinkings = new Dictionary<int, StringBuilder>();

                    await AiSse.ConsumeAsync(sseStream, @event =>
                    {
                        if (@event.Event is null) return;
                        JsonObject? payload;
                        try { payload = JsonNode.Parse(@event.Data) as JsonObject; }
                        catch { return; }
                        if (payload is null) return;

                        switch (@event.Event)
                        {
                            case "message_start":
                                if (payload["message"]?["usage"] is JsonObject usage)
                                {
                                    inputTokens = (long?)usage["input_tokens"];
                                    outputTokens = (long?)usage["output_tokens"];
                                }
                                break;

                            case "content_block_start":
                            {
                                var index = (int?)payload["index"] ?? 0;
                                if (payload["content_block"] is JsonObject block)
                                {
                                    var type = block["type"]?.GetValue<string>() ?? "";
                                    blockTypes[index] = type;
                                    if (type == "tool_use")
                                    {
                                        toolIds[index] = block["id"]?.GetValue<string>() ?? "";
                                        toolNames[index] = block["name"]?.GetValue<string>() ?? "";
                                    }
                                }
                                break;
                            }

                            case "content_block_delta":
                            {
                                var index = (int?)payload["index"] ?? 0;
                                if (payload["delta"] is not JsonObject delta) return;
                                var type = delta["type"]?.GetValue<string>();
                                if (type == "text_delta" && delta["text"] is JsonValue textValue
                                    && textValue.TryGetValue<string>(out var textFragment))
                                {
                                    sequence++;
                                    texts.TryAdd(index, new StringBuilder());
                                    texts[index].Append(textFragment);
                                    partial = Rebuild(partial, blockTypes, toolIds, toolNames, toolArgs, texts, thinkings);
                                    stream.Push(new AssistantMessageEvent.TextDelta(0, textFragment, sequence, partial));
                                }
                                else if (type == "thinking_delta" && delta["thinking"] is JsonValue thinkValue
                                    && thinkValue.TryGetValue<string>(out var thinkFragment))
                                {
                                    sequence++;
                                    thinkings.TryAdd(index, new StringBuilder());
                                    thinkings[index].Append(thinkFragment);
                                    partial = Rebuild(partial, blockTypes, toolIds, toolNames, toolArgs, texts, thinkings);
                                    stream.Push(new AssistantMessageEvent.ThinkingDelta(0, thinkFragment, sequence, null, partial));
                                }
                                else if (type == "input_json_delta" && delta["partial_json"] is JsonValue jsonValue
                                    && jsonValue.TryGetValue<string>(out var jsonFragment))
                                {
                                    toolArgs.TryAdd(index, new StringBuilder());
                                    toolArgs[index].Append(jsonFragment);
                                }
                                break;
                            }

                            case "message_delta":
                            {
                                if (payload["delta"]?["stop_reason"] is JsonValue stopValue)
                                    stopReason = MapStopReason(stopValue.GetValue<string>());
                                if (payload["usage"]?["output_tokens"] is JsonValue outValue)
                                    outputTokens = outValue.GetValue<long>();
                                break;
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);

                    partial = Rebuild(partial, blockTypes, toolIds, toolNames, toolArgs, texts, thinkings);
                    Finalize(stream, partial,
                        stopReason ?? StopReason.Stop, null, timestamp,
                        inputTokens is null && outputTokens is null
                            ? null
                            : new Usage(inputTokens ?? 0, outputTokens ?? 0));
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finalize(stream, partial, StopReason.Aborted, "Request aborted", timestamp);
        }
        catch (Exception error)
        {
            Finalize(stream, partial, StopReason.Error, error.Message, timestamp);
        }
    }

    /// <summary>按块序号重建内容块列表（text / thinking / tool_use）。</summary>
    private static AssistantMessage Rebuild(
        AssistantMessage partial,
        Dictionary<int, string> blockTypes,
        Dictionary<int, string> toolIds,
        Dictionary<int, string> toolNames,
        Dictionary<int, StringBuilder> toolArgs,
        Dictionary<int, StringBuilder> texts,
        Dictionary<int, StringBuilder> thinkings)
    {
        var blocks = new List<ContentBlock>();
        foreach (var index in blockTypes.Keys.OrderBy(i => i))
        {
            switch (blockTypes[index])
            {
                case "text" when texts.TryGetValue(index, out var text):
                    blocks.Add(new TextContent(text.ToString()));
                    break;
                case "thinking" when thinkings.TryGetValue(index, out var thinking):
                    blocks.Add(new ThinkingContent(thinking.ToString()));
                    break;
                case "tool_use":
                {
                    object? input = null;
                    var raw = toolArgs.TryGetValue(index, out var args) ? args.ToString() : "";
                    try { input = JsonNode.Parse(raw.Length > 0 ? raw : "{}"); }
                    catch { input = raw; }
                    blocks.Add(new ToolCallContent(
                        toolIds.GetValueOrDefault(index, $"tool_{Guid.NewGuid():N}"),
                        toolNames.GetValueOrDefault(index, ""), input));
                    break;
                }
            }
        }
        return partial with { Content = blocks };
    }

    private static void Finalize(AssistantMessageEventStream stream, AssistantMessage partial,
        StopReason stopReason, string? errorMessage, long timestamp, Usage? usage = null)
    {
        var final = partial with
        {
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            UsageStats = usage ?? partial.UsageStats,
            Timestamp = timestamp,
        };
        stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
        stream.End(final);
    }

    /// <summary>stop_reason → StopReason 映射。对应 TS 侧 switch。</summary>
    public static StopReason MapStopReason(string stop) => stop switch
    {
        "end_turn" or "stop_sequence" => StopReason.Stop,
        "max_tokens" => StopReason.Length,
        "tool_use" => StopReason.ToolUse,
        _ => StopReason.Stop,
    };
}
