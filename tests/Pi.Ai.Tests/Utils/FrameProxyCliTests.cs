using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>utils/assistant-message-frame.ts 测试（帧编码与回放）。</summary>
public class AssistantMessageFrameTests
{
    private static AssistantMessage Start() => new(
        Content: [], StopReason: StopReason.Pending, UsageStats: new Usage(0, 0),
        Model: "m", Api: "api", Provider: "p", Timestamp: 1);

    [Fact]
    public void EncodesAndReducesTextStream()
    {
        var encoder = new AssistantMessageFrameEncoder();
        var frames = new List<AssistantMessageFrame>();
        var start = Start();

        var startFrame = encoder.Encode(new AssistantMessageEvent.Start(start));
        Assert.IsType<AssistantMessageFrame.Start>(startFrame);
        frames.Add(startFrame!);

        // 起始块为空文本 → 增量不被起始快照覆盖。
        var partial = start with { Content = [new TextContent("")] };
        frames.Add(encoder.Encode(new AssistantMessageEvent.TextStart(0, partial))!);
        var delta = encoder.Encode(new AssistantMessageEvent.TextDelta(0, "he", 1, partial with
        {
            Content = [new TextContent("he")],
        }));
        Assert.IsType<AssistantMessageFrame.TextDelta>(delta);
        frames.Add(delta!);

        var finalPartial = start with { Content = [new TextContent("hello")] };
        frames.Add(encoder.Encode(new AssistantMessageEvent.TextEnd(0, "hello", finalPartial))!);

        var reduced = AssistantMessageFrameReducer.Reduce(frames);
        Assert.NotNull(reduced);
        Assert.Equal("hello", Assert.IsType<TextContent>(reduced!.Content[0]).Text);
        Assert.Equal(StopReason.Pending, reduced.StopReason);
    }

    [Fact]
    public void SuppressesDeltasAlreadyCoveredByStartSnapshot()
    {
        var encoder = new AssistantMessageFrameEncoder();
        var start = Start();
        encoder.Encode(new AssistantMessageEvent.Start(start));

        var partial = start with { Content = [new TextContent("hello")] };
        encoder.Encode(new AssistantMessageEvent.TextStart(0, partial));
        // 起始快照已有 5 字符，重放 3 字符增量应被完全覆盖 → 无帧。
        Assert.Null(encoder.Encode(new AssistantMessageEvent.TextDelta(0, "hel", 1, partial)));
    }

    [Fact]
    public void TerminalEventsProduceNoFramesAndCloseEncoder()
    {
        var encoder = new AssistantMessageFrameEncoder();
        var start = Start();
        encoder.Encode(new AssistantMessageEvent.Start(start));

        var done = new AssistantMessage([], StopReason.Stop, UsageStats: new Usage(1, 1));
        Assert.Null(encoder.Encode(new AssistantMessageEvent.Done(StopReason.Stop, done)));
        Assert.Throws<InvalidOperationException>(() => encoder.Encode(new AssistantMessageEvent.Start(start)));
    }

    [Fact]
    public void RejectsSecondStartAndEventBeforeStart()
    {
        var encoder = new AssistantMessageFrameEncoder();
        var start = Start();
        encoder.Encode(new AssistantMessageEvent.Start(start));
        Assert.Throws<InvalidOperationException>(() => encoder.Encode(new AssistantMessageEvent.Start(start)));

        var fresh = new AssistantMessageFrameEncoder();
        Assert.Throws<InvalidOperationException>(() => fresh.Encode(
            new AssistantMessageEvent.TextDelta(0, "x", 1, start)));
    }

    [Fact]
    public void ReducesToolCallCheckpointAndEnd()
    {
        var encoder = new AssistantMessageFrameEncoder();
        var frames = new List<AssistantMessageFrame>();
        var start = Start();
        frames.Add(encoder.Encode(new AssistantMessageEvent.Start(start))!);

        var toolCall = new ToolCallContent("id-1", "read", new JsonObject { ["a"] = 1 });
        var partial = start with { Content = [toolCall] };
        frames.Add(encoder.Encode(new AssistantMessageEvent.ToolCallStart(0, partial))!);

        // 起始快照非空 → 走 catch-up，命中前缀后发 checkpoint。
        var checkpoint = encoder.Encode(new AssistantMessageEvent.ToolCallDelta(
            0, 0, "{\"a\":1,\"b\":2}", partial));
        Assert.IsType<AssistantMessageFrame.ToolCallCheckpoint>(checkpoint);
        frames.Add(checkpoint!);

        var finalToolCall = new ToolCallContent("id-1", "read", new JsonObject { ["a"] = 1, ["b"] = 2 });
        frames.Add(encoder.Encode(new AssistantMessageEvent.ToolCallEnd(
            0, finalToolCall, start with { Content = [finalToolCall] }))!);

        var reduced = AssistantMessageFrameReducer.Reduce(frames);
        Assert.NotNull(reduced);
        var block = Assert.IsType<ToolCallContent>(reduced!.Content[0]);
        Assert.Equal("id-1", block.Id);
        Assert.Equal("read", block.Name);
        Assert.Equal(2, ((JsonObject)block.Arguments!)["b"]!.GetValue<int>());
    }

    [Fact]
    public void ReduceWithoutStartReturnsNull()
    {
        Assert.Null(AssistantMessageFrameReducer.Reduce(
            [new AssistantMessageFrame.TextDelta(0, "x")]));
    }

    [Fact]
    public void ReduceRejectsGapsAndDuplicateStarts()
    {
        var start = Start();
        var frames = new List<AssistantMessageFrame>
        {
            new AssistantMessageFrame.Start(start),
            new AssistantMessageFrame.Start(start),
        };
        Assert.Throws<InvalidOperationException>(() => AssistantMessageFrameReducer.Reduce(frames));

        Assert.Throws<InvalidOperationException>(() => AssistantMessageFrameReducer.Reduce(
        [
            new AssistantMessageFrame.Start(start),
            new AssistantMessageFrame.TextStart(2, new TextContent("x")),
        ]));
    }
}

/// <summary>utils/node-http-proxy.ts 测试（代理与 no_proxy 规则）。</summary>
public class NodeHttpProxyTests
{
    private static readonly string[] ProxyEnvNames =
        ["http_proxy", "https_proxy", "all_proxy", "no_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    private static IReadOnlyDictionary<string, string> Env(params (string Key, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    /// <summary>
    /// 本机可能已设置 *_proxy，会污染 GetProxyEnv 的进程环境回退。
    /// 测试期间清空这些变量，结束后恢复（对齐 TS 测试的进程隔离）。
    /// </summary>
    private static void WithCleanProxyEnv(Action action)
    {
        var saved = ProxyEnvNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in ProxyEnvNames) Environment.SetEnvironmentVariable(name, null);
            action();
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void ResolvesSchemeProxyAndAllProxyFallback()
    {
    WithCleanProxyEnv(() =>
    {
            Assert.Equal("http://proxy.local:8080",
                NodeHttpProxy.GetProxyForUrl("https://api.example.com/v1",
                    Env(("https_proxy", "http://proxy.local:8080"))));
            // 无 scheme 前缀时补协议。
            Assert.Equal("https://proxy.local:8080",
                NodeHttpProxy.GetProxyForUrl("https://api.example.com/v1", Env(("https_proxy", "proxy.local:8080"))));
            Assert.Equal("http://all.local:3128",
                NodeHttpProxy.GetProxyForUrl("https://api.example.com", Env(("all_proxy", "http://all.local:3128"))));
            Assert.Equal("", NodeHttpProxy.GetProxyForUrl("https://api.example.com", Env(("http_proxy", "http://p:1"))));
    });

    }

    [Fact]
    public void HonorsNoProxyRules()
    {
    WithCleanProxyEnv(() =>
    {
            Assert.Equal("", NodeHttpProxy.GetProxyForUrl("https://api.example.com",
                Env(("https_proxy", "http://p:1"), ("no_proxy", "example.com"))));
            Assert.Equal("", NodeHttpProxy.GetProxyForUrl("https://api.example.com",
                Env(("https_proxy", "http://p:1"), ("no_proxy", "*.example.com"))));
            Assert.Equal("", NodeHttpProxy.GetProxyForUrl("https://api.example.com",
                Env(("https_proxy", "http://p:1"), ("no_proxy", "*"))));
            // 端口不匹配则不豁免。
            Assert.Equal("http://p:1", NodeHttpProxy.GetProxyForUrl("https://api.example.com",
                Env(("https_proxy", "http://p:1"), ("no_proxy", "api.example.com:8443"))));
            // 其他主机不受影响。
            Assert.Equal("http://p:1", NodeHttpProxy.GetProxyForUrl("https://other.test",
                Env(("https_proxy", "http://p:1"), ("no_proxy", "example.com"))));
    });

    }

    [Fact]
    public void RejectsUnsupportedProxyProtocol()
    {
        var error = Assert.Throws<InvalidOperationException>(() => NodeHttpProxy.ResolveHttpProxyUrlForTarget(
            "https://api.example.com", Env(("https_proxy", "socks5://proxy.local:1080"))));
        Assert.Contains(NodeHttpProxy.UnsupportedProxyProtocolMessage, error.Message);
    }

    [Fact]
    public void ReturnsNullWithoutProxy()
    {
    WithCleanProxyEnv(() =>
    {
            Assert.Null(NodeHttpProxy.ResolveHttpProxyUrlForTarget("https://api.example.com", Env(("other", "x"))));
    });

    }
}

/// <summary>cli.ts 测试（list/help/未知命令与 auth.json 读写）。</summary>
public class CliTests
{
    [Fact]
    public async Task ListPrintsOAuthProviders()
    {
        var output = new StringWriter();
        var code = await Cli.RunAsync(["list"], new StringReader(""), output, new StringWriter());
        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("anthropic", text);
        Assert.Contains("github-copilot", text);
        // 无 OAuth 的家不出现在列表里。
        Assert.DoesNotContain("deepseek", text);
    }

    [Fact]
    public async Task HelpAndUnknownCommand()
    {
        var help = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync([], new StringReader(""), help, new StringWriter()));
        Assert.Contains("Usage: pi-ai", help.ToString());

        var error = new StringWriter();
        Assert.Equal(1, await Cli.RunAsync(["nope"], new StringReader(""), new StringWriter(), error));
        Assert.Contains("Unknown command: nope", error.ToString());
    }

    [Fact]
    public async Task UnknownProviderFailsLogin()
    {
        var error = new StringWriter();
        var code = await Cli.RunAsync(["login", "not-a-provider"], new StringReader(""),
            new StringWriter(), error);
        Assert.Equal(1, code);
        Assert.Contains("Unknown provider: not-a-provider", error.ToString());
    }

    [Fact]
    public void AuthFileRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-ai-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Empty(Cli.LoadAuth(directory));
            Cli.SaveAuth(new Dictionary<string, Credential>
            {
                ["anthropic"] = new Credential.OAuth("refresh-token", "access-token", 1_700_000_000_000),
            }, directory);

            var loaded = Cli.LoadAuth(directory);
            var oauth = Assert.IsType<Credential.OAuth>(loaded["anthropic"]);
            Assert.Equal("refresh-token", oauth.Refresh);
            Assert.Equal("access-token", oauth.Access);
            Assert.Equal(1_700_000_000_000, oauth.Expires);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
