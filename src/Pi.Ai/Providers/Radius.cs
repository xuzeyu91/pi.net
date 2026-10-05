using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

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
        => LazyApis.PiMessagesApi().StreamSimple(model, new TranscriptContext(context), options);
}

/// <summary>radiusProvider 的选项。对应 TS <c>RadiusProviderOptions</c>（providers/radius.ts）。</summary>
public sealed record RadiusProviderOptions
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Gateway { get; init; }
}

/// <summary>
/// Radius provider 工厂。对应 TS <c>providers/radius.ts</c> 的 <c>radiusProvider(options)</c>。
/// </summary>
public static class Radius
{
    public static IProvider Provider(RadiusProviderOptions? options = null) => new RadiusProvider(
        options?.Id ?? "radius",
        options?.Name ?? "Radius",
        options?.Gateway);
}
