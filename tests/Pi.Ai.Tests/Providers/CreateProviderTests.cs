using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>createProvider 工厂语义测试（models.ts createProvider）。</summary>
public class CreateProviderTests
{
    private static ModelSpec Model(string id, string api, string provider, ModelType type = ModelType.Chat)
        => new()
        {
            Id = id,
            Name = id,
            Api = api,
            Provider = provider,
            BaseUrl = "https://example.invalid",
            Type = type,
        };

    private static IAssistantMessageEventStream DoneStream(ModelSpec model, string text)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage([new TextContent(text)], StopReason.Stop,
            UsageStats: new Usage(1, 1), Model: model.Id, Api: model.Api, Provider: model.Provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }

    private static ProviderStreams Streams(string marker) => new()
    {
        Stream = (model, _, _) => DoneStream(model, marker),
        StreamSimple = (model, _, _) => DoneStream(model, marker),
    };

    private static ProviderAuth Auth() => new()
    {
        ApiKey = new EnvApiKeyAuth("Test API key", ["TEST_API_KEY"]),
    };

    [Fact]
    public void RequiresAtLeastOneImplementation()
    {
        var error = Assert.Throws<ArgumentException>(() => ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "empty",
            Models = [Model("m1", "api-a", "empty")],
            Auth = Auth(),
        }));
        Assert.Contains("at least one of", error.Message);
    }

    [Fact]
    public async Task SingleApiServesChatModelsOnly()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models =
            [
                Model("chat-1", "api-a", "p"),
                Model("img-1", "api-img", "p", ModelType.Image),
                Model("cls-1", "api-cls", "p", ModelType.Classifier),
            ],
            Auth = Auth(),
            Api = Streams("single"),
        });

        Assert.Equal("p", provider.Name); // name 缺省取 id
        Assert.Single(provider.GetModels());
        Assert.Equal("chat-1", provider.GetModels()[0].Id);
        Assert.Equal(3, provider.GetAllModels().Count);

        var message = await provider.StreamSimple(provider.GetModels()[0], [], null).WaitForDoneAsync();
        Assert.Equal("single", Assert.IsType<TextContent>(message.Content[0]).Text);
    }

    [Fact]
    public async Task ApiMapDispatchesByModelApi()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [Model("m-a", "api-a", "p"), Model("m-b", "api-b", "p"), Model("m-c", "api-c", "p")],
            Auth = Auth(),
            ApiByApi = new Dictionary<string, ProviderStreams>
            {
                ["api-a"] = Streams("A"),
                ["api-b"] = Streams("B"),
            },
        });

        Assert.Equal("A", await TextAsync(provider, "m-a"));
        Assert.Equal("B", await TextAsync(provider, "m-b"));

        // 无实现的 api → error 终态流（不抛出）。
        var message = await provider.StreamSimple(provider.GetModels().First(m => m.Id == "m-c"), [], null)
            .WaitForDoneAsync();
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("has no API implementation", message.ErrorMessage);
    }

    [Fact]
    public async Task ImagesAndClassifiersDispatchByApi()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [Model("img-1", "api-img", "p", ModelType.Image), Model("cls-1", "api-cls", "p", ModelType.Classifier)],
            Auth = Auth(),
            Images = new Dictionary<string, ProviderImages>
            {
                ["api-img"] = new()
                {
                    GenerateImages = (model, _, _, _) => Task.FromResult(new AssistantImages
                    {
                        Api = model.Api, Provider = model.Provider, Model = model.Id,
                    }),
                },
            },
            Classifiers = new Dictionary<string, ProviderClassifier>
            {
                ["api-cls"] = new()
                {
                    Classify = (model, _, _, _) => Task.FromResult(new ClassifierResult
                    {
                        Api = model.Api, Provider = model.Provider, Model = model.Id,
                    }),
                },
            },
        });

        Assert.True(provider is IImagesProvider);
        Assert.True(provider is IClassifierProvider);

        var images = await ((IImagesProvider)provider).GenerateImagesAsync(
            provider.GetAllModels().First(m => m.Type == ModelType.Image), new ImagesContext { Input = [] },
            null, default);
        Assert.Equal(ImagesStopReason.Stop, images.StopReason);

        var classified = await ((IClassifierProvider)provider).ClassifyAsync(
            provider.GetAllModels().First(m => m.Type == ModelType.Classifier),
            new ClassifierContext { State = new System.Text.Json.Nodes.JsonObject(), Questions = new Dictionary<string, ClassifierQuestion>() },
            null, default);
        Assert.Equal(ClassifierStopReason.Stop, classified.StopReason);
    }

    [Fact]
    public async Task MissingImagesImplementationYieldsErrorResultNotThrow()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [Model("img-1", "api-other", "p", ModelType.Image)],
            Auth = Auth(),
            Images = new Dictionary<string, ProviderImages>
            {
                ["api-img"] = new()
                {
                    GenerateImages = (model, _, _, _) => Task.FromResult(new AssistantImages
                    {
                        Api = model.Api, Provider = model.Provider, Model = model.Id,
                    }),
                },
            },
        });

        var images = await ((IImagesProvider)provider).GenerateImagesAsync(
            provider.GetAllModels()[0], new ImagesContext { Input = [] }, null, default);
        Assert.Equal(ImagesStopReason.Error, images.StopReason);
        Assert.Contains("no image generation implementation", images.ErrorMessage);
    }

    [Fact]
    public void FilterModelsReceivesCredential()
    {
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [Model("m-1", "api-a", "p"), Model("m-2", "api-a", "p")],
            Auth = Auth(),
            Api = Streams("x"),
            FilterModels = (models, credential) => credential is Credential.OAuth oauth
                && oauth.AvailableModelIds is { } ids
                ? models.Where(model => ids.Contains(model.Id)).ToList()
                : models,
        });

        Assert.Equal(2, provider.FilterModels(provider.GetModels(), null).Count);
        var filtered = provider.FilterModels(provider.GetModels(),
            new Credential.OAuth("refresh", "access", long.MaxValue) { AvailableModelIds = ["m-2"] });
        Assert.Single(filtered);
        Assert.Equal("m-2", filtered[0].Id);
    }

    private static async Task<string> TextAsync(IProvider provider, string modelId)
    {
        var model = provider.GetModels().First(m => m.Id == modelId);
        var message = await provider.StreamSimple(model, [], null).WaitForDoneAsync();
        return Assert.IsType<TextContent>(message.Content[0]).Text;
    }
}

/// <summary>延后响应（deferred）全链路测试。</summary>
public class DeferredTests
{
    private static ModelSpec Model(string provider)
        => new() { Id = "m-1", Name = "M", Api = "api-a", Provider = provider, BaseUrl = "https://example.invalid" };

    private static IAssistantMessageEventStream DoneStream(ModelSpec model, string text)
    {
        var stream = new AssistantMessageEventStream();
        var message = new AssistantMessage([new TextContent(text)], StopReason.Stop,
            UsageStats: new Usage(1, 1), Model: model.Id, Api: model.Api, Provider: model.Provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }

    private static DeferredHandle Handle() => new()
    {
        Provider = "p", ModelId = "m-1", Api = "api-a", Id = "tok-1",
    };

    [Fact]
    public async Task FetchDeferredRunsProviderImplementation()
    {
        var model = Model("p");
        var cancelled = new List<string>();
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [model],
            Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("k", ["TEST_API_KEY"]) },
            Api = new ProviderStreams
            {
                Stream = (m, _, _) => DoneStream(m, "stream"),
                StreamSimple = (m, _, _) => DoneStream(m, "stream"),
                FetchDeferred = (m, handle, _) => DoneStream(m, $"deferred:{handle.Id}"),
                CancelDeferred = (m, handle, _, _) =>
                {
                    cancelled.Add(handle.Id);
                    return Task.CompletedTask;
                },
            },
        });

        var models = new Pi.Ai.Models.Models();
        models.SetProvider(provider);

        var message = await models.FetchDeferredAsync(model, Handle());
        Assert.Equal("deferred:tok-1", Assert.IsType<TextContent>(message.Content[0]).Text);

        await models.CancelDeferredAsync(model, Handle());
        Assert.Equal(new[] { "tok-1" }, cancelled);
    }

    [Fact]
    public async Task ProviderWithoutDeferredFailsWithErrorEvent()
    {
        var model = Model("p");
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = "p",
            Models = [model],
            Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("k", ["TEST_API_KEY"]) },
            Api = new ProviderStreams
            {
                Stream = (m, _, _) => DoneStream(m, "stream"),
                StreamSimple = (m, _, _) => DoneStream(m, "stream"),
            },
        });

        var models = new Pi.Ai.Models.Models();
        models.SetProvider(provider);

        // 工厂未暴露 StreamDeferred → Models 报错（延迟流内以 error 终态呈现）。
        Assert.Null(provider.StreamDeferred(model, Handle()));
        var message = await models.FetchDeferredAsync(model, Handle());
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("does not support deferred responses", message.ErrorMessage);

        await Assert.ThrowsAsync<Pi.Ai.Utils.ModelsError>(() => models.CancelDeferredAsync(model, Handle()));
    }

    [Fact]
    public void AssistantMessageCarriesDeferredHandle()
    {
        var handle = Handle();
        var message = new AssistantMessage([], StopReason.Deferred) { Deferred = handle };
        Assert.Same(handle, message.Deferred);
        Assert.Equal("tok-1", message.Deferred!.Id);
    }
}
