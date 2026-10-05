using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// 延后响应句柄：provider 返回 <c>deferred</c> 停止原因时携带的续取令牌。
/// 对应 TS <c>DeferredHandle</c>（types.ts）。
/// </summary>
public sealed record DeferredHandle
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Api { get; init; }

    /// <summary>provider 令牌（响应 id 或批次 id + 行 id）。</summary>
    public required string Id { get; init; }

    /// <summary>句柄失效时刻（epoch 毫秒）。</summary>
    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; init; }

    /// <summary>建议的下一次轮询间隔（毫秒）。</summary>
    [JsonPropertyName("pollAfterMs")]
    public long? PollAfterMs { get; init; }

    /// <summary>重建最终助手消息所需的 provider 转换数据。</summary>
    public JsonNode? Data { get; init; }
}
