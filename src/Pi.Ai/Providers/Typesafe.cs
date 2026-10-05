using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// TypeSafe provider。对应 TS <c>providers/typesafe.ts</c> 的 <c>typesafeProvider()</c>：
/// 纯分类器 provider（无 chat API）。
/// </summary>
public static class Typesafe
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "typesafe",
        Name = "TypeSafe",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("TypeSafe API key", ["TYPESAFE_API_KEY"]) },
        Models = BuiltinCatalog.All("typesafe"),
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            ["typesafe-system-one"] = LazyApis.TypesafeSystemOneApi(),
        },
    });
}
