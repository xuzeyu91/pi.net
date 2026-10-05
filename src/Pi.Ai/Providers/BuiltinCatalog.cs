using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// 内建模型目录读取。对应 TS <c>providers/&lt;name&gt;.models.ts</c>（每家的
/// <c>XXX_MODELS</c>/<c>XXX_IMAGE_MODELS</c>/<c>XXX_CLASSIFIER_MODELS</c>）与
/// <c>models.generated.ts</c> 的按 provider 汇总：从嵌入资源加载 groups JSON，
/// 缺失时退回空目录（pi 仓库不带生成产物，与 P18 约定一致）。
/// <para>TS 的 42 个 <c>.models.ts</c> 只是 <c>flatten*ModelCatalog</c> 的薄壳，
/// C# 归并为这一个按 provider 键控的读取器（同 <c>models.generated.ts</c> →
/// 嵌入资源 的既定处理）。</para>
/// </summary>
internal static class BuiltinCatalog
{
    private static readonly ConcurrentDictionary<string, ModelCatalog> Cache = new(StringComparer.Ordinal);

    public static ModelCatalog Load(string provider)
        => Cache.GetOrAdd(provider, static id =>
        {
            try
            {
                return ModelCatalog.LoadFromResource(id);
            }
            catch (FileNotFoundException)
            {
                return ModelCatalog.Load(id, new JsonObject());
            }
        });

    public static IReadOnlyList<ModelSpec> Chat(string provider) => [.. Load(provider).ChatModels.Values];

    public static IReadOnlyList<ModelSpec> All(string provider)
        => [.. Load(provider).ChatModels.Values, .. Load(provider).ImageModels.Values, .. Load(provider).ClassifierModels.Values];

    public static ModelSpec? Find(string provider, string modelId) => Load(provider).Find(modelId);
}
