using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Azure OpenAI Responses 专属流选项。对应 TS <c>AzureOpenAIResponsesOptions</c>。</summary>
public record AzureOpenAiResponsesOptions
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

    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>推理档位（minimal/low/medium/high/xhigh/max）。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>推理摘要级别（auto/detailed/concise）。</summary>
    public string? ReasoningSummary { get; init; }

    /// <summary>工具选择（透传 provider 原生形状）。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>Azure API 版本（缺省 v1；AZURE_OPENAI_API_VERSION）。</summary>
    public string? AzureApiVersion { get; init; }

    /// <summary>Azure 资源名（构造 https://{resource}.openai.azure.com/openai/v1）。</summary>
    public string? AzureResourceName { get; init; }

    /// <summary>显式 Azure baseUrl。</summary>
    public string? AzureBaseUrl { get; init; }

    /// <summary>显式部署名（覆盖模型 id 与 env 映射表）。</summary>
    public string? AzureDeploymentName { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>
/// Azure OpenAI Responses API 流式生成。对应 TS <c>api/azure-openai-responses.ts</c>：
/// 与 openai-responses 共享消息/工具转换与流处理，差异在部署名解析、baseUrl 归一化与
/// api-version 查询参数（TS 用 AzureOpenAI SDK；C# REST 等价实现）。
/// </summary>
public static class AzureOpenAiResponses
{
    private const string DefaultAzureApiVersion = "v1";

    private static readonly HashSet<string> AzureToolCallProviders =
        ["openai", "openai-codex", "opencode", "azure-openai-responses"];

    /// <summary>工具调用 ID 归一化路径适用的 provider 集（测试可见）。</summary>
    public static IReadOnlySet<string> ProviderIdSet => AzureToolCallProviders;

    // OpenAI Responses 拒绝低于 16 的 max_output_tokens。（pi#6265）
    private const int MinOutputTokens = 16;

    /// <summary>解析 AZURE_OPENAI_DEPLOYMENT_NAME_MAP（"model=deployment,model2=deployment2"）。对应 TS <c>parseDeploymentNameMap</c>。</summary>
    public static Dictionary<string, string> ParseDeploymentNameMap(string? value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value)) return map;
        foreach (var entry in value.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split('=', 2);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
            map[parts[0].Trim()] = parts[1].Trim();
        }
        return map;
    }

    /// <summary>部署名解析：option &gt; env 映射表 &gt; 模型 id。对应 TS <c>resolveDeploymentName</c>。</summary>
    public static string ResolveDeploymentName(ModelSpec model, AzureOpenAiResponsesOptions? options)
    {
        if (options?.AzureDeploymentName is { Length: > 0 } explicitName) return explicitName;
        var mapped = ParseDeploymentNameMap(
            ProviderEnvValue.Get("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", options?.Env))
            .GetValueOrDefault(model.Id) ?? "";
        return mapped.Length > 0 ? mapped : model.Id;
    }

    /// <summary>Azure baseUrl 归一化：Azure 官方主机确保 /openai/v1 基路径。对应 TS <c>normalizeAzureBaseUrl</c>。</summary>
    public static string NormalizeAzureBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        Uri url;
        try
        {
            url = new Uri(trimmed);
        }
        catch (UriFormatException)
        {
            throw new InvalidOperationException($"Invalid Azure OpenAI base URL: {baseUrl}");
        }

        var host = url.Host;
        var isAzureHost = host.EndsWith(".openai.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".cognitiveservices.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".ai.azure.com", StringComparison.Ordinal);
        var normalizedPath = url.AbsolutePath.TrimEnd('/');

        if (isAzureHost
            && (normalizedPath.Length == 0
                || normalizedPath == "/openai"
                || normalizedPath == "/openai/v1/responses"))
        {
            return $"{url.Scheme}://{url.Authority}/openai/v1";
        }
        return trimmed.TrimEnd('/');
    }

    private static string BuildDefaultBaseUrl(string resourceName)
        => $"https://{resourceName}.openai.azure.com/openai/v1";

    /// <summary>baseUrl/apiVersion 解析。对应 TS <c>resolveAzureConfig</c>。</summary>
    public static (string BaseUrl, string ApiVersion) ResolveAzureConfig(
        ModelSpec model, AzureOpenAiResponsesOptions? options)
    {
        var apiVersion = options?.AzureApiVersion
            ?? ProviderEnvValue.Get("AZURE_OPENAI_API_VERSION", options?.Env)
            ?? DefaultAzureApiVersion;

        var baseUrl = options?.AzureBaseUrl?.Trim()
            ?? ProviderEnvValue.Get("AZURE_OPENAI_BASE_URL", options?.Env)?.Trim();
        var resourceName = options?.AzureResourceName
            ?? ProviderEnvValue.Get("AZURE_OPENAI_RESOURCE_NAME", options?.Env);

        if (string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(resourceName))
        {
            baseUrl = BuildDefaultBaseUrl(resourceName);
        }
        if (string.IsNullOrEmpty(baseUrl) && model.BaseUrl.Length > 0)
        {
            baseUrl = model.BaseUrl;
        }
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException(
                "Azure OpenAI base URL is required. Set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME, "
                + "or pass azureBaseUrl, azureResourceName, or model.baseUrl.");
        }

        return (NormalizeAzureBaseUrl(baseUrl), apiVersion);
    }

    /// <summary>构建 /responses 请求载荷。对应 TS <c>buildParams</c>。</summary>
    public static JsonObject BuildParams(
        ModelSpec model,
        TranscriptContext context,
        AzureOpenAiResponsesOptions? options,
        string deploymentName,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        var compat = model.Compat;
        bool CompatBool(string key, bool fallback)
            => compat is not null && compat[key] is JsonValue { } value && value.TryGetValue<bool>(out var flag)
                ? flag
                : fallback;

        var supportsAdditionalTools = CompatBool("supportsAdditionalTools", false);
        var supportsToolSearch = CompatBool("supportsToolSearch", false);
        var supportsMidConvo = CompatBool("supportsMidConvoSystemMessages", false);
        var normalizedContext = Transcript.ResolveTranscript(context, supportsMidConvo);
        var transcriptTools = Transcript.ResolveTranscriptTools(
            normalizedContext.Messages, supportsAdditionalTools || supportsToolSearch);
        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model, normalizedContext, AzureToolCallProviders, new ConvertResponsesMessagesOptions
            {
                GrammarToolInputProperties = grammarToolInputProperties,
                SupportsMidConvoSystemMessages = supportsMidConvo,
                SupportsAdditionalTools = supportsAdditionalTools,
                SupportsToolSearch = supportsToolSearch,
                ToolOptions = new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = CompatBool("supportsStrictMode", true),
                    SupportsOpenAiGrammarTools = CompatBool("supportsOpenAIGrammarTools", false),
                },
            });

        var requestParams = new JsonObject
        {
            ["model"] = deploymentName,
            ["input"] = messages,
            ["stream"] = true,
            ["prompt_cache_key"] = options?.SessionId is { } sessionId
                ? OpenAiPromptCache.ClampPromptCacheKey(sessionId)
                : null,
            ["store"] = false,
        };

        if (options?.MaxTokens is { } maxTokens && maxTokens > 0)
        {
            requestParams["max_output_tokens"] = Math.Max(maxTokens, MinOutputTokens);
        }

        if (options?.Temperature is { } temperature)
        {
            requestParams["temperature"] = temperature;
        }

        if (transcriptTools.RequestTools.Count > 0)
        {
            requestParams["tools"] = OpenAiResponsesShared.ConvertResponsesTools(
                transcriptTools.RequestTools,
                new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = CompatBool("supportsStrictMode", true),
                    SupportsOpenAiGrammarTools = CompatBool("supportsOpenAIGrammarTools", false),
                });
        }

        if (options?.ToolChoice is { } toolChoice)
        {
            requestParams["tool_choice"] = toolChoice.DeepClone();
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
            else if (!(model.ThinkingLevelMap is { } azureLevelMap
                && azureLevelMap.Has("off") && azureLevelMap["off"] is null))
            {
                // TS：thinkingLevelMap?.off !== null → 设置 effort（map 缺失 / off 缺省 / off 非 null 均成立）。
                requestParams["reasoning"] = new JsonObject
                {
                    ["effort"] = model.ThinkingLevelMap?["off"] ?? "none",
                };
            }
        }

        // 最后合并采样参数。对应 TS 末尾的 resolveSamplingParams。
        if (SimpleOptions.ResolveSamplingParams(model, reasoningEffort ?? "off", options?.SamplingParams) is { } sampling)
        {
            foreach (var (key, value) in sampling) requestParams[key] = value?.DeepClone();
        }

        return requestParams;
    }

    /// <summary>由 <see cref="SimpleStreamOptions"/> 装配公共字段（对应 TS <c>buildBaseOptions</c> + streamSimple）。</summary>
    public static AzureOpenAiResponsesOptions FromSimple(
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
        return new AzureOpenAiResponsesOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            SessionId = options?.SessionId,
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
        var reasoningEffort = clampedReasoning == "off" ? null : clampedReasoning;

        var baseOptions = FromSimple(model, context, options);
        return Stream(model, context, baseOptions with
        {
            ReasoningEffort = reasoningEffort,
        }, httpClient, cancellationToken);
    }

    /// <summary>流式生成（POST {baseUrl}/openai/v1/responses?api-version=…，SSE 消费）。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        AzureOpenAiResponsesOptions? options = null,
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
                if (string.IsNullOrEmpty(options?.ApiKey))
                {
                    throw new InvalidOperationException($"No API key for provider: {model.Provider}");
                }
                var deploymentName = ResolveDeploymentName(model, options);
                var (baseUrl, apiVersion) = ResolveAzureConfig(model, options);
                var grammarToolInputProperties = ConstrainedSampling.CreateGrammarToolInputProperties(
                    Transcript.GetDeclaredTools(normalizedContext.Messages),
                    model.Compat?["supportsOpenAIGrammarTools"] is JsonValue { } grammar
                        && grammar.TryGetValue<bool>(out var grammarFlag) && grammarFlag);
                var payload = BuildParams(model, normalizedContext, options, deploymentName, grammarToolInputProperties);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal);
                    if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/openai/v1/responses")
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    request.Headers.TryAddWithoutValidation("api-key", options!.ApiKey);
                    request.Headers.TryAddWithoutValidation("User-Agent", PiUserAgent.Get());
                    if (model.Headers is not null)
                    {
                        foreach (var (key, value) in model.Headers) request.Headers.TryAddWithoutValidation(key, value);
                    }
                    if (options.Headers is not null)
                    {
                        foreach (var (key, value) in options.Headers)
                        {
                            if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                    // api-version 走查询串（Azure SDK 行为等价）。
                    var separator = request.RequestUri!.Query.Length > 0 ? "&" : "?";
                    request.RequestUri = new Uri(
                        $"{request.RequestUri}{separator}api-version={Uri.EscapeDataString(apiVersion)}");
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
                        GrammarToolInputProperties = grammarToolInputProperties,
                    }, cancellationToken).ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Azure OpenAI Responses stream ended without a stop reason");
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
                output.ErrorMessage = ProviderError.Format(
                    ProviderError.Normalize(error), "Azure OpenAI API error");
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }
}
