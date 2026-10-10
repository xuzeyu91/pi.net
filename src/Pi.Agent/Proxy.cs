using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Agent.Types;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Agent;

/// <summary>
/// Proxy 流式函数。对应 TS <c>proxy.ts</c>：把 LLM 调用通过 proxy 服务器转发
/// （服务器管理认证并代理到各 LLM provider），服务器下发时剥掉 delta 事件的
/// partial 字段以节省带宽，客户端按事件重建部分消息。
/// <para>
/// 作为 <see cref="StreamFn"/> 使用：<c>streamFn: (m, c, o, ct) =&gt;
/// Proxy.StreamProxy(m, c, ProxyOptions, httpClient)</c>。
/// </para>
/// </summary>
public static class Proxy
{
    private static readonly HttpClient SharedHttpClient = new();

    // 请求体序列化约定与 ai 包一致（camelCase；null 字段省略，对齐 TS JSON.stringify 对 undefined 的省略）。
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 发起 proxy 流式请求。对应 TS <c>streamProxy</c>。返回的事件流可立即迭代；
    /// 请求在后台进行，终态（done / error）始终会到达（EOF 无终态也会补 error 事件）。
    /// </summary>
    /// <param name="model">请求的模型（透传给 proxy 服务器）。</param>
    /// <param name="context">对话 transcript。</param>
    /// <param name="options">proxy 选项（含认证令牌与服务器地址）。</param>
    /// <param name="httpClient">可注入的 HTTP 客户端（测试替身）；缺省用进程内共享实例。</param>
    public static IAssistantMessageEventStream StreamProxy(
        Model model,
        TranscriptContext context,
        ProxyStreamOptions options,
        HttpClient? httpClient = null)
    {
        var stream = new AssistantMessageEventStream();
        _ = PumpAsync(model, context, options, httpClient ?? SharedHttpClient, stream);
        return stream;
    }

    private static async Task PumpAsync(
        Model model,
        TranscriptContext context,
        ProxyStreamOptions options,
        HttpClient httpClient,
        AssistantMessageEventStream stream)
    {
        var pump = new Pump(model, stream);
        try
        {
            using var response = await httpClient
                .SendAsync(BuildRequest(model, context, options), HttpCompletionOption.ResponseHeadersRead, options.Signal)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 对齐 TS：非 2xx 读错误体里的 error 字段，失败则保留状态码文案，最终转为 error 事件。
                throw new InvalidOperationException(
                    await ReadProxyErrorAsync(response, options.Signal).ConfigureAwait(false));
            }

            await using var body = await response.Content.ReadAsStreamAsync(options.Signal).ConfigureAwait(false);
            await AiSse.ConsumeAsync(body, pump.ProcessSseEvent, options.Signal).ConfigureAwait(false);

            // 对齐 TS 循环后的 aborted 检查：取消时 ConsumeAsync 静默退出，这里补抛转为 aborted 终态。
            options.Signal.ThrowIfCancellationRequested();

            pump.EndOfStream();
        }
        catch (Exception error)
        {
            // 对齐 TS 的外层 catch：所有失败（含取消）都编码为 error 终态事件，绝不让流悬挂。
            pump.FailWith(error, options.Signal);
        }
    }

    private static HttpRequestMessage BuildRequest(
        Model model, TranscriptContext context, ProxyStreamOptions options)
    {
        var payload = new JsonObject
        {
            ["model"] = JsonSerializer.SerializeToNode(model, RequestJsonOptions),
            ["context"] = JsonSerializer.SerializeToNode(context, RequestJsonOptions),
            ["options"] = JsonSerializer.SerializeToNode(ToRequestOptions(options), RequestJsonOptions),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ProxyUrl}/api/stream")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        return request;
    }

    /// <summary>只挑可序列化字段。对应 TS <c>buildProxyRequestOptions</c>。</summary>
    private static ProxySerializableStreamOptions ToRequestOptions(ProxyStreamOptions options)
        => new()
        {
            Temperature = options.Temperature,
            SamplingParams = options.SamplingParams,
            MaxTokens = options.MaxTokens,
            Reasoning = options.Reasoning,
            CacheRetention = options.CacheRetention,
            SessionId = options.SessionId,
            Headers = options.Headers,
            Metadata = options.Metadata,
            Transport = options.Transport,
            ThinkingBudgets = options.ThinkingBudgets,
            MaxRetryDelayMs = options.MaxRetryDelayMs,
        };

    private static async Task<string> ReadProxyErrorAsync(HttpResponseMessage response, CancellationToken signal)
    {
        var message = $"Proxy error: {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var body = await response.Content.ReadAsStringAsync(signal).ConfigureAwait(false);
            if (JsonNode.Parse(body) is JsonObject errorData
                && errorData["error"] is JsonValue errorValue
                && errorValue.TryGetValue<string>(out var error)
                && error.Length > 0)
            {
                message = $"Proxy error: {error}";
            }
        }
        catch (JsonException)
        {
            // 错误响应体不是 JSON——保留状态码文案（对齐 TS 的 catch {}）。
        }
        return message;
    }

    /// <summary>
    /// 单个 proxy 连接的部分消息重建器。对应 TS <c>streamProxy</c>闭包内的
    /// <c>partial</c> + <c>processProxyEvent</c>：按 wire 事件重建部分消息并派生归一化事件。
    /// </summary>
    private sealed class Pump
    {
        private readonly AssistantMessageEventStream _stream;
        private readonly List<ContentBlock> _content = [];
        private readonly Dictionary<int, StringBuilder> _partialJson = [];
        private AssistantMessage _partial;
        private long _sequence;
        private bool _sawTerminal;

        public Pump(Model model, AssistantMessageEventStream stream)
        {
            _stream = stream;
            // 对齐 TS 的初始 partial：pending + 零用量 + 模型标识。
            _partial = new AssistantMessage(
                Content: [],
                StopReason: StopReason.Pending,
                UsageStats: new Usage(0, 0),
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>处理一条 SSE 事件（data 载荷是 proxy 事件 JSON）。对应 TS <c>processLine</c>。</summary>
        public void ProcessSseEvent(AiSseEvent sse)
        {
            var data = sse.Data.Trim();
            if (data.Length == 0) return;
            var proxyEvent = ProxyEventJson.Parse(data);
            if (proxyEvent is null) return;
            if (Apply(proxyEvent) is { } @event)
            {
                if (@event.IsTerminal) _sawTerminal = true;
                _stream.Push(@event);
            }
        }

        /// <summary>流干净结束。无终态事件时补 error（服务器中途断流）。对应 TS <c>stream.end()</c> 前段。</summary>
        public void EndOfStream()
        {
            if (!_sawTerminal)
            {
                _partial = _partial with
                {
                    StopReason = StopReason.Error,
                    ErrorMessage = "Connection closed by proxy server before the response completed",
                };
                _stream.Push(new AssistantMessageEvent.Error(StopReason.Error, _partial.ErrorMessage!, _partial));
            }
            _stream.End(_partial);
        }

        /// <summary>请求失败（含取消）。对应 TS 的外层 catch。</summary>
        public void FailWith(Exception error, CancellationToken signal)
        {
            var reason = signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
            var message = error.Message;
            _partial = _partial with { StopReason = reason, ErrorMessage = message };
            _stream.Push(new AssistantMessageEvent.Error(reason, message, _partial));
            _stream.End(_partial);
        }

        private AssistantMessageEvent? Apply(ProxyAssistantMessageEvent proxyEvent)
        {
            switch (proxyEvent)
            {
                case ProxyAssistantMessageEvent.Start:
                    return new AssistantMessageEvent.Start(_partial);

                case ProxyAssistantMessageEvent.TextStart textStart:
                    SetBlock(textStart.ContentIndex, new TextContent(""));
                    return new AssistantMessageEvent.TextStart(textStart.ContentIndex, _partial);

                case ProxyAssistantMessageEvent.TextDelta textDelta:
                    if (GetBlock(textDelta.ContentIndex) is TextContent text)
                    {
                        SetBlock(textDelta.ContentIndex, text with { Text = text.Text + textDelta.Delta });
                        return new AssistantMessageEvent.TextDelta(
                            textDelta.ContentIndex, textDelta.Delta, ++_sequence, _partial);
                    }
                    throw new InvalidOperationException("Received text_delta for non-text content");

                case ProxyAssistantMessageEvent.TextEnd textEnd:
                    if (GetBlock(textEnd.ContentIndex) is TextContent endText)
                    {
                        SetBlock(textEnd.ContentIndex, endText with { TextSignature = textEnd.ContentSignature });
                        return new AssistantMessageEvent.TextEnd(textEnd.ContentIndex, endText.Text, _partial);
                    }
                    throw new InvalidOperationException("Received text_end for non-text content");

                case ProxyAssistantMessageEvent.ThinkingStart thinkingStart:
                    SetBlock(thinkingStart.ContentIndex, new ThinkingContent(""));
                    return new AssistantMessageEvent.ThinkingStart(thinkingStart.ContentIndex, _partial);

                case ProxyAssistantMessageEvent.ThinkingDelta thinkingDelta:
                    if (GetBlock(thinkingDelta.ContentIndex) is ThinkingContent thinking)
                    {
                        SetBlock(thinkingDelta.ContentIndex, thinking with { Thinking = thinking.Thinking + thinkingDelta.Delta });
                        return new AssistantMessageEvent.ThinkingDelta(
                            thinkingDelta.ContentIndex, thinkingDelta.Delta, ++_sequence, Signature: null, _partial);
                    }
                    throw new InvalidOperationException("Received thinking_delta for non-thinking content");

                case ProxyAssistantMessageEvent.ThinkingEnd thinkingEnd:
                    if (GetBlock(thinkingEnd.ContentIndex) is ThinkingContent endThinking)
                    {
                        SetBlock(thinkingEnd.ContentIndex, endThinking with { Signature = thinkingEnd.ContentSignature });
                        return new AssistantMessageEvent.ThinkingEnd(thinkingEnd.ContentIndex, endThinking.Thinking, _partial);
                    }
                    throw new InvalidOperationException("Received thinking_end for non-thinking content");

                case ProxyAssistantMessageEvent.ToolCallStart toolCallStart:
                    SetBlock(toolCallStart.ContentIndex, new ToolCallContent(toolCallStart.Id, toolCallStart.ToolName, new JsonObject()));
                    _partialJson[toolCallStart.ContentIndex] = new StringBuilder();
                    return new AssistantMessageEvent.ToolCallStart(toolCallStart.ContentIndex, _partial);

                case ProxyAssistantMessageEvent.ToolCallDelta toolCallDelta:
                    if (GetBlock(toolCallDelta.ContentIndex) is ToolCallContent call)
                    {
                        // 对齐 TS：累积 partialJson 并流式重解析参数（arguments 随增量增长）。
                        var buffer = _partialJson.TryGetValue(toolCallDelta.ContentIndex, out var existing)
                            ? existing
                            : _partialJson[toolCallDelta.ContentIndex] = new StringBuilder();
                        buffer.Append(toolCallDelta.Delta);
                        SetBlock(toolCallDelta.ContentIndex, call with { Arguments = JsonParse.ParseStreamingJson(buffer.ToString()) });
                        return new AssistantMessageEvent.ToolCallDelta(
                            toolCallDelta.ContentIndex, toolCallDelta.ContentIndex, toolCallDelta.Delta, _partial);
                    }
                    throw new InvalidOperationException("Received toolcall_delta for non-toolCall content");

                case ProxyAssistantMessageEvent.ToolCallEnd toolCallEnd:
                    if (GetBlock(toolCallEnd.ContentIndex) is ToolCallContent)
                    {
                        // 对齐 TS 的 Object.assign(content, toolCall)：终态 toolCall 权威覆盖整个块。
                        _partialJson.Remove(toolCallEnd.ContentIndex);
                        SetBlock(toolCallEnd.ContentIndex, toolCallEnd.ToolCall);
                        return new AssistantMessageEvent.ToolCallEnd(toolCallEnd.ContentIndex, toolCallEnd.ToolCall, _partial);
                    }
                    return null;

                case ProxyAssistantMessageEvent.Done done:
                    _partial = _partial with
                    {
                        StopReason = done.Reason,
                        UsageStats = ConvertUsage(done.Usage),
                    };
                    if (done.ProviderThinkingLevel is not null)
                        _partial = _partial with { ProviderThinkingLevel = done.ProviderThinkingLevel };
                    return new AssistantMessageEvent.Done(done.Reason, _partial);

                case ProxyAssistantMessageEvent.Error error:
                    _partial = _partial with
                    {
                        StopReason = error.Reason,
                        ErrorMessage = error.ErrorMessage,
                        UsageStats = ConvertUsage(error.Usage),
                    };
                    if (error.ProviderThinkingLevel is not null)
                        _partial = _partial with { ProviderThinkingLevel = error.ProviderThinkingLevel };
                    return new AssistantMessageEvent.Error(error.Reason, error.ErrorMessage ?? "", _partial);

                default:
                    return null;
            }
        }

        private ContentBlock? GetBlock(int index)
            => index >= 0 && index < _content.Count ? _content[index] : null;

        private void SetBlock(int index, ContentBlock block)
        {
            // TS 直接按索引赋值（越界时 JS 数组留洞，后续访问抛错）；C# 只允许追加或原地替换。
            if (index == _content.Count) _content.Add(block);
            else if (index >= 0 && index < _content.Count) _content[index] = block;
            else throw new InvalidOperationException($"Proxy event contentIndex {index} out of range");
            _partial = _partial with { Content = _content.ToArray() };
        }

        /// <summary>
        /// wire 用量 JSON → <see cref="Usage"/>。对应 TS 侧直接透传 server 的 usage 对象：
        /// 逐字段照抄（含嵌套 cost 五桶与存储的 totalTokens），可选桶按 wire 是否存在决定
        /// 是 null 还是 0。
        /// </summary>
        private static Usage? ConvertUsage(JsonObject? usage)
        {
            if (usage is null) return null;
            var cost = usage["cost"] as JsonObject;
            return new Usage(
                ReadTokenCount(usage["input"]),
                ReadTokenCount(usage["output"]),
                ReadTokenCount(usage["cacheRead"]),
                ReadTokenCount(usage["cacheWrite"]))
            {
                CacheWrite1h = usage["cacheWrite1h"] is JsonValue ? ReadTokenCount(usage["cacheWrite1h"]) : null,
                Reasoning = usage["reasoning"] is JsonValue ? ReadTokenCount(usage["reasoning"]) : null,
                TotalTokens = ReadTokenCount(usage["totalTokens"]),
                Cost = cost is null
                    ? UsageCost.Zero
                    : new UsageCost(
                        ReadCost(cost["input"]),
                        ReadCost(cost["output"]),
                        ReadCost(cost["cacheRead"]),
                        ReadCost(cost["cacheWrite"]),
                        ReadCost(cost["total"])),
            };
        }

        private static double ReadCost(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

        private static long ReadTokenCount(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<double>(out var number)
                ? (long)number
                : 0;
    }
}

/// <summary>
/// proxy 事件 JSON 的宽容解析。对应 TS <c>JSON.parse(data) as ProxyAssistantMessageEvent</c>：
/// 类型不符按缺省值读取（TS 的 as 不做运行时检查），未知事件类型忽略。
/// </summary>
internal static class ProxyEventJson
{
    public static ProxyAssistantMessageEvent? Parse(string json)
        => Parse(JsonNode.Parse(json));

    public static ProxyAssistantMessageEvent? Parse(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        switch (ReadString(obj, "type"))
        {
            case "start":
                return new ProxyAssistantMessageEvent.Start();
            case "text_start":
                return new ProxyAssistantMessageEvent.TextStart(ReadIndex(obj));
            case "text_delta":
                return new ProxyAssistantMessageEvent.TextDelta(ReadIndex(obj), ReadString(obj, "delta"));
            case "text_end":
                return new ProxyAssistantMessageEvent.TextEnd(ReadIndex(obj), ReadOptionalString(obj, "contentSignature"));
            case "thinking_start":
                return new ProxyAssistantMessageEvent.ThinkingStart(ReadIndex(obj));
            case "thinking_delta":
                return new ProxyAssistantMessageEvent.ThinkingDelta(ReadIndex(obj), ReadString(obj, "delta"));
            case "thinking_end":
                return new ProxyAssistantMessageEvent.ThinkingEnd(ReadIndex(obj), ReadOptionalString(obj, "contentSignature"));
            case "toolcall_start":
                return new ProxyAssistantMessageEvent.ToolCallStart(
                    ReadIndex(obj), ReadString(obj, "id"), ReadString(obj, "toolName"));
            case "toolcall_delta":
                return new ProxyAssistantMessageEvent.ToolCallDelta(ReadIndex(obj), ReadString(obj, "delta"));
            case "toolcall_end":
                return new ProxyAssistantMessageEvent.ToolCallEnd(ReadIndex(obj), ReadToolCall(obj["toolCall"] as JsonObject));
            case "done":
                return new ProxyAssistantMessageEvent.Done(
                    ReadStopReason(obj), obj["usage"] as JsonObject, ReadOptionalString(obj, "providerThinkingLevel"));
            case "error":
                return new ProxyAssistantMessageEvent.Error(
                    ReadStopReason(obj), ReadOptionalString(obj, "errorMessage"),
                    obj["usage"] as JsonObject, ReadOptionalString(obj, "providerThinkingLevel"));
            default:
                // 对齐 TS switch 的 default 分支：未知类型不中断流。
                return null;
        }
    }

    private static ToolCallContent ReadToolCall(JsonObject? call)
    {
        var toolCall = new ToolCallContent(
            ReadString(call, "id"), ReadString(call, "name"), call?["arguments"]?.DeepClone());
        if (ReadOptionalString(call, "namespace") is { } ns) toolCall.Namespace = ns;
        if (ReadOptionalString(call, "thoughtSignature") is { } signature) toolCall.ThoughtSignature = signature;
        return toolCall;
    }

    private static int ReadIndex(JsonObject obj)
        => obj["contentIndex"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : 0;

    private static StopReason ReadStopReason(JsonObject obj)
        => Enum.TryParse(ReadString(obj, "reason"), ignoreCase: true, out StopReason reason)
            ? reason
            : StopReason.Error;

    private static string ReadString(JsonObject? obj, string name)
        => obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? ReadOptionalString(JsonObject? obj, string name)
        => obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
