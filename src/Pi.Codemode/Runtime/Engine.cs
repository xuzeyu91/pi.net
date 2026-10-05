namespace Pi.Codemode.Runtime;

/// <summary>
/// JS 引擎注入点。对应 TS <c>runtime/worker.ts</c> + <c>quickjs-wasi</c> VM：TS 在 worker 线程里跑
/// <c>WebAssembly.compile</c> 编译的 QuickJS，用共享 Int32 做中断轮询、以 <c>worker.terminate()</c>
/// 兜底。C# 无 <c>WebAssembly.compile</c> 与 worker 线程模型，故把「VM 执行」抽成该接口
/// （设计差异第 18 条）；消息形状与 <c>runtime/protocol.ts</c> 一致——工具参数、结果与取值都以
/// JSON 字符串跨界，VM 只把字符串送进/送出，自己从不构造结构化值。<para/>
/// 实现方职责（对应 worker.ts）：在全新 VM 里求值 <see cref="CodemodePrelude.Source"/> 得到
/// <c>{ settle, run, stalled }</c>；把 <c>data.Code</c> 包成
/// <c>(async (tools, console) =&gt; {${code}\n})</c> 后运行（前缀与脚本共享首行，报错行号才与
/// 手写脚本一致）；VM 消息经 <c>onMessage</c> 有序送达 host；host 的 result 消息经
/// <see cref="ICodemodeVm.Post"/> 送入 <c>settle</c>；每条消息处理后执行待决作业并调用
/// <c>stalled()</c>（对应 TS 的 drain）；VM 在脚本控制之外失败（wasm trap 等）以
/// <see cref="WorkerToHostMessage.Crash"/> 上报；QuickJS 写向 fd 1/2 的引擎诊断应被丢弃
/// （那属于宿主应用的输出）。
/// </summary>
public interface ICodemodeJsEngine
{
    /// <summary>
    /// 在全新 VM 里执行一次脚本（一次执行一个 VM，对应 TS「每次 execute 新建 worker」：失控脚本
    /// 被终止后不会污染后续执行）。必须快速返回——脚本进度经 <paramref name="onMessage"/> 异步送达，
    /// 不得在此等待脚本结束。抛出异常时 host 以 <c>kind: "sandbox"</c> 失败结束本次执行。
    /// </summary>
    ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage);
}

/// <summary>一次 VM 执行的句柄。对应 TS 的 Worker 实例（postMessage + 中断标志 + terminate）。</summary>
public interface ICodemodeVm
{
    /// <summary>
    /// host → VM 消息（工具调用结果）。对应 TS <c>worker.postMessage</c>。
    /// <see cref="StopAsync"/> 之后到达的消息应被忽略（对应 TS 中向已终止 worker 发送即丢弃）。
    /// </summary>
    void Post(HostToWorkerMessage message);

    /// <summary>
    /// 停止 VM 并等待其退出：对应 TS 的 <c>Atomics.store(interrupt, 0, 1)</c> + <c>worker.terminate()</c>。
    /// 必须能停下正在 wasm 里自旋的脚本；返回后不再产生消息。host 在每次执行结束时都会调用
    /// （正常完成、超时、中止、沙箱关闭），实现抛出的异常被忽略。
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// 默认引擎：明确拒绝执行（同 chord 的 <c>UnsupportedFacetModuleHost</c> 约定）。原版以 worker +
/// QuickJS wasm 执行脚本；本移植不内置 JS 引擎，注入 <see cref="ICodemodeJsEngine"/> 即可运行。
/// </summary>
public sealed class UnsupportedCodemodeJsEngine : ICodemodeJsEngine
{
    public static readonly UnsupportedCodemodeJsEngine Instance = new();

    public const string Message =
        "Executing a codemode script requires a JavaScript engine. " +
        "This .NET port does not bundle one (the original runs QuickJS wasm in a worker thread); " +
        "inject an ICodemodeJsEngine into CodemodeSandboxOptions.Engine to run scripts.";

    private UnsupportedCodemodeJsEngine()
    {
    }

    public ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
        => throw new NotSupportedException(Message);
}
