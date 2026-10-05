using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Images;

/// <summary>
/// 全局图片生成门面：按 <c>model.api</c> 经注册表分发。对应 TS <c>images.ts</c> 的
/// <c>generateImages()</c>。认证须经 <c>options.ApiKey</c> 显式传入；推荐
/// <c>Models.GenerateImagesAsync</c>（会解析 provider 认证）。
/// </summary>
public static class ImagesApi
{
    /// <summary>模块加载时注册内置图片 API（对齐 TS images.ts 的 import register-builtins）。</summary>
    static ImagesApi() => Providers.Images.RegisterBuiltins.RegisterAll();

    private static IImagesApiProvider ResolveImagesApiProvider(string api)
    {
        var provider = ImagesApiRegistry.Get(api);
        if (provider is null)
        {
            throw new InvalidOperationException($"No API provider registered for api: {api}");
        }
        return provider;
    }

    public static Task<AssistantImages> GenerateImages(
        ModelSpec model, ImagesContext context, ImagesOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var provider = ResolveImagesApiProvider(model.Api);
        return provider.GenerateImagesAsync(model, context, options, cancellationToken);
    }
}
