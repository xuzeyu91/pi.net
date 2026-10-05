using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>认证解析测试。对应 TS auth/resolve.ts 的语义。</summary>
public class AuthResolveTests
{
    private sealed class FakeAuthContext : IAuthContext
    {
        public Dictionary<string, string> Env { get; } = [];

        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(Env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>可编程 OAuth：记录刷新与请求的 refresh token，可注入延迟与故障。</summary>
    private sealed class FakeOAuth : IOAuthAuth
    {
        public int RefreshCalls;
        public List<string> RefreshRequests { get; } = [];
        public Func<Task>? RefreshDelay;
        public Exception? ThrowOnRefresh;
        public long NextExpires = long.MaxValue;

        public string Name => "Fake";
        public bool IsSubscription => true;

        public async Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            RefreshRequests.Add(credential.Refresh);
            if (ThrowOnRefresh is not null) throw ThrowOnRefresh;
            if (RefreshDelay is not null) await RefreshDelay().ConfigureAwait(false);
            return new Credential.OAuth($"rotated-{RefreshCalls}", $"access-{RefreshCalls}", NextExpires);
        }

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Credential.OAuth("l", "l", long.MaxValue));
    }

    private static ProviderAuth Provider(IApiKeyAuth? apiKey = null, IOAuthAuth? oauth = null)
        => new() { ApiKey = apiKey, OAuth = oauth };

    private static long NowMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    [Fact]
    public async Task StoredApiKeyWinsOverEnvironment()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored-key")));
        var context = new FakeAuthContext { Env = { ["P_KEY"] = "env-key" } };
        var auth = Provider(new EnvApiKeyAuth("P key", ["P_KEY"]));

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, context);

        Assert.Equal("stored-key", result!.Auth.ApiKey);
        Assert.Equal("stored credential", result.Source);
    }

    [Fact]
    public async Task EnvironmentResolvesWhenNothingStored()
    {
        var store = new InMemoryCredentialStore();
        var context = new FakeAuthContext { Env = { ["P_KEY"] = "env-key" } };
        var auth = Provider(new EnvApiKeyAuth("P key", ["MISSING", "P_KEY"]));

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, context);

        Assert.Equal("env-key", result!.Auth.ApiKey);
        Assert.Equal("P_KEY", result.Source);
    }

    [Fact]
    public async Task ExplicitApiKeyOverrideBeatsStoredCredential()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.ApiKey("stored-key")));
        var auth = Provider(new EnvApiKeyAuth("P key", ["P_KEY"]));

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext(),
            new AuthResolutionOverrides { ApiKey = "override-key" });

        Assert.Equal("override-key", result!.Auth.ApiKey);
    }

    [Fact]
    public async Task ReturnsNullWhenStoredTypeHasNoMatchingHandler()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.ApiKey("k")));
        var auth = Provider(apiKey: null, oauth: new FakeOAuth()); // 无 apiKey handler

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext());

        Assert.Null(result); // 无静默回退
    }

    [Fact]
    public async Task FreshOAuthSkipsRefresh()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.OAuth("r", "a", NowMs() + 3600_000)));
        var oauth = new FakeOAuth();
        var auth = Provider(oauth: oauth);

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext());

        Assert.Equal("a", result!.Auth.ApiKey);
        Assert.Equal("OAuth", result.Source);
        Assert.Equal(0, oauth.RefreshCalls);
    }

    [Fact]
    public async Task ExpiredOAuthRefreshesUnderLockAndStoresRotation()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.OAuth("r", "a", NowMs() - 1000)));
        var oauth = new FakeOAuth();
        var auth = Provider(oauth: oauth);

        var result = await AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext());

        Assert.Equal("access-1", result!.Auth.ApiKey);
        Assert.Equal(1, oauth.RefreshCalls);
        var stored = await store.ReadAsync("p");
        Assert.Equal("rotated-1", (stored as Credential.OAuth)!.Refresh);
    }

    [Fact]
    public async Task ConcurrentResolvesRefreshOnlyOnce()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.OAuth("r", "a", NowMs() - 1000)));
        var oauth = new FakeOAuth { RefreshDelay = () => Task.Delay(50) };
        var auth = Provider(oauth: oauth);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext())));

        Assert.Equal(1, oauth.RefreshCalls); // 双检锁：只有一个请求真正刷新
        Assert.All(results, result => Assert.NotNull(result!.Auth.ApiKey));
        var stored = await store.ReadAsync("p");
        Assert.Equal("rotated-1", (stored as Credential.OAuth)!.Refresh);
    }

    [Fact]
    public async Task MinValidityIsEnforcedAfterExplicitRefresh()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.OAuth("r", "a", NowMs() + 2 * 60_000))); // 2 分钟后过期 → 触发刷新
        var oauth = new FakeOAuth { NextExpires = NowMs() + 6 * 60_000 }; // 刷新后仍不足 10 分钟
        var auth = Provider(oauth: oauth);

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext(),
                new AuthResolutionOverrides { MinOAuthValidityMs = 10 * 60_000 }));

        Assert.Equal(ModelsErrorCode.OAuth, error.Code);
        Assert.Contains("expires too soon", error.Message);
    }

    [Fact]
    public async Task RefreshFailureWrapsInModelsError()
    {
        var store = new InMemoryCredentialStore();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(new Credential.OAuth("r", "a", NowMs() - 1000)));
        var oauth = new FakeOAuth { ThrowOnRefresh = new InvalidOperationException("invalid_grant") };
        var auth = Provider(oauth: oauth);

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext()));

        Assert.Equal(ModelsErrorCode.OAuth, error.Code);
        Assert.Contains("OAuth refresh failed for p", error.Message);
        Assert.Contains("invalid_grant", error.Message); // 底层原因保持在消息里
    }

    [Fact]
    public async Task StoreReadFailureWrapsInModelsError()
    {
        var store = new ThrowingStore();
        var auth = Provider(new EnvApiKeyAuth("P key", ["P_KEY"]));

        var error = await Assert.ThrowsAsync<ModelsError>(() =>
            AuthResolve.ResolveProviderAuthAsync("p", auth, store, new FakeAuthContext()));

        Assert.Equal(ModelsErrorCode.Auth, error.Code);
        Assert.Contains("Credential store read failed for p", error.Message);
    }

    private sealed class ThrowingStore : ICredentialStore
    {
        public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("disk on fire");

        public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Credential?> ModifyAsync(string providerId,
            Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
