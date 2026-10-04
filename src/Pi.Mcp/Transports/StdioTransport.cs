using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>
/// stdio 传输：启动子进程，以"每行一条 JSON-RPC 消息"的方式经 stdin/stdout 通信，
/// stderr 转发为错误事件。对应 TS <c>transports/stdio.ts</c>；cross-spawn 语义由
/// System.Diagnostics.Process 承接。
/// </summary>
public sealed class StdioTransport : TransportEvents, IMcpTransport
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly IReadOnlyDictionary<string, string>? _env;
    private readonly string? _workingDirectory;
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private string? _protocolVersion;

    /// <param name="command">可执行文件（或 PATH 上的命令名）。</param>
    /// <param name="args">命令行参数。</param>
    /// <param name="env">附加环境变量（null 表示继承当前进程环境）。</param>
    /// <param name="workingDirectory">子进程工作目录（缺省继承）。</param>
    public StdioTransport(string command, IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null, string? workingDirectory = null)
    {
        _command = command;
        _args = args ?? [];
        _env = env;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _command,
            WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in _args) startInfo.ArgumentList.Add(arg);
        if (_env is not null)
        {
            foreach (var (key, value) in _env) startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new McpError(JsonRpcErrorCodes.InternalError, $"Failed to spawn MCP server process: {_command}");
        _process = process;
        _readCts = new CancellationTokenSource();

        // stdout：逐行读取并解析 JSON-RPC 消息。
        _ = PumpStdoutAsync(process, _readCts.Token);
        // stderr：转发为错误事件（诊断信息，不影响消息流）。
        _ = PumpStderrAsync(process, _readCts.Token);
        // 进程退出：等价于连接关闭。
        process.Exited += (_, _) => EmitClose();
        process.EnableRaisingEvents = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var process = _process
            ?? throw new McpConnectionClosedError("MCP server is not started");
        if (process.HasExited) throw new McpConnectionClosedError("MCP server process has exited");

        var line = JsonRpc.Serialize(message).ToJsonString() + "\n";
        await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _readCts?.Cancel();
        var process = _process;
        if (process is not null)
        {
            try
            {
                // 优雅关闭：先关 stdin 触发服务器退出，短宽限期后强制终止。
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已退出：忽略。
            }
        }
        _process = null;
        EmitClose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SetProtocolVersion(string version) => _protocolVersion = version;

    private async Task PumpStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break; // EOF：子进程关闭 stdout。
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue; // 忽略空行。
                try
                {
                    EmitMessage(JsonRpc.ParseText(trimmed));
                }
                catch (McpError error)
                {
                    // 单条消息解析失败不影响连接（对齐 TS：仅报告）。
                    EmitError(error);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            EmitError(error);
        }
        EmitClose();
    }

    private async Task PumpStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) return;
                if (line.Trim().Length > 0)
                    EmitError(new InvalidOperationException($"MCP server stderr: {line}"));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            EmitError(error);
        }
    }
}
