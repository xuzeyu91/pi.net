using Pi.Ai.Models;
using Pi.Ai.Api;
using Pi.Ai;
using Pi.Ai.Types;

namespace Pi.Ai.Providers.Images;

/// <summary>
/// 内置图片 API 提供者注册。对应 TS <c>providers/images/register-builtins.ts</c>：
/// TS 用 lazy import 隔离 Node-only 模块，C# 直接注册。
/// </summary>
public static class RegisterBuiltins
{
    private sealed class OpenRouterImagesProvider : IImagesApiProvider
    {
        public string Api => "openrouter-images";

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
            => OpenRouterImages.GenerateImages(model, context, options, cancellationToken: cancellationToken);
    }

    /// <summary>注册全部内置图片 API。</summary>
    public static void RegisterAll()
        => ImagesApiRegistry.Register(new OpenRouterImagesProvider());
}
