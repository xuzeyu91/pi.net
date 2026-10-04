using Pi.Ai.Types;
using Pi.Agent.Types;

namespace Pi.Agent;

/// <summary>
/// <see cref="Agent"/> 的构造选项。对应 TS <c>AgentOptions</c>；
/// 全部钩子为可选项，缺省使用安全回退实现。
/// </summary>
public sealed class AgentOptions
{
    /// <summary>初始 system prompt（与 Tools 一起合成首条 system 消息）。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>初始模型。</summary>
    public Model? Model { get; init; }

    /// <summary>初始思考档位。</summary>
    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>初始可执行工具集。</summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }

    /// <summary>LLM 调用边界的消息转换器（缺省：四种标准角色原样通过）。</summary>
    public Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>>? ConvertToLlm { get; init; }

    /// <summary>转换前的上下文变换。</summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext { get; init; }

    /// <summary>流式函数（必填；也可用 DefaultStreamFn.Set 全局注册）。</summary>
    public required StreamFn StreamFn { get; init; }

    /// <summary>按 provider 动态解析 API key。</summary>
    public Func<string, Task<string?>>? GetApiKey { get; init; }

    /// <summary>工具调用前钩子。</summary>
    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

    /// <summary>工具执行后覆盖钩子。</summary>
    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; init; }

    /// <summary>回合完成后的调度钩子。</summary>
    public Func<AgentTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurn { get; init; }

    /// <summary>每次 provider 请求前的状态替换钩子。</summary>
    public Func<AgentContext, Model, ThinkingLevel, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareRequest { get; init; }

    /// <summary>turn_end 之后、下一回合开始前的准备钩子。</summary>
    public Func<AgentTurnContext, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    /// <summary>steering 队列排空模式（缺省 one-at-a-time，与 TS 一致）。</summary>
    public QueueMode? SteeringMode { get; init; }

    /// <summary>follow-up 队列排空模式（缺省 one-at-a-time，与 TS 一致）。</summary>
    public QueueMode? FollowUpMode { get; init; }

    /// <summary>会话 id（转发给缓存感知后端）。</summary>
    public string? SessionId { get; init; }

    /// <summary>多工具调用执行策略。</summary>
    public ToolExecutionMode? ToolExecution { get; init; }
}
