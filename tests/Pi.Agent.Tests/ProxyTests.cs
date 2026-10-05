using System.Net;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Agent.Tests;

/// <summary>
/// <see cref="Proxy.StreamProxy"/> 测试。对应 TS <c>test/proxy.test.ts</c>：
/// toolcall_end 元数据保留、无换行终态处理、EOF 无终态补 error；
/// 另补请求形状与错误状态断言（TS 未覆盖）。
/// </summary>
public class ProxyTests
{
    private static readonly Model TestModel = new(
        Id: "gpt-5.4",
        Name: "GPT-5.4",
        Api: "openai-responses",
        Provider: "openai");

    private static ProxyStreamOptions TestOptions(string proxyUrl = "https://proxy.example.com")
        => new() { AuthToken = "test-token", ProxyUrl = proxyUrl };

    [Fact]
    public async Task PreservesToolCallMetadataReceivedOnlyOnToolCallEnd()
    {
        var body = SseBody(
            Ev("start"),
            Ev("toolcall_start", ("contentIndex", 0), ("id", "call_test|fc_test"), ("toolName", "lookup")),
            Ev("toolcall_delta", ("contentIndex", 0), ("delta", "{\"value\":\"hello\"}")),
            Ev("toolcall_end", ("contentIndex", 0), ("toolCall", new JsonObject
            {
                ["type"] = "toolCall",
                ["id"] = "call_test|fc_test",
                ["name"] = "lookup",
                ["arguments"] = new JsonObject { ["value"] = "hello" },
                ["namespace"] = "dynamic_tools",
            })),
            Ev("done", ("reason", "toolUse"), ("usage", ZeroUsage())));

        var (events, result) = await RunAsync(body);

        var endEvent = Assert.Single(events.OfType<AssistantMessageEvent.ToolCallEnd>());
        Assert.Equal("dynamic_tools", endEvent.ToolCall.Namespace);

        var call = Assert.IsType<ToolCallContent>(Assert.Single(result.Content));
        Assert.Equal("lookup", call.Name);
        var args = Assert.IsType<JsonObject>(call.Arguments);
        Assert.Equal("hello", args["value"]?.GetValue<string>());
        Assert.Equal("dynamic_tools", call.Namespace);
    }

    // Regression: 终态事件可能不带尾随换行，必须照常处理。
    [Fact]
    public async Task ProcessesTerminalMetadataWhenEventIsNotNewlineTerminated()
    {
        var done = Ev("done", ("reason", "stop"), ("usage", ZeroUsage()), ("providerThinkingLevel", "high"));
        var body = SseBody(Ev("start")) + $"data: {done.ToJsonString()}";

        var (events, result) = await RunAsync(body);

        Assert.Equal(["start", "done"], events.Select(EventType));
        Assert.Equal(StopReason.Stop, result.StopReason);
        Assert.Equal("high", result.ProviderThinkingLevel);
    }

    [Fact]
    public async Task EmitsErrorInsteadOfHangingWhenStreamEndsWithoutTerminalEvent()
    {
        var (events, result) = await RunAsync(SseBody(Ev("start")));

        Assert.Equal(["start", "error"], events.Select(EventType));
        Assert.Equal(StopReason.Error, result.StopReason);
        Assert.Contains("Connection closed by proxy server", result.ErrorMessage);
    }

    [Fact]
    public async Task PostsAuthenticatedRequestToProxyStreamEndpoint()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseBody(Ev("done", ("reason", "stop"), ("usage", ZeroUsage())))),
        });

        var (_, result) = await RunAsync(handler);

        Assert.Equal(StopReason.Stop, result.StopReason);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://proxy.example.com/api/stream", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SurfacesProxyErrorStatusAsErrorEvent()
    {
        var (events, result) = await RunAsync(
            new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"bad token\"}"),
            }));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
        Assert.Contains("bad token", result.ErrorMessage);
    }

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Result)> RunAsync(
        FakeHandler handler)
    {
        var client = new HttpClient(handler);
        var stream = Proxy.StreamProxy(
            TestModel,
            Transcript.NormalizeContext("", null, []),
            TestOptions(),
            client);
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream.ConfigureAwait(false))
            events.Add(@event);
        var result = await stream.WaitForDoneAsync();
        return (events, result);
    }

    private static async Task<(List<AssistantMessageEvent> Events, AssistantMessage Result)> RunAsync(string body)
        => await RunAsync(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        }));

    private static string EventType(AssistantMessageEvent @event) => @event switch
    {
        AssistantMessageEvent.Start => "start",
        AssistantMessageEvent.TextStart => "text_start",
        AssistantMessageEvent.TextDelta => "text_delta",
        AssistantMessageEvent.TextEnd => "text_end",
        AssistantMessageEvent.ThinkingStart => "thinking_start",
        AssistantMessageEvent.ThinkingDelta => "thinking_delta",
        AssistantMessageEvent.ThinkingEnd => "thinking_end",
        AssistantMessageEvent.ToolCallStart => "toolcall_start",
        AssistantMessageEvent.ToolCallDelta => "toolcall_delta",
        AssistantMessageEvent.ToolCallEnd => "toolcall_end",
        AssistantMessageEvent.Done => "done",
        AssistantMessageEvent.Error => "error",
        _ => @event.GetType().Name,
    };

    private static string SseBody(params JsonObject[] events)
        => string.Concat(events.Select(e => $"data: {e.ToJsonString()}\n\n"));

    private static JsonObject Ev(string type, params (string Key, JsonNode? Value)[] fields)
    {
        var obj = new JsonObject { ["type"] = type };
        foreach (var (key, value) in fields) obj[key] = value;
        return obj;
    }

    private static JsonObject ZeroUsage() => new()
    {
        ["input"] = 0,
        ["output"] = 0,
        ["cacheRead"] = 0,
        ["cacheWrite"] = 0,
        ["totalTokens"] = 0,
        ["cost"] = new JsonObject
        {
            ["input"] = 0,
            ["output"] = 0,
            ["cacheRead"] = 0,
            ["cacheWrite"] = 0,
            ["total"] = 0,
        },
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
