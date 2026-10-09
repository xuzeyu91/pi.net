namespace Pi.Ai.Types;

/// <summary>
/// 模型专属的图像缩放配置。对应 TS <c>ModelImageResizeOptions</c>（types.ts）。
/// 与 coding-agent 通用 <c>ImageResizeOptions</c> 字段相同但语义独立：这是模型目录下发的
/// 「该模型的安全档位」，通用层可另行覆盖。
/// </summary>
public sealed record ModelImageResizeOptions
{
    public int? MaxWidth { get; init; }

    public int? MaxHeight { get; init; }

    /// <summary>base64 编码后的载荷上限（字节）。</summary>
    public long? MaxBytes { get; init; }

    public int? JpegQuality { get; init; }
}

/// <summary>模型图像输入限制。对应 TS <c>ModelImageInputLimits</c>。</summary>
public sealed record ModelImageInputLimits
{
    /// <summary>新图像进入会话历史前应用的缓存安全缩放配置。</summary>
    public ModelImageResizeOptions? Resize { get; init; }

    /// <summary>单条 provider 消息接受的最大图像数。</summary>
    public int? MaxPerMessage { get; init; }

    /// <summary>单次 provider 请求接受的最大图像数。</summary>
    public int? MaxPerRequest { get; init; }
}

/// <summary>模型输入限制。对应 TS <c>ModelInputLimits</c>。</summary>
public sealed record ModelInputLimits
{
    /// <summary>序列化后 provider 请求的大小上限（字节）。</summary>
    public long? MaxRequestBytes { get; init; }

    public ModelImageInputLimits? Images { get; init; }
}
