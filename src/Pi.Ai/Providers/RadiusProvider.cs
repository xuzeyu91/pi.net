using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
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

/// <summary>
/// Radius 网关 provider：基线目录 + 动态刷新目录（凭据扩展字段 / 网络）。
/// 对应 TS <c>providers/radius.ts</c> 的 <c>radiusProvider</c>。
/// <para>TS 的刷新通过 ModelsStore 的 <c>context.publish</c> 做事务化持久化；
/// C# 侧持久化上下文尚未移植，故 <see cref="RefreshModelsAsync"/> 只做内存更新，
/// 持久化由调用方按需处理（差异已在 porting-status 记录）。</para>
/// </summary>
public sealed class RadiusProvider : IProvider
{
    private readonly IReadOnlyList<ModelSpec> _baseline;
    private IReadOnlyList<ModelSpec> _dynamic;

    public RadiusProvider(string id = "radius", string name = "Radius", string? gateway = null,
        HttpClient? httpClient = null)
    {
        Id = id;
        Name = name;
        Gateway = RadiusConfig.NormalizeGatewayUrl(gateway ?? RadiusConfig.DefaultRadiusGateway);
        HttpClient = httpClient;
        _baseline = Gateway == RadiusConfig.NormalizeGatewayUrl(RadiusConfig.DefaultRadiusGateway)
            ? BuiltinCatalog.All("radius")
                .Where(model => model.Type == ModelType.Chat)
                .Select(model => model with { Provider = id })
                .ToList()
            : [];
        _dynamic = [];
    }

    public string Id { get; }

    public string Name { get; }

    public string? BaseUrl => null;

    /// <summary>规范化后的网关地址。</summary>
    public string Gateway { get; }

    internal HttpClient? HttpClient { get; }

    public ProviderAuth Auth => new()
    {
        ApiKey = new EnvApiKeyAuth("Radius API key", ["RADIUS_API_KEY"]),
        OAuth = new LazyOAuthAuth(Name, () => OAuthFlows.LoadRadius(
            new RadiusOAuth.Options(Name, Gateway), HttpClient)),
    };

    /// <summary>基线目录 + 动态目录（动态条目按 id 覆盖基线）。对应 TS <c>getModels</c>。</summary>
    public IReadOnlyList<ModelSpec> GetModels()
    {
        var merged = new List<ModelSpec>(_baseline);
        foreach (var model in _dynamic)
        {
            var index = merged.FindIndex(entry => entry.Id == model.Id);
            if (index >= 0) merged[index] = model;
            else merged.Add(model);
        }
        return merged;
    }

    /// <summary>
    /// 刷新动态目录：先从凭据扩展字段恢复，再（可选）经网络拉取网关配置。
    /// 对应 TS <c>refreshModels</c> 的内存部分。
    /// </summary>
    public async Task<IReadOnlyList<ModelSpec>> RefreshModelsAsync(Credential? credential, bool allowNetwork = true,
        CancellationToken cancellationToken = default)
    {
        var oauth = credential as Credential.OAuth;
        var restored = RadiusProviderConfig.Models(Id, oauth);
        if (restored.Count > 0) _dynamic = restored;

        if (!allowNetwork) return _dynamic;
        var apiKey = oauth is not null ? oauth.Access : (credential as Credential.ApiKey)?.Key;
        var config = await RadiusProviderConfig.LoadGatewayConfigAsync(Gateway, apiKey, HttpClient, cancellationToken)
            .ConfigureAwait(false);
        _dynamic = RadiusProviderConfig.ModelsFromConfig(Id, config);
        return _dynamic;
    }

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => StreamSimple(model, context, options);

    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => BuiltinApis.PiMessages().StreamSimple(model, new TranscriptContext(context), options);
}
