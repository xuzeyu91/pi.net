using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Azure OpenAI provider。对应 TS <c>providers/azure-openai-responses.ts</c> 的
/// <c>azureOpenAIResponsesProvider()</c>：部署名/端点由模型与选项决定，故无静态 baseUrl。
/// </summary>
public static class AzureOpenAiResponses
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "azure-openai-responses",
        Name = "Azure OpenAI",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Azure OpenAI API key", ["AZURE_OPENAI_API_KEY"]) },
        Models = BuiltinCatalog.All("azure-openai-responses"),
        Api = LazyApis.AzureOpenAiResponsesApi(),
    });
}
