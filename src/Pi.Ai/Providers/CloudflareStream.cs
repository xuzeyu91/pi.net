using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>Cloudflare Workers AI / AI Gateway 端点模板。对应 TS <c>api/cloudflare.ts</c>。</summary>
public static class CloudflareEndpoints
{
    public const string WorkersAiBaseUrl =
        "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai/v1";

    public const string WorkersAiRestBaseUrl =
        "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai";

    /// <summary>AI Gateway Unified API。</summary>
    public const string AiGatewayCompatBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/compat";

    /// <summary>AI Gateway → OpenAI 透传（/compat 尚不支持 /v1/responses 时使用）。</summary>
    public const string AiGatewayOpenAiBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/openai";

    /// <summary>AI Gateway → Anthropic 透传。</summary>
    public const string AiGatewayAnthropicBaseUrl =
        "https://gateway.ai.cloudflare.com/v1/{CLOUDFLARE_ACCOUNT_ID}/{CLOUDFLARE_GATEWAY_ID}/anthropic";
}

/// <summary>
/// 把 Cloudflare account/gateway 端点占位符按 provider env 物化。
/// 对应 TS <c>providers/cloudflare-stream.ts</c> 的 <c>resolveCloudflareModel</c>。
/// </summary>
public static class CloudflareStreams
{
    private const string AccountIdKey = "CLOUDFLARE_ACCOUNT_ID";
    private const string GatewayIdKey = "CLOUDFLARE_GATEWAY_ID";

    /// <summary>替换模型 baseUrl 中的 Cloudflare 占位符（无 env 时原样返回）。</summary>
    public static ModelSpec ResolveModel(ModelSpec model, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null || !model.BaseUrl.Contains('{')) return model;
        var baseUrl = model.BaseUrl
            .Replace($"{{{AccountIdKey}}}", env.GetValueOrDefault(AccountIdKey) ?? $"{{{AccountIdKey}}}")
            .Replace($"{{{GatewayIdKey}}}", env.GetValueOrDefault(GatewayIdKey) ?? $"{{{GatewayIdKey}}}");
        return baseUrl == model.BaseUrl ? model : model with { BaseUrl = baseUrl };
    }

    /// <summary>
    /// 包装流式分发：Cloudflare provider 在派发前物化占位符。
    /// 对应 TS <c>cloudflareStreams</c>（C# 侧 provider 工厂在自身 StreamAsync 内调用 ResolveModel）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> RequiredEnvKeys { get; } =
        new Dictionary<string, string> { [AccountIdKey] = AccountIdKey, [GatewayIdKey] = GatewayIdKey };
}

/// <summary>
/// 包装 API 实现：派发前把 Cloudflare account/gateway 端点占位符按 provider env 物化。
/// 对应 TS <c>providers/cloudflare-stream.ts</c> 的 <c>cloudflareStreams</c>。
/// </summary>
public static class CloudflareStreamWrappers
{
    public static ProviderStreams WithCloudflare(ProviderStreams streams) => new()
    {
        Stream = (model, context, options) => streams.Stream(
            CloudflareStreams.ResolveModel(model, ProviderStreamOptions.FromDictionary(options)?.Env), context, options),
        StreamSimple = (model, context, options) => streams.StreamSimple(
            CloudflareStreams.ResolveModel(model, ProviderStreamOptions.FromDictionary(options)?.Env), context, options),
    };

    /// <summary>分类器对应包装。对应 TS <c>cloudflareClassifier</c>。</summary>
    public static ProviderClassifier WithCloudflare(ProviderClassifier classifier) => new()
    {
        Classify = (model, context, options, cancellationToken) => classifier.Classify(
            CloudflareStreams.ResolveModel(model, options?.Env), context, options, cancellationToken),
    };
}
