using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>OpenAI Responses 专属流选项。对应 TS <c>OpenAIResponsesOptions</c>。</summary>
public record OpenAiResponsesOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    /// <summary>会话亲和 id（prompt_cache_key / session 头）。</summary>
    public string? SessionId { get; init; }

    /// <summary>缓存保留偏好（short/long/none；缺省 short，PI_CACHE_RETENTION 兼容）。</summary>
    public string? CacheRetention { get; init; }

    /// <summary>推理档位（minimal/low/medium/high/xhigh/max）。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>推理摘要级别（auto/detailed/concise）。</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>service tier（flex/priority/fast/default/auto）。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>工具选择（透传 provider 原生形状）。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>请求元数据（保留扩展位）。</summary>
    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }

    /// <summary>任意采样参数（最后合并进请求体）。对应 TS <c>samplingParams</c>。</summary>
    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    /// <summary>作用域环境变量覆盖。对应 TS <c>env</c>。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>
/// OpenAI Responses API 流式生成。对应 TS <c>api/openai-responses.ts</c> 的
/// <c>stream()</c>——TS 用 openai npm client；C# 直接 REST + SSE（wire 等价）。
/// </summary>
public static class OpenAiResponses
{
    private static readonly HashSet<string> OpenAiToolCallProviders = ["openai", "openai-codex", "opencode"];

    /// <summary>工具调用 ID 归一化路径适用的 provider 集（测试可见）。对应 TS <c>OPENAI_TOOL_CALL_PROVIDERS</c>。</summary>
    public static IReadOnlySet<string> ProviderIdSet => OpenAiToolCallProviders;

    // OpenAI Responses 拒绝低于 16 的 max_output_tokens。（pi#6265）
    private const int MinOutputTokens = 16;
    private const string ChatGptUsageUrl = "https://chatgpt.com/settings/usage";

    /// <summary>OpenAI API key 以 sk- 开头；其它凭据直连 OpenAI 是 ChatGPT 登录令牌。</summary>
    private static bool IsChatGptSignIn(ModelSpec model, string? apiKey)
        => model.Provider == "openai"
           && model.BaseUrl == "https://api.openai.com/v1"
           && apiKey is not null
           && !apiKey.StartsWith("sk-", StringComparison.Ordinal);

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name)
    {
        if (headers is null) return false;
        foreach (var (key, value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && value is not null && value.Trim().Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string GetClientApiKey(string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return apiKey;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "cf-aig-authorization")) return "unused";
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    private static string DetectSessionAffinityFormat(ModelSpec model)
        => model.Provider == "openrouter" || model.BaseUrl.Contains("openrouter.ai") ? "openrouter" : "openai";

    /// <summary>缓存保留偏好解析。对应 TS <c>resolveCacheRetention</c>。</summary>
    private static string ResolveCacheRetention(string? cacheRetention, IReadOnlyDictionary<string, string>? env)
        => cacheRetention
           ?? (ProviderEnvValue.Get("PI_CACHE_RETENTION", env) == "long" ? "long" : "short");

    /// <summary>Responses compat 缺省值解析（compat 形状存于目录 Extra）。对应 TS <c>getCompat</c>。</summary>
    public static JsonObject GetCompat(ModelSpec model)
    {
        var compat = model.Extra?["compat"] as JsonObject ?? [];
        bool? Opt(string key, bool fallback)
            => compat[key] is JsonValue { } value && value.TryGetValue<bool>(out var flag) ? flag : fallback;
        return new JsonObject
        {
            ["supportsDeveloperRole"] = Opt("supportsDeveloperRole", true),
            ["supportsMidConvoSystemMessages"] = Opt("supportsMidConvoSystemMessages", false),
            ["sessionAffinityFormat"] = compat.Str("sessionAffinityFormat") ?? DetectSessionAffinityFormat(model),
            ["supportsLongCacheRetention"] = Opt("supportsLongCacheRetention", true),
            ["supportsStrictMode"] = Opt("supportsStrictMode", false),
            ["supportsOpenAIGrammarTools"] = Opt("supportsOpenAIGrammarTools", false),
            ["supportsAdditionalTools"] = Opt("supportsAdditionalTools", false),
            ["supportsToolSearch"] = Opt("supportsToolSearch", false),
            ["supportsExplicitPromptCacheMode"] = Opt("supportsExplicitPromptCacheMode", false),
            ["supportsMaxOutputTokens"] = Opt("supportsMaxOutputTokens", true),
        };
    }

    private static bool CompatBool(JsonObject compat, string key)
        => compat[key] is JsonValue { } value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? GetPromptCacheRetention(JsonObject compat, string cacheRetention)
        => cacheRetention == "long" && CompatBool(compat, "supportsLongCacheRetention")
            && !CompatBool(compat, "supportsExplicitPromptCacheMode")
            ? "24h"
            : null;

    private static JsonObject? GetPromptCacheOptions(JsonObject compat, string cacheRetention)
    {
        if (!CompatBool(compat, "supportsExplicitPromptCacheMode")) return null;
        if (cacheRetention == "none") return new JsonObject { ["mode"] = "explicit" };
        if (cacheRetention == "long" && CompatBool(compat, "supportsLongCacheRetention"))
        {
            return new JsonObject { ["ttl"] = "30m" };
        }
        return null;
    }

    /// <summary>构建 /responses 请求载荷。对应 TS <c>buildParams</c>。</summary>
    public static JsonObject BuildParams(
        ModelSpec model,
        TranscriptContext context,
        OpenAiResponsesOptions? options,
        JsonObject? compat = null,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        compat ??= GetCompat(model);
        grammarToolInputProperties ??= ConstrainedSampling.CreateGrammarToolInputProperties(
            Transcript.GetDeclaredTools(context.Messages), CompatBool(compat, "supportsOpenAIGrammarTools"));

        var supportsMidConvo = CompatBool(compat, "supportsMidConvoSystemMessages");
        var normalizedContext = Transcript.ResolveTranscript(context, supportsMidConvo);
        var transcriptTools = Transcript.ResolveTranscriptTools(
            normalizedContext.Messages,
            CompatBool(compat, "supportsAdditionalTools") || CompatBool(compat, "supportsToolSearch"));
        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model, normalizedContext, OpenAiToolCallProviders, new ConvertResponsesMessagesOptions
            {
                GrammarToolInputProperties = grammarToolInputProperties,
                SupportsMidConvoSystemMessages = supportsMidConvo,
                SupportsAdditionalTools = CompatBool(compat, "supportsAdditionalTools"),
                SupportsToolSearch = CompatBool(compat, "supportsToolSearch"),
                ToolOptions = new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = CompatBool(compat, "supportsStrictMode"),
                    SupportsOpenAiGrammarTools = CompatBool(compat, "supportsOpenAIGrammarTools"),
                },
            });

        var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
        var cacheSessionId = cacheRetention == "none" ? null : options?.SessionId;
        // Sign in with ChatGPT 拒绝这些请求字段。
        var omitUnsupportedFields = IsChatGptSignIn(model, options?.ApiKey);
        var requestParams = new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = messages,
            ["stream"] = true,
            ["prompt_cache_key"] = cacheSessionId is null ? null : OpenAiPromptCache.ClampPromptCacheKey(cacheSessionId),
            ["prompt_cache_retention"] = omitUnsupportedFields ? null : GetPromptCacheRetention(compat, cacheRetention),
            ["prompt_cache_options"] = omitUnsupportedFields ? null : GetPromptCacheOptions(compat, cacheRetention),
            ["store"] = false,
        };

        if (options?.MaxTokens is { } maxTokens && maxTokens > 0 && CompatBool(compat, "supportsMaxOutputTokens") && !omitUnsupportedFields)
        {
            requestParams["max_output_tokens"] = Math.Max(maxTokens, MinOutputTokens);
        }

        if (options?.Temperature is { } temperature && !omitUnsupportedFields)
        {
            requestParams["temperature"] = temperature;
        }

        if (options?.ServiceTier is { } serviceTier)
        {
            requestParams["service_tier"] = serviceTier;
        }

        if (transcriptTools.RequestTools.Count > 0)
        {
            requestParams["tools"] = OpenAiResponsesShared.ConvertResponsesTools(
                transcriptTools.RequestTools,
                new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = CompatBool(compat, "supportsStrictMode"),
                    SupportsOpenAiGrammarTools = CompatBool(compat, "supportsOpenAIGrammarTools"),
                });
        }

        if (options?.ToolChoice is { } toolChoice)
        {
            requestParams["tool_choice"] = toolChoice;
        }

        var reasoningEffort = options?.ReasoningEffort
            ?? (options?.ReasoningSummary is not null ? "medium" : null);
        if (model.Reasoning)
        {
            if (reasoningEffort is not null)
            {
                var effort = options?.ReasoningEffort is not null
                    ? model.ThinkingLevelMap?[options.ReasoningEffort] ?? options.ReasoningEffort
                    : reasoningEffort;
                requestParams["reasoning"] = new JsonObject
                {
                    ["effort"] = effort,
                    ["summary"] = options?.ReasoningSummary ?? "auto",
                };
                requestParams["include"] = new JsonArray("reasoning.encrypted_content");
            }
            else if (model.Provider != "github-copilot"
                && !(model.ThinkingLevelMap is { } levelMap && levelMap.Has("off") && levelMap["off"] is null))
            {
                // TS：thinkingLevelMap?.off !== null → 设置 effort（map 缺失 / off 缺省 / off 非 null 均成立）。
                requestParams["reasoning"] = new JsonObject
                {
                    ["effort"] = model.ThinkingLevelMap?["off"] ?? "none",
                };
            }
            if (model.Provider == "xai")
            {
                requestParams["include"] = new JsonArray("reasoning.encrypted_content");
            }
        }

        // 最后合并采样参数，让模型级/档位级/请求级字段覆盖上面的命名字段。对应 TS 末尾的 resolveSamplingParams。
        if (SimpleOptions.ResolveSamplingParams(model, reasoningEffort ?? "off", options?.SamplingParams) is { } sampling)
        {
            foreach (var (key, value) in sampling) requestParams[key] = value?.DeepClone();
        }

        return requestParams;
    }

    /// <summary>
    /// OpenAI Responses 流式生成：POST {baseUrl}/responses（stream:true），SSE 消费。
    /// 对应 TS <c>stream()</c>。
    /// </summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        OpenAiResponsesOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var compat = GetCompat(model);
        var supportsMidConvo = CompatBool(compat, "supportsMidConvoSystemMessages");
        var normalizedContext = Transcript.ResolveTranscript(context, supportsMidConvo);
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider);
            try
            {
                var apiKey = GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
                var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
                var cacheSessionId = cacheRetention == "none" ? null : options?.SessionId;
                var grammarToolInputProperties = ConstrainedSampling.CreateGrammarToolInputProperties(
                    Transcript.GetDeclaredTools(normalizedContext.Messages),
                    CompatBool(compat, "supportsOpenAIGrammarTools"));
                var payload = BuildParams(model, normalizedContext, options, compat, grammarToolInputProperties);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                var headers = Headers.Merge(
                    BuildDefaultHeaders(model, normalizedContext, compat, cacheSessionId),
                    options?.Headers);

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal);
                    if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl.TrimEnd('/')}/responses")
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    if (headers is not null)
                    {
                        foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
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
                var events = AiSse.EventsAsync(content, cancellationToken);
                await OpenAiResponsesShared.ProcessResponsesStream(
                    events, output, stream, model,
                    new ResponsesStreamOptions
                    {
                        OnProviderStreamEvent = options?.OnProviderStreamEvent,
                        ServiceTier = options?.ServiceTier,
                        GrammarToolInputProperties = grammarToolInputProperties,
                        ApplyServiceTierPricing = (usage, serviceTier)
                            => ApplyServiceTierPricing(usage, serviceTier, model),
                    }, cancellationToken).ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }

                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("OpenAI Responses stream ended without a stop reason");
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
                var errorMessage = ProviderError.Format(
                    ProviderError.Normalize(error),
                    $"{(model.Provider == "openai" ? "OpenAI" : model.Provider)} API error");
                // Sign in with ChatGPT 与其它应用共享订阅用量上限。
                output.ErrorMessage = errorMessage.Contains("subscription_sharing_usage_limit_exceeded")
                    ? $"{errorMessage}\nCheck your ChatGPT usage: {ChatGptUsageUrl}"
                    : errorMessage;
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>service tier 成本乘数。对应 TS <c>getServiceTierCostMultiplier</c>。</summary>
    public static double GetServiceTierCostMultiplier(ModelSpec model, string? serviceTier)
        => serviceTier switch
        {
            "flex" => 0.5,
            "priority" or "fast" => model.Id == "gpt-5.5" ? 2.5 : 2,
            _ => 1,
        };

    /// <summary>按 service tier 折算用量成本。对应 TS <c>applyServiceTierPricing</c>。</summary>
    public static Usage ApplyServiceTierPricing(Usage usage, string? serviceTier, ModelSpec model)
    {
        var multiplier = GetServiceTierCostMultiplier(model, serviceTier);
        if (multiplier == 1) return usage;
        return usage with { Cost = usage.Cost.Scale(multiplier) };
    }

    /// <summary>
    /// 由 <see cref="SimpleStreamOptions"/> 装配 Responses 专属选项的公共字段。
    /// 对应 TS <c>buildBaseOptions</c>（simple-options.ts）——samplingParams 按模型级/
    /// 档位级/请求级合并；maxTokens 按上下文窗口收敛（缺省取 model.maxTokens，0 视为未设置）。
    /// </summary>
    public static OpenAiResponsesOptions FromSimple(
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
        return new OpenAiResponsesOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            SessionId = options?.SessionId,
            CacheRetention = options?.CacheRetention,
            Metadata = options?.Metadata,
            Env = options?.Env,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice,
            SamplingParams = samplingParams,
            MaxTokens = maxTokens,
            Temperature = options?.Temperature,
        };
    }

    /// <summary>
    /// 简单入口流式生成。对应 TS <c>streamSimple</c>：reasoning 档位经模型收敛
    /// （off → 不发 reasoning），toolChoice 透传。
    /// </summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);

        var clampedReasoning = options?.Reasoning is not null
            ? ThinkingLevels.Clamp(model, options.Reasoning)
            : null;
        var reasoningEffort = clampedReasoning == "off" ? null : clampedReasoning;

        var baseOptions = FromSimple(model, context, options);
        return Stream(model, context, baseOptions with
        {
            ReasoningEffort = reasoningEffort,
        }, httpClient, cancellationToken);
    }

    /// <summary>默认请求头（UA / copilot 动态头 / session 亲和）。对应 TS <c>createClient</c> 头部逻辑。</summary>
    private static IReadOnlyDictionary<string, string?> BuildDefaultHeaders(
        ModelSpec model, TranscriptContext context, JsonObject compat, string? sessionId)
    {
        var headers = new Dictionary<string, string?>
        {
            ["User-Agent"] = PiUserAgent.Get(),
        };
        if (model.Extra?["headers"] is JsonObject modelHeaders)
        {
            foreach (var (key, value) in modelHeaders)
            {
                headers[key] = value is JsonValue { } primitive && primitive.TryGetValue<string>(out var text) ? text : null;
            }
        }
        if (model.Provider == "github-copilot")
        {
            foreach (var (key, value) in GitHubCopilotHeaders.BuildDynamicHeaders(
                context.Messages, GitHubCopilotHeaders.HasVisionInput(context.Messages)))
            {
                headers[key] = value;
            }
        }

        if (sessionId is not null)
        {
            if (compat.Str("sessionAffinityFormat") == "openrouter")
            {
                headers["x-session-id"] = sessionId;
            }
            else
            {
                if (compat.Str("sessionAffinityFormat") == "openai")
                {
                    headers["session_id"] = sessionId;
                }
                headers["x-client-request-id"] = sessionId;
            }
        }
        return headers;
    }
}
