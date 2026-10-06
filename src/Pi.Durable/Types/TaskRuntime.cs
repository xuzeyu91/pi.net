using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Types;

/// <summary>一次任务的阶段处理器。对应 TS <c>PhaseHandler&lt;I, P, S, R, H&gt;</c>（C# 侧擦除：任务记录 + 擦除运行时）。</summary>
public delegate Task TaskPhaseHandler(TaskRecord task, ITaskRuntime runtime, Context context);

/// <summary>
/// 按名称把一个任务的钩子分发给每个匹配的已注册处理器。对应 TS <c>HookRunner&lt;H&gt;</c>。
/// 实现随 harness 阶段落地；处理器对象是注册表里 <see cref="HookRegistration.Handlers"/>
///（C# 形状为完整的钩子接口实例，未声明的钩子是显式 no-op——行为等价 TS 的「未定义即跳过」，
/// 见差异记录）。
/// </summary>
public interface IHookRunner
{
    /// <summary>
    /// 用每个声明了该钩子的处理器调用 <paramref name="invoke"/>。invoke 的普通抛出由实现上报并继续下一个
    /// 处理器；一旦调用被中止信号打断，错误向上传播。组合发生在 invoke 内部。
    /// </summary>
    Task EachAsync(string name, Func<object?, Task> invoke);
}

/// <summary>
/// 一次任务调用的操作面；调用结束后所有操作拒绝。对应 TS <c>TaskRuntime&lt;I, S, R, H&gt;</c>
/// （C# 侧擦除：任务 ID / 记录 / 结果全部用擦除形状，泛型任务阶段以 JSON 字典承载输入、
/// checkpoint 与结果——与调度器的擦除执行模型一致）。同时实现 <see cref="IHookApi"/>，
/// 因此运行时可直接作为钩子的 api 实参。
/// </summary>
public interface ITaskRuntime : IDocumentObserver, IDocumentReader, IHookApi
{
    /// <summary>
    /// run 被 abortTask()、Harness 关闭或调用结束时中止。调用结束后仍在使用它的悬挂等待会被取消
    /// ——反正它已写不进任何东西。
    /// </summary>
    CancellationToken Signal { get; }

    /// <summary>当前阶段的注册表快照；每个阶段边界刷新。</summary>
    IRegistrySnapshot Registry { get; }

    /// <summary>任务对话的 agent；每阶段至多解析一次、首次使用时解析、阶段内固定。</summary>
    Task<Agent> AgentAsync(Context context);

    /// <summary><c>HarnessOptions.settings</c>，每次访问时解析。</summary>
    Settings Settings { get; }

    /// <summary>Harness 的模型目录。</summary>
    Pi.Ai.Models.Models Models { get; }

    /// <summary>为任务的对话调用 <c>HarnessOptions.env</c>；其错误原样抛出。</summary>
    Task<IExecutionEnv?> EnvAsync(Context context);

    /// <summary>该任务名称的钩子（来自其对话选择的扩展），按扩展顺序。</summary>
    IHookRunner Hooks { get; }

    /// <summary>
    /// 在重读任务之后的 Session 变更线上提交。任务已终态、调用已结束、Harness 正在关闭或 run 任务
    /// 带中止标记时拒绝。返回的状态在同一提交里替换任务状态；返回 null 则保持不变。
    /// <c>tx.createTask()</c> 缺省为该任务的对话。（C# 以具体 <see cref="Transaction"/> 承载回调，
    /// 因文档面辅助（边界、任务创建）需要事务级方法。）
    /// </summary>
    Task CommitAsync(Func<Transaction, TaskRecord, Task<TaskState?>> change, Context context);

    /// <summary>以任务对话为缺省属主创建子任务（调用内便捷入口）。</summary>
    Task<TaskId<object?>> CreateTaskAsync(AnyDurableTask task, object? input, TaskOptions options, Context context);

    /// <summary>已提交的任务记录；不存在为 null。</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context);

    /// <summary>解析为任务的终态回执；调用结束时拒绝。</summary>
    Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context);

    /// <summary>终态任务的结果，按序；缺失或未终态时拒绝。等待之后使用。</summary>
    Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(IReadOnlyList<TaskId<object?>> ids, Context context);

    /// <summary>
    /// 既有对话的调用内句柄，例如本任务拥有的对话；不存在为 null。其操作与返回的提交在调用结束后拒绝；
    /// 已受理的工作保持持久。
    /// </summary>
    Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context);

    /// <summary>任务对话可见的已提交条目；不存在 / 不可见为 null。</summary>
    Task<EntryRecord?> EntryAsync(EntryId id, Context context);

    /// <summary>条目不存在、不可见或种类不符为 null。</summary>
    Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, Context context);

    /// <summary>已提交的原始活动转录与模型上下文；可在可见条目 <paramref name="at"/> 处截断。</summary>
    Task<ContextView> ContextAsync(ConversationId conversationId, Context context, EntryId? at = null);

    /// <summary>Harness 时钟（Unix 毫秒）。</summary>
    long Now();

    /// <summary>把非致命失败转发给 <c>HarnessOptions.onReport</c>。</summary>
    void Report(Exception error);

    /// <summary>Harness 时钟到达 <paramref name="until"/> 后解析；调用或 context 取消时拒绝。</summary>
    Task SleepAsync(long until, Context context);
}
