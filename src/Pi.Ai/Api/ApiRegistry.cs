using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>API 分发函数（wire 形状的流选项）。对应 TS <c>ApiStreamFunction</c>。</summary>
public delegate IAssistantMessageEventStream ApiStreamFn(
    ModelSpec model, TranscriptContext context, JsonObject? options);

/// <summary>单个 API 的流式实现。对应 TS <c>ApiProvider</c>（api/stream/streamSimple 三元组）。</summary>
public sealed record ApiProvider
{
    public required string Api { get; init; }

    /// <summary>完整选项流式入口（JsonObject 承载 provider 专属选项；wire 等价）。</summary>
    public required Func<ModelSpec, TranscriptContext, JsonObject?, HttpClient?, CancellationToken, IAssistantMessageEventStream> Stream { get; init; }

    /// <summary>简单选项流式入口。</summary>
    public required Func<ModelSpec, TranscriptContext, SimpleStreamOptions?, HttpClient?, CancellationToken, IAssistantMessageEventStream> StreamSimple { get; init; }
}

/// <summary>
/// API 提供方注册表（api-dispatch）。对应 TS <c>compat.ts</c> 的
/// <c>registerApiProvider/getApiProvider/unregisterApiProviders/registerBuiltInApiProviders</c>：
/// 扩展可注册/覆盖 API 实现（带 sourceId 分组注销），内建 API 注册时不覆盖已有条目。
/// </summary>
public static class ApiRegistry
{
    private sealed record RegisteredProvider(ApiProvider Provider, string? SourceId);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, RegisteredProvider> Registry = new(StringComparer.Ordinal);

    /// <summary>注册 API 实现（同 api 覆盖）。</summary>
    public static void RegisterApiProvider(ApiProvider provider, string? sourceId = null)
    {
        lock (Gate) Registry[provider.Api] = new RegisteredProvider(provider, sourceId);
    }

    /// <summary>按 api 取实现。</summary>
    public static ApiProvider? GetApiProvider(string api)
    {
        lock (Gate) return Registry.GetValueOrDefault(api)?.Provider;
    }

    /// <summary>全部已注册实现。</summary>
    public static IReadOnlyList<ApiProvider> GetApiProviders()
    {
        lock (Gate) return [.. Registry.Values.Select(entry => entry.Provider)];
    }

    /// <summary>按 sourceId 分组注销（扩展卸载）。</summary>
    public static void UnregisterApiProviders(string sourceId)
    {
        lock (Gate)
        {
            foreach (var (api, entry) in Registry.Where(kv => kv.Value.SourceId == sourceId).ToList())
            {
                Registry.Remove(api);
            }
        }
    }

    /// <summary>清空注册表。</summary>
    public static void ClearApiProviders()
    {
        lock (Gate) Registry.Clear();
    }

    /// <summary>
    /// 注册内建 API 实现（不覆盖已有条目：compat 可能晚于测试/扩展加载，
    /// 那时内建 api id 已被覆盖注册）。对应 TS <c>registerBuiltInApiProviders</c>。
    /// </summary>
    public static void RegisterBuiltInApiProviders()
    {
        foreach (var (api, provider) in BuiltInApis())
        {
            if (GetApiProvider(api) is null)
            {
                RegisterApiProvider(provider);
            }
        }
    }

    /// <summary>重置注册表并重新注册内建实现。对应 TS <c>resetApiProviders</c>。</summary>
    public static void ResetApiProviders()
    {
        ClearApiProviders();
        RegisterBuiltInApiProviders();
    }

    private static IEnumerable<(string Api, ApiProvider Provider)> BuiltInApis()
    {
        yield return ("openai-responses", new ApiProvider
        {
            Api = "openai-responses",
            Stream = (model, context, options, client, ct) => OpenAiResponses.Stream(
                model, context, FromJson<OpenAiResponsesOptions>(options), client, ct),
            StreamSimple = OpenAiResponses.StreamSimple,
        });
        yield return ("azure-openai-responses", new ApiProvider
        {
            Api = "azure-openai-responses",
            Stream = (model, context, options, client, ct) => AzureOpenAiResponses.Stream(
                model, context, FromJson<AzureOpenAiResponsesOptions>(options), client, ct),
            StreamSimple = AzureOpenAiResponses.StreamSimple,
        });
        yield return ("openai-codex-responses", new ApiProvider
        {
            Api = "openai-codex-responses",
            Stream = (model, context, options, client, ct) => OpenAiCodexResponses.Stream(
                model, context, FromJson<OpenAiCodexResponsesOptions>(options), client, ct),
            StreamSimple = OpenAiCodexResponses.StreamSimple,
        });
        yield return ("google-generative-ai", new ApiProvider
        {
            Api = "google-generative-ai",
            Stream = (model, context, options, client, ct) => GoogleGenerativeAi.Stream(
                model, context, FromJson<GoogleOptions>(options), client, ct),
            StreamSimple = GoogleGenerativeAi.StreamSimple,
        });
        yield return ("google-vertex", new ApiProvider
        {
            Api = "google-vertex",
            Stream = (model, context, options, client, ct) => GoogleVertex.Stream(
                model, context, FromJson<GoogleVertexOptions>(options), client, ct),
            StreamSimple = GoogleVertex.StreamSimple,
        });
        yield return ("mistral-conversations", new ApiProvider
        {
            Api = "mistral-conversations",
            Stream = (model, context, options, client, ct) => MistralConversations.Stream(
                model, context, FromJson<MistralOptions>(options), client, ct),
            StreamSimple = MistralConversations.StreamSimple,
        });
        yield return ("pi-messages", new ApiProvider
        {
            Api = "pi-messages",
            Stream = (model, context, options, client, ct) => PiMessages.Stream(
                model, context, FromJson<PiMessagesOptions>(options), client, ct),
            StreamSimple = PiMessages.StreamSimple,
        });
        yield return ("cloudflare-workers-ai-system-one", new ApiProvider
        {
            Api = "cloudflare-workers-ai-system-one",
            Stream = (_, _, _, _, _) => throw new NotSupportedException(
                "cloudflare-workers-ai-system-one is a classifier API without chat streaming"),
            StreamSimple = (_, _, _, _, _) => throw new NotSupportedException(
                "cloudflare-workers-ai-system-one is a classifier API without chat streaming"),
        });
        yield return ("typesafe-system-one", new ApiProvider
        {
            Api = "typesafe-system-one",
            Stream = (_, _, _, _, _) => throw new NotSupportedException(
                "typesafe-system-one is a classifier API without chat streaming"),
            StreamSimple = (_, _, _, _, _) => throw new NotSupportedException(
                "typesafe-system-one is a classifier API without chat streaming"),
        });
    }

    /// <summary>把 wire JsonObject 选项反序列化为 provider 专属选项类型。</summary>
    private static TOptions? FromJson<TOptions>(JsonObject? options) where TOptions : class
        => options is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<TOptions>(
                options, ApiRegistryJson.Options);
}

/// <summary>ApiRegistry 的 JSON 反序列化选项。</summary>
internal static class ApiRegistryJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// 旧版全局别名（deprecated）。对应 TS <c>legacy-api-aliases.ts</c>：
/// 兼容旧入口命名的薄转发，新代码应使用各 API 静态类的 Stream/StreamSimple。
/// </summary>
public static class LegacyApiAliases
{
    /// <summary>@deprecated 用 <see cref="OpenAiResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamOpenAIResponses(
        ModelSpec model, TranscriptContext context, OpenAiResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleOpenAIResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="AzureOpenAiResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamAzureOpenAIResponses(
        ModelSpec model, TranscriptContext context, AzureOpenAiResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => AzureOpenAiResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="AzureOpenAiResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleAzureOpenAIResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => AzureOpenAiResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiCodexResponses.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamOpenAICodexResponses(
        ModelSpec model, TranscriptContext context, OpenAiCodexResponsesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiCodexResponses.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="OpenAiCodexResponses.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleOpenAICodexResponses(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => OpenAiCodexResponses.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="MistralConversations.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamMistral(
        ModelSpec model, TranscriptContext context, MistralOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => MistralConversations.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="MistralConversations.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimpleMistral(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => MistralConversations.StreamSimple(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="PiMessages.Stream"/>。</summary>
    public static IAssistantMessageEventStream StreamPiMessages(
        ModelSpec model, TranscriptContext context, PiMessagesOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => PiMessages.Stream(model, context, options, client, ct);

    /// <summary>@deprecated 用 <see cref="PiMessages.StreamSimple"/>。</summary>
    public static IAssistantMessageEventStream StreamSimplePiMessages(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options = null,
        HttpClient? client = null, CancellationToken ct = default)
        => PiMessages.StreamSimple(model, context, options, client, ct);
}
