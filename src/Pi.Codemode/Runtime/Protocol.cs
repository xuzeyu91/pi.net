using System.Text.Json.Nodes;

namespace Pi.Codemode.Runtime;

/// <summary>
/// host（主线程）与 worker 之间的消息。对应 TS <c>runtime/protocol.ts</c>：
/// 工具参数、结果与取值都以 JSON 字符串跨界——worker 只把字符串送进/送出 QuickJS VM，
/// 自己从不构造结构化值。
/// </summary>
public sealed record CodemodeWorkerData
{
    public required string Code { get; init; }

    /// <summary><c>JsName</c> 是脚本使用的标识符；<c>Description</c> 列在 <c>ALL_TOOLS</c> 里。</summary>
    public required IReadOnlyList<CodemodeWorkerTool> Tools { get; init; }

    public required IReadOnlyList<CodemodeWorkerGlobal> Globals { get; init; }

    /// <summary>
    /// host 透传的引擎特定载荷（如 <see cref="CodemodeWasmModule"/>）；<c>null</c> 表示未提供，
    /// 由引擎按需加载（TS 是 host 编译后经 structured clone 分享给 worker 的 quickjs-wasi 模块）。
    /// </summary>
    public object? Wasm { get; init; }

    public long? MemoryLimitBytes { get; init; }

    /// <summary><c>load()</c> 的快照：键 → JSON 文本。</summary>
    public required IReadOnlyDictionary<string, string> Store { get; init; }
}

/// <summary>worker 数据里的工具描述。对应 TS <c>WorkerData.tools</c> 元素。</summary>
public sealed record CodemodeWorkerTool(string Name, string JsName, string Description);

/// <summary>worker 数据里的全局函数描述。对应 TS <c>WorkerData.globals</c> 元素。</summary>
public sealed record CodemodeWorkerGlobal(string Name, bool Spread);

/// <summary>脚本抛出的错误的 JSON 编码 <c>{ name?, message, stack? }</c>。对应 TS <c>ScriptErrorJson</c>。</summary>
public sealed record CodemodeScriptError(string Message)
{
    public string? Name { get; init; }

    public string? Stack { get; init; }
}

/// <summary>worker → host 消息。对应 TS <c>WorkerToHostMessage</c>。</summary>
public abstract record WorkerToHostMessage
{
    private WorkerToHostMessage() { }

    public sealed record Call(long Id, string Target, string Name, string? Args) : WorkerToHostMessage;

    public sealed record Output(CodemodeOutputItem Item) : WorkerToHostMessage;

    /// <summary><c>Writes</c> 是 <c>store()</c> 的 JSON 数组（<c>[key, json]</c>）与删除（<c>[key]</c>）；失败时不上报。</summary>
    public sealed record Done(bool Ok, string? Value, string? Writes, CodemodeScriptError? Error) : WorkerToHostMessage;

    /// <summary>VM 在脚本控制之外失败（例如 wasm trap）。</summary>
    public sealed record Crash(string Message) : WorkerToHostMessage;
}

/// <summary>host → worker 消息。对应 TS <c>HostToWorkerMessage</c>。</summary>
public abstract record HostToWorkerMessage
{
    private HostToWorkerMessage() { }

    /// <summary><c>Payload</c> 在 <c>Ok</c> 时是 JSON 结果，否则是错误消息。</summary>
    public sealed record Result(long Id, bool Ok, string? Payload) : HostToWorkerMessage;
}

/// <summary>消息形状判定。对应 TS <c>isWorkerToHostMessage</c> / <c>isHostToWorkerMessage</c>。</summary>
public static class CodemodeProtocol
{
    public static bool IsWorkerToHostMessage(object? value)
    {
        if (value is not JsonObject obj) return false;
        var type = obj["type"]?.GetValue<string>();
        return type is "call" or "output" or "done" or "crash";
    }

    public static bool IsHostToWorkerMessage(object? value)
        => value is JsonObject obj && obj["type"]?.GetValue<string>() == "result";
}
