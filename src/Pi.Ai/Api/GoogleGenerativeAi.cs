using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Google Generative AI 专属流选项。对应 TS <c>GoogleOptions</c>（api/google-generative-ai.ts）。</summary>
public record GoogleOptions
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

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    /// <summary>工具选择（auto/none/any）。对应 TS <c>toolChoice</c>。</summary>
    public string? ToolChoice { get; init; }

    /// <summary>思考控制。对应 TS <c>thinking</c>。</summary>
    public GoogleThinkingControl? Thinking { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>思考控制。对应 TS <c>thinking: {enabled, budgetTokens?, level?}</c>。</summary>
public record GoogleThinkingControl
{
    public required bool Enabled { get; init; }

    /// <summary>-1 动态、0 禁用。</summary>
    public long? BudgetTokens { get; init; }

    /// <summary>Gemini 3 离散档位（MINIMAL/LOW/MEDIUM/HIGH）。</summary>
    public string? Level { get; init; }
}

/// <summary>
/// Google Generative AI（Gemini）REST 直调实现。对应 TS <c>api/google-generative-ai.ts</c>：
/// POST :streamGenerateContent?alt=sse，GenerateContentResponse 流解析（thought part、
/// thoughtSignature 保留、functionCall/Response）、usageMetadata（含 thoughtsTokenCount）、
/// finishReason 映射与 toolUse 升级。与 Vertex 共享流消费逻辑。
/// </summary>
public static class GoogleGenerativeAi
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private static int _toolCallCounter;

    /// <summary>流式生成。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        GoogleOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider);
            try
            {
                var apiKey = options?.ApiKey
                    ?? throw new InvalidOperationException($"No API key for provider: {model.Provider}");
                var baseUrl = string.IsNullOrEmpty(model.BaseUrl) ? DefaultBaseUrl : model.BaseUrl.TrimEnd('/');
                var payload = BuildParams(model, context, options);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                    if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                    var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"{baseUrl}/models/{model.Id}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(apiKey)}")
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("User-Agent", PiUserAgent.Get());
                    if (model.Headers is not null)
                    {
                        foreach (var (key, value) in model.Headers) request.Headers.TryAddWithoutValidation(key, value);
                    }
                    if (options?.Headers is not null)
                    {
                        foreach (var (key, value) in options.Headers)
                        {
                            if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                    try
                    {
                        var httpResponse = await client.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token).ConfigureAwait(false);
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            var body = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                            throw new ProviderHttpException(
                                (int)httpResponse.StatusCode, body,
                                headers: httpResponse.Headers.ToDictionary(
                                    kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase));
                        }
                        return httpResponse;
                    }
                    catch (OperationCanceledException) when (signal.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Request aborted", signal);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"Request timed out after {options?.TimeoutMs}ms");
                    }
                }, new ProviderRetryOptions
                {
                    MaxRetries = options?.MaxRetries,
                    MaxRetryDelayMs = options?.MaxRetryDelayMs,
                    Signal = signal,
                }).ConfigureAwait(false);

                if (options?.OnResponse is { } onResponse)
                {
                    await onResponse(new ProviderResponse
                    {
                        Status = (int)response.StatusCode,
                        Headers = response.Headers.ToDictionary(
                            kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
                    }, model).ConfigureAwait(false);
                }

                stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await ConsumeContentStreamAsync(
                    model, output, stream, content, options, signal, cancellationToken).ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Google stream ended without a finish reason");
                }
                if (output.StopReason is StopReason.Aborted or StopReason.Error)
                {
                    var errorMessage = output.RawStopReason is not null
                        ? $"Provider stopped with: {output.RawStopReason}"
                        : "An unknown error occurred";
                    throw new InvalidOperationException(output.ErrorMessage ?? errorMessage);
                }

                stream.Push(new AssistantMessageEvent.Done(output.StopReason, output.Snapshot()));
                stream.End(output.Snapshot());
            }
            catch (Exception error)
            {
                output.StopReason = signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = ProviderError.Format(ProviderError.Normalize(error));
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>简单入口。对应 TS <c>streamSimple</c>：无 reasoning → 禁用思考；budget 型按模型表取预算。</summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
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

        GoogleThinkingControl? thinking;
        if (options?.Reasoning is not { } reasoning)
        {
            thinking = new GoogleThinkingControl { Enabled = false };
        }
        else
        {
            var clamped = ThinkingLevels.Clamp(model, reasoning);
            if (clamped == "off")
            {
                thinking = new GoogleThinkingControl { Enabled = false };
            }
            else if (GoogleShared.UsesGoogleThinkingLevel(model))
            {
                thinking = new GoogleThinkingControl
                {
                    Enabled = true,
                    Level = GoogleShared.ToGoogleThinkingLevel(GoogleShared.ResolveGoogleThinkingLevel(model, clamped)),
                };
            }
            else
            {
                thinking = new GoogleThinkingControl
                {
                    Enabled = true,
                    BudgetTokens = GetGoogleBudget(model, GoogleShared.ResolveGoogleThinkingLevel(model, clamped), options.ThinkingBudgets),
                };
            }
        }

        return Stream(model, context, new GoogleOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            SessionId = options?.SessionId,
            Env = options?.Env,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice is System.Text.Json.Nodes.JsonValue { } toolChoiceValue
                && toolChoiceValue.TryGetValue<string>(out var toolChoiceText) ? toolChoiceText : null,
            SamplingParams = samplingParams,
            MaxTokens = maxTokens,
            Temperature = options?.Temperature,
            Thinking = thinking,
        }, httpClient, cancellationToken);
    }

    /// <summary>token 预算表（2.5-pro / 2.5-flash；其余 -1 动态）。对应 TS <c>getGoogleBudget</c>。</summary>
    public static long GetGoogleBudget(
        ModelSpec model, string level, ThinkingBudgets? customBudgets = null)
    {
        var custom = level switch
        {
            "minimal" => customBudgets?.Minimal,
            "low" => customBudgets?.Low,
            "medium" => customBudgets?.Medium,
            "high" => customBudgets?.High,
            _ => null,
        };
        if (custom is { } explicitBudget) return explicitBudget;

        if (model.Id.Contains("2.5-pro"))
        {
            return level switch
            {
                "minimal" => 128,
                "low" => 2048,
                "medium" => 8192,
                "high" => 32768,
                _ => -1,
            };
        }
        if (model.Id.Contains("2.5-flash"))
        {
            return level switch
            {
                "minimal" => 128,
                "low" => 2048,
                "medium" => 8192,
                "high" => 24576,
                _ => -1,
            };
        }
        return -1;
    }

    /// <summary>构建 generateContent 载荷。对应 TS <c>buildParams</c>（google-vertex 形状，两 API 共用）。</summary>
    public static JsonObject BuildParams(
        ModelSpec model, TranscriptContext context, GoogleOptions? options = null)
    {
        var (systemInstruction, contents) = GoogleShared.ConvertMessages(model, context);
        var initialSystemMessage = Transcript.GetInitialSystemMessage(context.Messages);
        var currentTools = Transcript.GetCurrentTools(context.Messages);

        var generationConfig = new JsonObject();
        if (options?.Temperature is { } temperature) generationConfig["temperature"] = temperature;
        if (options?.MaxTokens is { } maxTokens) generationConfig["maxOutputTokens"] = maxTokens;
        foreach (var (key, value) in options?.SamplingParams ?? new JsonObject())
        {
            generationConfig[key] = value?.DeepClone();
        }

        var supportsStrictMode = GoogleShared.SupportsGoogleStrictToolSampling(model.Id);
        var functionCallingMode = currentTools.Count > 0
            ? GoogleShared.ResolveGoogleFunctionCallingMode(currentTools, options?.ToolChoice, supportsStrictMode)
            : null;

        // REST wire 形状（TS 经 SDK 的 config 字段在传输层展开为此形状）：
        // systemInstruction 是 parts 对象；thinkingConfig 位于 generationConfig 内。
        if (!string.IsNullOrEmpty(systemInstruction))
        {
            generationConfig["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = SanitizeUnicode.SanitizeSurrogates(systemInstruction),
                }),
            };
        }
        if (currentTools.Count > 0)
        {
            var tools = GoogleShared.ConvertTools(currentTools, useParameters: false, supportsStrictMode);
            if (tools is not null) generationConfig["tools"] = tools;
        }
        if (functionCallingMode is not null)
        {
            generationConfig["toolConfig"] = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject { ["mode"] = functionCallingMode },
            };
        }

        var thinking = options?.Thinking;
        if (thinking is { Enabled: true } && model.Reasoning)
        {
            var thinkingConfig = new JsonObject { ["includeThoughts"] = true };
            if (thinking.Level is { } level)
            {
                thinkingConfig["thinkingLevel"] = level;
            }
            else if (thinking.BudgetTokens is { } budget)
            {
                thinkingConfig["thinkingBudget"] = budget;
            }
            generationConfig["thinkingConfig"] = thinkingConfig;
        }
        else if (model.Reasoning && thinking is { Enabled: false })
        {
            generationConfig["thinkingConfig"] = GoogleShared.GetDisabledGoogleThinkingConfig(model);
        }

        var payload = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };
        return payload;
    }

    // =============================================================================
    // 流消费（Vertex 与 Generative AI 共用同一 GenerateContentResponse 形状）
    // =============================================================================

    /// <summary>
    /// 消费 GenerateContentResponse SSE 流：text/thought part 折叠为文本/思维块
    /// （thoughtSignature 保留首个非空值）、functionCall 生成唯一 ID、finishReason
    /// 映射并升级 toolUse、usageMetadata 含 reasoning token。对应 TS stream 内循环。
    /// </summary>
    public static async Task ConsumeContentStreamAsync(
        ModelSpec model,
        OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream,
        System.IO.Stream body,
        GoogleOptions? options,
        CancellationToken signal,
        CancellationToken cancellationToken)
    {
        var hasCurrentBlock = false;
        string? currentBlockType = null;

        int BlockIndex() => output.Content.Count - 1;

        void FinishCurrentBlock()
        {
            if (!hasCurrentBlock) return;
            var block = output.Content[BlockIndex()];
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
            hasCurrentBlock = false;
            currentBlockType = null;
        }

        var events = AiSse.EventsAsync(body, cancellationToken);
        await foreach (var sseEvent in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            signal.ThrowIfCancellationRequested();
            if (JsonNode.Parse(sseEvent.Data) is not JsonObject chunk) continue;
            if (options?.OnProviderStreamEvent is { } onProviderStreamEvent)
            {
                await onProviderStreamEvent(chunk, model).ConfigureAwait(false);
            }
            // responseId 是每个响应的输出标识（多 chunk 保留首个）。
            if (output.ResponseId is null && chunk.Str("responseId") is { } responseId)
            {
                output.ResponseId = responseId;
            }
            if (chunk["candidates"] is not JsonArray candidates
                || candidates.Count == 0
                || candidates[0] is not JsonObject candidate)
            {
                continue;
            }

            if (candidate["content"]?["parts"] is JsonArray parts)
            {
                foreach (var partNode in parts)
                {
                    if (partNode is not JsonObject part) continue;

                    if (part["text"] is System.Text.Json.Nodes.JsonValue { } textValue
                        && textValue.TryGetValue<string>(out var text))
                    {
                        var isThinking = GoogleShared.IsThinkingPart(part);
                        var signature = part.Str("thoughtSignature");
                        if (!hasCurrentBlock
                            || (isThinking && currentBlockType != "thinking")
                            || (!isThinking && currentBlockType != "text"))
                        {
                            FinishCurrentBlock();
                            output.Content.Add(isThinking
                                ? new ThinkingContent("") { Signature = null }
                                : new TextContent(""));
                            hasCurrentBlock = true;
                            currentBlockType = isThinking ? "thinking" : "text";
                            stream.Push(isThinking
                                ? new AssistantMessageEvent.ThinkingStart(BlockIndex(), output.Snapshot())
                                : new AssistantMessageEvent.TextStart(BlockIndex(), output.Snapshot()));
                        }

                        var index = BlockIndex();
                        if (output.Content[index] is ThinkingContent thinkingBlock)
                        {
                            output.Content[index] = thinkingBlock with
                            {
                                Thinking = thinkingBlock.Thinking + text,
                                Signature = GoogleShared.RetainThoughtSignature(thinkingBlock.Signature, signature),
                            };
                            stream.Push(new AssistantMessageEvent.ThinkingDelta(
                                index, text, 0, output.Content[index] is ThinkingContent t ? t.Signature : null,
                                output.Snapshot()));
                        }
                        else if (output.Content[index] is TextContent textBlock)
                        {
                            output.Content[index] = textBlock with
                            {
                                Text = textBlock.Text + text,
                                TextSignature = GoogleShared.RetainThoughtSignature(textBlock.TextSignature, signature),
                            };
                            stream.Push(new AssistantMessageEvent.TextDelta(
                                index, text, 0, output.Snapshot()));
                        }
                    }

                    if (part["functionCall"] is JsonObject functionCall)
                    {
                        FinishCurrentBlock();

                        var providedId = functionCall.Str("id");
                        var needsNewId = string.IsNullOrEmpty(providedId)
                            || output.Content.OfType<ToolCallContent>().Any(call => call.Id == providedId);
                        var toolCallId = needsNewId
                            ? $"{functionCall.Str("name") ?? "call"}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{++_toolCallCounter}"
                            : providedId!;

                        var toolCall = new ToolCallContent(
                            toolCallId,
                            functionCall.Str("name") ?? "",
                            functionCall["args"]?.DeepClone() ?? new JsonObject())
                        {
                            ThoughtSignature = part.Str("thoughtSignature"),
                        };
                        output.Content.Add(toolCall);
                        stream.Push(new AssistantMessageEvent.ToolCallStart(BlockIndex(), output.Snapshot()));
                        stream.Push(new AssistantMessageEvent.ToolCallDelta(
                            BlockIndex(), BlockIndex(),
                            (toolCall.Arguments as System.Text.Json.Nodes.JsonNode)?.ToJsonString() ?? "{}",
                            output.Snapshot()));
                        stream.Push(new AssistantMessageEvent.ToolCallEnd(
                            BlockIndex(), toolCall, output.Snapshot()));
                    }
                }
            }

            if (candidate.Str("finishReason") is { } finishReason)
            {
                output.RawStopReason = finishReason;
                output.StopReason = GoogleShared.MapStopReason(finishReason);
                if (output.Content.Any(block => block is ToolCallContent) && output.StopReason == StopReason.Stop)
                {
                    output.StopReason = StopReason.ToolUse;
                }
            }

            if (chunk["usageMetadata"] is JsonObject usage)
            {
                var prompt = (long)(usage.Num("promptTokenCount") ?? 0);
                var cached = (long)(usage.Num("cachedContentTokenCount") ?? 0);
                var candidatesTokens = (long)(usage.Num("candidatesTokenCount") ?? 0);
                var thoughts = (long)(usage.Num("thoughtsTokenCount") ?? 0);
                output.Usage = new Usage(
                    Math.Max(0, prompt - cached),
                    candidatesTokens + thoughts,
                    cached,
                    0,
                    null,
                    thoughts);
            }
        }

        FinishCurrentBlock();
    }
}
