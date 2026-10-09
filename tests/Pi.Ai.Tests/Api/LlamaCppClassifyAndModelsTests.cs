using Pi.Ai.Auth.OAuth;
using Pi.Ai.Tests.Auth;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>llama.cpp 分类流测试：纯函数 + mock HTTP 全流程。</summary>
public class LlamaCppClassifyTests
{
    private static readonly JsonObject State = new()
    {
        ["repo"] = "pi.net",
        ["ci"] = "passing",
    };

    private static ClassifierContext ChoiceContext() => new()
    {
        State = State,
        Questions = new Dictionary<string, ClassifierQuestion>
        {
            ["language"] = new ClassifierChoiceQuestion("Which language dominates?", new Dictionary<string, string>
            {
                ["German"] = "ok",
                ["French"] = "",
            }),
        },
    };

    [Theory]
    [InlineData("https://api.example.com/v1", "https://api.example.com")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com")]
    [InlineData("https://api.example.com", "https://api.example.com")]
    public void StripsV1SuffixFromServerRoot(string baseUrl, string expected)
        => Assert.Equal(expected, LlamaCppClassify.LlamaServerRoot(baseUrl));

    [Fact]
    public void ChoiceLabelsFollowCriteriaOrder()
    {
        var (labels, keys) = LlamaCppClassify.QuestionLabels(new ClassifierChoiceQuestion("q", new Dictionary<string, string>
        {
            ["German"] = "ok",
            ["French"] = "",
            ["Spanish"] = "",
        }));
        Assert.Equal(["A", "B", "C"], labels);
        Assert.Equal(["German", "French", "Spanish"], keys);
    }

    [Fact]
    public void RejectsTooFewChoiceOptions()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LlamaCppClassify.QuestionLabels(new ClassifierChoiceQuestion("q", new Dictionary<string, string>
            {
                ["only"] = "",
            })));
        Assert.Contains("needs 2 to 62 options", error.Message);
    }

    [Fact]
    public void ScoreLabelsAreDigitsAndKeysMatch()
    {
        var (labels, keys) = LlamaCppClassify.QuestionLabels(new ClassifierScoreQuestion("q", ["low", "mid", "high"]));
        Assert.Equal(["0", "1", "2"], labels);
        Assert.Equal(keys, labels);
    }

    [Fact]
    public void BoolLabelsAreYesNo()
    {
        var (labels, keys) = LlamaCppClassify.QuestionLabels(
            new ClassifierBoolQuestion("q", "is true", "is false"));
        Assert.Equal(["Yes", "No"], labels);
        Assert.Equal(["true", "false"], keys);
    }

    [Fact]
    public void LabelProbabilitiesIsSoftmaxAfterScaling()
    {
        var probabilities = LlamaCppClassify.LabelProbabilities([-0.2, -2.3], 1);
        Assert.Equal(1, probabilities.Sum(), 12);
        Assert.True(probabilities[0] > probabilities[1]);

        // temperature 缩放：除以 <1 的值放大差异。
        var sharper = LlamaCppClassify.LabelProbabilities([-0.2, -2.3], 0.5);
        Assert.True(sharper[0] > probabilities[0]);
    }

    [Fact]
    public void PeakConfidenceClampsToOne()
    {
        Assert.Equal(1, LlamaCppClassify.PeakConfidence([1.0, 0.0]), 12);
        Assert.Equal(0.8, LlamaCppClassify.PeakConfidence([0.9, 0.1]), 12);
        Assert.Equal(0, LlamaCppClassify.PeakConfidence([0.5, 0.5]), 12);
    }

    [Fact]
    public void AnswersFromProbabilities()
    {
        var choice = LlamaCppClassify.AnswerFromProbabilities(
            new ClassifierChoiceQuestion("q", new Dictionary<string, string> { ["a"] = "", ["b"] = "" }),
            ["a", "b"],
            LlamaCppClassify.LabelProbabilities([-0.2, -2.3], 1));
        var choiceAnswer = Assert.IsType<ClassifierChoiceAnswer>(choice);
        Assert.Equal("a", choiceAnswer.Choice);
        Assert.Equal(2, choiceAnswer.Probabilities.Count);

        var score = Assert.IsType<ClassifierScoreAnswer>(LlamaCppClassify.AnswerFromProbabilities(
            new ClassifierScoreQuestion("q", ["low", "high"]),
            ["0", "1"],
            [0.25, 0.75]));
        Assert.Equal(0.75, score.Score, 12); // 0*0.25 + 1*0.75

        var boolAnswer = Assert.IsType<ClassifierBoolAnswer>(LlamaCppClassify.AnswerFromProbabilities(
            new ClassifierBoolQuestion("q", "t", "f"),
            ["true", "false"],
            [0.9, 0.1]));
        Assert.Equal(0.9, boolAnswer.Probability, 12);
    }

    [Fact]
    public void RenderQuestionRepeatsStateAndLabelsOptions()
    {
        var rendered = LlamaCppClassify.RenderQuestion(ChoiceContext(), "language");
        // 结构：状态、总览、状态（重复）、本题带标签
        var stateCount = rendered.Content.Split("State:\n").Length - 1;
        Assert.Equal(2, stateCount);
        Assert.Contains("A. German: ok", rendered.Content);
        Assert.Contains("B. French", rendered.Content);
        Assert.Contains("Question: Which language dominates?", rendered.Content);
        Assert.Contains("Answer with one letter.", rendered.Content);
        Assert.Equal(["A", "B"], rendered.Labels);
        Assert.Equal(["German", "French"], rendered.Keys);
    }

    [Fact]
    public void RenderQuestionRejectsUnknownId()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LlamaCppClassify.RenderQuestion(ChoiceContext(), "missing"));
        Assert.Equal("Unknown question: missing", error.Message);
    }

    [Fact]
    public async Task ClassifyRunsTokenizeTemplateAndCompletion()
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/tokenize" => TokenizeResponse(body!),
                "/apply-template" => FakeResponse.Json(new JsonObject
                {
                    ["prompt"] = "<|system|>answer<|user|>question",
                }),
                "/completion" => FakeResponse.Json(new JsonObject
                {
                    ["completion_probabilities"] = new JsonArray(new JsonObject
                    {
                        ["top_logprobs"] = new JsonArray(
                            new JsonObject { ["id"] = 42, ["logprob"] = -0.2 },
                            new JsonObject { ["id"] = 43, ["logprob"] = -2.3 },
                            new JsonObject { ["id"] = 99, ["logprob"] = -9.0 }),
                    }),
                }),
                _ => FakeResponse.Text("unexpected", HttpStatusCode.NotFound),
            };
        });
        var model = new ModelSpec
        {
            Id = "qwen3-8b", Name = "Qwen3", Api = "llama-cpp-classify", Provider = "llama.cpp",
            BaseUrl = "http://localhost:8080/v1", Input = ["text"], Type = ModelType.Classifier,
        };
        var onResponseSeen = new List<ProviderResponse>();
        var result = await LlamaCppClassify.Classify(
            model,
            ChoiceContext(),
            new ClassifierOptions
            {
                OnResponse = (response, _) =>
                {
                    onResponseSeen.Add(response);
                    return Task.CompletedTask;
                },
            },
            new HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        var answer = Assert.IsType<ClassifierChoiceAnswer>(result.Answers["language"]);
        Assert.Equal("German", answer.Choice);
        Assert.True(answer.Confidence > 0.7); // softmax(-0.2/-2.3) → (2·0.891−1) ≈ 0.78
        // 观察回调只挂在 observe=true 的 /completion 上。
        Assert.All(onResponseSeen, response => Assert.Equal(200, response.Status));

        // /completion 载荷契约。
        var completionRequest = handler.Requests
            .Last(request => request.Url!.EndsWith("/completion"));
        var completionBody = JsonNode.Parse(completionRequest.Body!)!.AsObject();
        Assert.Equal(1, completionBody.Num("n_predict"));
        Assert.Equal(256, completionBody.Num("n_probs")); // max(256, 16*2)
        Assert.Equal(0, completionBody.Num("temperature"));
        Assert.Equal(true, completionBody.Bool("cache_prompt"));
    }

    private static HttpResponseMessage TokenizeResponse(string body)
    {
        var content = JsonNode.Parse(body)!.AsObject().Str("content")!;
        return content switch
        {
            "\n" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10) }),
            "\nA" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10, 42) }),
            "\nB" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10, 43) }),
            "A" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(42) }),
            "B" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(43) }),
            _ => FakeResponse.Text("unexpected tokenize content", HttpStatusCode.BadRequest),
        };
    }

    [Fact]
    public async Task ApplyTemplateThinkBlockIsClosedImmediately()
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/apply-template")
            {
                return FakeResponse.Json(new JsonObject
                {
                    ["prompt"] = "prefix<think>",
                });
            }
            if (request.RequestUri!.AbsolutePath == "/tokenize")
            {
                var content = JsonNode.Parse(body!)!.AsObject().Str("content")!;
                return content switch
                {
                    "\n" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10) }),
                    "\nYes" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10, 42) }),
                    "\nNo" => FakeResponse.Json(new JsonObject { ["tokens"] = new JsonArray(10, 43) }),
                    _ => FakeResponse.Text("unexpected tokenize content", HttpStatusCode.BadRequest),
                };
            }
            return FakeResponse.Json(new JsonObject
            {
                ["completion_probabilities"] = new JsonArray(new JsonObject
                {
                    ["top_logprobs"] = new JsonArray(
                        new JsonObject { ["id"] = 42, ["logprob"] = -0.01 },
                        new JsonObject { ["id"] = 43, ["logprob"] = -7.0 }),
                }),
            });
        });
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "llama-cpp-classify", Provider = "llama.cpp",
            BaseUrl = "http://localhost:8080/v1", Input = ["text"], Type = ModelType.Classifier,
        };
        var result = await LlamaCppClassify.Classify(
            model,
            new ClassifierContext
            {
                State = [],
                Questions = new Dictionary<string, ClassifierQuestion>
                {
                    ["b"] = new ClassifierBoolQuestion("is ci green?", "", ""),
                },
            },
            null,
            new HttpClient(handler));
        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        var answer = Assert.IsType<ClassifierBoolAnswer>(result.Answers["b"]);
        Assert.Equal(0.99908, answer.Probability, 4); // softmax(-0.01/-7)
    }

    [Fact]
    public async Task ClassificationFailureBecomesErrorResult()
    {
        var handler = new FakeHttpHandler((request, _) =>
            FakeResponse.Text("server down", HttpStatusCode.InternalServerError));
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "llama-cpp-classify", Provider = "llama.cpp",
            BaseUrl = "http://localhost:8080/v1", Input = ["text"], Type = ModelType.Classifier,
        };
        var result = await LlamaCppClassify.Classify(model, ChoiceContext(), null, new HttpClient(handler));
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("llama.cpp error", result.ErrorMessage);
        Assert.Empty(result.Answers);
    }

    [Fact]
    public async Task UnsupportedApiIsRejected()
    {
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "typesafe-system-one", Provider = "p",
            BaseUrl = "http://localhost", Input = ["text"], Type = ModelType.Classifier,
        };
        var result = await LlamaCppClassify.Classify(model, ChoiceContext(), null, new HttpClient(new FakeHttpHandler()));
        Assert.Contains("Unsupported classifier API", result.ErrorMessage);
    }

    [Fact]
    public async Task NonPositiveTemperatureIsRejected()
    {
        var model = new ModelSpec
        {
            Id = "m", Name = "M", Api = "llama-cpp-classify", Provider = "p",
            BaseUrl = "http://localhost/v1", Input = ["text"], Type = ModelType.Classifier,
        };
        var result = await LlamaCppClassify.Classify(
            model, ChoiceContext(), new ClassifierOptions { Temperature = 0 }, new HttpClient(new FakeHttpHandler()));
        Assert.Contains("Temperature must be a positive number", result.ErrorMessage);
    }
}

/// <summary>Models 门面的图片/分类分发测试。</summary>
public class ModelsImagesClassifierTests
{
    private sealed class ImageCapableProvider : IProvider, IImagesProvider
    {
        public string Id => "openrouter";
        public string Name => "OpenRouter";
        public string? BaseUrl => "https://openrouter.ai/api/v1";
        public IReadOnlyList<ModelSpec> GetModels() =>
        [
            new ModelSpec
            {
                Id = "img-1", Name = "Img", Api = "openrouter-images", Provider = Id,
                BaseUrl = BaseUrl!, Input = ["image"], Type = ModelType.Image,
            },
        ];
        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();
        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();

        public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
            ImagesOptions? options, CancellationToken cancellationToken)
            => Task.FromResult(new AssistantImages
            {
                Api = model.Api,
                Provider = model.Provider,
                Model = model.Id,
                Output = [new ImageContent("QUJD", "image/png")],
                StopReason = ImagesStopReason.Stop,
            });
    }

    private static ModelSpec ImageModel() => new()
    {
        Id = "img-1", Name = "Img", Api = "openrouter-images", Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1", Input = ["image"], Type = ModelType.Image,
    };

    private static ModelSpec ChatModel() => new()
    {
        Id = "chat-1", Name = "Chat", Api = "openai-completions", Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1", Input = ["text"],
    };

    [Fact]
    public async Task GeneratesImagesThroughCapableProvider()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new ImageCapableProvider());

        var result = await models.GenerateImagesAsync(
            ImageModel(), new ImagesContext { Input = [new TextContent("cat")] });

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Single(result.Output.OfType<ImageContent>());
    }

    [Fact]
    public async Task ChatModelIsRejectedForImageGeneration()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new ImageCapableProvider());

        var result = await models.GenerateImagesAsync(
            ChatModel(), new ImagesContext { Input = [new TextContent("cat")] });

        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("not an image model", result.ErrorMessage);
    }

    [Fact]
    public async Task UnknownProviderBecomesErrorResult()
    {
        var models = new Pi.Ai.Models.Models();
        var result = await models.GenerateImagesAsync(
            ImageModel(), new ImagesContext { Input = [new TextContent("cat")] });
        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("Unknown provider: openrouter", result.ErrorMessage);
    }

    [Fact]
    public async Task ProviderWithoutImagesCapabilityBecomesErrorResult()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new ImageCapableProvider()); // 有图片能力
        var other = models.GetProvider("openrouter");
        Assert.NotNull(other);
        models.DeleteProvider("openrouter");
        // 注册一个只支持 chat 的 provider，但模型目录包含该 image 模型：
        // 直接构造仅实现 IProvider 的适配器。
        models.SetProvider(new ChatOnlyProvider());

        var result = await models.GenerateImagesAsync(
            ImageModel(), new ImagesContext { Input = [new TextContent("cat")] });
        Assert.Contains("does not support image generation", result.ErrorMessage);
    }

    private sealed class ChatOnlyProvider : IProvider
    {
        public string Id => "openrouter";
        public string Name => "OpenRouter";
        public string? BaseUrl => null;
        public IReadOnlyList<ModelSpec> GetModels() =>
        [
            new ModelSpec
            {
                Id = "img-1", Name = "Img", Api = "openrouter-images", Provider = Id,
                BaseUrl = "https://openrouter.ai/api/v1", Input = ["image"], Type = ModelType.Image,
            },
        ];
        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();
        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => throw new NotSupportedException();
    }

    [Fact]
    public void GetModelsOfTypeFiltersByType()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new ImageCapableProvider());
        var imageModels = models.GetModelsOfType(ModelType.Image, "openrouter");
        Assert.Single(imageModels);
        Assert.Empty(models.GetModelsOfType(ModelType.Classifier, "openrouter"));
        Assert.Null(models.GetModelOfType(ModelType.Image, "openrouter", "missing"));
        Assert.NotNull(models.GetModelOfType(ModelType.Image, "openrouter", "img-1"));
    }
}
