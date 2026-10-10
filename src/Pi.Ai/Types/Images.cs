using Pi.Ai.Models;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>图片生成的停止原因。对应 TS <c>ImagesStopReason</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImagesStopReason>))]
public enum ImagesStopReason
{
    [JsonStringEnumMemberName("stop")]
    Stop,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("aborted")]
    Aborted,
}

/// <summary>图片生成的输入/输出内容块集合：文本与图片。对应 TS <c>ImagesInputContent</c>/<c>ImagesOutputContent</c>。</summary>

/// <summary>图片生成上下文。对应 TS <c>ImagesContext</c>。</summary>
public sealed record ImagesContext
{
    public required IReadOnlyList<ContentBlock> Input { get; init; }
}

/// <summary>图片生成结果。对应 TS <c>AssistantImages</c>。</summary>
public sealed record AssistantImages
{
    public required string Api { get; init; }

    public required string Provider { get; init; }

    public required string Model { get; init; }

    public IReadOnlyList<ContentBlock> Output { get; init; } = [];

    public string? ResponseId { get; init; }

    public Usage? Usage { get; init; }

    public ImagesStopReason StopReason { get; init; } = ImagesStopReason.Stop;

    public string? ErrorMessage { get; init; }

    /// <summary>Unix 毫秒时间戳。</summary>
    public long Timestamp { get; init; } = DateTimeOffset.Now.ToUnixTimeMilliseconds();
}

/// <summary>图片生成请求选项。对应 TS <c>ImagesOptions</c>（继承 ProviderRequestOptions 的公共字段）。</summary>
public record ImagesOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    /// <summary>自定义 HTTP 头；调用方值覆盖默认值，null 值抑制同名默认头。</summary>
    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>HTTP 请求超时（毫秒）。</summary>
    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    /// <summary>包含进 API 请求的元数据；provider 各取所需，忽略其余。对应 TS <c>metadata</c>。</summary>
    public JsonObject? Metadata { get; init; }

    /// <summary>作用域环境变量覆盖。对应 TS <c>env</c>。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>发送前检查/替换请求载荷；返回 null 表示不变。对应 TS <c>onPayload</c>。</summary>
    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    /// <summary>收到 HTTP 响应后的回调。对应 TS <c>onResponse</c>。</summary>
    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }
}

/// <summary>开放选项（TS <c>ProviderImagesOptions = ImagesOptions &amp; Record&lt;string, unknown&gt;</c>；C# 以继承承载扩展）。</summary>
public record ProviderImagesOptions : ImagesOptions;

/// <summary>收到 HTTP 响应后的观察快照。对应 TS <c>ProviderResponse</c>。</summary>
public sealed record ProviderResponse
{
    public required int Status { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}

/// <summary>图片生成 API 实现的函数形状。对应 TS <c>ImagesFunction</c>。</summary>
public delegate Task<AssistantImages> ImagesFunction(
    ModelSpec model, ImagesContext context, ImagesOptions? options);
