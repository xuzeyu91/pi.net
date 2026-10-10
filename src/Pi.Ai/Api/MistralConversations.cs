using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Mistral 专属流选项。对应 TS <c>MistralOptions</c>。</summary>
public record MistralOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    public string? SessionId { get; init; }

    public string? CacheRetention { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>工具选择（auto/none/any/required 或函数定位）。对应 TS <c>toolChoice</c>。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>promptMode（"reasoning"；无 thinkingLevelMap 的推理模型用）。对应 TS <c>promptMode</c>。</summary>
    public string? PromptMode { get; init; }

    /// <summary>推理档位（none/low/medium/high/max）。对应 TS <c>reasoningEffort</c>。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>任意采样参数（wire 层转 snake_case 后并入）。对应 TS <c>samplingParams</c>。</summary>
    public JsonObject? SamplingParams { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>Mistral HTTP 错误（带状态码与响应体）。对应 TS <c>MistralHttpError</c>。</summary>
public sealed class MistralHttpError : Exception
{
    public int StatusCode { get; }

    public string Body { get; }

    public MistralHttpError(int statusCode, string body, string statusText)
        : base(statusText.Length > 0 ? statusText : $"Request failed with status {statusCode}")
    {
        StatusCode = statusCode;
        Body = body;
    }
}

/// <summary>
/// 原生 Mistral Chat Completions 端点流式实现。对应 TS <c>api/mistral-conversations.ts</c>：
/// camelCase 请求字段（toolCalls/promptCacheKey 等）在 wire 层转 snake_case，
/// 流式增量按内容块类型折叠，工具调用 ID 归一化为 9 位字母数字。
/// </summary>
public static class MistralConversations
{
    private const int MistralToolCallIdLength = 9;
    private const int MaxMistralErrorBodyChars = 4000;
    private const int DefaultTimeoutMs = 60_000;

    /// <summary>流式生成。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        MistralOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var supportsMidConvo = model.Compat?["supportsMidConvoSystemMessages"] is System.Text.Json.Nodes.JsonValue { } mid
            && mid.TryGetValue<bool>(out var midFlag) && midFlag;
        var normalizedContext = Transcript.ResolveTranscript(context, supportsMidConvo);
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider);
            try
            {
                var apiKey = options?.ApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    throw new InvalidOperationException($"No API key for provider: {model.Provider}");
                }

                var toolCallIdNormalizer = CreateMistralToolCallIdNormalizer();
                var transformedMessages = TransformMessages.Transform(
                    normalizedContext.Messages, model,
                    (id, _, _) => toolCallIdNormalizer(id));

                var payload = BuildChatPayload(model, normalizedContext, transformedMessages, options);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }
                var mistralStream = await RequestMistralStreamAsync(
                    model, payload, apiKey, options, client, signal, cancellationToken).ConfigureAwait(false);
                stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                await ConsumeChatStreamAsync(
                    model, output, stream, mistralStream, options?.OnProviderStreamEvent,
                    cancellationToken).ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Mistral stream ended without a finish reason");
                }
                if (output.StopReason is StopReason.Aborted or StopReason.Error)
                {
                    throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
                }

                stream.Push(new AssistantMessageEvent.Done(output.StopReason, output.Snapshot()));
                stream.End(output.Snapshot());
            }
            catch (Exception error)
            {
                output.StopReason = signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = FormatMistralError(error);
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>简单入口。对应 TS <c>streamSimple</c>：有 thinkingLevelMap 的模型用 reasoning_effort，其余推理模型用 prompt_mode。</summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options?.ApiKey))
        {
            throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        }

        var samplingParams = SimpleOptions.ResolveSamplingParams(
            model, options?.Reasoning ?? "off", options?.SamplingParams);
        int? maxTokens;
        if (options?.MaxTokens is { } requestedMaxTokens)
        {
            maxTokens = SimpleOptions.ClampMaxTokensToContext(model, context, requestedMaxTokens);
        }
        else
        {
            maxTokens = model.MaxTokens > 0
                ? SimpleOptions.ClampMaxTokensToContext(model, context, (int)model.MaxTokens)
                : null;
        }
        var clampedReasoning = options?.Reasoning is not null
            ? ThinkingLevels.Clamp(model, options.Reasoning)
            : null;
        var reasoning = clampedReasoning == "off" ? null : clampedReasoning;
        var effortMap = model.Reasoning ? model.ThinkingLevelMap : null;
        string? reasoningEffort = effortMap is not null
            ? reasoning is not null ? effortMap[reasoning] ?? "high" : effortMap["off"]
            : null;
        if (reasoningEffort is { Length: 0 }) reasoningEffort = null;

        return Stream(model, context, new MistralOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            SessionId = options?.SessionId,
            CacheRetention = options?.CacheRetention,
            Env = options?.Env,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice,
            SamplingParams = samplingParams,
            MaxTokens = maxTokens,
            Temperature = options?.Temperature,
            PromptMode = model.Reasoning && effortMap is null && reasoning is not null ? "reasoning" : null,
            ReasoningEffort = reasoningEffort,
        }, httpClient, cancellationToken);
    }

    // ============================================================================
    // 工具调用 ID 归一化（9 位字母数字）
    // ============================================================================

    /// <summary>创建幂等的工具调用 ID 归一化器。对应 TS <c>createMistralToolCallIdNormalizer</c>。</summary>
    public static Func<string, string> CreateMistralToolCallIdNormalizer()
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverseMap = new Dictionary<string, string>(StringComparer.Ordinal);

        return id =>
        {
            if (idMap.TryGetValue(id, out var existing)) return existing;

            var attempt = 0;
            while (true)
            {
                var candidate = DeriveMistralToolCallId(id, attempt);
                if (!reverseMap.TryGetValue(candidate, out var owner) || owner == id)
                {
                    idMap[id] = candidate;
                    reverseMap[candidate] = id;
                    return candidate;
                }
                attempt++;
            }
        };
    }

    private static string DeriveMistralToolCallId(string id, int attempt)
    {
        var normalized = new string([.. id.Where(char.IsAsciiLetterOrDigit)]);
        if (attempt == 0 && normalized.Length == MistralToolCallIdLength) return normalized;
        var seedBase = normalized.Length > 0 ? normalized : id;
        var seed = attempt == 0 ? seedBase : $"{seedBase}:{attempt}";
        var hash = new string([.. Hash.ShortHash(seed).Where(char.IsAsciiLetterOrDigit)]);
        return hash.Length > MistralToolCallIdLength ? hash[..MistralToolCallIdLength] : hash;
    }

    private static string FormatMistralError(Exception error)
    {
        if (error is MistralHttpError httpError)
        {
            var bodyText = httpError.Body.Trim();
            if (bodyText.Length > 0)
            {
                return $"Mistral API error ({httpError.StatusCode}): {TruncateErrorText(bodyText, MaxMistralErrorBodyChars)}";
            }
            return $"Mistral API error ({httpError.StatusCode}): {httpError.Message}";
        }
        return error.Message;
    }

    private static string TruncateErrorText(string text, int maxChars)
        => text.Length <= maxChars ? text : $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";

    // ============================================================================
    // 请求发送
    // ============================================================================

    private static async Task<IAsyncEnumerable<JsonObject>> RequestMistralStreamAsync(
        ModelSpec model,
        JsonObject payload,
        string apiKey,
        MistralOptions? options,
        HttpClient client,
        CancellationToken signal,
        CancellationToken cancellationToken)
    {
        var baseUrl = model.BaseUrl.TrimEnd('/') + "/";
        var url = $"{baseUrl}v1/chat/completions";
        var headers = BuildMistralHeaders(model, apiKey, options);
        var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;

        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
        perCallCts.CancelAfter(timeoutMs);
        var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Post, url)
        {
            Content = new StringContent(
                ToMistralWirePayload(payload).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        foreach (var (key, value) in headers)
        {
            if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
        }
        System.Net.Http.HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, perCallCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!signal.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {timeoutMs}ms");
        }

        if (options?.OnResponse is { } onResponse)
        {
            await onResponse(new ProviderResponse
            {
                Status = (int)response.StatusCode,
                Headers = response.Headers.ToDictionary(
                    kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
            }, model).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
            throw new MistralHttpError((int)response.StatusCode, body, response.ReasonPhrase ?? "");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return ReadMistralEvents(content, signal, cancellationToken);
    }

    private static Dictionary<string, string?> BuildMistralHeaders(
        ModelSpec model, string apiKey, MistralOptions? options)
    {
        var headers = new Dictionary<string, string?>
        {
            ["User-Agent"] = PiUserAgent.Get(),
            ["accept"] = "text/event-stream",
            ["authorization"] = $"Bearer {apiKey}",
            ["content-type"] = "application/json",
        };
        ApplyOverrides(headers, model.Headers is null
            ? null : new Dictionary<string, string?>(model.Headers.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)));
        ApplyOverrides(headers, options?.Headers);

        var hasExplicitAffinity = HasOverride(model.Headers?.Keys, "x-affinity")
            || HasOverride(options?.Headers?.Keys, "x-affinity");
        if (ShouldUsePromptCaching(options) && !hasExplicitAffinity)
        {
            headers["x-affinity"] = options!.SessionId;
        }
        return headers;
    }

    private static void ApplyOverrides(Dictionary<string, string?> headers, IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is null) return;
        foreach (var (name, value) in overrides)
        {
            if (value is null) headers.Remove(name);
            else headers[name] = value;
        }
    }

    private static bool HasOverride(IEnumerable<string>? keys, string target)
        => keys?.Any(name => name.Equals(target, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool ShouldUsePromptCaching(MistralOptions? options)
        => options?.CacheRetention != "none" && !string.IsNullOrEmpty(options?.SessionId);

    // ============================================================================
    // camelCase → wire snake_case
    // ============================================================================

    /// <summary>camelCase 请求字段 → wire snake_case。对应 TS <c>toMistralWirePayload</c>。</summary>
    public static JsonObject ToMistralWirePayload(JsonObject payload)
    {
        var wirePayload = (JsonObject)payload.DeepClone();
        foreach (var (source, target) in WireFieldMap)
        {
            Remap(wirePayload, source, target);
        }
        if (wirePayload["response_format"] is JsonObject responseFormat)
        {
            Remap(responseFormat, "jsonSchema", "json_schema");
            if (responseFormat["json_schema"] is JsonObject jsonSchema)
            {
                Remap(jsonSchema, "schemaDefinition", "schema");
            }
        }
        if (wirePayload["messages"] is JsonArray messages)
        {
            foreach (var messageNode in messages)
            {
                if (messageNode is not JsonObject message) continue;
                Remap(message, "toolCalls", "tool_calls");
                Remap(message, "toolCallId", "tool_call_id");
                if (message["content"] is JsonArray content)
                {
                    foreach (var chunkNode in content)
                    {
                        if (chunkNode is not JsonObject chunk) continue;
                        Remap(chunk, "imageUrl", "image_url");
                        Remap(chunk, "documentUrl", "document_url");
                        Remap(chunk, "documentName", "document_name");
                        Remap(chunk, "fileId", "file_id");
                        Remap(chunk, "referenceIds", "reference_ids");
                        Remap(chunk, "inputAudio", "input_audio");
                    }
                }
            }
        }
        return wirePayload;
    }

    private static readonly IReadOnlyList<(string Source, string Target)> WireFieldMap =
    [
        ("topP", "top_p"),
        ("maxTokens", "max_tokens"),
        ("randomSeed", "random_seed"),
        ("responseFormat", "response_format"),
        ("toolChoice", "tool_choice"),
        ("presencePenalty", "presence_penalty"),
        ("frequencyPenalty", "frequency_penalty"),
        ("parallelToolCalls", "parallel_tool_calls"),
        ("reasoningEffort", "reasoning_effort"),
        ("promptMode", "prompt_mode"),
        ("promptCacheKey", "prompt_cache_key"),
        ("safePrompt", "safe_prompt"),
    ];

    private static void Remap(JsonObject record, string source, string target)
    {
        if (!record.ContainsKey(source)) return;
        record[target] = record[source]?.DeepClone();
        record.Remove(source);
    }

    // ============================================================================
    // SSE 事件解析
    // ============================================================================

    private static async IAsyncEnumerable<JsonObject> ReadMistralEvents(
        System.IO.Stream body,
        CancellationToken signal,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var buffer = new StringBuilder();
        while (true)
        {
            signal.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // EOF：处理残余帧。
                var tail = ParseMistralEvent(buffer.ToString());
                if (tail is not null) yield return tail;
                break;
            }
            buffer.Append(line).Append('\n');
            var text = buffer.ToString();
            int boundary;
            while ((boundary = text.IndexOf("\n\n", StringComparison.Ordinal)) != -1)
            {
                var @event = ParseMistralEvent(text[..boundary]);
                text = text[(boundary + 2)..];
                if (@event is not null) yield return @event;
            }
            buffer.Clear();
            buffer.Append(text);
        }
    }

    /// <summary>解析一个 Mistral SSE 帧；[DONE] 返回 null。对应 TS <c>parseMistralEvent</c>。</summary>
    public static JsonObject? ParseMistralEvent(string raw)
    {
        var data = string.Join("\n", raw
            .Split(['\r', '\n'])
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].TrimStart()))
            .Trim();
        if (data.Length == 0 || data == "[DONE]") return null;
        if (JsonNode.Parse(data) is not JsonObject parsed || parsed["choices"] is not JsonArray)
        {
            throw new InvalidOperationException("Invalid Mistral streaming event");
        }
        return parsed;
    }

    // ============================================================================
    // 请求载荷构建
    // ============================================================================

    /// <summary>构建 chat/completions 载荷（camelCase；wire 层再转）。对应 TS <c>buildChatPayload</c>。</summary>
    public static JsonObject BuildChatPayload(
        ModelSpec model,
        TranscriptContext context,
        IReadOnlyList<ChatMessage> messages,
        MistralOptions? options)
    {
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["stream"] = true,
            ["messages"] = ToChatMessages(messages, model.Input.Contains("image")),
        };

        var currentTools = Transcript.GetCurrentTools(context.Messages);
        if (currentTools.Count > 0) payload["tools"] = ToFunctionTools(currentTools);
        if (options?.Temperature is { } temperature) payload["temperature"] = temperature;
        if (options?.MaxTokens is { } maxTokens) payload["maxTokens"] = maxTokens;
        if (options?.ToolChoice is { } toolChoice) payload["toolChoice"] = toolChoice.DeepClone();
        if (options?.PromptMode is { } promptMode) payload["promptMode"] = promptMode;
        if (options?.ReasoningEffort is { } reasoningEffort) payload["reasoningEffort"] = reasoningEffort;
        if (ShouldUsePromptCaching(options)) payload["promptCacheKey"] = options!.SessionId;

        return payload;
    }

    private static JsonArray ToFunctionTools(IReadOnlyList<ToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, true);
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict)
                        ?? new JsonObject(),
                    ["strict"] = strict ?? false,
                },
            });
        }
        return result;
    }

    // ============================================================================
    // 消息转换
    // ============================================================================

    private static JsonArray ToChatMessages(IReadOnlyList<ChatMessage> messages, bool supportsImages)
    {
        var result = new JsonArray();
        foreach (var (index, msg) in messages.Index())
        {
            switch (msg)
            {
                case SystemMessage system:
                {
                    var text = index == 0
                        ? Text.GetSystemMessageText(system)
                        : Text.RenderSystemMessageUpdate(system);
                    if (text.Length > 0)
                    {
                        result.Add(new JsonObject { ["role"] = "system", ["content"] = Sanitize(text) });
                    }
                    break;
                }
                case UserMessage user:
                {
                    var hadImages = user.Content.OfType<ImageContent>().Any();
                    var content = new JsonArray();
                    foreach (var item in user.Content)
                    {
                        if (item is TextContent textItem)
                        {
                            content.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = Sanitize(textItem.Text ?? ""),
                            });
                        }
                        else if (supportsImages && item is ImageContent image)
                        {
                            content.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["imageUrl"] = $"data:{image.MimeType};base64,{image.Data}",
                            });
                        }
                    }
                    if (content.Count > 0)
                    {
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    }
                    else if (hadImages && !supportsImages)
                    {
                        result.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "(image omitted: model does not support images)",
                        });
                    }
                    break;
                }
                case AssistantMessage assistant:
                {
                    var contentParts = new JsonArray();
                    var toolCalls = new JsonArray();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent textBlock:
                                if ((textBlock.Text ?? "").Trim().Length > 0)
                                {
                                    contentParts.Add(new JsonObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = Sanitize(textBlock.Text ?? ""),
                                    });
                                }
                                break;
                            case ThinkingContent thinkingBlock:
                                if (thinkingBlock.Thinking.Trim().Length > 0)
                                {
                                    contentParts.Add(new JsonObject
                                    {
                                        ["type"] = "thinking",
                                        ["thinking"] = new JsonArray(new JsonObject
                                        {
                                            ["type"] = "text",
                                            ["text"] = Sanitize(thinkingBlock.Thinking),
                                        }),
                                    });
                                }
                                break;
                            case ToolCallContent toolCall:
                                toolCalls.Add(new JsonObject
                                {
                                    ["id"] = toolCall.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = toolCall.Name,
                                        ["arguments"] = (toolCall.Arguments as JsonNode)?.ToJsonString() ?? "{}",
                                    },
                                    ["index"] = 0,
                                });
                                break;
                        }
                    }
                    var assistantMessage = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["prefix"] = false,
                    };
                    if (contentParts.Count > 0) assistantMessage["content"] = contentParts;
                    if (toolCalls.Count > 0) assistantMessage["tool_calls"] = toolCalls;
                    if (contentParts.Count > 0 || toolCalls.Count > 0) result.Add(assistantMessage);
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var toolContent = new JsonArray();
                    var textResult = string.Join("\n", toolResult.Content
                        .OfType<TextContent>()
                        .Select(part => Sanitize(part.Text ?? "")));
                    var hasImages = toolResult.Content.OfType<ImageContent>().Any();
                    var toolText = BuildToolResultText(textResult, hasImages, supportsImages, toolResult.IsError);
                    toolContent.Add(new JsonObject { ["type"] = "text", ["text"] = toolText });
                    if (supportsImages)
                    {
                        foreach (var part in toolResult.Content.OfType<ImageContent>())
                        {
                            toolContent.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["imageUrl"] = $"data:{part.MimeType};base64,{part.Data}",
                            });
                        }
                    }
                    result.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolResult.ToolCallId,
                        ["name"] = toolResult.ToolName,
                        ["content"] = toolContent,
                    });
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>工具结果文本（含错误前缀与图片占位）。对应 TS <c>buildToolResultText</c>。</summary>
    public static string BuildToolResultText(string text, bool hasImages, bool supportsImages, bool isError)
    {
        var trimmed = text.Trim();
        var errorPrefix = isError ? "[tool error] " : "";

        if (trimmed.Length > 0)
        {
            var imageSuffix = hasImages && !supportsImages
                ? "\n[tool image omitted: model does not support images]"
                : "";
            return $"{errorPrefix}{trimmed}{imageSuffix}";
        }

        if (hasImages)
        {
            return supportsImages
                ? (isError ? "[tool error] (see attached image)" : "(see attached image)")
                : (isError
                    ? "[tool error] (image omitted: model does not support images)"
                    : "(image omitted: model does not support images)");
        }

        return isError ? "[tool error] (no tool output)" : "(no tool output)";
    }

    private static string Sanitize(string text) => SanitizeUnicode.SanitizeSurrogates(text);

    // ============================================================================
    // 流消费
    // ============================================================================

    /// <summary>Mistral usage 的缓存命中 token（多形状字段探测）。对应 TS <c>getMistralCachedPromptTokens</c>。</summary>
    public static long GetMistralCachedPromptTokens(JsonObject usage, long promptTokens)
    {
        var cached = usage.Obj("promptTokensDetails")?.Num("cachedTokens")
            ?? usage.Obj("prompt_tokens_details")?.Num("cached_tokens")
            ?? usage.Obj("promptTokenDetails")?.Num("cachedTokens")
            ?? usage.Obj("prompt_token_details")?.Num("cached_tokens")
            ?? usage.Num("numCachedTokens")
            ?? usage.Num("num_cached_tokens")
            ?? 0;
        var cachedTokens = double.IsFinite(cached) ? (long)cached : 0;
        return Math.Min(promptTokens, Math.Max(0, cachedTokens));
    }

    /// <summary>finish_reason → StopReason。对应 TS <c>mapChatStopReason</c>。</summary>
    public static (StopReason StopReason, string? ErrorMessage) MapChatStopReason(string? reason)
        => reason switch
        {
            null => (StopReason.Stop, null),
            "stop" => (StopReason.Stop, null),
            "length" or "model_length" => (StopReason.Length, null),
            "tool_calls" => (StopReason.ToolUse, null),
            "error" => (StopReason.Error, "Provider stopped with: error"),
            _ => (StopReason.Error, $"Provider stopped with: {reason}"),
        };

    private static async Task ConsumeChatStreamAsync(
        ModelSpec model,
        OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream,
        IAsyncEnumerable<JsonObject> mistralStream,
        Func<JsonObject, ModelSpec, Task>? onProviderStreamEvent,
        CancellationToken cancellationToken)
    {
        // currentBlock 用类型+索引跟踪（record 不可变，替换写入）。
        int? currentBlockIndex = null;
        string? currentBlockType = null;
        var toolBlocksByKey = new Dictionary<object, int>();

        int BlockIndex() => output.Content.Count - 1;

        void FinishCurrentBlock()
        {
            if (currentBlockIndex is not { } index) return;
            var block = output.Content[index];
            switch (block)
            {
                case TextContent textBlock:
                    stream.Push(new AssistantMessageEvent.TextEnd(
                        BlockIndex(), textBlock.Text ?? "", output.Snapshot()));
                    break;
                case ThinkingContent thinkingBlock:
                    stream.Push(new AssistantMessageEvent.ThinkingEnd(
                        BlockIndex(), thinkingBlock.Thinking, output.Snapshot()));
                    break;
            }
        }

        await foreach (var @event in mistralStream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (onProviderStreamEvent is { } callback)
            {
                await callback(@event, model).ConfigureAwait(false);
            }
            // 流式 CompletionChunk 携带 id：保留首个非空值。
            if (output.ResponseId is null && @event.Str("id") is { } responseId)
            {
                output.ResponseId = responseId;
            }

            if (@event["usage"] is JsonObject usage)
            {
                var promptTokens = (long)(usage.Num("prompt_tokens") ?? 0);
                var cachedPromptTokens = GetMistralCachedPromptTokens(usage, promptTokens);
                var input = Math.Max(0, promptTokens - cachedPromptTokens);
                var outputTokens = (long)(usage.Num("completion_tokens") ?? 0);
                var stats = new Usage(input, outputTokens, cachedPromptTokens, 0)
                {
                    // TS：totalTokens 优先取 wire 的 total_tokens，回退到全桶之和。
                    TotalTokens = (long)(usage.Num("total_tokens") ?? 0) is var reported && reported != 0
                        ? reported
                        : input + outputTokens + cachedPromptTokens,
                };
                output.Usage = ModelOperations.CalculateCost(model, stats);
            }

            if (@event["choices"] is not JsonArray choices || choices.Count == 0
                || choices[0] is not JsonObject choice)
            {
                continue;
            }

            if (choice.Str("finish_reason") is { } finishReason)
            {
                output.RawStopReason = finishReason;
                var (stopReason, errorMessage) = MapChatStopReason(finishReason);
                output.StopReason = stopReason;
                if (errorMessage is not null)
                {
                    output.ErrorMessage = errorMessage;
                }
            }

            if (choice["delta"] is not JsonObject delta) continue;

            var rawContent = delta["content"];
            if (rawContent is not null && rawContent.GetValueKind() != System.Text.Json.JsonValueKind.Null)
            {
                var contentItems = rawContent as JsonArray;
                if (delta["content"] is JsonValue { } directText
                    && directText.TryGetValue<string>(out var directTextValue))
                {
                    AppendText(directTextValue);
                }
                else if (contentItems is not null)
                {
                    foreach (var item in contentItems)
                    {
                        if (item is JsonValue { } stringValue && stringValue.TryGetValue<string>(out var text))
                        {
                            AppendText(text);
                        }
                        else if (item is JsonObject chunk)
                        {
                            var chunkType = chunk.Str("type");
                            if (chunkType == "thinking")
                            {
                                var deltaText = string.Concat(((chunk["thinking"] as JsonArray) ?? [])
                                    .OfType<JsonObject>()
                                    .Select(part => part.Str("text") ?? "")
                                    .Where(text => text.Length > 0));
                                AppendThinking(deltaText);
                            }
                            else if (chunkType == "text")
                            {
                                AppendText(chunk.Str("text") ?? "");
                            }
                        }
                    }
                }
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var toolCallNode in toolCalls)
                {
                    if (toolCallNode is not JsonObject toolCall) continue;
                    if (currentBlockIndex is not null)
                    {
                        FinishCurrentBlock();
                        currentBlockIndex = null;
                        currentBlockType = null;
                    }
                    var callId = toolCall.Str("id") is { Length: > 0 } id && id != "null"
                        ? id
                        : DeriveMistralToolCallId($"toolcall:{toolCall.Num("index") ?? 0}", 0);
                    var key = toolCall.Has("index") ? (object)(toolCall.Num("index") ?? 0) : callId;
                    ToolCallContent? block = null;
                    if (toolBlocksByKey.TryGetValue(key, out var existingIndex)
                        && existingIndex < output.Content.Count
                        && output.Content[existingIndex] is ToolCallContent existing)
                    {
                        block = existing;
                    }

                    if (block is null)
                    {
                        block = new ToolCallContent(callId, toolCall.Obj("function")?.Str("name") ?? "", new JsonObject());
                        output.Content.Add(block);
                        toolBlocksByKey[key] = output.Content.Count - 1;
                        stream.Push(new AssistantMessageEvent.ToolCallStart(
                            output.Content.Count - 1, output.Snapshot()));
                    }

                    var argsDelta = toolCall.Obj("function")?["arguments"] switch
                    {
                        JsonValue { } argsValue when argsValue.TryGetValue<string>(out var argsText) => argsText,
                        JsonObject argsObject => argsObject.ToJsonString(),
                        _ => "{}",
                    };
                    var partialArgs = PartialArgs.GetOrCreate(block.Id + "|" + BlockIndex()) + argsDelta;
                    PartialArgs.Set(block.Id + "|" + BlockIndex(), partialArgs);
                    var blockIndex = toolBlocksByKey[key];
                    output.Content[blockIndex] = block with
                    {
                        Arguments = JsonParse.ParseStreamingJson(partialArgs) ?? new JsonObject(),
                    };
                    stream.Push(new AssistantMessageEvent.ToolCallDelta(
                        blockIndex, blockIndex, argsDelta, output.Snapshot()));
                }
            }
        }

        FinishCurrentBlock();
        foreach (var index in toolBlocksByKey.Values)
        {
            if (index >= output.Content.Count || output.Content[index] is not ToolCallContent toolBlock) continue;
            var key = toolBlock.Id + "|" + index;
            var partialArgs = PartialArgs.GetOrCreate(key);
            var finalized = toolBlock with
            {
                Arguments = JsonParse.ParseStreamingJson(partialArgs) ?? new JsonObject(),
            };
            // 就地终结并剥离草稿缓冲，重放只携带解析后的参数。
            output.Content[index] = finalized;
            PartialArgs.Remove(key);
            stream.Push(new AssistantMessageEvent.ToolCallEnd(index, finalized, output.Snapshot()));
        }
        PartialArgs.Clear();

        return;

        void AppendText(string raw)
        {
            var textDelta = Sanitize(raw);
            // GLM 系模型在思维与工具调用之间发送空 content 增量——为它们开块会把思维切成
            // 多块，Mistral 重放会拒绝。
            if (textDelta.Length == 0) return;
            if (currentBlockType != "text")
            {
                FinishCurrentBlock();
                output.Content.Add(new TextContent(""));
                currentBlockIndex = BlockIndex();
                currentBlockType = "text";
                stream.Push(new AssistantMessageEvent.TextStart(BlockIndex(), output.Snapshot()));
            }
            var index = currentBlockIndex!.Value;
            if (output.Content[index] is TextContent textBlock)
            {
                output.Content[index] = textBlock with { Text = textBlock.Text + textDelta };
            }
            stream.Push(new AssistantMessageEvent.TextDelta(
                BlockIndex(), textDelta, 0, output.Snapshot()));
        }

        void AppendThinking(string raw)
        {
            var thinkingDelta = Sanitize(raw);
            if (thinkingDelta.Length == 0) return;
            if (currentBlockType != "thinking")
            {
                FinishCurrentBlock();
                output.Content.Add(new ThinkingContent(""));
                currentBlockIndex = BlockIndex();
                currentBlockType = "thinking";
                stream.Push(new AssistantMessageEvent.ThinkingStart(BlockIndex(), output.Snapshot()));
            }
            var index = currentBlockIndex!.Value;
            if (output.Content[index] is ThinkingContent thinkingBlock)
            {
                output.Content[index] = thinkingBlock with { Thinking = thinkingBlock.Thinking + thinkingDelta };
            }
            stream.Push(new AssistantMessageEvent.ThinkingDelta(
                BlockIndex(), thinkingDelta, 0, null, output.Snapshot()));
        }
    }

    /// <summary>流式工具参数草稿缓冲（流结束后清空）。替代 TS 在 block 上挂 partialArgs 的做法。</summary>
    private static PartialArgsBuffer PartialArgs { get; } = new();

    private sealed class PartialArgsBuffer
    {
        private readonly Dictionary<string, string> _buffers = new(StringComparer.Ordinal);

        public string GetOrCreate(string key) => _buffers.TryGetValue(key, out var value) ? value : "";

        public void Set(string key, string value) => _buffers[key] = value;

        public void Remove(string key) => _buffers.Remove(key);

        public void Clear() => _buffers.Clear();
    }
}
