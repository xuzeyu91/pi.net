using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Client;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;
using Context = Pi.Chord.Context.Context;
using ClientConnectionState = Pi.Client.ConnectionState;

namespace Pi.Server.Tests;

/// <summary>server.ts + client.ts 端到端：真实 Unix 域套接字上的握手、请求与断开。</summary>
public class ServerClientEndToEndTests
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f01";

    private sealed record Metadata(string Id) : ISessionMetadata;

    private sealed class EchoServerServiceHost : IRoutedServerServiceHost
    {
        public Task<IRoutedServerServiceAttachment> AttachClientAsync(IRoutedServerPresentation presentation,
            Context context)
            => Task.FromResult<IRoutedServerServiceAttachment>(new EchoAttachment());
    }

    private sealed class EchoAttachment : IRoutedServerServiceAttachment
    {
        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
            => Task.FromResult<object?>($"{call.ServiceId}.{call.Member}:{call.Args.Count}");

        public Task ReleaseAsync(Context context) => Task.CompletedTask;
    }

    private sealed class EchoHost : IServerHost<Metadata>
    {
        public IRoutedServerServiceHost ServerServices { get; } = new EchoServerServiceHost();

        public Task<Metadata> ResolveSessionAsync(string sessionId, Context context)
            => Task.FromResult(new Metadata(sessionId));

        public Task<IRoutedSessionHandle> OpenSessionAsync(Metadata metadata, Context context)
            => throw new NotSupportedException();
    }

    private static string TempSocketPath()
        => Path.Combine(Path.GetTempPath(), $"pi-{Guid.NewGuid():N}"[..11] + ".sock");

    private static ClientOptions ClientOptionsFor(string path, string serverId = ServerId) => new()
    {
        ServerId = serverId,
        TransportFactory = UnixTransport.CreateUnixTransportFactory(new UnixTransportOptions { Path = path }),
    };

    [Fact]
    public async Task HandshakesAndRoutesRequestsOverUnixSocket()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();

            var states = new List<ClientConnectionState>();
            await using var client = new Pi.Client.Client(ClientOptionsFor(path));
            client.OnConnectionStateChange(change => states.Add(change.State));

            var hello = await client.ConnectAsync();
            Assert.Equal(ServerId, hello.ServerId);
            Assert.Equal(ServerId, client.Hello!.ServerId);
            Assert.True(client.Connected);
            Assert.Equal([ClientConnectionState.Connecting, ClientConnectionState.Connected], states);

            // 服务端域调用经真实 socket 往返。
            var result = await client.RequestAsync(
                new RpcTarget.ServerTarget(ServerId), new ServiceCall("echoer", "echo", ["a", "b"]));
            Assert.Equal("echoer.echo:2", result);

            client.Disconnect();
            Assert.Equal(ClientConnectionState.Disconnected, client.ConnectionState);
            Assert.False(client.Connected);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsServerIdMismatchAndDisposedClient()
    {
        var path = TempSocketPath();
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();

            await using (var wrong = new Pi.Client.Client(ClientOptionsFor(
                path, "11111111-1111-4111-8111-111111111111")))
            {
                var error = await Assert.ThrowsAsync<ProtocolValidationError>(() => wrong.ConnectAsync());
                Assert.Contains("does not match", error.Message);
            }

            var client = new Pi.Client.Client(ClientOptionsFor(path));
            await client.ConnectAsync();
            await client.DisposeAsync();
            Assert.True(client.Disposed);
            await Assert.ThrowsAsync<ClientDisposedError>(() => client.ConnectAsync());
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task DiscoversRunningServerAndSkipsStaleSockets()
    {
        // 发现按「<serverId>.sock」约定寻址。
        var path = UnixSocketAddress.GetUnixSocketPath(ServerId, Path.GetTempPath());
        var stalePath = UnixSocketAddress.GetUnixSocketPath(
            Guid.NewGuid().ToString(), Path.GetTempPath());
        var server = new Server<Metadata>(new EchoHost(), new ServerOptions
        {
            Listeners = [new UnixListener(new UnixListenerOptions { Path = path })],
            ServerId = ServerId,
        });
        try
        {
            await server.StartAsync();
            // 陈旧 socket：写一个普通文件，探测应跳过而非报错。
            File.WriteAllText(stalePath, "stale");

            var directory = Path.GetTempPath();
            var routes = await UnixTransport.DiscoverUnixServersAsync(new DiscoverUnixServersOptions
            {
                Directory = directory,
                TimeoutMs = 2_000,
            });
            Assert.Contains(routes, route => route.ServerId == ServerId && route.Path == path);
        }
        finally
        {
            await server.CloseAsync();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(stalePath)) File.Delete(stalePath);
        }
    }
}
