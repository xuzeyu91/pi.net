using System.Text.Json.Serialization;
using Pi.Chord.Delta;

namespace Pi.Durable.Types;

/// <summary>
/// JSON 对象形状。对应 TS <c>JsonObject = { [key: string]: JsonValue }</c>（chord 的
/// 严格 JSON 值树）：C# 侧即 <see cref="IReadOnlyDictionary{TKey,TValue}"/>（值树用
/// <see cref="object?""/>：null / bool / double / string / <see cref="IReadOnlyList{T}"/> /
/// 本类型）。可变场景用 <see cref="Dictionary{TKey,TValue}"/>，公开 API 一律只读形状。
/// </summary>
public enum ConversationHistory
{
    /// <summary>只保留当前状态。对应 TS <c>history: "latest"</c>。</summary>
    Latest,

    /// <summary>保留可寻址历史（支持 as-of 读）。对应 TS <c>history: "rewindable"</c>。</summary>
    Rewindable,
}

/// <summary>分叉初始化来源。对应 TS <c>fork</c> 字段。</summary>
public enum ConversationFork
{
    /// <summary>从源当前状态初始化。</summary>
    Current,

    /// <summary>从定义的初始值初始化。</summary>
    Initial,

    /// <summary>在切点处初始化（仅 rewindable）。</summary>
    AsOf,
}

/// <summary>文档的所有权与生命周期。对应 TS <c>DocumentSemantics</c> 判别联合。</summary>
public abstract record DocumentSemantics
{
    private DocumentSemantics() { }

    /// <summary>会话级文档。</summary>
    public sealed record SessionScope : DocumentSemantics;

    /// <summary>对话级文档（latest 语义）；<paramref name="Fork"/> 为定义时分叉来源。</summary>
    public sealed record LatestConversationScope(ConversationFork Fork) : DocumentSemantics;

    /// <summary>对话级文档（rewindable 语义）；<paramref name="Fork"/> 为定义时分叉来源。</summary>
    public sealed record RewindableConversationScope(ConversationFork Fork) : DocumentSemantics;

    /// <summary>任务级文档。</summary>
    public sealed record TaskScope : DocumentSemantics;
}

/// <summary>传给文档 checkpoint 谓词的已存储重放状态。对应 TS <c>CheckpointInfo</c>。</summary>
public sealed record CheckpointInfo
{
    /// <summary>最新 base 之后已存储的 delta 数（不含正在评估的变更）。</summary>
    public required long DeltasSinceBase { get; init; }
}

/// <summary>单例文档定义。对应 TS <c>DocDefinition&lt;T&gt;</c>（<c>CommonDocDefinition</c> + 语义；<c>T extends JsonObject</c>）。</summary>
public sealed record DocDefinition<T> where T : class, IReadOnlyDictionary<string, object?>
{
    /// <summary>稳定持久化种类；公共协议的一部分。</summary>
    public required string Kind { get; init; }

    /// <summary>存储值形状的正整数版本。</summary>
    public required int Version { get; init; }

    /// <summary>初始值工厂。</summary>
    public required Func<T> Initial { get; init; }

    /// <summary>旧版本值迁移（读路径按需调用）。</summary>
    public Func<IReadOnlyDictionary<string, object?>, int, T>? Migrate { get; init; }

    /// <summary>返回 true 时把该普通变更存为完整 base 而非 delta。</summary>
    public Func<T, IReadOnlyList<DeltaOp>, CheckpointInfo, bool>? CheckpointWhen { get; init; }

    /// <summary>所有权与生命周期语义。</summary>
    public required DocumentSemantics Semantics { get; init; }
}

/// <summary>键控文档族定义。对应 TS <c>DocFamilyDefinition&lt;T, I&gt;</c>：<c>initial(seed)</c> 仅在成员缺失时运行。</summary>
public sealed record DocFamilyDefinition<T, TSeed> where T : class, IReadOnlyDictionary<string, object?>
{
    public required string Kind { get; init; }

    public required int Version { get; init; }

    /// <summary>族成员初始值工厂（按种子键）。</summary>
    public required Func<object?, T> Initial { get; init; }

    public Func<IReadOnlyDictionary<string, object?>, int, T>? Migrate { get; init; }

    public Func<T, IReadOnlyList<DeltaOp>, CheckpointInfo, bool>? CheckpointWhen { get; init; }

    public required DocumentSemantics Semantics { get; init; }
}

/// <summary>类型化单例文档令牌（显式传给类型化访问）。对应 TS <c>DocToken&lt;T, D&gt;</c>。</summary>
public sealed class DocToken<T> where T : class, IReadOnlyDictionary<string, object?>
{
    /// <summary>令牌携带的定义。</summary>
    public required DocDefinition<T> Definition { get; init; }
}

/// <summary>类型化文档族令牌。对应 TS <c>DocFamilyToken&lt;T, I, D&gt;</c>。</summary>
public sealed class DocFamilyToken<T, TSeed> where T : class, IReadOnlyDictionary<string, object?>
{
    public required DocFamilyDefinition<T, TSeed> Definition { get; init; }
}

/// <summary>文档逻辑作用域（存储记录的一部分）。对应 TS <c>DocumentRecord["scope"]</c> 判别联合。</summary>
public abstract record DocumentScope
{
    private DocumentScope() { }

    public sealed record SessionScope : DocumentScope;

    public sealed record ConversationScope(ConversationId ConversationId) : DocumentScope;

    public sealed record TaskScope(TaskId<object?> TaskId) : DocumentScope;
}

/// <summary>一个文档化身的持久化生命周期记录。对应 TS <c>DocumentRecord</c>。</summary>
public sealed record DocumentRecord
{
    /// <summary>唯一化身 ID；同一逻辑文档重建时不复用。</summary>
    public required DocumentId Id { get; init; }

    /// <summary>稳定文档定义种类。</summary>
    public required string Kind { get; init; }

    /// <summary>族成员键；单例文档缺省。</summary>
    public string? Key { get; init; }

    /// <summary>创建化身的提交（storage 戳记）。</summary>
    public required Seq CreatedAt { get; init; }

    /// <summary>退役化身的提交；当前化身缺省。</summary>
    public Seq? RetiredAt { get; init; }

    /// <summary>作用域。</summary>
    public required DocumentScope Scope { get; init; }

    /// <summary>仅对话作用域：历史语义。</summary>
    public ConversationHistory? History { get; init; }

    /// <summary>仅对话作用域：分叉语义。</summary>
    public ConversationFork? Fork { get; init; }
}

/// <summary>storage 创建新 <see cref="DocumentRecord"/> 时提供的字段（createdAt/retiredAt 由 storage 戳记）。</summary>
public sealed record DocumentCreate
{
    public required DocumentId Id { get; init; }

    public required string Kind { get; init; }

    public string? Key { get; init; }

    public required DocumentScope Scope { get; init; }

    public ConversationHistory? History { get; init; }

    public ConversationFork? Fork { get; init; }
}

/// <summary>单例或族成员的精确逻辑身份。对应 TS <c>DocumentAddress</c>。</summary>
public sealed record DocumentAddress
{
    public required string Kind { get; init; }

    public required DocumentScope Scope { get; init; }

    /// <summary>缺省选单例；存在选一个族成员。</summary>
    public string? Key { get; init; }
}

/// <summary>当前状态或某个历史提交序列（文档成员与内容读的选择点）。对应 TS <c>DocumentPoint</c>。</summary>
public abstract record DocumentPoint
{
    private DocumentPoint() { }

    /// <summary>当前状态。</summary>
    public sealed record Current : DocumentPoint;

    /// <summary>某个提交序列处的状态。</summary>
    public sealed record AtSeq(Seq Seq) : DocumentPoint;
}

/// <summary>一个精确作用域在某点的文档化身有序扫描。对应 TS <c>DocumentQuery</c>。</summary>
public sealed record DocumentQuery
{
    public required DocumentScope Scope { get; init; }

    public required DocumentPoint At { get; init; }

    public string? Kind { get; init; }
}

/// <summary>文档内容：完整 checkpoint 或 Chord 操作批（由所属 Session 选择）。对应 TS <c>DocumentContent</c>。</summary>
public abstract record DocumentContent
{
    private DocumentContent() { }

    /// <summary>完整 base。</summary>
    public sealed record Base(int Version, IReadOnlyDictionary<string, object?> Value) : DocumentContent;

    /// <summary>delta 操作批。</summary>
    public sealed record Delta(int Version, IReadOnlyList<DeltaOp> Ops) : DocumentContent;
}

/// <summary>无定义文档拷贝的精确持久化来源。对应 TS <c>DocumentCopySource</c>。</summary>
public sealed record DocumentCopySource
{
    public required DocumentId Id { get; init; }

    public required DocumentPoint At { get; init; }
}

/// <summary>某点脱钩的物化值与存储定义版本。对应 TS <c>StoredDocument</c>。</summary>
public sealed record StoredDocument
{
    public required DocumentRecord Record { get; init; }

    public required int Version { get; init; }

    public required IReadOnlyDictionary<string, object?> Value { get; init; }

    /// <summary>所选 base 之后重放以物化 <see cref="Value"/> 的 delta 数。</summary>
    public required long DeltasSinceBase { get; init; }
}

/// <summary>一次有序扫描结果与可选续扫状态。对应 TS <c>Page&lt;T, C&gt;</c>。</summary>
public sealed record Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>后端持有的 JSON 续扫状态；调用方只能原样回传给同一次扫描。</summary>
    public IReadOnlyDictionary<string, object?>? Next { get; init; }
}

/// <summary>后端 JSON 续扫游标。对应 TS <c>Cursor</c>。</summary>
public sealed record ConversationQuery
{
    public ConversationId? OwnerConversationId { get; init; }

    public TaskId<object?>? OwnerTaskId { get; init; }
}

/// <summary>一个对话 fork 感知历史的最新优先扫描的含界 ID 范围。对应 TS <c>EntryQuery</c>。</summary>
public sealed record EntryQuery
{
    public required ConversationId ConversationId { get; init; }

    /// <summary>可返回的最旧条目 ID。</summary>
    public EntryId? MinEntryId { get; init; }

    /// <summary>可返回的最新条目 ID。</summary>
    public EntryId? MaxEntryId { get; init; }
}

/// <summary>任务记录有序扫描的可选过滤器。对应 TS <c>TaskQuery</c>。</summary>
public sealed record TaskQuery
{
    public ConversationId? ConversationId { get; init; }

    public string? Kind { get; init; }

    public TaskStatus? Status { get; init; }

    public bool? AbortRequested { get; init; }

    public bool? Background { get; init; }
}

/// <summary>提交记录有序扫描的可选过滤器。对应 TS <c>SubmissionQuery</c>。</summary>
public sealed record SubmissionQuery
{
    public ConversationId? ConversationId { get; init; }

    public SubmissionStatus? Status { get; init; }
}

/// <summary>对话的不可变身份、历史祖先与任务所有权。对应 TS <c>ConversationRecord</c>。</summary>
public sealed record ConversationRecord
{
    public required ConversationId Id { get; init; }

    /// <summary>分叉来源与继承历史的含界父条目。</summary>
    public ConversationParent? Parent { get; init; }

    /// <summary>创建者边（归属、子树中止、子树空闲等待用）。</summary>
    public ConversationOwner? Owner { get; init; }
}

/// <summary>分叉来源边。对应 TS <c>ConversationRecord.parent</c>。</summary>
public sealed record ConversationParent
{
    public required ConversationId ConversationId { get; init; }

    public required EntryId At { get; init; }
}

/// <summary>创建者边。对应 TS <c>ConversationRecord.owner</c>。</summary>
public sealed record ConversationOwner
{
    public required ConversationId ConversationId { get; init; }

    public required TaskId<object?> TaskId { get; init; }
}

/// <summary>对一个可见条目的模型上下文贡献的不可变覆盖。对应 TS <c>ContextEdit</c>。</summary>
public abstract record ContextEdit
{
    private ContextEdit() { }

    /// <summary>省略目标条目的模型消息。</summary>
    public sealed record Omit(EntryId Target) : ContextEdit;

    /// <summary>用提供的消息替换目标条目的模型消息。</summary>
    public sealed record Replace(EntryId Target, IReadOnlyList<Pi.Ai.Types.ChatMessage> Messages) : ContextEdit;
}

/// <summary>不可变 transcript 事件：模型面载荷与应用面载荷分离。对应 TS <c>EntryRecord</c>。</summary>
public sealed record EntryRecord
{
    public required EntryId Id { get; init; }

    public required ConversationId ConversationId { get; init; }

    /// <summary>应用定义的条目判别符。</summary>
    public required string Kind { get; init; }

    /// <summary>贡献给模型上下文的消息；展示 / 簿记条目缺省。</summary>
    public IReadOnlyList<Pi.Ai.Types.ChatMessage>? Model { get; init; }

    /// <summary>视图 / 扩展 / 簿记逻辑消费的 JSON 载荷。</summary>
    public object? Data { get; init; }

    /// <summary>该条目选定的活动上下文的第一个条目。</summary>
    public EntryId? Head { get; init; }

    /// <summary>仅上下文层的早期可见条目覆盖。</summary>
    public IReadOnlyList<ContextEdit>? Edits { get; init; }

    /// <summary>追加该条目的任务（持久化工作产出时）。</summary>
    public TaskId<object?>? ByTaskId { get; init; }
}

/// <summary>Session 分配身份与任务归属前的条目内容。对应 TS <c>EntryDraft</c>。</summary>
public sealed record EntryDraft
{
    public required string Kind { get; init; }

    public IReadOnlyList<Pi.Ai.Types.ChatMessage>? Model { get; init; }

    public object? Data { get; init; }

    /// <summary><c>"self"</c> 把活动上下文设到新分配的条目 ID。</summary>
    public EntryId? Head { get; init; }

    public bool HeadIsSelf { get; init; }

    public IReadOnlyList<ContextEdit>? Edits { get; init; }
}

/// <summary>数据为 <typeparamref name="TData"/> 的类型化条目。对应 TS <c>TypedEntry&lt;D&gt;</c>。</summary>
public sealed record TypedEntry<TData>
{
    public required EntryId Id { get; init; }

    public required ConversationId ConversationId { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyList<Pi.Ai.Types.ChatMessage>? Model { get; init; }

    public TData? Data { get; init; }

    public EntryId? Head { get; init; }

    public IReadOnlyList<ContextEdit>? Edits { get; init; }

    public TaskId<object?>? ByTaskId { get; init; }
}

/// <summary>类型化条目种类 + 窄化守卫。对应 TS <c>Entry&lt;D&gt;</c>。</summary>
public sealed class Entry<TData>
{
    /// <summary>持久化条目判别符。</summary>
    public required string Kind { get; init; }

    /// <summary>记录是否属于该种类（按 <see cref="EntryRecord.Kind"/> 判别）。</summary>
    public bool Is(EntryRecord? entry) => entry is not null && entry.Kind == Kind;
}

/// <summary>提交生命周期状态。对应 TS <c>SubmissionRecord["status"]</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubmissionStatus>))]
public enum SubmissionStatus
{
    /// <summary>已受理但尚未进入 transcript。</summary>
    Queued,

    /// <summary>已入 transcript 且被活动运行持有。</summary>
    Placed,

    /// <summary>成功结束。</summary>
    Done,

    /// <summary>无法再被应答的终态。</summary>
    Unanswered,
}

/// <summary>一次受理的用户输入或被动条目写入的持久化生命周期。对应 TS <c>SubmissionRecord</c>。</summary>
public abstract record SubmissionRecord
{
    private SubmissionRecord() { }

    public required SubmissionId Id { get; init; }

    public required ConversationId ConversationId { get; init; }

    /// <summary>宿主提供的去重键（对话作用域）。</summary>
    public string? RequestId { get; init; }

    /// <summary>wire 判别符："input" / "write"。</summary>
    public abstract string Type { get; }

    /// <summary>用户输入提交。</summary>
    public sealed record InputRecord : SubmissionRecord
    {
        public override string Type => "input";

        public required SubmissionStatus Status { get; init; }

        public EntryId? Entry { get; init; }

        public EntryId? Answer { get; init; }

        public string? Reason { get; init; }

        public object? Detail { get; init; }
    }

    /// <summary>被动条目写入提交。</summary>
    public sealed record WriteRecord : SubmissionRecord
    {
        public override string Type => "write";

        public required SubmissionStatus Status { get; init; }

        public EntryId? Entry { get; init; }

        public string? Reason { get; init; }

        public object? Detail { get; init; }
    }
}

/// <summary>为提交暂存的终态；身份、类型与条目取自其当前记录。对应 TS <c>SubmissionSettlement</c>。</summary>
public abstract record SubmissionSettlement
{
    private SubmissionSettlement() { }

    public sealed record Done(EntryId Answer) : SubmissionSettlement;

    public sealed record Unanswered(string Reason, object? Detail = null) : SubmissionSettlement;
}

/// <summary>JSON 安全的错误快照（持久化时替代运行时 Error 对象）。对应 TS <c>TaskOutcomeError</c>。</summary>
public sealed record TaskOutcomeError
{
    public required string Message { get; init; }

    /// <summary>可选结构化诊断数据。</summary>
    public object? Detail { get; init; }
}

/// <summary>任务终态原因。对应 TS <c>TaskOutcome&lt;R&gt;["status"]</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskOutcomeStatus>))]
public enum TaskOutcomeStatus
{
    /// <summary>正常完成。</summary>
    Completed,

    /// <summary>任务实现显式提交的预期失败。</summary>
    Failed,

    /// <summary>任务中止协议处理的显式取消。</summary>
    Aborted,

    /// <summary>定义 / 迁移不可用而无法恢复。</summary>
    Orphaned,

    /// <summary>运行时检测到的契约失败（未捕获抛出 / 无持久化进展）。</summary>
    Faulted,
}

/// <summary>任务终态时记录的持久化原因与可选结果。对应 TS <c>TaskOutcome&lt;R&gt;</c>。</summary>
public sealed record TaskOutcome
{
    public required TaskOutcomeStatus Status { get; init; }

    public object? Result { get; init; }

    public TaskOutcomeError? Error { get; init; }

    public string? Reason { get; init; }
}

/// <summary>任务生命周期状态。对应 TS <c>TaskState&lt;S, R&gt;["status"]</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskStatus>))]
public enum TaskStatus
{
    /// <summary>可被调度。</summary>
    Pending,

    /// <summary>已被一个内存调用保留。</summary>
    Running,

    /// <summary>挂起直至 <see cref="TaskState.On"/> 中全部任务终态。</summary>
    Waiting,

    /// <summary>结果已定；无普通下属存活时转终态。</summary>
    Completing,

    /// <summary>永久落定的持久化结果回执。</summary>
    Terminal,
}

/// <summary>任务的完整持久化执行状态。对应 TS <c>TaskState&lt;S, R&gt;</c>。</summary>
public sealed record TaskState
{
    public required TaskStatus Status { get; init; }

    /// <summary>可恢复的完整持久化状态（completing / terminal 缺省）。</summary>
    public object? Checkpoint { get; init; }

    /// <summary>等待的任务（waiting）。</summary>
    public IReadOnlyList<TaskId<object?>>? On { get; init; }

    /// <summary>等待连接策略（waiting）。</summary>
    public JoinPolicy? Policy { get; init; }

    /// <summary>已定结果（completing / terminal）。</summary>
    public TaskOutcome? Outcome { get; init; }
}

/// <summary>等待任务对待其等待任务的方式。对应 TS <c>JoinPolicy</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JoinPolicy>))]
public enum JoinPolicy
{
    /// <summary>任一失败即快速失败。</summary>
    FailFast,

    /// <summary>等待全部落定。</summary>
    AllSettled,
}

/// <summary>任务的所有者：其对话（顶层任务）或同对话的另一任务（子任务）。对应 TS <c>TaskOwnership</c>。</summary>
public abstract record TaskOwnership
{
    private TaskOwnership() { }

    public sealed record ConversationOwner : TaskOwnership;

    public sealed record TaskOwner(TaskId<object?> TaskId) : TaskOwnership;
}

/// <summary>创建对话时显式选择的所有权。对应 TS <c>ConversationOwnership</c>。</summary>
public abstract record ConversationOwnership
{
    private ConversationOwnership() { }

    public sealed record Ownerless : ConversationOwnership;

    public sealed record TaskOwned(TaskId<object?> TaskId) : ConversationOwnership;
}

/// <summary>持久任务的创建选项。对应 TS <c>TaskOptions</c>。</summary>
public sealed record TaskOptions
{
    /// <summary>必填：任务总是命名其所有者。</summary>
    public required TaskOwnership Ownership { get; init; }

    /// <summary>缺省为所有者任务的对话或事务绑定对话。</summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>仅对话所有任务：排除在普通空闲等待、对话中止与级联之外。</summary>
    public bool Background { get; init; }
}

/// <summary>一个持久任务状态机的完整替换记录。对应 TS <c>TaskRecord&lt;I, S, R&gt;</c>。</summary>
public sealed record TaskRecord
{
    public required TaskId<object?> Id { get; init; }

    public required ConversationId ConversationId { get; init; }

    /// <summary>注册的任务定义名。</summary>
    public required string Kind { get; init; }

    /// <summary>用于迁移活动输入与 checkpoint 的定义版本。</summary>
    public required int Version { get; init; }

    /// <summary>任务存活或终态期间保留的原始输入。</summary>
    public object? Input { get; init; }

    /// <summary>子任务的所有任务；对话所有的任务缺省。不可变。</summary>
    public TaskId<object?>? Owner { get; init; }

    /// <summary>该对话所有任务是否排除在普通空闲等待、对话中止与级联之外。</summary>
    public required bool Background { get; init; }

    /// <summary>run 模式进展提交前检查的持久化中止标记。</summary>
    public required bool AbortRequested { get; init; }

    /// <summary>当前状态。</summary>
    public required TaskState State { get; init; }

    /// <summary>任务可运行期间保留的小型首写必胜值。</summary>
    public IReadOnlyDictionary<string, object?>? Memos { get; init; }
}

/// <summary>一次原子存储提交中的一条记录或文档变更。对应 TS <c>StorageWrite</c>。</summary>
public abstract record StorageWrite
{
    private StorageWrite() { }

    public sealed record Conversation(ConversationRecord Value) : StorageWrite;

    public sealed record Entry(EntryRecord Value) : StorageWrite;

    public sealed record Task(TaskRecord Value) : StorageWrite;

    public sealed record Submission(SubmissionRecord Value) : StorageWrite;

    public sealed record DocumentCreateWrite(DocumentCreate Record, DocumentContent.Base Content) : StorageWrite;

    public sealed record DocumentCopyWrite(DocumentCreate Record, DocumentCopySource Source) : StorageWrite;

    public sealed record DocumentChange(DocumentId Id, DocumentContent Content) : StorageWrite;

    public sealed record DocumentRetire(DocumentId Id) : StorageWrite;
}

/// <summary>一次成功 Session 提交的全部不可变变更；变更顺序不定。对应 TS <c>CommitPublication</c>。</summary>
public sealed record CommitPublication
{
    public required Seq Seq { get; init; }

    public required IReadOnlyList<CommitChange> Changes { get; init; }
}

/// <summary>一个文档化身的一次提交变更。对应 TS <c>DocumentCommitChange</c>。</summary>
public abstract record DocumentCommitChange
{
    private DocumentCommitChange() { }

    /// <summary>普通文档变更。</summary>
    public sealed record Document(
        DocumentRecord Record,
        ConversationId? ConversationId,
        int? Version,
        IReadOnlyDictionary<string, object?>? Value,
        IReadOnlyList<DeltaOp> Ops) : DocumentCommitChange;

    /// <summary>无定义子初始化；消费者经状态或 watch 获取水合。</summary>
    public sealed record Copy(
        DocumentRecord Record,
        ConversationId ConversationId,
        DocumentCopySource Source) : DocumentCommitChange;
}

/// <summary>无另一份发布副本的完整表记录提交。对应 TS <c>TableCommitChange</c>。</summary>
public abstract record TableCommitChange
{
    private TableCommitChange() { }

    public sealed record Conversation(ConversationRecord Value) : TableCommitChange;

    public sealed record Entry(EntryRecord Value) : TableCommitChange;

    public sealed record Task(TaskRecord Value) : TableCommitChange;

    public sealed record Submission(SubmissionRecord Value) : TableCommitChange;
}

/// <summary>一次提交中的一条变更。对应 TS <c>CommitChange</c>。</summary>
public abstract record CommitChange
{
    private CommitChange() { }

    public sealed record ConversationTable(ConversationRecord Value) : CommitChange;

    public sealed record EntryTable(EntryRecord Value) : CommitChange;

    public sealed record TaskTable(TaskRecord Value) : CommitChange;

    public sealed record SubmissionTable(SubmissionRecord Value) : CommitChange;

    public sealed record DocumentChanged(DocumentCommitChange.Document Change) : CommitChange;

    public sealed record DocumentCopied(DocumentCommitChange.Copy Change) : CommitChange;
}

/// <summary>一个文档 watch 的终态结果。对应 TS <c>WatchEnd</c>。</summary>
public abstract record WatchEnd
{
    private WatchEnd() { }

    public sealed record Stopped : WatchEnd;

    public sealed record Cancelled : WatchEnd;

    public sealed record SessionClosed : WatchEnd;

    public sealed record Retired : WatchEnd;

    public sealed record ListenerError(Exception Error) : WatchEnd;
}

/// <summary>不可变值的精确帧观察句柄（有待发上限的序列化观察）。对应 TS <c>WatchHandle&lt;T&gt;</c>。</summary>
public interface IWatchHandle<T>
{
    /// <summary>获取时的修订；最新交付的不可变修订。</summary>
    T Value { get; }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    void Start(Func<T, IReadOnlyList<DeltaOp>, Pi.Chord.Context.Context, Task> listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    Task<WatchEnd> Stop();

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>文档 watch（值可空：化身可能尚未物化）。对应 TS <c>DocumentWatch&lt;T&gt;</c>。</summary>
public interface IDocumentWatch<T> : IWatchHandle<IReadOnlyDictionary<string, object?>?> where T : class;

/// <summary>
/// 绑定到一个已提交文档化身的可释放只读 Chord 状态。对应 TS
/// <c>DocumentState&lt;T&gt; = AttachedReplicatedState&lt;Readonly&lt;T&gt; | null&gt;</c>。
/// </summary>
public sealed class DocumentState<T> : IDisposable where T : class
{
    private readonly Pi.Chord.Services.AttachedReplicatedState<IReadOnlyDictionary<string, object?>?> _inner;

    internal DocumentState(Pi.Chord.Services.AttachedReplicatedState<IReadOnlyDictionary<string, object?>?> inner)
        => _inner = inner;

    /// <summary>当前值快照（未物化时为 null）。</summary>
    public IReadOnlyDictionary<string, object?>? Value => _inner.Value;

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// 绑定到一个已提交文档化身的精确帧 watch。对应 TS
/// <c>DocumentWatch&lt;T&gt; = WatchHandle&lt;Readonly&lt;T&gt; | null&gt;</c>。
/// C# 无结构化类型，TS 的接口与具体类之分由本包装承载（Cancel / ObserveCancellation
/// 位于具体类型，对齐 TS <c>CommittedWatch</c> 的公开面）。
/// </summary>
public sealed class DocumentWatch<T> : IDocumentWatch<T> where T : class
{
    private readonly Session.CommittedWatch<IReadOnlyDictionary<string, object?>?> _inner;

    internal DocumentWatch(Session.CommittedWatch<IReadOnlyDictionary<string, object?>?> inner)
        => _inner = inner;

    /// <summary>获取时的修订；最新交付的不可变修订。</summary>
    public IReadOnlyDictionary<string, object?>? Value => _inner.Value;

    /// <inheritdoc />
    public void Start(Func<IReadOnlyDictionary<string, object?>?, IReadOnlyList<Pi.Chord.Delta.DeltaOp>,
        Pi.Chord.Context.Context, Task> listener) => _inner.Start(listener);

    /// <inheritdoc />
    public Task<WatchEnd> Stop() => _inner.Stop();

    /// <inheritdoc />
    public Task<WatchEnd> Closed => _inner.Closed;

    /// <summary>以 cancelled 终态终止。对应 TS <c>CommittedWatch.cancel()</c>。</summary>
    public void Cancel() => _inner.Cancel();

    /// <summary>安装取消信号观察。对应 TS <c>CommittedWatch.observeCancellation()</c>。</summary>
    public void ObserveCancellation(CancellationToken signal) => _inner.ObserveCancellation(signal);
}
