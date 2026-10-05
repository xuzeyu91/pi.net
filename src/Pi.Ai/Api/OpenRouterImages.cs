using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// OpenRouter 的图片生成：走 chat completions 端点。对应 TS
/// <c>api/openrouter-images.ts</c>——TS 用 openai npm client；C# 直接对
/// <c>model.baseUrl</c> + /chat/completions 发 REST 请求（wire 等价）。
/// </summary>
public static class OpenRouterImages
{
    /// <summary>图片生成的请求载荷。对应 TS <c>OpenRouterImagesCreateParams</c>。</summary>
    public static JsonObject BuildParams(ModelSpec model, ImagesContext context)
    {
        var content = new JsonArray();
        foreach (var item in context.Input)
        {
            switch (item)
            {
                case TextContent text:
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                    });
                    break;
                case ImageContent image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{image.MimeType};base64,{image.Data}",
                        },
                    });
                    break;
            }
        }

        // 模型能输出文本时同时请求两种模态（TS：output.includes("text")）。
        var modalities = new JsonArray("image");
        if (model.Input.Contains("text")) modalities.Add("text");

        return new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            }),
            ["stream"] = false,
            ["modalities"] = modalities,
        };
    }

    /// <summary>用量解析：缓存读/写拆分 + 目录报价成本。对应 TS <c>parseUsage</c>。</summary>
    public static Usage ParseUsage(JsonObject rawUsage, ModelSpec model)
    {
        long Get(string key) => (long)(rawUsage.Num(key) ?? 0);
        var promptTokens = Get("prompt_tokens");
        var details = rawUsage.Obj("prompt_tokens_details");
        var reportedCachedTokens = (long)(details?.Num("cached_tokens") ?? 0);
        var cacheWriteTokens = (long)(details?.Num("cache_write_tokens") ?? 0);
        var cacheReadTokens = cacheWriteTokens > 0
            ? Math.Max(0, reportedCachedTokens - cacheWriteTokens)
            : reportedCachedTokens;
        var input = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var output = Get("completion_tokens");
        var costInput = (model.Cost?.Input ?? 0) / 1_000_000 * input;
        var costOutput = (model.Cost?.Output ?? 0) / 1_000_000 * output;
        var costCacheRead = (model.Cost?.CacheRead ?? 0) / 1_000_000 * cacheReadTokens;
        var costCacheWrite = (model.Cost?.CacheWrite ?? 0) / 1_000_000 * cacheWriteTokens;
        return new Usage(
            Input: input,
            Output: output,
            CacheRead: cacheReadTokens,
            CacheWrite: cacheWriteTokens)
        {
            Cost = costInput + costOutput + costCacheRead + costCacheWrite,
        };
    }

    /// <summary>图片生成（OpenRouter chat completions）。对应 TS <c>generateImages</c>。</summary>
    public static async Task<AssistantImages> GenerateImages(
        ModelSpec model, ImagesContext context, ImagesOptions? options,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var outputContent = new List<ContentBlock>();
        var output = new AssistantImages
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = outputContent,
            StopReason = ImagesStopReason.Stop,
        };

        try
        {
            var apiKey = options?.ApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }

            var payload = BuildParams(model, context);
            if (options?.OnPayload is { } onPayload)
            {
                var transformed = await onPayload(payload, model).ConfigureAwait(false);
                if (transformed is not null) payload = transformed;
            }

            var headers = Headers.Merge(
                new Dictionary<string, string?> { ["authorization"] = $"Bearer {apiKey}" },
                ModelHeaders(model),
                options?.Headers);

            var client = httpClient ?? OAuthHttp.Shared;
            var response = await SendWithRetryAsync(client, model.BaseUrl, payload, headers,
                options, cancellationToken).ConfigureAwait(false);

            if (options?.OnResponse is { } onResponse)
            {
                var responseHeaders = response.Headers.ToDictionary(
                    kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase);
                await onResponse(new ProviderResponse
                {
                    Status = (int)response.StatusCode,
                    Headers = responseHeaders,
                }, model).ConfigureAwait(false);
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                as JsonObject ?? throw new InvalidOperationException("OpenRouter images returned invalid JSON");
            output = output with { ResponseId = body.Str("id") };
            if (body.Obj("usage") is { } usage)
            {
                output = output with { Usage = ParseUsage(usage, model) };
            }

            var choice = (body["choices"] as JsonArray)?[0] as JsonObject;
            if (choice is not null)
            {
                var message = choice.Obj("message");
                var contentText = message?.Str("content");
                if (contentText is { Length: > 0 })
                {
                    outputContent.Add(new TextContent(contentText));
                }

                foreach (var imageNode in (message?["images"] as JsonArray) ?? [])
                {
                    if (imageNode is not JsonObject image) continue;
                    var imageUrl = image.Str("image_url")
                        ?? image.Obj("image_url")?.Str("url");
                    if (imageUrl is null || !imageUrl.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var match = System.Text.RegularExpressions.Regex.Match(
                        imageUrl, @"^data:([^;]+);base64,(.+)$");
                    if (!match.Success) continue;
                    outputContent.Add(new ImageContent(match.Groups[2].Value, match.Groups[1].Value));
                }
            }

            return output;
        }
        catch (Exception error)
        {
            return output with
            {
                Output = [],
                StopReason = options is { Signal.IsCancellationRequested: true }
                    ? ImagesStopReason.Aborted
                    : ImagesStopReason.Error,
                ErrorMessage = ProviderError.Format(ProviderError.Normalize(error)),
            };
        }
    }

    /// <summary>model.headers 存于目录条目的扩展字段（TS BaseModel.headers）。</summary>
    private static IReadOnlyDictionary<string, string?>? ModelHeaders(ModelSpec model)
    {
        if (model.Extra?["headers"] is not JsonObject headers) return null;
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in headers)
        {
            result[key] = value is JsonValue { } primitive && primitive.TryGetValue<string>(out var text)
                ? text
                : null; // null 值 = 抑制同名默认头
        }
        return result;
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, string baseUrl, JsonObject payload,
        IReadOnlyDictionary<string, string>? headers, ImagesOptions? options,
        CancellationToken cancellationToken)
    {
        var signal = options?.Signal ?? default;
        using var linked = signal.CanBeCanceled && cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken)
            : null;
        var effectiveSignal = linked?.Token
            ?? (signal.CanBeCanceled ? signal : cancellationToken);

        return await ProviderRetry.RetryAsync(async () =>
        {
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveSignal);
            if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (headers is not null)
            {
                foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
            }
            try
            {
                var response = await client.SendAsync(request, perCallCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                    throw new ProviderHttpException(
                        (int)response.StatusCode, body,
                        headers: response.Headers.ToDictionary(
                            kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase));
                }
                return response;
            }
            catch (OperationCanceledException) when (effectiveSignal.IsCancellationRequested)
            {
                throw new OperationCanceledException("Request aborted", effectiveSignal);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Request timed out after {options?.TimeoutMs}ms");
            }
        }, new ProviderRetryOptions
        {
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            Signal = effectiveSignal,
        }).ConfigureAwait(false);
    }
}
