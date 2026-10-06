using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Storage;

namespace Pi.Durable.Types;

/// <summary>
/// 分配身份前提供的提交字段。对应 TS <c>SubmissionCreate = Omit&lt;SubmissionRecord, "id"&gt;</c>：
/// <see cref="Types.SubmissionRecord"/> 去掉 <c>id</c>（由 Session <c>createSubmission</c> 铸造）。
/// </summary>
public abstract record SubmissionCreate
{
    public required ConversationId ConversationId { get; init; }

    /// <summary>宿主提供的去重键（对话作用域）。</summary>
    public string? RequestId { get; init; }

    /// <summary>wire 判别符："input" / "write"。</summary>
    public abstract string Type { get; }

    /// <summary>用户输入提交的创建形状。</summary>
    public sealed record InputCreate : SubmissionCreate
    {
        public override string Type => "input";

        public required SubmissionStatus Status { get; init; }

        public EntryId? Entry { get; init; }

        public EntryId? Answer { get; init; }

        public string? Reason { get; init; }

        public object? Detail { get; init; }
    }

    /// <summary>被动条目写入提交的创建形状。</summary>
    public sealed record WriteCreate : SubmissionCreate
    {
        public override string Type => "write";

        public required SubmissionStatus Status { get; init; }

        public EntryId? Entry { get; init; }

        public string? Reason { get; init; }

        public object? Detail { get; init; }
    }
}

/// <summary>
/// 类型化条目内容（种类由 <see cref="Types.Entry{TData}"/> 令牌提供）。
/// 对应 TS <c>TypedEntryDraft&lt;D&gt; = Omit&lt;EntryDraft, "kind" | "data"&gt; &amp; { data?: D }</c>。
/// </summary>
public sealed record TypedEntryDraft<TData>
{
    public IReadOnlyList<Pi.Ai.Types.ChatMessage>? Model { get; init; }

    public TData? Data { get; init; }

    /// <summary><see cref="HeadIsSelf"/> 把活动上下文设到新分配的条目 ID。</summary>
    public EntryId? Head { get; init; }

    public bool HeadIsSelf { get; init; }

    public IReadOnlyList<ContextEdit>? Edits { get; init; }
}

/// <summary>
/// 可执行持久任务的定义（注册进 registry 供 Harness 运行）。对应 TS
/// <c>TaskDefinition&lt;I, S, R, H&gt;</c> 的会话阶段所需面（name / version / initial）。
/// <para>阶段处理器（<c>phases</c>）、<c>abort</c>、<c>migrate</c> 与 <c>hooks</c>
/// 由 Harness 阶段补充为派生定义；本阶段 createTask 只消费 <see cref="Initial"/>。</para>
/// </summary>
public sealed record TaskDefinition<TInput, TState, TResult>
{
    /// <summary>注册的任务种类，持久化在 <see cref="TaskRecord.Kind"/>。</summary>
    public required string Name { get; init; }

    /// <summary>定义版本，与活动输入和 checkpoint 一起持久化。</summary>
    public required int Version { get; init; }

    /// <summary>新建任务的第一个持久化 checkpoint。</summary>
    public required Func<TInput?, TState> Initial { get; init; }
}

/// <summary>类型化可执行任务。对应 TS <c>Task&lt;I, S, R, H&gt;</c>（<c>defineTask</c> 的产物）。</summary>
public sealed class DurableTask<TInput, TState, TResult>
{
    public required TaskDefinition<TInput, TState, TResult> Definition { get; init; }
}

/// <summary>一次提交绑定的默认值：事务的对话与条目归属任务。对应 TS <c>TransactionScope</c>。</summary>
public sealed record TransactionScope
{
    /// <summary><c>tx.createTask()</c> 缺省对话。</summary>
    public ConversationId? ConversationId { get; init; }

    /// <summary>追加条目的 <c>byTaskId</c> 归属。</summary>
    public TaskId<object?>? TaskId { get; init; }
}

/// <summary>
/// 一次 Session 提交回调的事务边界。对应 TS <c>Tx</code> 接口：表读在首次表写后拒绝
/// （<see cref="ReadAfterWrite"/>）；所有写暂存为原子批次；<c>doc</c> 返回草稿变更
/// （C# 无 JS Proxy，TS 的 <c>Draft&lt;T&gt;</c> 由 <see cref="Tracker{T}.Change"/> 承载，
/// 草稿经 <see cref="Tracker{T}.Change.Draft"/> 访问、变更用显式动词或整值替换）。
/// </summary>
public interface ITx
{
    // ─── 表读 ───────────────────────────────────────────────────────────────

    Task<ConversationRecord?> GetConversationAsync(ConversationId id);

    /// <summary>按全局 ID 读条目。</summary>
    Task<EntryRecord?> GetEntryAsync(EntryId id);

    /// <summary>按类型化令牌读条目；种类不符时为 undefined。</summary>
    Task<TypedEntry<TData>?> GetEntryAsync<TData>(Entry<TData> token, EntryId id);

    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id);

    Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>对话中携带 <c>head</c> 的最新可见条目。</summary>
    Task<EntryRecord?> LatestHeadMarkerAsync(ConversationId conversationId);

    Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>带对话作用域去重键的已提交提交。</summary>
    Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId);

    // ─── 表写 ───────────────────────────────────────────────────────────────

    /// <summary>以显式选择的所有权创建对话。</summary>
    Task<ConversationRecord> CreateConversationAsync(ConversationOwnership ownership);

    /// <summary>在一个具体可见条目处创建历史 fork。</summary>
    Task<ConversationRecord> ForkConversationAsync(
        ConversationId parentConversationId, EntryId at, ConversationOwnership ownership);

    /// <summary>追加条目；返回的记录为 Session 所有并可与提交监听器共享。</summary>
    Task<EntryRecord> AppendEntryAsync(ConversationId conversationId, EntryDraft value);

    /// <summary>按类型化令牌追加条目；令牌提供 <c>kind</c> 并收窄 <c>data</c>。</summary>
    Task<TypedEntry<TData>> AppendEntryAsync<TData>(
        Entry<TData> token, ConversationId conversationId, TypedEntryDraft<TData> value);

    /// <summary>创建持久任务并返回其 ID。</summary>
    Task<TaskId<TResult>> CreateTaskAsync<TInput, TState, TResult>(
        DurableTask<TInput, TState, TResult> task, TInput? input, TaskOptions options);

    /// <summary>
    /// 以新 ID 创建原始提交记录。不适用受理规则：无忙碌检查、无收件队列、无放置。
    /// 除非调用方自行实现受理，否则请使用对话句柄或 <c>Conversation.submit()</c>。
    /// </summary>
    Task<SubmissionRecord> CreateSubmissionAsync(SubmissionCreate create);

    /// <summary>
    /// 结算一个排队或已放置的提交；只有已放置的输入可被应答，已结算的提交保持不变。
    /// 在装配期间按本事务的该提交最新记录解析，因此首次表写之后仍可用。
    /// </summary>
    void SettleSubmission(SubmissionId id, SubmissionSettlement settlement);

    /// <summary>
    /// 把排队提交放置到 <paramref name="entry"/>：输入变 <c>placed</c>、写入变 <c>done</c>。
    /// 解析方式同 <see cref="SettleSubmission"/>。
    /// </summary>
    void PlaceSubmission(SubmissionId id, EntryId entry);

    // ─── 文档 ───────────────────────────────────────────────────────────────

    /// <summary>取得会话级文档草稿（不存在则以定义初值创建）。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(
        DocToken<T> token) where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得对话级文档草稿。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(
        DocToken<T> token, ConversationId conversationId) where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得任务级文档草稿。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T>(
        DocToken<T> token, TaskId<object?> taskId) where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得会话级文档族成员草稿（缺失时以 <paramref name="seed"/> 初始化）。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, TSeed? seed) where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得对话级文档族成员草稿。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, TSeed? seed)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得任务级文档族成员草稿。</summary>
    Task<Tracker<IReadOnlyDictionary<string, object?>>.Change> DocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, TSeed? seed)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役会话级文档。</summary>
    Task RetireDocAsync<T>(DocToken<T> token) where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役对话级文档。</summary>
    Task RetireDocAsync<T>(DocToken<T> token, ConversationId conversationId)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役任务级文档。</summary>
    Task RetireDocAsync<T>(DocToken<T> token, TaskId<object?> taskId)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役会话级文档族成员。</summary>
    Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, string key)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役对话级文档族成员。</summary>
    Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>退役任务级文档族成员。</summary>
    Task RetireDocAsync<T, TSeed>(DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key)
        where T : class, IReadOnlyDictionary<string, object?>;
}

/// <summary>
/// 一条变更线、其记录与被跟踪文档的所有者。对应 TS <c>Session</c>。
/// 只有已提交状态可观察；监听器在提交采纳之后异步运行。
/// </summary>
public interface ISession
{
    /// <summary>在 Session 变更线上运行一次原子事务。</summary>
    Task<TResult> CommitAsync<TResult>(Func<ITx, Task<TResult>> change, Context context);

    /// <summary>无返回值回调的重载（对应 TS 回调隐式返回 undefined）。</summary>
    Task CommitAsync(Func<ITx, Task> change, Context context);

    /// <summary>封闭受理、结算已受理提交，然后关闭存储。</summary>
    Task CloseAsync(Context context);

    /// <summary>
    /// 在采纳之后同步观察完整提交。监听器不得抛出、阻塞或调用 Session API。
    /// 返回退订 <see cref="IDisposable"/>。
    /// </summary>
    IDisposable SubscribeCommits(Action<CommitPublication, Context> listener);

    /// <summary>在 close 开始时同步观察。监听器不得抛出、阻塞或调用 Session API。</summary>
    IDisposable SubscribeClose(Action listener);

    /// <summary>读会话级文档的已提交快照；不存在为 undefined。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读对话级文档的已提交快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读任务级文档的已提交快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读会话级文档族成员的已提交快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读对话级文档族成员的已提交快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读任务级文档族成员的已提交快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交会话级文档化身的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交对话级文档化身的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交任务级文档化身的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交会话级文档族成员的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交对话级文档族成员的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>取得绑定到已提交任务级文档族成员的可释放只读状态。</summary>
    Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察会话级文档的精确帧 watch；文档不存在为 undefined。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察对话级文档的精确帧 watch。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察任务级文档的精确帧 watch。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察会话级文档族成员的精确帧 watch。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察对话级文档族成员的精确帧 watch。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>观察任务级文档族成员的精确帧 watch。</summary>
    Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读 rewindable 对话文档在某条目提交处的历史快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读 rewindable 对话文档族成员在某条目提交处的历史快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;
}
