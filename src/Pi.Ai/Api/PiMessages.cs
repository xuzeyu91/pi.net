using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>pi-messages 专属流选项。对应 TS <c>PiMessagesOptions</c>。</summary>
public record PiMessagesOptions
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

    /// <summary>推理档位。对应 TS <c>reasoning</c>。</summary>
    public string? Reasoning { get; init; }

    /// <summary>工具选择（auto/none/required 或 {type:function,function:{name}}）。</summary>
    public JsonNode? ToolChoice { get; init; }

    /// <summary>向后端请求调试元数据（如路由响应头）。对应 TS <c>debug</c>。</summary>
    public bool Debug { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>pi-messages 后端响应错误（带 wire code 与诊断详情）。对应 TS <c>PiMessagesResponseError</c>。</summary>
public sealed class PiMessagesResponseError : Exception
{
    public string? Code { get; }

    public JsonObject DiagnosticDetails { get; }

    public PiMessagesResponseError(string message, string? code, JsonObject diagnosticDetails)
        : base(message)
    {
        Code = code;
        DiagnosticDetails = diagnosticDetails;
    }
}

/// <summary>
/// pi-messages API：把 pi 自己的消息协议直连后端——单次 POST {model, context, options} 到
/// <c>&lt;baseUrl&gt;/messages</c>，响应是序列化助手消息事件的 SSE 流 + 终态 done/error 事件。
/// 这是 Radius 网关的 wire 协议；任何实现它的后端都可用（models.json 自定义 provider）。
/// 对应 TS <c>api/pi-messages.ts</c>。
/// </summary>
public static class PiMessages
{
    /// <summary>流式生成。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        PiMessagesOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var eventStream = new AssistantMessageEventStream();
        var converter = new EventConverter(model);
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            try
            {
                var apiKey = options?.ApiKey;
                if (string.IsNullOrEmpty(apiKey))
                {
                    throw new InvalidOperationException($"No API key provided for provider \"{model.Provider}\"");
                }

                var url = $"{model.BaseUrl.TrimEnd('/')}/messages";
                if (options?.Debug == true)
                {
                    url += "?debug=1";
                }

                JsonObject payload = new()
                {
                    ["model"] = model.Id,
                    ["context"] = JsonSerializer.SerializeToNode(
                        context, PiMessagesJson.Options)!.AsObject(),
                    ["options"] = new JsonObject
                    {
                        ["temperature"] = options?.Temperature,
                        ["maxTokens"] = options?.MaxTokens,
                        ["reasoning"] = options?.Reasoning,
                        ["cacheRetention"] = ResolveCacheRetention(options?.CacheRetention, options?.Env),
                        ["sessionId"] = options?.SessionId,
                        ["toolChoice"] = options?.ToolChoice?.DeepClone(),
                    },
                };
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
                request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
                if (options?.Headers is not null)
                {
                    foreach (var (key, value) in options.Headers)
                    {
                        if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                    }
                }
                var response = await client.SendAsync(
                    request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, perCallCts.Token)
                    .ConfigureAwait(false);

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
                    throw CreateResponseError(model, url, response, errorBody);
                }

                await using var content = await response.Content
                    .ReadAsStreamAsync(perCallCts.Token).ConfigureAwait(false);
                await foreach (var piEvent in ReadEvents(content, perCallCts.Token).ConfigureAwait(false))
                {
                    if (options?.OnProviderStreamEvent is { } onProviderStreamEvent)
                    {
                        await onProviderStreamEvent(piEvent, model).ConfigureAwait(false);
                    }
                    var @event = converter.Convert(piEvent);
                    eventStream.Push(@event);
                    if (@event.IsTerminal)
                    {
                        return;
                    }
                }

                throw new InvalidOperationException(
                    $"{model.Provider} stream ended without a terminal event");
            }
            catch (Exception error)
            {
                var failure = CreateErrorEvent(model, error, signal.IsCancellationRequested);
                eventStream.Push(failure);
                if (failure is AssistantMessageEvent.Error { Message: var failedMessage })
                {
                    eventStream.End(failedMessage);
                }
            }
        }, CancellationToken.None);

        return eventStream;
    }

    /// <summary>简单入口。对应 TS <c>streamSimple</c>（公共字段直通）。</summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
        => Stream(model, context, new PiMessagesOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxTokens = options?.MaxTokens,
            Temperature = options?.Temperature,
            SessionId = options?.SessionId,
            CacheRetention = options?.CacheRetention,
            Env = options?.Env,
            Reasoning = options?.Reasoning,
            ToolChoice = options?.ToolChoice,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
        }, httpClient, cancellationToken);

    /// <summary>缓存保留偏好：后端缺省生效，仅映射旧版 env 选入。对应 TS <c>resolveCacheRetention</c>。</summary>
    private static string? ResolveCacheRetention(string? cacheRetention, IReadOnlyDictionary<string, string>? env)
        => cacheRetention
           ?? (ProviderEnvValue.Get("PI_CACHE_RETENTION", env) == "long" ? "long" : null);

    // ============================================================================
    // SSE 事件解析
    // ============================================================================

    private static async IAsyncEnumerable<JsonObject> ReadEvents(
        System.IO.Stream body,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var buffer = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            line = line.Replace("\r\n", "\n");
            buffer.Append(line).Append('\n');
            var text = buffer.ToString();
            int split;
            while ((split = text.IndexOf("\n\n", StringComparison.Ordinal)) != -1)
            {
                var @event = ParseEvent(text[..split]);
                if (@event is not null) yield return @event;
                text = text[(split + 2)..];
            }
            buffer.Clear();
            buffer.Append(text);
        }
        var tail = buffer.ToString().Trim();
        if (tail.Length > 0)
        {
            var @event = ParseEvent(tail);
            if (@event is not null) yield return @event;
        }
    }

    private static JsonObject? ParseEvent(string raw)
    {
        var data = raw
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))
            is { } dataLine ? dataLine[5..].Trim() : null;
        if (data is null || data.Length == 0 || data == "[DONE]") return null;
        return JsonNode.Parse(data) as JsonObject;
    }

    // ============================================================================
    // 事件转换（content-index 寻址的增量累积）
    // ============================================================================

    private sealed class EventConverter
    {
        private readonly OpenAiResponsesShared.MutableAssistantMessage _partial;
        private readonly ModelSpec _model;
        private readonly Dictionary<int, string> _toolJson = new();

        public EventConverter(ModelSpec model)
        {
            _model = model;
            _partial = new OpenAiResponsesShared.MutableAssistantMessage(model.Id, model.Api, model.Provider);
        }

        public AssistantMessageEvent Convert(JsonObject @event)
        {
            var type = @event.Str("type");
            switch (type)
            {
                case "done":
                {
                    _partial.StopReason = MapStopReason(@event.Str("reason"), terminal: true);
                    _partial.Usage = ParseUsage(@event["usage"]);
                    _partial.ResponseId = @event.Str("responseId");
                    if (@event.Str("providerThinkingLevel") is { } thinkingLevel)
                    {
                        _partial.ProviderThinkingLevel = thinkingLevel;
                    }
                    AppendRewriteDiagnostic(@event.Obj("rewrite"));
                    return new AssistantMessageEvent.Done(_partial.StopReason, _partial.Snapshot());
                }
                case "error":
                {
                    _partial.StopReason = MapStopReason(@event.Str("reason"), terminal: false);
                    _partial.ErrorMessage = @event.Str("errorMessage");
                    _partial.Usage = ParseUsage(@event["usage"]);
                    _partial.ResponseId = @event.Str("responseId");
                    if (@event.Str("providerThinkingLevel") is { } errorThinkingLevel)
                    {
                        _partial.ProviderThinkingLevel = errorThinkingLevel;
                    }
                    AppendRewriteDiagnostic(@event.Obj("rewrite"));
                    return new AssistantMessageEvent.Error(
                        _partial.StopReason, _partial.ErrorMessage ?? "", _partial.Snapshot());
                }
                case "text_start":
                {
                    var index = ContentIndex(@event);
                    SetBlock(index, new TextContent(""));
                    return SimpleEvent(@event, "TextStart", index);
                }
                case "text_delta":
                {
                    var index = ContentIndex(@event);
                    if (GetBlock(index) is TextContent textBlock)
                    {
                        _partial.Content[index] = textBlock with { Text = textBlock.Text + (@event.Str("delta") ?? "") };
                    }
                    return SimpleEvent(@event, "TextDelta", index);
                }
                case "text_end":
                {
                    var index = ContentIndex(@event);
                    if (GetBlock(index) is TextContent endTextBlock)
                    {
                        _partial.Content[index] = endTextBlock with
                        {
                            Text = @event.Str("content") ?? "",
                            TextSignature = @event.Str("contentSignature"),
                        };
                    }
                    return SimpleEvent(@event, "TextEnd", index, @event.Str("content") ?? "");
                }
                case "thinking_start":
                {
                    var index = ContentIndex(@event);
                    SetBlock(index, new ThinkingContent(""));
                    return SimpleEvent(@event, "ThinkingStart", index);
                }
                case "thinking_delta":
                {
                    var index = ContentIndex(@event);
                    if (GetBlock(index) is ThinkingContent thinkingBlock)
                    {
                        _partial.Content[index] = thinkingBlock with
                        {
                            Thinking = thinkingBlock.Thinking + (@event.Str("delta") ?? ""),
                        };
                    }
                    return SimpleEvent(@event, "ThinkingDelta", index);
                }
                case "thinking_end":
                {
                    var index = ContentIndex(@event);
                    if (GetBlock(index) is ThinkingContent endThinkingBlock)
                    {
                        _partial.Content[index] = endThinkingBlock with
                        {
                            Thinking = @event.Str("content") ?? "",
                            Signature = @event.Str("contentSignature"),
                            Redacted = @event.Bool("redacted") == true,
                        };
                    }
                    return SimpleEvent(@event, "ThinkingEnd", index, @event.Str("content") ?? "");
                }
                case "toolcall_start":
                {
                    var index = ContentIndex(@event);
                    SetBlock(index, new ToolCallContent(
                        @event.Str("id") ?? "", @event.Str("toolName") ?? "", new JsonObject()));
                    _toolJson[index] = "";
                    return SimpleEvent(@event, "ToolCallStart", index);
                }
                case "toolcall_delta":
                {
                    var index = ContentIndex(@event);
                    var json = (_toolJson.GetValueOrDefault(index) ?? "") + (@event.Str("delta") ?? "");
                    _toolJson[index] = json;
                    if (GetBlock(index) is ToolCallContent deltaCall)
                    {
                        _partial.Content[index] = deltaCall with
                        {
                            Arguments = JsonParse.ParseStreamingJson(json) ?? new JsonObject(),
                        };
                    }
                    return SimpleEvent(@event, "ToolCallDelta", index);
                }
                case "toolcall_end":
                {
                    var index = ContentIndex(@event);
                    _toolJson.Remove(index);
                    if (GetBlock(index) is ToolCallContent endCall
                        && @event["toolCall"] is JsonObject finalCall)
                    {
                        endCall = endCall with
                        {
                            Id = finalCall.Str("id") ?? endCall.Id,
                            Name = finalCall.Str("name") ?? endCall.Name,
                            Arguments = finalCall["arguments"]?.DeepClone() ?? endCall.Arguments,
                            Namespace = finalCall.Str("namespace") ?? endCall.Namespace,
                        };
                        _partial.Content[index] = endCall;
                        return new AssistantMessageEvent.ToolCallEnd(index, endCall, _partial.Snapshot());
                    }
                    return SimpleEvent(@event, "ToolCallEnd", index);
                }
                case "start":
                    return new AssistantMessageEvent.Start(_partial.Snapshot());
                default:
                    return SimpleEvent(@event, "Start", 0);
            }
        }

        private AssistantMessageEvent SimpleEvent(JsonObject @event, string eventName, int index, string content = "")
        {
            var snapshot = _partial.Snapshot();
            return eventName switch
            {
                "TextStart" => new AssistantMessageEvent.TextStart(index, snapshot),
                "TextDelta" => new AssistantMessageEvent.TextDelta(
                    index, @event.Str("delta") ?? "", 0, snapshot),
                "TextEnd" => new AssistantMessageEvent.TextEnd(index, content, snapshot),
                "ThinkingStart" => new AssistantMessageEvent.ThinkingStart(index, snapshot),
                "ThinkingDelta" => new AssistantMessageEvent.ThinkingDelta(
                    index, @event.Str("delta") ?? "", 0, null, snapshot),
                "ThinkingEnd" => new AssistantMessageEvent.ThinkingEnd(index, content, snapshot),
                "ToolCallStart" => new AssistantMessageEvent.ToolCallStart(index, snapshot),
                "ToolCallDelta" => new AssistantMessageEvent.ToolCallDelta(
                    index, index, @event.Str("delta") ?? "", snapshot),
                "ToolCallEnd" => new AssistantMessageEvent.ToolCallEnd(
                    index, new ToolCallContent("", "", new JsonObject()), snapshot),
                _ => new AssistantMessageEvent.Start(snapshot),
            };
        }

        private void AppendRewriteDiagnostic(JsonObject? rewrite)
        {
            if (rewrite is null) return;
            _partial.Diagnostics.Add(Diagnostics.CreateAssistantMessageDiagnostic(
                "pi_messages_rewrite", null, (JsonObject)rewrite.DeepClone()));
        }

        private int ContentIndex(JsonObject @event)
            => (int)(@event.Num("contentIndex") ?? 0);

        private ContentBlock? GetBlock(int index)
            => index >= 0 && index < _partial.Content.Count ? _partial.Content[index] : null;

        private void SetBlock(int index, ContentBlock block)
        {
            while (_partial.Content.Count <= index) _partial.Content.Add(new TextContent(""));
            _partial.Content[index] = block;
        }
    }

    private static StopReason MapStopReason(string? reason, bool terminal)
        => reason switch
        {
            "stop" => StopReason.Stop,
            "length" => StopReason.Length,
            "toolUse" => StopReason.ToolUse,
            "aborted" => StopReason.Aborted,
            "error" => StopReason.Error,
            _ => terminal ? StopReason.Error : StopReason.Pending,
        };

    /// <summary>
    /// done/error 事件上的 usage 字段。TS 侧是 <c>event.usage</c> 原样透传
    /// （pi-messages.ts 的 <c>usage: event.usage</c>），故这里按 wire 的完整形状重建：
    /// 可选桶仅在 wire 存在时才带（区分 undefined 与 0），cost 取嵌套对象的五桶。
    /// </summary>
    private static Usage? ParseUsage(JsonNode? usage)
    {
        if (usage is not JsonObject usageObject) return new Usage(0, 0);
        var cost = usageObject.Obj("cost");
        return new Usage(
            (long)(usageObject.Num("input") ?? 0),
            (long)(usageObject.Num("output") ?? 0),
            (long)(usageObject.Num("cacheRead") ?? 0),
            (long)(usageObject.Num("cacheWrite") ?? 0))
        {
            CacheWrite1h = usageObject.Num("cacheWrite1h") is { } cacheWrite1h ? (long)cacheWrite1h : null,
            Reasoning = usageObject.Num("reasoning") is { } reasoning ? (long)reasoning : null,
            TotalTokens = (long)(usageObject.Num("totalTokens") ?? 0),
            Cost = cost is null
                ? UsageCost.Zero
                : new UsageCost(
                    cost.Num("input") ?? 0,
                    cost.Num("output") ?? 0,
                    cost.Num("cacheRead") ?? 0,
                    cost.Num("cacheWrite") ?? 0,
                    cost.Num("total") ?? 0),
        };
    }

    private static PiMessagesResponseError CreateResponseError(
        ModelSpec model, string url, System.Net.Http.HttpResponseMessage response, string body)
    {
        JsonObject? errorBody;
        try
        {
            errorBody = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            errorBody = null;
        }
        var error = errorBody?.Obj("error");
        var message = error?.Str("message");
        var code = error?.Str("code");
        var suffix = message ?? body;
        var codeSuffix = code is not null ? $" ({code})" : "";
        var diagnosticDetails = new JsonObject
        {
            ["version"] = 1,
            ["provider"] = model.Provider,
            ["model"] = model.Id,
            ["url"] = url,
            ["status"] = (int)response.StatusCode,
            ["statusText"] = response.ReasonPhrase ?? "",
            ["timestampMs"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
        if (error is not null) diagnosticDetails["error"] = error.DeepClone();
        else diagnosticDetails["body"] = TruncateDiagnosticString(body);
        return new PiMessagesResponseError(
            $"{(int)response.StatusCode} {response.ReasonPhrase}: {suffix}{codeSuffix}",
            code, diagnosticDetails);
    }

    private static string TruncateDiagnosticString(string value)
        => value.Length > 8192 ? $"{value[..8192]}…" : value;

    /// <summary>终态错误事件（携带响应失败诊断）。对应 TS <c>createErrorEvent</c>。</summary>
    private static AssistantMessageEvent CreateErrorEvent(ModelSpec model, Exception error, bool aborted)
    {
        var reason = aborted ? StopReason.Aborted : StopReason.Error;
        var message = new AssistantMessage(
            [], reason, error.Message, new Usage(0, 0), model.Id, model.Api, model.Provider,
            Timestamp: DateTimeOffset.Now.ToUnixTimeMilliseconds());
        if (!aborted && error is PiMessagesResponseError responseError)
        {
            message.Diagnostics =
            [
                Diagnostics.CreateAssistantMessageDiagnostic(
                    "pi_messages_response_failure", error, responseError.DiagnosticDetails),
            ];
        }
        return new AssistantMessageEvent.Error(reason, error.Message, message);
    }
}

/// <summary>pi-messages 序列化选项。</summary>
internal static class PiMessagesJson
{
    /// <summary>TranscriptContext 序列化（判别 role 多态由标注驱动）。</summary>
    public static readonly JsonSerializerOptions Options = new();
}
