using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

public static partial class OpenAiCodexResponses
{
    private const int WebsocketMessageTooBigCloseCode = 1009;
    private const int SessionWebsocketCacheTtlMs = 5 * 60 * 1000;
    private const int SessionWebsocketMaxAgeMs = 55 * 60 * 1000;

    /// <summary>WS 会话缓存条目。对应 TS <c>CachedWebSocketConnection</c>。</summary>
    private sealed class CachedWebSocketConnection : IDisposable
    {
        public required ClientWebSocket Socket;
        public bool Busy = true;
        public long CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        public System.Threading.Timer? IdleTimer;
        public WebSocketContinuation? Continuation;

        public void Dispose()
        {
            IdleTimer?.Dispose();
            Socket.Dispose();
        }
    }

    /// <summary>WS continuation 状态（上次请求体/响应 id/响应 item）。对应 TS <c>CachedWebSocketContinuationState</c>。</summary>
    private sealed class WebSocketContinuation
    {
        public required JsonObject LastRequestBody;
        public required string LastResponseId;
        public required JsonArray LastResponseItems;
    }

    /// <summary>WS 调试统计。对应 TS <c>OpenAICodexWebSocketDebugStats</c>。</summary>
    public sealed class WebSocketDebugStats
    {
        public int Requests;
        public int ConnectionsCreated;
        public int ConnectionsReused;
        public int CachedContextRequests;
        public int StoreTrueRequests;
        public int FullContextRequests;
        public int DeltaRequests;
        public int LastInputItems;
        public int? LastDeltaInputItems;
        public string? LastPreviousResponseId;
        public int WebsocketFailures;
        public int SseFallbacks;
        public bool? WebsocketFallbackActive;
        public string? LastWebSocketError;

        public WebSocketDebugStats Clone() => (WebSocketDebugStats)MemberwiseClone();
    }

    private static readonly object WsGate = new();
    private static readonly Dictionary<string, Dictionary<string, CachedWebSocketConnection>> WebsocketSessionCache = new();
    private static readonly Dictionary<string, WebSocketDebugStats> WebsocketDebugStats = new();
    private static readonly HashSet<string> WebsocketSseFallbackSessions = new(StringComparer.Ordinal);

    static OpenAiCodexResponses()
    {
        // 进程退出时关闭全部 WS 会话。对应 TS registerSessionResourceCleanup(closeOpenAICodexWebSocketSessions)。
        SessionResources.Register(sessionId => CloseWebSocketSessions(sessionId));
    }

    /// <summary>取（或建）会话调试统计。对应 TS <c>getOrCreateWebSocketDebugStats</c>。</summary>
    private static WebSocketDebugStats GetOrCreateStats(string sessionId)
    {
        lock (WsGate)
        {
            if (!WebsocketDebugStats.TryGetValue(sessionId, out var stats))
            {
                stats = new WebSocketDebugStats();
                WebsocketDebugStats[sessionId] = stats;
            }
            return stats;
        }
    }

    /// <summary>读取会话调试统计（副本）。对应 TS <c>getOpenAICodexWebSocketDebugStats</c>。</summary>
    public static WebSocketDebugStats? GetWebSocketDebugStats(string sessionId)
    {
        lock (WsGate)
        {
            return WebsocketDebugStats.TryGetValue(sessionId, out var stats) ? stats.Clone() : null;
        }
    }

    /// <summary>重置调试统计与会话回退标记。对应 TS <c>resetOpenAICodexWebSocketDebugStats</c>。</summary>
    public static void ResetWebSocketDebugStats(string? sessionId = null)
    {
        lock (WsGate)
        {
            if (sessionId is not null)
            {
                WebsocketDebugStats.Remove(sessionId);
                WebsocketSseFallbackSessions.Remove(sessionId);
                return;
            }
            WebsocketDebugStats.Clear();
            WebsocketSseFallbackSessions.Clear();
        }
    }

    /// <summary>关闭会话 WS 连接。对应 TS <c>closeOpenAICodexWebSocketSessions</c>。</summary>
    public static void CloseWebSocketSessions(string? sessionId = null)
    {
        List<CachedWebSocketConnection> entries;
        lock (WsGate)
        {
            entries = [];
            if (sessionId is not null)
            {
                if (WebsocketSessionCache.Remove(sessionId, out var accountEntries))
                {
                    entries.AddRange(accountEntries.Values);
                }
            }
            else
            {
                foreach (var accountEntries in WebsocketSessionCache.Values) entries.AddRange(accountEntries.Values);
                WebsocketSessionCache.Clear();
            }
        }
        foreach (var entry in entries) CloseSilently(entry);
    }

    private static void CloseSilently(CachedWebSocketConnection entry)
    {
        try
        {
            entry.IdleTimer?.Dispose();
            entry.Socket.Abort();
        }
        catch
        {
            // 已关闭
        }
        entry.Dispose();
    }

    private static bool IsSseFallbackActive(string? sessionId)
        => sessionId is not null && WebsocketSseFallbackSessions.Contains(sessionId);

    private static void RecordSseFallback(string? sessionId)
    {
        if (sessionId is null) return;
        var stats = GetOrCreateStats(sessionId);
        stats.SseFallbacks++;
        lock (WsGate) stats.WebsocketFallbackActive = WebsocketSseFallbackSessions.Contains(sessionId);
    }

    private static void RecordWebSocketFailure(string? sessionId, Exception error)
    {
        if (sessionId is null) return;
        lock (WsGate) WebsocketSseFallbackSessions.Add(sessionId);
        var stats = GetOrCreateStats(sessionId);
        stats.WebsocketFailures++;
        stats.LastWebSocketError = Diagnostics.FormatThrownValue(error);
        stats.WebsocketFallbackActive = true;
    }

    // ============================================================================
    // WebSocket 连接与消息泵
    // ============================================================================

    /// <summary>建立 WS 连接（自定义头 + 连接超时 + 取消）。对应 TS <c>connectWebSocket</c>。</summary>
    private static async Task<ClientWebSocket> ConnectWebSocketAsync(
        string url,
        IReadOnlyDictionary<string, string?> headers,
        CancellationToken signal,
        int connectTimeoutMs)
    {
        var socket = new ClientWebSocket();
        foreach (var (key, value) in headers)
        {
            if (value is not null && key != "Authorization") socket.Options.SetRequestHeader(key, value);
        }
        // Authorization 头单独设置（需要合法字符校验豁免时仍走 TryAdd）。
        if (headers.TryGetValue("Authorization", out var auth) && auth is not null)
        {
            socket.Options.SetRequestHeader("Authorization", auth);
        }
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(signal);
        if (connectTimeoutMs > 0) timeoutCts.CancelAfter(connectTimeoutMs);
        try
        {
            await socket.ConnectAsync(new Uri(url), timeoutCts.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!signal.IsCancellationRequested)
        {
            socket.Dispose();
            throw new InvalidOperationException($"WebSocket connect timeout after {connectTimeoutMs}ms");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>WS 会话条目获取或新建。对应 TS <c>acquireWebSocket</c>（简化：重用仅要求非 busy/未过期）。</summary>
    private sealed class WsLease
    {
        public required ClientWebSocket Socket;
        public CachedWebSocketConnection? Entry;
        public bool Reused;
        public Action<bool>? Release;
    }

    private static async Task<WsLease> AcquireWebSocketAsync(
        string url,
        IReadOnlyDictionary<string, string?> headers,
        string? sessionId,
        string accountId,
        CancellationToken signal,
        int connectTimeoutMs)
    {
        if (sessionId is null)
        {
            var socket = await ConnectWebSocketAsync(url, headers, signal, connectTimeoutMs).ConfigureAwait(false);
            return new WsLease
            {
                Socket = socket,
                Reused = false,
                Release = _ => { try { socket.Dispose(); } catch { /* 已关闭 */ } },
            };
        }

        Dictionary<string, CachedWebSocketConnection> accountEntries;
        lock (WsGate)
        {
            if (!WebsocketSessionCache.TryGetValue(sessionId, out accountEntries!))
            {
                accountEntries = new Dictionary<string, CachedWebSocketConnection>(StringComparer.Ordinal);
                WebsocketSessionCache[sessionId] = accountEntries;
            }
        }

        lock (WsGate)
        {
            if (accountEntries.TryGetValue(accountId, out var cached))
            {
                cached.IdleTimer?.Dispose();
                cached.IdleTimer = null;
                var expired = !cached.Busy
                    && DateTimeOffset.Now.ToUnixTimeMilliseconds() - cached.CreatedAt >= SessionWebsocketMaxAgeMs;
                if (expired)
                {
                    accountEntries.Remove(accountId);
                    CloseSilently(cached);
                }
                else if (!cached.Busy && cached.Socket.State == WebSocketState.Open)
                {
                    cached.Busy = true;
                    return new WsLease
                    {
                        Socket = cached.Socket,
                        Entry = cached,
                        Reused = true,
                        Release = keep => ReleaseEntry(sessionId, accountId, cached, keep),
                    };
                }
            }
        }

        var fresh = await ConnectWebSocketAsync(url, headers, signal, connectTimeoutMs).ConfigureAwait(false);
        var entry = new CachedWebSocketConnection { Socket = fresh, Busy = true };
        lock (WsGate)
        {
            accountEntries[accountId] = entry;
        }
        return new WsLease
        {
            Socket = fresh,
            Entry = entry,
            Reused = false,
            Release = keep => ReleaseEntry(sessionId, accountId, entry, keep),
        };
    }

    /// <summary>释放会话条目（keep 时调度空闲过期）。对应 TS release 闭包。</summary>
    private static bool ReleaseEntry(string sessionId, string accountId, CachedWebSocketConnection entry, bool keep)
    {
        lock (WsGate)
        {
            var reusable = entry.Socket.State == WebSocketState.Open;
            if (!keep || !reusable)
            {
                if (WebsocketSessionCache.TryGetValue(sessionId, out var accountEntries)
                    && accountEntries.TryGetValue(accountId, out var current) && ReferenceEquals(current, entry))
                {
                    accountEntries.Remove(accountId);
                    if (accountEntries.Count == 0) WebsocketSessionCache.Remove(sessionId);
                }
                CloseSilently(entry);
                return false;
            }
            entry.Busy = false;
            entry.IdleTimer = new System.Threading.Timer(_ =>
            {
                if (entry.Busy) return;
                lock (WsGate)
                {
                    if (WebsocketSessionCache.TryGetValue(sessionId, out var accountEntries)
                        && accountEntries.TryGetValue(accountId, out var current) && ReferenceEquals(current, entry))
                    {
                        accountEntries.Remove(accountId);
                        if (accountEntries.Count == 0) WebsocketSessionCache.Remove(sessionId);
                    }
                }
                CloseSilently(entry);
            }, null, TimeSpan.FromMilliseconds(SessionWebsocketCacheTtlMs), Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    /// <summary>WS 消息流：连接 → 发请求帧 → 逐条 yield JSON（终止事件后收尾）。对应 TS <c>parseWebSocket</c>。</summary>
    private static async IAsyncEnumerable<JsonObject> ParseWebSocketAsync(
        ClientWebSocket socket,
        string requestFrame,
        CancellationToken signal,
        int? idleTimeoutMs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var frameBytes = Encoding.UTF8.GetBytes(requestFrame);
        await socket.SendAsync(frameBytes, WebSocketMessageType.Text, true, signal).ConfigureAwait(false);

        var sawCompletion = false;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            signal.ThrowIfCancellationRequested();
            var message = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (idleTimeoutMs is { } idle && idle > 0) idleCts.CancelAfter(idle);
                do
                {
                    result = await socket.ReceiveAsync(buffer, idleCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        var closeCode = result.CloseStatus is { } status ? (int)status : (int?)null;
                        var closeReason = result.CloseStatusDescription;
                        var reasonText = string.IsNullOrEmpty(closeReason)
                            && closeCode == WebsocketMessageTooBigCloseCode ? " message too big"
                            : string.IsNullOrEmpty(closeReason) ? "" : $" {closeReason}";
                        var codeText = closeCode is { } code ? $" {code}" : "";
                        if (sawCompletion) yield break;
                        throw new CodexProtocolError(
                            $"WebSocket closed{codeText}{reasonText}".Trim());
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"WebSocket idle timeout after {idleTimeoutMs}ms");
            }
            catch (OperationCanceledException) when (signal.IsCancellationRequested)
            {
                throw new InvalidOperationException("Request was aborted");
            }

            var text = Encoding.UTF8.GetString(message.ToArray());
            JsonObject parsed;
            try
            {
                parsed = JsonNode.Parse(text) as JsonObject
                    ?? throw new JsonException("not an object");
            }
            catch (JsonException cause)
            {
                throw new CodexProtocolError($"Invalid Codex WebSocket JSON: {Diagnostics.FormatThrownValue(cause)}", text);
            }
            var type = parsed.Str("type") ?? "";
            if (type is "response.completed" or "response.done" or "response.incomplete")
            {
                sawCompletion = true;
            }
            yield return parsed;
            if (sawCompletion) yield break;
        }
    }

    // ============================================================================
    // WS continuation 增量请求
    // ============================================================================

    private static JsonObject RequestBodyWithoutInput(JsonObject body)
    {
        var rest = (JsonObject)body.DeepClone();
        rest.Remove("input");
        rest.Remove("previous_response_id");
        return rest;
    }

    /// <summary>除 input/previous_response_id 外请求体是否一致（JSON 字符串比较）。对应 TS <c>requestBodiesMatchExceptInput</c>。</summary>
    private static bool RequestBodiesMatchExceptInput(JsonObject a, JsonObject b)
        => RequestBodyWithoutInput(a).ToJsonString() == RequestBodyWithoutInput(b).ToJsonString();

    /// <summary>当前输入是否为「上次请求 + 上次响应 item」的前缀延伸，返回增量。对应 TS <c>getCachedWebSocketInputDelta</c>。</summary>
    private static JsonArray? GetCachedWebSocketInputDelta(JsonObject body, WebSocketContinuation continuation)
    {
        if (!RequestBodiesMatchExceptInput(body, continuation.LastRequestBody)) return null;

        var currentInput = body["input"] as JsonArray ?? [];
        var baseline = new JsonArray();
        foreach (var item in continuation.LastRequestBody["input"] as JsonArray ?? [])
        {
            baseline.Add(item?.DeepClone());
        }
        foreach (var item in continuation.LastResponseItems)
        {
            baseline.Add(item?.DeepClone());
        }
        if (currentInput.Count < baseline.Count) return null;

        for (var index = 0; index < baseline.Count; index++)
        {
            if (currentInput[index]?.ToJsonString() != baseline[index]?.ToJsonString()) return null;
        }
        var delta = new JsonArray();
        for (var index = baseline.Count; index < currentInput.Count; index++)
        {
            delta.Add(currentInput[index]?.DeepClone());
        }
        return delta;
    }

    /// <summary>可缓存会话时构造 continuation 请求体。对应 TS <c>buildCachedWebSocketRequestBody</c>。</summary>
    private static JsonObject BuildCachedWebSocketRequestBody(CachedWebSocketConnection entry, JsonObject body)
    {
        var continuation = entry.Continuation;
        if (continuation is null) return body;

        var delta = GetCachedWebSocketInputDelta(body, continuation);
        if (delta is null || continuation.LastResponseId.Length == 0)
        {
            entry.Continuation = null;
            return body;
        }

        var clone = (JsonObject)body.DeepClone();
        clone["previous_response_id"] = continuation.LastResponseId;
        clone["input"] = delta;
        return clone;
    }

    // ============================================================================
    // WebSocket 流处理
    // ============================================================================

    /// <summary>WS 流处理（连接/发送/消费/continuation 保存）。对应 TS <c>processWebSocketStream</c>。</summary>
    private static async Task ProcessWebSocketStreamAsync(
        string url,
        JsonObject body,
        IReadOnlyDictionary<string, string?> websocketHeaders,
        OpenAiResponsesShared.MutableAssistantMessage output,
        AssistantMessageEventStream stream,
        ModelSpec model,
        Action onStart,
        int? idleTimeoutMs,
        int websocketConnectTimeoutMs,
        string? cacheSessionId,
        string accountId,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties,
        OpenAiCodexResponsesOptions? options,
        CancellationToken cancellationToken)
    {
        var signal = options?.Signal ?? default;
        var lease = await AcquireWebSocketAsync(
            url, websocketHeaders, cacheSessionId, accountId, signal, websocketConnectTimeoutMs).ConfigureAwait(false);
        var keepConnection = true;
        var useCachedContext = options?.Transport is null or "auto" or "websocket-cached";
        var requestBody = useCachedContext && lease.Entry is not null
            ? BuildCachedWebSocketRequestBody(lease.Entry, body)
            : body;
        var stats = cacheSessionId is not null ? GetOrCreateStats(cacheSessionId) : null;
        if (stats is not null)
        {
            stats.Requests++;
            if (lease.Reused) stats.ConnectionsReused++; else stats.ConnectionsCreated++;
            if (useCachedContext) stats.CachedContextRequests++;
            if (requestBody["store"] is JsonValue { } storeValue && storeValue.TryGetValue<bool>(out var store) && store)
            {
                stats.StoreTrueRequests++;
            }
            stats.LastInputItems = (requestBody["input"] as JsonArray)?.Count ?? 0;
            if (requestBody.Str("previous_response_id") is { } previousId)
            {
                stats.DeltaRequests++;
                stats.LastDeltaInputItems = (requestBody["input"] as JsonArray)?.Count ?? 0;
                stats.LastPreviousResponseId = previousId;
            }
            else
            {
                stats.FullContextRequests++;
                stats.LastDeltaInputItems = null;
                stats.LastPreviousResponseId = null;
            }
        }
        try
        {
            var frame = new JsonObject { ["type"] = "response.create" };
            foreach (var (key, value) in requestBody) frame[key] = value?.DeepClone();
            var events = ParseWebSocketAsync(lease.Socket, frame.ToJsonString(), signal, idleTimeoutMs, cancellationToken);

            var started = false;
            var mapped = MapCodexEvents(events, output, model, options?.OnProviderStreamEvent, cancellationToken);
            await OpenAiResponsesShared.ProcessResponsesStream(
                AsSseEvents(EnumerateWithStart(mapped, () =>
                {
                    if (started) return;
                    started = true;
                    onStart();
                })), output, stream, model,
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
                keepConnection = false;
            }
            else if (useCachedContext && lease.Entry is not null && output.ResponseId is { } responseId)
            {
                // 保存 continuation：上次请求体 + 响应 id + 本次输出（去掉工具结果 item）。
                var responseItems = OpenAiResponsesShared.ConvertResponsesMessages(
                    model,
                    Transcript.NormalizeContext(null, null, [output.Snapshot()]),
                    CodexToolCallProviders,
                    new ConvertResponsesMessagesOptions
                    {
                        IncludeSystemPrompt = false,
                        GrammarToolInputProperties = grammarToolInputProperties,
                    });
                var filtered = new JsonArray();
                foreach (var item in responseItems)
                {
                    if (item is not JsonObject itemObject) continue;
                    if (itemObject.Str("type") is { } itemType && itemType is "function_call_output" or "custom_tool_call_output")
                    {
                        continue;
                    }
                    filtered.Add(itemObject.DeepClone());
                }
                lease.Entry.Continuation = new WebSocketContinuation
                {
                    LastRequestBody = (JsonObject)body.DeepClone(),
                    LastResponseId = responseId,
                    LastResponseItems = filtered,
                };
            }
        }
        catch
        {
            if (lease.Entry is not null) lease.Entry.Continuation = null;
            keepConnection = false;
            throw;
        }
        finally
        {
            lease.Release?.Invoke(keepConnection);
        }
    }

    /// <summary>JsonObject 事件 → AiSseEvent 流适配（ProcessResponsesStream 的入参契约）。</summary>
    private static async IAsyncEnumerable<Pi.Ai.Utils.AiSseEvent> AsSseEvents(
        IAsyncEnumerable<JsonObject> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var @event in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new Pi.Ai.Utils.AiSseEvent(@event.ToJsonString());
        }
    }

    private static async IAsyncEnumerable<JsonObject> EnumerateWithStart(
        IAsyncEnumerable<JsonObject> events,
        Action onStart,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var started = false;
        await foreach (var @event in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!started)
            {
                started = true;
                onStart();
            }
            yield return @event;
        }
    }
}
