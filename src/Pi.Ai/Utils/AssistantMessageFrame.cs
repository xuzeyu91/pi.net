using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// 紧凑、可回放的助手消息进度帧。对应 TS <c>AssistantMessageFrame</c>
/// （utils/assistant-message-frame.ts）：**刻意不含终态结算**，终态须另行持久化。
/// </summary>
public abstract record AssistantMessageFrame
{
    private AssistantMessageFrame() { }

    public sealed record Start(AssistantMessage Partial) : AssistantMessageFrame;

    public sealed record TextStart(int ContentIndex, TextContent Content) : AssistantMessageFrame;

    public sealed record TextDelta(int ContentIndex, string Delta) : AssistantMessageFrame;

    public sealed record TextEnd(int ContentIndex, string Content, string? TextSignature = null)
        : AssistantMessageFrame;

    public sealed record ThinkingStart(int ContentIndex, ThinkingContent Content) : AssistantMessageFrame;

    public sealed record ThinkingDelta(int ContentIndex, string Delta) : AssistantMessageFrame;

    public sealed record ThinkingEnd(int ContentIndex, string Content,
        string? ThinkingSignature = null, bool? Redacted = null) : AssistantMessageFrame;

    public sealed record ToolCallStart(int ContentIndex, ToolCallContent ToolCall) : AssistantMessageFrame;

    public sealed record ToolCallCheckpoint(int ContentIndex, string Json) : AssistantMessageFrame;

    public sealed record ToolCallDelta(int ContentIndex, string Delta) : AssistantMessageFrame;

    public sealed record ToolCallEnd(int ContentIndex, string Id, string Name, object? Arguments,
        string? ThoughtSignature = null, string? Namespace = null) : AssistantMessageFrame;
}

/// <summary>
/// 助手流 → 帧的编码器。对应 TS <c>AssistantMessageFrameEncoder</c>：
/// <c>partial</c> 是共享的实时累积器，编码器用逐块偏移量避免重放旧事件时重复输出
/// 已经可见的增量。
/// </summary>
public sealed class AssistantMessageFrameEncoder
{
    private abstract record EncoderBlockState;

    private sealed record TextEncoderState(string Kind, int CoveredChars, int DeltaChars) : EncoderBlockState;

    private sealed record ToolCallEncoderState(bool CaughtUp, string CatchupJson, string SnapshotArguments)
        : EncoderBlockState;

    private static readonly string EmptyParsedToolArguments =
        FrameJson.SerializeArguments(JsonParse.ParseStreamingJson(""));

    private bool _started;
    private bool _terminal;
    private readonly Dictionary<int, EncoderBlockState> _blocks = [];

    /// <summary>编码一个事件；返回 null 表示该事件不产生帧（done/error、被覆盖的增量等）。</summary>
    public AssistantMessageFrame? Encode(AssistantMessageEvent @event)
    {
        if (_terminal)
        {
            throw new InvalidOperationException(
                $"Assistant message event {@event.GetType().Name} follows a terminal event");
        }

        switch (@event)
        {
            case AssistantMessageEvent.Start start:
                if (_started) throw new InvalidOperationException(
                    "Assistant message stream contains more than one start event");
                _started = true;
                return new AssistantMessageFrame.Start(CloneStartMessage(start.Partial));
            case AssistantMessageEvent.Done:
                if (!_started) throw new InvalidOperationException(
                    "Assistant message done event appears before start");
                _terminal = true;
                return null;
            case AssistantMessageEvent.Error:
                _terminal = true;
                return null;
        }

        if (!_started)
        {
            throw new InvalidOperationException(
                $"Assistant message {@event.GetType().Name} event appears before start");
        }

        switch (@event)
        {
            case AssistantMessageEvent.TextStart textStart:
            {
                var content = EventBlock(textStart.ContentIndex, textStart.Partial);
                if (content is not TextContent text)
                {
                    throw new InvalidOperationException(
                        $"text_start event points to {content.GetType().Name} block at index {textStart.ContentIndex}");
                }
                StartBlock(textStart.ContentIndex, new TextEncoderState("text", text.Text.Length, 0));
                return new AssistantMessageFrame.TextStart(textStart.ContentIndex, CloneTextContent(text));
            }
            case AssistantMessageEvent.TextDelta textDelta:
                return EncodeTextDelta(textDelta.ContentIndex, textDelta.Delta, "text");
            case AssistantMessageEvent.TextEnd textEnd:
            {
                var content = EventBlock(textEnd.ContentIndex, textEnd.Partial);
                if (content is not TextContent text)
                {
                    throw new InvalidOperationException(
                        $"text_end event points to {content.GetType().Name} block at index {textEnd.ContentIndex}");
                }
                EndBlock(textEnd.ContentIndex, "text");
                return new AssistantMessageFrame.TextEnd(textEnd.ContentIndex, textEnd.Content, text.TextSignature);
            }
            case AssistantMessageEvent.ThinkingStart thinkingStart:
            {
                var content = EventBlock(thinkingStart.ContentIndex, thinkingStart.Partial);
                if (content is not ThinkingContent thinking)
                {
                    throw new InvalidOperationException(
                        $"thinking_start event points to {content.GetType().Name} block at index {thinkingStart.ContentIndex}");
                }
                StartBlock(thinkingStart.ContentIndex, new TextEncoderState("thinking", thinking.Thinking.Length, 0));
                return new AssistantMessageFrame.ThinkingStart(
                    thinkingStart.ContentIndex, CloneThinkingContent(thinking));
            }
            case AssistantMessageEvent.ThinkingDelta thinkingDelta:
                return EncodeTextDelta(thinkingDelta.ContentIndex, thinkingDelta.Delta, "thinking");
            case AssistantMessageEvent.ThinkingEnd thinkingEnd:
            {
                var content = EventBlock(thinkingEnd.ContentIndex, thinkingEnd.Partial);
                if (content is not ThinkingContent thinking)
                {
                    throw new InvalidOperationException(
                        $"thinking_end event points to {content.GetType().Name} block at index {thinkingEnd.ContentIndex}");
                }
                EndBlock(thinkingEnd.ContentIndex, "thinking");
                return new AssistantMessageFrame.ThinkingEnd(thinkingEnd.ContentIndex, thinkingEnd.Content,
                    thinking.Signature, thinking.Redacted);
            }
            case AssistantMessageEvent.ToolCallStart toolCallStart:
            {
                var content = EventBlock(toolCallStart.ContentIndex, toolCallStart.Partial);
                if (content is not ToolCallContent toolCall)
                {
                    throw new InvalidOperationException(
                        $"toolcall_start event points to {content.GetType().Name} block at index {toolCallStart.ContentIndex}");
                }
                var snapshotArguments = FrameJson.SerializeArguments(toolCall.Arguments);
                var caughtUp = snapshotArguments == EmptyParsedToolArguments;
                StartBlock(toolCallStart.ContentIndex,
                    new ToolCallEncoderState(caughtUp, "", caughtUp ? "" : snapshotArguments));
                return new AssistantMessageFrame.ToolCallStart(
                    toolCallStart.ContentIndex, CloneToolCall(toolCall));
            }
            case AssistantMessageEvent.ToolCallDelta toolCallDelta:
            {
                var state = Block(toolCallDelta.ContentIndex, "toolCall") as ToolCallEncoderState
                    ?? throw new InvalidOperationException("Unreachable tool-call encoder state");
                if (state.CaughtUp)
                {
                    return toolCallDelta.Delta.Length == 0
                        ? null
                        : new AssistantMessageFrame.ToolCallDelta(toolCallDelta.ContentIndex, toolCallDelta.Delta);
                }
                state = state with { CatchupJson = state.CatchupJson + toolCallDelta.Delta };
                _blocks[toolCallDelta.ContentIndex] = state;

                var argumentsValue = JsonParse.ParseStreamingJson(state.CatchupJson);
                if (FrameJson.SerializeArguments(argumentsValue) != state.SnapshotArguments)
                {
                    // 旧语法调用在 toolcall_start 里就带了初始输入，但其 JSON 增量流仍从空输入
                    // 开始；因此解析出的参数可能是 start 快照的「延伸」而非精确复现。
                    var snapshotArguments = JsonParse.ParseStreamingJson(state.SnapshotArguments);
                    if (!FrameJson.IsJsonPrefix(snapshotArguments, argumentsValue)) return null;
                }

                var json = state.CatchupJson;
                _blocks[toolCallDelta.ContentIndex] = state with { CaughtUp = true, SnapshotArguments = "", CatchupJson = "" };
                return json.Length == 0
                    ? null
                    : new AssistantMessageFrame.ToolCallCheckpoint(toolCallDelta.ContentIndex, json);
            }
            case AssistantMessageEvent.ToolCallEnd toolCallEnd:
            {
                var content = EventBlock(toolCallEnd.ContentIndex, toolCallEnd.Partial);
                if (content is not ToolCallContent)
                {
                    throw new InvalidOperationException(
                        $"toolcall_end event points to {content.GetType().Name} block at index {toolCallEnd.ContentIndex}");
                }
                EndBlock(toolCallEnd.ContentIndex, "toolCall");
                return new AssistantMessageFrame.ToolCallEnd(
                    toolCallEnd.ContentIndex,
                    toolCallEnd.ToolCall.Id,
                    toolCallEnd.ToolCall.Name,
                    FrameJson.CloneValue(toolCallEnd.ToolCall.Arguments),
                    toolCallEnd.ToolCall.ThoughtSignature,
                    toolCallEnd.ToolCall.Namespace);
            }
            default:
                throw new InvalidOperationException($"Unhandled assistant message event {@event.GetType().Name}");
        }
    }

    private void StartBlock(int contentIndex, EncoderBlockState state)
    {
        FrameAssert.AssertContentIndex(contentIndex);
        if (_blocks.ContainsKey(contentIndex))
        {
            throw new InvalidOperationException($"Assistant message block {contentIndex} starts more than once");
        }
        _blocks[contentIndex] = state;
    }

    private EncoderBlockState Block(int contentIndex, string kind)
    {
        FrameAssert.AssertContentIndex(contentIndex);
        if (!_blocks.TryGetValue(contentIndex, out var state))
        {
            throw new InvalidOperationException($"Assistant message {kind} block {contentIndex} has not started");
        }
        var actualKind = state switch
        {
            TextEncoderState text => text.Kind,
            ToolCallEncoderState => "toolCall",
            _ => "unknown",
        };
        if (actualKind != kind)
        {
            throw new InvalidOperationException(
                $"Assistant message block {contentIndex} is {actualKind}, not {kind}");
        }
        return state;
    }

    private void EndBlock(int contentIndex, string kind)
    {
        Block(contentIndex, kind);
        _blocks.Remove(contentIndex);
    }

    private AssistantMessageFrame? EncodeTextDelta(int contentIndex, string delta, string kind)
    {
        var state = (TextEncoderState)Block(contentIndex, kind);
        var deltaStart = state.DeltaChars;
        _blocks[contentIndex] = state with { DeltaChars = state.DeltaChars + delta.Length };
        var covered = Math.Max(0, state.CoveredChars - deltaStart);
        if (covered >= delta.Length) return null;
        var uncovered = covered == 0 ? delta : delta[covered..];
        return kind == "text"
            ? new AssistantMessageFrame.TextDelta(contentIndex, uncovered)
            : new AssistantMessageFrame.ThinkingDelta(contentIndex, uncovered);
    }

    private static ContentBlock EventBlock(int contentIndex, AssistantMessage partial)
    {
        FrameAssert.AssertContentIndex(contentIndex);
        if (contentIndex >= partial.Content.Count)
        {
            throw new InvalidOperationException(
                $"event has no content block at index {contentIndex}");
        }
        return partial.Content[contentIndex];
    }

    private static TextContent CloneTextContent(TextContent content)
        => new(content.Text) { TextSignature = content.TextSignature };

    private static ThinkingContent CloneThinkingContent(ThinkingContent content)
        => new(content.Thinking, content.Signature) { Redacted = content.Redacted };

    private static ToolCallContent CloneToolCall(ToolCallContent toolCall)
        => new(toolCall.Id, toolCall.Name, FrameJson.CloneValue(toolCall.Arguments))
        {
            ThoughtSignature = toolCall.ThoughtSignature,
            Namespace = toolCall.Namespace,
        };

    /// <summary>对应 TS <c>cloneStartMessage</c>：只保留起始元数据，内容清空、停止原因 Pending。</summary>
    private static AssistantMessage CloneStartMessage(AssistantMessage message)
        => new(
            Content: [],
            StopReason: StopReason.Pending,
            UsageStats: message.UsageStats,
            Model: message.Model,
            Api: message.Api,
            Provider: message.Provider,
            Timestamp: message.Timestamp)
        {
            ResponseModel = message.ResponseModel,
            ResponseId = message.ResponseId,
            ProviderThinkingLevel = message.ProviderThinkingLevel,
            Diagnostics = message.Diagnostics is null ? null : [.. message.Diagnostics],
        };
}

/// <summary>
/// 帧序列 → 助手消息的回放器。对应 TS <c>reduceAssistantMessageFrames</c>：
/// 不改动传入帧；序列中没有 start 帧时返回 null。
/// </summary>
public static class AssistantMessageFrameReducer
{
    private abstract class MutableBlock;

    private sealed class MutableText : MutableBlock
    {
        public required string Text { get; set; }

        public string? TextSignature { get; set; }
    }

    private sealed class MutableThinking : MutableBlock
    {
        public required string Thinking { get; set; }

        public string? Signature { get; set; }

        public bool? Redacted { get; set; }
    }

    private sealed class MutableToolCall : MutableBlock
    {
        public required string Id { get; set; }

        public required string Name { get; set; }

        public object? Arguments { get; set; }

        public string? ThoughtSignature { get; set; }

        public string? Namespace { get; set; }
    }

    private abstract record ReducerBlockState
    {
        public bool Ended { get; set; }
    }

    private sealed record TextReducerState : ReducerBlockState;

    private sealed record ThinkingReducerState : ReducerBlockState;

    private sealed record ToolCallReducerState : ReducerBlockState
    {
        public string Json { get; set; } = "";
    }

    public static AssistantMessage? Reduce(IEnumerable<AssistantMessageFrame> frames)
    {
        AssistantMessage? template = null;
        var blocks = new List<MutableBlock>();
        var states = new Dictionary<int, ReducerBlockState>();
        string? frameBeforeStart = null;

        foreach (var frame in frames)
        {
            if (frame is AssistantMessageFrame.Start start)
            {
                if (template is not null)
                {
                    throw new InvalidOperationException(
                        "Assistant message frame sequence contains more than one start frame");
                }
                if (frameBeforeStart is not null)
                {
                    throw new InvalidOperationException(
                        $"{frameBeforeStart} frame appears before the start frame");
                }
                template = start.Partial;
                continue;
            }
            if (template is null)
            {
                frameBeforeStart ??= frame.GetType().Name;
                continue;
            }

            switch (frame)
            {
                case AssistantMessageFrame.TextStart textStart:
                    if (textStart.Content is null)
                    {
                        throw new InvalidOperationException("text_start frame contains no text content");
                    }
                    AppendBlock(blocks, states, textStart.ContentIndex,
                        new MutableText { Text = textStart.Content.Text, TextSignature = textStart.Content.TextSignature },
                        new TextReducerState());
                    break;
                case AssistantMessageFrame.TextDelta textDelta:
                {
                    var (block, _) = ActiveBlock(blocks, states, textDelta.ContentIndex, "text", "text_delta");
                    ((MutableText)block).Text += textDelta.Delta;
                    break;
                }
                case AssistantMessageFrame.TextEnd textEnd:
                {
                    var (block, state) = ActiveBlock(blocks, states, textEnd.ContentIndex, "text", "text_end");
                    var text = (MutableText)block;
                    text.Text = textEnd.Content;
                    text.TextSignature = textEnd.TextSignature;
                    state.Ended = true;
                    break;
                }
                case AssistantMessageFrame.ThinkingStart thinkingStart:
                    if (thinkingStart.Content is null)
                    {
                        throw new InvalidOperationException("thinking_start frame contains no thinking content");
                    }
                    AppendBlock(blocks, states, thinkingStart.ContentIndex,
                        new MutableThinking
                        {
                            Thinking = thinkingStart.Content.Thinking,
                            Signature = thinkingStart.Content.Signature,
                            Redacted = thinkingStart.Content.Redacted,
                        },
                        new ThinkingReducerState());
                    break;
                case AssistantMessageFrame.ThinkingDelta thinkingDelta:
                {
                    var (block, _) = ActiveBlock(blocks, states, thinkingDelta.ContentIndex, "thinking", "thinking_delta");
                    ((MutableThinking)block).Thinking += thinkingDelta.Delta;
                    break;
                }
                case AssistantMessageFrame.ThinkingEnd thinkingEnd:
                {
                    var (block, state) = ActiveBlock(blocks, states, thinkingEnd.ContentIndex, "thinking", "thinking_end");
                    var thinking = (MutableThinking)block;
                    thinking.Thinking = thinkingEnd.Content;
                    thinking.Signature = thinkingEnd.ThinkingSignature;
                    thinking.Redacted = thinkingEnd.Redacted;
                    state.Ended = true;
                    break;
                }
                case AssistantMessageFrame.ToolCallStart toolCallStart:
                    if (toolCallStart.ToolCall is null)
                    {
                        throw new InvalidOperationException("toolcall_start frame contains no tool call");
                    }
                    AppendBlock(blocks, states, toolCallStart.ContentIndex,
                        new MutableToolCall
                        {
                            Id = toolCallStart.ToolCall.Id,
                            Name = toolCallStart.ToolCall.Name,
                            Arguments = FrameJson.CloneValue(toolCallStart.ToolCall.Arguments),
                            ThoughtSignature = toolCallStart.ToolCall.ThoughtSignature,
                            Namespace = toolCallStart.ToolCall.Namespace,
                        },
                        new ToolCallReducerState());
                    break;
                case AssistantMessageFrame.ToolCallCheckpoint checkpoint:
                {
                    var (block, state) = ActiveBlock(blocks, states, checkpoint.ContentIndex, "toolCall", "toolcall_checkpoint");
                    var toolCall = (MutableToolCall)block;
                    ((ToolCallReducerState)state).Json = checkpoint.Json;
                    toolCall.Arguments = JsonParse.ParseStreamingJson(checkpoint.Json);
                    break;
                }
                case AssistantMessageFrame.ToolCallDelta toolCallDelta:
                {
                    var (_, state) = ActiveBlock(blocks, states, toolCallDelta.ContentIndex, "toolCall", "toolcall_delta");
                    var toolCallState = (ToolCallReducerState)state;
                    toolCallState.Json += toolCallDelta.Delta;
                    break;
                }
                case AssistantMessageFrame.ToolCallEnd toolCallEnd:
                {
                    var (block, state) = ActiveBlock(blocks, states, toolCallEnd.ContentIndex, "toolCall", "toolcall_end");
                    var toolCall = (MutableToolCall)block;
                    toolCall.Id = toolCallEnd.Id;
                    toolCall.Name = toolCallEnd.Name;
                    toolCall.Arguments = FrameJson.CloneValue(toolCallEnd.Arguments);
                    toolCall.ThoughtSignature = toolCallEnd.ThoughtSignature;
                    toolCall.Namespace = toolCallEnd.Namespace;
                    state.Ended = true;
                    break;
                }
            }
        }

        if (template is null) return null;

        // 未收到 toolcall_end 的块用累积 JSON 收尾（对齐 TS 的收尾循环）。
        foreach (var (contentIndex, state) in states)
        {
            if (state is not ToolCallReducerState toolCallState || toolCallState.Ended || toolCallState.Json.Length == 0)
            {
                continue;
            }
            if (blocks[contentIndex] is not MutableToolCall toolCall)
            {
                throw new InvalidOperationException("Unreachable tool-call frame state");
            }
            toolCall.Arguments = JsonParse.ParseStreamingJson(toolCallState.Json);
        }

        return template with { Content = blocks.Select(Materialize).ToList() };
    }

    private static ContentBlock Materialize(MutableBlock block) => block switch
    {
        MutableText text => new TextContent(text.Text) { TextSignature = text.TextSignature },
        MutableThinking thinking => new ThinkingContent(thinking.Thinking, thinking.Signature)
        {
            Redacted = thinking.Redacted,
        },
        MutableToolCall toolCall => new ToolCallContent(toolCall.Id, toolCall.Name, toolCall.Arguments)
        {
            ThoughtSignature = toolCall.ThoughtSignature,
            Namespace = toolCall.Namespace,
        },
        _ => throw new InvalidOperationException("Unknown mutable block"),
    };

    private static void AppendBlock(List<MutableBlock> blocks, Dictionary<int, ReducerBlockState> states,
        int contentIndex, MutableBlock block, ReducerBlockState state)
    {
        FrameAssert.AssertContentIndex(contentIndex);
        if (contentIndex != blocks.Count)
        {
            var reason = contentIndex < blocks.Count ? "already exists" : "would leave a gap";
            throw new InvalidOperationException(
                $"Cannot start assistant message block at index {contentIndex}: {reason}");
        }
        blocks.Add(block);
        states[contentIndex] = state;
    }

    private static (MutableBlock Block, ReducerBlockState State) ActiveBlock(
        List<MutableBlock> blocks, Dictionary<int, ReducerBlockState> states,
        int contentIndex, string expectedKind, string frameType)
    {
        FrameAssert.AssertContentIndex(contentIndex);
        if (!states.TryGetValue(contentIndex, out var state) || contentIndex >= blocks.Count)
        {
            throw new InvalidOperationException(
                $"{frameType} frame has no started block at index {contentIndex}");
        }
        var block = blocks[contentIndex];
        var actualKind = KindOf(block);
        if (actualKind != expectedKind)
        {
            throw new InvalidOperationException(
                $"{frameType} frame expected {expectedKind} block at index {contentIndex}, found {actualKind}");
        }
        if (state.Ended)
        {
            throw new InvalidOperationException(
                $"{frameType} frame follows the end of block at index {contentIndex}");
        }
        return (block, state);
    }

    private static string KindOf(MutableBlock block) => block switch
    {
        MutableText => "text",
        MutableThinking => "thinking",
        MutableToolCall => "toolCall",
        _ => "unknown",
    };
}

/// <summary>帧层的共享断言与 JSON 辅助。</summary>
internal static class FrameAssert
{
    public static void AssertContentIndex(int contentIndex)
    {
        if (contentIndex < 0)
        {
            throw new InvalidOperationException($"Invalid assistant message frame contentIndex: {contentIndex}");
        }
    }
}

/// <summary>帧层的 JSON 辅助：参数序列化、深拷贝与 JSON 前缀判定。</summary>
internal static class FrameJson
{
    /// <summary>对应 TS <c>serializedArguments</c>（JSON.stringify）。</summary>
    public static string SerializeArguments(object? argumentsValue)
        => ToNode(argumentsValue)?.ToJsonString() ?? "null";

    /// <summary>对应 TS <c>structuredClone</c>。</summary>
    public static object? CloneValue(object? value) => ToNode(value)?.DeepClone();

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonSerializer.SerializeToNode(value),
    };

    /// <summary>
    /// 判定 <paramref name="snapshot"/> 是否为 <paramref name="current"/> 的 JSON 前缀。
    /// 对应 TS <c>isJsonPrefix</c>：字符串按前缀、数组按逐元素、对象按键存在且值前缀、
    /// 标量按相等。
    /// </summary>
    public static bool IsJsonPrefix(object? snapshot, object? current)
        => IsJsonPrefix(ToNode(snapshot), ToNode(current));

    private static bool IsJsonPrefix(JsonNode? snapshot, JsonNode? current)
    {
        if (snapshot is null || snapshot.GetValueKind() == JsonValueKind.Null)
        {
            return current is null || current.GetValueKind() == JsonValueKind.Null;
        }

        if (snapshot is JsonArray snapshotArray)
        {
            if (current is not JsonArray currentArray) return false;
            if (snapshotArray.Count > currentArray.Count) return false;
            for (var index = 0; index < snapshotArray.Count; index++)
            {
                if (!IsJsonPrefix(snapshotArray[index], currentArray[index])) return false;
            }
            return true;
        }

        if (snapshot is JsonObject snapshotObject)
        {
            if (current is not JsonObject currentObject) return false;
            foreach (var (key, value) in snapshotObject)
            {
                if (!currentObject.ContainsKey(key) || !IsJsonPrefix(value, currentObject[key])) return false;
            }
            return true;
        }

        return snapshot.ToJsonString() == current?.ToJsonString();
    }
}
