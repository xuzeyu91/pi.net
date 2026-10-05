using System.Text.Json.Nodes;
using Pi.Codemode.Runtime;

namespace Pi.Codemode;

/// <summary>工具调用上下文。对应 TS <c>CodemodeToolContext</c>（types.ts）。</summary>
public sealed record CodemodeToolContext
{
    /// <summary>
    /// 脚本结束（含未 await 的调用）、执行超时、调用方中止或沙箱关闭时取消。
    /// </summary>
    public required CancellationToken Signal { get; init; }
}

/// <summary>脚本可见的工具/全局函数。对应 TS <c>CodemodeTool</c>。</summary>
public sealed record CodemodeTool
{
    /// <summary>
    /// 脚本以 <c>tools.&lt;id&gt;(args)</c> 调用（<c>&lt;id&gt;</c> 是名字里非标识符字符替换为 <c>_</c> 后的结果），
    /// 也可用 <c>tools["&lt;name&gt;"](args)</c>。全局函数以 <c>&lt;name&gt;(args)</c> 调用，
    /// 必须是标识符或 <c>&lt;namespace&gt;.&lt;member&gt;</c>（后者归入一个冻结的命名空间对象）。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>渲染为声明里的文档注释，并在工具列表的 <c>ALL_TOOLS</c> 中列出。</summary>
    public string? Description { get; init; }

    /// <summary>单参数 schema；渲染为参数类型，缺省为 <c>unknown</c>。</summary>
    public JsonNode? InputSchema { get; init; }

    /// <summary>解析值 schema；渲染为 promise 类型，缺省为 <c>unknown</c>。</summary>
    public JsonNode? OutputSchema { get; init; }

    /// <summary>仅全局函数：<c>execute</c> 收到全部调用参数数组而非第一个。</summary>
    public bool Spread { get; init; }

    /// <summary>
    /// 仅全局函数：用于 <c>renderDeclarations</c> 的 TypeScript 参数列表与返回类型
    /// （如 <c>(type: string, id?: string): Promise&lt;Model[]&gt;</c>），替代按 schema 渲染。
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    /// <c>args</c> 是脚本传入值经 JSON 往返后的结果；返回值必须可 JSON 序列化；
    /// 抛出的错误在脚本侧表现为同名 message 的 <c>Error</c>。
    /// </summary>
    public required Func<JsonNode?, CodemodeToolContext, Task<JsonNode?>> Execute { get; init; }
}

/// <summary>脚本输出的一项。对应 TS <c>CodemodeOutputItem</c>（<c>data</c> 为 base64）。</summary>
public abstract record CodemodeOutputItem
{
    private CodemodeOutputItem() { }

    public sealed record Text(string Value) : CodemodeOutputItem;

    public sealed record Image(string Data, string MimeType) : CodemodeOutputItem;
}

/// <summary>单次工具调用状态。对应 TS <c>CodemodeCallStatus</c>。</summary>
public enum CodemodeCallStatus
{
    Ok,
    Error,
    Cancelled,
}

/// <summary>一次工具调用记录。对应 TS <c>CodemodeCall</c>。</summary>
public sealed record CodemodeCall(string Name, CodemodeCallStatus Status, long DurationMs);

/// <summary>失败类别。对应 TS <c>CodemodeErrorKind</c>。</summary>
public enum CodemodeErrorKind
{
    /// <summary>脚本抛出或解析失败；<c>Name</c>/<c>Stack</c> 来自脚本错误。</summary>
    Script,

    /// <summary>整体截止时间到；worker 已被终止。</summary>
    Timeout,

    /// <summary>调用方信号触发或沙箱关闭；worker 已被终止。</summary>
    Aborted,

    /// <summary>worker 或 VM 在脚本控制之外失败（wasm trap、worker 文件缺失等）。</summary>
    Sandbox,
}

/// <summary>结构化失败。对应 TS <c>CodemodeError</c>。</summary>
public sealed record CodemodeError(CodemodeErrorKind Kind, string Message)
{
    public string? Name { get; init; }

    public string? Stack { get; init; }
}

/// <summary>脚本用 <c>store()</c> 改动的键。对应 TS <c>CodemodeStoreWrites</c>。</summary>
public sealed record CodemodeStoreWrites
{
    public required IReadOnlyDictionary<string, JsonNode?> Set { get; init; }

    /// <summary>以 <c>undefined</c> 存储（即删除）的键。</summary>
    public required IReadOnlyList<string> Delete { get; init; }
}

/// <summary>
/// 一次执行的结果。对应 TS <c>CodemodeResult</c>：失败的执行同样保留到失败点为止的
/// <c>output</c>；<c>exit()</c> 以 <c>Value = null</c> 完成。
/// </summary>
public abstract record CodemodeResult
{
    private CodemodeResult() { }

    public sealed record Success(JsonNode? Value, IReadOnlyList<CodemodeOutputItem> Output,
        IReadOnlyList<CodemodeCall> Calls, CodemodeStoreWrites StoreWrites) : CodemodeResult;

    public sealed record Failure(CodemodeError Error, IReadOnlyList<CodemodeOutputItem> Output,
        IReadOnlyList<CodemodeCall> Calls) : CodemodeResult;
}

/// <summary>沙箱选项。对应 TS <c>CodemodeSandboxOptions</c>。</summary>
public sealed record CodemodeSandboxOptions
{
    public IReadOnlyList<CodemodeTool>? Tools { get; init; }

    /// <summary>
    /// 以顶层标识符（而非挂在 <c>tools</c> 下）暴露的函数，用于挂图片等宿主助手。
    /// 行为与工具一致（JSON 往返、promise 结果），但不记入 <c>result.calls</c>。
    /// 名字必须是标识符且不得遮蔽内建全局（tools/ALL_TOOLS/console/text/image/exit/store/load）。
    /// </summary>
    public IReadOnlyList<CodemodeTool>? Globals { get; init; }

    /// <summary>每次执行的整体截止时间（含工具耗时）；<c>long.MaxValue</c> 表示禁用。缺省 300000。</summary>
    public long? TimeoutMs { get; init; }

    /// <summary>QuickJS VM 可分配的最大内存；超出后脚本内报 <c>InternalError: out of memory</c>。</summary>
    public long? MemoryLimitBytes { get; init; }

    /// <summary>
    /// 引擎特定的 VM 载荷（如 <see cref="CodemodeWasmModule"/>），经 <c>CodemodeWorkerData.Wasm</c>
    /// 透传给引擎；缺省由引擎自行加载。对应 TS 的 <c>wasm</c> 选项（host 侧 <c>loadQuickJSWasm</c>
    /// 提供的已编译 quickjs-wasi 模块）。
    /// </summary>
    public object? Wasm { get; init; }

    /// <summary>
    /// JS 引擎注入点；缺省 <see cref="UnsupportedCodemodeJsEngine"/>（执行以 <c>kind: "sandbox"</c>
    /// 失败并提示注入引擎）。对应 TS 的 worker + quickjs-wasi VM——C# 无
    /// <c>WebAssembly.compile</c> 与 worker 线程模型，故 VM 执行抽成该注入点（设计差异第 18 条）。
    /// </summary>
    public ICodemodeJsEngine? Engine { get; init; }
}

/// <summary>单次执行选项。对应 TS <c>CodemodeExecuteOptions</c>。</summary>
public sealed record CodemodeExecuteOptions
{
    public CancellationToken Signal { get; init; }

    /// <summary>覆盖沙箱默认的截止时间。</summary>
    public long? TimeoutMs { get; init; }

    /// <summary>脚本用 <c>load(key)</c> 读取的值（必须可 JSON 序列化）。</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Store { get; init; }
}
