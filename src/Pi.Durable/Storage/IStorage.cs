using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Types;

namespace Pi.Durable.Storage;

/// <summary>
/// 存储后端边界。对应 TS <c>storage/storage.ts</c> 的 <c>Storage</c>：
/// 实现必须原子提交批次、按 ID 排序返回记录、把文档化身暴露为可附加的只读 Chord 状态。
/// </summary>
public interface IStorage
{
    /// <summary>
    /// 原子提交一个批次。对应 TS <c>commit(writes, onCommit)</c>：
    /// <paramref name="onCommit"/> 在序列分配后、返回前同步运行（可再提交）；
    /// 若其抛出，实现必须撤销自身变更并向上传播错误。
    /// </summary>
    Task<Seq> CommitAsync(
        IReadOnlyList<StorageWrite> writes,
        Func<IReadOnlyList<StorageWrite>, Task>? onCommit = null,
        CancellationToken signal = default);

    /// <summary>注册提交发布监听器（不得阻塞调用方）。</summary>
    Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default);

    /// <summary>注销提交发布监听器。</summary>
    Task UnsubscribeAsync(Func<CommitPublication, Task> listener);

    /// <summary>关闭存储并释放资源。</summary>
    Task CloseAsync();

    /// <summary>按 ID 读对话。</summary>
    Task<ConversationRecord?> GetConversationAsync(ConversationId id);

    /// <summary>按 ID 升序扫描对话。</summary>
    Task<IReadOnlyList<ConversationRecord>> ScanConversationsAsync(ConversationQuery? query = null);

    /// <summary>按 ID 读条目。</summary>
    Task<EntryRecord?> GetEntryAsync(EntryId id);

    /// <summary>按 ID 升序扫描条目。</summary>
    Task<IReadOnlyList<EntryRecord>> ScanEntriesAsync(EntryQuery query);

    /// <summary>按 ID 读任务。</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId<object?> id);

    /// <summary>按 ID 升序扫描任务。</summary>
    Task<IReadOnlyList<TaskRecord>> ScanTasksAsync(TaskQuery? query = null);

    /// <summary>按 ID 读提交。</summary>
    Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id);

    /// <summary>按 ID 升序扫描提交。</summary>
    Task<IReadOnlyList<SubmissionRecord>> ScanSubmissionsAsync(SubmissionQuery? query = null);

    /// <summary>按 ID 升序扫描文档化身；cursor 为后端持有的 JSON 续扫状态。</summary>
    Task<Page<StoredDocument>> ScanDocumentsAsync(
        DocumentQuery query, IReadOnlyDictionary<string, object?>? cursor = null);

    /// <summary>
    /// 附加到一个已提交化身以获得可释放只读 Chord 状态。对应 TS <c>attachDocument</c>：
    /// 化身必须已存在；可安装唯一异步监听器（绝不内联调用）。
    /// </summary>
    Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null);
}
