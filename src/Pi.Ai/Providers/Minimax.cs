using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// MiniMax provider。对应 TS <c>providers/minimax.ts</c> 的 <c>minimaxProvider()</c>。
/// </summary>
public static class Minimax
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "minimax",
        Name = "MiniMax",
        BaseUrl = "https://api.minimax.io/anthropic",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("MiniMax API key", ["MINIMAX_API_KEY"]) },
        Models = BuiltinCatalog.All("minimax"),
        Api = LazyApis.AnthropicMessagesApi(),
    });
}
