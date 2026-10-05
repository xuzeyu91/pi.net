using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Types;

namespace Pi.Durable.Storage;

/// <summary>
/// 一次条目查找的结果：条目记录与持久化它的提交序列。对应 TS
/// <c>Storage.entry()</c> 的返回 <c>{ entry, commitSeq }</c>。
/// </summary>
public sealed record StoredEntry
{
    public required EntryRecord Entry { get; init; }

    /// <summary>持久化该条目的提交序列（fork / as-of 读的选择点）。</summary>
    public required Seq CommitSeq { get; init; }
}

/// <summary>
/// 存储后端边界。对应 TS <c>types.ts</c> 的 <c>Storage</c>：
/// 实现必须原子提交批次、按 ID 排序返回记录、全局拥有 ID 命名空间、按对话祖先链
/// 判定条目可见性，并把文档化身暴露为可点查询（current / at-seq）的持久化状态。
/// <para>Session 信任存储接受语义合法的记录；存储强制原子性、全局 ID 所有权、
/// 不可变创建、文档记录一致性与脱钩值。Session 负责串行化提交。</para>
/// </summary>
public interface IStorage
{
    /// <summary>原子提交一个批次并返回其序列；解决后经此存储的读取即可见。</summary>
    Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default);

    /// <summary>从 Session 全局数字 ID 命名空间取一个新品牌 ID 候选。对应 TS <c>mintId&lt;I&gt;()</c>。</summary>
    Task<TId> MintIdAsync<TId>() where TId : struct;

    /// <summary>按精确 ID 读对话。</summary>
    Task<ConversationRecord?> GetConversationAsync(ConversationId id);

    /// <summary>按 ID 升序分页扫描对话。</summary>
    Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>按全局 ID 读条目及其提交序列。</summary>
    Task<StoredEntry?> GetEntryAsync(EntryId id);

    /// <summary>仅当条目经请求对话的祖先链可见时读它。对应 TS <c>entry(conversationId, id)</c>。</summary>
    Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id);

    /// <summary>
    /// 返回 <c>head</c> 不高于可选含界切点、且可见的最新条目；其 <c>head</c> 值即该范围的实际下界。
    /// 对应 TS <c>findLatestHeadMarker</c>。
    /// </summary>
    Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId);

    /// <summary>按 ID 升序分页扫描对话可见条目（最新优先由调用方反转）。</summary>
    Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>按 ID 读任务的最近完整记录。</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id);

    /// <summary>按 ID 升序分页扫描匹配全部过滤器的任务。</summary>
    Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>按 ID 读提交的最近完整记录。</summary>
    Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id);

    /// <summary>按 ID 升序分页扫描匹配全部过滤器的提交。</summary>
    Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>按对话作用域的宿主去重键查找提交。</summary>
    Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId);

    /// <summary>解析占据所选点上精确逻辑地址的化身。对应 TS <c>findDocument</c>。</summary>
    Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at);

    /// <summary>按 ID 物化所选点上的一个具体化身（不跟随其地址上的替换）。对应 TS <c>document</c>。</summary>
    Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at);

    /// <summary>按 ID 升序分页扫描所选点上存活于精确作用域内的化身。</summary>
    Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>释放后端资源；之后所有操作必须拒绝。</summary>
    Task CloseAsync();

    /// <summary>
    /// 注册提交发布监听器（不得阻塞调用方）。C# 存储扩展能力（TS <c>Storage</c> 无此方法；
    /// Session 的提交观察走自身的 commit 监听，不经过存储）。
    /// </summary>
    Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default);

    /// <summary>注销提交发布监听器。</summary>
    Task UnsubscribeAsync(Func<CommitPublication, Task> listener);

    /// <summary>
    /// 附加到一个已提交化身以获得可释放只读 Chord 状态。C# 存储扩展能力（TS <c>Storage</c> 无此方法）：
    /// 化身必须存在且未退役；可安装唯一异步监听器（绝不内联调用）。
    /// </summary>
    Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null);
}
