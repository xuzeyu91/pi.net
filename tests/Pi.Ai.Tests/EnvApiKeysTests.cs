using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>环境变量密钥发现测试（env-api-keys.ts）。</summary>
public class EnvApiKeysTests
{
    private static IReadOnlyDictionary<string, string> Env(params (string Key, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    [Fact]
    public void FindEnvKeysReportsOnlyApiKeyVariables()
    {
        Assert.Equal(new[] { "DEEPSEEK_API_KEY" }, EnvApiKeys.FindEnvKeys("deepseek", Env(("DEEPSEEK_API_KEY", "d"))));
        Assert.Equal(new[] { "COPILOT_GITHUB_TOKEN" },
            EnvApiKeys.FindEnvKeys("github-copilot", Env(("COPILOT_GITHUB_TOKEN", "t"))));
        // ambient 凭据源（AWS profile / ADC）不参与 key 发现。
        Assert.Null(EnvApiKeys.FindEnvKeys("amazon-bedrock", Env(("AWS_PROFILE", "p"))));
        Assert.Null(EnvApiKeys.FindEnvKeys("google-vertex", Env(("GOOGLE_CLOUD_PROJECT", "x"))));
        // 未知 provider 无候选。
        Assert.Null(EnvApiKeys.FindEnvKeys("nope-provider", Env(("ANY", "x"))));
    }

    [Fact]
    public void AnthropicSkipsAuthTokenForRequestKey()
    {
        // 发现列表包含 ANTHROPIC_AUTH_TOKEN（用于状态展示）。
        var keys = EnvApiKeys.FindEnvKeys("anthropic",
            Env((AnthropicEnv.AuthToken, "bearer"), (AnthropicEnv.ApiKey, "k")));
        Assert.Equal(new[] { AnthropicEnv.AuthToken, AnthropicEnv.ApiKey }, keys);

        // getEnvApiKey 跳过 AUTH_TOKEN（必须以 Authorization: Bearer 传递）。
        var key = EnvApiKeys.GetEnvApiKey("anthropic",
            Env((AnthropicEnv.AuthToken, "bearer"), (AnthropicEnv.ApiKey, "k")));
        Assert.Equal("k", key);

        // 只有 AUTH_TOKEN → 无 API key。
        Assert.Null(EnvApiKeys.GetEnvApiKey("anthropic", Env((AnthropicEnv.AuthToken, "bearer"))));
    }

    [Fact]
    public void AmbientCredentialChainsReportAuthenticatedMarker()
    {
        Assert.Equal(EnvApiKeys.AmbientAuthMarker,
            EnvApiKeys.GetEnvApiKey("amazon-bedrock", Env(("AWS_PROFILE", "p"))));
        Assert.Equal(EnvApiKeys.AmbientAuthMarker,
            EnvApiKeys.GetEnvApiKey("amazon-bedrock",
                Env(("AWS_ACCESS_KEY_ID", "id"), ("AWS_SECRET_ACCESS_KEY", "secret"))));
        Assert.Equal(EnvApiKeys.AmbientAuthMarker,
            EnvApiKeys.GetEnvApiKey("amazon-bedrock", Env(("AWS_BEARER_TOKEN_BEDROCK", "tok"))));
        Assert.Equal(EnvApiKeys.AmbientAuthMarker,
            EnvApiKeys.GetEnvApiKey("amazon-bedrock", Env(("AWS_WEB_IDENTITY_TOKEN_FILE", "/tmp/tok"))));

        // 只有一半 AWS 键 → 不算已认证。
        Assert.Null(EnvApiKeys.GetEnvApiKey("amazon-bedrock", Env(("AWS_ACCESS_KEY_ID", "id"))));
    }

    [Fact]
    public void VertexPrefersExplicitApiKeyOverAdc()
    {
        Assert.Equal("gk", EnvApiKeys.GetEnvApiKey("google-vertex", Env(("GOOGLE_CLOUD_API_KEY", "gk"))));
        // 无 key 且无 ADC（测试环境通常不存在默认 gcloud 凭据）→ null 或 marker，
        // 两者都不应抛错。
        var adc = EnvApiKeys.GetEnvApiKey("google-vertex",
            Env(("GOOGLE_CLOUD_PROJECT", "p"), ("GOOGLE_CLOUD_LOCATION", "us-central1")));
        Assert.True(adc is null || adc == EnvApiKeys.AmbientAuthMarker);
    }

    [Fact]
    public void EnvMapCoversEveryBuiltinProviderWithEnvVar()
    {
        // 有环境变量的内建家都在映射表里（Bedrock/Vertex 走 ambient 链，不入表）。
        Assert.Equal("MOONSHOT_API_KEY", EnvApiKeys.ProviderEnvMap["moonshotai-cn"]);
        Assert.Equal("QWEN_TOKEN_PLAN_API_KEY", EnvApiKeys.ProviderEnvMap["qwen-token-plan-individual"]);
        Assert.Equal("CLOUDFLARE_API_KEY", EnvApiKeys.ProviderEnvMap["cloudflare-ai-gateway"]);
        // Vertex 有显式 key 变量（ADC 另走 ambient 链）；Bedrock 完全靠 ambient 链。
        Assert.Equal("GOOGLE_CLOUD_API_KEY", EnvApiKeys.ProviderEnvMap["google-vertex"]);
        Assert.False(EnvApiKeys.ProviderEnvMap.ContainsKey("amazon-bedrock"));
    }
}
