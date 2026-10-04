using Pi.Agent.Types;
using Pi.Ai.Types;

namespace Pi.Agent;

/// <summary>
/// 有状态 agent 运行时：低层 agent 循环的封装。对应 TS <c>agent.ts</c>。
/// 持有当前 transcript、发出生命周期事件、执行工具，并提供 steering 与
/// follow-up 消息的排队 API。
/// </summary>
public sealed class Agent
{
    private readonly List<ChatMessage> _messages = [];
    private readonly List<AgentTool> _tools = [];
    private readonly HashSet<string> _pendingToolCalls = [];
    private readonly List<Func<AgentEvent, CancellationToken, Task>> _listeners = [];

    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;
    private ActiveRun? _activeRun;
    private Model _model = new("unknown", "unknown", "unknown", "unknown");
    private bool _isStreaming;
    private AssistantMessage? _streamingMessage;
    private string? _errorMessage;

    /// <param name="options">构造选项（对应 TS <c>AgentOptions</c>）。</param>
    public Agent(AgentOptions options)
    {
        Model = options.Model ?? new Model("unknown", "unknown", "unknown", "unknown");
        ThinkingLevel = options.ThinkingLevel ?? ThinkingLevel.Off;
        Tools = options.Tools ?? [];
        _steeringQueue = new PendingMessageQueue(options.SteeringMode ?? QueueMode.OneAtATime);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode ?? QueueMode.OneAtATime);
        ConvertToLlm = options.ConvertToLlm ?? DefaultConvertToLlm;
        TransformContext = options.TransformContext;
        StreamFunction = options.StreamFn;
        GetApiKey = options.GetApiKey;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        FinishTurn = options.FinishTurn;
        PrepareRequest = options.PrepareRequest;
        PrepareNextTurn = options.PrepareNextTurn;
        ToolExecution = options.ToolExecution ?? ToolExecutionMode.Parallel;
        SessionId = options.SessionId;

        // 初始 prompt / tools 转为首条 system 消息（transcript 未自带 system 时）。
        var systemPrompt = options.SystemPrompt;
        if (_messages.Count == 0 && (systemPrompt is not null || _tools.Count > 0))
            _messages.Add(new SystemMessage(Content: systemPrompt,
                Tools: _tools.Select(t => t.Definition).ToList()));
    }

    /// <summary>默认消息转换：保留全部四种标准角色消息（对齐 TS defaultConvertToLlm）。</summary>
    public static Task<IReadOnlyList<ChatMessage>> DefaultConvertToLlm(IReadOnlyList<ChatMessage> messages)
        => Task.FromResult(messages);

    // ---------- 状态（对应 TS AgentState） ----------

    /// <summary>当前对话 transcript（replay 基线：首条 system 消息保留 prompt 与工具声明）。</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>当前使用的模型。</summary>
    public Model Model { get => _model; set => _model = value; }

    /// <summary>后续回合的思考档位。</summary>
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;

    /// <summary>可执行工具集。赋值替换整个集合（差异由循环以 system 消息声明）。</summary>
    public IReadOnlyList<AgentTool> Tools { get => _tools; set { _tools.Clear(); _tools.AddRange(value); } }

    /// <summary>是否正在处理 prompt 或续跑（等待 agent_end 监听器完成后才复位）。</summary>
    public bool IsStreaming => _isStreaming;

    /// <summary>当前流式响应的部分助手消息（无则为 null）。</summary>
    public AssistantMessage? StreamingMessage => _streamingMessage;

    /// <summary>当前执行中的工具调用 id 集合。</summary>
    public IReadOnlyCollection<string> PendingToolCalls => _pendingToolCalls;

    /// <summary>最近一次失败/中止回合的错误消息。</summary>
    public string? ErrorMessage => _errorMessage;

    /// <summary>从 transcript 推导当前 system prompt（最后一条 system 消息的内容）。</summary>
    public string? SystemPrompt
    {
        get
        {
            foreach (var message in _messages)
                if (message is SystemMessage system)
                    return system.Content;
            return null;
        }
    }

    // ---------- 选项（构造后可变，对应 TS AgentOptions 公开字段） ----------

    /// <summary>LLM 调用边界的消息转换器。</summary>
    public Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>> ConvertToLlm { get; set; }

    /// <summary>转换前的上下文变换，可空。</summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext { get; set; }

    /// <summary>流式函数。</summary>
    public StreamFn StreamFunction { get; set; }

    /// <summary>按 provider 动态解析 API key。</summary>
    public Func<string, Task<string?>>? GetApiKey { get; set; }

    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; set; }

    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; set; }

    public Func<AgentTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurn { get; set; }

    public Func<AgentContext, Model, ThinkingLevel, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareRequest { get; set; }

    public Func<AgentTurnContext, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; set; }

    /// <summary>转发给 provider 的会话 id（缓存感知后端使用）。</summary>
    public string? SessionId { get; set; }

    /// <summary>多工具调用执行策略。</summary>
    public ToolExecutionMode ToolExecution { get; set; }

    // ---------- 订阅 ----------

    /// <summary>
    /// 订阅 agent 生命周期事件。监听器按订阅顺序 await，参与本次运行的收尾；
    /// 同时接收当前运行的中止 token。返回取消订阅的委托。
    /// </summary>
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, Task> listener)
    {
        _listeners.Add(listener);
        return new Unsubscriber(_listeners, listener);
    }

    private sealed class Unsubscriber(List<Func<AgentEvent, CancellationToken, Task>> listeners,
        Func<AgentEvent, CancellationToken, Task> listener) : IDisposable
    {
        public void Dispose() => listeners.Remove(listener);
    }

    // ---------- 队列 API ----------

    /// <summary>steering 队列的排空模式。</summary>
    public QueueMode SteeringMode { get => _steeringQueue.Mode; set => _steeringQueue.Mode = value; }

    /// <summary>follow-up 队列的排空模式。</summary>
    public QueueMode FollowUpMode { get => _followUpQueue.Mode; set => _followUpQueue.Mode = value; }

    /// <summary>排队一条消息：在当前助手回合结束后注入。</summary>
    public void Steer(ChatMessage message) => _steeringQueue.Enqueue(message);

    /// <summary>排队一条消息：仅在 agent 本应停止时运行。</summary>
    public void FollowUp(ChatMessage message) => _followUpQueue.Enqueue(message);

    /// <summary>清空全部 steering 排队消息。</summary>
    public void ClearSteeringQueue() => _steeringQueue.Clear();

    /// <summary>清空全部 follow-up 排队消息。</summary>
    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    /// <summary>清空两类队列。</summary>
    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    /// <summary>任一队列仍有待处理消息时返回 true。</summary>
    public bool HasQueuedMessages() => _steeringQueue.HasItems() || _followUpQueue.HasItems();

    /// <summary>预览下一回合将选取的消息（不消费）。</summary>
    public IReadOnlyList<ChatMessage> PeekQueuedMessages()
    {
        var steering = _steeringQueue.Peek();
        return steering.Count > 0 ? steering : _followUpQueue.Peek();
    }

    // ---------- 运行控制 ----------

    /// <summary>当前运行的中止 token（无运行时为 null）。</summary>
    public CancellationToken? Signal => _activeRun?.AbortController.Token;

    /// <summary>中止当前运行（若有）。</summary>
    public void Abort() => _activeRun?.AbortController.Cancel();

    /// <summary>等待当前运行与全部事件监听器结束（在 agent_end 监听器完成后解析）。</summary>
    public Task WaitForIdle() => _activeRun is null ? Task.CompletedTask : _activeRun.Promise.Task;

    /// <summary>
    /// 清空对话状态与队列，仅保留开头的 system 基线消息（prompt/工具声明）。
    /// </summary>
    public void Reset()
    {
        if (_activeRun is not null)
            throw new InvalidOperationException("Agent is already processing. Wait for completion before resetting.");

        var baseline = _messages.FirstOrDefault(m => m is SystemMessage);
        _messages.Clear();
        if (baseline is SystemMessage systemMessage) _messages.Add(systemMessage);
        _isStreaming = false;
        _streamingMessage = null;
        _pendingToolCalls.Clear();
        _errorMessage = null;
        ClearFollowUpQueue();
        ClearSteeringQueue();
    }

    /// <summary>以文本启动新 prompt。</summary>
    public Task Prompt(string input) => Prompt(new UserMessage([new TextContent(input)],
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    /// <summary>以一条或多条消息启动新 prompt。</summary>
    public async Task Prompt(ChatMessage message) => await Prompt([message]);

    /// <summary>以消息批次启动新 prompt。</summary>
    public async Task Prompt(IReadOnlyList<ChatMessage> messages)
    {
        if (_activeRun is not null)
            throw new InvalidOperationException(
                "Agent is already processing a prompt. Use Steer() or FollowUp() to queue messages, or wait for completion.");
        await RunPromptMessages(messages).ConfigureAwait(false);
    }

    /// <summary>
    /// 从当前 transcript 续跑。最后一条消息须为 user 或 toolResult；
    /// 最后一条为 assistant 时先排空 steering / follow-up 队列。
    /// </summary>
    public async Task Continue()
    {
        if (_activeRun is not null)
            throw new InvalidOperationException("Agent is already processing. Wait for completion before continuing.");

        var lastMessage = _messages.LastOrDefault();
        if (lastMessage is null || _messages.All(m => m is SystemMessage))
            throw new InvalidOperationException("No messages to continue from");

        if (lastMessage is AssistantMessage)
        {
            var queuedSteering = _steeringQueue.Drain();
            if (queuedSteering.Count > 0)
            {
                await RunPromptMessages(queuedSteering, skipInitialSteeringPoll: true).ConfigureAwait(false);
                return;
            }
            var queuedFollowUps = _followUpQueue.Drain();
            if (queuedFollowUps.Count > 0)
            {
                await RunPromptMessages(queuedFollowUps).ConfigureAwait(false);
                return;
            }
            throw new InvalidOperationException("Cannot continue from message role: assistant");
        }

        await RunContinuation().ConfigureAwait(false);
    }

    // ---------- 内部运行机制 ----------

    private sealed class ActiveRun
    {
        public required CancellationTokenSource AbortController { get; init; }
        public required TaskCompletionSource Promise { get; init; }
    }

    private async Task RunPromptMessages(IReadOnlyList<ChatMessage> messages, bool skipInitialSteeringPoll = false)
        => await RunWithLifecycle(async signal =>
        {
            await AgentLoop.RunWithPrompts(
                messages, CreateContextSnapshot(), CreateLoopConfig(skipInitialSteeringPoll),
                ProcessEvents, signal, StreamFunction).ConfigureAwait(false);
        }).ConfigureAwait(false);

    private async Task RunContinuation()
        => await RunWithLifecycle(async signal =>
        {
            await AgentLoop.RunContinueCore(
                CreateContextSnapshot(), CreateLoopConfig(),
                ProcessEvents, signal, StreamFunction).ConfigureAwait(false);
        }).ConfigureAwait(false);

    private AgentContext CreateContextSnapshot()
        => new() { Messages = [.. _messages], Tools = _tools.ToList() };

    private AgentLoopConfig CreateLoopConfig(bool skipInitialSteeringPoll = false)
    {
        var localSkipInitialSteeringPoll = skipInitialSteeringPoll;
        return new AgentLoopConfig
        {
            Model = _model,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            GetApiKey = GetApiKey,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            FinishTurn = FinishTurn,
            PrepareRequest = PrepareRequest,
            PrepareNextTurn = PrepareNextTurn,
            ToolExecution = ToolExecution,
            StreamOptions = new SimpleStreamOptions(SessionId: SessionId),
            GetSteeringMessages = () =>
            {
                if (localSkipInitialSteeringPoll)
                {
                    localSkipInitialSteeringPoll = false;
                    return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
                }
                return Task.FromResult(_steeringQueue.Drain());
            },
            GetFollowUpMessages = () => Task.FromResult(_followUpQueue.Drain()),
        };
    }

    private async Task RunWithLifecycle(Func<CancellationToken, Task> executor)
    {
        if (_activeRun is not null)
            throw new InvalidOperationException("Agent is already processing.");

        var abortController = new CancellationTokenSource();
        var promise = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeRun = new ActiveRun { AbortController = abortController, Promise = promise };

        _isStreaming = true;
        _streamingMessage = null;
        _errorMessage = null;

        try
        {
            await executor(abortController.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await HandleRunFailure(error, abortController.IsCancellationRequested).ConfigureAwait(false);
        }
        finally
        {
            FinishRun();
        }
    }

    /// <summary>循环异常时的降级：合成一条 error/aborted 助手消息并补发缺失的生命周期事件。</summary>
    private async Task HandleRunFailure(Exception error, bool aborted)
    {
        var failureMessage = new AssistantMessage(
            [new TextContent("")],
            aborted ? StopReason.Aborted : StopReason.Error,
            ErrorMessage: error.Message,
            UsageStats: new Usage(0, 0),
            Model: _model.Id,
            Api: _model.Api,
            Provider: _model.Provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await ProcessEvents(new AgentEvent.MessageStart(failureMessage)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.MessageEnd(failureMessage)).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.TurnEnd(failureMessage, [])).ConfigureAwait(false);
        await ProcessEvents(new AgentEvent.AgentEnd([failureMessage])).ConfigureAwait(false);
    }

    private void FinishRun()
    {
        _isStreaming = false;
        _streamingMessage = null;
        _pendingToolCalls.Clear();
        _activeRun?.Promise.TrySetResult();
        _activeRun = null;
    }

    /// <summary>
    /// 为循环事件归约内部状态，然后按订阅顺序 await 全部监听器。
    /// <c>agent_end</c> 只表示不再有循环事件；空闲状态要等监听器全部收尾后由 FinishRun 置位。
    /// </summary>
    private async Task ProcessEvents(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentEvent.MessageStart start:
                if (start.Message is AssistantMessage assistant) _streamingMessage = assistant;
                break;

            case AgentEvent.MessageUpdate update:
                _streamingMessage = update.Message;
                break;

            case AgentEvent.MessageEnd end:
                _streamingMessage = null;
                _messages.Add(end.Message);
                break;

            case AgentEvent.ToolExecutionStart toolStart:
                _pendingToolCalls.Add(toolStart.ToolCallId);
                break;

            case AgentEvent.ToolExecutionEnd toolEnd:
                _pendingToolCalls.Remove(toolEnd.ToolCallId);
                break;

            case AgentEvent.TurnEnd turnEnd:
                if (turnEnd.Message.ErrorMessage is not null)
                    _errorMessage = turnEnd.Message.ErrorMessage;
                break;

            case AgentEvent.AgentEnd:
                _streamingMessage = null;
                break;
        }

        if (_activeRun is null)
            throw new InvalidOperationException("Agent listener invoked outside active run");
        var signal = _activeRun.AbortController.Token;
        foreach (var listener in _listeners)
            await listener(@event, signal).ConfigureAwait(false);
    }
}

/// <summary>待处理消息队列。对应 TS <c>PendingMessageQueue</c>：
/// all 模式一次排空全部，one-at-a-time 每次只投递最旧的一条。</summary>
public sealed class PendingMessageQueue(QueueMode mode)
{
    private readonly List<ChatMessage> _messages = [];

    /// <summary>排空模式。</summary>
    public QueueMode Mode { get; set; } = mode;

    /// <summary>入队一条消息。</summary>
    public void Enqueue(ChatMessage message) => _messages.Add(message);

    /// <summary>队列非空。</summary>
    public bool HasItems() => _messages.Count > 0;

    /// <summary>预览将被选取的消息（不消费）。</summary>
    public IReadOnlyList<ChatMessage> Peek()
    {
        if (Mode == QueueMode.All) return [.. _messages];
        return _messages.Count > 0 ? [_messages[0]] : [];
    }

    /// <summary>排空并移除被选取的消息。</summary>
    public IReadOnlyList<ChatMessage> Drain()
    {
        var drained = Peek();
        _messages.RemoveRange(0, drained.Count);
        return drained;
    }

    /// <summary>清空队列。</summary>
    public void Clear() => _messages.Clear();
}
