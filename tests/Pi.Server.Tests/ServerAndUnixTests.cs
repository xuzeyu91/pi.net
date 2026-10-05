using System.Net.Sockets;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;

namespace Pi.Server.Tests;

/// <summary>server.ts + transports/unix/* 测试（握手、目标路由、监听器生命周期）。</summary>
public class ServerAndUnixTests
{
    private const string TestServerId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private sealed record Metadata(string Id) : ISessionMetadata;

    private sealed class FakeServerServiceAttachment : IRoutedServerServiceAttachment
    {
        public List<ServiceCall> Calls { get; } = [];

        public int Released { get; private set; }

        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
        {
            Calls.Add(call);
            return Task.FromResult<object?>($"handled:{call.ServiceId}.{call.Member}");
        }

        public Task ReleaseAsync(Context context)
        {
            Released++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServerServiceHost : IRoutedServerServiceHost
    {
        public FakeServerServiceAttachment Attachment { get; } = new();

        public Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
            Context context)
            => Task.FromResult<IRoutedServerServiceAttachment>(Attachment);
    }

    private sealed class FakeHost : IServerHost<Metadata>
    {
        public FakeServerServiceHost Services { get; } = new();

        public IRoutedServerServiceHost ServerServices => Services;

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
            => Task.FromResult(new Metadata(sessionId));

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => throw new NotSupportedException();
    }

    private static string TempSocketPath()
        => Path.Combine(Path.GetTempPath(), $"pi-{Guid.NewGuid():N}"[..11] + ".sock");

    private static async Task<Socket> ConnectAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return socket;
    }

    private static async Task<ServerMessage> ReadMessageAsync(Socket socket, ProtocolCodec.ServerMessageDecoder decoder)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            if (read == 0) throw new InvalidOperationException("socket closed before a message arrived");
            var messages = decoder.Push(buffer[..read]);
            if (messages.Count > 0) return messages[0];
        }
    }

    [Fact]
    public void ValidatesServerOptions()
    {
        var host = new FakeHost();
        Assert.Throws<ArgumentException>(() => new Server<Metadata>(host, new ServerOptions
        {
            Listeners = [],
            ServerId = "not-a-uuid",
        }));
        Assert.Throws<ArgumentException>(() => new Server<Metadata>(host, new ServerOptions
        {
            Listeners = [],
            ServerId = TestServerId,
            MaxFrameLength = 0,
        }));
        Assert.Throws<ArgumentException>(() => new Server<Metadata>(host, new ServerOptions
        {
            Listeners = [],
            ServerId = TestServerId,
            HandshakeTimeoutMs = -1,
        }));
    }

    [Fact]
    public void UnixSocketAddressRequiresCanonicalUuid()
    {
        var directory = Path.GetTempPath();
        Assert.Equal(Path.Combine(directory, $"{TestServerId}.sock"),
            UnixSocketAddress.GetUnixSocketPath(TestServerId, directory));
        Assert.Throws<ArgumentException>(() => UnixSocketAddress.GetUnixSocketPath("abc", directory));
        // 大写不是规范形式。
        Assert.Throws<ArgumentException>(() => UnixSocketAddress.GetUnixSocketPath(
            TestServerId.ToUpperInvariant(), directory));
    }

    [Fact]
    public async Task HandshakeThenRouteServerScopedCall()
    {
        var path = TempSocketPath();
        var host = new FakeHost();
        var counts = new List<int>();
        var server = new Server<Metadata>(host, new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = TestServerId,
            OnConnectionCountChanged = counts.Add,
        });

        try
        {
            await server.StartAsync();
            using var socket = await ConnectAsync(path);
            var decoder = new ProtocolCodec.ServerMessageDecoder();

            await socket.SendAsync(ProtocolCodec.EncodeClientMessage(new ClientMessage.Hello(ProtocolVersion.Current)));
            var hello = Assert.IsType<ServerMessage.Hello>(await ReadMessageAsync(socket, decoder));
            Assert.Equal(TestServerId, hello.ServerId);
            Assert.Equal(ProtocolVersion.Current, hello.Version);

            // 服务端域调用：目标为 ServerTarget。
            var call = new Dictionary<string, object?>
            {
                ["serviceId"] = "echoer",
                ["member"] = "echo",
                ["args"] = new List<object?> { "x" },
            };
            await socket.SendAsync(ProtocolCodec.EncodeClientMessage(
                new ClientMessage.Request("r1", new RpcTarget.ServerTarget(TestServerId), call)));
            var response = Assert.IsType<ServerMessage.ResponseOk>(await ReadMessageAsync(socket, decoder));
            Assert.Equal("r1", response.Id);
            Assert.Equal("handled:echoer.echo", response.Result);
            Assert.Equal("echo", Assert.Single(host.Services.Attachment.Calls).Member);

            // 发往别的 serverId 的调用被拒。
            await socket.SendAsync(ProtocolCodec.EncodeClientMessage(new ClientMessage.Request(
                "r2", new RpcTarget.ServerTarget("11111111-1111-4111-8111-111111111111"), call)));
            var wrongServer = Assert.IsType<ServerMessage.ResponseError>(await ReadMessageAsync(socket, decoder));
            Assert.Equal("wrong_server", wrongServer.Error.Code);

            Assert.Contains(1, counts);
        }
        finally
        {
            await server.CloseAsync();
            await server.Closed;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedVersionAndNonHelloFirstMessage()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new FakeHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = TestServerId,
        });
        try
        {
            await server.StartAsync();

            using (var socket = await ConnectAsync(path))
            {
                var decoder = new ProtocolCodec.ServerMessageDecoder();
                await socket.SendAsync(ProtocolCodec.EncodeClientMessage(new ClientMessage.Hello(1)));
                var error = Assert.IsType<ServerMessage.HelloError>(await ReadMessageAsync(socket, decoder));
                Assert.Equal("version", error.Error.Code);
            }

            using (var socket = await ConnectAsync(path))
            {
                var decoder = new ProtocolCodec.ServerMessageDecoder();
                await socket.SendAsync(ProtocolCodec.EncodeClientMessage(new ClientMessage.Cancel(
                    "c1", new RpcTarget.ServerTarget(TestServerId))));
                var error = Assert.IsType<ServerMessage.HelloError>(await ReadMessageAsync(socket, decoder));
                Assert.Equal("invalid_request", error.Error.Code);
                Assert.Contains("must be hello", error.Error.Message);
            }
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ListenerLifecycleGuardsAndIdempotentClose()
    {
        var path = TempSocketPath();
        var listener = new UnixListener(new UnixListenerOptions { Path = path });
        ByteConnectionAcceptor accept = _ => new NullHandler();
        try
        {
            await listener.StartAsync(accept);
            await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync(accept));
            await listener.CloseAsync();
            await listener.CloseAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ListenerRefusesToStartWhenAnotherListenerIsLive()
    {
        var path = TempSocketPath();
        var first = new UnixListener(new UnixListenerOptions { Path = path });
        var second = new UnixListener(new UnixListenerOptions { Path = path });
        ByteConnectionAcceptor accept = _ => new NullHandler();
        try
        {
            await first.StartAsync(accept);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(accept));
        }
        finally
        {
            await first.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ListenerOptionValidation()
    {
        Assert.Throws<ArgumentException>(() => new UnixListener(new UnixListenerOptions { Path = "" }));
        Assert.Throws<ArgumentException>(() => new UnixListener(new UnixListenerOptions
        {
            Path = TempSocketPath(),
            Mode = 0b1000_000_000, // 0o1000：超出 0o777
        }));
        Assert.Throws<ArgumentException>(() => new UnixListener(new UnixListenerOptions
        {
            Path = TempSocketPath(),
            GracefulCloseTimeoutMs = 0,
        }));
    }

    private sealed class NullHandler : IByteConnectionHandler
    {
        public void OnData(byte[] chunk)
        {
        }

        public void OnClose()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
