using Pi.Chord.Context;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 把已提交文档读取 / 观察面整体转发到一个源（IDocumentReader + IDocumentObserver）的可复用基类。
/// 对应 TS 的结构性转发（api 对象展开 runtime 的 snapshot / snapshotAsOf / watchDoc）；
/// C# 接口无继承转发，以显式基类表达。构造可传入门控回调（如调用已结束检查），
/// 在每次读取 / 观察前执行。读取方法按源转发；观察方法可被派生类重写以登记 watch 生命周期。
/// </summary>
internal abstract class RuntimeDocumentFaces : IDocumentReader, IDocumentObserver
{
    private readonly IDocumentReader _reader;
    private readonly IDocumentObserver _observer;
    private readonly Action? _gate;

    protected RuntimeDocumentFaces(IDocumentReader reader, IDocumentObserver observer, Action? gate = null)
        => (_reader, _observer, _gate) = (reader, observer, gate);

    private void Gate() => _gate?.Invoke();

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, conversationId, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, taskId, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, key, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, conversationId, key, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsync(token, taskId, key, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, conversationId, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, taskId, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, key, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, conversationId, key, context);
    }

    public virtual Task<DocumentState<T>?> DocumentStateAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.DocumentStateAsync(token, taskId, key, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsOfAsync(token, conversationId, at, context);
    }

    public virtual Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _reader.SnapshotAsOfAsync(token, conversationId, key, at, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, conversationId, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, taskId, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, key, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, conversationId, key, context);
    }

    public virtual Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        Gate();
        return _observer.WatchDocAsync(token, taskId, key, context);
    }
}
