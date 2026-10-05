using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Utils;

namespace Pi.Ai.Providers;

/// <summary>
/// Radius 网关配置 → 模型目录 / 网络加载。对应 TS <c>providers/radius-config.ts</c>
/// 的 <c>getRadiusModelsFromConfig</c>/<c>getRadiusModels</c>/<c>loadRadiusGatewayConfig</c>。
/// </summary>
public static class RadiusProviderConfig
{
    /// <summary>网关模型条目 → <c>pi-messages</c> 模型（补齐 api/provider/baseUrl）。</summary>
    public static IReadOnlyList<ModelSpec> ModelsFromConfig(string providerId, RadiusGatewayConfig config)
        => config.Models.Select(model => new ModelSpec
        {
            Id = model.Id,
            Name = model.Name,
            Api = "pi-messages",
            Provider = providerId,
            BaseUrl = config.BaseUrl,
            Input = model.Input,
            Cost = model.Cost,
            Reasoning = model.Reasoning,
            ContextWindow = model.ContextWindow,
            MaxTokens = model.MaxTokens,
            ThinkingLevelMap = model.ThinkingLevelMap is null
                ? null
                : ThinkingLevelMapFromBudgets(model.ThinkingLevelMap),
        }).ToList();

    /// <summary>从凭据扩展字段取网关模型。对应 TS <c>getRadiusModels</c>。</summary>
    public static IReadOnlyList<ModelSpec> Models(string providerId, Credential.OAuth? credential)
    {
        var config = RadiusConfig.GetCredentialConfig(credential);
        return config is null ? [] : ModelsFromConfig(providerId, config);
    }

    /// <summary>
    /// 从网关 <c>/v1/config</c> 加载配置。对应 TS <c>loadRadiusGatewayConfig</c>：
    /// 非 2xx 抛错（响应体截断到 512 字符），形状不符抛错。
    /// </summary>
    public static async Task<RadiusGatewayConfig> LoadGatewayConfigAsync(string gateway, string? apiKey,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{gateway.TrimEnd('/')}/v1/config"));
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {apiKey}");
        }

        var client = httpClient ?? OAuthHttp.Shared;
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not load Radius config from {gateway}: {(int)response.StatusCode}: {TruncateHttpBody(body)}");
        }

        var config = RadiusConfig.SanitizeGatewayConfig(
            JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException($"Invalid Radius config from {gateway}"));
        return config ?? throw new InvalidOperationException($"Invalid Radius config from {gateway}");
    }

    private static string TruncateHttpBody(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length > 512 ? $"{trimmed[..512]}…" : trimmed;
    }

    /// <summary>
    /// Radius 的 <c>thinkingLevelMap</c> 是 level → thinking budget（数字），
    /// 与目录里的 string|null 映射不同；此处把数字预算转成字符串 effort 档位。
    /// </summary>
    private static ThinkingLevelMap ThinkingLevelMapFromBudgets(IReadOnlyDictionary<string, long> budgets)
    {
        var json = new JsonObject();
        foreach (var (level, budget) in budgets) json[level] = budget.ToString();
        return ThinkingLevelMap.FromJsonObject(json);
    }
}
