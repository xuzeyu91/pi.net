using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenAI Chat Completions provider。对应 TS <c>api/openai-completions.ts</c>
/// （本移植实现核心链路：消息转换 → buildParams → POST /v1/chat/completions
/// 流式 → delta 事件映射；reasoning details / cache control / thinking budgets
/// 等兼容层随后续会话补全）。直接以 HttpClient 调 REST，对齐原实现 fetch/undici 行为。
/// </summary>
public static class OpenAiCompletions
{
    /// <summary>构建请求参数。对应 TS <c>buildParams</c> 的核心字段。</summary>
    public static JsonObject BuildParams(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        SimpleStreamOptions options)
    {
        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = ConvertMessages(messages),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (options.Temperature is not null) parameters["temperature"] = options.Temperature;
        if (options.MaxTokens is not null) parameters["max_tokens"] = options.MaxTokens;
        if (options.StopSequences is { Count: > 0 })
            parameters["stop"] = new JsonArray(options.StopSequences.Select(s => (JsonNode)s).ToArray());
        if (tools is { Count: > 0 })
            parameters["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonObject.Create(JsonSerializer.SerializeToElement(tool.Parameters.JsonSchema)),
                },
            }).ToArray());
        return parameters;
    }

    /// <summary>
    /// 消息转换。对应 TS <c>convertCompletionsMessages</c>：
    /// system → system 角色；user 文本/图片 → content 数组；assistant 工具调用 →
    /// tool_calls；toolResult → tool 角色。
    /// </summary>
    public static JsonArray ConvertMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            switch (message)
            {
                case SystemMessage system:
                    result.Add(new JsonObject { ["role"] = "system", ["content"] = system.Content ?? "" });
                    break;

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
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType ?? "image/png"};base64,{image.Data}",
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
                    var entry = new JsonObject { ["role"] = "assistant" };
                    var text = string.Concat(assistant.Content.OfType<TextContent>().Select(t => t.Text));
                    if (text.Length > 0) entry["content"] = text;
                    var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();
                    if (toolCalls.Count > 0)
                    {
                        entry["tool_calls"] = new JsonArray(toolCalls.Select(call => (JsonNode)new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                // TS 侧 arguments 是 JSON 字符串；若已是对象则原样序列化。
                                ["arguments"] = call.Arguments is string s ? s : JsonValue.Create(call.Arguments)?.ToJsonString() ?? "{}",
                            },
                        }).ToArray());
                    }
                    result.Add(entry);
                    break;
                }

                case ToolResultMessage toolResult:
                {
                    var text = string.Concat(toolResult.Content.OfType<TextContent>().Select(t => t.Text));
                    result.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolResult.ToolCallId,
                        ["content"] = text,
                    });
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 流式调用。返回事件流：start → text/thinking/toolcall delta → done/error。
    /// 请求失败以 StopReason=Error 的终态消息编码（契约：不抛出）。
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

            var baseUrl = effectiveOptions.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            request.Content = new StringContent(
                BuildParams(model, context.Messages, context.Tools, effectiveOptions).ToJsonString(),
                Encoding.UTF8, "application/json");
            if (effectiveOptions.ApiKey is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", effectiveOptions.ApiKey);

            using var http = new HttpClient();
            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Finalize(stream, partial, StopReason.Error,
                    $"OpenAI request failed with status {(int)response.StatusCode}: {body}", timestamp);
                return;
            }

            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ContinueWith(async (sst, _) =>
                {
                    var sseStream = sst.Result;
                    long sequence = 0;
                    var toolCallBuffers = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
                    StopReason? finishReason = null;
                    Usage? usage = null;
                    string? errorMessage = null;

                    await AiSse.ConsumeAsync(sseStream, @event =>
                    {
                        var data = @event.Data;
                        if (data == "[DONE]") return;
                        JsonObject chunk;
                        try { chunk = JsonNode.Parse(data) as JsonObject ?? new JsonObject(); }
                        catch { return; }

                        // usage 块（include_usage 时最后一块）。
                        if (chunk["usage"] is JsonObject usageJson)
                        {
                            usage = new Usage(
                                (long?)usageJson["prompt_tokens"] ?? 0,
                                (long?)usageJson["completion_tokens"] ?? 0);
                        }

                        if (chunk["choices"] is not JsonArray choices || choices.Count == 0) return;
                        var choice = choices[0] as JsonObject;
                        if (choice?["delta"] is not JsonObject delta) return;

                        // 文本增量。
                        if (delta["content"] is JsonValue contentValue
                            && contentValue.TryGetValue<string>(out var textDelta)
                            && textDelta.Length > 0)
                        {
                            sequence++;
                            var blocks = partial.Content.ToList();
                            if (blocks.FirstOrDefault(b => b is TextContent) is TextContent existing)
                            {
                                var index = blocks.IndexOf(existing);
                                blocks[index] = new TextContent(existing.Text + textDelta);
                            }
                            else blocks.Add(new TextContent(textDelta));
                            partial = partial with { Content = blocks };
                            stream.Push(new AssistantMessageEvent.TextDelta(textDelta, sequence, partial));
                        }

                        // 思维链增量（reasoning_content / reasoning 兼容字段）。
                        if (delta["reasoning_content"] is JsonValue reasoningValue
                            && reasoningValue.TryGetValue<string>(out var reasoningDelta)
                            && reasoningDelta.Length > 0)
                        {
                            sequence++;
                            var blocks = partial.Content.ToList();
                            if (blocks.FirstOrDefault(b => b is ThinkingContent) is ThinkingContent existingThinking)
                            {
                                var index = blocks.IndexOf(existingThinking);
                                blocks[index] = new ThinkingContent(existingThinking.Thinking + reasoningDelta);
                            }
                            else blocks.Add(new ThinkingContent(reasoningDelta));
                            partial = partial with { Content = blocks };
                            stream.Push(new AssistantMessageEvent.ThinkingDelta(reasoningDelta, sequence, null, partial));
                        }

                        // 工具调用增量（按 index 聚合参数片段）。
                        if (delta["tool_calls"] is JsonArray toolCallDeltas)
                        {
                            foreach (var node in toolCallDeltas)
                            {
                                if (node is not JsonObject callDelta) continue;
                                var index = (int?)callDelta["index"] ?? 0;
                                var buffer = toolCallBuffers.TryGetValue(index, out var existing2)
                                    ? existing2
                                    : (Id: "", Name: "", Args: new StringBuilder());
                                if (callDelta["id"] is JsonValue idValue) buffer.Id = idValue.GetValue<string>();
                                if (callDelta["function"] is JsonObject function)
                                {
                                    if (function["name"] is JsonValue nameValue
                                        && nameValue.TryGetValue<string>(out var name))
                                        buffer.Name += name;
                                    if (function["arguments"] is JsonValue argsValue
                                        && argsValue.TryGetValue<string>(out var argsFragment))
                                        buffer.Args.Append(argsFragment);
                                }
                                toolCallBuffers[index] = buffer;
                            }
                        }

                        // 终止原因。
                        if (choice["finish_reason"] is JsonValue finishValue
                            && finishValue.TryGetValue<string>(out var finish))
                            finishReason = MapFinishReason(finish);
                    }, cancellationToken).ConfigureAwait(false);

                    // 流结束：聚合工具调用块。
                    var finalBlocks = partial.Content.ToList();
                    foreach (var (_, (id, name, args)) in toolCallBuffers.OrderBy(kv => kv.Key))
                    {
                        object? arguments = null;
                        try { arguments = JsonNode.Parse(args.Length > 0 ? args.ToString() : "{}"); }
                        catch { arguments = args.ToString(); }
                        finalBlocks.Add(new ToolCallContent(
                            id.Length > 0 ? id : $"call_{Guid.NewGuid():N}", name, arguments));
                    }
                    partial = partial with { Content = finalBlocks };

                    Finalize(stream, partial,
                        finishReason ?? (toolCallBuffers.Count > 0 ? StopReason.ToolUse : StopReason.Stop),
                        errorMessage, timestamp, usage);
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

    private static void Finalize(AssistantMessageEventStream stream, AssistantMessage partial,
        StopReason stopReason, string? errorMessage, long timestamp, Usage? usage = null)
    {
        var final = partial with
        {
            StopReason = stopReason,
            ErrorMessage = errorMessage,
            UsageStats = usage ?? partial.UsageStats,
            Model = partial.Model,
            Timestamp = timestamp,
        };
        stream.Push(new AssistantMessageEvent.Done(final));
        stream.End(final);
    }

    /// <summary>finish_reason → StopReason 映射。对应 TS 侧 switch。</summary>
    public static StopReason MapFinishReason(string finish) => finish switch
    {
        "stop" => StopReason.Stop,
        "length" => StopReason.Length,
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "content_filter" => StopReason.Error,
        _ => StopReason.Stop,
    };
}
