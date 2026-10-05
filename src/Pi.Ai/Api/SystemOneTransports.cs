using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Utils;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// Cloudflare Workers AI System One 分类（REST `POST /accounts/{account}/ai/run`）。
/// 对应 TS <c>api/cloudflare-workers-ai-system-one.ts</c>：第三方模型（typesafe/jev）
/// 返回 {success, result:{state,result:{answers,usage}}}，Cloudflare 自有模型（@cf/clef）
/// 直接返回 {success, result:{answers,usage}}。
/// </summary>
public static class CloudflareWorkersAiSystemOne
{
    private const string Label = "Cloudflare Workers AI";

    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "cloudflare-workers-ai-system-one";

    /// <summary>该传输实例。对应 TS <c>transport</c>。</summary>
    public static readonly SystemOneTransport Transport = new()
    {
        Api = ApiName,
        Label = Label,
        Url = model => new Uri($"{model.BaseUrl.TrimEnd('/')}/run"),
        Payload = (model, request) => new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = new JsonObject
            {
                ["state"] = request.State.DeepClone(),
                ["questions"] = request.Questions.DeepClone(),
            },
        },
        Output = body =>
        {
            if (body.Bool("success") == false)
            {
                throw CloudflareErrorMessage(body["errors"]);
            }
            if (body["result"] is not JsonObject result)
            {
                throw new InvalidOperationException($"{Label} returned an unexpected response");
            }
            if (result.Has("answers")) return result;
            if (result.Str("state") != "Completed")
            {
                throw new InvalidOperationException(
                    $"{Label} run did not complete (state: {result.Str("state") ?? "unknown"})");
            }
            if (result["result"] is not JsonObject nested)
            {
                throw new InvalidOperationException($"{Label} returned an unexpected response");
            }
            return nested;
        },
    };

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ClassifierOptions? options,
        System.Net.Http.HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SystemOne.ClassifyAsync(Transport, model, context, options, httpClient, cancellationToken);

    private static Exception CloudflareErrorMessage(JsonNode? errors)
    {
        if (errors is System.Text.Json.Nodes.JsonArray array)
        {
            var messages = array.OfType<JsonObject>()
                .Select(error => error.Str("message"))
                .Where(message => message is not null)
                .Cast<string>()
                .ToList();
            if (messages.Count > 0)
            {
                return new InvalidOperationException($"{Label} error: {string.Join("; ", messages)}");
            }
        }
        return new InvalidOperationException($"{Label} request failed");
    }
}

/// <summary>
/// TypeSafe 原生 System One 协议（OpenRouter 转发同一协议，仅 baseUrl 不同）。
/// 对应 TS <c>api/typesafe-system-one.ts</c>。
/// </summary>
public static class TypesafeSystemOne
{
    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "typesafe-system-one";

    /// <summary>该传输实例。对应 TS <c>transport</c>。</summary>
    public static readonly SystemOneTransport Transport = new()
    {
        Api = ApiName,
        Label = "System One API",
        Url = model => new Uri($"{model.BaseUrl.TrimEnd('/')}/systemone"),
        Payload = (model, request) =>
        {
            var payload = new JsonObject
            {
                ["state"] = request.State.DeepClone(),
                ["questions"] = request.Questions.DeepClone(),
            };
            payload["model"] = model.Id;
            return payload;
        },
        Output = body => body
            ?? throw new InvalidOperationException("System One API returned an unexpected response"),
    };

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ClassifierOptions? options,
        System.Net.Http.HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SystemOne.ClassifyAsync(Transport, model, context, options, httpClient, cancellationToken);
}
