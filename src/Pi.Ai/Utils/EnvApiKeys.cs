namespace Pi.Ai.Utils;

/// <summary>
/// Anthropic 相关环境变量名。对应 TS <c>env-api-keys.ts</c> 的常量组。
/// </summary>
public static class AnthropicEnv
{
    public const string AuthToken = "ANTHROPIC_AUTH_TOKEN";
    public const string OAuthToken = "ANTHROPIC_OAUTH_TOKEN";
    public const string ApiKey = "ANTHROPIC_API_KEY";
    public const string FederationRuleId = "ANTHROPIC_FEDERATION_RULE_ID";
    public const string OrganizationId = "ANTHROPIC_ORGANIZATION_ID";
    public const string ServiceAccountId = "ANTHROPIC_SERVICE_ACCOUNT_ID";
    public const string IdentityTokenFile = "ANTHROPIC_IDENTITY_TOKEN_FILE";
    public const string WorkspaceId = "ANTHROPIC_WORKSPACE_ID";
}

/// <summary>
/// 环境变量密钥发现。对应 TS <c>env-api-keys.ts</c>：<c>findEnvKeys</c> 只报告真正的
/// API key 变量，刻意排除 ambient 凭据源（AWS profile / IAM / Google ADC）；
/// <c>getEnvApiKey</c> 额外对 Vertex / Bedrock 报告 <c>&lt;authenticated&gt;</c> 环境标记。
/// </summary>
public static class EnvApiKeys
{
    /// <summary>已认证环境标记（无法从环境变量取到具体密钥，但凭据链完整）。对应 TS <c>AMBIENT_AUTH_MARKER</c>。</summary>
    public const string AmbientAuthMarker = "<authenticated>";

    private static readonly object Gate = new();
    private static bool? _cachedVertexAdcCredentialsExists;

    /// <summary>清空 Vertex ADC 存在性缓存（测试注入用）。</summary>
    public static void ResetVertexAdcCache()
    {
        lock (Gate) _cachedVertexAdcCredentialsExists = null;
    }

    /// <summary>某 provider 的 API key 环境变量候选。对应 TS <c>getApiKeyEnvVars</c>。</summary>
    public static IReadOnlyList<string>? GetApiKeyEnvVars(string provider)
    {
        if (provider == "github-copilot") return ["COPILOT_GITHUB_TOKEN"];

        // ANTHROPIC_AUTH_TOKEN 参与环境发现/状态展示，但 getEnvApiKey() 会跳过它，
        // 因为请求必须以 Authorization: Bearer 传递。
        if (provider == "anthropic")
        {
            return [AnthropicEnv.AuthToken, AnthropicEnv.OAuthToken, AnthropicEnv.ApiKey];
        }

        var envVar = ProviderEnvMap.GetValueOrDefault(provider);
        return envVar is null ? null : [envVar];
    }

    /// <summary>provider → 单一 API key 环境变量映射。对应 TS <c>envMap</c>。</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderEnvMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ant-ling"] = "ANT_LING_API_KEY",
            ["qwen-token-plan"] = "QWEN_TOKEN_PLAN_API_KEY",
            ["qwen-token-plan-cn"] = "QWEN_TOKEN_PLAN_CN_API_KEY",
            ["qwen-token-plan-individual"] = "QWEN_TOKEN_PLAN_API_KEY",
            ["openai"] = "OPENAI_API_KEY",
            ["azure-openai-responses"] = "AZURE_OPENAI_API_KEY",
            ["nvidia"] = "NVIDIA_API_KEY",
            ["deepseek"] = "DEEPSEEK_API_KEY",
            ["google"] = "GEMINI_API_KEY",
            ["google-vertex"] = "GOOGLE_CLOUD_API_KEY",
            ["groq"] = "GROQ_API_KEY",
            ["cerebras"] = "CEREBRAS_API_KEY",
            ["xai"] = "XAI_API_KEY",
            ["typesafe"] = "TYPESAFE_API_KEY",
            ["radius"] = "RADIUS_API_KEY",
            ["openrouter"] = "OPENROUTER_API_KEY",
            ["vercel-ai-gateway"] = "AI_GATEWAY_API_KEY",
            ["zai"] = "ZAI_API_KEY",
            ["zai-coding-cn"] = "ZAI_CODING_CN_API_KEY",
            ["mistral"] = "MISTRAL_API_KEY",
            ["minimax"] = "MINIMAX_API_KEY",
            ["minimax-cn"] = "MINIMAX_CN_API_KEY",
            ["moonshotai"] = "MOONSHOT_API_KEY",
            ["moonshotai-cn"] = "MOONSHOT_API_KEY",
            ["huggingface"] = "HF_TOKEN",
            ["fireworks"] = "FIREWORKS_API_KEY",
            ["together"] = "TOGETHER_API_KEY",
            ["baseten"] = "BASETEN_API_KEY",
            ["opencode"] = "OPENCODE_API_KEY",
            ["opencode-go"] = "OPENCODE_API_KEY",
            ["kimi-coding"] = "KIMI_API_KEY",
            ["meta"] = "META_API_KEY",
            ["cloudflare-workers-ai"] = "CLOUDFLARE_API_KEY",
            ["cloudflare-ai-gateway"] = "CLOUDFLARE_API_KEY",
            ["xiaomi"] = "XIAOMI_API_KEY",
            ["xiaomi-token-plan-cn"] = "XIAOMI_TOKEN_PLAN_CN_API_KEY",
            ["xiaomi-token-plan-ams"] = "XIAOMI_TOKEN_PLAN_AMS_API_KEY",
            ["xiaomi-token-plan-sgp"] = "XIAOMI_TOKEN_PLAN_SGP_API_KEY",
        };

    /// <summary>
    /// 查找已配置的 API key 环境变量。只报告真正的 key 变量，不含 ambient 凭据源。
    /// 对应 TS <c>findEnvKeys</c>。
    /// </summary>
    public static IReadOnlyList<string>? FindEnvKeys(string provider, IReadOnlyDictionary<string, string>? env = null)
    {
        var envVars = GetApiKeyEnvVars(provider);
        if (envVars is null) return null;
        var found = envVars.Where(envVar => !string.IsNullOrEmpty(ProviderEnvValue.Get(envVar, env))).ToList();
        return found.Count > 0 ? found : null;
    }

    /// <summary>
    /// 从已知环境变量取 provider 的 API key。需要 OAuth 令牌的 provider 不返回密钥。
    /// 对应 TS <c>getEnvApiKey</c>。
    /// </summary>
    public static string? GetEnvApiKey(string provider, IReadOnlyDictionary<string, string>? env = null)
    {
        var envKeys = FindEnvKeys(provider, env);
        if (envKeys is { Count: > 0 })
        {
            var apiKeyEnv = provider == "anthropic"
                ? envKeys.FirstOrDefault(key => key != AnthropicEnv.AuthToken)
                : envKeys[0];
            if (apiKeyEnv is not null) return ProviderEnvValue.Get(apiKeyEnv, env);
        }

        // Vertex AI 支持显式 API key 或 Application Default Credentials。
        if (provider == "google-vertex")
        {
            var hasCredentials = HasVertexAdcCredentials(env);
            var hasProject = !string.IsNullOrEmpty(ProviderEnvValue.Get("GOOGLE_CLOUD_PROJECT", env))
                || !string.IsNullOrEmpty(ProviderEnvValue.Get("GCLOUD_PROJECT", env));
            var hasLocation = !string.IsNullOrEmpty(ProviderEnvValue.Get("GOOGLE_CLOUD_LOCATION", env));
            if (hasCredentials && hasProject && hasLocation) return AmbientAuthMarker;
        }

        if (provider == "amazon-bedrock")
        {
            // AWS_PROFILE / IAM 键 / bearer token / ECS 任务角色 / IRSA 任一即可。
            if (!string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_PROFILE", env))
                || (!string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_ACCESS_KEY_ID", env))
                    && !string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_SECRET_ACCESS_KEY", env)))
                || !string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_BEARER_TOKEN_BEDROCK", env))
                || !string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", env))
                || !string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_CONTAINER_CREDENTIALS_FULL_URI", env))
                || !string.IsNullOrEmpty(ProviderEnvValue.Get("AWS_WEB_IDENTITY_TOKEN_FILE", env)))
            {
                return AmbientAuthMarker;
            }
        }

        return null;
    }

    /// <summary>
    /// Vertex Application Default Credentials 是否存在（显式路径优先，否则默认 gcloud 路径）。
    /// 对应 TS <c>hasVertexAdcCredentials</c>；<paramref name="env"/> 提供作用域覆盖。
    /// </summary>
    public static bool HasVertexAdcCredentials(IReadOnlyDictionary<string, string>? env = null)
    {
        var explicitPath = env is not null && env.TryGetValue("GOOGLE_APPLICATION_CREDENTIALS", out var scoped)
            && scoped.Length > 0
            ? scoped
            : null;
        if (explicitPath is not null) return File.Exists(explicitPath);

        lock (Gate)
        {
            if (_cachedVertexAdcCredentialsExists is not null) return _cachedVertexAdcCredentialsExists.Value;

            var configured = ProviderEnvValue.Get("GOOGLE_APPLICATION_CREDENTIALS", env);
            var path = configured is { Length: > 0 }
                ? configured
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "gcloud", "application_default_credentials.json");
            _cachedVertexAdcCredentialsExists = File.Exists(path);
            return _cachedVertexAdcCredentialsExists.Value;
        }
    }
}
