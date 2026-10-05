using Context = Pi.Chord.Context.Context;
using Pi.Chord.Services;
using Pi.Protocol;
using Xunit;

namespace Pi.Server.Tests;

/// <summary>session-router.ts 测试（附加 / 脱离 / 删除 / 排空 / 每客户端串行化）。</summary>
public class SessionRouterTests
{
    private static Context Ctx => Context.Background;

    private sealed record Metadata(string Id) : ISessionMetadata;

    private sealed class FakeSessionAttachment : IRoutedSessionAttachment
    {
        public List<ServiceCall> Calls { get; } = [];

        public int Released { get; private set; }

        public Func<ServiceCall, object?>? Handler { get; set; }

        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
        {
            Calls.Add(call);
            return Task.FromResult(Handler?.Invoke(call));
        }

        public Task ReleaseAsync(Context context)
        {
            Released++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHandle : IRoutedSessionHandle
    {
        public FakeSessionAttachment Attachment { get; } = new();

        public int Closed { get; private set; }

        public Task<Exception?>? Terminated { get; init; }

        public Task<IRoutedSessionAttachment> AttachClientAsync(Context context)
            => Task.FromResult<IRoutedSessionAttachment>(Attachment);

        public Task CloseAsync(Context context)
        {
            Closed++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHost : IServerHost<Metadata>
    {
        public Dictionary<string, FakeHandle> Handles { get; } = new(StringComparer.Ordinal);

        public List<string> Resolved { get; } = [];

        public Exception? ResolveFailure { get; set; }

        public IRoutedServerServiceHost ServerServices => throw new NotSupportedException();

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
        {
            Resolved.Add(sessionId);
            if (ResolveFailure is not null) return Task.FromException<Metadata>(ResolveFailure);
            return Task.FromResult(new Metadata(sessionId));
        }

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => Task.FromResult<IRoutedSessionHandle>(Handles[metadata.Id]);
    }

    private sealed record Published(object Client, RpcTarget? Route);

    private sealed class Harness
    {
        public FakeHost Host { get; } = new();

        public List<Published> Published { get; } = [];

        public List<Exception> Reported { get; } = [];

        public bool Closing { get; set; }

        public SessionRouter<Metadata> Router { get; }

        public Harness()
        {
            Router = new SessionRouter<Metadata>(new SessionRouterOptions<Metadata>
            {
                Host = Host,
                ServerId = "server-1",
                IsClosing = () => Closing,
                PublishAttachment = (client, route, _) =>
                {
                    Published.Add(new Published(client, route));
                    return Task.CompletedTask;
                },
                ReportError = Reported.Add,
            });
        }

        public FakeHandle AddSession(string sessionId)
        {
            var handle = new FakeHandle();
            Host.Handles[sessionId] = handle;
            return handle;
        }
    }

    private static ServiceCall Call(string member = "echo") => new("echoer", member, ["x"]);

    private static ServiceUpdatePublisher NoPublish => (_, _, _) => Task.CompletedTask;

    [Fact]
    public async Task AttachPublishesSessionTargetAndRoutesCalls()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var client = new object();

        await harness.Router.AttachClientAsync(client, "s1", Ctx);

        var published = Assert.Single(harness.Published);
        var target = Assert.IsType<RpcTarget.SessionTarget>(published.Route);
        Assert.Equal("server-1", target.ServerId);
        Assert.Equal("s1", target.SessionId);
        Assert.Equal(["s1"], harness.Host.Resolved);

        handle.Attachment.Handler = _ => "pong";
        var result = await harness.Router.ExecuteServiceCallAsync(Call(), target, client, NoPublish, Ctx);
        Assert.Equal("pong", result);
        Assert.Equal("echo", Assert.Single(handle.Attachment.Calls).Member);

        // ServerTarget 与不匹配的 attachmentId 都拒绝。
        await Assert.ThrowsAsync<SessionNotAttachedError>(() => harness.Router.ExecuteServiceCallAsync(
            Call(), new RpcTarget.ServerTarget("server-1"), client, NoPublish, Ctx));
        await Assert.ThrowsAsync<SessionNotAttachedError>(() => harness.Router.ExecuteServiceCallAsync(
            Call(), new RpcTarget.SessionTarget("server-1", "s1", "other"), client, NoPublish, Ctx));
    }

    [Fact]
    public async Task ReattachingSameSessionIsNoOpAndSwitchingReleases()
    {
        var harness = new Harness();
        harness.AddSession("s1");
        var second = harness.AddSession("s2");
        var client = new object();

        await harness.Router.AttachClientAsync(client, "s1", Ctx);
        await harness.Router.AttachClientAsync(client, "s1", Ctx);
        // 同会话重复附加不重复发布。
        Assert.Single(harness.Published);

        await harness.Router.AttachClientAsync(client, "s2", Ctx);
        Assert.Equal(2, harness.Published.Count);
        var target = Assert.IsType<RpcTarget.SessionTarget>(harness.Published[1].Route);
        Assert.Equal("s2", target.SessionId);
        Assert.Equal("s2", Assert.IsType<Metadata>(new Metadata("s2")).Id);
        Assert.Equal(0, second.Attachment.Released);
    }

    [Fact]
    public async Task DetachReleasesAndPublishesNullRoute()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var client = new object();

        await harness.Router.AttachClientAsync(client, "s1", Ctx);
        await harness.Router.DetachClientAsync(client, Ctx);

        Assert.Equal(1, handle.Attachment.Released);
        Assert.Equal(2, harness.Published.Count);
        Assert.Null(harness.Published[1].Route);

        // 脱离后再调用即未附加。
        await Assert.ThrowsAsync<SessionNotAttachedError>(() => harness.Router.ExecuteServiceCallAsync(
            Call(), new RpcTarget.SessionTarget("server-1", "s1", "x"), client, NoPublish, Ctx));
    }

    [Fact]
    public async Task RemoveSessionReleasesAttachmentsAndClosesHandle()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var client = new object();
        await harness.Router.AttachClientAsync(client, "s1", Ctx);

        await harness.Router.RemoveSessionAsync("s1", Ctx);
        Assert.Equal(1, handle.Attachment.Released);
        Assert.Equal(1, handle.Closed);

        // 再次删除是空操作。
        await harness.Router.RemoveSessionAsync("s1", Ctx);
        Assert.Equal(1, handle.Closed);
    }

    [Fact]
    public async Task DisconnectReleasesWithoutPublishingDetach()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var client = new object();
        await harness.Router.AttachClientAsync(client, "s1", Ctx);

        await harness.Router.DisconnectAsync(client, Ctx);
        Assert.Equal(1, handle.Attachment.Released);
        // 只发布过附加，没有脱离发布（客户端已不可达）。
        Assert.Single(harness.Published);
    }

    [Fact]
    public async Task DrainingRejectsAttachAndRemove()
    {
        var harness = new Harness();
        harness.AddSession("s1");
        harness.Closing = true;

        await Assert.ThrowsAsync<ServerDrainingError>(() =>
            harness.Router.AttachClientAsync(new object(), "s1", Ctx));
        await Assert.ThrowsAsync<ServerDrainingError>(() => harness.Router.RemoveSessionAsync("s1", Ctx));
    }

    [Fact]
    public async Task ResolveFailurePropagates()
    {
        var harness = new Harness();
        harness.Host.ResolveFailure = new SessionNotFoundError();
        await Assert.ThrowsAsync<SessionNotFoundError>(() =>
            harness.Router.AttachClientAsync(new object(), "missing", Ctx));
    }

    [Fact]
    public async Task HandleTerminationInvalidatesAndReleasesAttachments()
    {
        var harness = new Harness();
        var termination = new TaskCompletionSource<Exception?>();
        var handle = new FakeHandle { Terminated = termination.Task };
        harness.Host.Handles["s1"] = handle;
        var client = new object();
        await harness.Router.AttachClientAsync(client, "s1", Ctx);

        termination.SetResult(null);
        // 等失效处理跑完（释放是 fire-and-forget）。
        await Task.Delay(50);

        Assert.Equal(1, handle.Attachment.Released);
        await Assert.ThrowsAsync<SessionNotAttachedError>(() => harness.Router.ExecuteServiceCallAsync(
            Call(), new RpcTarget.SessionTarget("server-1", "s1", "x"), client, NoPublish, Ctx));
    }

    [Fact]
    public async Task CloseReleasesEverythingAndIsIdempotent()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var client = new object();
        await harness.Router.AttachClientAsync(client, "s1", Ctx);

        await harness.Router.CloseAsync(Ctx);
        await harness.Router.CloseAsync(Ctx);
        Assert.Equal(1, handle.Attachment.Released);
        Assert.Equal(1, handle.Closed);
        Assert.Empty(harness.Reported);
    }

    [Fact]
    public async Task PerClientOperationsRunInOrder()
    {
        var harness = new Harness();
        var handle = harness.AddSession("s1");
        var gate = new TaskCompletionSource();
        handle.Attachment.Handler = _ => gate.Task;
        var client = new object();
        await harness.Router.AttachClientAsync(client, "s1", Ctx);
        var target = Assert.IsType<RpcTarget.SessionTarget>(Assert.Single(harness.Published).Route);

        var first = harness.Router.ExecuteServiceCallAsync(Call("first"), target, client, NoPublish, Ctx);
        var second = harness.Router.ExecuteServiceCallAsync(Call("second"), target, client, NoPublish, Ctx);
        gate.SetResult();
        await Task.WhenAll(first, second);

        // 同一客户端的操作串行执行，调用顺序与提交顺序一致。
        Assert.Equal(["first", "second"], handle.Attachment.Calls.Select(call => call.Member).ToArray());
    }
}
