using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth.OAuth;

/// <summary>
/// GitHub Copilot OAuth 流。对应 TS auth/oauth/github-copilot.ts：
/// GitHub 设备流取 access token → copilot_internal token 交换 → 模型目录
/// 解析（429 限速重试 + Individual 端点策略回退）→ 未配置模型 best-effort 启用。
/// </summary>
public sealed class GitHubCopilotOAuth(HttpClient? httpClient = null,
    IReadOnlySet<string>? knownChatModelIds = null) : IOAuthAuth
{
    // atob("SXYxLmI1MDdhMDhjODdlY2ZlOTg=")，保持与源码相同的编码形态。
    private const string ClientId = "Iv1.b507a08c87ecfe98";

    private static readonly IReadOnlyDictionary<string, string> CopilotHeaders = new Dictionary<string, string>
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat",
    };
    private const string CopilotApiVersion = "2026-06-01";

    private readonly HttpClient _client = httpClient ?? OAuthHttp.Shared;
    private readonly IReadOnlySet<string> _knownChatModelIds = knownChatModelIds ?? LoadKnownChatModelIds();

    /// <summary>时钟（测试注入）。TS 用 Date.now()。</summary>
    public Func<long> Now { get; set; } = static () => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    /// <summary>休眠实现（测试注入）。对应 TS sleep。</summary>
    internal Func<int, CancellationToken, Task> SleepAsync { get; init; } = Sleep.DelayAsync;

    public string Name => "GitHub Copilot";

    public bool IsSubscription => true;

    public string? LoginLabel => null;

    public async Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
        LoginOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = await interaction.PromptAsync(
            new AuthPrompt.Text(
                "GitHub Enterprise URL/domain (blank for github.com)",
                "company.ghe.com"), cancellationToken).ConfigureAwait(false);
        if (interaction.Signal.IsCancellationRequested)
            throw new OperationCanceledException("Login cancelled", interaction.Signal);

        var trimmed = input.Trim();
        var enterpriseDomain = NormalizeDomain(input);
        if (trimmed.Length > 0 && enterpriseDomain is null)
            throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain");
        var domain = enterpriseDomain ?? "github.com";

        var device = await StartDeviceFlowAsync(domain, cancellationToken).ConfigureAwait(false);
        interaction.Notify(new AuthEvent.DeviceCode(
            device.UserCode, device.VerificationUri, device.Interval, device.ExpiresIn));

        var githubAccessToken = await PollForGitHubAccessTokenAsync(domain, device, cancellationToken).ConfigureAwait(false);
        var credentials = await RefreshCopilotAccessTokenAsync(
            githubAccessToken, enterpriseDomain, cancellationToken).ConfigureAwait(false);
        var models = await FetchGitHubCopilotModelCatalogAsync(
            credentials.Access, enterpriseDomain, cancellationToken,
            new RetryPolicy { MaxRetries = 2, MaxElapsedMs = 5000 }).ConfigureAwait(false);
        var enabledModelIds = new List<string>();
        if (models.PolicyModelIds.Count > 0)
        {
            interaction.Notify(new AuthEvent.Progress("Enabling models..."));
            enabledModelIds = await EnableModelsAsync(
                credentials.Access, models.PolicyModelIds, enterpriseDomain, cancellationToken).ConfigureAwait(false);
        }
        credentials.AvailableModelIds = models.AvailableModelIds
            .Concat(enabledModelIds)
            .Distinct()
            .ToList();
        return credentials;
    }

    public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
        CancellationToken cancellationToken = default)
    {
        var credentials = await RefreshCopilotAccessTokenAsync(
            credential.Refresh, CopilotEnterpriseDomain(credential), cancellationToken).ConfigureAwait(false);
        var models = await FetchGitHubCopilotModelCatalogAsync(
            credentials.Access, CopilotEnterpriseDomain(credential), cancellationToken,
            new RetryPolicy { MaxRetries = 0, MaxElapsedMs = 0 }).ConfigureAwait(false);
        credentials.AvailableModelIds = models.AvailableModelIds;
        return credentials;
    }

    /// <summary>派生每次请求的代理端点。对应 TS toAuth。</summary>
    public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelAuth
        {
            ApiKey = credential.Access,
            BaseUrl = GetCopilotBaseUrl(credential.Access, CopilotEnterpriseDomain(credential)),
        });

    /// <summary>429 限速重试预算。对应 TS <c>retryPolicy: { maxRetries, maxElapsedMs }</c>。</summary>
    public sealed record RetryPolicy
    {
        public int MaxRetries { get; init; }

        public int MaxElapsedMs { get; init; }
    }

    /// <summary>模型目录解析结果。对应 TS <c>parseGitHubCopilotModelCatalog</c> 的返回。</summary>
    public sealed record CopilotModelCatalog(
        IReadOnlyList<string> AvailableModelIds,
        IReadOnlyList<string> PolicyModelIds);

    private static string? NormalizeDomain(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        try
        {
            var url = trimmed.Contains("://")
                ? new Uri(trimmed)
                : new Uri($"https://{trimmed}");
            return url.Host;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static (string DeviceCodeUrl, string AccessTokenUrl, string CopilotTokenUrl) GetUrls(string domain)
        => (
            $"https://{domain}/login/device/code",
            $"https://{domain}/login/oauth/access_token",
            $"https://api.{domain}/copilot_internal/v2/token");

    /// <summary>
    /// 从 Copilot token 解析 proxy-ep 并转成 API base URL：
    /// tid=...;exp=...;proxy-ep=proxy.individual.githubcopilot.com;...
    /// → https://api.individual.githubcopilot.com
    /// </summary>
    private static string? GetBaseUrlFromToken(string token)
    {
        var match = System.Text.RegularExpressions.Regex.Match(token, @"proxy-ep=([^;]+)");
        if (!match.Success) return null;
        // proxy.xxx → api.xxx
        var apiHost = System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"^proxy\.", "api.");
        return $"https://{apiHost}";
    }

    private static string GetCopilotBaseUrl(string? token, string? enterpriseDomain)
    {
        // 有 token 时从 proxy-ep 提取 base URL。
        if (token is not null && GetBaseUrlFromToken(token) is { } fromToken) return fromToken;
        // 企业域或 token 解析失败的回退。
        if (enterpriseDomain is not null) return $"https://copilot-api.{enterpriseDomain}";
        return "https://api.individual.githubcopilot.com";
    }

    private static string? CopilotEnterpriseDomain(Credential.OAuth credential)
    {
        if (credential.EnterpriseUrl is not { Length: > 0 } enterpriseUrl) return null;
        return NormalizeDomain(enterpriseUrl);
    }

    /// <summary>
    /// 已知 chat 模型 id 表（TS 的 GITHUB_COPILOT_MODELS 键集）。生成的目录数据
    /// 以嵌入资源 <c>Pi.Ai.ModelData.github-copilot.json</c> 提供；缺省时为空集。
    /// </summary>
    private static IReadOnlySet<string> LoadKnownChatModelIds()
    {
        try
        {
            using var stream = typeof(GitHubCopilotOAuth).Assembly
                .GetManifestResourceStream("Pi.Ai.ModelData.github-copilot.json");
            if (stream is null) return new HashSet<string>();
            var groups = JsonNode.Parse(stream) as JsonObject;
            return new HashSet<string>(ModelCatalog.Load("github-copilot", groups ?? []).ChatModels.Keys);
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private CopilotModelCatalog ParseModelCatalog(JsonObject raw, bool allowPolicyFallback)
    {
        if (raw["data"] is not JsonArray dataArray)
        {
            throw new InvalidOperationException("Invalid Copilot models response");
        }

        var accountModels = new List<(string Id, bool PickerEnabled, string? PolicyState)>();
        foreach (var node in dataArray)
        {
            if (node is not JsonObject item) continue;
            if (item.Str("id") is not { Length: > 0 } id) continue;

            var supports = item.Obj("capabilities")?.Obj("supports");
            if (supports?.Bool("tool_calls") is false) continue;

            accountModels.Add((id,
                item.Bool("model_picker_enabled") ?? false,
                item.Obj("policy")?.Str("state")));
        }
        var pickerModelIds = accountModels
            .Where(model => model.PickerEnabled && model.PolicyState != "disabled")
            .Select(model => model.Id)
            .ToList();
        var usePolicyFallback = allowPolicyFallback && pickerModelIds.Count == 0;
        var availableModelIds =
            pickerModelIds.Count > 0 || !allowPolicyFallback
                ? pickerModelIds
                : accountModels.Where(model => model.PolicyState == "enabled").Select(model => model.Id).ToList();
        var policyModelIds = accountModels
            .Where(model => model.PolicyState == "unconfigured"
                && _knownChatModelIds.Contains(model.Id)
                && (model.PickerEnabled || usePolicyFallback))
            .Select(model => model.Id)
            .ToList();
        return new CopilotModelCatalog(availableModelIds, policyModelIds);
    }

    /// <summary>429 重试（retry-after 优先，指数退避兜底，受预算约束）。对应 TS <c>fetchWithRateLimitRetry</c>。</summary>
    private async Task<HttpResponseMessage> FetchWithRateLimitRetryAsync(
        string url, Func<HttpRequestMessage> requestFactory, CancellationToken signal, RetryPolicy retryPolicy)
    {
        using var budgetCts = retryPolicy is { MaxRetries: > 0, MaxElapsedMs: > 0 }
            ? new CancellationTokenSource(retryPolicy.MaxElapsedMs)
            : null;
        using var linked = budgetCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(signal, budgetCts.Token)
            : null;
        var requestSignal = linked?.Token ?? signal;
        var retryDeadline = budgetCts is not null ? Now() + retryPolicy.MaxElapsedMs : (long?)null;

        for (var retry = 0; ; retry++)
        {
            // HttpRequestMessage 不可复用，每次重试新建。
            using var perRequestCts = CancellationTokenSource.CreateLinkedTokenSource(requestSignal);
            perRequestCts.CancelAfter(5000);
            var response = await _client.SendAsync(requestFactory(), perRequestCts.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 429 || retry == retryPolicy.MaxRetries) return response;

            var retryAfter = response.Headers.RetryAfter;
            var delayMs = 500 * (int)Math.Pow(2, retry);
            if (retryAfter is not null)
            {
                if (retryAfter.Delta is { } delta)
                {
                    delayMs = (int)delta.TotalMilliseconds;
                }
                else if (retryAfter.Date is { } date)
                {
                    delayMs = (int)((date - DateTimeOffset.Now).TotalMilliseconds);
                    if (!double.IsFinite(delayMs)) return response;
                }
            }
            delayMs = Math.Max(0, delayMs);
            if (retryDeadline is { } deadline && delayMs >= deadline - Now()) return response;
            response.Dispose();
            await SleepAsync(delayMs, requestSignal).ConfigureAwait(false);
        }
    }

    private async Task<CopilotModelCatalog> FetchGitHubCopilotModelCatalogAsync(
        string copilotToken, string? enterpriseDomain, CancellationToken signal, RetryPolicy retryPolicy)
    {
        var baseUrl = GetCopilotBaseUrl(copilotToken, enterpriseDomain);
        // 部分 Individual 账号对所有 picker 标志都返回 false，尽管显式启用了策略。
        // 回退仅限该端点，其他账号类型保持严格的 picker 语义。
        var allowPolicyFallback = baseUrl == "https://api.individual.githubcopilot.com";
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", copilotToken);
            foreach (var (key, value) in CopilotHeaders) request.Headers.Add(key, value);
            request.Headers.Add("X-GitHub-Api-Version", CopilotApiVersion);
            return request;
        }
        var response = await FetchWithRateLimitRetryAsync($"{baseUrl}/models", Request, signal, retryPolicy)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false)}");
        }
        var raw = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Invalid Copilot models response");
        return ParseModelCatalog(raw, allowPolicyFallback);
    }

    private static async Task<JsonObject> FetchJsonAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken signal)
    {
        var response = await client.SendAsync(request, signal).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false);
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {text}");
        }
        var json = await OAuthHttp.ReadObjectAsync(response).ConfigureAwait(false);
        return json ?? throw new InvalidOperationException("Expected JSON object response");
    }

    private static HttpRequestMessage CopilotRequest(HttpMethod method, string url, string? jsonBody)
    {
        var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private sealed record DeviceCodeResponse(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        int? Interval,
        int ExpiresIn);

    private async Task<DeviceCodeResponse> StartDeviceFlowAsync(string domain, CancellationToken signal)
    {
        var urls = GetUrls(domain);
        var request = CopilotRequest(HttpMethod.Post, urls.DeviceCodeUrl, null);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content ??= new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user",
        });
        request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0");
        var data = await FetchJsonAsync(_client, request, signal).ConfigureAwait(false);

        if (data.Str("device_code") is not { Length: > 0 } deviceCode
            || data.Str("user_code") is not { Length: > 0 } userCode
            || data.Str("verification_uri") is not { Length: > 0 } verificationUri
            || data.Num("expires_in") is not { } expiresIn
            || (data.Has("interval") && data.Num("interval") is null))
        {
            throw new InvalidOperationException("Invalid device code response fields");
        }

        // 验证 URI 会在用户浏览器打开；强制是 URL，防止 `open` 打开可执行文件等。
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var parsedUri)
            || parsedUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("Untrusted verification_uri in device code response");
        }

        return new DeviceCodeResponse(
            deviceCode, userCode, parsedUri.ToString(),
            data.PositiveInt("interval"), (int)expiresIn);
    }

    private async Task<string> PollForGitHubAccessTokenAsync(
        string domain, DeviceCodeResponse device, CancellationToken signal)
    {
        var urls = GetUrls(domain);
        return await DeviceCodeFlow.PollAsync(new DeviceCodePollOptions<string>
        {
            IntervalSeconds = device.Interval,
            ExpiresInSeconds = device.ExpiresIn,
            WaitBeforeFirstPoll = true,
            Signal = signal,
            Poll = async () =>
            {
                var request = CopilotRequest(HttpMethod.Post, urls.AccessTokenUrl, null);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                });
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.35.0");
                var raw = await FetchJsonAsync(_client, request, signal).ConfigureAwait(false);

                if (raw.Str("access_token") is { Length: > 0 } accessToken)
                {
                    return new DeviceCodePollResult<string>.Complete(accessToken);
                }

                if (raw.Str("error") is { Length: > 0 } error)
                {
                    var interval = raw.Num("interval");
                    if (error == "authorization_pending")
                    {
                        return new DeviceCodePollResult<string>.Pending();
                    }
                    if (error == "slow_down")
                    {
                        return new DeviceCodePollResult<string>.SlowDown(
                            interval is { } value && !double.IsNaN(value) ? (int)value : null);
                    }
                    var descriptionSuffix = raw.Str("error_description") is { Length: > 0 } description
                        ? $": {description}" : "";
                    return new DeviceCodePollResult<string>.Failed($"Device flow failed: {error}{descriptionSuffix}");
                }

                return new DeviceCodePollResult<string>.Failed("Invalid device token response");
            },
        }).ConfigureAwait(false);
    }

    private async Task<Credential.OAuth> RefreshCopilotAccessTokenAsync(
        string refreshToken, string? enterpriseDomain, CancellationToken signal)
    {
        var domain = enterpriseDomain ?? "github.com";
        var urls = GetUrls(domain);

        var request = CopilotRequest(HttpMethod.Get, urls.CopilotTokenUrl, null);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshToken);
        foreach (var (key, value) in CopilotHeaders) request.Headers.Add(key, value);
        var raw = await FetchJsonAsync(_client, request, signal).ConfigureAwait(false);

        if (raw.Str("token") is not { Length: > 0 } token || raw.Num("expires_at") is not { } expiresAt)
        {
            throw new InvalidOperationException("Invalid Copilot token response fields");
        }

        return new Credential.OAuth(refreshToken, token, (long)(expiresAt * 1000) - 5 * 60 * 1000)
        {
            EnterpriseUrl = enterpriseDomain,
        };
    }

    /// <summary>
    /// 为用户的 GitHub Copilot 账户启用一个模型（Claude/Grok 等模型需要先启用）。
    /// </summary>
    private async Task<bool> EnableModelAsync(
        string token, string modelId, string? enterpriseDomain, CancellationToken signal)
    {
        var baseUrl = GetCopilotBaseUrl(token, enterpriseDomain);
        var url = $"{baseUrl}/models/{modelId}/policy";

        HttpRequestMessage Request()
        {
            var request = CopilotRequest(HttpMethod.Post, url, new JsonObject { ["state"] = "enabled" }.ToJsonString());
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            foreach (var (key, value) in CopilotHeaders) request.Headers.Add(key, value);
            request.Headers.Add("openai-intent", "chat-policy");
            request.Headers.Add("x-interaction-type", "chat-policy");
            return request;
        }

        HttpResponseMessage response;
        try
        {
            response = await FetchWithRateLimitRetryAsync(url, Request, signal,
                new RetryPolicy { MaxRetries = 2, MaxElapsedMs = 5000 }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (signal.IsCancellationRequested) throw;
            _ = error;
            return false;
        }
        if ((int)response.StatusCode == 429)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {await OAuthHttp.ReadTextAsync(response).ConfigureAwait(false)}");
        }
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// 启用请求的模型并返回成功的 id。策略更新是 best-effort；限速耗尽会中止批量。
    /// 对应 TS <c>enableGitHubCopilotModels</c>。
    /// </summary>
    private async Task<List<string>> EnableModelsAsync(
        string token, IReadOnlyList<string> modelIds, string? enterpriseDomain, CancellationToken signal)
    {
        var enabledModelIds = new List<string>();
        foreach (var modelId in modelIds)
        {
            try
            {
                if (await EnableModelAsync(token, modelId, enterpriseDomain, signal).ConfigureAwait(false))
                {
                    enabledModelIds.Add(modelId);
                }
            }
            catch (Exception error)
            {
                if (signal.IsCancellationRequested) throw;
                _ = error;
                break;
            }
        }
        return enabledModelIds;
    }
}
