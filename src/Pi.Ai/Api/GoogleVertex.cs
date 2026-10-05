using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Google Vertex 专属流选项。对应 TS <c>GoogleVertexOptions</c>。</summary>
public record GoogleVertexOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public int? MaxTokens { get; init; }

    public double? Temperature { get; init; }

    public string? SessionId { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public System.Text.Json.Nodes.JsonObject? SamplingParams { get; init; }

    /// <summary>工具选择（auto/none/any）。</summary>
    public string? ToolChoice { get; init; }

    /// <summary>思考控制（enabled/budgetTokens/level）。</summary>
    public GoogleThinkingControl? Thinking { get; init; }

    /// <summary>GCP 项目 ID（GOOGLE_CLOUD_PROJECT / GCLOUD_PROJECT）。</summary>
    public string? Project { get; init; }

    /// <summary>GCP location（GOOGLE_CLOUD_LOCATION）。</summary>
    public string? Location { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }

    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }
}

/// <summary>
/// Google Vertex AI REST 直调实现。对应 TS <c>api/google-vertex.ts</c>（SDK → C# REST 等价）：
/// express 模式 API key（publishers/google 端点 + x-goog-api-key）或 ADC
/// （GOOGLE_APPLICATION_CREDENTIALS 服务账号 JWT 换 token / gcloud refresh token），
/// ADC 另需 project + location。流消费与 Gemini API 共享。
/// </summary>
public static class GoogleVertex
{
    public const string ApiVersion = "v1";
    /// <summary>占位凭据标记（ADC 走 env）。对应 TS <c>GCP_VERTEX_CREDENTIALS_MARKER</c>。</summary>
    public const string GcpVertexCredentialsMarker = "gcp-vertex-credentials";
    private const string DefaultEndpoint = "https://aiplatform.googleapis.com";

    /// <summary>流式生成。对应 TS <c>stream</c>。</summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        GoogleVertexOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider);
            try
            {
                var apiKey = ResolveApiKey(options);
                var request = apiKey is not null
                    ? await BuildExpressRequestAsync(model, context, options, apiKey, cancellationToken)
                        .ConfigureAwait(false)
                    : await BuildAdcRequestAsync(model, context, options, cancellationToken).ConfigureAwait(false);

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                    if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                    try
                    {
                        var httpResponse = await client.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token).ConfigureAwait(false);
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            var body = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                            throw new ProviderHttpException(
                                (int)httpResponse.StatusCode, body,
                                headers: httpResponse.Headers.ToDictionary(
                                    kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase));
                        }
                        return httpResponse;
                    }
                    catch (OperationCanceledException) when (signal.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Request aborted", signal);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"Request timed out after {options?.TimeoutMs}ms");
                    }
                }, new ProviderRetryOptions
                {
                    MaxRetries = options?.MaxRetries,
                    MaxRetryDelayMs = options?.MaxRetryDelayMs,
                    Signal = signal,
                }).ConfigureAwait(false);

                if (options?.OnResponse is { } onResponse)
                {
                    await onResponse(new ProviderResponse
                    {
                        Status = (int)response.StatusCode,
                        Headers = response.Headers.ToDictionary(
                            kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
                    }, model).ConfigureAwait(false);
                }

                stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await GoogleGenerativeAi.ConsumeContentStreamAsync(
                    model, output, stream, content, ToGenerativeOptions(options), signal, cancellationToken)
                    .ConfigureAwait(false);

                if (signal.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Google Vertex stream ended without a finish reason");
                }
                if (output.StopReason is StopReason.Aborted or StopReason.Error)
                {
                    var errorMessage = output.RawStopReason is not null
                        ? $"Provider stopped with: {output.RawStopReason}"
                        : "An unknown error occurred";
                    throw new InvalidOperationException(output.ErrorMessage ?? errorMessage);
                }

                stream.Push(new AssistantMessageEvent.Done(output.StopReason, output.Snapshot()));
                stream.End(output.Snapshot());
            }
            catch (Exception error)
            {
                output.StopReason = signal.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = ProviderError.Format(ProviderError.Normalize(error));
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(output.StopReason, output.ErrorMessage ?? "", failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>简单入口。对应 TS <c>streamSimple</c>：budget/level 二选一，无 reasoning → 禁用。</summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var samplingParams = SimpleOptions.ResolveSamplingParams(
            model, options?.Reasoning ?? "off", options?.SamplingParams);
        int? maxTokens;
        if (options?.MaxTokens is { } requestedMaxTokens)
        {
            maxTokens = SimpleOptions.ClampMaxTokensToContext(model, context, requestedMaxTokens);
        }
        else
        {
            maxTokens = model.MaxTokens > 0
                ? SimpleOptions.ClampMaxTokensToContext(model, context, (int)model.MaxTokens)
                : null;
        }

        GoogleThinkingControl? thinking;
        if (options?.Reasoning is not { } reasoning)
        {
            thinking = new GoogleThinkingControl { Enabled = false };
        }
        else
        {
            var clamped = ThinkingLevels.Clamp(model, reasoning);
            if (clamped == "off")
            {
                thinking = new GoogleThinkingControl { Enabled = false };
            }
            else if (GoogleShared.UsesGoogleThinkingLevel(model))
            {
                thinking = new GoogleThinkingControl
                {
                    Enabled = true,
                    Level = GoogleShared.ToGoogleThinkingLevel(GoogleShared.ResolveGoogleThinkingLevel(model, clamped)),
                };
            }
            else
            {
                thinking = new GoogleThinkingControl
                {
                    Enabled = true,
                    BudgetTokens = GoogleGenerativeAi.GetGoogleBudget(
                        model, GoogleShared.ResolveGoogleThinkingLevel(model, clamped), options.ThinkingBudgets),
                };
            }
        }

        return Stream(model, context, new GoogleVertexOptions
        {
            ApiKey = options?.ApiKey,
            Headers = options?.Headers,
            Signal = options?.Signal ?? default,
            TimeoutMs = options?.TimeoutMs,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            SessionId = options?.SessionId,
            Env = options?.Env,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice is System.Text.Json.Nodes.JsonValue { } toolChoiceValue
                && toolChoiceValue.TryGetValue<string>(out var toolChoiceText) ? toolChoiceText : null,
            SamplingParams = samplingParams,
            MaxTokens = maxTokens,
            Temperature = options?.Temperature,
            Thinking = thinking,
        }, httpClient, cancellationToken);
    }

    // =============================================================================
    // 请求构建与认证
    // =============================================================================

    private static GoogleOptions? ToGenerativeOptions(GoogleVertexOptions? options)
        => options is null ? null : new GoogleOptions
        {
            OnProviderStreamEvent = options.OnProviderStreamEvent,
        };

    /// <summary>API key 解析：标记串与占位符视为未提供。对应 TS <c>resolveApiKey</c>。</summary>
    public static string? ResolveApiKey(GoogleVertexOptions? options)
    {
        var apiKey = options?.ApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey)
            || apiKey == GcpVertexCredentialsMarker
            || IsPlaceholderApiKey(apiKey))
        {
            return null;
        }
        return apiKey;
    }

    private static bool IsPlaceholderApiKey(string apiKey)
        => System.Text.RegularExpressions.Regex.IsMatch(apiKey, @"^<[^>]+$>");

    /// <summary>project 解析。对应 TS <c>resolveProject</c>。</summary>
    public static string ResolveProject(GoogleVertexOptions? options)
    {
        var project = options?.Project
            ?? ProviderEnvValue.Get("GOOGLE_CLOUD_PROJECT", options?.Env)
            ?? ProviderEnvValue.Get("GCLOUD_PROJECT", options?.Env);
        if (string.IsNullOrEmpty(project))
        {
            throw new InvalidOperationException(
                "Vertex AI requires a project ID. Set GOOGLE_CLOUD_PROJECT/GCLOUD_PROJECT or pass project in options.");
        }
        return project!;
    }

    /// <summary>location 解析。对应 TS <c>resolveLocation</c>。</summary>
    public static string ResolveLocation(GoogleVertexOptions? options)
    {
        var location = options?.Location ?? ProviderEnvValue.Get("GOOGLE_CLOUD_LOCATION", options?.Env);
        if (string.IsNullOrEmpty(location))
        {
            throw new InvalidOperationException(
                "Vertex AI requires a location. Set GOOGLE_CLOUD_LOCATION or pass location in options.");
        }
        return location!;
    }

    private static HttpRequestMessage BuildRequest(
        string url, string? apiKeyHeader, JsonObject payload,
        ModelSpec model, GoogleVertexOptions? options)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", PiUserAgent.Get());
        if (apiKeyHeader is not null) request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKeyHeader);
        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers) request.Headers.TryAddWithoutValidation(key, value);
        }
        if (options?.Headers is not null)
        {
            foreach (var (key, value) in options.Headers)
            {
                if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
            }
        }
        return request;
    }

    /// <summary>express 模式（API key）：publishers/google 端点。</summary>
    private static Task<HttpRequestMessage> BuildExpressRequestAsync(
        ModelSpec model, TranscriptContext context, GoogleVertexOptions? options,
        string apiKey, CancellationToken cancellationToken)
    {
        var baseUrl = ResolveCustomBaseUrl(model.BaseUrl) ?? DefaultEndpoint;
        var versionSuffix = BaseUrlIncludesApiVersion(baseUrl) ? "" : $"/{ApiVersion}";
        var url = $"{baseUrl.TrimEnd('/')}{versionSuffix}/publishers/google/models/{model.Id}:streamGenerateContent?alt=sse";
        var payload = GoogleGenerativeAi.BuildParams(model, context, ToGenerativeParamsOptions(options));
        var wrapped = ApplyOnPayload(options, payload, model, cancellationToken);
        return Task.FromResult(BuildRequest(url, apiKey, wrapped, model, options));
    }

    /// <summary>ADC 模式：项目级端点 + Bearer token（服务账号 JWT 或 gcloud refresh token）。</summary>
    private static async Task<HttpRequestMessage> BuildAdcRequestAsync(
        ModelSpec model, TranscriptContext context, GoogleVertexOptions? options,
        CancellationToken cancellationToken)
    {
        var project = ResolveProject(options);
        var location = ResolveLocation(options);
        var credentialsPath = ProviderEnvValue.Get("GOOGLE_APPLICATION_CREDENTIALS", options?.Env)
            ?? "~/.config/gcloud/application_default_credentials.json";
        var token = await GoogleVertexAuth.GetAccessTokenAsync(credentialsPath, cancellationToken).ConfigureAwait(false);

        var endpoint = DefaultEndpoint;
        var url = $"{endpoint}/{ApiVersion}/projects/{project}/locations/{location}"
            + $"/models/{model.Id}:streamGenerateContent?alt=sse";
        var payload = GoogleGenerativeAi.BuildParams(model, context, ToGenerativeParamsOptions(options));
        var wrapped = ApplyOnPayload(options, payload, model, cancellationToken);
        var request = BuildRequest(url, null, wrapped, model, options);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return request;
    }

    private static GoogleOptions? ToGenerativeParamsOptions(GoogleVertexOptions? options)
        => options is null ? null : new GoogleOptions
        {
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            ToolChoice = options.ToolChoice,
            Thinking = options.Thinking,
            SamplingParams = options.SamplingParams,
        };

    private static JsonObject ApplyOnPayload(
        GoogleVertexOptions? options, JsonObject payload, ModelSpec model, CancellationToken cancellationToken)
    {
        if (options?.OnPayload is { } onPayload)
        {
            var transformed = onPayload(payload, model).GetAwaiter().GetResult();
            if (transformed is not null) return transformed;
        }
        return payload;
    }

    /// <summary>自定义 baseUrl（含 {location} 模板时忽略）。对应 TS <c>resolveCustomBaseUrl</c>。</summary>
    public static string? ResolveCustomBaseUrl(string? baseUrl)
    {
        var trimmed = baseUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains("{location}")) return null;
        return trimmed;
    }

    /// <summary>baseUrl 路径段是否已含 vN 版本。对应 TS <c>baseUrlIncludesApiVersion</c>。</summary>
    public static bool BaseUrlIncludesApiVersion(string baseUrl)
        => System.Text.RegularExpressions.Regex.IsMatch(baseUrl, @"(?:^|/)v\d+(?:beta\d*)?(?:/|$)");
}

/// <summary>
/// Vertex ADC token 获取（服务账号 JSON → RS256 JWT → OAuth token 交换；
/// gcloud ADC refresh token → 授权码换 token），按凭据路径缓存。
/// 对应 TS 的 SDK googleAuthOptions 行为（keyFilename / ADC）。
/// </summary>
public static class GoogleVertexAuth
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Token, long ExpiresAt)> TokenCache = new(StringComparer.Ordinal);

    /// <summary>取（或缓存）访问令牌。</summary>
    public static async Task<string> GetAccessTokenAsync(string credentialsPath, CancellationToken cancellationToken = default)
    {
        var expanded = ExpandPath(credentialsPath);
        lock (Gate)
        {
            if (TokenCache.TryGetValue(expanded, out var cached) && cached.ExpiresAt > DateTimeOffset.Now.ToUnixTimeMilliseconds() + 60_000)
            {
                return cached.Token;
            }
        }

        var json = await File.ReadAllTextAsync(expanded, cancellationToken).ConfigureAwait(false);
        var credentials = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"Invalid Google credentials file: {credentialsPath}");

        string token;
        if (credentials.Str("type") == "service_account")
        {
            token = await ServiceAccountTokenAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        else if (credentials.Str("refresh_token") is { Length: > 0 } refreshToken)
        {
            token = await RefreshTokenAsync(credentials, refreshToken, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported Google credentials format in {credentialsPath} (expected service_account or user refresh token)");
        }

        lock (Gate)
        {
            // token 过期时间由响应给出；未知时按 50 分钟保守缓存。
            TokenCache[expanded] = (token, DateTimeOffset.Now.ToUnixTimeMilliseconds() + 50 * 60_000);
        }
        return token;
    }

    /// <summary>清空 token 缓存（测试用）。</summary>
    public static void Reset() => TokenCache.Clear();

    private static async Task<string> ServiceAccountTokenAsync(JsonObject credentials, CancellationToken cancellationToken)
    {
        var clientEmail = credentials.Str("client_email")
            ?? throw new InvalidOperationException("service account missing client_email");
        var privateKeyPem = credentials.Str("private_key")
            ?? throw new InvalidOperationException("service account missing private_key");
        var tokenUri = credentials.Str("token_uri") ?? "https://oauth2.googleapis.com/token";
        var scope = "https://www.googleapis.com/auth/cloud-platform";

        var iat = DateTimeOffset.Now.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = clientEmail,
            ["scope"] = scope,
            ["aud"] = tokenUri,
            ["iat"] = iat,
            ["exp"] = iat + 3600,
        };

        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var unsigned = $"{header}.{payload}";
        var signature = Base64Url(rsa.SignData(
            Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var assertion = $"{unsigned}.{signature}";

        return await ExchangeTokenAsync(tokenUri, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RefreshTokenAsync(
        JsonObject credentials, string refreshToken, CancellationToken cancellationToken)
    {
        var clientId = credentials.Str("client_id")
            ?? throw new InvalidOperationException("ADC credentials missing client_id");
        var clientSecret = credentials.Str("client_secret")
            ?? throw new InvalidOperationException("ADC credentials missing client_secret");
        var tokenUri = credentials.Str("token_uri") ?? "https://oauth2.googleapis.com/token";
        return await ExchangeTokenAsync(tokenUri, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExchangeTokenAsync(
        string tokenUri, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(tokenUri, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google token exchange failed ({(int)response.StatusCode}): {body}");
        }
        var token = (JsonNode.Parse(body) as JsonObject)?.Str("access_token")
            ?? throw new InvalidOperationException("Google token exchange returned no access_token");
        return token;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }
        return path;
    }
}
