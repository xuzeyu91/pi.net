using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 把已提交文档读取 / 观察面整体转发到一个源（IDocumentReader + IDocumentObserver）的可复用基类。
/// 对应 TS 的结构性转发（api 对象展开 runtime 的 snapshot / snapshotAsOf / watchDoc）；
/// C# 接口无继承转发，以显式基类表达。
/// </summary>
internal abstract class RuntimeDocumentFaces(IDocumentReader reader, IDocumentObserver observer)
    : IDocumentReader, IDocumentObserver
{
    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, conversationId, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, taskId, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, key, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, conversationId, key, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsync(token, taskId, key, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, conversationId, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, taskId, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, key, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, conversationId, key, context);

    public Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.DocumentStateAsync(token, taskId, key, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsOfAsync(token, conversationId, at, context);

    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => reader.SnapshotAsOfAsync(token, conversationId, key, at, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, conversationId, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, taskId, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, key, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, conversationId, key, context);

    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => observer.WatchDocAsync(token, taskId, key, context);
}
