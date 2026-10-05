using Pi.Ai;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>compat 目录（compat.ts）测试：目录别名、api-dispatch 回退、环境密钥注入、faux 注册。</summary>
public class CompatTests
{
    private static ModelSpec DeepSeekModel() => new()
    {
        Id = "deepseek-chat",
        Name = "DeepSeek Chat",
        Api = "openai-completions",
        Provider = "deepseek",
        BaseUrl = "https://api.deepseek.com/v1",
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

    [Fact]
    public void CatalogAliasesReadBuiltinData()
    {
        // 静态构造触发内建 api 注册。
        Assert.Equal(42, CompatApi.GetProviders().Count);
        Assert.NotNull(CompatApi.GetModel("deepseek", "deepseek-chat"));
        Assert.Equal(2, CompatApi.GetModels("deepseek").Count);
        Assert.Contains("openai-completions", CompatApi.BuiltinApiIds);
        Assert.Contains("anthropic-messages", CompatApi.BuiltinApiIds);
        Assert.Equal(10, CompatApi.BuiltinApiIds.Count);
    }

    [Fact]
    public void UnknownApiThrowsModelsError()
    {
        var model = DeepSeekModel() with { Api = "not-a-real-api" };
        var error = Assert.Throws<ModelsError>(() => CompatApi.StreamSimple(model, []));
        Assert.Contains("No API provider registered", error.Message);
    }

    [Fact]
    public async Task RegistryFallbackInjectsScopedEnvApiKey()
    {
        _ = CompatApi.GetProviders(); // 确保内建快照已建立
        var captured = new List<SimpleStreamOptions?>();
        ApiRegistry.RegisterApiProvider(new ApiProvider
        {
            Api = "openai-completions",
            Stream = (model, _, _, _, _) => DoneStream(model, "override"),
            StreamSimple = (model, _, options, _, _) =>
            {
                captured.Add(options);
                return DoneStream(model, "override");
            },
        }, "compat-test-override");

        try
        {
            var options = new Dictionary<string, object?>
            {
                ["env"] = new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = "env-ds" },
            };
            var message = await CompatApi.CompleteSimpleAsync(DeepSeekModel(), [], options);
            Assert.Equal("override", Assert.IsType<TextContent>(message.Content[0]).Text);
            Assert.Single(captured);
            Assert.Equal("env-ds", captured[0]!.ApiKey);
        }
        finally
        {
            ApiRegistry.UnregisterApiProviders("compat-test-override");
            CompatApi.RegisterBuiltInApiProviders();
        }
    }

    [Fact]
    public async Task ExplicitApiKeyWinsOverEnv()
    {
        _ = CompatApi.GetProviders();
        var captured = new List<SimpleStreamOptions?>();
        ApiRegistry.RegisterApiProvider(new ApiProvider
        {
            Api = "openai-completions",
            Stream = (model, _, _, _, _) => DoneStream(model, "override"),
            StreamSimple = (model, _, options, _, _) =>
            {
                captured.Add(options);
                return DoneStream(model, "override");
            },
        }, "compat-test-explicit");

        try
        {
            var options = new Dictionary<string, object?>
            {
                ["apiKey"] = "explicit-key",
                ["env"] = new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = "env-ds" },
            };
            await CompatApi.CompleteSimpleAsync(DeepSeekModel(), [], options);
            Assert.Equal("explicit-key", captured[0]!.ApiKey);
        }
        finally
        {
            ApiRegistry.UnregisterApiProviders("compat-test-explicit");
            CompatApi.RegisterBuiltInApiProviders();
        }
    }

    [Fact]
    public async Task BuiltinProviderPathIsUsedWhenApiNotOverridden()
    {
        _ = CompatApi.GetProviders();
        // 未被覆盖 → 走内建 provider（deepseek 的 openai-completions 实现）。
        // 指向不可达端口，确认返回的是事件流而非抛出，且以 error 终态收尾。
        var options = new Dictionary<string, object?>
        {
            ["apiKey"] = "dummy",
            ["baseUrl"] = "http://127.0.0.1:1/v1",
        };
        var message = await CompatApi.CompleteSimpleAsync(DeepSeekModel(), [], options);
        Assert.Equal(StopReason.Error, message.StopReason);
    }

    [Fact]
    public async Task FauxProviderRegistrationReplaysAndUnregisters()
    {
        var registration = CompatApi.RegisterFauxProvider();
        try
        {
            Assert.StartsWith("faux-", registration.Api);
            var model = registration.Models[0];
            Assert.Equal(registration.Api, model.Api);

            registration.SetResponses([Faux.Response.Text("hello from faux")]);
            Assert.Equal(1, registration.GetPendingResponseCount());

            var message = await CompatApi.CompleteSimpleAsync(model, []);
            Assert.Equal("hello from faux", Assert.IsType<TextContent>(message.Content[0]).Text);
            Assert.Equal(0, registration.GetPendingResponseCount());

            // 脚本耗尽 → error 终态。
            var exhausted = await CompatApi.CompleteSimpleAsync(model, []);
            Assert.Equal(StopReason.Error, exhausted.StopReason);

            registration.AppendResponses([Faux.Response.Text("second")]);
            var second = await CompatApi.CompleteSimpleAsync(model, []);
            Assert.Equal("second", Assert.IsType<TextContent>(second.Content[0]).Text);
        }
        finally
        {
            registration.Unregister();
        }

        var error = Assert.Throws<ModelsError>(() => CompatApi.StreamSimple(registration.Models[0], []));
        Assert.Contains("No API provider registered", error.Message);
    }
}
