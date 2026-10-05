using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// 跨 provider 的消息转录变换。对应 TS <c>api/transform-messages.ts</c>：
/// ① 不支持图片的模型把图片降级为占位文本；② 跨模型重放时思维块/签名规范化；
/// ③ 工具调用 ID 归一化；④ 为孤儿的工具调用插入合成结果，error/aborted 的
/// 助手回合整体跳过。
/// </summary>
public static class TransformMessages
{
    private const string NonVisionUserImagePlaceholder = "(image omitted: model does not support images)";
    private const string NonVisionToolImagePlaceholder = "(tool image omitted: model does not support images)";

    /// <summary>把连续图片块折叠成一个占位文本块。</summary>
    private static List<ContentBlock> ReplaceImagesWithPlaceholder(
        IReadOnlyList<ContentBlock> content, string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;

        foreach (var block in content)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder)
                {
                    result.Add(new TextContent(placeholder));
                }
                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent { Text: { } text } && text == placeholder;
        }

        return result;
    }

    private static bool SupportsImages(ModelSpec model) => model.Input.Contains("image");

    private static IReadOnlyList<ChatMessage> DowngradeUnsupportedImages(
        IReadOnlyList<ChatMessage> messages, ModelSpec model)
    {
        if (SupportsImages(model)) return messages;

        return messages.Select(msg => msg switch
        {
            UserMessage user => user with
            {
                Content = ReplaceImagesWithPlaceholder(user.Content, NonVisionUserImagePlaceholder),
            },
            ToolResultMessage toolResult => toolResult with
            {
                Content = ReplaceImagesWithPlaceholder(toolResult.Content, NonVisionToolImagePlaceholder),
            },
            _ => msg,
        }).ToList();
    }

    /// <summary>
    /// 跨 provider 兼容的消息变换。对应 TS <c>transformMessages</c>：
    /// OpenAI Responses 生成的工具调用 ID 是 450+ 字符且含 | 等特殊字符，
    /// Anthropic 系 API 要求 ^[a-zA-Z0-9_-]+$（最长 64）——由可选的
    /// <paramref name="normalizeToolCallId"/> 跨模型归一化。
    /// </summary>
    public static IReadOnlyList<ChatMessage> Transform(
        IReadOnlyList<ChatMessage> messages,
        ModelSpec model,
        Func<string, ModelSpec, AssistantMessage, string>? normalizeToolCallId = null)
    {
        // 原始工具调用 ID → 归一化 ID。
        var toolCallIdMap = new Dictionary<string, string>();
        var imageAwareMessages = DowngradeUnsupportedImages(messages, model);

        // 第一遍：变换消息（图片降级、思维块、工具调用 ID 归一化）。
        var transformed = imageAwareMessages.Select(msg =>
        {
            // system/user 原样通过。
            if (msg is SystemMessage or UserMessage) return msg;

            // toolResult：按映射归一化 toolCallId。
            if (msg is ToolResultMessage toolResult)
            {
                if (toolCallIdMap.TryGetValue(toolResult.ToolCallId, out var normalizedId)
                    && normalizedId != toolResult.ToolCallId)
                {
                    return toolResult with { ToolCallId = normalizedId };
                }
                return toolResult;
            }

            if (msg is not AssistantMessage assistantMsg) return msg;
            var isSameModel =
                assistantMsg.Provider == model.Provider
                && assistantMsg.Api == model.Api
                && assistantMsg.Model == model.Id;

            var transformedContent = new List<ContentBlock>();
            foreach (var block in assistantMsg.Content)
            {
                switch (block)
                {
                    case ThinkingContent thinking:
                    {
                        // 加密思维块内容不透明，仅同模型有效；跨模型直接丢弃避免 API 报错。
                        if (thinking.Redacted == true)
                        {
                            if (isSameModel) transformedContent.Add(thinking);
                            break;
                        }
                        // 同模型保留带签名的思维块（回放需要），即使思考文本为空
                        // （OpenAI 加密推理）。
                        if (isSameModel && thinking.Signature is not null)
                        {
                            transformedContent.Add(thinking);
                            break;
                        }
                        // 空思维块跳过，其余转为纯文本。
                        if (string.IsNullOrWhiteSpace(thinking.Thinking)) break;
                        if (isSameModel)
                        {
                            transformedContent.Add(thinking);
                        }
                        else
                        {
                            transformedContent.Add(new TextContent(thinking.Thinking));
                        }
                        break;
                    }
                    case TextContent text:
                        transformedContent.Add(isSameModel ? text : new TextContent(text.Text));
                        break;
                    case ToolCallContent toolCall:
                    {
                        var normalizedToolCall = toolCall;
                        if (!isSameModel && toolCall.ThoughtSignature is not null)
                        {
                            normalizedToolCall = toolCall with { ThoughtSignature = null };
                        }
                        if (!isSameModel && normalizeToolCallId is not null)
                        {
                            var normalized = normalizeToolCallId(toolCall.Id, model, assistantMsg);
                            if (normalized != toolCall.Id)
                            {
                                toolCallIdMap[toolCall.Id] = normalized;
                                normalizedToolCall = normalizedToolCall with { Id = normalized };
                            }
                        }
                        transformedContent.Add(normalizedToolCall);
                        break;
                    }
                    default:
                        transformedContent.Add(block);
                        break;
                }
            }

            return assistantMsg with { Content = transformedContent };
        }).ToList();

        // 第二遍：为孤儿的工具调用插入合成空结果——保留思维签名并满足 API 要求。
        var result = new List<ChatMessage>();
        var pendingToolCalls = new List<ToolCallContent>();
        var existingToolResultIds = new HashSet<string>();
        // system 消息对工具调用记账是透明的：落在工具调用与其结果之间的 system
        // 被扣住，等到结果（含合成的）之后发出，避免对稍后才应答的调用产生重复结果。
        var heldSystemMessages = new List<ChatMessage>();

        void ClosePendingToolCalls()
        {
            if (pendingToolCalls.Count > 0)
            {
                foreach (var toolCall in pendingToolCalls)
                {
                    if (!existingToolResultIds.Contains(toolCall.Id))
                    {
                        result.Add(new ToolResultMessage(
                            toolCall.Id, toolCall.Name,
                            [new TextContent("No result provided")],
                            IsError: true,
                            Timestamp: DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                    }
                }
                pendingToolCalls = [];
                existingToolResultIds = new HashSet<string>();
            }
            result.AddRange(heldSystemMessages);
            heldSystemMessages.Clear();
        }

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case AssistantMessage assistantMsg:
                    // 上一条助手的孤儿工具调用在此插入合成结果。
                    ClosePendingToolCalls();

                    // error/aborted 的助手消息整体跳过：它们是不完整回合，重放会
                    // 造成 API 错误（如 OpenAI "reasoning without following item"）；
                    // 模型应从上一个有效状态重试。
                    if (assistantMsg.StopReason is StopReason.Error or StopReason.Aborted) continue;

                    var toolCalls = assistantMsg.Content
                        .OfType<ToolCallContent>()
                        .ToList();
                    if (toolCalls.Count > 0)
                    {
                        pendingToolCalls = toolCalls;
                        existingToolResultIds = new HashSet<string>();
                    }
                    result.Add(assistantMsg);
                    break;
                case ToolResultMessage toolResult:
                    existingToolResultIds.Add(toolResult.ToolCallId);
                    result.Add(toolResult);
                    break;
                case SystemMessage:
                    if (pendingToolCalls.Count > 0) heldSystemMessages.Add(msg);
                    else result.Add(msg);
                    break;
                case UserMessage:
                    // 新的用户回合打断工具流——为孤儿调用插入合成结果。
                    ClosePendingToolCalls();
                    result.Add(msg);
                    break;
                default:
                    result.Add(msg);
                    break;
            }
        }

        // 会话以未应答的工具调用收尾时，在此合成结果。
        ClosePendingToolCalls();

        return result;
    }
}
