using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// GitHub Copilot 动态请求头。对应 TS <c>api/github-copilot-headers.ts</c>：
/// X-Initiator 区分用户/agent 发起；带图请求必须带 Copilot-Vision-Request。
/// </summary>
public static class GitHubCopilotHeaders
{
    /// <summary>最后一条消息不是 user 即为 agent 发起（如助手/工具消息后的跟进）。</summary>
    public static string InferInitiator(IReadOnlyList<ChatMessage> messages)
    {
        var last = messages.Count > 0 ? messages[^1] : null;
        return last is not null && last is not UserMessage ? "agent" : "user";
    }

    public static bool HasVisionInput(IReadOnlyList<ChatMessage> messages)
        => messages.Any(msg => msg switch
        {
            UserMessage user => user.Content.Any(c => c is ImageContent),
            ToolResultMessage toolResult => toolResult.Content.Any(c => c is ImageContent),
            _ => false,
        });

    public static IReadOnlyDictionary<string, string> BuildDynamicHeaders(
        IReadOnlyList<ChatMessage> messages, bool hasImages)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Initiator"] = InferInitiator(messages),
            ["Openai-Intent"] = "conversation-edits",
        };
        if (hasImages) headers["Copilot-Vision-Request"] = "true";
        return headers;
    }
}
