using System.Text.Json.Nodes;
using Pi.Ai.Utils;

using Pi.Ai.Models;
namespace Pi.Ai.Auth.OAuth;

/// <summary>Radius 网关模型条目。对应 TS <c>RadiusGatewayModel</c>（providers/radius-config.ts）。</summary>
public sealed record RadiusGatewayModel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required bool Reasoning { get; init; }

    /// <summary>思考档位映射（level → thinking budget）。</summary>
    public IReadOnlyDictionary<string, long>? ThinkingLevelMap { get; init; }

    public required IReadOnlyList<string> Input { get; init; }

    public required ModelCostRates Cost { get; init; }

    public required long ContextWindow { get; init; }

    public required long MaxTokens { get; init; }
}

/// <summary>Radius 网关配置。对应 TS <c>RadiusGatewayConfig</c>。</summary>
public sealed record RadiusGatewayConfig
{
    public required string BaseUrl { get; init; }

    public required IReadOnlyList<RadiusGatewayModel> Models { get; init; }
}

/// <summary>
/// Radius 网关配置辅助。对应 TS providers/radius-config.ts 的
/// normalize/getRadiusCredentialConfig 部分（loadRadiusGatewayConfig 与模型目录
/// → provider 阶段接线）。
/// </summary>
public static class RadiusConfig
{
    public const string DefaultRadiusGateway = "https://radius.pi.dev";

    /// <summary>补协议、去尾部斜杠。对应 TS <c>normalizeRadiusGatewayUrl</c>。</summary>
    public static string NormalizeGatewayUrl(string value)
    {
        var withScheme = System.Text.RegularExpressions.Regex.IsMatch(value, @"^https?://",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            ? value
            : $"https://{value}";
        return withScheme.TrimEnd('/');
    }

    /// <summary>模型条目形状校验。对应 TS <c>isRadiusGatewayModel</c>。</summary>
    private static bool IsGatewayModel(JsonObject value)
        => value.Str("id") is not null
            && value.Str("name") is not null
            && value.Bool("reasoning") is not null
            && value["input"] is JsonArray
            && value.Obj("cost") is not null;

    /// <summary>配置消毒：形状不符时丢弃。对应 TS <c>sanitizeRadiusGatewayConfig</c>（供 JSON 加载路径使用）。</summary>
    public static RadiusGatewayConfig? SanitizeGatewayConfig(JsonObject? config)
    {
        if (config is null) return null;
        var baseUrl = config.Str("baseUrl");
        if (baseUrl is null || config["models"] is not JsonArray models) return null;

        var parsed = new List<RadiusGatewayModel>();
        foreach (var node in models)
        {
            if (node is not JsonObject item || !IsGatewayModel(item)) continue;
            var cost = item.Obj("cost")!;
            parsed.Add(new RadiusGatewayModel
            {
                Id = item.Str("id")!,
                Name = item.Str("name")!,
                Reasoning = item.Bool("reasoning") ?? false,
                ThinkingLevelMap = item.Obj("thinkingLevelMap") is { } map
                    ? map.ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value is JsonValue { } v && v.TryGetValue<long>(out var n) ? n : 0)
                    : null,
                Input = item["input"]!.AsArray()
                    .OfType<JsonValue>()
                    .Select(v => v.TryGetValue<string>(out var s) ? s : "")
                    .Where(s => s.Length > 0)
                    .ToList(),
                Cost = new ModelCostRates(
                    cost.Num("input") ?? 0,
                    cost.Num("output") ?? 0,
                    cost.Num("cache_read"),
                    cost.Num("cache_write")),
                ContextWindow = (long)(item.Num("contextWindow") ?? 0),
                MaxTokens = (long)(item.Num("maxTokens") ?? 0),
            });
        }
        return new RadiusGatewayConfig { BaseUrl = baseUrl, Models = parsed };
    }

    /// <summary>从凭据扩展字段取网关配置。对应 TS <c>getRadiusCredentialConfig</c>。</summary>
    public static RadiusGatewayConfig? GetCredentialConfig(Credential.OAuth? credential)
        => credential?.GatewayConfig;
}
