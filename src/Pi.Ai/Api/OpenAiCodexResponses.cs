using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>OpenAI Codex Responses 专属流选项。对应 TS <c>OpenAICodexResponsesOptions</c>。</summary>
public record OpenAiCodexResponsesOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public int? WebsocketConnectTimeoutMs { get; init; }

    /// <summary>首选传输（auto/sse/websocket/websocket-cached）。对应 TS <c>transport</c>。</summary>
    public string? Transport { get; init; }

    public string? SessionId { get; init; }

    public string? CacheRetention { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    /// <summary>推理档位（none/minimal/low/medium/high/xhigh/max）。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>推理摘要级别（auto/concise/detailed/off/on）。</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>service tier（flex/priority/default/auto）。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>文本详细度（low/medium/high；缺省 low）。对应 TS <c>textVerbosity</c>。</summary>
    public string? TextVerbosity { get; init; }

    /// <summary>工具选择（auto/none/required）。对应 TS <c>toolChoice</c>。</summary>
    public string? ToolChoice { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>Codex API 错误（带 wire code）。对应 TS <c>CodexApiError</c>。</summary>
public sealed class CodexApiError : Exception
{
    public string? Code { get; }
    public JsonObject? Payload { get; }

    public CodexApiError(string message, string? code = null, JsonObject? payload = null)
        : base(message)
    {
        Code = code;
        Payload = payload;
    }
}

/// <summary>Codex 协议错误（SSE/WS JSON 解析失败等）。对应 TS <c>CodexProtocolError</c>。</summary>
public sealed class CodexProtocolError : Exception
{
    public object? Payload { get; }

    public CodexProtocolError(string message, object? payload = null) : base(message) => Payload = payload;
}

/// <summary>onProviderStreamEvent 回调异常（不参与 WS 重试/SSE 回退）。对应 TS <c>ProviderStreamEventCallbackError</c>。</summary>
public sealed class ProviderStreamEventCallbackError : Exception
{
    public ProviderStreamEventCallbackError(object? cause)
        : base(Diagnostics.FormatThrownValue(cause)) => InnerCause = cause;

    public object? InnerCause { get; }
}

/// <summary>服务端要求的重试延迟超出上限。对应 TS <c>RetryDelayExceededError</c>。</summary>
public sealed class RetryDelayExceededError : Exception
{
    public RetryDelayExceededError(string message) : base(message) { }
}

/// <summary>
/// OpenAI Codex Responses API（chatgpt.com/backend-api/codex/responses）。
/// 对应 TS <c>api/openai-codex-responses.ts</c>：SSE 与 WebSocket 双传输、
/// WS 会话缓存与 continuation 增量请求、SSE 回退与重试。
/// </summary>
public static partial class OpenAiCodexResponses
{
    private const string DefaultCodexBaseUrl = "https://chatgpt.com/backend-api";
    private const string JwtClaimPath = "https://api.openai.com/auth";
    private const int DefaultMaxRetries = 0;
    private const int BaseDelayMs = 1000;
    private const int DefaultMaxRetryDelayMs = 60_000;
    private const int DefaultWebsocketConnectTimeoutMs = 15_000;
    private const string OpenAiBetaResponsesWebsockets = "responses_websockets=2026-02-06";
    private const string WebsocketConnectionLimitReachedCode = "websocket_connection_limit_reached";
    private const string PreviousResponseNotFoundCode = "previous_response_not_found";

    private static readonly HashSet<string> CodexToolCallProviders = ["openai", "openai-codex", "opencode"];

    private static readonly HashSet<string> CodexResponseStatuses =
    ["completed", "incomplete", "failed", "cancelled", "queued", "in_progress"];

    // ============================================================================
    // Retry Helpers
    // ============================================================================

    /// <summary>终态限额错误（429 但重试无意义）。对应 TS <c>isTerminalRateLimitError</c>。</summary>
    public static bool IsTerminalRateLimitError(string errorText)
        => System.Text.RegularExpressions.Regex.IsMatch(errorText,
            "GoUsageLimitError|FreeUsageLimitError|Monthly usage limit reached|available balance|insufficient_quota|out of budget|quota exceeded|billing",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>按状态与错误文本判断可重试。对应 TS <c>isRetryableError</c>。</summary>
    public static bool IsRetryableError(int status, string errorText)
    {
        if (status == 429 && IsTerminalRateLimitError(errorText)) return false;
        if (status is 429 or 500 or 502 or 503 or 504) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(errorText,
            "rate.?limit|overloaded|service.?unavailable|upstream.?connect|connection.?refused",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>retry-after-ms / retry-after（秒或 HTTP 日期）→ 延迟毫秒。对应 TS <c>getRetryAfterDelayMs</c>。</summary>
    public static long? GetRetryAfterDelayMs(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("retry-after-ms", out var msValues)
            && long.TryParse(msValues.FirstOrDefault(), out var millis))
        {
            return Math.Max(0, millis);
        }
        if (!headers.TryGetValues("retry-after", out var values)) return null;
        var retryAfter = values.FirstOrDefault();
        if (retryAfter is null) return null;

        if (double.TryParse(retryAfter, out var seconds))
        {
            return Math.Max(0, (long)(seconds * 1000));
        }
        if (DateTimeOffset.TryParse(retryAfter, out var date))
        {
            return Math.Max(0, (long)(date - DateTimeOffset.Now).TotalMilliseconds);
        }
        return null;
    }

    private static long ValidateRetryDelayMs(long delayMs, OpenAiCodexResponsesOptions? options)
    {
        var maxRetryDelayMs = options?.MaxRetryDelayMs ?? DefaultMaxRetryDelayMs;
        if (maxRetryDelayMs > 0 && delayMs > maxRetryDelayMs)
        {
            throw new RetryDelayExceededError(
                $"Server requested {Math.Ceiling(delayMs / 1000.0)}s retry delay (max: {Math.Ceiling(maxRetryDelayMs / 1000.0)}s)");
        }
        return delayMs;
    }

    private static async Task AbortableDelayAsync(int ms, CancellationToken signal)
    {
        try
        {
            await Task.Delay(ms, signal).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Request was aborted");
        }
    }

    private static int? NormalizeTimeoutMs(int? value)
    {
        if (value is null) return null;
        if (value < 0) throw new InvalidOperationException($"Invalid timeoutMs: {value}");
        return value;
    }

    // ============================================================================
    // Request Building
    // ============================================================================

    /// <summary>构建 /codex/responses 请求体。对应 TS <c>buildRequestBody</c>。</summary>
    public static JsonObject BuildRequestBody(
        ModelSpec model,
        TranscriptContext context,
        OpenAiCodexResponsesOptions? options,
        string? cacheSessionId,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        bool CompatBool(string key, bool fallback)
            => model.Compat is not null && model.Compat[key] is JsonValue { } value
                && value.TryGetValue<bool>(out var flag) ? flag : fallback;

        var supportsStrictMode = CompatBool("supportsStrictMode", true);
        var supportsOpenAiGrammarTools = CompatBool("supportsOpenAIGrammarTools", false);
        var supportsAdditionalTools = CompatBool("supportsAdditionalTools", false);
        var supportsToolSearch = CompatBool("supportsToolSearch", false);
        var supportsMidConvo = CompatBool("supportsMidConvoSystemMessages", false);
        var normalizedContext = Transcript.ResolveTranscript(context, supportsMidConvo);
        var transcriptTools = Transcript.ResolveTranscriptTools(
            normalizedContext.Messages, supportsAdditionalTools || supportsToolSearch);
        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model, normalizedContext, CodexToolCallProviders, new ConvertResponsesMessagesOptions
            {
                IncludeSystemPrompt = false,
                GrammarToolInputProperties = grammarToolInputProperties,
                SupportsMidConvoSystemMessages = supportsMidConvo,
                SupportsAdditionalTools = supportsAdditionalTools,
                SupportsToolSearch = supportsToolSearch,
                ToolOptions = new ConvertResponsesToolsOptions
                {
                    Strict = null,
                    SupportsStrictMode = supportsStrictMode,
                    SupportsOpenAiGrammarTools = supportsOpenAiGrammarTools,
                },
            });

        var initialSystemMessage = Transcript.GetInitialSystemMessage(context.Messages);
        var instructions = initialSystemMessage is not null
            ? Text.GetSystemMessageText(initialSystemMessage)
            : "";
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["store"] = false,
            ["stream"] = true,
            ["instructions"] = instructions.Length > 0 ? instructions : "You are a helpful assistant.",
            ["input"] = messages,
            ["text"] = new JsonObject { ["verbosity"] = options?.TextVerbosity ?? "low" },
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["prompt_cache_key"] = cacheSessionId,
            ["tool_choice"] = options?.ToolChoice ?? "auto",
            ["parallel_tool_calls"] = true,
        };

        if (options?.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }
        if (options?.ServiceTier is { } serviceTier)
        {
            body["service_tier"] = serviceTier;
        }

        if (transcriptTools.RequestTools.Count > 0)
        {
            body["tools"] = OpenAiResponsesShared.ConvertResponsesTools(
                transcriptTools.RequestTools,
                new ConvertResponsesToolsOptions
                {
                    Strict = null,
                    SupportsStrictMode = supportsStrictMode,
                    SupportsOpenAiGrammarTools = supportsOpenAiGrammarTools,
                });
        }

        if (options?.ReasoningEffort is { } requestedEffort)
        {
            var effort = requestedEffort == "none"
                ? (model.ThinkingLevelMap is { } map && map.Has("off") ? map["off"] : "none")
                : model.ThinkingLevelMap?[requestedEffort] ?? requestedEffort;
            if (effort is not null)
            {
                body["reasoning"] = new JsonObject
                {
                    ["effort"] = effort,
                    ["summary"] = options.ReasoningSummary ?? "auto",
                };
            }
        }
        else if (model.Reasoning
            && !(model.ThinkingLevelMap is { } codexLevelMap && codexLevelMap.Has("off") && codexLevelMap["off"] is null))
        {
            // TS：thinkingLevelMap?.off !== null → 设置 effort（map 缺失 / off 缺省 / off 非 null 均成立）。
            body["reasoning"] = new JsonObject
            {
                ["effort"] = model.ThinkingLevelMap?["off"] ?? "none",
            };
        }

        if (SimpleOptions.ResolveSamplingParams(model, options?.ReasoningEffort ?? "off", options?.SamplingParams) is { } sampling)
        {
            foreach (var (key, value) in sampling) body[key] = value?.DeepClone();
        }

        return body;
    }

    /// <summary>service tier 成本乘数（codex 无 fast）。对应 TS <c>getServiceTierCostMultiplier</c>。</summary>
    public static double GetServiceTierCostMultiplier(ModelSpec model, string? serviceTier)
        => serviceTier switch
        {
            "flex" => 0.5,
            "priority" => model.Id == "gpt-5.5" ? 2.5 : 2,
            _ => 1,
        };

    /// <summary>按 service tier 折算用量成本。对应 TS <c>applyServiceTierPricing</c>。</summary>
    public static Usage ApplyServiceTierPricing(Usage usage, string? serviceTier, ModelSpec model)
    {
        var multiplier = GetServiceTierCostMultiplier(model, serviceTier);
        if (multiplier == 1) return usage;
        return usage with { Cost = (usage.Cost ?? 0) * multiplier };
    }

    /// <summary>响应 tier "default" 回退到请求 tier（flex/priority）。对应 TS <c>resolveCodexServiceTier</c>。</summary>
    public static string? ResolveCodexServiceTier(string? responseServiceTier, string? requestServiceTier)
        => responseServiceTier == "default" && requestServiceTier is "flex" or "priority"
            ? requestServiceTier
            : responseServiceTier ?? requestServiceTier;

    /// <summary>codex responses URL 解析（自动补 /codex/responses）。对应 TS <c>resolveCodexUrl</c>。</summary>
    public static string ResolveCodexUrl(string? baseUrl)
    {
        var raw = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.Trim() : DefaultCodexBaseUrl;
        var normalized = raw.TrimEnd('/');
        if (normalized.EndsWith("/codex/responses", StringComparison.Ordinal)) return normalized;
        if (normalized.EndsWith("/codex", StringComparison.Ordinal)) return $"{normalized}/responses";
        return $"{normalized}/codex/responses";
    }

    /// <summary>codex WebSocket URL（https→wss / http→ws）。对应 TS <c>resolveCodexWebSocketUrl</c>。</summary>
    public static string ResolveCodexWebSocketUrl(string? baseUrl)
    {
        var url = new Uri(ResolveCodexUrl(baseUrl));
        var scheme = url.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => url.Scheme,
        };
        return $"{scheme}://{url.Authority}{url.PathAndQuery}";
    }

    // ============================================================================
    // Response Processing（事件映射与错误分类）
    // ============================================================================

    private static bool IsCodexNonTransportError(Exception error)
        => error is CodexApiError or CodexProtocolError or ProviderStreamEventCallbackError;

    private static bool IsWebSocketConnectionLimitReachedError(Exception error)
        => error is CodexApiError { Code: WebsocketConnectionLimitReachedCode };

    private static bool IsPreviousResponseNotFoundError(Exception error)
        => error is CodexApiError { Code: PreviousResponseNotFoundCode };

    private static (string? Code, string? Message) ExtractCodexEventError(JsonObject @event)
    {
        var nested = @event.Obj("error");
        return (
            Code: @event.Str("code") ?? nested?.Str("code"),
            Message: @event.Str("message") ?? nested?.Str("message"));
    }

    /// <summary>
    /// Codex 事件 → 标准 Responses 事件：error/response.failed 抛 <see cref="CodexApiError"/>；
    /// response.done/completed/incomplete 归一化 status 后统一重发为 response.completed。
    /// 对应 TS <c>mapCodexEvents</c>。
    /// </summary>
    private static async IAsyncEnumerable<JsonObject> MapCodexEvents(
        IAsyncEnumerable<JsonObject> events,
        OpenAiResponsesShared.MutableAssistantMessage output,
        ModelSpec model,
        Func<JsonObject, ModelSpec, Task>? onProviderStreamEvent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var @event in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (onProviderStreamEvent is not null)
            {
                try
                {
                    await onProviderStreamEvent(@event, model).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    // 回调失败不进入 WS 重试与 SSE 回退路径。
                    throw new ProviderStreamEventCallbackError(error);
                }
            }
            var type = @event.Str("type");
            if (type is null) continue;

            if (type == "error")
            {
                var (code, message) = ExtractCodexEventError(@event);
                throw new CodexApiError(
                    $"Codex error: {message ?? code ?? @event.ToJsonString()}", code, @event);
            }

            if (type == "response.failed")
            {
                var response = @event.Obj("response");
                var code = response?.Obj("error")?.Str("code");
                var message = response?.Obj("error")?.Str("message");
                throw new CodexApiError(message ?? "Codex response failed", code, @event);
            }

            if (type is "response.done" or "response.completed" or "response.incomplete")
            {
                var response = @event.Obj("response");
                if (response?["end_turn"] is JsonValue { } endTurnValue
                    && endTurnValue.TryGetValue<bool>(out var endTurn))
                {
                    output.EndTurn = endTurn;
                }
                var normalizedResponse = response is null
                    ? null
                    : NormalizeCodexStatus(response) ?? response.DeepClone();
                yield return new JsonObject
                {
                    ["type"] = "response.completed",
                    ["response"] = normalizedResponse,
                };
                yield break;
            }

            yield return @event;
        }
    }

    private static JsonObject NormalizeCodexStatus(JsonObject response)
    {
        var status = response.Str("status");
        var normalized = status is not null && CodexResponseStatuses.Contains(status) ? status : null;
        var clone = (JsonObject)response.DeepClone();
        clone["status"] = normalized;
        return clone;
    }

    // ============================================================================
    // SSE Parsing
    // ============================================================================

    /// <summary>解析 codex SSE 帧（data: 行合并、[DONE] 跳过、坏 JSON → 协议错误）。对应 TS <c>parseSSE</c>。</summary>
    private static async IAsyncEnumerable<JsonObject> ParseCodexSse(
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
                // EOF 视为终止残余帧。
                if (buffer.Length > 0 && buffer.ToString().Trim().Length > 0) buffer.Append("\n\n");
                line = null;
            }
            if (line is not null)
            {
                buffer.Append(line).Append('\n');
                if (buffer.ToString().Contains("\n\n"))
                {
                    // 逐帧处理
                    var text = buffer.ToString();
                    int index;
                    while ((index = text.IndexOf("\n\n", StringComparison.Ordinal)) != -1)
                    {
                        var chunk = text[..index];
                        text = text[(index + 2)..];
                        var parsed = TryParseSseChunk(chunk);
                        if (parsed is not null) yield return parsed;
                    }
                    buffer.Clear();
                    buffer.Append(text);
                }
                continue;
            }
            break;
        }
        // 收尾残余帧
        var tail = buffer.ToString();
        var tailParsed = TryParseSseChunk(tail);
        if (tailParsed is not null) yield return tailParsed;
    }

    private static JsonObject? TryParseSseChunk(string chunk)
    {
        var dataLines = chunk
            .Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].Trim())
            .ToList();
        if (dataLines.Count == 0) return null;
        var data = string.Join("\n", dataLines).Trim();
        if (data.Length == 0 || data == "[DONE]") return null;
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException cause)
        {
            throw new CodexProtocolError($"Invalid Codex SSE JSON: {Diagnostics.FormatThrownValue(cause)}", data);
        }
    }

    // ============================================================================
    // Auth & Headers
    // ============================================================================

    /// <summary>从 JWT 提取 chatgpt_account_id。对应 TS <c>extractAccountId</c>。</summary>
    public static string ExtractAccountId(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new InvalidOperationException("Invalid token");
            var payloadJson = Base64UrlDecode(parts[1]);
            var payload = JsonNode.Parse(payloadJson) as JsonObject;
            var accountId = payload?.Obj(JwtClaimPath)?.Str("chatgpt_account_id");
            if (string.IsNullOrEmpty(accountId)) throw new InvalidOperationException("No account ID in token");
            return accountId!;
        }
        catch
        {
            throw new InvalidOperationException("Failed to extract accountId from token");
        }
    }

    private static string Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static Dictionary<string, string?> BuildBaseCodexHeaders(
        IReadOnlyDictionary<string, string?>? initHeaders,
        IReadOnlyDictionary<string, string?>? additionalHeaders,
        string accountId,
        string token)
    {
        var headers = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (initHeaders is not null)
        {
            foreach (var (key, value) in initHeaders) headers[key] = value;
        }
        if (additionalHeaders is not null)
        {
            foreach (var (key, value) in additionalHeaders) headers[key] = value;
        }
        headers["Authorization"] = $"Bearer {token}";
        headers["chatgpt-account-id"] = accountId;
        headers["originator"] = "pi";
        headers["User-Agent"] = PiUserAgent.Get();
        return headers;
    }

    private static Dictionary<string, string?> BuildSseHeaders(
        IReadOnlyDictionary<string, string?>? initHeaders,
        IReadOnlyDictionary<string, string?>? additionalHeaders,
        string accountId,
        string token,
        string? sessionId)
    {
        var headers = BuildBaseCodexHeaders(initHeaders, additionalHeaders, accountId, token);
        headers["OpenAI-Beta"] = "responses=experimental";
        headers["accept"] = "text/event-stream";
        headers["content-type"] = "application/json";
        if (sessionId is not null)
        {
            headers["session-id"] = sessionId;
            headers["x-client-request-id"] = sessionId;
        }
        return headers;
    }

    // ============================================================================
    // Main Stream Function
    // ============================================================================

    /// <summary>终态断言：pending/error/aborted 之外才算成功。对应 TS <c>assertSuccessfulOutput</c>。</summary>
    private static void AssertSuccessfulOutput(OpenAiResponsesShared.MutableAssistantMessage output)
    {
        if (output.StopReason == StopReason.Pending)
        {
            throw new InvalidOperationException("Codex stream ended without a stop reason");
        }
        if (output.StopReason is StopReason.Error or StopReason.Aborted)
        {
            throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
        }
    }

    /// <summary>错误响应解析（usage limit 友好提示）。对应 TS <c>parseErrorResponse</c>。</summary>
    public static (string Message, string? FriendlyMessage) ParseErrorResponse(int status, string raw)
    {
        var message = raw.Length > 0 ? raw : "Request failed";
        string? friendlyMessage = null;
        try
        {
            var parsed = JsonNode.Parse(raw) as JsonObject;
            if (parsed?.Obj("error") is { } err)
            {
                var code = err.Str("code") ?? err.Str("type") ?? "";
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        code, "usage_limit_reached|usage_not_included|rate_limit_exceeded",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase) || status == 429)
                {
                    var plan = err.Str("plan_type") is { Length: > 0 } planType
                        ? $" ({planType.ToLowerInvariant()} plan)"
                        : "";
                    string? when = null;
                    if (err.Num("resets_at") is { } resetsAt)
                    {
                        var remainingMs = resetsAt * 1000 - DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        var mins = Math.Max(0, (long)Math.Round(remainingMs / 60000.0, MidpointRounding.AwayFromZero));
                        when = $" Try again in ~{mins} min.";
                    }
                    friendlyMessage = $"You have hit your ChatGPT usage limit{plan}.{when}".Trim();
                }
                message = err.Str("message") ?? friendlyMessage ?? message;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体：保留原文。
        }
        return (message, friendlyMessage);
    }

    /// <summary>由 <see cref="SimpleStreamOptions"/> 装配公共字段（对应 TS <c>buildBaseOptions</c>）。</summary>
    public static OpenAiCodexResponsesOptions FromSimple(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options)
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
        // codex 的 reasoningEffort 含 "none"（显式关闭）；simple reasoning 的 off 直接不设。
        var clampedReasoning = options?.Reasoning is not null
            ? ThinkingLevels.Clamp(model, options.Reasoning)
            : null;
        return new OpenAiCodexResponsesOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            WebsocketConnectTimeoutMs = options?.WebsocketConnectTimeoutMs,
            Transport = options?.Transport,
            SessionId = options?.SessionId,
            CacheRetention = options?.CacheRetention,
            Env = options?.Env,
            Metadata = options?.Metadata,
            SamplingParams = samplingParams,
            MaxTokens = maxTokens,
            Temperature = options?.Temperature,
            ToolChoice = clampedReasoning == "off"
                ? null
                : options?.ToolChoice is System.Text.Json.Nodes.JsonValue { } toolChoiceValue
                    && toolChoiceValue.TryGetValue<string>(out var toolChoiceText)
                    ? toolChoiceText
                    : null,
        };
    }

    /// <summary>简单入口流式生成。对应 TS <c>streamSimple</c>。</summary>
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

        var clampedReasoning = options?.Reasoning is not null
            ? ThinkingLevels.Clamp(model, options.Reasoning)
            : null;
        var reasoningEffort = clampedReasoning == "off" ? "none" : clampedReasoning;

        var baseOptions = FromSimple(model, context, options);
        return Stream(model, context, baseOptions with
        {
            ReasoningEffort = reasoningEffort,
        }, httpClient, cancellationToken);
    }

    /// <summary>
    /// 流式生成：WebSocket（可缓存会话）优先、SSE 回退（重试 429/5xx）。对应 TS <c>stream</c>。
    /// </summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        OpenAiCodexResponsesOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var supportsMidConvo = model.Compat?["supportsMidConvoSystemMessages"] is JsonValue { } mid
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
                var accountId = ExtractAccountId(apiKey);
                var grammarToolInputProperties = ConstrainedSampling.CreateGrammarToolInputProperties(
                    Transcript.GetDeclaredTools(normalizedContext.Messages),
                    model.Compat?["supportsOpenAIGrammarTools"] is JsonValue { } grammar
                        && grammar.TryGetValue<bool>(out var grammarFlag) && grammarFlag);
                var cacheSessionId = options?.CacheRetention == "none" ? null : options?.SessionId;
                var codexSessionId = cacheSessionId is null
                    ? null
                    : OpenAiPromptCache.ClampPromptCacheKey(cacheSessionId);
                var body = BuildRequestBody(
                    model, normalizedContext, options, codexSessionId, grammarToolInputProperties);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(body, model).ConfigureAwait(false);
                    if (transformed is not null) body = (JsonObject)transformed;
                }
                var websocketRequestId = codexSessionId ?? Uuid.Uuidv7();
                Dictionary<string, string?>? modelHeaders = null;
                if (model.Headers is not null)
                {
                    modelHeaders = new Dictionary<string, string?>(model.Headers.Count, StringComparer.Ordinal);
                    foreach (var (key, value) in model.Headers) modelHeaders[key] = value;
                }
                var sseHeaders = BuildSseHeaders(
                    modelHeaders, options?.Headers, accountId, apiKey!, codexSessionId);
                var websocketHeaders = BuildWebSocketHeaders(
                    modelHeaders, options?.Headers, accountId, apiKey!, websocketRequestId);
                var httpTimeoutMs = NormalizeTimeoutMs(options?.TimeoutMs);
                var websocketConnectTimeoutMs = NormalizeTimeoutMs(options?.WebsocketConnectTimeoutMs)
                    ?? DefaultWebsocketConnectTimeoutMs;
                var transport = options?.Transport ?? "auto";
                var startEmitted = false;
                var websocketDisabledForSession = transport != "sse" && IsSseFallbackActive(cacheSessionId);
                if (websocketDisabledForSession)
                {
                    RecordSseFallback(cacheSessionId);
                }

                void EmitStart()
                {
                    if (startEmitted) return;
                    startEmitted = true;
                    stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                }

                if (transport != "sse" && !websocketDisabledForSession)
                {
                    var retriedWebSocketConnectionLimit = false;
                    var retriedMissingWebSocketContinuation = false;
                    while (true)
                    {
                        var websocketStarted = false;
                        try
                        {
                            await ProcessWebSocketStreamAsync(
                                ResolveCodexWebSocketUrl(model.BaseUrl),
                                body,
                                websocketHeaders,
                                output,
                                stream,
                                model,
                                () =>
                                {
                                    websocketStarted = true;
                                    EmitStart();
                                },
                                httpTimeoutMs,
                                websocketConnectTimeoutMs,
                                cacheSessionId,
                                accountId,
                                grammarToolInputProperties,
                                options,
                                cancellationToken).ConfigureAwait(false);

                            if (signal.IsCancellationRequested)
                            {
                                throw new InvalidOperationException("Request was aborted");
                            }
                            AssertSuccessfulOutput(output);
                            stream.Push(new AssistantMessageEvent.Done(output.StopReason, output.Snapshot()));
                            stream.End(output.Snapshot());
                            return;
                        }
                        catch (Exception error) when (error is not OperationCanceledException
                            || !signal.IsCancellationRequested)
                        {
                            var connectionLimitBeforeStart = !websocketStarted && IsWebSocketConnectionLimitReachedError(error);
                            var previousResponseNotFound = IsPreviousResponseNotFoundError(error);
                            if (!signal.IsCancellationRequested && previousResponseNotFound && !retriedMissingWebSocketContinuation)
                            {
                                retriedMissingWebSocketContinuation = true;
                                continue;
                            }
                            if (!signal.IsCancellationRequested && connectionLimitBeforeStart && !retriedWebSocketConnectionLimit)
                            {
                                retriedWebSocketConnectionLimit = true;
                                continue;
                            }
                            if (signal.IsCancellationRequested || (IsCodexNonTransportError(error) && !connectionLimitBeforeStart))
                            {
                                throw;
                            }
                            output.Diagnostics.Add(
                                Diagnostics.CreateAssistantMessageDiagnostic("provider_transport_failure", error,
                                    new JsonObject
                                    {
                                        ["configuredTransport"] = transport,
                                        ["eventsEmitted"] = websocketStarted,
                                        ["phase"] = websocketStarted
                                            ? "after_message_stream_start"
                                            : "before_message_stream_start",
                                        ["requestBytes"] = Encoding.UTF8.GetByteCount(body.ToJsonString()),
                                    }));
                            RecordWebSocketFailure(cacheSessionId, error);
                            if (websocketStarted)
                            {
                                throw;
                            }
                            RecordSseFallback(cacheSessionId);
                            break;
                        }
                    }
                }

                // SSE 路径：请求体不压缩（C# 无 zstd 内建；等价 TS 浏览器构建的未压缩回退）。
                var maxRetries = options?.MaxRetries ?? DefaultMaxRetries;
                HttpResponseMessage? response = null;
                Exception? lastError = null;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    signal.ThrowIfCancellationRequested();
                    try
                    {
                        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                        if (httpTimeoutMs is { } headerTimeout && headerTimeout > 0)
                        {
                            perCallCts.CancelAfter(headerTimeout);
                        }
                        var request = new HttpRequestMessage(HttpMethod.Post, ResolveCodexUrl(model.BaseUrl))
                        {
                            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                        };
                        foreach (var (key, value) in sseHeaders)
                        {
                            if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                        }
                        HttpResponseMessage httpResponse;
                        try
                        {
                            httpResponse = await client.SendAsync(
                                request, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            !signal.IsCancellationRequested && httpTimeoutMs is not null)
                        {
                            throw new InvalidOperationException(
                                $"Codex SSE response headers timed out after {httpTimeoutMs}ms");
                        }
                        response = httpResponse;
                        if (options?.OnResponse is { } onResponse)
                        {
                            await onResponse(new ProviderResponse
                            {
                                Status = (int)httpResponse.StatusCode,
                                Headers = httpResponse.Headers.ToDictionary(
                                    kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
                            }, model).ConfigureAwait(false);
                        }
                        if (httpResponse.IsSuccessStatusCode) break;

                        var errorText = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                        if (attempt < maxRetries && IsRetryableError((int)httpResponse.StatusCode, errorText))
                        {
                            var retryAfterDelayMs = GetRetryAfterDelayMs(httpResponse.Headers);
                            var delayMs = retryAfterDelayMs is { } serverDelay
                                ? ValidateRetryDelayMs(serverDelay, options)
                                : (long)BaseDelayMs * (long)Math.Pow(2, attempt);
                            await AbortableDelayAsync((int)delayMs, signal).ConfigureAwait(false);
                            continue;
                        }
                        var (message, friendly) = ParseErrorResponse((int)httpResponse.StatusCode, errorText);
                        throw new InvalidOperationException(friendly ?? message);
                    }
                    catch (Exception error) when (error is not RetryDelayExceededError)
                    {
                        if (error is InvalidOperationException { Message: "Request was aborted" })
                        {
                            throw new InvalidOperationException("Request was aborted");
                        }
                        lastError = error;
                        // 网络错误可重试。
                        if (attempt < maxRetries
                            && error.Message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) == false)
                        {
                            var delayMs = (long)BaseDelayMs * (long)Math.Pow(2, attempt);
                            await AbortableDelayAsync((int)delayMs, signal).ConfigureAwait(false);
                            continue;
                        }
                        throw;
                    }
                }

                if (response is not { IsSuccessStatusCode: true })
                {
                    throw lastError ?? new InvalidOperationException("Failed after retries");
                }
                EmitStart();
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var sseEvents = ParseCodexSse(content, signal, cancellationToken);
                var mapped = MapCodexEvents(sseEvents, output, model, options?.OnProviderStreamEvent, cancellationToken);
                await OpenAiResponsesShared.ProcessResponsesStream(
                    AsSseEvents(mapped), output, stream, model,
                    new ResponsesStreamOptions
                    {
                        ServiceTier = options?.ServiceTier,
                        GrammarToolInputProperties = grammarToolInputProperties,
                        ResolveServiceTier = ResolveCodexServiceTier,
                        ApplyServiceTierPricing = (usage, serviceTier)
                            => ApplyServiceTierPricing(usage, serviceTier, model),
                    }, cancellationToken).ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Request was aborted");
                }
                AssertSuccessfulOutput(output);
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

    private static Dictionary<string, string?> BuildWebSocketHeaders(
        IReadOnlyDictionary<string, string?>? initHeaders,
        IReadOnlyDictionary<string, string?>? additionalHeaders,
        string accountId,
        string token,
        string requestId)
    {
        var headers = BuildBaseCodexHeaders(initHeaders, additionalHeaders, accountId, token);
        headers.Remove("accept");
        headers.Remove("content-type");
        headers.Remove("OpenAI-Beta");
        headers.Remove("openai-beta");
        headers["OpenAI-Beta"] = OpenAiBetaResponsesWebsockets;
        headers["x-client-request-id"] = requestId;
        headers["session-id"] = requestId;
        return headers;
    }
}
