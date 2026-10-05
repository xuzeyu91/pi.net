using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>各家 provider 工厂（providers/&lt;name&gt;.ts）的目录绑定与认证解析测试。</summary>
public class ProviderFactoriesTests
{
    private static readonly IAuthContext EmptyEnv = new FakeAuthContext(new Dictionary<string, string>());

    private static FakeAuthContext Env(params (string Key, string Value)[] entries)
        => new(entries.ToDictionary(entry => entry.Key, entry => entry.Value));

    [Fact]
    public void OpenAiCompatibleFamilyBindsCatalogAndAuth()
    {
        var deepseek = Deepseek.Provider();
        Assert.Equal("deepseek", deepseek.Id);
        Assert.Equal("DeepSeek", deepseek.Name);
        Assert.Equal("https://api.deepseek.com", deepseek.BaseUrl);
        Assert.Equal(2, deepseek.GetModels().Count);
        Assert.Contains(deepseek.GetModels(), m => m.Id == "deepseek-reasoner" && m.Reasoning);
    }

    [Fact]
    public async Task StoredCredentialWinsOverEnv()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("deepseek", _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored-ds")));
        var resolved = await AuthResolve.ResolveProviderAuthAsync(
            "deepseek", Deepseek.Provider().Auth!, store, Env(("DEEPSEEK_API_KEY", "env-ds")));
        Assert.NotNull(resolved);
        Assert.Equal("stored-ds", resolved!.Auth.ApiKey);
        Assert.Equal("stored credential", resolved.Source);
    }

    [Fact]
    public async Task AuthFallsBackToEnvVars()
    {
        var resolved = await AuthResolve.ResolveProviderAuthAsync(
            "groq", Groq.Provider().Auth!, new InMemoryCredentialStore(), Env(("GROQ_API_KEY", "env-groq")));
        Assert.NotNull(resolved);
        Assert.Equal("env-groq", resolved!.Auth.ApiKey);
        Assert.Equal("GROQ_API_KEY", resolved.Source);
    }

    [Fact]
    public async Task UnconfiguredProviderResolvesNull()
    {
        var resolved = await AuthResolve.ResolveProviderAuthAsync(
            "xai", Xai.Provider().Auth!, new InMemoryCredentialStore(), EmptyEnv);
        Assert.Null(resolved);
    }

    [Fact]
    public void DedicatedProvidersCarryOwnBaseUrlAndApi()
    {
        var anthropic = Anthropic.Provider();
        Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
        Assert.Equal("anthropic-messages", anthropic.GetModels().First(m => m.Id == "claude-sonnet-4-5").Api);
        Assert.NotNull(anthropic.Auth!.OAuth);

        var google = Google.Provider();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", google.BaseUrl);
        Assert.Null(google.Auth!.OAuth);

        // openai-codex 仅 OAuth 认证。
        Assert.Null(OpenAiCodex.Provider().Auth!.ApiKey);
        Assert.NotNull(OpenAiCodex.Provider().Auth!.OAuth);
    }

    [Fact]
    public void BuiltinModelsRegistersEveryProvider()
    {
        var models = All.BuiltinModels();
        Assert.Equal(42, models.GetProviders().Count);
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
