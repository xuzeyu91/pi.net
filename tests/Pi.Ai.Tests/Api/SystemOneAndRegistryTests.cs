using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>P30：SystemOne/Cloudflare/ApiRegistry 测试。</summary>
public class SystemOneTests
{
    private static ModelSpec Model(string api, string baseUrl)
        => new()
        {
            Id = "classifier-x", Name = "Classifier", Api = api, Provider = "test",
            BaseUrl = baseUrl, Cost = new ModelCostRates(1, 2),
        };

    private static ClassifierContext Context()
        => new()
        {
            State = new JsonObject { ["step"] = 1 },
            Questions = new Dictionary<string, ClassifierQuestion>
            {
                ["q-choice"] = new ClassifierChoiceQuestion(
                    "Pick one", new Dictionary<string, string> { ["a"] = "first", ["b"] = "second" }),
                ["q-score"] = new ClassifierScoreQuestion("Rate", ["low", "high"]),
                ["q-bool"] = new ClassifierBoolQuestion("Yes or no", "yes", "no"),
            },
        };

    private static ClassifierOptions Options(string apiKey = "key-1") => new() { ApiKey = apiKey };

    [Fact]
    public async Task TypesafeSystemOneRoundTrips()
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            var parsed = JsonNode.Parse(body!)!.AsObject();
            // typesafe：state/questions/model 顶层平铺，bool → noul。
            Assert.Equal("classifier-x", parsed.Str("model"));
            Assert.Equal("noul", parsed.Obj("questions")?.Obj("q-bool")?.Str("type"));
            Assert.Equal("choice", parsed.Obj("questions")?.Obj("q-choice")?.Str("type"));
            Assert.Equal(1, parsed.Obj("state")?.Num("step"));
            return FakeResponse.Json(new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["q-choice"] = new JsonObject
                    {
                        ["type"] = "choice",
                        ["choice"] = "a",
                        ["probabilities"] = new JsonObject { ["a"] = 0.8, ["b"] = 0.2 },
                        ["confidence"] = 0.9,
                    },
                    ["q-score"] = new JsonObject
                    {
                        ["type"] = "score", ["score"] = 2, ["confidence"] = 0.7,
                    },
                    ["q-bool"] = new JsonObject { ["type"] = "noul", ["noul"] = 0.95 },
                },
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
            });
        });
        var result = await TypesafeSystemOne.ClassifyAsync(
            Model("typesafe-system-one", "https://svc.example.com/"),
            Context(), Options(), new System.Net.Http.HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        var choice = Assert.IsType<ClassifierChoiceAnswer>(result.Answers["q-choice"]);
        Assert.Equal("a", choice.Choice);
        Assert.Equal(0.9, choice.Confidence);
        Assert.Equal(0.8, choice.Probabilities["a"]);
        var score = Assert.IsType<ClassifierScoreAnswer>(result.Answers["q-score"]);
        Assert.Equal(2, score.Score);
        var boolean = Assert.IsType<ClassifierBoolAnswer>(result.Answers["q-bool"]);
        Assert.Equal(0.95, boolean.Probability);
        // usage 按目录报价计费：10/1M·1 + 5/1M·2 ≈ 0.00002（浮点经 1e-6 除法）。
        Assert.True(Math.Abs(result.Usage!.Cost!.Value - 0.00002) < 1e-12);
    }

    [Fact]
    public async Task CloudflareWorkersAiUnwrapsEnvelopes()
    {
        // 第三方模型：state Completed + 内层 result。
        var handler = new FakeHttpHandler((request, body) =>
        {
            Assert.EndsWith("/run", request.RequestUri!.ToString());
            var parsed = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("classifier-x", parsed.Str("model"));
            Assert.NotNull(parsed.Obj("input")?.Obj("questions"));
            return FakeResponse.Json(new JsonObject
            {
                ["success"] = true,
                ["result"] = new JsonObject
                {
                    ["state"] = "Completed",
                    ["result"] = new JsonObject
                    {
                        ["answers"] = new JsonObject
                        {
                            ["q-choice"] = new JsonObject
                            {
                                ["type"] = "choice", ["choice"] = "b",
                                ["probabilities"] = new JsonObject { ["a"] = 0.3, ["b"] = 0.7 },
                                ["confidence"] = 0.6,
                            },
                            ["q-score"] = new JsonObject { ["type"] = "score", ["score"] = 1, ["confidence"] = 0.5 },
                            ["q-bool"] = new JsonObject { ["type"] = "noul", ["noul"] = 0.4 },
                        },
                        ["usage"] = new JsonObject { ["input_tokens"] = 4 },
                    },
                },
            });
        });
        var result = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            Model("cloudflare-workers-ai-system-one", "https://api.cloudflare.com/client/v4/accounts/acct/ai"),
            Context(), Options(), new System.Net.Http.HttpClient(handler));
        Assert.Equal("b", Assert.IsType<ClassifierChoiceAnswer>(result.Answers["q-choice"]).Choice);

        // Cloudflare 自有模型：answers 直接在 result 上。
        var directHandler = new FakeHttpHandler(_ => FakeResponse.Json(new JsonObject
        {
            ["success"] = true,
            ["result"] = new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    ["q-choice"] = new JsonObject
                    {
                        ["type"] = "choice", ["choice"] = "a",
                        ["probabilities"] = new JsonObject { ["a"] = 1 },
                        ["confidence"] = 1,
                    },
                    ["q-score"] = new JsonObject { ["type"] = "score", ["score"] = 0, ["confidence"] = 1 },
                    ["q-bool"] = new JsonObject { ["type"] = "noul", ["noul"] = 0 },
                },
            },
        }));
        var direct = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            Model("cloudflare-workers-ai-system-one", "https://x.example/ai"),
            Context(), Options(), new System.Net.Http.HttpClient(directHandler));
        Assert.Equal(ClassifierStopReason.Stop, direct.StopReason);

        // success=false → 错误结果。
        var failHandler = new FakeHttpHandler(_ => FakeResponse.Json(new JsonObject
        {
            ["success"] = false,
            ["errors"] = new JsonArray(new JsonObject { ["message"] = "model overloaded" }),
        }));
        var failure = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            Model("cloudflare-workers-ai-system-one", "https://x.example/ai"),
            Context(), Options(), new System.Net.Http.HttpClient(failHandler));
        Assert.Equal(ClassifierStopReason.Error, failure.StopReason);
        Assert.Contains("model overloaded", failure.ErrorMessage);
    }

    [Fact]
    public void ResolveModelReplacesCloudflarePlaceholders()
    {
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "openai-completions", Provider = "cloudflare-ai-gateway",
            BaseUrl = CloudflareEndpoints.AiGatewayCompatBaseUrl,
        };
        var resolved = CloudflareStreams.ResolveModel(model, new Dictionary<string, string>
        {
            ["CLOUDFLARE_ACCOUNT_ID"] = "acct-9",
            ["CLOUDFLARE_GATEWAY_ID"] = "gw-1",
        });
        Assert.Equal("https://gateway.ai.cloudflare.com/v1/acct-9/gw-1/compat", resolved.BaseUrl);
        Assert.Same(model, CloudflareStreams.ResolveModel(model, null));
    }
}

/// <summary>P30：ApiRegistry 注册/覆盖/注销。</summary>
public class ApiRegistryTests
{
    private static ModelSpec Model(string api)
        => new()
        {
            Id = "m", Name = "M", Api = api, Provider = "p", BaseUrl = "https://x.example",
        };

    [Fact]
    public void RegisterOverrideAndUnregisterBySource()
    {
        ApiRegistry.ResetApiProviders();
        // 内建实现就位。
        Assert.NotNull(ApiRegistry.GetApiProvider("openai-responses"));

        // 扩展覆盖 + sourceId 注销。
        ApiRegistry.RegisterApiProvider(new ApiProvider
        {
            Api = "openai-responses",
            Stream = (_, _, _, _, _) => throw new InvalidOperationException("override"),
            StreamSimple = (_, _, _, _, _) => throw new InvalidOperationException("override"),
        }, sourceId: "ext-1");
        Assert.Equal("override", Assert.Throws<InvalidOperationException>(
            () => ApiRegistry.GetApiProvider("openai-responses")!.Stream(
                Model("openai-responses"), new TranscriptContext([]), null, null, default)).Message);

        ApiRegistry.UnregisterApiProviders("ext-1");
        // 内建实现注册时不覆盖已有条目——覆盖注销后需重置恢复。
        ApiRegistry.ResetApiProviders();
        Assert.NotNull(ApiRegistry.GetApiProvider("openai-responses"));
        Assert.NotNull(ApiRegistry.GetApiProvider("mistral-conversations"));
        Assert.NotNull(ApiRegistry.GetApiProvider("pi-messages"));

        ApiRegistry.ResetApiProviders();
    }
}
