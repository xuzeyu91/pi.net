using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai;

/// <summary>图片生成 API 提供者。对应 TS <c>ImagesApiProvider</c>。</summary>
public interface IImagesApiProvider
{
    /// <summary>API 名（如 "openrouter-images"）。</summary>
    string Api { get; }

    Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options, CancellationToken cancellationToken);
}

/// <summary>
/// 图片 API 注册表：按 <c>model.api</c> 分发图片生成。对应 TS
/// <c>images-api-registry.ts</c>——注册时包一层 api 匹配校验。
/// </summary>
public static class ImagesApiRegistry
{
    private sealed record Registered(IImagesApiProvider Provider, string? SourceId);

    private static readonly Dictionary<string, Registered> Registry = new(StringComparer.Ordinal);

    private sealed class WrappedProvider(IImagesApiProvider provider) : IImagesApiProvider
    {
        public string Api => provider.Api;

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
        {
            if (model.Api != provider.Api)
            {
                throw new InvalidOperationException($"Mismatched api: {model.Api} expected {provider.Api}");
            }
            return provider.GenerateImagesAsync(model, context, options, cancellationToken);
        }
    }

    /// <summary>注册（或替换）一个 API 的图片生成实现。</summary>
    public static void Register(IImagesApiProvider provider, string? sourceId = null)
        => Registry[provider.Api] = new Registered(new WrappedProvider(provider), sourceId);

    /// <summary>按 API 名取实现；未注册返回 null。</summary>
    public static IImagesApiProvider? Get(string api)
        => Registry.TryGetValue(api, out var registered) ? registered.Provider : null;
}
