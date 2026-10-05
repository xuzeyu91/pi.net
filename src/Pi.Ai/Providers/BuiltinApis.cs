using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// 内建 API 实现句柄。对应 TS 各 <c>api/&lt;name&gt;.lazy.ts</c> 导出的单例：
/// TS 用动态 import 隔离 Node-only 模块，C# 直接引用静态入口。
/// </summary>
internal static class BuiltinApis
{
    public static ProviderStreams AnthropicMessages()
        => ProviderStreamsFactory.FromAsyncStream((model, context, options) =>
            Providers.AnthropicMessages.StreamSimple(ToRuntime(model), context, options));

    public static ProviderStreams OpenAiCompletions()
        => ProviderStreamsFactory.FromAsyncStream((model, context, options) =>
            Providers.OpenAiCompletions.StreamSimple(ToRuntime(model), context, options));

    public static ProviderStreams OpenAiResponses()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.OpenAiResponses.StreamSimple(model, context, options));

    public static ProviderStreams OpenAiCodexResponses()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.OpenAiCodexResponses.StreamSimple(model, context, options));

    public static ProviderStreams AzureOpenAiResponses()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.AzureOpenAiResponses.StreamSimple(model, context, options));

    public static ProviderStreams GoogleGenerativeAi()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.GoogleGenerativeAi.StreamSimple(model, context, options));

    public static ProviderStreams GoogleVertex()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.GoogleVertex.StreamSimple(model, context, options));

    public static ProviderStreams MistralConversations()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.MistralConversations.StreamSimple(model, context, options));

    public static ProviderStreams BedrockConverseStream()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.BedrockConverseStream.StreamSimple(model, context, options));

    public static ProviderStreams PiMessages()
        => ProviderStreamsFactory.FromStream((model, context, options) =>
            Api.PiMessages.StreamSimple(model, context, options));

    /// <summary>OpenRouter 图片生成 API。对应 TS <c>openrouterImagesApi()</c>。</summary>
    public static ProviderImages OpenRouterImages() => new()
    {
        GenerateImages = (model, context, options, cancellationToken) =>
            Api.OpenRouterImages.GenerateImages(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>TypeSafe System One 分类 API。对应 TS <c>typesafeSystemOneApi()</c>。</summary>
    public static ProviderClassifier TypesafeSystemOne() => new()
    {
        Classify = (model, context, options, cancellationToken) =>
            Api.TypesafeSystemOne.ClassifyAsync(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>Cloudflare Workers AI System One 分类 API。对应 TS <c>cloudflareWorkersAISystemOneApi()</c>。</summary>
    public static ProviderClassifier CloudflareWorkersAiSystemOne() => new()
    {
        Classify = (model, context, options, cancellationToken) =>
            Api.CloudflareWorkersAiSystemOne.ClassifyAsync(model, context, options, cancellationToken: cancellationToken),
    };

    /// <summary>
    /// 包装：派发前把 Cloudflare account/gateway 端点占位符按 provider env 物化。
    /// 对应 TS <c>cloudflareStreams</c>。
    /// </summary>
    public static ProviderStreams WithCloudflare(ProviderStreams streams) => new()
    {
        Stream = (model, context, options) => streams.Stream(
            CloudflareStreams.ResolveModel(model, ProviderStreamOptions.FromDictionary(options)?.Env), context, options),
        StreamSimple = (model, context, options) => streams.StreamSimple(
            CloudflareStreams.ResolveModel(model, ProviderStreamOptions.FromDictionary(options)?.Env), context, options),
    };

    /// <summary>
    /// 包装：派发前补 OpenCode 必需的会话路由头（缺省取 <c>options.sessionId</c>，已有同名头则不覆盖）。
    /// 对应 TS <c>withOpenCodeSessionHeader</c>。
    /// </summary>
    public static ProviderStreams WithOpenCodeSessionHeader(ProviderStreams streams) => new()
    {
        Stream = (model, context, options) => streams.Stream(model, context, WithSessionHeader(options)),
        StreamSimple = (model, context, options) => streams.StreamSimple(model, context, WithSessionHeader(options)),
    };

    private const string OpenCodeSessionHeader = "x-opencode-session";

    private static IReadOnlyDictionary<string, object?>? WithSessionHeader(
        IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null
            || !options.TryGetValue("sessionId", out var sessionIdValue)
            || sessionIdValue is not string sessionId
            || sessionId.Length == 0)
        {
            return options;
        }

        var headers = options.TryGetValue("headers", out var headersValue)
            ? headersValue as IReadOnlyDictionary<string, string?>
            : null;
        if (headers is not null
            && headers.Keys.Any(key => string.Equals(key, OpenCodeSessionHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        var merged = new Dictionary<string, string?>(headers ?? new Dictionary<string, string?>())
        {
            [OpenCodeSessionHeader] = sessionId,
        };
        return new Dictionary<string, object?>(options) { ["headers"] = merged };
    }

    /// <summary>把 <see cref="ModelSpec"/> 投影为 API 实现消费的运行时模型描述。</summary>
    private static Types.Model ToRuntime(ModelSpec model)
        => new(model.Id, model.Name, model.Api, model.Provider);
}
