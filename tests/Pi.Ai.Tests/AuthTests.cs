using Pi.Ai.Auth;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>凭据存储与认证解析测试。</summary>
public class AuthTests
{
    [Fact]
    public async Task StoreReadModifyDeleteSerializes()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(await store.ReadAsync("anthropic"));
        Assert.Empty(await store.ListAsync());

        // modify 写入。
        var key = new Credential.ApiKey("sk-ant-1");
        var after = await store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(key));
        Assert.Same(key, after);
        Assert.Same(key, await store.ReadAsync("anthropic"));

        // modify 读改写：fn 看到当前凭据。
        var rotated = await store.ModifyAsync("anthropic", current =>
        {
            Assert.Same(key, current);
            return Task.FromResult<Credential?>(key with { Key = "sk-ant-2" });
        });
        Assert.Equal("sk-ant-2", ((Credential.ApiKey)rotated!).Key);

        // 并发 modify 串行化：最终值是最后完成的写。
        await Task.WhenAll(
            store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(new Credential.ApiKey("a"))),
            store.ModifyAsync("anthropic", _ => Task.FromResult<Credential?>(new Credential.ApiKey("b"))));
        var final = (Credential.ApiKey)(await store.ReadAsync("anthropic"))!;
        Assert.True(final.Key is "a" or "b");

        // 元数据列举（不暴露密钥）。
        var infos = await store.ListAsync();
        Assert.Equal([new CredentialInfo("anthropic", CredentialKind.ApiKey)], infos);

        // 删除。
        await store.DeleteAsync("anthropic");
        Assert.Null(await store.ReadAsync("anthropic"));
    }

    [Fact]
    public async Task ModifyReturningNullKeepsCurrent()
    {
        var store = new InMemoryCredentialStore();
        var key = new Credential.ApiKey("k1");
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(key));

        var result = await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(null));
        Assert.Same(key, result); // null = 不变
        Assert.Same(key, await store.ReadAsync("p"));
    }

    [Fact]
    public async Task OAuthCredentialRoundTrip()
    {
        var store = new InMemoryCredentialStore();
        var oauth = new Credential.OAuth("refresh-1", "access-1", 1700000000000);
        await store.ModifyAsync("openai", _ => Task.FromResult<Credential?>(oauth));
        var read = Assert.IsType<Credential.OAuth>(await store.ReadAsync("openai"));
        Assert.Equal("refresh-1", read.Refresh);
        Assert.Equal(CredentialKind.OAuth, read.Kind);
        // JSON 往返（auth.json 形状）。
        var json = System.Text.Json.JsonSerializer.Serialize<Credential>(oauth);
        var restored = System.Text.Json.JsonSerializer.Deserialize<Credential>(json);
        Assert.Equal(oauth, restored);
    }

    [Fact]
    public async Task ResolveApiKeyMergesCredentialAndEnv()
    {
        var env = new TestAuthContext(new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "env-key",
            ["CF_ACCOUNT_ID"] = "env-account",
        });

        // 凭据 key 优先于环境变量。
        var withKey = await AuthResolver.ResolveApiKeyAuthAsync("ANTHROPIC_API_KEY",
            new Credential.ApiKey("stored-key"), env);
        Assert.Equal("stored-key", withKey!.Auth.ApiKey);

        // 无凭据 key 时用环境变量。
        var fromEnv = await AuthResolver.ResolveApiKeyAuthAsync("ANTHROPIC_API_KEY",
            new Credential.ApiKey(), env);
        Assert.Equal("env-key", fromEnv!.Auth.ApiKey);

        // credential.env 合并（凭据值优先）+ 全空 = 未配置。
        var withEnv = await AuthResolver.ResolveApiKeyAuthAsync("ANTHROPIC_API_KEY",
            new Credential.ApiKey(Env: new Dictionary<string, string> { ["CF_ACCOUNT_ID"] = "cred-account" }), env);
        Assert.Equal("cred-account", withEnv!.Env!["CF_ACCOUNT_ID"]);

        var missing = new TestAuthContext(new Dictionary<string, string>());
        Assert.Null(await AuthResolver.ResolveApiKeyAuthAsync("MISSING_KEY", null, missing));
    }

    private sealed class TestAuthContext(IReadOnlyDictionary<string, string> values) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(values.GetValueOrDefault(name));

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
