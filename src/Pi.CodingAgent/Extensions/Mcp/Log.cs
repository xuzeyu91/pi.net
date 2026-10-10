// ============================================================================
// MCP server log — port of extensions/mcp/log.ts (4d-4a)
// ============================================================================
//
// Log messages MCP servers send with `notifications/message`, appended to `mcp.log` in the agent
// directory. Several pi processes may write to the same file, so every message is one synchronous
// append. The file is rotated to `mcp.log.1` once it grows past MAX_LOG_BYTES. Write errors are
// ignored: logging must not break tools.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>Port of <c>extensions/mcp/log.ts</c>.</summary>
public static class McpServerLogFormat
{
    /// <summary>Size at which the log rotates to <c>mcp.log.1</c>.</summary>
    public const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>Format one <c>notifications/message</c> from <paramref name="server"/> as a log line; continuation lines are indented.</summary>
    public static string FormatMessage(string server, JsonNode? parameters, DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var message = parameters as JsonObject ?? new JsonObject { ["data"] = parameters?.DeepClone() };
        var level = message["level"] is JsonValue levelValue && levelValue.TryGetValue<string>(out var levelText)
            ? levelText
            : "info";
        var logger = message["logger"] is JsonValue loggerValue
            && loggerValue.TryGetValue<string>(out var loggerText)
            && !string.IsNullOrEmpty(loggerText)
                ? $" {loggerText}:"
                : "";
        var text = Regex.Replace(FormatData(message["data"]), "\r?\n", "\n    ");
        return $"{timestamp} [{server}] {level}{logger} {text}\n";
    }

    private static string FormatData(JsonNode? data)
    {
        if (data is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        try
        {
            // Difference C107: a missing `data` key formats as "null" (TS JSON.stringify(undefined)
            // yields "undefined"); JSON null and missing are the same JsonNode? in C#.
            return data?.ToJsonString() ?? "null";
        }
        catch
        {
            return data?.ToString() ?? "null";
        }
    }
}

/// <summary>Appends server log messages to one file. TS <c>McpServerLog</c>.</summary>
public sealed class McpServerLog
{
    public McpServerLog(string path)
    {
        Path = path;
    }

    public string Path { get; }

    private long? size;

    public void Write(string server, JsonNode? parameters)
    {
        var line = McpServerLogFormat.FormatMessage(server, parameters);
        try
        {
            if (this.size is null)
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
                Directory.CreateDirectory(directory);
                this.size = CurrentSize();
            }

            if (this.size > McpServerLogFormat.MaxLogBytes)
            {
                // Another process may have rotated it already; check before renaming. TS renameSync
                // fails when the target exists (the message is then dropped); the port keeps that
                // by moving without overwrite.
                if (CurrentSize() > McpServerLogFormat.MaxLogBytes)
                {
                    File.Move(Path, $"{Path}.1");
                }

                this.size = CurrentSize();
            }

            File.AppendAllText(Path, line);
            this.size += Encoding.UTF8.GetByteCount(line);
        }
        catch
        {
            // Ignore: the log is best effort.
        }
    }

    private long CurrentSize()
    {
        try
        {
            return new FileInfo(Path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
