using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// MiniMax CN provider。对应 TS <c>providers/minimax-cn.ts</c> 的 <c>minimaxCnProvider()</c>。
/// </summary>
public static class MinimaxCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "minimax-cn",
        Name = "MiniMax CN",
        BaseUrl = "https://api.minimaxi.com/anthropic",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("MiniMax CN API key", ["MINIMAX_CN_API_KEY"]) },
        Models = BuiltinCatalog.All("minimax-cn"),
        Api = LazyApis.AnthropicMessagesApi(),
    });
}
