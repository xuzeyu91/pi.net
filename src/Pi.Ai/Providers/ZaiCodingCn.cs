using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Z.AI Coding CN provider。对应 TS <c>providers/zai-coding-cn.ts</c> 的 <c>zaiCodingCnProvider()</c>。
/// </summary>
public static class ZaiCodingCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "zai-coding-cn",
        Name = "Z.AI Coding CN",
        BaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Z.AI Coding CN API key", ["ZAI_CODING_CN_API_KEY"]) },
        Models = BuiltinCatalog.All("zai-coding-cn"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
