using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>SSE 事件（data 载荷 + 可选 event/id）。对应 TS ai 包内联的 SSE 解析。</summary>
public sealed record AiSseEvent(string Data, string? Event = null, string? Id = null);

/// <summary>
/// SSE 流解析器（ai 包内联版本）。行协议语义与 MCP 传输的一致：
/// field: value、空行分发、data 多行 \n 连接、注释行跳过。
/// </summary>
public static class AiSse
{
    public static async Task ConsumeAsync(System.IO.Stream stream, Action<AiSseEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        string? eventName = null;
        string? eventId = null;
        var dataLines = new List<string>();

        void Dispatch()
        {
            if (dataLines.Count > 0)
            {
                onEvent(new AiSseEvent(string.Join("\n", dataLines), eventName, eventId));
                dataLines = [];
            }
            eventName = null;
            eventId = null;
        }

        void ProcessLine(string rawLine)
        {
            var line = rawLine.EndsWith("\r") ? rawLine[..^1] : rawLine;
            if (line.Length == 0) { Dispatch(); return; }
            if (line.StartsWith(':')) return;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            if (field == "data") dataLines.Add(value);
            else if (field == "event") eventName = value;
            else if (field == "id") eventId = value;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            ProcessLine(line);
        }
        Dispatch();
    }
}
