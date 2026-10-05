using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pi.Codemode.Runtime;

/// <summary>
/// 沙箱化脚本执行。对应 TS <c>runtime/host.ts</c> 的 <c>CodemodeSandbox</c>：脚本以
/// <c>tools.&lt;name&gt;(args)</c> 调用每个注册工具，另有 <c>ALL_TOOLS</c>、输出助手
/// <c>text</c> / <c>image</c> / <c>exit</c>、<c>console.*</c>、<c>store</c> / <c>load</c>
/// 与配置的全局函数；此外一无所有（无定时器、fetch、process、require、模块）。<para/>
/// 每次 <see cref="ExecuteAsync"/> 是一次独立执行（独立 VM、互不共享状态）；沙箱只持有工具表与
/// 默认值，<see cref="CloseAsync"/> 中止全部在执行中的执行。<para/>
/// 与 TS 的差异：TS 每次执行新建 worker 线程 + QuickJS wasm VM；C# 把 VM 执行抽成注入点
/// <see cref="ICodemodeJsEngine"/>，本类只保留编排逻辑（工具表、截止时间、中止、调用中继、
/// 结果装配），故 TS 的 <c>workerUrl</c> 选项没有对应物。
/// </summary>
public sealed partial class CodemodeSandbox
{
    /// <summary>TS <c>DEFAULT_TIMEOUT_MS</c>（host.ts，未导出）。</summary>
    private const long DefaultTimeoutMs = 300_000;

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierPattern();

    private static readonly HashSet<string> ReservedGlobals = new(StringComparer.Ordinal)
    {
        "tools", "ALL_TOOLS", "console", "text", "image", "exit", "globalThis", "store", "load",
    };

    /// <summary>对应 TS 的 <c>JSON.stringify</c>：默认编码器转义非 ASCII，与 JS 不一致。</summary>
    private static readonly JsonSerializerOptions JsonStringifyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly Dictionary<string, CodemodeTool> toolsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodemodeTool> globalsByName = new(StringComparer.Ordinal);
    private readonly HashSet<Execution> running = [];
    private readonly long timeoutMs;
    private readonly long? memoryLimitBytes;
    private readonly object? wasm;
    private readonly ICodemodeJsEngine engine;
    private bool closed;

    public CodemodeSandbox(CodemodeSandboxOptions? options = null)
    {
        options ??= new CodemodeSandboxOptions();
        this.timeoutMs = options.TimeoutMs ?? DefaultTimeoutMs;
        this.memoryLimitBytes = options.MemoryLimitBytes;
        this.wasm = options.Wasm;
        this.engine = options.Engine ?? UnsupportedCodemodeJsEngine.Instance;
        foreach (var tool in options.Tools ?? []) this.RegisterTool(tool);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var global in options.Globals ?? [])
        {
            var parts = global.Name.Split('.');
            if (parts.Length > 2 || !parts.All(IsIdentifier) || ReservedGlobals.Contains(parts[0]))
            {
                throw new ArgumentException($"Invalid global name \"{global.Name}\"");
            }
            if (this.globalsByName.ContainsKey(global.Name))
            {
                throw new ArgumentException($"Global \"{global.Name}\" is already registered");
            }
            if (parts.Length == 2) namespaces.Add(parts[0]);
            this.globalsByName[global.Name] = global;
        }
        foreach (var name in namespaces)
        {
            if (this.globalsByName.ContainsKey(name))
            {
                throw new ArgumentException($"Global \"{name}\" conflicts with the namespace \"{name}\"");
            }
        }
    }

    private static bool IsIdentifier(string part) => IdentifierPattern().IsMatch(part);

    /// <summary>注册工具；重名抛 <see cref="ArgumentException"/>。对应 TS <c>registerTool</c>。</summary>
    public void RegisterTool(CodemodeTool tool)
    {
        lock (this.gate)
        {
            if (this.toolsByName.ContainsKey(tool.Name))
            {
                throw new ArgumentException($"Tool \"{tool.Name}\" is already registered");
            }
            this.toolsByName[tool.Name] = tool;
        }
    }

    /// <summary>注销工具；返回是否确有该工具。对应 TS <c>unregisterTool</c>。</summary>
    public bool UnregisterTool(string name)
    {
        lock (this.gate) return this.toolsByName.Remove(name);
    }

    /// <summary>已注册工具的快照。对应 TS <c>tools</c> getter。</summary>
    public IReadOnlyList<CodemodeTool> Tools
    {
        get { lock (this.gate) return [.. this.toolsByName.Values]; }
    }

    /// <summary>已注册全局函数的快照。对应 TS <c>globals</c> getter。</summary>
    public IReadOnlyList<CodemodeTool> Globals
    {
        get { lock (this.gate) return [.. this.globalsByName.Values]; }
    }

    /// <summary>
    /// 执行脚本：<c>code</c> 是 async 函数体，<c>return</c> 与顶层 <c>await</c> 可用。脚本失败不以
    /// 异常呈现，而是 <c>{ ok: false }</c> 结果（对应 TS「never rejects for script failures」）；
    /// 只有沙箱已关闭才抛 <see cref="InvalidOperationException"/>。脚本可用
    /// <c>store(key, value)</c> / <c>load(key)</c> 读写 <paramref name="options"/> 的 store。
    /// </summary>
    public async Task<CodemodeResult> ExecuteAsync(string code, CodemodeExecuteOptions? options = null)
    {
        options ??= new CodemodeExecuteOptions();
        lock (this.gate)
        {
            if (this.closed) throw new InvalidOperationException("Sandbox is closed");
        }

        // 工具表快照：对应 TS 的 new Map(this.toolsByName)（globals 构造后不再变化，同样快照）。
        Dictionary<string, CodemodeTool> tools;
        Dictionary<string, CodemodeTool> globals;
        lock (this.gate)
        {
            tools = new Dictionary<string, CodemodeTool>(this.toolsByName, StringComparer.Ordinal);
            globals = new Dictionary<string, CodemodeTool>(this.globalsByName, StringComparer.Ordinal);
        }

        var execution = new Execution(new Execution.ExecutionOptions(
            code,
            tools,
            globals,
            options.TimeoutMs ?? this.timeoutMs,
            options.Signal,
            this.memoryLimitBytes,
            SerializeStore(options.Store),
            this.wasm,
            this.engine));

        bool abortNow;
        lock (this.gate)
        {
            this.running.Add(execution);
            abortNow = this.closed;
        }
        // 构造期间沙箱被关闭：对应 TS「close() 只中止已注册执行」的竞态，由 Abort 收敛。
        if (abortNow) _ = execution.AbortAsync("Sandbox closed");

        try
        {
            return await execution.Promise.ConfigureAwait(false);
        }
        finally
        {
            lock (this.gate) this.running.Remove(execution);
        }
    }

    /// <summary>
    /// 关闭沙箱：中止全部在执行中的执行（以 <c>kind: "aborted"</c>、消息 "Sandbox closed" 结束），
    /// 之后的执行抛 <see cref="InvalidOperationException"/>。对应 TS <c>close</c>。
    /// </summary>
    public async Task CloseAsync()
    {
        Execution[] running;
        lock (this.gate)
        {
            this.closed = true;
            running = [.. this.running];
        }
        await Task.WhenAll(running.Select(execution => execution.AbortAsync("Sandbox closed")))
            .ConfigureAwait(false);
    }

    private static Dictionary<string, string> SerializeStore(IReadOnlyDictionary<string, JsonNode?>? store)
    {
        var serialized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store is null) return serialized;
        foreach (var (key, value) in store)
        {
            // 对应 TS：JSON.stringify(undefined) 返回 undefined，该条目被跳过。
            if (value is null) continue;
            serialized[key] = value.ToJsonString(JsonStringifyOptions);
        }
        return serialized;
    }

    /// <summary>一次脚本执行。对应 TS host.ts 的 <c>Execution</c> 类。</summary>
    private sealed class Execution
    {
        private readonly TaskCompletionSource<CodemodeResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object gate = new();
        private readonly Dictionary<string, CodemodeTool> tools;
        private readonly Dictionary<string, CodemodeTool> globals;
        private readonly long timeoutMs;
        private readonly long? memoryLimitBytes;
        private readonly Dictionary<string, string> store;
        private readonly object? wasm;
        private readonly ICodemodeJsEngine engine;
        private readonly CancellationToken signal;
        private readonly List<CodemodeOutputItem> output = [];
        private readonly List<CallState> calls = [];
        private readonly Dictionary<long, PendingCall> pending = new();
        private readonly Timer? timer;
        private readonly CancellationTokenRegistration? registration;
        private ICodemodeVm? vm;
        private bool finished;

        internal Execution(ExecutionOptions options)
        {
            this.tools = options.Tools;
            this.globals = options.Globals;
            this.timeoutMs = options.TimeoutMs;
            this.memoryLimitBytes = options.MemoryLimitBytes;
            this.store = options.Store;
            this.wasm = options.Wasm;
            this.engine = options.Engine;
            this.signal = options.Signal;

            // 对应 TS：Number.isFinite(timeoutMs) 才设定时器（C# 以 long.MaxValue 表示禁用）。
            if (options.TimeoutMs < long.MaxValue)
            {
                var dueMs = Math.Min(options.TimeoutMs, (long)TimeSpan.MaxValue.TotalMilliseconds);
                this.timer = new Timer(
                    static state => ((Execution)state!).OnTimeout(),
                    this,
                    TimeSpan.FromMilliseconds(dueMs),
                    Timeout.InfiniteTimeSpan);
            }

            if (this.signal.CanBeCanceled)
            {
                this.registration = this.signal.Register(
                    static state => ((Execution)state!).OnAbort(), this);
            }

            this.Start(options);
        }

        internal Task<CodemodeResult> Promise => this.completion.Task;

        /// <summary>对应 TS <c>abort(message)</c>。</summary>
        internal Task<CodemodeResult> AbortAsync(string message)
        {
            this.Finish(new CodemodeError(CodemodeErrorKind.Aborted, message));
            return this.Promise;
        }

        private void OnTimeout()
            => this.Finish(new CodemodeError(
                CodemodeErrorKind.Timeout, $"Execution timed out after {this.timeoutMs} ms"));

        private void OnAbort()
        {
            // 对应 TS：signal.reason 是 Error 时取其 message。C# 的 CancellationToken 不携带原因
            // 载荷（见设计差异），故消息固定为 "Execution aborted"。
            this.Finish(new CodemodeError(CodemodeErrorKind.Aborted, "Execution aborted"));
        }

        private void Start(ExecutionOptions options)
        {
            if (this.IsFinished) return;

            ICodemodeVm started;
            try
            {
                started = options.Engine.Run(this.BuildWorkerData(options), this.HandleMessageAsync);
            }
            catch (Exception error)
            {
                // 对应 TS：worker 启动失败（worker 文件缺失等）→ sandbox 失败。
                this.Finish(new CodemodeError(CodemodeErrorKind.Sandbox, error.Message));
                return;
            }

            bool alreadyFinished;
            lock (this.gate)
            {
                alreadyFinished = this.finished;
                if (!alreadyFinished) this.vm = started;
            }
            if (alreadyFinished)
            {
                // 引擎启动期间执行已结束（超时/中止/沙箱关闭）：补上终止，VM 不泄漏。
                _ = StopAndIgnoreAsync(started);
            }
        }

        private CodemodeWorkerData BuildWorkerData(ExecutionOptions options) => new()
        {
            Code = options.Code,
            Tools =
            [
                .. options.Tools.Values.Select(tool => new CodemodeWorkerTool(
                    tool.Name, CodemodeIdentifiers.ToCodemodeIdentifier(tool.Name), tool.Description ?? "")),
            ],
            Globals =
            [
                .. options.Globals.Values.Select(global => new CodemodeWorkerGlobal(global.Name, global.Spread)),
            ],
            Wasm = options.Wasm,
            MemoryLimitBytes = options.MemoryLimitBytes,
            Store = options.Store,
        };

        private bool IsFinished
        {
            get { lock (this.gate) return this.finished; }
        }

        private Task HandleMessageAsync(WorkerToHostMessage message)
        {
            try
            {
                switch (message)
                {
                    case WorkerToHostMessage.Output output:
                        lock (this.gate)
                        {
                            if (this.finished) return Task.CompletedTask;
                            this.output.Add(output.Item);
                        }
                        break;
                    case WorkerToHostMessage.Call call:
                        // 对应 TS 的 void this.handleCall(message)：工具调用并发进行，不阻塞后续消息。
                        _ = this.HandleCallAsync(call);
                        break;
                    case WorkerToHostMessage.Done done:
                        this.HandleDone(done);
                        break;
                    case WorkerToHostMessage.Crash crash:
                        this.Finish(new CodemodeError(CodemodeErrorKind.Sandbox, crash.Message));
                        break;
                }
            }
            catch (Exception error)
            {
                this.Finish(new CodemodeError(CodemodeErrorKind.Sandbox, error.Message));
            }
            return Task.CompletedTask;
        }

        private void HandleDone(WorkerToHostMessage.Done done)
        {
            if (!done.Ok)
            {
                // 对应 TS：JSON.parse(message.error) as Omit<CodemodeError, "kind"> 后展开。
                // P42 的协议把错误建模为结构化 CodemodeScriptError（引擎侧完成 JSON 解析）。
                this.Finish(new CodemodeError(CodemodeErrorKind.Script, done.Error?.Message ?? "")
                {
                    Name = done.Error?.Name,
                    Stack = done.Error?.Stack,
                });
                return;
            }
            var value = done.Value is null ? null : JsonNode.Parse(done.Value);
            this.Finish(null, value, done.Writes);
        }

        private static CodemodeStoreWrites ParseStoreWrites(string? writes)
        {
            var set = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            var delete = new List<string>();
            if (string.IsNullOrEmpty(writes)) return new CodemodeStoreWrites { Set = set, Delete = delete };
            foreach (var entry in (JsonNode.Parse(writes) as JsonArray) ?? [])
            {
                if (entry is not JsonArray pair || pair.Count == 0) continue;
                var key = pair[0]?.GetValue<string>();
                if (key is null) continue;
                // 对应 TS：解构得到 undefined（即 [key] 单元素）→ 删除；否则 set[key] = JSON.parse(value)。
                if (pair.Count < 2) delete.Add(key);
                else
                {
                    var raw = pair[1];
                    set[key] = raw is null ? null : JsonNode.Parse(raw.GetValue<string>());
                }
            }
            return new CodemodeStoreWrites { Set = set, Delete = delete };
        }

        private async Task HandleCallAsync(WorkerToHostMessage.Call call)
        {
            var isTool = call.Target == "tool";
            CallState? record = null;
            PendingCall pendingCall;
            lock (this.gate)
            {
                if (this.finished) return;
                if (isTool)
                {
                    record = new CallState(call.Name);
                    this.calls.Add(record);
                }
                pendingCall = new PendingCall(record);
                this.pending[call.Id] = pendingCall;
            }

            CodemodeCallStatus status;
            HostToWorkerMessage reply;
            try
            {
                var tool = (isTool ? this.tools : this.globals).GetValueOrDefault(call.Name);
                if (tool is null)
                {
                    throw new InvalidOperationException(
                        $"Unknown {(isTool ? "tool" : "global")} \"{call.Name}\"");
                }

                var args = call.Args is null ? null : JsonNode.Parse(call.Args);
                var value = await tool.Execute(
                    args, new CodemodeToolContext { Signal = pendingCall.Controller.Token }).ConfigureAwait(false);
                reply = new HostToWorkerMessage.Result(
                    call.Id, true, value is null ? null : value.ToJsonString(JsonStringifyOptions));
                status = CodemodeCallStatus.Ok;
            }
            catch (Exception error)
            {
                reply = new HostToWorkerMessage.Result(call.Id, false, error.Message);
                status = CodemodeCallStatus.Error;
            }

            ICodemodeVm? target;
            lock (this.gate)
            {
                // 已被 finish() 取消（脚本返回/超时/中止）：记录保持 "cancelled"，且不再向已停止的
                // VM 发送（对应 TS 中向已终止 worker 发送即丢弃）。
                if (!this.pending.Remove(call.Id, out _)) return;
                if (record is not null)
                {
                    record.Status = status;
                    record.DurationMs = ElapsedMs(pendingCall.StartedAt);
                }
                target = this.finished ? null : this.vm;
            }
            target?.Post(reply);
        }

        private void Finish(CodemodeError? error, JsonNode? value = null, string? writes = null)
        {
            CodemodeResult result;
            ICodemodeVm? target;
            lock (this.gate)
            {
                if (this.finished) return;
                this.finished = true;
                this.timer?.Dispose();
                this.registration?.Dispose();

                foreach (var pendingCall in this.pending.Values)
                {
                    if (pendingCall.Record is { } record)
                    {
                        record.DurationMs = ElapsedMs(pendingCall.StartedAt);
                    }
                    pendingCall.Controller.Cancel();
                }
                this.pending.Clear();

                var calls = new List<CodemodeCall>(this.calls.Count);
                foreach (var call in this.calls)
                {
                    calls.Add(new CodemodeCall(call.Name, call.Status, call.DurationMs));
                }

                result = error is null
                    ? new CodemodeResult.Success(value, [.. this.output], calls, ParseStoreWrites(writes))
                    : new CodemodeResult.Failure(error, [.. this.output], calls);
                target = this.vm;
            }

            if (target is null)
            {
                this.completion.TrySetResult(result);
                return;
            }
            // 对应 TS：Atomics.store(interrupt, 0, 1) + worker.terminate() 之后才 resolve。
            _ = StopThenResolveAsync(target, result);
        }

        private async Task StopThenResolveAsync(ICodemodeVm target, CodemodeResult result)
        {
            try
            {
                await target.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 对应 TS 的 terminate().catch(() => undefined)。
            }
            this.completion.TrySetResult(result);
        }

        private static async Task StopAndIgnoreAsync(ICodemodeVm target)
        {
            try
            {
                await target.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // 终止失败不影响已结束的执行。
            }
        }

        private static long ElapsedMs(long startedAt) => (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        internal sealed record ExecutionOptions(
            string Code,
            Dictionary<string, CodemodeTool> Tools,
            Dictionary<string, CodemodeTool> Globals,
            long TimeoutMs,
            CancellationToken Signal,
            long? MemoryLimitBytes,
            Dictionary<string, string> Store,
            object? Wasm,
            ICodemodeJsEngine Engine);

        private sealed class CallState(string name)
        {
            public string Name { get; } = name;

            public CodemodeCallStatus Status { get; set; } = CodemodeCallStatus.Cancelled;

            public long DurationMs { get; set; }
        }

        private sealed class PendingCall(CallState? record)
        {
            public CallState? Record { get; } = record;

            public long StartedAt { get; } = Stopwatch.GetTimestamp();

            public CancellationTokenSource Controller { get; } = new();
        }
    }
}
