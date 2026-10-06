using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Durable.Env;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using System.Text.Json.Serialization;

/// <summary>经 pi-ai <see cref="Models"/> 解析的 provider 与模型 ID。对应 TS <c>ModelRef</c>。</summary>
public sealed record ModelRef(string Provider, string ModelId);

/// <summary>
/// 用户输入：用户消息的内容块列表。对应 TS <c>UserInput = UserMessage["content"]</c>。
/// </summary>
public static class UserInput
{
    /// <summary>空输入（TS 的空 content 数组）。</summary>
    public static IReadOnlyList<ContentBlock> Empty { get; } = [];
}

/// <summary>
/// 宿主提交草稿：可能启动一次运行的用户输入，或被动条目写入。
/// 对应 TS <c>SubmissionDraft</c>（判别联合，C# 以抽象 record 承载）。
/// </summary>
public abstract record SubmissionDraft
{
    /// <summary>wire 判别符："input" / "write"。</summary>
    public abstract string Type { get; }

    /// <summary>宿主提供的去重键（对话作用域）。</summary>
    public string? RequestId { get; init; }

    /// <summary>用户输入提交。</summary>
    public sealed record Input : SubmissionDraft
    {
        public override string Type => "input";

        public required IReadOnlyList<ContentBlock> Content { get; init; }

        /// <summary>忙碌时的处置："steer" / "followUp" / "reject"。</summary>
        public string? WhenBusy { get; init; }
    }

    /// <summary>被动条目写入提交。</summary>
    public sealed record Write : SubmissionDraft
    {
        public override string Type => "write";

        public required EntryDraft Entry { get; init; }
    }
}

/// <summary>已结算（done / unanswered）的提交记录。对应 TS <c>SettledSubmissionRecord</c>。</summary>
public sealed record SettledSubmissionRecord(SubmissionRecord Record)
{
    /// <summary>底层记录；<see cref="SubmissionRecord"/> 携带终态 <see cref="SubmissionStatus"/>。</summary>
    public SubmissionRecord Record { get; } = Record;
}

/// <summary>终态任务的回执视图。对应 TS <c>SettledTask&lt;R&gt;</c>（C# 无泛型结果参数：R 擦除）。</summary>
public sealed record SettledTask(TaskRecord Record)
{
    /// <summary>底层记录的状态必须已是终态。</summary>
    public TaskState State => Record.State;
}

/// <summary>一个对话 <c>pi.usage</c> 的解析视图。对应 TS <c>UsageState</c>。</summary>
public sealed record UsageState
{
    public required IReadOnlyDictionary<string, Pi.Ai.Types.Usage> Models { get; init; }

    public required IReadOnlyDictionary<string, Pi.Ai.Types.Usage> Tools { get; init; }
}

/// <summary>一次已被受理的持久化提交的宿主句柄。对应 TS <c>Submission</c>。</summary>
public interface ISubmission
{
    SubmissionId Id { get; }

    Task<SubmissionRecord> StatusAsync(Context context);

    Task<SettledSubmissionRecord> WaitAsync(Context context);

    /// <summary>返回 "aborted" / "already_placed" / "settled"。</summary>
    Task<string> AbortAsync(Context context);
}

/// <summary><c>Conversation.abort()</c> 的选项。对应 TS <c>ConversationAbortOptions</c>。</summary>
public sealed record ConversationAbortOptions
{
    /// <summary>
    /// 跨后台边界：受理时标记可达的每个存活任务（无视后台标记）、撤回可达对话的排队输入，
    /// 并等待这些任务终态、对话普通空闲。之后创建的后台工作既不标记也不等待。
    /// </summary>
    public bool Background { get; init; }
}

/// <summary>
/// 绑定到一次调用的对话操作，供任务与工具使用。调用结束后拒绝；被动条目改为普通事务写入。
/// 对应 TS <c>ConversationHandle</c>。
/// </summary>
public interface IConversationHandle
{
    ConversationId Id { get; }

    Task<ISubmission> SubmitAsync(SubmissionDraft.Input submission, Context context);

    /// <summary><c>Conversation.abort()</c>：撤回排队输入、中止普通属主范围并等待其空闲。</summary>
    Task AbortAsync(Context context, ConversationAbortOptions? options = null);

    /// <summary>对话的普通属主范围没有存活非后台任务时完成。</summary>
    Task WaitForIdleAsync(Context context);
}

/// <summary>工具结果请求的 post-tools 控制。对应 TS <c>ToolControl</c>。</summary>
public sealed record ToolControl
{
    public IReadOnlyList<string>? AddTools { get; init; }

    public bool Terminate { get; init; }

    public string? Handoff { get; init; }
}

/// <summary>关于一次调用的模型与 UI 可见备注（如截断或 spill 路径）。对应 TS <c>ToolDiagnostic</c>。</summary>
public sealed record ToolDiagnostic
{
    /// <summary>"info" / "warn" / "error"。</summary>
    public required string Severity { get; init; }

    public required string Message { get; init; }

    public string? Code { get; init; }
}

/// <summary>一次工具执行的结果。对应 TS <c>ToolExecutionResult&lt;TDetails&gt;</c>（C# 侧 details 擦除为 object?）。</summary>
public sealed record ToolExecutionResult
{
    /// <summary>缺省：保留的 <c>output()</c> 文本成为内容。</summary>
    public IReadOnlyList<ContentBlock>? Content { get; init; }

    public bool IsError { get; init; }

    /// <summary>缺省：最后一次 <c>details()</c> 的值成为 details。</summary>
    public object? Details { get; init; }

    /// <summary>追加在 <c>api.diagnostic()</c> 记录之后。</summary>
    public IReadOnlyList<ToolDiagnostic>? Diagnostics { get; init; }

    /// <summary>执行自身的花费（如模型调用）；存于结果与 <c>pi.usage.tools</c>。</summary>
    public Pi.Ai.Types.Usage? Usage { get; init; }

    public ToolControl? Control { get; init; }
}

/// <summary>一轮的工具是并行还是按调用顺序逐个执行。对应 TS <c>ToolExecutionMode</c>。</summary>
[JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ToolExecutionMode>))]
public enum ToolExecutionMode
{
    Parallel,
    Sequential,
}

/// <summary>边界放置一种模式的多少个排队项。对应 TS <c>QueueMode</c>。</summary>
[JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<QueueMode>))]
public enum QueueMode
{
    All,
    OneAtATime,
}

/// <summary>
/// 一次工具调用可用的操作。普通对象，包装器可展开。调用结束后每个操作都拒绝。
/// 对应 TS <c>ToolExecutionApi&lt;TDetails&gt;</c>（C# 侧 details 擦除为 object?）。
/// </summary>
public interface IToolExecutionApi : IDocumentObserver, IDocumentReader
{
    TaskId<object?> TaskId { get; }

    ConversationId ConversationId { get; }

    string CallId { get; }

    /// <summary>工具任务所处阶段的注册表快照。</summary>
    IRegistrySnapshot Registry { get; }

    /// <summary>调用方对话的 agent，按工具任务的阶段解析。</summary>
    Task<Agent> AgentAsync(Context context);

    /// <summary>由 <see cref="HarnessOptions.EnvFactory"/> 为本次调用构建；无环境为 undefined。</summary>
    IExecutionEnv? Env { get; }

    /// <summary>
    /// 追加运行输出；结果缺省 content 时成为内容。<paramref name="skipped"/> 计此块之前被省略的输出。
    /// </summary>
    void Output(string chunk, ShellOutputSkip? skipped = null);

    /// <summary>同 <see cref="Output(string, ShellOutputSkip?)"/> 的字节重载。</summary>
    void Output(byte[] chunk, ShellOutputSkip? skipped = null);

    /// <summary>
    /// 本次调用输出保留的尾部与进度提交节奏，用于 <c>ShellExecOptions.window</c>；
    /// 工具保留输出头部时为 undefined（不能接受 skip）。
    /// </summary>
    ShellOutputWindow? OutputWindow { get; }

    /// <summary>记录关于本次调用的模型可见备注。</summary>
    void Diagnostic(ToolDiagnostic diagnostic);

    /// <summary>替换运行中的 details；结果缺省 details 时最后一次值生效。</summary>
    Task DetailsAsync(object? value, Context context);

    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context);

    /// <summary>读取命名的首写必胜 memo；不存在为 undefined。</summary>
    Task<object?> MemoAsync(string name, Context context);

    /// <summary>写入 memo（首写必胜），并解析为生效值。</summary>
    Task<object> MemoAsync(string name, object candidate, Context context);

    Task<TaskId<object?>> CreateTaskAsync(
        AnyDurableTask task, object? input, TaskOptions options, Context context);

    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context);

    Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context);

    /// <summary>既有对话的调用内句柄，例如本工具在 <c>commit()</c> 中创建的对话。</summary>
    Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context);
}

/// <summary>
/// 注册表中的可执行工具。只有 pi-ai 工具字段进入转录。对应 TS
/// <c>ToolRegistration&lt;TParameters, TDetails&gt;</c>（C# 无 TSchema 泛型：参数经
/// <see cref="ToolSchema"/> 承载，args 以 JSON 字典表达）。
/// </summary>
public interface IToolRegistration
{
    string Name { get; }

    string Description { get; }

    ToolSchema Parameters { get; }

    /// <summary>中断的执行在恢复时可否重跑。缺省 "unsafe"。</summary>
    string? Replay { get; }

    /// <summary>缺省跟随设置 <c>toolExecution</c>；一次 sequential 调用使整轮 sequential。</summary>
    ToolExecutionMode? ExecutionMode { get; }

    ToolOutputLimits? OutputLimits { get; }

    /// <summary>
    /// 校验前修复模型常见的参数错误（如数组位置放了 JSON 字符串）。必须纯且不得改参：
    /// 重试重跑；结果仍按 <see cref="Parameters"/> 校验。
    /// </summary>
    object? PrepareArguments(object args);

    Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context);
}

/// <summary>工具输出保留限制。对应 TS <c>outputLimits</c> 形状。</summary>
public sealed record ToolOutputLimits
{
    public int? MaxBytes { get; init; }

    public int? MaxLines { get; init; }

    /// <summary>"head" / "tail"。</summary>
    public string? Retain { get; init; }
}

/// <summary>已擦除的可执行任务定义（存于注册表）。对应 TS <c>AnyTask</c>。</summary>
public sealed record AnyDurableTask
{
    public required string Name { get; init; }

    public required int Version { get; init; }

    /// <summary>擦除的初值工厂（无参委托）。</summary>
    public required Func<object?> Initial { get; init; }

    /// <summary>阶段名 → 擦除的处理器对象。</summary>
    public required IReadOnlyDictionary<string, object?> Phases { get; init; }

    /// <summary>擦除的 abort 处理器。</summary>
    public object? Abort { get; init; }

    /// <summary>擦除的迁移委托。</summary>
    public object? Migrate { get; init; }

    /// <summary>声明的钩子处理器映射。</summary>
    public object? Hooks { get; init; }

    /// <summary>从类型化任务擦除。</summary>
    public static AnyDurableTask From<TInput, TState, TResult>(DurableTask<TInput, TState, TResult> task)
        where TState : class
    {
        var d = task.Definition;
        return new AnyDurableTask
        {
            Name = d.Name,
            Version = d.Version,
            Initial = () => d.Initial(default!),
            Phases = (IReadOnlyDictionary<string, object?>?)d.Phases ?? new Dictionary<string, object?>(),
            Abort = d.Abort,
            Migrate = d.Migrate,
            Hooks = d.Hooks,
        };
    }
}

/// <summary>一次请求准备的系统提示段渲染输入。对应 TS <c>PromptInput</c>。</summary>
public sealed record PromptInput
{
    public required ConversationId ConversationId { get; init; }

    /// <summary>请求的解析；<c>Agent.Tools</c> 是本请求提供的工具。</summary>
    public required Agent Agent { get; init; }

    /// <summary>由 <see cref="HarnessOptions.EnvFactory"/> 为本次准备构建；无环境为 undefined。</summary>
    public IExecutionEnv? Env { get; init; }

    /// <summary>重放活动转录后已生效的段。</summary>
    public required IReadOnlyDictionary<string, string> Shown { get; init; }

    /// <summary>已提交文档读取。</summary>
    public required IDocumentReader Read { get; init; }
}

/// <summary>一个系统提示段；agent 的段按顺序在每次请求前渲染。对应 TS <c>PromptSection</c>。</summary>
public interface IPromptSection
{
    string Key { get; }

    /// <summary>渲染；undefined 表示本请求跳过该段。</summary>
    Task<string?> RenderAsync(PromptInput input, Context context);

    /// <summary>缺省 true：文本包裹为 <c>&lt;key&gt;…&lt;/key&gt;</c>。</summary>
    bool? Tag { get; }
}

/// <summary>由 <c>hook()</c> 构建；按名称匹配任务。对应 TS <c>HookRegistration</c>。</summary>
public sealed record HookRegistration(string Task, object Handlers);

/// <summary>
/// 由 <c>wrapTool()</c> / <c>wrapSection()</c> 构建；以名称为目标的纯包装。对应 TS <c>Wrap</c>。
/// </summary>
public abstract record Wrap
{
    public sealed record Tool(string Name, Func<IToolRegistration, IToolRegistration> Wrap) : Wrap;

    public sealed record Section(string Key, Func<IPromptSection, IPromptSection> Wrap) : Wrap;
}

/// <summary>命名的代码包；安装进注册表并按名称被对话选择。对应 TS <c>Extension</c>。</summary>
public interface IExtension
{
    string Name { get; }

    IReadOnlyList<IToolRegistration>? Tools { get; }

    IReadOnlyList<IPromptSection>? Sections { get; }

    IReadOnlyList<HookRegistration>? Hooks { get; }

    /// <summary>本扩展被选择处按序应用。</summary>
    IReadOnlyList<Wrap>? Wraps { get; }

    /// <summary>按名称为每个任务解析，无论哪个对话选择本扩展。</summary>
    IReadOnlyList<AnyDurableTask>? Tasks { get; }
}

/// <summary>一次已发布注册表状态的不可变视图。对应 TS <c>RegistrySnapshot</c>。</summary>
public interface IRegistrySnapshot
{
    IReadOnlyList<IExtension> Installed();

    IExtension? Extension(string name);

    /// <summary>每个已安装工具与其扩展，按安装顺序。名称可跨扩展重复。</summary>
    IReadOnlyList<(IExtension Extension, IToolRegistration Tool)> Tools();

    IReadOnlyList<(IExtension Extension, IPromptSection Section)> Sections();

    /// <summary>内建与已安装的任务定义。</summary>
    IReadOnlyList<AnyDurableTask> Tasks();

    AnyDurableTask? Task(string name);
}

/// <summary>Harness 消费的注册表读侧。对应 TS <c>RegistryReader</c>。</summary>
public interface IRegistryReader
{
    /// <summary>整个当前注册表的不可变视图。</summary>
    IRegistrySnapshot Snapshot();

    /// <summary>每次发布后同步调用；唤醒调度器重估被阻塞的任务。返回退订。</summary>
    IDisposable Subscribe(Action listener);
}

/// <summary>应用拥有的扩展注册表。对应 TS <c>Registry</c>。</summary>
public interface IRegistry : IRegistryReader
{
    /// <summary>安装 <paramref name="extension"/>，或就地替换同名已安装扩展。立即发布。</summary>
    void Install(IExtension extension);

    /// <summary>移除名为 <paramref name="extension"/>.Name 的已安装扩展（无论对象）。之后可再安装。</summary>
    void Uninstall(IExtension extension);
}

/// <summary>一个对话的存储选择；名称而非对象。未设字段跟随宿主。对应 TS <c>AgentState</c>。</summary>
public sealed record AgentState
{
    public ModelRef? Model { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>数组：恰好选择这些扩展（按序）。对象：编辑宿主默认选择。</summary>
    public ExtensionSelection? Extensions { get; init; }

    /// <summary>过滤所选扩展的工具；数组：恰好提供这些（按序）。</summary>
    public ToolSelection? Tools { get; init; }

    /// <summary>在每个扩展段之后渲染，作为段 <c>instructions</c>。</summary>
    public string? Instructions { get; init; }

    /// <summary>环境文件系统内的目录，传给 <see cref="HarnessOptions.EnvFactory"/>。</summary>
    public string? Cwd { get; init; }

    /// <summary>扩展选择：数组或 {add, remove} 对象。</summary>
    public sealed record ExtensionSelection
    {
        public IReadOnlyList<string>? Exact { get; init; }

        public IReadOnlyList<string>? Add { get; init; }

        public IReadOnlyList<string>? Remove { get; init; }
    }

    /// <summary>工具选择：数组或 {remove} 对象。</summary>
    public sealed record ToolSelection
    {
        public IReadOnlyList<string>? Exact { get; init; }

        public IReadOnlyList<string>? Remove { get; init; }
    }
}

/// <summary>
/// 对 <c>pi.agent</c> 的变更：给定字段替换存储值，<c>ClearX</c> 清除。对应 TS <c>AgentChange</c>
/// （TS 以 null 表示清除；C# 无隐式 null 语义，用显式清除标记）。
/// </summary>
public sealed record AgentChange
{
    public ModelRef? Model { get; init; }

    public bool ClearModel { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public bool ClearThinkingLevel { get; init; }

    public ExtensionChange? Extensions { get; init; }

    public bool ClearExtensions { get; init; }

    public ToolChange? Tools { get; init; }

    public bool ClearTools { get; init; }

    public string? Instructions { get; init; }

    public bool ClearInstructions { get; init; }

    public string? Cwd { get; init; }

    public bool ClearCwd { get; init; }

    /// <summary>扩展对象或 {add, remove} 的变更载荷。</summary>
    public sealed record ExtensionChange
    {
        public IReadOnlyList<IExtension>? Exact { get; init; }

        public IReadOnlyList<IExtension>? Add { get; init; }

        public IReadOnlyList<IExtension>? Remove { get; init; }
    }

    /// <summary>工具对象或 {remove} 的变更载荷。</summary>
    public sealed record ToolChange
    {
        public IReadOnlyList<IToolRegistration>? Exact { get; init; }

        public IReadOnlyList<IToolRegistration>? Remove { get; init; }
    }
}

/// <summary>对照注册表快照与设置解析出的对话 agent。对应 TS <c>Agent</c>。</summary>
public sealed record Agent
{
    public ModelRef? Model { get; init; }

    public required ThinkingLevel ThinkingLevel { get; init; }

    public required IReadOnlyList<IExtension> Extensions { get; init; }

    /// <summary>一个请求提供的工具，按序。</summary>
    public required IReadOnlyList<IToolRegistration> Tools { get; init; }

    /// <summary>扩展段，然后（设置时）<c>instructions</c>。</summary>
    public required IReadOnlyList<IPromptSection> Sections { get; init; }

    public string? Instructions { get; init; }

    public string? Cwd { get; init; }
}

/// <summary>对话创建初始化回调：运行于创建提交内。对应 TS <c>ConversationInit</c>。</summary>
public delegate Task ConversationInit(ITx tx, ConversationId conversationId);

/// <summary>对话创建选项。对应 TS <c>ConversationCreateOptions</c>。</summary>
public sealed record ConversationCreateOptions
{
    public required ConversationOwnership Ownership { get; init; }

    /// <summary>在创建钩子拷贝之后、<c>init</c> 之前的创建提交中应用。</summary>
    public AgentChange? Agent { get; init; }

    public ConversationInit? Init { get; init; }
}

/// <summary>精选的 pi-ai 请求选项；缺省字段用 pi-ai 默认。对应 TS <c>ConversationStreamOptions</c>。</summary>
public sealed record ConversationStreamOptions
{
    public object? Transport { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>单次请求尝试内的 provider/SDK 重试。</summary>
    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    public string? CacheRetention { get; init; }

    public DeferredOptions? Deferred { get; init; }

    /// <summary>延迟处理选项：布尔或 {window}。</summary>
    public sealed record DeferredOptions
    {
        public bool Enabled { get; init; }

        /// <summary>"15m" / "1h" / "24h"。</summary>
        public string? Window { get; init; }
    }
}

/// <summary>持久化生成尝试重试；pi-ai <c>RetryPolicy</c> 的 JSON 形状。对应 TS <c>ConversationRetryPolicy</c>。</summary>
public sealed record ConversationRetryPolicy
{
    public required bool Enabled { get; init; }

    public required int MaxRetries { get; init; }

    public required int BaseDelayMs { get; init; }

    public int? MaxAgentDelayMs { get; init; }
}

/// <summary>自动压缩阈值（spec §8.7）；手动压缩无视 enabled。对应 TS <c>CompactionPolicy</c>。</summary>
public sealed record CompactionPolicy
{
    /// <summary>阈值与溢出压缩。</summary>
    public required bool Enabled { get; init; }

    /// <summary>为回答保留的空间：超过 contextWindow - reserveTokens 时生成阻塞以压缩。</summary>
    public required int ReserveTokens { get; init; }

    /// <summary>摘要在原文中保留的近期上下文的近似大小。</summary>
    public required int KeepRecentTokens { get; init; }

    /// <summary>后台压缩在阻塞阈值之下 backgroundTokens 处启动；0 禁用。</summary>
    public required int BackgroundTokens { get; init; }
}

/// <summary>运行进度提交频率。对应 TS <c>ProgressPolicy</c>。</summary>
public sealed record ProgressPolicy
{
    /// <summary>生成中答案的提交之间的最小停顿。</summary>
    public required int PartialIntervalMs { get; init; }

    /// <summary>运行中工具输出的提交之间的最小停顿；大提交按其大小成比例停顿。</summary>
    public required int OutputIntervalMs { get; init; }
}

/// <summary>压缩的原因：<c>compact()</c>、生成准备中的阈值、或上下文溢出。对应 TS <c>CompactionReason</c>。</summary>
[JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CompactionReason>))]
public enum CompactionReason
{
    Manual,
    Threshold,
    Overflow,
}

/// <summary>
/// 阻塞式压缩摘要的 entryId，或对话自有压缩的摘要写入的 submissionId；
/// 无压缩内容时两者皆缺省。对应 TS <c>CompactionResult</c>。
/// </summary>
public sealed record CompactionResult
{
    public EntryId? EntryId { get; init; }

    public SubmissionId? SubmissionId { get; init; }
}

/// <summary>
/// Harness 级运行策略。每次解析都读取且从不拷贝；getter 亦可。同步：一些读取器运行在 Session 线上。
/// 对应 TS <c>HarnessSettings</c>。
/// </summary>
public sealed record HarnessSettings
{
    /// <summary>缺省扩展选择；缺省：每个已安装扩展，按安装顺序。</summary>
    public IReadOnlyList<IExtension>? Extensions { get; init; }

    public ConversationStreamOptions? Stream { get; init; }

    public ConversationRetryPolicy? Retry { get; init; }

    public CompactionPolicy? Compaction { get; init; }

    public ProgressPolicy? Progress { get; init; }

    public ToolExecutionMode? ToolExecution { get; init; }

    public QueueMode? SteeringMode { get; init; }

    public QueueMode? FollowUpMode { get; init; }
}

/// <summary>已解析设置：每个字段都有其内建默认值，对象字段合并。对应 TS <c>Settings</c>。</summary>
public sealed record Settings
{
    /// <summary>缺省：每个已安装扩展，按安装顺序。</summary>
    public IReadOnlyList<IExtension>? Extensions { get; init; }

    public required ConversationStreamOptions Stream { get; init; }

    public required ConversationRetryPolicy Retry { get; init; }

    public required CompactionPolicy Compaction { get; init; }

    public required ProgressPolicy Progress { get; init; }

    public required ToolExecutionMode ToolExecution { get; init; }

    public required QueueMode SteeringMode { get; init; }

    public required QueueMode FollowUpMode { get; init; }
}

/// <summary><see cref="HarnessOptions.EnvFactory"/> 为其构建环境。对应 TS <c>EnvTarget</c>。</summary>
public sealed record EnvTarget
{
    public required ConversationId ConversationId { get; init; }

    /// <summary>对话的 agent <c>cwd</c>。</summary>
    public string? Cwd { get; init; }

    public required IDocumentReader Read { get; init; }
}

/// <summary>Harness 选项。对应 TS <c>HarnessOptions</c>。</summary>
public sealed record HarnessOptions
{
    /// <summary>生成使用的 pi-ai 模型访问。</summary>
    public required Models Models { get; init; }

    public required IRegistryReader Registry { get; init; }

    public HarnessSettings? Settings { get; init; }

    /// <summary>
    /// 每次使用时构建对话环境。从不在 Session 线上调用；可为异步。
    /// 对应 TS <c>env?: (target, context) => ExecutionEnv | undefined | Promise&lt;…&gt;</c>。
    /// </summary>
    public Func<EnvTarget, Context, Task<IExecutionEnv?>>? EnvFactory { get; init; }

    /// <summary>
    /// 运行于每个创建或 fork 对话的提交（含裸 <c>tx.createConversation()</c>），在内建 <c>pi.*</c>
    /// 文档之后、便捷应用 <c>agent</c> 与 <c>init</c> 之前。fork 已有其拷贝。表读抛
    /// <see cref="ReadAfterWrite"/>；抛出使创建提交失败。
    /// </summary>
    public Func<ITx, ConversationRecord, Task>? ConversationCreated { get; init; }

    public Func<long>? Now { get; init; }

    /// <summary>接收不使调用操作失败的扩展失败。不得抛出。</summary>
    public Action<object>? OnReport { get; init; }
}

/// <summary>存活任务及当前注册表下调度器将如何对待它。对应 TS <c>TaskInspection</c>。</summary>
public abstract record TaskInspection
{
    public required TaskRecord Record { get; init; }

    /// <summary>一次调用处于活跃状态。</summary>
    public sealed record Running : TaskInspection;

    /// <summary>下一调度轮保留它；定义更新且带 migrate 时 migrates。</summary>
    public sealed record Ready(bool Migrates) : TaskInspection;

    /// <summary>
    /// 等待这些存活任务：<c>on</code> 的存活部分，或（标记中止时）其存活的普通自有工作——
    /// 必须先于其 abort 处理器结束。
    /// </summary>
    public sealed record Waiting(IReadOnlyList<TaskId<object?>> On) : TaskInspection;

    /// <summary>结果保持到其普通自有工作排空。</summary>
    public sealed record Completing : TaskInspection;

    /// <summary>没有注册的定义能接手；中止它将以 orphaned 结算。</summary>
    public sealed record Blocked(string Reason, object? Error = null) : TaskInspection;
}

/// <summary>存活工作的时点视图：未完成任务与提交，读于 Session 线。对应 TS <c>HarnessInspection</c>。</summary>
public sealed record HarnessInspection
{
    /// <summary>"paused" / "running" / "closing"。</summary>
    public required string Scheduling { get; init; }

    public required IReadOnlyList<TaskInspection> Tasks { get; init; }

    /// <summary>排队与已放置的提交，按 ID 序。</summary>
    public required IReadOnlyList<SubmissionRecord> Submissions { get; init; }
}

/// <summary>原始活动转录与派生的模型上下文。对应 TS <c>ContextView</c>。</summary>
public sealed record ContextView
{
    /// <summary>最新适用的 head 标记，若有。</summary>
    public EntryRecord? Head { get; init; }

    /// <summary>原始活动条目：head 标记 + 其 head 到尾部的非 head 条目。</summary>
    public required IReadOnlyList<EntryRecord> Entries { get; init; }

    /// <summary>按条目：编辑与排除停止原因之后、工具结果排序之前的模型消息。</summary>
    public required IReadOnlyList<IReadOnlyList<ChatMessage>> Contributions { get; init; }

    /// <summary>下一次 provider 请求的模型上下文。</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
}

/// <summary>绑定到返回它的 Harness 的无状态对话句柄。按 <c>Id</c> 比较。对应 TS <c>Conversation</c>。</summary>
public interface IConversation
{
    ConversationId Id { get; }

    /// <summary>以当前注册表快照与设置解析。</summary>
    Task<Agent> AgentAsync(Context context);

    /// <summary>独立提交中的 <c>configure()</c>。</summary>
    Task ConfigureAsync(AgentChange change, Context context);

    /// <summary>
    /// 持久受理用户输入或被动条目写入。忙碌或有排队项的对话将其排入 <c>pi.inbox</c>；
    /// <c>whenBusy: "reject"</c> 以 <c>ConversationBusy</c> 拒绝且不写任何内容。
    /// </summary>
    Task<ISubmission> SubmitAsync(SubmissionDraft submission, Context context);

    /// <summary>
    /// 受理开启新上下文的 <c>pi.reset</c> 条目写入；给定 handoff 时作为用户消息携带。
    /// 受理后完成；忙碌时置于下一边界。
    /// </summary>
    Task ResetAsync(string? handoff, Context context);

    /// <summary>
    /// 受理手动压缩任务并返回其 ID。它在对话继续工作的同时摘要，并经写提交放置摘要：
    /// 空闲时立即，否则在下一边界（spec §8.7）。
    /// </summary>
    Task<TaskId<CompactionResult>> CompactAsync(string? instructions, Context context);

    /// <summary>Session 提交，其 <c>tx.createTask()</c> 缺省为本对话。</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context);

    Task<ContextView> ContextAsync(Context context);

    /// <summary>本对话的 fork 感知历史（最新在前）。</summary>
    Task<Page<EntryRecord>> EntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor, Context context);

    Task<IConversation> ForkAsync(EntryId at, ConversationCreateOptions options, Context context);

    /// <summary>
    /// 撤回排队输入（排队写入保留）、标记普通属主范围的每个存活非后台任务、发信号，
    /// 并在范围空闲时完成。除非设置 background，后台子树存活。
    /// </summary>
    Task AbortAsync(Context context, ConversationAbortOptions? options = null);

    /// <summary>
    /// 普通属主范围没有存活非后台任务时完成：本对话与其非后台任务（传递地）拥有的对话。
    /// </summary>
    Task WaitForIdleAsync(Context context);

    /// <summary>
    /// 结构视图（spec §9.3）作为可释放只读 Chord 状态；以及有界待发帧的序列化精确帧 watch。
    /// 对应 TS <c>viewState()</c> / <c>watch()</c> —— 视图文档定义在 P52（view.ts）落地后接入。
    /// </summary>
    Task<IConversationWatch> WatchAsync(Context context);
}

/// <summary>结构视图的有界待发帧 watch（泛型载荷在 P52 view.ts 落地时具体化）。对应 TS <c>ConversationWatch</c>。</summary>
public interface IConversationWatch : IDisposable
{
    /// <summary>本帧的已提交视图快照帧数（诊断用）。</summary>
    long Frames { get; }
}

/// <summary>一个 Session 上的持久 agent harness。对应 TS <c>Harness</c>。</summary>
public interface IHarness : ISession
{
    /// <summary>
    /// 启用任务调度。幂等；close 后抛出。索取进度的调用也会启用：
    /// <c>submit()</c>、<c>compact()</c>、<c>abort()</c>、<c>wait()</c>、<c>waitForTask()</c>、
    /// <c>waitForIdle()</c> 等；只读查看器不会。
    /// </summary>
    void Resume();

    /// <summary>返回保留的根对话；缺省时以 agent 与 init 在一次提交中创建。</summary>
    Task<IConversation> RootAsync(Context context, AgentChange? agent = null, ConversationInit? init = null);

    Task<IConversation?> ConversationAsync(ConversationId id, Context context);

    Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, Context context);

    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context);

    /// <summary>存活任务与未结算提交。不写任何内容，不运行任务代码。</summary>
    Task<HarnessInspection> InspectAsync(Context context);

    /// <summary>重新获取提交，例如重开后。</summary>
    Task<ISubmission?> SubmissionAsync(SubmissionId id, Context context);

    /// <summary>未知提交或属于其他对话时 "not_found"。</summary>
    Task<string> AbortSubmissionAsync(SubmissionId id, Context context, ConversationId? conversationId = null);

    /// <summary>
    /// 提交中止标记、发信号并加入活跃运行调用，再调度 abort 调用。定义不能接手的任务以 orphaned 结算。
    /// </summary>
    Task<string> AbortTaskAsync(TaskId<object?> id, Context context);

    /// <summary>以终态回执完成；取消 context 只取消本等待。</summary>
    Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context);

    /// <summary>每个无主对话的普通属主范围都没有存活非后台任务时完成。</summary>
    Task WaitForIdleAsync(Context context);

    /// <summary>Session 总计：每个对话的 <c>pi.usage</c> 相加。</summary>
    Task<UsageState> UsageAsync(Context context);

    /// <summary>
    /// 任务图（spec §9.5）作为可释放 Chord 状态；对应 TS <c>taskGraph()</c> / <c>watchTaskGraph()</c>
    /// —— 图文档定义在 P52（task-graph.ts）落地后接入。
    /// </summary>
    Task<ITaskGraphWatch> WatchTaskGraphAsync(Context context);
}

/// <summary>任务图的有界待发帧 watch（泛型载荷在 P52 task-graph.ts 落地时具体化）。对应 TS <c>TaskGraphWatch</c>。</summary>
public interface ITaskGraphWatch : IDisposable
{
    /// <summary>已投递的帧数（诊断用）。</summary>
    long Frames { get; }
}

/// <summary>
/// 钩子可用的能力：已提交读取与询问任务的 memo（钩子与任务共享）。对应 TS <c>HookApi</c>。
/// </summary>
public interface IHookApi : IDocumentReader
{
    TaskId<object?> TaskId { get; }

    ConversationId ConversationId { get; }

    Task<object?> MemoAsync(string name, Context context);

    Task<object> MemoAsync(string name, object candidate, Context context);
}

/// <summary>钩子回调。对应 TS <c>HookResult&lt;T&gt;</code> 的函数形状。</summary>
public delegate Task<T?> HookHandler<TArgs, T>(TArgs args, IHookApi api, Context context);

/// <summary>内建生成任务的钩子。对应 TS <c>GenerationHooks</c>。</summary>
public interface IGenerationHooks
{
    /// <summary>每次请求尝试之前（含恢复）；结果只用于该请求。</summary>
    Task<(IReadOnlyList<ChatMessage> Messages, bool Used)?> BeforeRequest(
        IReadOnlyList<ChatMessage> messages, IHookApi api, Context context);

    /// <summary>每个终态 provider 消息，分类之前。</summary>
    Task AfterResponse(AssistantMessage message, IHookApi api, Context context);

    /// <summary>最终答案；首个 continue 追加用户消息并继续运行。</summary>
    Task<(IReadOnlyList<ContentBlock> Continue, bool ContinueRun)?> OnYield(
        AssistantMessage answer, IHookApi api, Context context);

    /// <summary>一轮每个工具都终态之后；results 是调用顺序中的轮结果条目。</summary>
    Task AfterTools(EntryId assistant, IReadOnlyList<EntryId> results, IHookApi api, Context context);
}

/// <summary>内建工具任务的钩子。对应 TS <c>ToolHooks</c>。</summary>
public interface IToolHooks
{
    /// <summary>intent 之前；首个 block 胜出，否则 arguments 替换调用参数。抛出即阻塞。</summary>
    Task<ToolHookDecision?> BeforeTool(ToolCallContent call, IHookApi api, Context context);

    /// <summary>执行之后、结果条目之前；替换结果。</summary>
    Task<ToolExecutionResult?> AfterTool(
        ToolCallContent call, ToolExecutionResult result, IHookApi api, Context context);
}

/// <summary>beforeTool 钩子的决策载荷。对应 TS <c>{ arguments?, block? }</c>。</summary>
public sealed record ToolHookDecision
{
    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    public string? Block { get; init; }
}

/// <summary>内建压缩任务的钩子。对应 TS <c>CompactionHooks</c>。</summary>
public interface ICompactionHooks
{
    /// <summary>
    /// 范围选择之后、摘要之前；首个决策胜出。entries 是摘要替换的活动条目（head 标记在前），
    /// messages 是其模型上下文（摘要器来源）；firstKept 是逐字保留的首个条目。
    /// </summary>
    Task<CompactionHookDecision?> BeforeCompact(CompactionInput compaction, IHookApi api, Context context);
}

/// <summary>beforeCompact 的输入。对应 TS 匿名形状。</summary>
public sealed record CompactionInput
{
    public required CompactionReason Reason { get; init; }

    public required IReadOnlyList<EntryRecord> Entries { get; init; }

    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public required EntryId FirstKept { get; init; }

    public string? Instructions { get; init; }
}

/// <summary>beforeCompact 的决策：拒绝或给出摘要。对应 TS <c>{ decline: true } | { summary }</c>。</summary>
public sealed record CompactionHookDecision
{
    public bool Decline { get; init; }

    public string? Summary { get; init; }
}
