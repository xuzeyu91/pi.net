using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Stream;

/// <summary>
/// 基于通用 <see cref="EventStream{TEvent,TResult}"/> 的助手事件流实现。
/// 跟踪最近的 Partial 快照并转发终态。
/// </summary>
public sealed class AssistantMessageEventStream : Types.IAssistantMessageEventStream
{
    private readonly EventStream<AssistantMessageEvent, AssistantMessage> _inner;

    /// <summary>当前部分消息快照（每次事件后更新）。</summary>
    public AssistantMessage? Partial { get; private set; }

    public AssistantMessageEventStream()
        => _inner = new EventStream<AssistantMessageEvent, AssistantMessage>();

    /// <summary>推送事件并更新部分消息快照。</summary>
    public void Push(AssistantMessageEvent @event)
    {
        Partial = @event switch
        {
            AssistantMessageEvent.Start start => start.Partial,
            AssistantMessageEvent.TextDelta { Partial: var p } => p,
            AssistantMessageEvent.TextStart { Partial: var p } => p,
            AssistantMessageEvent.TextEnd { Partial: var p } => p,
            AssistantMessageEvent.ThinkingDelta { Partial: var p } => p,
            AssistantMessageEvent.ThinkingStart { Partial: var p } => p,
            AssistantMessageEvent.ThinkingEnd { Partial: var p } => p,
            AssistantMessageEvent.ToolCallStart { Partial: var p } => p,
            AssistantMessageEvent.ToolCallDelta { Partial: var p } => p,
            AssistantMessageEvent.ToolCallEnd { Partial: var p } => p,
            AssistantMessageEvent.Done done => done.Message,
            AssistantMessageEvent.Error error => error.Message,
            _ => Partial,
        };
        _inner.Push(@event);
    }

    /// <summary>写入终态消息并关闭。</summary>
    public void End(AssistantMessage finalMessage) => _inner.End(finalMessage);

    /// <inheritdoc />
    public Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default)
        => _inner.WaitForResultAsync(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => _inner.GetAsyncEnumerator(cancellationToken);
}
