using System.Text.Json.Nodes;
using Pi.Client;
using Pi.Server;
using Pi.Protocol;
using Xunit;

namespace Pi.Server.Tests;

/// <summary>
/// server + client 端到端测试：真实 TCP 连接上跑完 hello 握手 →
/// 请求/响应 → service_update 推送 → 取消 → 关闭。
/// </summary>
public class EndToEndTests
{
    private static (RpcServer Server, int Port) StartServer(Action<RpcServer> configure)
    {
        var server = new RpcServer();
        configure(server);
        var port = FreePort();
        _ = server.ListenAsync(port, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return (server, port);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task HandshakeCallAndServiceEventRoundTrip()
    {
        var (server, port) = StartServer(s =>
        {
            s.RegisterHandler("echo", call =>
                Task.FromResult<object?>(new JsonObject { ["echo"] = call?["text"]?.ToString() }));
            s.RegisterHandler("fail", _ => throw new InvalidOperationException("业务失败"));
        });

        await using var client = new RpcClient("127.0.0.1", port);

        // hello 握手。
        await client.ConnectAsync();
        Assert.Equal(ClientState.Connected, client.State);
        Assert.NotEmpty(client.ServerId!);

        // 请求 / 响应（结果为 CBOR 值模型：Dictionary）。
        var echo = await client.CallAsync("echo", new JsonObject { ["text"] = "你好" });
        var echoMap = Assert.IsType<Dictionary<string, object?>>(echo);
        Assert.Equal("你好", echoMap["echo"]?.ToString());

        // 服务器异常 → ResponseError → 客户端异常。
        var error = await Assert.ThrowsAsync<ClientServerError>(
            () => client.CallAsync("fail", null));
        Assert.Contains("业务失败", error.Message);

        // 未知方法 → handler_error。
        await Assert.ThrowsAsync<ClientServerError>(() => client.CallAsync("missing", null));

        // service_update 推送。
        var received = new List<ServerMessage.ServiceEvent>();
        using var subscription = client.OnServiceEvent(received.Add);
        await server.SendServiceEventAsync("sub-1", new JsonObject { ["n"] = 1 });
        await Task.Delay(100);
        Assert.Single(received);
        Assert.Equal("sub-1", received[0].SubscriptionId);

        await client.CloseAsync();
        Assert.Equal(ClientState.Disconnected, client.State);
    }

    [Fact]
    public async Task HandshakeRejectsWrongVersion()
    {
        // 用协议层直接发错误版本的 hello：服务器应回 hello_error 并断开。
        var (server, port) = StartServer(_ => { });
        Assert.NotNull(server);

        var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        var stream = tcp.GetStream();
        var badHello = new ClientMessage.Hello(3);
        await stream.WriteAsync(Pi.Protocol.ProtocolCodec.EncodeClientMessage(badHello));

        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        Assert.True(read > 0);
        // 帧解码 + 消息解码后应为 hello_error。
        // ProtocolCodec 解码器内含帧解码：直接喂原始字节。
        var message = Assert.Single(new Pi.Protocol.ProtocolCodec.ServerMessageDecoder().Push(buffer[..read]));
        var helloError = Assert.IsType<ServerMessage.HelloError>(message);
        Assert.Contains("Unsupported protocol version", helloError.Error.Message);
        tcp.Dispose();
    }
}
