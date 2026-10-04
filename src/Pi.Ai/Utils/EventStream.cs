using System.Threading.Channels;

namespace Pi.Ai.Utils;

/// <summary>
/// 可推送的事件流（带最终结果）。对应 TS <c>EventStream&lt;TEvent, TResult&gt;</c>：
/// 生产者 <see cref="Push"/> 事件、<see cref="End"/> 写入终值并关闭；
/// 消费者通过 <see cref="GetAsyncEnumerator"/> 逐事件迭代，结束后用
/// <see cref="WaitForResultAsync"/> 取回终值。
/// <para>基于 <see cref="Channel{T}"/> 实现单生产者多消费者语义：每个迭代器独立消费全部事件。</para>
/// </summary>
public sealed class EventStream<TEvent, TResult> : IAssistantEventStream<TEvent, TResult>
    where TEvent : notnull
{
    private readonly Channel<TEvent> _channel;
    private readonly Func<TResult>? _resultFactory;
    private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _ended;

    /// <summary>创建事件流。<paramref name="resultFactory"/> 可选：End 未带终值时使用。</summary>
    public EventStream(Func<TResult>? resultFactory = null)
    {
        _resultFactory = resultFactory;
        _channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>写入一个事件。流结束后再推送会被忽略（对齐 TS 版本的宽松语义）。</summary>
    public void Push(TEvent @event)
    {
        if (_ended) return;
        _channel.Writer.TryWrite(@event);
    }

    /// <summary>写入终值并关闭流。后续 Push 被忽略。</summary>
    public void End(TResult result)
    {
        if (_ended) return;
        _ended = true;
        _channel.Writer.TryComplete();
        _completion.TrySetResult(result);
    }

    /// <summary>以异常关闭流。</summary>
    public void Fail(Exception error)
    {
        if (_ended) return;
        _ended = true;
        _channel.Writer.TryComplete(error);
        _completion.TrySetException(error);
    }

    /// <summary>等待终值（流结束后完成）。</summary>
    public Task<TResult> WaitForResultAsync(CancellationToken cancellationToken = default)
        => _completion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var @event))
                yield return @event;
        }
    }
}

/// <summary>事件流只读契约（对应 TS 中 EventStream 作为 Iterable + result() 的角色）。</summary>
public interface IAssistantEventStream<TEvent, TResult> : IAsyncEnumerable<TEvent>
    where TEvent : notnull
{
    /// <summary>等待流终值。</summary>
    Task<TResult> WaitForResultAsync(CancellationToken cancellationToken = default);
}
