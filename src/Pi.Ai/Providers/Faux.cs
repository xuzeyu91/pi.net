using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Faux 测试 provider。对应 TS <c>providers/faux.ts</c>：以脚本化的响应序列
/// 回放"助手回合"，不访问网络。用于测试 agent 循环与上层消费方。
/// </summary>
public static class Faux
{
    private static readonly Usage ZeroUsage = new(0, 0);

    /// <summary>默认 faux 模型描述。</summary>
    public static Model DefaultModel { get; } = new(
        Id: "faux-1",
        Name: "Faux Model",
        Api: "faux",
        Provider: "faux");

    // ---------- 内容块工厂（对应 fauxText / fauxThinking / fauxToolCall） ----------

    public static TextContent Text(string text) => new(text);

    public static ThinkingContent Thinking(string thinking) => new(thinking);

    public static ToolCallContent ToolCall(string name, object? arguments, string? id = null)
        => new(id ?? $"tool_{Guid.NewGuid():N}", name, arguments);

    /// <summary>脚本条目：一个待回放的助手回合（内容块 + 停止原因）。</summary>
    public sealed record Response(
        IReadOnlyList<ContentBlock> Content,
        StopReason StopReason = StopReason.Stop,
        string? ErrorMessage = null,
        Usage? Usage = null)
    {
        /// <summary>纯文本回复。</summary>
        public static Response Text(string text) => new([new TextContent(text)]);

        /// <summary>工具调用回复（停止原因 ToolUse）。</summary>
        public static Response ToolUse(params ToolCallContent[] calls)
            => new(calls, StopReason.ToolUse);
    }

    /// <summary>
    /// 创建脚本化 <see cref="StreamFn"/>：每次调用按顺序回放一条 <see cref="Response"/>，
    /// 脚本耗尽后抛错（测试应保证长度匹配）。文本按小块切分以模拟流式。
    /// </summary>
    public static StreamFn CreateStreamFn(IReadOnlyList<Response> script)
    {
        var cursor = 0;
        return (model, context, options, cancellationToken) =>
        {
            if (cursor >= script.Count)
            {
                var exhausted = new AssistantMessageEventStream();
                var msg = new AssistantMessage([], StopReason.Error,
                    ErrorMessage: "Faux script exhausted", UsageStats: ZeroUsage);
                exhausted.Push(new AssistantMessageEvent.Error("Faux script exhausted", msg));
                exhausted.End(msg);
                return Task.FromResult<IAssistantMessageEventStream>(exhausted);
            }

            var response = script[cursor++];
            var stream = new AssistantMessageEventStream();
            _ = Task.Run(() => Replay(stream, response, cancellationToken), cancellationToken);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };
    }

    private static async Task Replay(AssistantMessageEventStream stream, Response response, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequence = 0L;

        // 先发 start（空内容）。
        var partial = new AssistantMessage([], response.StopReason, response.ErrorMessage,
            response.Usage, Timestamp: timestamp);
        stream.Push(new AssistantMessageEvent.Start(partial));

        foreach (var block in response.Content)
        {
            ct.ThrowIfCancellationRequested();
            switch (block)
            {
                case TextContent text:
                {
                    // 按小块切分文本模拟流式增量。
                    const int chunkSize = 5;
                    var chunks = Enumerable.Range(0, (int)Math.Ceiling(text.Text.Length / (double)chunkSize))
                        .Select(i => text.Text.Substring(i * chunkSize, Math.Min(chunkSize, text.Text.Length - i * chunkSize)))
                        .ToList();
                    stream.Push(new AssistantMessageEvent.TextStart(partial));
                    var accumulated = string.Empty;
                    foreach (var chunk in chunks)
                    {
                        accumulated += chunk;
                        sequence++;
                        partial = ReplaceOrAppendText(partial, accumulated);
                        stream.Push(new AssistantMessageEvent.TextDelta(chunk, sequence, partial));
                    }
                    stream.Push(new AssistantMessageEvent.TextEnd(partial));
                    break;
                }
                case ThinkingContent thinking:
                {
                    sequence++;
                    partial = AppendBlock(partial, new ThinkingContent(thinking.Thinking, thinking.Signature));
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(thinking.Thinking, sequence, thinking.Signature, partial));
                    break;
                }
                case ToolCallContent toolCall:
                {
                    // 工具调用一次性给出（参数增量解析留待真实 provider 移植时实现）。
                    partial = AppendBlock(partial, toolCall);
                    stream.Push(new AssistantMessageEvent.ToolCallDelta(
                        partial.Content.OfType<ToolCallContent>().ToList().IndexOf(toolCall), "", partial));
                    break;
                }
            }
        }

        var final = partial with
        {
            StopReason = response.StopReason,
            ErrorMessage = response.ErrorMessage,
            UsageStats = response.Usage ?? new Usage(10, 5),
            Model = "faux-1",
            Api = "faux",
            Provider = "faux",
            ThinkingLevel = null,
            Timestamp = timestamp,
        };

        if (response.StopReason is StopReason.Error)
            stream.Push(new AssistantMessageEvent.Error(response.ErrorMessage ?? "unknown", final));
        stream.Push(new AssistantMessageEvent.Done(final));
        stream.End(final);
    }

    private static AssistantMessage ReplaceOrAppendText(AssistantMessage message, string fullText)
    {
        var blocks = message.Content.ToList();
        var index = blocks.FindIndex(b => b is TextContent);
        if (index >= 0) blocks[index] = new TextContent(fullText);
        else blocks.Add(new TextContent(fullText));
        return message with { Content = blocks };
    }

    private static AssistantMessage AppendBlock(AssistantMessage message, ContentBlock block)
    {
        var blocks = message.Content.ToList();
        blocks.Add(block);
        return message with { Content = blocks };
    }
}
