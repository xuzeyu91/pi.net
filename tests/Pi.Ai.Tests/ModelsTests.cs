using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>Models 编排门面测试（注册/查询/分发/best-effort/归属校验）。</summary>
public class ModelsTests
{
    /// <summary>测试 provider：基于 ModelCatalog 的 faux 实现。</summary>
    private sealed class CatalogProvider(string id, ModelCatalog catalog, bool throwOnGetModels = false) : IProvider
    {
        public string Id => id;

        public string Name => id;

        public string? BaseUrl => null;

        public IReadOnlyList<ModelSpec> GetModels()
        {
            if (throwOnGetModels) throw new InvalidOperationException("ill-behaved provider");
            return catalog.ChatModels.Values.ToList();
        }

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => StreamSimple(model, context, options);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent($"echo:{model.Id}")],
                Types.StopReason.Stop,
                UsageStats: new Usage(1, 1),
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider,
                Timestamp: System.DateTimeOffset.Now.ToUnixTimeMilliseconds());
            stream.Push(new AssistantMessageEvent.Start(message));
            stream.Push(new AssistantMessageEvent.Done(message));
            stream.End(message);
            return stream;
        }
    }

    [Fact]
    public async Task RegistersDispatchesAndCompletes()
    {
        var catalog = ModelCatalog.LoadFromResource("openai");
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new CatalogProvider("openai", catalog));

        // 查询。
        Assert.Equal(4, models.GetModels().Count);
        Assert.Equal(4, models.GetModels("openai").Count);
        Assert.Empty(models.GetModels("unknown"));
        Assert.NotNull(models.GetModel("openai", "gpt-4o"));
        Assert.Null(models.GetModel("openai", "nonexistent"));

        // 分发：model.provider 指向注册的 provider。
        var gpt4o = models.GetModel("openai", "gpt-4o")!;
        var message = await models.CompleteSimpleAsync(gpt4o,
            [Messages.UserText("hi")]);
        Assert.Equal("echo:gpt-4o", ((TextContent)message.Content[0]).Text);
        Assert.Equal(Types.StopReason.Stop, message.StopReason);
    }

    [Fact]
    public void UnknownProviderAndUnlistedModelRejected()
    {
        var catalog = ModelCatalog.LoadFromResource("openai");
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new CatalogProvider("openai", catalog));

        var foreign = catalog.ChatModels["gpt-4o"] with { Provider = "anthropic" };
        Assert.Throws<KeyNotFoundException>(() =>
            models.StreamSimple(foreign, [Messages.UserText("hi")]));

        var unlisted = catalog.ChatModels["gpt-4o"] with { Id = "not-in-catalog" };
        Assert.Throws<KeyNotFoundException>(() =>
            models.StreamSimple(unlisted, [Messages.UserText("hi")]));
    }

    [Fact]
    public void IllBehavedProviderYieldsNoModels()
    {
        var catalog = ModelCatalog.LoadFromResource("deepseek");
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new CatalogProvider("good", catalog));
        models.SetProvider(new CatalogProvider("bad", catalog, throwOnGetModels: true));

        // best-effort：抛错的 provider 产出零模型，不影响其余。
        Assert.Equal(2, models.GetModels().Count);
        Assert.Empty(models.GetModels("bad"));
    }

    [Fact]
    public void DeleteAndClearProviders()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(new CatalogProvider("openai", ModelCatalog.LoadFromResource("openai")));
        models.SetProvider(new CatalogProvider("anthropic", ModelCatalog.LoadFromResource("anthropic")));
        Assert.Equal(2, models.GetProviders().Count);

        Assert.True(models.DeleteProvider("openai"));
        Assert.Single(models.GetProviders());
        models.ClearProviders();
        Assert.Empty(models.GetProviders());
    }
}
