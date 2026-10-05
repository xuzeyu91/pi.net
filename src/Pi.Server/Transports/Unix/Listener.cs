using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pi.Protocol;

namespace Pi.Server.Transports.Unix;

/// <summary>
/// Unix 域套接字监听器。对应 TS <c>transports/unix/listener.ts</c>。
/// <para>三处刻意的 C# 简化（BCL 无对应 API，记入 porting-status）：
/// ①TS 先绑私有路径再硬链接发布到公开路径，C# 直接绑定公开路径；
/// ②TS 用 <c>lstat</c> 的 dev/ino 做文件身份校验，C# 无 inode API；
/// ③TS 拒绝删除非 socket 路径，C# 只能按「存在 + 连通性探测」判定。</para>
/// </summary>
public sealed class UnixListener : IServerListener
{
    private const int DefaultSocketMode = 0b110_000_000; // 0o600
    private const int DefaultGracefulCloseTimeoutMs = 5_000;
    private const long MaxUInt32 = 0xffff_ffffL;
    private const int MaxTimerDelayMs = 2_147_483_647;
    private const int SocketProbeTimeoutMs = 1_000;
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly ResolvedUnixListenerOptions _options;
    private readonly object _gate = new();
    private readonly HashSet<UnixByteConnection> _connections = [];
    private Socket? _server;
    private bool _closing;
    private Task? _closePromise;
    private ByteConnectionAcceptor? _accept;

    public UnixListener(UnixListenerOptions options) => _options = ResolveUnixListenerOptions(options);

    public async Task StartAsync(ByteConnectionAcceptor accept, CancellationToken cancellationToken = default)
    {
        if (_server is not null) throw new InvalidOperationException("Unix listener is already started");
        if (_closing) throw new InvalidOperationException("Unix listener is closing or closed");
        _accept = accept;

        var directory = Path.GetDirectoryName(_options.Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        RemoveStaleSocket(_options.Path);

        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            server.Bind(new UnixDomainSocketEndPoint(_options.Path));
            server.Listen(backlog: 128);
        }
        catch
        {
            server.Dispose();
            throw;
        }
        SetSocketMode(_options.Path, _options.Mode);
        _server = server;
        _ = Task.Run(() => AcceptLoopAsync(server), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closePromise is not null) return _closePromise;
            _closing = true;
            _closePromise = CloseInternalAsync();
            return _closePromise;
        }
    }

    private async Task AcceptLoopAsync(Socket server)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await server.AcceptAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (_closing) return;
                continue;
            }
            AcceptSocket(socket);
        }
    }

    private void AcceptSocket(Socket socket)
    {
        if (_closing)
        {
            socket.Dispose();
            return;
        }
        var connection = new UnixByteConnection(socket, _options.GracefulCloseTimeoutMs, _options.MaxPendingBytes);
        lock (_gate) _connections.Add(connection);
        var accept = _accept;
        if (accept is null)
        {
            socket.Dispose();
            return;
        }
        var handler = accept(connection);
        _ = Task.Run(() => PumpAsync(socket, connection, handler), CancellationToken.None);
    }

    private async Task PumpAsync(Socket socket, UnixByteConnection connection, IByteConnectionHandler handler)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                if (read == 0) break;
                handler.OnData(buffer[..read]);
            }
        }
        catch (SocketException error)
        {
            handler.OnError(error);
            socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 监听器关闭导致的释放。
        }
        finally
        {
            connection.MarkClosed();
            lock (_gate) _connections.Remove(connection);
            handler.OnClose();
        }
    }

    private async Task CloseInternalAsync()
    {
        var server = _server;
        if (server is not null)
        {
            try
            {
                server.Dispose();
            }
            catch (Exception error)
            {
                ReportError(error);
            }
        }
        List<UnixByteConnection> connections;
        lock (_gate) connections = _connections.ToList();
        await Task.WhenAll(connections.Select(connection => connection.CloseAsync())).ConfigureAwait(false);
        RemovePath(_options.Path);
        lock (_gate)
        {
            _connections.Clear();
            _server = null;
        }
    }

    private void RemoveStaleSocket(string path)
    {
        if (!File.Exists(path)) return;
        if (IsSocketLive(path)) throw new InvalidOperationException($"Unix listener is already running: {path}");
        File.Delete(path);
    }

    private static bool IsSocketLive(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var connect = probe.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return connect.Wait(SocketProbeTimeoutMs);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static void RemovePath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // 已不存在。
        }
    }

    private static void SetSocketMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, (UnixFileMode)mode);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            _options.OnError?.Invoke(error);
        }
        catch
        {
            // 错误观察者不得影响监听器状态。
        }
    }

    private sealed record ResolvedUnixListenerOptions(
        string Path, int Mode, int MaxPendingBytes, int GracefulCloseTimeoutMs, Action<Exception>? OnError);

    private static ResolvedUnixListenerOptions ResolveUnixListenerOptions(UnixListenerOptions options)
    {
        if (string.IsNullOrEmpty(options.Path))
        {
            throw new ArgumentException("Server Unix socket path must not be empty");
        }
        var mode = options.Mode ?? DefaultSocketMode;
        if (mode < 0 || mode > 0b111_111_111)
        {
            throw new ArgumentException("Server Unix socket mode must be an integer between 0 and 0o777");
        }
        var maxFrameLength = options.MaxFrameLength ?? (int)Frame.DefaultMaxFrameLength;
        if (maxFrameLength <= 0 || (long)maxFrameLength > MaxUInt32)
        {
            throw new ArgumentException($"Server maxFrameLength must be an integer between 1 and {MaxUInt32}");
        }
        var maxPendingBytes = options.MaxPendingBytes ?? maxFrameLength * 4;
        if (maxPendingBytes < maxFrameLength + 4)
        {
            throw new ArgumentException("Server maxPendingBytes must be at least maxFrameLength + 4");
        }
        var gracefulCloseTimeoutMs = options.GracefulCloseTimeoutMs ?? DefaultGracefulCloseTimeoutMs;
        if (gracefulCloseTimeoutMs <= 0 || gracefulCloseTimeoutMs > MaxTimerDelayMs)
        {
            throw new ArgumentException(
                $"Server gracefulCloseTimeoutMs must be an integer between 1 and {MaxTimerDelayMs}");
        }
        return new ResolvedUnixListenerOptions(options.Path, mode, maxPendingBytes, gracefulCloseTimeoutMs,
            options.OnError);
    }

    /// <summary>私有绑定路径（<c>bind-&lt;sha256(path) 前 8 位&gt;</c>）。对应 TS <c>getOwnedBindPath</c>。</summary>
    internal static string GetOwnedBindPath(string path)
    {
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];
        return Path.Combine(Path.GetDirectoryName(path)!, $"bind-{suffix}");
    }
}

/// <summary>
/// Unix 字节连接：写串行化、排队字节上限、优雅关闭超时。对应 TS <c>UnixByteConnection</c>。
/// </summary>
public sealed class UnixByteConnection : IByteConnection
{
    private readonly Socket _socket;
    private readonly int _gracefulCloseTimeoutMs;
    private readonly int _maxPendingBytes;
    private readonly object _gate = new();
    private long _pendingBytes;
    private bool _closed;
    private bool _closing;
    private Task _writeTail = Task.CompletedTask;
    private Task? _closePromise;
    private TaskCompletionSource? _closeSignal;

    public UnixByteConnection(Socket socket, int gracefulCloseTimeoutMs, int maxPendingBytes)
    {
        _socket = socket;
        _gracefulCloseTimeoutMs = gracefulCloseTimeoutMs;
        _maxPendingBytes = maxPendingBytes;
    }

    public bool Closed
    {
        get
        {
            lock (_gate) return _closed;
        }
    }

    public Task SendAsync(byte[] chunk, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_closed || _closing)
            {
                return Task.FromException(new InvalidOperationException("Unix connection is closed"));
            }
            if (_pendingBytes + chunk.Length > _maxPendingBytes)
            {
                return Task.FromException(
                    new InvalidOperationException("Unix connection exceeded its pending byte limit"));
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
            // 前一笔写失败不阻断本笔（与 TS 的 writeTail.catch(() => {}) 一致）。
        }
        try
        {
            bool unusable;
            lock (_gate) unusable = _closed || _closing;
            if (unusable) throw new InvalidOperationException("Unix connection is closed");
            await _socket.SendAsync(chunk, SocketFlags.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _pendingBytes -= chunk.Length;
        }
    }

    public Task CloseAsync(byte[]? finalChunk = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                MarkClosed();
                return Task.CompletedTask;
            }
            if (_closePromise is not null) return _closePromise;
            _closing = true;
            _closeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closePromise = CloseCoreAsync(finalChunk, _closeSignal.Task);
            return _closePromise;
        }
    }

    private async Task CloseCoreAsync(byte[]? finalChunk, Task closedSignal)
    {
        try
        {
            await _writeTail.ConfigureAwait(false);
        }
        catch
        {
            // 写链失败不影响关闭流程。
        }
        try
        {
            if (_socket.Connected)
            {
                if (finalChunk is not null)
                {
                    await _socket.SendAsync(finalChunk, SocketFlags.None).ConfigureAwait(false);
                }
                _socket.Shutdown(SocketShutdown.Send);
            }
        }
        catch
        {
            try
            {
                _socket.Dispose();
            }
            catch
            {
                // 已释放。
            }
        }

        var completed = await Task.WhenAny(closedSignal, Task.Delay(_gracefulCloseTimeoutMs)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, closedSignal))
        {
            try
            {
                _socket.Dispose();
            }
            catch
            {
                // 已释放。
            }
            MarkClosed();
        }
    }

    /// <summary>标记连接已关闭（由读取循环或关闭流程调用）。对应 TS <c>markClosed</c>。</summary>
    public void MarkClosed()
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _closing = true;
            signal = _closeSignal;
        }
        signal?.TrySetResult();
    }
}
