using Pi.Chord.Models;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>ModelCatalog 加载与 flatten 测试。</summary>
public class ModelCatalogTests
{
    [Fact]
    public void LoadsFromEmbeddedResources()
    {
        var openai = ModelCatalog.LoadFromResource("openai");
        Assert.Equal("openai", openai.Provider);

        // flatten：跨 api 分组按模型 id 索引。
        var gpt4o = openai.ChatModels["gpt-4o"];
        Assert.Equal("openai-completions", gpt4o.Api);
        Assert.Equal("https://api.openai.com/v1", gpt4o.BaseUrl);
        Assert.Equal(["text", "image"], gpt4o.Input);
        Assert.Equal(2.5, gpt4o.Cost!.Input);
        Assert.False(gpt4o.Reasoning);

        // chat 是缺省类型（JSON 无 type 字段）。
        Assert.Equal(ModelType.Chat, gpt4o.Type);
        Assert.Equal(4, openai.ChatModels.Count);
        Assert.Empty(openai.ImageModels);
    }

    [Fact]
    public void CrossApiFlattenAndFind()
    {
        var openai = ModelCatalog.LoadFromResource("openai");
        // gpt-5 在 openai-responses 分组，flatten 后与 completions 同表。
        var gpt5 = openai.ChatModels["gpt-5"];
        Assert.Equal("openai-responses", gpt5.Api);
        Assert.True(gpt5.Reasoning);
        Assert.Equal(400000, gpt5.ContextWindow);

        Assert.NotNull(openai.Find("o3"));
        Assert.Null(openai.Find("nonexistent"));
    }

    [Fact]
    public void AnthropicAndDeepseekCatalogs()
    {
        var anthropic = ModelCatalog.LoadFromResource("anthropic");
        var sonnet = anthropic.ChatModels["claude-sonnet-4-5"];
        Assert.Equal("anthropic-messages", sonnet.Api);
        Assert.True(sonnet.Reasoning);
        Assert.Equal(200000, sonnet.ContextWindow);
        Assert.Equal("anthropic", sonnet.Provider);

        var deepseek = ModelCatalog.LoadFromResource("deepseek");
        Assert.Equal(2, deepseek.ChatModels.Count);
        Assert.True(deepseek.ChatModels["deepseek-reasoner"].Reasoning);
    }

    [Fact]
    public void TypeDispatchAndExtraPreserved()
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "openai-completions": {
                "chat:m1": {"id":"m1","name":"M1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"contextWindow":1000,"maxTokens":100,"custom":"kept"},
                "image:d1": {"id":"d1","name":"D1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"type":"image","output":["image"]},
                "classifier:c1": {"id":"c1","name":"C1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"type":"classifier"}
              }
            }
            """);
        var groups = Assert.IsType<System.Text.Json.Nodes.JsonObject>(parsed);
        var catalog = ModelCatalog.Load("p", groups);
        Assert.Single(catalog.ChatModels);
        Assert.Single(catalog.ImageModels);
        Assert.Single(catalog.ClassifierModels);
        // 未识别字段保留。
        Assert.Equal("kept", catalog.ChatModels["m1"].Extra!["custom"]!.GetValue<string>());
    }
}
