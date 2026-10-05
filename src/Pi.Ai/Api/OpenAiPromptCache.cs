namespace Pi.Ai.Api;

/// <summary>OpenAI 提示缓存键长度上限。对应 TS <c>openai-prompt-cache.ts</c>。</summary>
public static class OpenAiPromptCache
{
    public const int PromptCacheKeyMaxLength = 64;

    public static string? ClampPromptCacheKey(string? key)
    {
        if (key is null) return null;
        return key.Length <= PromptCacheKeyMaxLength ? key : key[..PromptCacheKeyMaxLength];
    }
}
