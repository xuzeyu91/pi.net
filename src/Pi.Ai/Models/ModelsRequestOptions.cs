using Pi.Ai.Types;

namespace Pi.Ai.Models;

/// <summary>
/// <see cref="Models"/> 门面接受的图片选项：<see cref="ImagesOptions"/> + <c>transformHeaders</c>。
/// 对应 TS <c>ModelsImagesOptions = ImagesOptions &amp; ModelsRequestTransforms</c>。
/// </summary>
/// <remarks>
/// provider 边界仍然是 <see cref="ImagesOptions"/>——变换在分派前已被摘除并执行，与 TS 的
/// <c>const { transformHeaders, ...rawProviderOptions } = options ?? {}</c> 一致。变换委托沿用
/// <c>Models</c> 聊天路径的既有约定（选项字典的 <c>"transformHeaders"</c> 键，差异 C45）：
/// 统一以 <see cref="Task{TResult}"/> 承载，而非 TS 允许的「同步或异步」。
/// </remarks>
public sealed record ModelsImagesOptions : ImagesOptions
{
    /// <summary>请求头变换；缺省表示不变换。</summary>
    public Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>? TransformHeaders
    {
        get;
        init;
    }
}

/// <summary>
/// <see cref="Models"/> 门面接受的分类选项：<see cref="ClassifierOptions"/> + <c>transformHeaders</c>。
/// 对应 TS <c>ModelsClassifierOptions = ClassifierOptions &amp; ModelsRequestTransforms</c>。
/// </summary>
public sealed record ModelsClassifierOptions : ClassifierOptions
{
    /// <summary>请求头变换；缺省表示不变换。</summary>
    public Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>? TransformHeaders
    {
        get;
        init;
    }
}
