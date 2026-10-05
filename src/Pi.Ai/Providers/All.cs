using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// 内建 provider 全集与目录读取。对应 TS <c>providers/all.ts</c>：
/// <c>getBuiltinModel(s)</c> / <c>getBuiltinImageModel(s)</c> / <c>getBuiltinClassifierModel(s)</c> /
/// <c>getBuiltinProviders()</c> / <c>getAllBuiltinModels()</c> / <c>builtinProviders()</c> / <c>builtinModels()</c>。
/// </summary>
public static class All
{
    /// <summary>内建 provider id 列表（由工厂顺序派生，与 <c>models.generated.ts</c> 的 MODELS 键一致）。</summary>
    public static IReadOnlyList<string> GetBuiltinProviders()
        => BuiltinProviders().Select(provider => provider.Id).ToList();

    /// <summary>按 provider + id 取 chat 模型。对应 TS <c>getBuiltinModel()</c>。</summary>
    public static ModelSpec? GetBuiltinModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ChatModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部 chat 模型。对应 TS <c>getBuiltinModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinModels(string provider) => BuiltinCatalog.Chat(provider);

    /// <summary>按 provider + id 取图片模型。对应 TS <c>getBuiltinImageModel()</c>。</summary>
    public static ModelSpec? GetBuiltinImageModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ImageModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部图片模型。对应 TS <c>getBuiltinImageModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinImageModels(string provider)
        => [.. BuiltinCatalog.Load(provider).ImageModels.Values];

    /// <summary>按 provider + id 取分类模型。对应 TS <c>getBuiltinClassifierModel()</c>。</summary>
    public static ModelSpec? GetBuiltinClassifierModel(string provider, string modelId)
        => BuiltinCatalog.Load(provider).ClassifierModels.GetValueOrDefault(modelId);

    /// <summary>按 provider 取全部分类模型。对应 TS <c>getBuiltinClassifierModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetBuiltinClassifierModels(string provider)
        => [.. BuiltinCatalog.Load(provider).ClassifierModels.Values];

    /// <summary>按 provider 取全部类别模型。对应 TS <c>getAllBuiltinModels()</c>。</summary>
    public static IReadOnlyList<ModelSpec> GetAllBuiltinModels(string provider) => BuiltinCatalog.All(provider);

    /// <summary>
    /// 全部内建 provider（每次新建，顺序与 TS <c>builtinProviders()</c> 一致）。
    /// </summary>
    public static IReadOnlyList<IProvider> BuiltinProviders() =>
    [
        AmazonBedrock.Provider(),
        AntLing.Provider(),
        Anthropic.Provider(),
        AzureOpenAiResponses.Provider(),
        Baseten.Provider(),
        Cerebras.Provider(),
        CloudflareAiGateway.Provider(),
        CloudflareWorkersAi.Provider(),
        Deepseek.Provider(),
        Fireworks.Provider(),
        GitHubCopilot.Provider(),
        Google.Provider(),
        GoogleVertex.Provider(),
        Groq.Provider(),
        HuggingFace.Provider(),
        KimiCoding.Provider(),
        Meta.Provider(),
        Minimax.Provider(),
        MinimaxCn.Provider(),
        Mistral.Provider(),
        MoonshotAi.Provider(),
        MoonshotAiCn.Provider(),
        Nvidia.Provider(),
        OpenAi.Provider(),
        OpenAiCodex.Provider(),
        OpenCode.Provider(),
        OpenCodeGo.Provider(),
        OpenRouter.Provider(),
        QwenTokenPlan.Provider(),
        QwenTokenPlanCn.Provider(),
        QwenTokenPlanIndividual.Provider(),
        Radius.Provider(),
        Together.Provider(),
        Typesafe.Provider(),
        VercelAiGateway.Provider(),
        Xai.Provider(),
        Xiaomi.Provider(),
        XiaomiTokenPlanAms.Provider(),
        XiaomiTokenPlanCn.Provider(),
        XiaomiTokenPlanSgp.Provider(),
        Zai.Provider(),
        ZaiCodingCn.Provider(),
    ];

    /// <summary>注册全部内建 provider 的 <see cref="Models"/> 集合。对应 TS <c>builtinModels()</c>。</summary>
    public static Models.Models BuiltinModels()
    {
        var models = new Models.Models();
        foreach (var provider in BuiltinProviders()) models.SetProvider(provider);
        return models;
    }

    /// <summary>
    /// 目录生成时间（epoch 毫秒）。pi 仓库不带 <c>data/.manifest.json</c> 生成产物，
    /// C# 侧以嵌入资源是否存在为准，缺失返回 null。对应 TS <c>getBuiltinModelDataGeneratedAt()</c>。
    /// </summary>
    public static long? GetBuiltinModelDataGeneratedAt()
    {
        var assembly = typeof(All).Assembly;
        using var stream = assembly.GetManifestResourceStream("Pi.Ai.ModelData..manifest.json");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        var generatedAt = manifest?.Str("generatedAt");
        return generatedAt is not null && DateTimeOffset.TryParse(generatedAt, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }
}
