using Pi.Ai.Models;
using Pi.Ai.Providers;

namespace Pi.Ai;

/// <summary>
/// 生成目录里图片模型部分的兼容读取。对应 TS <c>image-models.ts</c>：
/// 新代码应使用 <c>Models.GetModelOfType("image", ...)</c> 或
/// <c>All.GetBuiltinImageModel()</c>。
/// </summary>
public static class ImageModels
{
    /// <summary>@deprecated 用 <c>All.GetBuiltinImageModel</c> 或 <c>Models.GetModelOfType(ModelType.Image, ...)</c>。</summary>
    public static ModelSpec? GetImageModel(string provider, string modelId)
        => All.GetBuiltinImageModel(provider, modelId);

    /// <summary>@deprecated 用 <c>Models.GetProviders()</c>。</summary>
    public static IReadOnlyList<string> GetImageProviders()
        => All.GetBuiltinProviders()
            .Where(provider => All.GetBuiltinImageModels(provider).Count > 0)
            .ToList();

    /// <summary>@deprecated 用 <c>All.GetBuiltinImageModels</c> 或 <c>Models.GetModelsOfType(ModelType.Image)</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetImageModels(string provider)
        => All.GetBuiltinImageModels(provider);
}
