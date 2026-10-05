using Pi.Ai.Auth;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Chord.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// 专属 API provider 基类：anthropic-messages / google-generative-ai 各自
/// 的实现直连（非 openai-completions 兼容家）。对应 TS <c>createProvider</c>
/// 单 API 形状。
/// </summary>
public abstract class DedicatedProvider : IProvider
{
    private readonly IReadOnlyList<ModelSpec> _models;

    protected DedicatedProvider(string id, string name, string baseUrl,
        IReadOnlyList<string> envVars, IReadOnlyList<ModelSpec> models)
    {
        Id = id;
        Name = name;
        BaseUrl = baseUrl;
        Auth = new EnvApiKeyAuth($"{name} API key", envVars);
        _models = models;
    }

    public string Id { get; }

    public string Name { get; }

    public string? BaseUrl { get; }

    public EnvApiKeyAuth Auth { get; }

    public IReadOnlyList<ModelSpec> GetModels() => _models;

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => StreamSimple(model, context, options);

    public abstract IAssistantMessageEventStream StreamSimple(ModelSpec model,
        IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null);

    /// <summary>认证解析 + 流式选项注入（apiKey/baseUrl）。</summary>
    protected async Task<SimpleStreamOptions> ApplyAuthAsync(
        SimpleStreamOptions? options, ICredentialStore store, IAuthContext ctx,
        CancellationToken cancellationToken = default)
    {
        var credential = await store.ReadAsync(Id, cancellationToken).ConfigureAwait(false) as Credential.ApiKey;
        var resolved = await Auth.ResolveAsync(credential, ctx, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Provider {Id} is not configured");
        var current = options ?? new SimpleStreamOptions();
        return current with
        {
            ApiKey = resolved.Auth.ApiKey ?? current.ApiKey,
            BaseUrl = resolved.Auth.BaseUrl ?? BaseUrl,
        };
    }
}

/// <summary>Anthropic 专属 provider（anthropic-messages API）。</summary>
public sealed class AnthropicProvider(
    string id, string name, string baseUrl, IReadOnlyList<string> envVars,
    IReadOnlyList<ModelSpec> models) : DedicatedProvider(id, name, baseUrl, envVars, models)
{
    public override IAssistantMessageEventStream StreamSimple(ModelSpec model,
        IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
    {
        var runtimeModel = new Types.Model(model.Id, model.Name, model.Api, model.Provider);
        return AnthropicMessages.StreamSimple(runtimeModel,
            new TranscriptContext(context), ToStreamOptions(options)).GetAwaiter().GetResult();
    }

    private static SimpleStreamOptions? ToStreamOptions(IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null) return null;
        return new SimpleStreamOptions(
            ApiKey: options.TryGetValue("apiKey", out var key) ? key as string : null,
            BaseUrl: options.TryGetValue("baseUrl", out var url) ? url as string : null);
    }
}

/// <summary>Google 专属 provider（google-generative-ai API）。</summary>
public sealed class GoogleProvider(
    string id, string name, string baseUrl, IReadOnlyList<string> envVars,
    IReadOnlyList<ModelSpec> models) : DedicatedProvider(id, name, baseUrl, envVars, models)
{
    public override IAssistantMessageEventStream StreamSimple(ModelSpec model,
        IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
    {
        var runtimeModel = new Types.Model(model.Id, model.Name, model.Api, model.Provider);
        return GoogleGenerativeAi.StreamSimple(runtimeModel,
            new TranscriptContext(context), ToStreamOptions(options));
    }

    private static SimpleStreamOptions? ToStreamOptions(IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null) return null;
        return new SimpleStreamOptions(
            ApiKey: options.TryGetValue("apiKey", out var key) ? key as string : null,
            BaseUrl: options.TryGetValue("baseUrl", out var url) ? url as string : null);
    }
}

/// <summary>专属 provider 注册扩展（ProviderRegistry 的补充）。</summary>
public static class DedicatedProviderRegistry
{
    /// <summary>专属家元数据（id → 类型工厂 + envVars）。对应 anthropic.ts/google.ts。</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string BaseUrl, string[] EnvVars)>
        DedicatedProviders =
        [
            ("anthropic", "Anthropic", "https://api.anthropic.com", ["ANTHROPIC_API_KEY"]),
            ("google", "Google", "https://generativelanguage.googleapis.com/v1beta", ["GEMINI_API_KEY", "GOOGLE_API_KEY"]),
        ];

    /// <summary>按 id 创建专属 provider。</summary>
    public static IProvider CreateDedicated(string id)
    {
        var definition = DedicatedProviders.First(p => p.Id == id);
        ModelCatalog catalog;
        try
        {
            catalog = ModelCatalog.LoadFromResource(id);
        }
        catch (FileNotFoundException)
        {
            catalog = ModelCatalog.Load(id, new System.Text.Json.Nodes.JsonObject());
        }
        var models = catalog.ChatModels.Values.ToList();
        return id switch
        {
            "anthropic" => new AnthropicProvider(definition.Id, definition.Name, definition.BaseUrl,
                definition.EnvVars, models),
            "google" => new GoogleProvider(definition.Id, definition.Name, definition.BaseUrl,
                definition.EnvVars, models),
            _ => throw new ArgumentException($"Unknown dedicated provider: {id}"),
        };
    }

    /// <summary>注册专属家 + 全部兼容家。</summary>
    public static void RegisterBuiltins(Models models)
    {
        foreach (var definition in DedicatedProviders)
        {
            models.SetProvider(CreateDedicated(definition.Id));
        }
        ProviderRegistry.RegisterAll(models);
    }
}
