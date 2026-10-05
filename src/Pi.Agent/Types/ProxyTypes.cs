using Pi.Ai.Types;

namespace Pi.Agent.Types;

/// <summary>
/// Proxy 流式事件的 wire 形状。对应 TS <c>ProxyAssistantMessageEvent</c>：
/// 服务器下发时剥掉了 partial 字段以节省带宽，客户端按事件重建部分消息。
/// JSON 解析见 <see cref="ProxyEventJson"/>（对齐 TS <c>JSON.parse(data) as ProxyAssistantMessageEvent</c>
/// 的宽松语义与未知类型的 warn 行为）。
/// </summary>
public abstract record ProxyAssistantMessageEvent
{
    private ProxyAssistantMessageEvent() { }

    public sealed record Start : ProxyAssistantMessageEvent;

    public sealed record TextStart(int ContentIndex) : ProxyAssistantMessageEvent;

    public sealed record TextDelta(int ContentIndex, string Delta) : ProxyAssistantMessageEvent;

    public sealed record TextEnd(int ContentIndex, string? ContentSignature = null) : ProxyAssistantMessageEvent;

    public sealed record ThinkingStart(int ContentIndex) : ProxyAssistantMessageEvent;

    public sealed record ThinkingDelta(int ContentIndex, string Delta) : ProxyAssistantMessageEvent;

    public sealed record ThinkingEnd(int ContentIndex, string? ContentSignature = null) : ProxyAssistantMessageEvent;

    public sealed record ToolCallStart(int ContentIndex, string Id, string ToolName) : ProxyAssistantMessageEvent;

    public sealed record ToolCallDelta(int ContentIndex, string Delta) : ProxyAssistantMessageEvent;

    public sealed record ToolCallEnd(int ContentIndex, ToolCallContent ToolCall) : ProxyAssistantMessageEvent;

    /// <summary>终态：正常结束。<c>Usage</c> 为 wire 形状的用量 JSON（含 cost 对象）。</summary>
    public sealed record Done(
        StopReason Reason,
        System.Text.Json.Nodes.JsonObject? Usage,
        string? ProviderThinkingLevel = null) : ProxyAssistantMessageEvent;

    /// <summary>终态：失败。<c>Usage</c> 为 wire 形状的用量 JSON（含 cost 对象）。</summary>
    public sealed record Error(
        StopReason Reason,
        string? ErrorMessage,
        System.Text.Json.Nodes.JsonObject? Usage,
        string? ProviderThinkingLevel = null) : ProxyAssistantMessageEvent;
}

/// <summary>
/// Proxy 请求的可序列化流式选项子集。对应 TS <c>ProxySerializableStreamOptions</c>
/// （<c>Pick&lt;SimpleStreamOptions, …&gt;</c>）：随请求体发往 proxy 服务器。
/// <see cref="ProxyStreamOptions"/> 在其上追加本地字段（认证 / 地址 / 中止）。
/// </summary>
public record ProxySerializableStreamOptions
{
    public double? Temperature { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    public int? MaxTokens { get; init; }

    public string? Reasoning { get; init; }

    public string? CacheRetention { get; init; }

    public string? SessionId { get; init; }

    public System.Collections.Generic.IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public System.Text.Json.Nodes.JsonObject? Metadata { get; init; }

    public string? Transport { get; init; }

    public ThinkingBudgets? ThinkingBudgets { get; init; }

    public int? MaxRetryDelayMs { get; init; }
}

/// <summary>
/// <see cref="Proxy.StreamProxy"/> 的选项。对应 TS <c>ProxyStreamOptions</c>：
/// 可序列化子集 + 本地中止令牌 + proxy 服务器连接信息。
/// </summary>
public sealed record ProxyStreamOptions : ProxySerializableStreamOptions
{
    /// <summary>本地中止信号。对应 TS <c>signal</c>。</summary>
    public System.Threading.CancellationToken Signal { get; init; }

    /// <summary>proxy 服务器的认证令牌。对应 TS <c>authToken</c>。</summary>
    public required string AuthToken { get; init; }

    /// <summary>proxy 服务器地址（如 "https://genai.example.com"）。对应 TS <c>proxyUrl</c>。</summary>
    public required string ProxyUrl { get; init; }
}
