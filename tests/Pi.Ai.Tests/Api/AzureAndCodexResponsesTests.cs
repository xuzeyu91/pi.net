using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>P29：Azure/Codex Responses 请求构建与错误分类测试。</summary>
public class AzureAndCodexResponsesTests
{
    private static ModelSpec Model(string api = "azure-openai-responses", string provider = "azure-openai-responses")
        => new()
        {
            Id = "gpt-5.1", Name = "GPT-5.1", Api = api, Provider = provider,
            BaseUrl = "https://example.openai.azure.com",
            Reasoning = true, ContextWindow = 128_000, MaxTokens = 4096,
        };

    private static TranscriptContext Context(params ChatMessage[] messages) => new(messages);

    // ---------------------------------------------------------------------------
    // Azure
    // ---------------------------------------------------------------------------

    [Fact]
    public void ParsesDeploymentNameMap()
    {
        var map = AzureOpenAiResponses.ParseDeploymentNameMap("gpt-5.1 = deploy-a , gpt-5-nano=deploy-b,,");
        Assert.Equal("deploy-a", map["gpt-5.1"]);
        Assert.Equal("deploy-b", map["gpt-5-nano"]);
        Assert.Empty(AzureOpenAiResponses.ParseDeploymentNameMap(null));
    }

    [Fact]
    public void ResolveDeploymentNamePrefersOptionThenMap()
    {
        var model = Model();
        var viaOption = AzureOpenAiResponses.ResolveDeploymentName(model,
            new AzureOpenAiResponsesOptions { AzureDeploymentName = "explicit" });
        Assert.Equal("explicit", viaOption);

        var viaMap = AzureOpenAiResponses.ResolveDeploymentName(model,
            new AzureOpenAiResponsesOptions
            {
                Env = new Dictionary<string, string> { ["AZURE_OPENAI_DEPLOYMENT_NAME_MAP"] = "gpt-5.1=mapped" },
            });
        Assert.Equal("mapped", viaMap);

        var fallback = AzureOpenAiResponses.ResolveDeploymentName(model, null);
        Assert.Equal("gpt-5.1", fallback);
    }

    [Fact]
    public void NormalizesAzureBaseUrl()
    {
        Assert.Equal("https://r.openai.azure.com/openai/v1",
            AzureOpenAiResponses.NormalizeAzureBaseUrl("https://r.openai.azure.com"));
        Assert.Equal("https://r.openai.azure.com/openai/v1",
            AzureOpenAiResponses.NormalizeAzureBaseUrl("https://r.openai.azure.com/openai/"));
        Assert.Equal("https://r.openai.azure.com/openai/v1",
            AzureOpenAiResponses.NormalizeAzureBaseUrl("https://r.openai.azure.com/openai/v1/responses"));
        Assert.Equal("https://custom.example.com/v1",
            AzureOpenAiResponses.NormalizeAzureBaseUrl("https://custom.example.com/v1/"));
    }

    [Fact]
    public void BuildParamsUsesDeploymentAndReasoning()
    {
        var model = Model();
        var context = Context(
            new SystemMessage("be brief", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1),
            new AssistantMessage(
                [new ToolCallContent("call-1", "search", new JsonObject { ["q"] = "x" })],
                StopReason.ToolUse, Timestamp: 2),
            new ToolResultMessage("call-1", "search", [new TextContent("result")], Timestamp: 3));
        var requestParams = AzureOpenAiResponses.BuildParams(
            model, context,
            new AzureOpenAiResponsesOptions
            {
                SessionId = "sess-1",
                MaxTokens = 8,
                ReasoningEffort = "low",
            }, "deploy-a");

        Assert.Equal("deploy-a", requestParams.Str("model"));
        Assert.True(requestParams.Bool("stream"));
        Assert.Equal(false, requestParams.Bool("store"));
        Assert.Equal(16, requestParams.Num("max_output_tokens")); // 下限 16
        Assert.Equal("sess-1", requestParams.Str("prompt_cache_key"));
        Assert.Equal("low", requestParams.Obj("reasoning")?.Str("effort"));
        Assert.Equal("auto", requestParams.Obj("reasoning")?.Str("summary"));
        Assert.Equal("reasoning.encrypted_content", requestParams["include"] is JsonArray include && include[0] is JsonValue { } first ? first.GetValue<string>() : null);
        // 工具调用 id 未经归一化路径（azure provider id 不在归一化集合——但 azure-openai-responses 在集合里）
        Assert.Contains("azure-openai-responses", AzureOpenAiResponses.ProviderIdSet);
    }

    [Fact]
    public void BuildParamsOmitsReasoningWhenOffMapped()
    {
        var model = Model() with
        {
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject { ["off"] = null }),
        };
        var requestParams = AzureOpenAiResponses.BuildParams(model, Context(
            new UserMessage([new TextContent("hi")], 1)), new AzureOpenAiResponsesOptions(), "d");
        Assert.Null(requestParams["reasoning"]);
    }

    // ---------------------------------------------------------------------------
    // Codex
    // ---------------------------------------------------------------------------

    private static string CodexJwt()
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{B64(new JsonObject().ToJsonString())}.{B64(new JsonObject
        {
            [JwtClaimPath] = new JsonObject { ["chatgpt_account_id"] = "acct-42" },
        }.ToJsonString())}.sig";
    }

    private const string JwtClaimPath = "https://api.openai.com/auth";

    [Fact]
    public void ExtractsAccountIdFromJwt()
    {
        Assert.Equal("acct-42", OpenAiCodexResponses.ExtractAccountId(CodexJwt()));
        Assert.Throws<InvalidOperationException>(() => OpenAiCodexResponses.ExtractAccountId("not-a-jwt"));
    }

    [Fact]
    public void ResolvesCodexUrls()
    {
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
            OpenAiCodexResponses.ResolveCodexUrl(null));
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
            OpenAiCodexResponses.ResolveCodexUrl("https://chatgpt.com/backend-api/"));
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses",
            OpenAiCodexResponses.ResolveCodexUrl("https://chatgpt.com/backend-api/codex"));
        Assert.Equal("wss://chatgpt.com/backend-api/codex/responses",
            OpenAiCodexResponses.ResolveCodexWebSocketUrl(null));
    }

    [Fact]
    public void BuildRequestBodyUsesInstructionsAndVerbosity()
    {
        var model = Model("openai-codex-responses", "openai-codex") with
        {
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject { ["off"] = "none" }),
        };
        var body = OpenAiCodexResponses.BuildRequestBody(
            model,
            Context(
                new SystemMessage("You are pi.", Timestamp: 0),
                new UserMessage([new TextContent("hi")], 1)),
            new OpenAiCodexResponsesOptions { SessionId = "sess-9", TextVerbosity = "medium" },
            "clamped-sess");

        Assert.Equal("gpt-5.1", body.Str("model"));
        Assert.Equal("You are pi.", body.Str("instructions"));
        Assert.Equal(false, body.Bool("store"));
        Assert.Equal(true, body.Bool("stream"));
        Assert.Equal("medium", body.Obj("text")?.Str("verbosity"));
        Assert.Equal("clamped-sess", body.Str("prompt_cache_key"));
        Assert.Equal("auto", body.Str("tool_choice"));
        Assert.Equal(true, body.Bool("parallel_tool_calls"));
        Assert.Equal("none", body.Obj("reasoning")?.Str("effort")); // thinkingLevelMap.off
        Assert.True(body.Bool("include") is null || body["include"] is JsonArray);
    }

    [Fact]
    public void BuildRequestBodyExplicitNoneReasoning()
    {
        var model = Model("openai-codex-responses", "openai-codex");
        var body = OpenAiCodexResponses.BuildRequestBody(
            model, Context(new UserMessage([new TextContent("hi")], 1)),
            new OpenAiCodexResponsesOptions { ReasoningEffort = "none" }, null);
        // 无 thinkingLevelMap → "none" effort 仍然发送（off 缺省 → "none"）。
        Assert.Equal("none", body.Obj("reasoning")?.Str("effort"));
    }

    [Fact]
    public void RetryClassification()
    {
        Assert.True(OpenAiCodexResponses.IsRetryableError(429, "please slow down"));
        Assert.False(OpenAiCodexResponses.IsRetryableError(429, "insufficient_quota"));
        Assert.True(OpenAiCodexResponses.IsRetryableError(503, "overloaded"));
        Assert.True(OpenAiCodexResponses.IsRetryableError(500, "anything"));
        Assert.False(OpenAiCodexResponses.IsRetryableError(400, "bad request"));
        Assert.True(OpenAiCodexResponses.IsTerminalRateLimitError("Monthly usage limit reached"));
    }

    [Fact]
    public void ParseErrorResponseFriendlyMessage()
    {
        var (message, friendly) = OpenAiCodexResponses.ParseErrorResponse(429, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "usage_limit_reached",
                ["plan_type"] = "FREE",
                ["resets_at"] = DateTimeOffset.Now.ToUnixTimeSeconds() + 600,
                ["message"] = "usage limit",
            },
        }.ToJsonString());
        Assert.Equal("usage limit", message);
        Assert.NotNull(friendly);
        Assert.Contains("ChatGPT usage limit (free plan)", friendly);
        Assert.Contains("Try again in ~10 min", friendly);
    }

    [Fact]
    public void ServiceTierPricing()
    {
        var model = Model("openai-codex-responses", "openai-codex");
        Assert.Equal(0.5, OpenAiCodexResponses.GetServiceTierCostMultiplier(model, "flex"));
        Assert.Equal(2, OpenAiCodexResponses.GetServiceTierCostMultiplier(model, "priority"));
        Assert.Equal(1, OpenAiCodexResponses.GetServiceTierCostMultiplier(model, "default"));
        Assert.Equal("flex", OpenAiCodexResponses.ResolveCodexServiceTier("default", "flex"));
        Assert.Equal("default", OpenAiCodexResponses.ResolveCodexServiceTier("default", "auto"));
    }
}
