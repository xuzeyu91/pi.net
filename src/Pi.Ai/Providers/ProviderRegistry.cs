using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// 内置 provider 注册表：openai-completions 兼容家按 pi 的定义批量装配。
/// 对应 TS <c>providers/all.ts</c> 的 lazy 注册模式。
/// </summary>
public static class ProviderRegistry
{
    /// <summary>openai-completions 兼容家（id → name/baseUrl/envVars）。对应各 provider.ts 的元数据。</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string BaseUrl, string[] EnvVars)>
        OpenAiCompatibleProviders =
        [
            ("deepseek", "DeepSeek", "https://api.deepseek.com", ["DEEPSEEK_API_KEY"]),
            ("groq", "Groq", "https://api.groq.com/openai/v1", ["GROQ_API_KEY"]),
            ("cerebras", "Cerebras", "https://api.cerebras.ai/v1", ["CEREBRAS_API_KEY"]),
            ("together", "Together", "https://api.together.xyz/v1", ["TOGETHER_API_KEY"]),
            ("fireworks", "Fireworks", "https://api.fireworks.ai/inference/v1", ["FIREWORKS_API_KEY"]),
            ("moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1", ["MOONSHOT_API_KEY"]),
            ("moonshotai-cn", "Moonshot AI (CN)", "https://api.moonshot.cn/v1", ["MOONSHOT_API_KEY"]),
            ("xai", "xAI", "https://api.x.ai/v1", ["XAI_API_KEY"]),
            ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ["OPENROUTER_API_KEY"]),
            ("mistral", "Mistral", "https://api.mistral.ai/v1", ["MISTRAL_API_KEY"]),
        ];

    /// <summary>按 id 创建兼容 provider（模型目录来自嵌入资源；无数据时为空目录）。</summary>
    public static OpenAiCompatibleProvider Create(string id)
    {
        var definition = OpenAiCompatibleProviders.First(p => p.Id == id);
        ModelCatalog catalog;
        try
        {
            catalog = ModelCatalog.LoadFromResource(id);
        }
        catch (FileNotFoundException)
        {
            catalog = ModelCatalog.Load(id, new System.Text.Json.Nodes.JsonObject());
        }
        return new OpenAiCompatibleProvider(
            definition.Id, definition.Name, definition.BaseUrl,
            definition.EnvVars, catalog.ChatModels.Values.ToList());
    }

    /// <summary>把全部兼容家注册进 Models 门面。</summary>
    public static void RegisterAll(Pi.Ai.Models.Models models)
    {
        foreach (var definition in OpenAiCompatibleProviders)
        {
            models.SetProvider(Create(definition.Id));
        }
    }
}
