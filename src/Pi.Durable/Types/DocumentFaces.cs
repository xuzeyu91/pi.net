using Pi.Chord.Context;

namespace Pi.Durable.Types;

/// <summary>
/// 已提交文档的精确帧观察面。对应 TS durable <c>types.ts</c> 的 <c>DocumentObserver</c>：
/// 六种令牌形状的 <c>watchDoc</c> 重载。ISession 与 Harness 的工具/任务运行时都实现它。
/// </summary>
public interface IDocumentObserver
{
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
}

/// <summary>
/// 已提交文档的只读读取面。对应 TS <c>DocumentReader</c>：<c>snapshot</c> / <c>documentState</c> /
/// <c>snapshotAsOf</c> 的全部令牌重载。
/// </summary>
public interface IDocumentReader
{
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

    /// <summary>读 rewindable 对话文档在某条目提交处的历史快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;

    /// <summary>读 rewindable 对话文档族成员在某条目提交处的历史快照。</summary>
    Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>;
}
