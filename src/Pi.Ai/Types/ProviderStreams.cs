using Pi.Ai.Models;

namespace Pi.Ai.Types;

/// <summary>
/// API 实现模块的统一流契约：每个 API 实现导出 <c>stream</c>/<c>streamSimple</c>，
/// 有能力的实现还导出延后响应方法。对应 TS <c>ProviderStreams</c>（types.ts）。
/// <para>TS 的 <c>options</c> 是各 API 专属选项类型；C# 的 provider 边界统一用
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> 承载（见 <c>Api.ProviderStreamOptions</c>）。</para>
/// </summary>
public sealed record ProviderStreams
{
    public required Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>
        Stream { get; init; }

    public required Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>
        StreamSimple { get; init; }

    public Func<ModelSpec, DeferredHandle, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>?
        FetchDeferred { get; init; }

    public Func<ModelSpec, DeferredHandle, IReadOnlyDictionary<string, object?>?, CancellationToken, Task>?
        CancelDeferred { get; init; }
}

/// <summary>
/// 图片生成 API 实现模块的契约。对应 TS <c>ProviderImages</c>（types.ts）。
/// </summary>
public sealed record ProviderImages
{
    public required Func<ModelSpec, ImagesContext, ImagesOptions?, CancellationToken, Task<AssistantImages>>
        GenerateImages { get; init; }
}

/// <summary>
/// 结构化分类 API 实现模块的契约。对应 TS <c>ProviderClassifier</c>（types.ts）。
/// </summary>
public sealed record ProviderClassifier
{
    public required Func<ModelSpec, ClassifierContext, ClassifierOptions?, CancellationToken, Task<ClassifierResult>>
        Classify { get; init; }
}
