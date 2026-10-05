using System.Net.Sockets;
using Pi.Protocol;

namespace Pi.Client;

/// <summary>Unix 传输选项。对应 TS <c>UnixTransportOptions</c>（unix.ts）。</summary>
public sealed record UnixTransportOptions
{
    public required string Path { get; init; }

    public int? MaxPendingBytes { get; init; }
}

/// <summary>发现到的本地服务器路由。对应 TS <c>UnixServerRoute</c>。</summary>
public sealed record UnixServerRoute(string ServerId, string Path);

/// <summary>服务器发现选项。对应 TS <c>DiscoverUnixServersOptions</c>。</summary>
public sealed record DiscoverUnixServersOptions
{
    /// <summary>存放「按服务器寻址」的 Unix socket 的目录。</summary>
    public required string Directory { get; init; }

    /// <summary>每次连接与握手的最大耗时；缺省 1000 ms。</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>
/// Unix 域套接字传输与本地服务器发现。对应 TS <c>unix.ts</c>。
/// <para>TS 在 win32 上直接抛「不支持」；C# 用 <c>UnixDomainSocketEndPoint</c>，
/// Windows 10 1803+ 亦可用，故不设平台限制（与 Pi.Server 的 UnixListener 一致）。</para>
/// </summary>
public static class UnixTransport
{
    private const int DefaultDiscoveryTimeoutMs = 1_000;
    private const int MaxTimerDelayMs = 2_147_483_647;
    private const string SocketSuffix = ".sock";
    private const int MaxConcurrentDiscoveryProbes = 16;

    /// <summary>通过探测「按服务器寻址」的 Unix socket 发现可达的本地服务器。</summary>
    public static async Task<IReadOnlyList<UnixServerRoute>> DiscoverUnixServersAsync(
        DiscoverUnixServersOptions options)
    {
        var timeoutMs = options.TimeoutMs ?? DefaultDiscoveryTimeoutMs;
        if (timeoutMs <= 0 || timeoutMs > MaxTimerDelayMs)
        {
            throw new ArgumentException(
                $"Unix discovery timeoutMs must be an integer between 1 and {MaxTimerDelayMs}");
        }

        string[] names;
        try
        {
            names = Directory.GetFiles(options.Directory);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        var candidates = new List<UnixServerRoute>();
        foreach (var fullPath in names)
        {
            var name = Path.GetFileName(fullPath);
            if (!name.EndsWith(SocketSuffix, StringComparison.Ordinal)) continue;
            var serverId = name[..^SocketSuffix.Length];
            if (ServerIds.IsServerId(serverId)) candidates.Add(new UnixServerRoute(serverId, fullPath));
        }

        var routes = new List<UnixServerRoute>();
        var nextIndex = 0;
        Exception? failure = null;
        var gate = new object();
        var workerCount = Math.Min(MaxConcurrentDiscoveryProbes, candidates.Count);
        var workers = new List<Task>(workerCount);
        for (var worker = 0; worker < workerCount; worker++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (true)
                {
                    lock (gate)
                    {
                        if (failure is not null || nextIndex >= candidates.Count) return;
                    }
                    UnixServerRoute candidate;
                    lock (gate)
                    {
                        if (nextIndex >= candidates.Count) return;
                        candidate = candidates[nextIndex++];
                    }
                    try
                    {
                        if (!File.Exists(candidate.Path)) continue;
                        var route = await ProbeUnixServerAsync(candidate, timeoutMs).ConfigureAwait(false);
                        if (route is not null)
                        {
                            lock (gate) routes.Add(route);
                        }
                    }
                    catch (Exception error)
                    {
                        lock (gate) failure ??= error;
                    }
                }
            }));
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        if (failure is not null) throw failure;
        return routes.OrderBy(route => route.ServerId, StringComparer.Ordinal).ToList();
    }

    /// <summary>创建 Unix 域套接字传输工厂。对应 TS <c>createUnixTransportFactory</c>。</summary>
    public static ByteTransportFactory CreateUnixTransportFactory(UnixTransportOptions options)
    {
        var maxPendingBytes = ValidateUnixTransportOptions(options);
        return handlers => ConnectUnixSocketAsync(options.Path, maxPendingBytes, handlers);
    }

    private static int ValidateUnixTransportOptions(UnixTransportOptions options)
    {
        if (string.IsNullOrEmpty(options.Path))
        {
            throw new ArgumentException("Unix transport path must not be empty");
        }
        var maxPendingBytes = options.MaxPendingBytes ?? (int)Frame.DefaultMaxFrameLength * 4;
        if (maxPendingBytes <= 0)
        {
            throw new ArgumentException("Unix transport maxPendingBytes must be a positive safe integer");
        }
        return maxPendingBytes;
    }

    private static async Task<IByteTransport> ConnectUnixSocketAsync(string path, int maxPendingBytes,
        IByteTransportHandlers handlers)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var transport = new UnixByteTransport(socket, maxPendingBytes, handlers);
        transport.StartPump();
        return transport;
    }

    private static async Task<UnixServerRoute?> ProbeUnixServerAsync(UnixServerRoute route, int timeoutMs)
    {
        var maxPendingBytes = ValidateUnixTransportOptions(new UnixTransportOptions { Path = route.Path });
        Socket? probeSocket = null;
        using var timeout = new CancellationTokenSource(timeoutMs);
        var client = new Client(new ClientOptions
        {
            ServerId = route.ServerId,
            TransportFactory = async handlers =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probeSocket = socket;
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(route.Path), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                var transport = new UnixByteTransport(socket, maxPendingBytes, handlers);
                transport.StartPump();
                return transport;
            },
        });

        try
        {
            await client.ConnectAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            return route;
        }
        catch (Exception error) when (error is OperationCanceledException or ProtocolValidationError
            or DisconnectedError or ServerError or SocketException or IOException)
        {
            // 缺失/被拒的 socket 是陈旧或正在关闭；协议失败说明端点不是它宣称的服务器。
            return null;
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
            probeSocket?.Dispose();
        }
    }

    /// <summary>Unix 域套接字字节传输。对应 TS <c>UnixByteTransport</c>。</summary>
    private sealed class UnixByteTransport : IByteTransport
    {
        private readonly Socket _socket;
        private readonly int _maxPendingBytes;
        private readonly IByteTransportHandlers _handlers;
        private readonly object _gate = new();
        private bool _closed;
        private long _pendingBytes;
        private Task _writeTail = Task.CompletedTask;

        public UnixByteTransport(Socket socket, int maxPendingBytes, IByteTransportHandlers handlers)
        {
            _socket = socket;
            _maxPendingBytes = maxPendingBytes;
            _handlers = handlers;
        }

        public void StartPump() => _ = Task.Run(PumpAsync);

        private async Task PumpAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var read = await _socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                    if (read == 0) break;
                    _handlers.OnData(buffer[..read]);
                }
                Close();
            }
            catch (SocketException error)
            {
                if (CloseInternal()) _handlers.OnError(error);
            }
            catch (ObjectDisposedException)
            {
                // 本地关闭。
            }
        }

        public Task SendAsync(byte[] chunk)
        {
            lock (_gate)
            {
                if (_closed) return Task.FromException(new InvalidOperationException("Unix transport is closed"));
                if (_pendingBytes + chunk.Length > _maxPendingBytes)
                {
                    return Task.FromException(
                        new InvalidOperationException("Unix transport exceeded its pending byte limit"));
                }
                _pendingBytes += chunk.Length;
                var previous = _writeTail;
                var write = WriteAfterAsync(previous, chunk);
                _writeTail = write.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return write;
            }
        }

        private async Task WriteAfterAsync(Task previous, byte[] chunk)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // 前一笔失败不阻断本笔（对齐 TS 的 writeTail.catch(() => {})）。
            }
            try
            {
                lock (_gate)
                {
                    if (_closed) throw new InvalidOperationException("Unix transport is closed");
                }
                await _socket.SendAsync(chunk, SocketFlags.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _pendingBytes -= chunk.Length;
            }
        }

        public void Close()
        {
            if (!CloseInternal()) return;
            _handlers.OnClose();
        }

        private bool CloseInternal()
        {
            lock (_gate)
            {
                if (_closed) return false;
                _closed = true;
            }
            try
            {
                _socket.Dispose();
            }
            catch
            {
                // 已释放。
            }
            return true;
        }
    }
}
