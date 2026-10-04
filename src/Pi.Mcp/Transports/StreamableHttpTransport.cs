using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>SSE 事件。对应 TS <c>SseEvent</c>。</summary>
public sealed record SseEvent(string Data, string? Event = null, string? Id = null);

/// <summary>SSE 消费选项。对应 TS <c>ConsumeSseOptions</c>。</summary>
public sealed record ConsumeSseOptions
{
    public int? MaxEventBytes { get; init; }
    public required Action<SseEvent> OnEvent { get; init; }
    /// <summary>每个 id 字段都会回调（包括无 data 的恢复预热事件）。</summary>
    public Action<string>? OnId { get; init; }
    /// <summary>每个合法 retry 字段回调（毫秒）。</summary>
    public Action<long>? OnRetry { get; init; }
}

/// <summary>
/// SSE 流解析器。对应 TS <c>consumeSseStream</c>：按行处理 field: value，
/// 空行分发事件；data 多行以 \n 连接；字节上限防无界增长；冒号开头的注释行跳过。
/// </summary>
public static class SseStream
{
    public const int DefaultMaxMessageBytes = TransportDefaults.MaxMessageBytes;

    public static async Task ConsumeAsync(Stream stream, ConsumeSseOptions options,
        CancellationToken cancellationToken = default)
    {
        var maxEventBytes = options.MaxEventBytes ?? DefaultMaxMessageBytes;
        var eventName = null as string;
        var eventId = null as string;
        var dataLines = new List<string>();
        var dataBytes = 0;

        void Dispatch()
        {
            if (dataLines.Count > 0)
            {
                options.OnEvent(new SseEvent(string.Join("\n", dataLines), eventName, eventId));
                dataLines = [];
                dataBytes = 0;
            }
            eventName = null;
            eventId = null;
        }

        void ProcessLine(string rawLine)
        {
            var line = rawLine.EndsWith("\r") ? rawLine[..^1] : rawLine;
            if (line.Length == 0) { Dispatch(); return; }
            if (line.StartsWith(':')) return; // 注释行
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            if (field == "data")
            {
                dataBytes += Encoding.UTF8.GetByteCount(value) + (dataLines.Count > 0 ? 1 : 0);
                if (dataBytes > maxEventBytes)
                    throw new InvalidOperationException($"MCP SSE event exceeds {maxEventBytes} bytes");
                dataLines.Add(value);
            }
            else if (field == "event") eventName = value;
            else if (field == "id" && !value.Contains('\0')) { eventId = value; options.OnId?.Invoke(value); }
            else if (field == "retry" && long.TryParse(value, out var delay)) options.OnRetry?.Invoke(delay);
        }

        var buffered = new StringBuilder();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffered.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var text = buffered.ToString();
            buffered.Clear();
            var newline = text.IndexOf('\n');
            var consumed = 0;
            while (newline >= 0)
            {
                ProcessLine(text[consumed..newline]);
                consumed = newline + 1;
                newline = text.IndexOf('\n', consumed);
            }
            buffered.Append(text[consumed..]);
            if (Encoding.UTF8.GetByteCount(buffered.ToString()) > maxEventBytes)
                throw new InvalidOperationException($"MCP SSE event exceeds {maxEventBytes} bytes");
        }
        if (buffered.Length > 0) ProcessLine(buffered.ToString());
        Dispatch();
    }
}

/// <summary>MCP HTTP 错误（携带状态码与响应体摘要）。对应 TS <c>McpHttpError</c>。</summary>
public class McpHttpError(long status, string message, string body = "") : Exception(message)
{
    public long Status { get; } = status;

    public string Body { get; } = body;
}

/// <summary>服务器要求认证（401，或 403 insufficient_scope 阶梯授权）。对应 TS <c>McpAuthRequiredError</c>。</summary>
public sealed class McpAuthRequiredError(string? wwwAuthenticate = null, string body = "")
    : McpHttpError(401, "MCP server requires authentication", body)
{
    public string? WwwAuthenticate { get; } = wwwAuthenticate;
}

/// <summary>会话过期（404 且已持会话 id）。对应 TS <c>McpSessionExpiredError</c>。</summary>
public sealed class McpSessionExpiredError(string body = "")
    : McpHttpError(404, "MCP session expired", body);

/// <summary>HTTP 传输选项。对应 TS <c>StreamableHttpTransportOptions</c>。</summary>
public sealed record StreamableHttpTransportOptions
{
    public required string Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>握手完成后是否打开服务器到客户端的 GET 流（默认 true）。</summary>
    public bool OpenGetStream { get; init; } = true;
    public int? MaxMessageBytes { get; init; }
}

/// <summary>
/// Streamable HTTP 传输。对应 TS <c>StreamableHttpTransport</c>：
/// POST 发送消息（响应可能是 application/json 或 text/event-stream）、
/// 初始化后打开 GET 监听流、捕获 Mcp-Session-Id、回设 MCP-Protocol-Version、
/// 401/404 → 专用错误、DELETE 关闭会话。
/// </summary>
public sealed class StreamableHttpTransport : TransportEvents
{
    private const int MaxErrorBodyBytes = 8 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly StreamableHttpTransportOptions _options;
    private bool _started;
    private bool _closed;
    private string? _sessionId;
    private string? _protocolVersion;

    /// <param name="options">端点 URL 与可选头。</param>
    /// <param name="httpClient">外部 HttpClient（缺省内部创建）。</param>
    public StreamableHttpTransport(StreamableHttpTransportOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        if (httpClient is null) { _http = new HttpClient(); _ownsHttpClient = true; }
        else _http = httpClient;
    }

    /// <summary>当前会话 id（服务器在 initialize 响应头里下发）。</summary>
    public string? SessionId => _sessionId;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("MCP Streamable HTTP transport already started");
        if (_closed) throw new McpConnectionClosedError();
        _started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetProtocolVersion(string version) => _protocolVersion = version;

    /// <inheritdoc />
    public override async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!_started || _closed) throw new McpConnectionClosedError();

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonRpc.Serialize(message).ToJsonString(),
            Encoding.UTF8, "application/json");
        ApplyHeaders(request, includeSession: true);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await CheckResponseAsync(response).ConfigureAwait(false);
        CaptureSession(response);

        if (message is not JsonRpcMessage.Request)
        {
            // 通知/响应以 202 确认，无应答体；notifications/initialized 之后才允许开 GET 流。
            if (message is JsonRpcMessage.Notification { Method: "notifications/initialized" })
                StartGetStream();
            return;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType == "application/json")
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(body);
            foreach (var item in node is JsonArray array ? array : [node])
                EmitMessage(JsonRpc.Parse(item));
            return;
        }
        if (contentType == "text/event-stream")
        {
            // 请求的应答走 SSE：解析流并把其中的消息分发出去。
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await SseStream.ConsumeAsync(stream, new ConsumeSseOptions
            {
                MaxEventBytes = _options.MaxMessageBytes,
                OnEvent = @event =>
                {
                    if (@event.Data.Length == 0) return;
                    EmitMessage(JsonRpc.ParseText(@event.Data));
                },
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new McpHttpError((long)response.StatusCode, $"Unsupported MCP response content type: {contentType ?? "missing"}");
    }

    /// <inheritdoc />
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        _closed = true;
        _closedCts.Cancel();
        // DELETE 会话（服务器端过期清理）；失败不影响本地关闭。
        if (_started && _sessionId is not null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, _options.Url);
                ApplyHeaders(request, includeSession: true);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));
                await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 认证头解析失败也会落到这里；会话会在服务器侧自然过期。
            }
        }
        EmitClose();
        if (_ownsHttpClient) _http.Dispose();
    }

    // ---------- 内部 ----------

    /// <summary>初始化完成后打开服务器到客户端的 GET 监听流（后台常驻）。</summary>
    private void StartGetStream()
    {
        if (_options.OpenGetStream is false || _closed) return;
        _ = Task.Run(async () =>
        {
            while (!_closed)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    ApplyHeaders(request, includeSession: true);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        _closedCts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // 405 表示服务器不支持 GET 流：合法，静默放弃。
                        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed) return;
                        throw new McpHttpError((long)response.StatusCode,
                            $"MCP GET stream failed with status {(int)response.StatusCode}");
                    }
                    var stream = await response.Content.ReadAsStreamAsync(_closedCts.Token).ConfigureAwait(false);
                    await SseStream.ConsumeAsync(stream, new ConsumeSseOptions
                    {
                        MaxEventBytes = _options.MaxMessageBytes,
                        OnEvent = @event =>
                        {
                            if (@event.Data.Length == 0) return;
                            EmitMessage(JsonRpc.ParseText(@event.Data));
                        },
                    }, _closedCts.Token).ConfigureAwait(false);
                    return; // 正常 EOF：不重连（服务端主动关闭监听流是合法行为）。
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error)
                {
                    EmitError(error);
                    return;
                }
            }
        });
    }

    private void ApplyHeaders(HttpRequestMessage request, bool includeSession)
    {
        if (_options.Headers is not null)
            foreach (var (name, value) in _options.Headers)
                request.Headers.TryAddWithoutValidation(name, value);
        if (includeSession && _sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_protocolVersion is not null)
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var sessionId = values.FirstOrDefault();
            if (sessionId is not null) _sessionId = sessionId;
        }
    }

    private async Task CheckResponseAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = string.Empty;
        try { body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false))[..Math.Min(MaxErrorBodyBytes, (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Length)]; }
        catch { /* 体不可读时保留空摘要 */ }
        if ((int)response.StatusCode == 401)
        {
            response.Headers.TryGetValues("WWW-Authenticate", out var www);
            throw new McpAuthRequiredError(www?.FirstOrDefault(), body);
        }
        if ((int)response.StatusCode == 404 && _sessionId is not null)
            throw new McpSessionExpiredError(body);
        throw new McpHttpError((long)response.StatusCode,
            $"MCP HTTP request failed with status {(int)response.StatusCode}", body);
    }
}
