using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Claude 思考内容回传形态。对应 TS <c>BedrockThinkingDisplay</c>。</summary>
public record BedrockOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    public string? SessionId { get; init; }

    public string? CacheRetention { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    public string? Region { get; init; }

    /// <summary>工具选择（auto/any/none 或 {type:tool,name}）。对应 TS <c>toolChoice</c>。</summary>
    public System.Text.Json.Nodes.JsonNode? ToolChoice { get; init; }

    /// <summary>思考档位。对应 TS <c>reasoning</c>。</summary>
    public string? Reasoning { get; init; }

    /// <summary>思考预算覆盖。对应 TS <c>thinkingBudgets</c>。</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }

    /// <summary>Claude 4.x 交错思考（anthropic_beta）。对应 TS <c>interleavedThinking</c>。</summary>
    public bool? InterleavedThinking { get; init; }

    /// <summary>思考回传形态（summarized/omitted）。对应 TS <c>thinkingDisplay</c>。</summary>
    public string? ThinkingDisplay { get; init; }

    /// <summary>推理请求成本分摊标签。对应 TS <c>requestMetadata</c>。</summary>
    public System.Text.Json.Nodes.JsonObject? RequestMetadata { get; init; }

    /// <summary>Bedrock API key Bearer 令牌（绕过 SigV4）。对应 TS <c>bearerToken</c>。</summary>
    public string? BearerToken { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>
/// AWS Bedrock Converse Stream REST 直调实现。对应 TS <c>api/bedrock-converse-stream.ts</c>：
/// SigV4 签名（或 Bearer API key）POST /model/{id}/converse-stream，AWS event-stream
/// 二进制响应解码，思考块（reasoningContent 签名/加密 redactedContent）、缓存点
/// （Claude cachePoint + ttl）、工具结果单消息合并、additionalModelRequestFields
/// （adaptive thinking / block binding / interleaved beta / GovCloud 规避）。
/// SDK 的 profile/文件凭据链为运行时特性，C# 覆盖 env 凭据与 Bearer 路径。
/// </summary>
public static class BedrockConverseStream
{
    private const string EmptyTextPlaceholder = "<empty>";
    private const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";
    private const string RedactedThinkingPlaceholder = "[Reasoning redacted]";
    private const string BedrockDataRetentionDocsUrl =
        "https://docs.aws.amazon.com/bedrock/latest/userguide/data-retention.html";

    /// <summary>流式生成。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        BedrockOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        // Bedrock 无会话中段 system 消息——折叠进前导提示。
        var normalizedContext = Transcript.CollapseSystemMessages(context);
        var client = httpClient ?? new HttpClient();
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider);
            var drafts = new BlockDrafts(output);
            string? responseRequestId = null;
            try
            {
                var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
                var isClaude = IsAnthropicClaudeModel(model);
                var inferenceMaxTokens = options?.MaxTokens ?? (isClaude && model.MaxTokens > 0 ? (int)model.MaxTokens : null);
                var initialSystemMessage = Transcript.GetInitialSystemMessage(normalizedContext.Messages);
                var initialSystemPrompt = initialSystemMessage is not null
                    ? Text.GetSystemMessageText(initialSystemMessage)
                    : null;

                var payload = BuildCommandInput(
                    model, normalizedContext, options, cacheRetention, isClaude,
                    inferenceMaxTokens, initialSystemPrompt);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                var bodyJson = payload.ToJsonString();
                var (url, authHeaders) = BuildEndpointAndAuth(model, options);
                var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(bodyBytes),
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                foreach (var (key, value) in authHeaders)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
                if (options?.Headers is not null)
                {
                    foreach (var (key, value) in options.Headers)
                    {
                        // host/x-amz-*/authorization 由 SigV4/bearer 拥有（对齐 TS isReservedHeader）。
                        if (IsReservedHeader(key) || value is null) continue;
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token).ConfigureAwait(false);
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
                    var errorBody = await response.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                    throw new ProviderHttpException((int)response.StatusCode, errorBody);
                }

                stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                foreach (var message in AwsEventStream.Decode(content))
                {
                    signal.ThrowIfCancellationRequested();
                    var eventType = message.Headers.GetValueOrDefault(":event-type");
                    if (message.Payload is not { } @event) continue;
                    if (options?.OnProviderStreamEvent is { } onProviderStreamEvent)
                    {
                        await onProviderStreamEvent(@event, model).ConfigureAwait(false);
                    }
                    switch (eventType)
                    {
                        case "messageStart":
                            if (@event.Obj("messageStart")?.Str("role") == "user")
                            {
                                throw new InvalidOperationException(
                                    "Unexpected assistant message start but got user message start instead");
                            }
                            stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                            break;
                        case "contentBlockStart":
                            HandleContentBlockStart(@event.Obj("contentBlockStart") ?? [], drafts, output, stream);
                            break;
                        case "contentBlockDelta":
                            HandleContentBlockDelta(@event.Obj("contentBlockDelta") ?? [], drafts, output, stream);
                            break;
                        case "contentBlockStop":
                            HandleContentBlockStop(@event.Obj("contentBlockStop") ?? [], drafts, output, stream);
                            break;
                        case "messageStop":
                        {
                            var messageStop = @event.Obj("messageStop") ?? [];
                            output.RawStopReason = messageStop.Str("stopReason");
                            var (stopReason, errorMessage) = MapStopReason(messageStop.Str("stopReason"));
                            output.StopReason = stopReason;
                            if (errorMessage is not null) output.ErrorMessage = errorMessage;
                            break;
                        }
                        case "metadata":
                            HandleMetadata(@event.Obj("metadata") ?? [], model, output);
                            break;
                        case "internalServerException":
                        case "modelStreamErrorException":
                        case "validationException":
                        case "throttlingException":
                        case "serviceUnavailableException":
                            throw new InvalidOperationException(
                                $"{eventType}: {@event.Obj(eventType)?.ToJsonString() ?? ""}");
                    }
                }

                if (signal.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Request was aborted");
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Bedrock stream ended without a stop reason");
                }
                if (output.StopReason is StopReason.Error or StopReason.Aborted)
                {
                    throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
                }

                // 流可能未停每个块就收尾：这里统一终结。
                drafts.FinalizeAll();
                stream.Push(new AssistantMessageEvent.Done(output.StopReason, output.Snapshot()));
                stream.End(output.Snapshot());
            }
            catch (Exception error)
            {
                drafts.FinalizeAll();
                output.StopReason = signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = FormatBedrockError(error);
                if (output.StopReason == StopReason.Error)
                {
                    AppendBedrockFailureDiagnostic(output, error, responseRequestId);
                }
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>简单入口。对应 TS <c>streamSimple</c>：Claude 走思考预算调整（adaptive 模型除外）。</summary>
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
        var budgets = options?.ThinkingBudgets;
        var maxTokensOut = maxTokens;
        if (options?.Reasoning is { } reasoning && IsAnthropicClaudeModel(model)
            && !SupportsAdaptiveThinking(model.Id, model.Name))
        {
            // 无显式上限时由 helper 用模型上限；不能折叠成 0，否则预算吃满 maxTokens。
            var adjusted = SimpleOptions.AdjustMaxTokensForThinking(
                maxTokens, model.MaxTokens, reasoning, budgets);
            maxTokensOut = SimpleOptions.ClampMaxTokensToContext(model, context, (int)adjusted.MaxTokens);
            var clamped = SimpleOptions.ClampReasoning(reasoning) ?? "high";
            budgets = new ThinkingBudgets(
                budgets?.Minimal, budgets?.Low, budgets?.Medium, budgets?.High)
            {
                Low = clamped == "low" ? Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokensOut.Value - 1024)) : budgets?.Low,
                Medium = clamped == "medium" ? Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokensOut.Value - 1024)) : budgets?.Medium,
                High = clamped == "high" ? Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokensOut.Value - 1024)) : budgets?.High,
                Minimal = clamped == "minimal" ? Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokensOut.Value - 1024)) : budgets?.Minimal,
            };
        }

        return Stream(model, context, new BedrockOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxTokens = maxTokensOut,
            Temperature = options?.Temperature,
            SessionId = options?.SessionId,
            CacheRetention = options?.CacheRetention,
            Env = options?.Env,
            SamplingParams = samplingParams,
            ToolChoice = options?.ToolChoice,
            Reasoning = options?.Reasoning,
            ThinkingBudgets = budgets,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
        }, httpClient, cancellationToken);
    }

    // =============================================================================
    // 模型能力判定
    // =============================================================================

    /// <summary>模型匹配候选（id/name 各做连字符归一）。对应 TS <c>getModelMatchCandidates</c>。</summary>
    public static IReadOnlyList<string> GetModelMatchCandidates(string modelId, string? modelName)
    {
        var values = string.IsNullOrEmpty(modelName) ? [modelId] : new[] { modelId, modelName };
        var candidates = new List<string>();
        foreach (var value in values)
        {
            var lower = value.ToLowerInvariant();
            candidates.Add(lower);
            candidates.Add(System.Text.RegularExpressions.Regex.Replace(lower, @"[\s_.:]+", "-"));
        }
        return candidates;
    }

    /// <summary>adaptive thinking（Opus 4.6+ / Sonnet 4.6+）。对应 TS <c>supportsAdaptiveThinking</c>。</summary>
    public static bool SupportsAdaptiveThinking(string modelId, string? modelName = null)
        => GetModelMatchCandidates(modelId, modelName).Any(s =>
            s.Contains("opus-4-6") || s.Contains("opus-4-7") || s.Contains("opus-4-8") || s.Contains("opus-5")
            || s.Contains("sonnet-4-6") || s.Contains("sonnet-5") || s.Contains("fable-5"));

    /// <summary>原生 xhigh effort。对应 TS <c>supportsNativeXhighEffort</c>。</summary>
    public static bool SupportsNativeXhighEffort(string modelId, string? modelName = null)
        => GetModelMatchCandidates(modelId, modelName).Any(s =>
            s.Contains("opus-4-7") || s.Contains("opus-4-8") || s.Contains("opus-5")
            || s.Contains("sonnet-5") || s.Contains("fable-5"));

    /// <summary>是否接受 thinking.block_binding。对应 TS <c>supportsThinkingBlockBinding</c>。</summary>
    public static bool SupportsThinkingBlockBinding(string modelId, string? modelName = null)
        => SupportsNativeXhighEffort(modelId, modelName);

    /// <summary>Bedrock 上的 Anthropic Claude 模型。对应 TS <c>isAnthropicClaudeModel</c>。</summary>
    public static bool IsAnthropicClaudeModel(ModelSpec model)
    {
        var id = model.Id.ToLowerInvariant();
        var name = (model.Name ?? "").ToLowerInvariant();
        return id.Contains("anthropic.claude") || id.Contains("anthropic/claude")
            || name.Contains("anthropic.claude") || name.Contains("anthropic/claude")
            || name.Contains("claude");
    }

    /// <summary>提示缓存支持判定（Claude 3.5 Haiku/3.7 Sonnet/4.x/5；可 AWS_BEDROCK_FORCE_CACHE 强制）。对应 TS <c>supportsPromptCaching</c>。</summary>
    public static bool SupportsPromptCaching(ModelSpec model, IReadOnlyDictionary<string, string>? env = null)
    {
        var candidates = GetModelMatchCandidates(model.Id, model.Name);
        if (!candidates.Any(s => s.Contains("claude")))
        {
            return ProviderEnvValue.Get("AWS_BEDROCK_FORCE_CACHE", env) == "1";
        }
        if (candidates.Any(s => s.Contains("fable-5") || s.Contains("opus-5") || s.Contains("sonnet-5"))) return true;
        if (candidates.Any(s => s.Contains("-4-"))) return true;
        if (candidates.Any(s => s.Contains("claude-3-7-sonnet"))) return true;
        if (candidates.Any(s => s.Contains("claude-3-5-haiku"))) return true;
        return false;
    }

    /// <summary>思考档位 → effort（xhigh/max 需原生支持）。对应 TS <c>mapThinkingLevelToEffort</c>。</summary>
    public static string MapThinkingLevelToEffort(ModelSpec model, string? level)
    {
        if (level == "xhigh" && SupportsNativeXhighEffort(model.Id, model.Name)) return "xhigh";
        var mapped = level is not null ? model.ThinkingLevelMap?[level] : null;
        if (mapped is not null) return mapped;
        return level switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => "high",
        };
    }

    /// <summary>缓存保留偏好（缺省 short；PI_CACHE_RETENTION 兼容）。对应 TS <c>resolveCacheRetention</c>。</summary>
    public static string ResolveCacheRetention(string? cacheRetention, IReadOnlyDictionary<string, string>? env)
        => cacheRetention
           ?? (ProviderEnvValue.Get("PI_CACHE_RETENTION", env) == "long" ? "long" : "short");

    // =============================================================================
    // 载荷构建
    // =============================================================================

    /// <summary>构建 ConverseStream 请求体。对应 TS commandInput 组装 + buildAdditionalModelRequestFields。</summary>
    public static JsonObject BuildCommandInput(
        ModelSpec model,
        TranscriptContext normalizedContext,
        BedrockOptions? options,
        string cacheRetention,
        bool isClaude,
        int? inferenceMaxTokens,
        string? initialSystemPrompt)
    {
        var commandInput = new JsonObject
        {
            ["modelId"] = model.Id,
            ["messages"] = ConvertMessages(normalizedContext, model, cacheRetention, options?.Env),
            ["system"] = BuildSystemPrompt(initialSystemPrompt, model, cacheRetention, options?.Env),
            ["inferenceConfig"] = new JsonObject
            {
                ["maxTokens"] = inferenceMaxTokens,
                ["temperature"] = options?.Temperature,
            },
            ["toolConfig"] = ConvertToolConfig(
                Transcript.GetCurrentTools(normalizedContext.Messages), options?.ToolChoice,
                model.Compat?["supportsStrictMode"] is System.Text.Json.Nodes.JsonValue { } strictValue
                    && strictValue.TryGetValue<bool>(out var strictFlag) && strictFlag),
            ["additionalModelRequestFields"] = BuildAdditionalModelRequestFields(model, options),
        };
        if (options?.RequestMetadata is { Count: > 0 } metadata)
        {
            commandInput["requestMetadata"] = metadata.DeepClone();
        }
        return commandInput;
    }

    /// <summary>system 提示块 + Claude 缓存点。对应 TS <c>buildSystemPrompt</c>。</summary>
    public static JsonArray? BuildSystemPrompt(
        string? systemPrompt, ModelSpec model, string cacheRetention, IReadOnlyDictionary<string, string>? env)
    {
        if (string.IsNullOrEmpty(systemPrompt)) return null;
        var blocks = new JsonArray(new JsonObject
        {
            ["text"] = SanitizeUnicode.SanitizeSurrogates(systemPrompt),
        });
        if (cacheRetention != "none" && SupportsPromptCaching(model, env))
        {
            blocks.Add(BuildCachePoint(cacheRetention));
        }
        return blocks;
    }

    private static JsonObject BuildCachePoint(string cacheRetention)
    {
        var cachePoint = new JsonObject { ["type"] = "default" };
        if (cacheRetention == "long") cachePoint["ttl"] = "1h";
        return new JsonObject { ["cachePoint"] = cachePoint };
    }

    /// <summary>工具配置（toolSpec + inputSchema.json + strict）。对应 TS <c>convertToolConfig</c>。</summary>
    public static JsonObject? ConvertToolConfig(
        IReadOnlyList<ToolDefinition> tools, System.Text.Json.Nodes.JsonNode? toolChoice, bool supportsStrictMode)
    {
        if (tools.Count == 0) return null;
        var choiceText = (toolChoice as System.Text.Json.Nodes.JsonValue)?.GetValue<string>();
        if (choiceText == "none") return null;

        var bedrockTools = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var spec = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = new JsonObject
                {
                    ["json"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict) ?? new JsonObject(),
                },
            };
            if (strict == true) spec["strict"] = true;
            bedrockTools.Add(new JsonObject { ["toolSpec"] = spec });
        }

        JsonObject? bedrockToolChoice = choiceText switch
        {
            "auto" => new JsonObject { ["auto"] = new JsonObject() },
            "any" => new JsonObject { ["any"] = new JsonObject() },
            _ => null,
        };
        if (bedrockToolChoice is null && toolChoice is JsonObject choiceObject
            && choiceObject.Str("type") == "tool")
        {
            bedrockToolChoice = new JsonObject
            {
                ["tool"] = new JsonObject { ["name"] = choiceObject.Str("name") ?? "" },
            };
        }

        var config = new JsonObject { ["tools"] = bedrockTools };
        if (bedrockToolChoice is not null) config["toolChoice"] = bedrockToolChoice;
        return config;
    }

    /// <summary>思考扩展字段（adaptive/budget、display、block binding、interleaved beta、GovCloud）。对应 TS <c>buildAdditionalModelRequestFields</c>。</summary>
    public static JsonObject? BuildAdditionalModelRequestFields(ModelSpec model, BedrockOptions? options)
    {
        if (string.IsNullOrEmpty(options?.Reasoning) || !model.Reasoning) return null;
        if (!IsAnthropicClaudeModel(model)) return null;

        var isGovCloud = IsGovCloudBedrockTarget(model, options);
        var display = isGovCloud ? null : options.ThinkingDisplay ?? "summarized";
        var useBlockBinding = !isGovCloud && SupportsThinkingBlockBinding(model.Id, model.Name);
        var adaptive = SupportsAdaptiveThinking(model.Id, model.Name);
        JsonObject result;
        if (adaptive)
        {
            var thinking = new JsonObject { ["type"] = "adaptive" };
            if (display is not null) thinking["display"] = display;
            if (useBlockBinding)
            {
                thinking["block_binding"] = new JsonObject
                {
                    ["prefix_mismatch_behavior"] = "drop_block",
                };
            }
            result = new JsonObject
            {
                ["thinking"] = thinking,
                ["output_config"] = new JsonObject
                {
                    ["effort"] = MapThinkingLevelToEffort(model, options.Reasoning),
                },
            };
            if (useBlockBinding) result["anthropic_beta"] = new JsonArray(ThinkingBindingControlsBeta);
            return result;
        }

        // 预算型 Claude：xhigh/max 收敛到 high。
        var level = options.Reasoning is "xhigh" or "max" ? "high" : options.Reasoning;
        var budget = level switch
        {
            "minimal" => options.ThinkingBudgets?.Minimal ?? 1024,
            "low" => options.ThinkingBudgets?.Low ?? 2048,
            "medium" => options.ThinkingBudgets?.Medium ?? 8192,
            "high" => options.ThinkingBudgets?.High ?? 16384,
            _ => 16384,
        };
        var enabledThinking = new JsonObject
        {
            ["type"] = "enabled",
            ["budget_tokens"] = budget,
        };
        if (display is not null) enabledThinking["display"] = display;
        result = new JsonObject { ["thinking"] = enabledThinking };
        if (options.InterleavedThinking ?? true)
        {
            result["anthropic_beta"] = new JsonArray("interleaved-thinking-2025-05-14");
        }
        return result;
    }

    /// <summary>GovCloud 目标判定。对应 TS <c>isGovCloudBedrockTarget</c>。</summary>
    public static bool IsGovCloudBedrockTarget(ModelSpec model, BedrockOptions? options)
    {
        var region = options?.Region
            ?? ProviderEnvValue.Get("AWS_REGION", options?.Env)
            ?? ProviderEnvValue.Get("AWS_DEFAULT_REGION", options?.Env);
        if (region is not null && region.ToLowerInvariant().StartsWith("us-gov-", StringComparison.Ordinal)) return true;
        var modelId = model.Id.ToLowerInvariant();
        return modelId.StartsWith("us-gov.", StringComparison.Ordinal)
            || modelId.StartsWith("arn:aws-us-gov:", StringComparison.Ordinal);
    }

    // =============================================================================
    // 消息转换
    // =============================================================================

    private static string NormalizeToolCallId(string id)
    {
        var sanitized = new string([.. id.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_')]);
        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }

    private static JsonObject? CreateNonBlankTextBlock(string text)
    {
        var sanitized = SanitizeUnicode.SanitizeSurrogates(text);
        return sanitized.Trim().Length == 0 ? null : new JsonObject { ["text"] = sanitized };
    }

    private static JsonObject CreateRequiredTextBlock(string text)
        => CreateNonBlankTextBlock(text) ?? new JsonObject { ["text"] = EmptyTextPlaceholder };

    /// <summary>arguments → Bedrock document（过滤空键）。对应 TS <c>sanitizeBedrockDocument</c>。</summary>
    public static JsonNode? SanitizeBedrockDocument(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(SanitizeBedrockDocument(item));
            return result;
        }
        if (value is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var (key, nested) in obj)
            {
                if (key.Length == 0) continue;
                result[key] = SanitizeBedrockDocument(nested);
            }
            return result;
        }
        return value?.DeepClone();
    }

    /// <summary>图片块（格式白名单 + base64 字节）。对应 TS <c>createImageBlock</c>。</summary>
    public static JsonObject CreateImageBlock(string mimeType, string data)
    {
        var format = mimeType switch
        {
            "image/jpeg" or "image/jpg" => "jpeg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => throw new InvalidOperationException($"Unknown image type: {mimeType}"),
        };
        return new JsonObject
        {
            ["source"] = new JsonObject { ["bytes"] = data },
            ["format"] = format,
        };
    }

    private static JsonArray ConvertToolResultContent(IReadOnlyList<ContentBlock> content)
    {
        var result = new JsonArray();
        foreach (var block in content)
        {
            if (block is ImageContent image)
            {
                result.Add(new JsonObject { ["image"] = CreateImageBlock(image.MimeType ?? "", image.Data) });
            }
            else if (block is TextContent text)
            {
                var textBlock = CreateNonBlankTextBlock(text.Text ?? "");
                if (textBlock is not null) result.Add(textBlock);
            }
        }
        if (result.Count == 0) result.Add(new JsonObject { ["text"] = EmptyTextPlaceholder });
        return result;
    }

    /// <summary>
    /// 消息 → Converse Message[]。对应 TS <c>convertMessages</c>：空内容跳过、
    /// 思考签名仅 Claude 回传、加密 reasoning 走 redactedContent、连续 toolResult
    /// 合并单 user 消息、最后 user 消息附加缓存点。
    /// </summary>
    public static JsonArray ConvertMessages(
        TranscriptContext context, ModelSpec model, string cacheRetention, IReadOnlyDictionary<string, string>? env)
    {
        var result = new JsonArray();
        var transformedMessages = TransformMessages.Transform(
            Transcript.WithoutInitialSystemMessage(context.Messages), model,
            (id, _, _) => NormalizeToolCallId(id));
        var supportsSignature = IsAnthropicClaudeModel(model);

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var message = transformedMessages[i];
            switch (message)
            {
                case UserMessage user:
                {
                    var content = new JsonArray();
                    foreach (var block in user.Content)
                    {
                        if (block is TextContent text)
                        {
                            var textBlock = CreateNonBlankTextBlock(text.Text ?? "");
                            if (textBlock is not null) content.Add(textBlock);
                        }
                        else if (block is ImageContent image)
                        {
                            content.Add(new JsonObject { ["image"] = CreateImageBlock(image.MimeType ?? "", image.Data) });
                        }
                    }
                    if (content.Count == 0) content.Add(new JsonObject { ["text"] = EmptyTextPlaceholder });
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
                case AssistantMessage assistant:
                {
                    // 空 content（如中止请求）跳过——Bedrock 拒绝空内容数组。
                    if (assistant.Content.Count == 0) break;
                    var contentBlocks = new JsonArray();
                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent textBlock:
                            {
                                var textNode = CreateNonBlankTextBlock(textBlock.Text ?? "");
                                if (textNode is not null) contentBlocks.Add(textNode);
                                break;
                            }
                            case ToolCallContent toolCall:
                                contentBlocks.Add(new JsonObject
                                {
                                    ["toolUse"] = new JsonObject
                                    {
                                        ["toolUseId"] = toolCall.Id,
                                        ["name"] = toolCall.Name,
                                        ["input"] = SanitizeBedrockDocument(
                                            toolCall.Arguments as System.Text.Json.Nodes.JsonNode)
                                            ?? new JsonObject(),
                                    },
                                });
                                break;
                            case ThinkingContent thinking:
                            {
                                // 加密 reasoning 不透明：以 redactedContent 原样重放。
                                if (thinking.Redacted == true)
                                {
                                    if (thinking.Signature is { Length: > 0 } stored)
                                    {
                                        contentBlocks.Add(new JsonObject
                                        {
                                            ["reasoningContent"] = new JsonObject
                                            {
                                                ["redactedContent"] = stored,
                                            },
                                        });
                                    }
                                    break;
                                }
                                var text = SanitizeUnicode.SanitizeSurrogates(thinking.Thinking);
                                if (text.Trim().Length == 0) break;
                                // 签名仅 Claude 支持；缺失签名时回退纯文本（Bedrock 拒绝无签名重放）。
                                if (supportsSignature)
                                {
                                    if (string.IsNullOrWhiteSpace(thinking.Signature))
                                    {
                                        contentBlocks.Add(new JsonObject { ["text"] = text });
                                    }
                                    else
                                    {
                                        contentBlocks.Add(new JsonObject
                                        {
                                            ["reasoningContent"] = new JsonObject
                                            {
                                                ["reasoningText"] = new JsonObject
                                                {
                                                    ["text"] = text,
                                                    ["signature"] = thinking.Signature,
                                                },
                                            },
                                        });
                                    }
                                }
                                else
                                {
                                    contentBlocks.Add(new JsonObject
                                    {
                                        ["reasoningContent"] = new JsonObject
                                        {
                                            ["reasoningText"] = new JsonObject { ["text"] = text },
                                        },
                                    });
                                }
                                break;
                            }
                        }
                    }
                    if (contentBlocks.Count == 0) break;
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = contentBlocks });
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    // 连续 toolResult 合并为单条 user 消息（Bedrock 要求）。
                    var toolResults = new JsonArray(new JsonObject
                    {
                        ["toolResult"] = new JsonObject
                        {
                            ["toolUseId"] = toolResult.ToolCallId,
                            ["content"] = ConvertToolResultContent(toolResult.Content),
                            ["status"] = toolResult.IsError ? "error" : "success",
                        },
                    });
                    var j = i + 1;
                    while (j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage next)
                    {
                        toolResults.Add(new JsonObject
                        {
                            ["toolResult"] = new JsonObject
                            {
                                ["toolUseId"] = next.ToolCallId,
                                ["content"] = ConvertToolResultContent(next.Content),
                                ["status"] = next.IsError ? "error" : "success",
                            },
                        });
                        j++;
                    }
                    i = j - 1;
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                    break;
                }
            }
        }

        // 最后一条 user 消息附加缓存点（受支持的 Claude 模型）。
        if (cacheRetention != "none" && SupportsPromptCaching(model, env) && result.Count > 0
            && result[^1] is JsonObject lastMessage && lastMessage.Str("role") == "user"
            && lastMessage["content"] is JsonArray lastContent)
        {
            lastContent.Add(BuildCachePoint(cacheRetention));
        }

        return result;
    }

    /// <summary>停止原因映射。对应 TS <c>mapStopReason</c>。</summary>
    public static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? reason)
        => reason switch
        {
            "end_turn" or "stop_sequence" => (StopReason.Stop, null),
            "max_tokens" or "model_context_window_exceeded" => (StopReason.Length, null),
            "tool_use" => (StopReason.ToolUse, null),
            null => (StopReason.Error, null),
            _ => (StopReason.Error, $"Provider stopped with: {reason}"),
        };

    // =============================================================================
    // 端点与认证
    // =============================================================================

    /// <summary>region 解析：ARN 内嵌 &gt; 显式 option &gt; env &gt; 端点推断 &gt; us-east-1。</summary>
    public static string? GetConfiguredBedrockRegion(BedrockOptions? options)
        => options?.Region
           ?? ProviderEnvValue.Get("AWS_REGION", options?.Env)
           ?? ProviderEnvValue.Get("AWS_DEFAULT_REGION", options?.Env);

    /// <summary>标准 bedrock-runtime 端点里的 region。对应 TS <c>getStandardBedrockEndpointRegion</c>。</summary>
    public static string? GetStandardBedrockEndpointRegion(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return null;
        string host;
        try
        {
            host = new Uri(baseUrl).Host.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            host, @"^bedrock-runtime(?:-fips)?\.([a-z0-9-]+)\.amazonaws\.com(?:\.cn)?$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>是否显式端点（非标准端点恒真；标准端点仅在无 region/profile 时）。对应 TS <c>shouldUseExplicitBedrockEndpoint</c>。</summary>
    public static bool ShouldUseExplicitBedrockEndpoint(
        string baseUrl, string? configuredRegion, bool hasAmbientConfiguredProfile)
    {
        var endpointRegion = GetStandardBedrockEndpointRegion(baseUrl);
        if (endpointRegion is null) return true;
        return configuredRegion is null && !hasAmbientConfiguredProfile;
    }

    private static (string Url, IReadOnlyDictionary<string, string> AuthHeaders) BuildEndpointAndAuth(
        ModelSpec model, BedrockOptions? options)
    {
        var configuredRegion = GetConfiguredBedrockRegion(options);
        var hasAmbientConfiguredProfile = ProviderEnvValue.Get("AWS_PROFILE") is not null;
        var endpointRegion = GetStandardBedrockEndpointRegion(model.BaseUrl);
        var useExplicitEndpoint = ShouldUseExplicitBedrockEndpoint(
            model.BaseUrl, configuredRegion, hasAmbientConfiguredProfile);

        // region：ARN 内嵌 > 显式 > 端点推断 > 缺省 us-east-1（避免占用其它服务的 AWS_REGION）。
        var arnMatch = System.Text.RegularExpressions.Regex.Match(
            model.Id, @"^arn:aws(?:-[a-z0-9-]+)?:bedrock:([a-z0-9-]+):");
        var region = arnMatch.Success ? arnMatch.Groups[1].Value
            : configuredRegion
            ?? (endpointRegion is not null && useExplicitEndpoint ? endpointRegion : null)
            ?? (hasAmbientConfiguredProfile ? null : "us-east-1")
            ?? "us-east-1";

        var endpoint = useExplicitEndpoint && !string.IsNullOrEmpty(model.BaseUrl)
            ? model.BaseUrl.TrimEnd('/')
            : $"https://bedrock-runtime.{region}.amazonaws.com";
        var url = $"{endpoint}/model/{Uri.EscapeDataString(model.Id)}/converse-stream";

        // 认证：Bearer（API key）优先；skipAuth 用哑凭据；env 凭据走 SigV4。
        var skipAuth = ProviderEnvValue.Get("AWS_BEDROCK_SKIP_AUTH", options?.Env) == "1";
        var bearerToken = options?.BearerToken
            ?? options?.ApiKey
            ?? ProviderEnvValue.Get("AWS_BEARER_TOKEN_BEDROCK", options?.Env);
        if (bearerToken is not null && !skipAuth)
        {
            return (url, new Dictionary<string, string> { ["authorization"] = $"Bearer {bearerToken}" });
        }

        var accessKeyId = skipAuth ? "dummy-access-key" : ProviderEnvValue.Get("AWS_ACCESS_KEY_ID", options?.Env);
        var secretAccessKey = skipAuth ? "dummy-secret-key" : ProviderEnvValue.Get("AWS_SECRET_ACCESS_KEY", options?.Env);
        if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey))
        {
            throw new InvalidOperationException(
                "AWS Bedrock credentials are required: set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY "
                + "or AWS_BEARER_TOKEN_BEDROCK (profile/credential-file chains are SDK-runtime features).");
        }
        var sessionToken = skipAuth ? null : ProviderEnvValue.Get("AWS_SESSION_TOKEN", options?.Env);
        var headers = AwsSigV4.Sign(
            "POST", url, null, "{}", accessKeyId!, secretAccessKey!, sessionToken ?? "", region, DateTimeOffset.Now);
        // 载荷哈希随实际 body 更新：先占位，真正签名在发送前由调用方重签。
        return (url, headers);
    }

    // =============================================================================
    // 流事件处理
    // =============================================================================

    /// <summary>流式块草稿：wire index ↔ 内容块位置与 scratch 缓冲。替代 TS 在块上挂 index/partialJson。</summary>
    private sealed class BlockDrafts
    {
        private readonly OpenAiResponsesShared.MutableAssistantMessage _output;
        private readonly Dictionary<int, int> _byWireIndex = new();
        private readonly Dictionary<int, StringBuilder> _partialJson = new();
        private readonly Dictionary<int, List<byte[]>> _redactedChunks = new();
        private readonly HashSet<int> _redacted = new();

        public BlockDrafts(OpenAiResponsesShared.MutableAssistantMessage output) => _output = output;

        public int? Find(int wireIndex)
            => _byWireIndex.TryGetValue(wireIndex, out var position) ? position : null;

        public int Add(int wireIndex, ContentBlock block)
        {
            _output.Content.Add(block);
            var position = _output.Content.Count - 1;
            _byWireIndex[wireIndex] = position;
            return position;
        }

        public ContentBlock? At(int position)
            => position >= 0 && position < _output.Content.Count ? _output.Content[position] : null;

        public void Replace(int position, ContentBlock block) => _output.Content[position] = block;

        public StringBuilder PartialJson(int position)
        {
            if (!_partialJson.TryGetValue(position, out var builder))
            {
                builder = new StringBuilder();
                _partialJson[position] = builder;
            }
            return builder;
        }

        public void AddRedactedChunk(int position, string base64Chunk)
        {
            if (!_redactedChunks.TryGetValue(position, out var chunks))
            {
                chunks = [];
                _redactedChunks[position] = chunks;
            }
            chunks.Add(Convert.FromBase64String(base64Chunk));
        }

        public void MarkRedacted(int position) => _redacted.Add(position);

        public bool IsRedacted(int position) => _redacted.Contains(position);

        /// <summary>加密 reasoning 合并为 base64 签名并剥离 scratch（对齐 TS flushRedactedContent）。</summary>
        public void FinalizeAll()
        {
            foreach (var (position, chunks) in _redactedChunks)
            {
                if (_output.Content[position] is ThinkingContent thinking)
                {
                    _output.Content[position] = thinking with
                    {
                        Signature = Convert.ToBase64String(chunks.SelectMany(chunk => chunk).ToArray()),
                    };
                }
            }
            _redactedChunks.Clear();
            _partialJson.Clear();
        }
    }

    private static void HandleContentBlockStart(
        JsonObject @event, BlockDrafts drafts, OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream)
    {
        var wireIndex = (int)(@event.Num("contentBlockIndex") ?? 0);
        if (@event.Obj("start")?.Obj("toolUse") is not { } toolUse) return;
        var position = drafts.Add(wireIndex, new ToolCallContent(
            toolUse.Str("toolUseId") ?? "", toolUse.Str("name") ?? "", new JsonObject()));
        drafts.PartialJson(position);
        stream.Push(new AssistantMessageEvent.ToolCallStart(position, output.Snapshot()));
    }

    private static void HandleContentBlockDelta(
        JsonObject @event, BlockDrafts drafts, OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream)
    {
        var wireIndex = (int)(@event.Num("contentBlockIndex") ?? 0);
        var delta = @event.Obj("delta") ?? [];
        var position = drafts.Find(wireIndex);

        if (delta.Str("text") is { } textDelta)
        {
            // 文本块没有 Start 事件：缺块时补建。
            if (position is null)
            {
                position = drafts.Add(wireIndex, new TextContent(""));
                stream.Push(new AssistantMessageEvent.TextStart(position.Value, output.Snapshot()));
            }
            if (drafts.At(position.Value) is TextContent textBlock)
            {
                drafts.Replace(position.Value, textBlock with { Text = textBlock.Text + textDelta });
                stream.Push(new AssistantMessageEvent.TextDelta(position.Value, textDelta, 0, output.Snapshot()));
            }
            return;
        }
        if (delta.Obj("toolUse") is { } toolUseDelta && position is { } toolPosition
            && drafts.At(toolPosition) is ToolCallContent toolCall)
        {
            var json = drafts.PartialJson(toolPosition).Append(toolUseDelta.Str("input") ?? "").ToString();
            drafts.Replace(position.Value, toolCall with
            {
                Arguments = JsonParse.ParseStreamingJson(json) ?? new JsonObject(),
            });
            stream.Push(new AssistantMessageEvent.ToolCallDelta(
                toolPosition, toolPosition, toolUseDelta.Str("input") ?? "", output.Snapshot()));
            return;
        }
        if (delta.Obj("reasoningContent") is { } reasoning)
        {
            if (position is null)
            {
                position = drafts.Add(wireIndex, new ThinkingContent(""));
                stream.Push(new AssistantMessageEvent.ThinkingStart(position.Value, output.Snapshot()));
            }
            if (drafts.At(position.Value) is not ThinkingContent thinkingBlock) return;

            if (reasoning.Str("text") is { Length: > 0 } reasoningText)
            {
                drafts.Replace(position.Value, thinkingBlock with
                {
                    Thinking = thinkingBlock.Thinking + reasoningText,
                });
                stream.Push(new AssistantMessageEvent.ThinkingDelta(
                    position.Value, reasoningText, 0,
                    (drafts.At(position.Value) as ThinkingContent)?.Signature, output.Snapshot()));
            }
            // signature 与 redacted 载荷互斥：混用会破坏先到者。
            if (reasoning.Str("signature") is { Length: > 0 } signature && !drafts.IsRedacted(position.Value))
            {
                var current = (drafts.At(position.Value) as ThinkingContent)!;
                drafts.Replace(position.Value, current with
                {
                    Signature = (current.Signature ?? "") + signature,
                });
            }
            if (reasoning["redactedContent"] is JsonValue { } redactedValue
                && redactedValue.TryGetValue<string>(out var redactedContent)
                && redactedContent.Length > 0)
            {
                // 非 Anthropic 模型的加密 reasoning（如 GPT-5.6）：载荷不透明，
                // 原样存入签名供下轮重放（与 Anthropic redacted 一致）。
                if (!drafts.IsRedacted(position.Value))
                {
                    drafts.MarkRedacted(position.Value);
                    var current = (drafts.At(position.Value) as ThinkingContent)!;
                    drafts.Replace(position.Value, current with
                    {
                        Signature = "",
                        Thinking = current.Thinking + RedactedThinkingPlaceholder,
                    });
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                        position.Value, RedactedThinkingPlaceholder, 0, null, output.Snapshot()));
                }
                drafts.AddRedactedChunk(position.Value, redactedContent);
            }
        }
    }

    private static void HandleContentBlockStop(
        JsonObject @event, BlockDrafts drafts, OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream)
    {
        var wireIndex = (int)(@event.Num("contentBlockIndex") ?? 0);
        var position = drafts.Find(wireIndex);
        if (position is null) return;
        switch (drafts.At(position.Value))
        {
            case TextContent textBlock:
                stream.Push(new AssistantMessageEvent.TextEnd(
                    position.Value, textBlock.Text ?? "", output.Snapshot()));
                break;
            case ThinkingContent thinkingBlock:
                stream.Push(new AssistantMessageEvent.ThinkingEnd(
                    position.Value, thinkingBlock.Thinking, output.Snapshot()));
                break;
            case ToolCallContent toolCall:
            {
                var finalized = toolCall with
                {
                    Arguments = JsonParse.ParseStreamingJson(drafts.PartialJson(position.Value).ToString())
                        ?? new JsonObject(),
                };
                drafts.Replace(position.Value, finalized);
                stream.Push(new AssistantMessageEvent.ToolCallEnd(position.Value, finalized, output.Snapshot()));
                break;
            }
        }
    }

    /// <summary>usage + 成本（含 cacheDetails 1h 缓存写）。对应 TS <c>handleMetadata</c>。</summary>
    private static void HandleMetadata(
        JsonObject metadata, ModelSpec model, OpenAiResponsesShared.MutableAssistantMessage output)
    {
        if (metadata.Obj("usage") is not { } usage) return;
        var input = (long)(usage.Num("inputTokens") ?? 0);
        var outputTokens = (long)(usage.Num("outputTokens") ?? 0);
        var cacheRead = (long)(usage.Num("cacheReadInputTokens") ?? 0);
        var cacheWrite = (long)(usage.Num("cacheWriteInputTokens") ?? 0);
        // TS：cacheDetails 缺失时 cacheWrite1h 为 undefined（而非 0），calculateCost 的
        // 1h 加价按 `?? 0` 取值，故两种形态成本相同，但序列化形状不同。
        long? cacheWrite1h = null;
        if (usage["cacheDetails"] is JsonArray details)
        {
            long total = 0;
            foreach (var detail in details.OfType<JsonObject>())
            {
                if (detail.Str("ttl") == "1h")
                {
                    total += (long)(detail.Num("inputTokens") ?? 0);
                }
            }
            cacheWrite1h = total;
        }
        var stats = new Usage(input, outputTokens, cacheRead, cacheWrite)
        {
            CacheWrite1h = cacheWrite1h,
            // TS：totalTokens 优先取 wire 值，回退到 input + output（不含缓存桶）。
            TotalTokens = (long)(usage.Num("totalTokens") ?? 0) is var reported && reported != 0
                ? reported
                : input + outputTokens,
        };
        output.Usage = ModelOperations.CalculateCost(model, stats);
    }

    // =============================================================================
    // 错误格式化与诊断
    // =============================================================================

    private static readonly HashSet<string> ReservedHeaderExact = new(StringComparer.Ordinal)
    { "authorization", "host" };

    /// <summary>SigV4/认证保留头（x-amz-*、authorization、host）不可被调用头覆盖。对应 TS <c>isReservedHeader</c>。</summary>
    public static bool IsReservedHeader(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.StartsWith("x-amz-", StringComparison.Ordinal) || ReservedHeaderExact.Contains(lower);
    }

    private static readonly IReadOnlyDictionary<string, string> BedrockErrorPrefixes =
        new Dictionary<string, string>
        {
            ["InternalServerException"] = "Internal server error",
            ["ModelStreamErrorException"] = "Model stream error",
            ["ValidationException"] = "Validation error",
            ["ThrottlingException"] = "Throttling error",
            ["ServiceUnavailableException"] = "Service unavailable",
        };

    /// <summary>错误格式化（数据保留模式提示 + 可读前缀）。对应 TS <c>formatBedrockError</c>。</summary>
    public static string FormatBedrockError(Exception error)
    {
        var normalized = ProviderError.Normalize(error);
        // SDK 未折叠 HTTP body 时直接呈现「状态码: body」——避免网关 403 塌缩成 Unknown。
        var core = !normalized.MessageCarriesBody && normalized.Status is { } status && normalized.Body is { } body
            ? $"{status}: {body}"
            : normalized.Message;
        var dataRetentionHint = System.Text.RegularExpressions.Regex.IsMatch(core, "data retention mode",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? $" See {BedrockDataRetentionDocsUrl} for supported data retention modes."
            : "";
        return $"{core}{dataRetentionHint}";
    }

    /// <summary>超过上限的头值丢弃而非截断：截断的 requestId 不再是 requestId。</summary>
    private const int MaxBedrockDiagnosticValueChars = 200;

    private static string? NormalizeDiagnosticValue(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxBedrockDiagnosticValueChars) return null;
        return trimmed;
    }

    private static string? ExtractBedrockErrorCode(Exception error)
    {
        var name = error.GetType().Name;
        return name.EndsWith("Exception", StringComparison.Ordinal) ? NormalizeDiagnosticValue(name) : null;
    }

    /// <summary>结构化失败诊断（status/errorCode/requestId）。对应 TS <c>appendBedrockFailureDiagnostic</c>。</summary>
    private static void AppendBedrockFailureDiagnostic(
        OpenAiResponsesShared.MutableAssistantMessage output, Exception error, string? fallbackRequestId)
    {
        var details = new JsonObject();
        if (error is ProviderHttpException httpError)
        {
            if (httpError.Status is { } status) details["status"] = status;
            var errorCode = ExtractBedrockErrorCode(error);
            if (errorCode is not null) details["errorCode"] = errorCode;
            var requestId = NormalizeDiagnosticValue(
                httpError.Headers?.GetValueOrDefault("x-amzn-requestid")) ?? fallbackRequestId;
            if (requestId is not null) details["requestId"] = requestId;
        }
        else
        {
            var errorCode = ExtractBedrockErrorCode(error);
            if (errorCode is not null) details["errorCode"] = errorCode;
        }
        if (details.Count == 0) return;
        output.Diagnostics.Add(Diagnostics.CreateAssistantMessageDiagnostic(
            "bedrock_response_failure", error, details));
    }
}
