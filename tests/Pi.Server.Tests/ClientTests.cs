using Pi.Client;
using Pi.Protocol;
using Xunit;
using ClientConnectionState = Pi.Client.ConnectionState;

namespace Pi.Server.Tests;

/// <summary>client.ts / connection.ts 测试（假传输驱动的握手与错误路径）。</summary>
public class ClientTests
{
    private const string ServerId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    /// <summary>测试用假传输：记录发出的帧，并允许测试推送服务端消息。</summary>
    private sealed class FakeTransport : IByteTransport
    {
        private readonly IByteTransportHandlers _handlers;

        public FakeTransport(IByteTransportHandlers handlers) => _handlers = handlers;

        public List<ClientMessage> Sent { get; } = [];

        public bool Closed { get; private set; }

        public Exception? SendFailure { get; set; }

        public Task SendAsync(byte[] chunk)
        {
            if (SendFailure is not null) return Task.FromException(SendFailure);
            Sent.AddRange(new ProtocolCodec.ClientMessageDecoder().Push(chunk));
            return Task.CompletedTask;
        }

        public void Close() => Closed = true;

        public void Push(ServerMessage message)
            => _handlers.OnData(ProtocolCodec.EncodeServerMessage(message));

        public void PushRaw(byte[] chunk) => _handlers.OnData(chunk);

        public void RaiseError(Exception error) => _handlers.OnError(error);

        public void RaiseClose() => _handlers.OnClose();
    }

    /// <summary>持有客户端与其假传输（传输在连接时惰性创建）。</summary>
    private sealed class Harness
    {
        public FakeTransport? Transport { get; private set; }

        public Pi.Client.Client Client { get; }

        public Harness(string serverId = ServerId)
        {
            Client = new Pi.Client.Client(new ClientOptions
            {
                ServerId = serverId,
                TransportFactory = handlers =>
                {
                    Transport = new FakeTransport(handlers);
                    return Task.FromResult<IByteTransport>(Transport);
                },
            });
        }
    }

    private static Harness Create(string serverId = ServerId) => new(serverId);

    private static byte[] Encode(ClientMessage message) => ProtocolCodec.EncodeClientMessage(message);

    [Fact]
    public async Task HandshakeSucceedsAndSendsHelloFirst()
    {
        var harness = Create();
        var client = harness.Client;
        var states = new List<ClientConnectionState>();
        client.OnConnectionStateChange(change => states.Add(change.State));

        var handshake = client.ConnectAsync();
        var hello = Assert.IsType<ClientMessage.Hello>(Assert.Single(harness.Transport!.Sent));
        Assert.Equal(ProtocolVersion.Current, hello.Version);

        harness.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        var serverHello = await handshake;
        Assert.Equal(ServerId, serverHello.ServerId);
        Assert.True(client.Connected);
        Assert.Equal([ClientConnectionState.Connecting, ClientConnectionState.Connected], states);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task HelloErrorBecomesServerError()
    {
        var harness = Create();
        var client = harness.Client;
        var handshake = client.ConnectAsync();
        harness.Transport!.Push(new ServerMessage.HelloError(new ProtocolError("version", "Unsupported protocol version 1")));

        var error = await Assert.ThrowsAsync<Pi.Client.ServerError>(() => handshake);
        Assert.Equal("version", error.Code);
        Assert.False(client.Connected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RejectsDataBeforeHelloAndUnexpectedHandshakeMessages()
    {
        var harness = Create();
        var client = harness.Client;
        var handshake = client.ConnectAsync();
        // 客户端 hello 已发出，但先收到普通消息（非 hello）→ 协议校验失败。
        harness.Transport!.Push(new ServerMessage.ResponseOk("r1", null));
        var error = await Assert.ThrowsAsync<ProtocolValidationError>(() => handshake);
        Assert.Contains("Expected server hello", error.Message);

        // 就绪后再收到 hello 视为非法握手消息。
        var harness2 = Create();
        var client2 = harness2.Client;
        var handshake2 = client2.ConnectAsync();
        harness2.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        await handshake2;
        harness2.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        await Task.Delay(20);
        Assert.False(client2.Connected);
        await client.DisposeAsync();
        await client2.DisposeAsync();
    }

    [Fact]
    public async Task TransportCloseAndErrorsDisconnectClient()
    {
        var harness = Create();
        var client = harness.Client;
        var handshake = client.ConnectAsync();
        harness.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        await handshake;

        harness.Transport!.RaiseClose();
        await Task.Delay(20);
        Assert.False(client.Connected);
        Assert.Equal(ClientConnectionState.Disconnected, client.ConnectionState);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RejectsMismatchedServerId()
    {
        var harness = Create("11111111-1111-4111-8111-111111111111");
        var client = harness.Client;
        var handshake = client.ConnectAsync();
        harness.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        var error = await Assert.ThrowsAsync<ProtocolValidationError>(() => handshake);
        Assert.Contains("does not match", error.Message);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task DisposeRejectsPendingRequestsAndBlocksReconnect()
    {
        var harness = Create();
        var client = harness.Client;
        var handshake = client.ConnectAsync();
        harness.Transport!.Push(new ServerMessage.Hello(ProtocolVersion.Current, ServerId));
        await handshake;

        var pending = client.RequestAsync(new RpcTarget.ServerTarget(ServerId),
            new Pi.Chord.Services.ServiceCall("echoer", "echo", []));
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ClientDisposedError>(() => pending);
        await Assert.ThrowsAsync<ClientDisposedError>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ValidatesOptionsAndRejectsRequestsWhileDisconnected()
    {
        Assert.Throws<ArgumentException>(() => new Pi.Client.Client(new ClientOptions
        {
            ServerId = "not-a-uuid",
            TransportFactory = _ => throw new InvalidOperationException(),
        }));

        var harness = Create();
        var client = harness.Client;
        await Assert.ThrowsAsync<DisconnectedError>(() => client.RequestAsync(
            new RpcTarget.ServerTarget(ServerId), new Pi.Chord.Services.ServiceCall("echoer", "echo", [])));
    }
}
