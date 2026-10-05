using Pi.Ai.Auth;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>OAuth 刷新编排测试（锁定刷新/轮换安全/并发单一刷新）。</summary>
public class OAuthRefreshTests
{
    /// <summary>可编程 OAuth 实现：记录刷新次数与请求的 refresh token。</summary>
    private sealed class FakeOAuthAuth : IOAuthAuth
    {
        public int RefreshCalls { get; private set; }
        public List<string> RefreshRequests { get; } = [];
        public List<long> ExpiresAt { get; set; } = [];

        public string Name => "Fake OAuth";

        public bool IsSubscription => true;

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            RefreshRequests.Add(credential.Refresh);
            var expires = ExpiresAt.Count >= RefreshCalls
                ? ExpiresAt[RefreshCalls - 1] : long.MaxValue;
            return Task.FromResult(new Credential.OAuth(
                $"refresh-{RefreshCalls}", $"access-{RefreshCalls}", expires));
        }

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    private static (InMemoryCredentialStore Store, FakeOAuthAuth Auth, OAuthRefreshCoordinator Coordinator)
        Create(long now = 1_000_000)
    {
        var store = new InMemoryCredentialStore();
        var auth = new FakeOAuthAuth { ExpiresAt = [long.MaxValue] };
        var coordinator = new OAuthRefreshCoordinator(store, () => auth) { Now = () => now };
        return (store, auth, coordinator);
    }

    [Fact]
    public async Task UnexpiredCredentialSkipsRefresh()
    {
        var (store, auth, coordinator) = Create(now: 1_000_000);
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.OAuth("r0", "a0", 2_000_000))); // 未过期

        var result = await coordinator.GetAuthAsync("p");
        Assert.Equal("a0", result!.ApiKey);
        Assert.Equal(0, auth.RefreshCalls); // 未触碰网络
    }

    [Fact]
    public async Task ExpiredCredentialRefreshesOnceAndStores()
    {
        var (store, auth, coordinator) = Create(now: 1_000_000);
        auth.ExpiresAt = [2_000_000]; // 刷新后的新凭据未过期
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.OAuth("r0", "a0", 500_000))); // 已过期

        var result = await coordinator.GetAuthAsync("p");
        Assert.Equal("access-1", result!.ApiKey);
        Assert.Equal(1, auth.RefreshCalls);
        Assert.Equal("r0", auth.RefreshRequests[0]); // 用原始 refresh token 刷新

        // 存储里已是轮换后的凭据。
        var stored = Assert.IsType<Credential.OAuth>(await store.ReadAsync("p"));
        Assert.Equal("refresh-1", stored.Refresh);
    }

    [Fact]
    public async Task RotatedRefreshTokenIsUsedForNextRefresh()
    {
        var (store, auth, coordinator) = Create(now: 1_000_000);
        auth.ExpiresAt = [500_000, 500_000]; // 每次刷新后立刻又过期（轮换）
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.OAuth("r0", "a0", 500_000)));

        _ = await coordinator.GetAuthAsync("p"); // r0 → refresh-1
        _ = await coordinator.GetAuthAsync("p"); // 用 refresh-1（轮换后的）
        Assert.Equal(["r0", "refresh-1"], auth.RefreshRequests);
        Assert.Equal(2, auth.RefreshCalls);
    }

    [Fact]
    public async Task ConcurrentRequestsShareSingleRefresh()
    {
        var (store, auth, coordinator) = Create(now: 1_000_000);
        auth.ExpiresAt = [2_000_000];
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.OAuth("r0", "a0", 500_000)));

        // 并发 3 个请求：store 锁串行化 → 第一个刷新后其余看到未过期新凭据。
        var results = await Task.WhenAll(
            coordinator.GetAuthAsync("p"),
            coordinator.GetAuthAsync("p"),
            coordinator.GetAuthAsync("p"));
        Assert.All(results, r => Assert.Equal("access-1", r!.ApiKey));
        Assert.Equal(1, auth.RefreshCalls); // 只刷新一次（双重刷新防护）
    }

    [Fact]
    public async Task ApiKeyCredentialResolvesDirectly()
    {
        var (store, auth, coordinator) = Create();
        await store.ModifyAsync("p", _ => Task.FromResult<Credential?>(
            new Credential.ApiKey("sk-key")));

        var result = await coordinator.GetAuthAsync("p");
        Assert.Equal("sk-key", result!.ApiKey);
        Assert.Equal(0, auth.RefreshCalls);
    }
}
