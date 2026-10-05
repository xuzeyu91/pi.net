using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>TypeSafe System One 请求体（不含传输层信封）。对应 TS <c>SystemOneWireRequest</c>。</summary>
public sealed record SystemOneWireRequest(JsonObject State, JsonObject Questions);

/// <summary>各服务承载 System One 模型的传输差异。对应 TS <c>SystemOneTransport</c>。</summary>
public sealed record SystemOneTransport
{
    /// <summary>该传输实现的分类器 API。</summary>
    public required string Api { get; init; }

    /// <summary>错误信息里的服务名。</summary>
    public required string Label { get; init; }

    /// <summary>绝对请求 URL。</summary>
    public required Func<ModelSpec, Uri> Url { get; init; }

    /// <summary>把 System One 请求包装进服务的请求信封。</summary>
    public required Func<ModelSpec, SystemOneWireRequest, JsonObject> Payload { get; init; }

    /// <summary>从服务响应信封提取 System One 输出（{answers, usage}）。</summary>
    public required Func<JsonObject, JsonObject> Output { get; init; }
}

/// <summary>
/// System One 结构化分类协议（TypeSafe 专有；Cloudflare/OpenRouter 转发同一协议）。
/// 对应 TS <c>api/system-one-shared.ts</c>。公共 <c>bool</c> 值映射为 wire 级 <c>noul</c>。
/// </summary>
public static class SystemOne
{
    /// <summary>值是否为对象（非数组）。对应 TS <c>isRecord</c>。</summary>
    public static bool IsRecord(JsonNode? value)
        => value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.Object;

    /// <summary>运行一次 System One 分类。对应 TS <c>classifySystemOne</c>。</summary>
    public static async Task<ClassifierResult> ClassifyAsync(
        SystemOneTransport transport,
        ModelSpec model,
        ClassifierContext context,
        ClassifierOptions? options,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var output = new ClassifierResult
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };

        try
        {
            if (model.Api != transport.Api)
            {
                throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            }
            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }
            var apiKey = options.ApiKey;
            var payload = transport.Payload(model, WireRequest(context));
            if (options.OnPayload is { } onPayload)
            {
                var transformed = await onPayload(payload, model).ConfigureAwait(false);
                if (transformed is not null) payload = transformed;
            }
            var client = httpClient ?? OAuthHttp.Shared;
            var signal = options.Signal;
            HttpResponseMessage response;
            JsonObject? body;
            (response, body) = await ProviderRetry.RetryAsync(async () =>
            {
                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, transport.Url(model))
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (key, value) in RequestHeaders(model, apiKey, options.Headers))
                {
                    if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                }
                try
                {
                    var httpResponse = await client.SendAsync(request, perCallCts.Token).ConfigureAwait(false);
                    var text = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        throw new ProviderHttpException(
                            (int)httpResponse.StatusCode, text,
                            headers: httpResponse.Headers.ToDictionary(
                                kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase));
                    }
                    return (httpResponse, JsonNode.Parse(text) as JsonObject);
                }
                catch (OperationCanceledException) when (!signal.IsCancellationRequested)
                {
                    throw new TimeoutException($"Request timed out after {options.TimeoutMs}ms");
                }
            }, new ProviderRetryOptions
            {
                MaxRetries = options.MaxRetries ?? 2,
                MaxRetryDelayMs = options.MaxRetryDelayMs,
                Signal = signal,
            }).ConfigureAwait(false);

            if (options.OnResponse is { } onResponse)
            {
                await onResponse(new ProviderResponse
                {
                    Status = (int)response.StatusCode,
                    Headers = response.Headers.ToDictionary(
                        kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
                }, model).ConfigureAwait(false);
            }
            var result = transport.Output(body ?? []);
            // 先解析 usage：答案畸形时请求同样已计费。
            var usage = ParseUsage(result["usage"], model);
            output = output with
            {
                Usage = usage,
                Answers = ParseAnswers(transport.Label, result["answers"], context),
            };
            return output;
        }
        catch (Exception error)
        {
            return output with
            {
                StopReason = options?.Signal.IsCancellationRequested == true
                    ? ClassifierStopReason.Aborted
                    : ClassifierStopReason.Error,
                ErrorMessage = ProviderError.Format(
                    ProviderError.Normalize(error), $"{transport.Label} error"),
            };
        }
    }

    /// <summary>公共 bool 问题 → wire 级 noul。对应 TS <c>wireRequest</c>。</summary>
    private static SystemOneWireRequest WireRequest(ClassifierContext context)
    {
        var questions = new JsonObject();
        foreach (var (id, question) in context.Questions)
        {
            // JsonPolymorphic 判别标注由默认序列化器处理。
            var serialized = JsonSerializer.SerializeToNode(question) is JsonObject questionObject
                ? questionObject
                : [];
            if (question.Kind == "bool") serialized["type"] = "noul";
            questions[id] = serialized;
        }
        return new SystemOneWireRequest(context.State, questions);
    }

    private static Dictionary<string, string?> RequestHeaders(
        ModelSpec model, string apiKey, IReadOnlyDictionary<string, string?>? optionsHeaders)
    {
        var headers = new Dictionary<string, string?>
        {
            ["authorization"] = $"Bearer {apiKey}",
            ["content-type"] = "application/json",
        };
        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers) headers[key] = value;
        }
        if (optionsHeaders is not null)
        {
            foreach (var (key, value) in optionsHeaders) headers[key] = value;
        }
        return headers;
    }

    private static double RequiredNumber(string label, JsonNode? value, string field)
        => value is JsonValue { } primitive && primitive.TryGetValue<double>(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number
            : throw new InvalidOperationException($"{label} returned an invalid {field}");

    private static Dictionary<string, double> Probabilities(string label, JsonNode? value, string id)
    {
        if (value is not JsonObject objectValue)
        {
            throw new InvalidOperationException($"{label} returned invalid probabilities for {id}");
        }
        var probabilities = new Dictionary<string, double>();
        foreach (var (key, probability) in objectValue)
        {
            probabilities[key] = RequiredNumber(label, probability, $"probability for {id}.{key}");
        }
        return probabilities;
    }

    /// <summary>按上下文问题集解析答案。对应 TS <c>parseAnswers</c>。</summary>
    public static IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(
        string label, JsonNode? value, ClassifierContext context)
    {
        if (value is not JsonObject answersObject)
        {
            throw new InvalidOperationException($"{label} returned an unexpected response");
        }
        var answers = new Dictionary<string, ClassifierAnswer>();
        foreach (var (id, question) in context.Questions)
        {
            if (answersObject[id] is not JsonObject answer)
            {
                throw new InvalidOperationException($"{label} did not return an answer for {id}");
            }
            switch (question.Kind)
            {
                case "choice":
                    if (answer.Str("type") != "choice" || answer.Str("choice") is not { } choice)
                    {
                        throw new InvalidOperationException($"{label} did not return a choice answer for {id}");
                    }
                    answers[id] = new ClassifierChoiceAnswer(
                        choice,
                        Probabilities(label, answer["probabilities"], id),
                        RequiredNumber(label, answer["confidence"], $"confidence for {id}"));
                    break;
                case "score":
                    if (answer.Str("type") != "score")
                    {
                        throw new InvalidOperationException($"{label} did not return a score answer for {id}");
                    }
                    answers[id] = new ClassifierScoreAnswer(
                        RequiredNumber(label, answer["score"], $"score for {id}"),
                        RequiredNumber(label, answer["confidence"], $"confidence for {id}"));
                    break;
                default:
                    // wire 级 noul 映射回公共 bool。
                    if (answer.Str("type") != "noul")
                    {
                        throw new InvalidOperationException($"{label} did not return a bool answer for {id}");
                    }
                    answers[id] = new ClassifierBoolAnswer(
                        RequiredNumber(label, answer["noul"], $"probability for {id}"));
                    break;
            }
        }
        return answers;
    }

    private static long TokenCount(JsonNode? value)
        => value is JsonValue { } primitive && primitive.TryGetValue<double>(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0
            ? (long)number
            : 0;

    /// <summary>System One 的 {input_tokens, output_tokens} 按目录报价计费；缺失/畸形时返回 null 不失败。对应 TS <c>parseUsage</c>。</summary>
    public static Usage? ParseUsage(JsonNode? value, ModelSpec model)
    {
        if (value is not JsonObject usage || (!usage.Has("input_tokens") && !usage.Has("output_tokens")))
        {
            return null;
        }
        var input = TokenCount(usage["input_tokens"]);
        var output = TokenCount(usage["output_tokens"]);
        return ModelOperations.CalculateCost(model, new Usage(input, output));
    }
}
