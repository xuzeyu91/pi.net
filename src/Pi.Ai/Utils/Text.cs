using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>消息文本提取。对应 TS <c>utils/text.ts</c>。</summary>
public static class Text
{
    /// <summary>提取并拼接消息内容中的文本块。对应 TS <c>contentText</c>。</summary>
    public static string ContentText(string? content, string separator = "\n")
        => content ?? "";

    public static string ContentText(IReadOnlyList<ContentBlock> content, string separator = "\n")
        => string.Join(separator,
            content.OfType<TextContent>().Select(block => block.Text));

    public static string ContentText(SystemMessage message, string separator = "\n")
        => message.Content is null ? "" : ContentText(message.Content, separator);

    /// <summary>把 system 消息渲染为完整提示：content 后接全部 sections。对应 TS <c>getSystemMessageText</c>。</summary>
    public static string GetSystemMessageText(SystemMessage message)
    {
        var parts = new List<string> { ContentText(message) };
        if (message.Sections is not null)
        {
            foreach (var text in message.Sections.Values)
            {
                if (text is not null) parts.Add(text);
            }
        }
        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>
    /// 渲染中段 system 消息（接受中段系统消息的 API 用）。段变更按名加框，
    /// 让模型能对应到前导提示；该框架仅请求期使用。对应 TS <c>renderSystemMessageUpdate</c>。
    /// </summary>
    public static string RenderSystemMessageUpdate(SystemMessage message)
    {
        var parts = new List<string>();
        var text = ContentText(message);
        if (text.Length > 0) parts.Add(text);
        if (message.Sections is not null)
        {
            foreach (var (name, value) in message.Sections)
            {
                parts.Add(value is null
                    ? $"Removed system prompt section \"{name}\"."
                    : $"Updated system prompt section \"{name}\":\n\n{value}");
            }
        }
        return string.Join("\n\n", parts);
    }
}
