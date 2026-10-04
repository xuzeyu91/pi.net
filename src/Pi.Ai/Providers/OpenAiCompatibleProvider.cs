using Pi.Ai.Auth;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Chord.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenAI 兼容 API 的通用 provider 工厂：单一实现服务全部兼容家
/// （deepseek/groq/cerebras/together/fireworks/moonshotai/xai/openrouter/mistral…）。
/// 对应 TS <c>createProvider({ id, name, baseUrl, auth: envApiKeyAuth, models, api: openAICompletionsApi() })</c>。
/// </summary>
public sealed class OpenAiCompatibleProvider : IProvider
{
    private readonly EnvApiKeyAuth _auth;
    private readonly IReadOnlyList<ModelSpec> _models;

    public OpenAiCompatibleProvider(
        string id, string name, string baseUrl, IReadOnlyList<string> envVars,
        IReadOnlyList<ModelSpec> models)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl;
        _auth = new EnvApiKeyAuth($"{name} API key", envVars);
        _models = models;
    }

    public string Id { get; }

    public string Name { get; }

    public string? BaseUrl { get; }

    public EnvApiKeyAuth Auth => _auth;

    /// <summary>当前已知 chat 模型目录。</summary>
    public IReadOnlyList<ModelSpec> GetModels() => _models;

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => StreamSimple(model, context, options);

    /// <summary>流式：ModelSpec → Types.Model 映射并转发 OpenAI 兼容实现。</summary>
    public IAssistantMessageEventStream StreamSimple(ModelSpec model,
        IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
    {
        var runtimeModel = new Types.Model(model.Id, model.Name, model.Api, model.Provider);
        var streamOptions = ToStreamOptions(options);
        return OpenAiCompletions.StreamSimple(runtimeModel,
            new TranscriptContext(context), streamOptions).GetAwaiter().GetResult();
    }

    /// <summary>用解析出的认证构造流式选项（apiKey/baseUrl 注入）。</summary>
    public async Task<SimpleStreamOptions> ApplyAuthAsync(
        SimpleStreamOptions? options, ICredentialStore store, IAuthContext ctx,
        CancellationToken cancellationToken = default)
    {
        var credential = await store.ReadAsync(Id, cancellationToken).ConfigureAwait(false) as Credential.ApiKey;
        var resolved = await _auth.ResolveAsync(credential, ctx, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Provider {Id} is not configured");
        return new SimpleStreamOptions(
            ApiKey: resolved.Auth.ApiKey,
            BaseUrl: resolved.Auth.BaseUrl ?? BaseUrl);
    }

    private static SimpleStreamOptions? ToStreamOptions(IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null) return null;
        string? apiKey = options.TryGetValue("apiKey", out var key) ? key as string : null;
        string? baseUrl = options.TryGetValue("baseUrl", out var url) ? url as string : null;
        return new SimpleStreamOptions(ApiKey: apiKey, BaseUrl: baseUrl);
    }
}
