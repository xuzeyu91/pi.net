using Pi.Ai.Auth;
using Pi.Ai.Providers;
using Pi.Chord.Models;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>OpenAI 兼容 provider 批量注册与认证解析测试。</summary>
public class ProviderRegistryTests
{
    [Fact]
    public void RegistryHasAllCompatibleProviders()
    {
        Assert.Contains(ProviderRegistry.OpenAiCompatibleProviders, p => p.Id == "deepseek" && p.BaseUrl == "https://api.deepseek.com");
        Assert.Contains(ProviderRegistry.OpenAiCompatibleProviders, p => p.Id == "groq");
        Assert.Contains(ProviderRegistry.OpenAiCompatibleProviders, p => p.Id == "moonshotai-cn");
        Assert.True(ProviderRegistry.OpenAiCompatibleProviders.Count >= 10);
    }

    [Fact]
    public async Task CreateBindsCatalogAndAuth()
    {
        var deepseek = ProviderRegistry.Create("deepseek");
        Assert.Equal("DeepSeek", deepseek.Name);
        // 嵌入资源里有 deepseek 目录。
        Assert.Equal(2, deepseek.GetModels().Count);
        Assert.Contains(deepseek.GetModels(), m => m.Id == "deepseek-reasoner" && m.Reasoning);

        // 认证解析：存储凭据优先。
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("deepseek",
            _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored-ds")));
        var resolved = await deepseek.ApplyAuthAsync(null, store, new FakeAuthContext(new Dictionary<string, string>()));
        Assert.Equal("stored-ds", resolved.ApiKey);
        Assert.Equal("https://api.deepseek.com", resolved.BaseUrl);
    }

    [Fact]
    public async Task AuthFallsBackToEnvVars()
    {
        var provider = ProviderRegistry.Create("groq");
        var store = new InMemoryCredentialStore();
        var resolved = await provider.ApplyAuthAsync(null, store,
            new FakeAuthContext(new Dictionary<string, string> { ["GROQ_API_KEY"] = "env-groq" }));
        Assert.Equal("env-groq", resolved.ApiKey);
    }

    [Fact]
    public async Task UnconfiguredProviderFailsAuth()
    {
        var provider = ProviderRegistry.Create("xai");
        var store = new InMemoryCredentialStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ApplyAuthAsync(null, store, new FakeAuthContext(new Dictionary<string, string>())));
    }

    [Fact]
    public void RegisterAllPopulatesModels()
    {
        var models = new Models();
        ProviderRegistry.RegisterAll(models);
        Assert.True(models.GetProviders().Count >= 10);
        // deepseek 目录可用。
        Assert.NotNull(models.GetModel("deepseek", "deepseek-chat"));
    }

    private sealed class FakeAuthContext(IReadOnlyDictionary<string, string> values) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(values.GetValueOrDefault(name));

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
