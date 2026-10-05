using System.Text.Json.Nodes;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>诊断错误信息。对应 TS <c>DiagnosticErrorInfo</c>。</summary>
public sealed record DiagnosticErrorInfo(
    string? Name,
    string Message,
    string? Stack,
    object? Code);

/// <summary>附加在助手消息上的诊断条目。对应 TS <c>AssistantMessageDiagnostic</c>。</summary>
public sealed record AssistantMessageDiagnostic(
    string Type,
    long Timestamp,
    DiagnosticErrorInfo? Error = null,
    JsonObject? Details = null);

/// <summary>助手消息诊断辅助。对应 TS <c>utils/diagnostics.ts</c>。</summary>
public static class Diagnostics
{
    /// <summary>把抛出值格式化为字符串。对应 TS <c>formatThrownValue</c>。</summary>
    public static string FormatThrownValue(object? value) => value switch
    {
        Exception exception => exception.Message.Length > 0 ? exception.Message : exception.GetType().Name,
        string text => text,
        null => "",
        _ => value.ToString() ?? "",
    };

    /// <summary>从异常提取诊断信息。对应 TS <c>extractDiagnosticError</c>。</summary>
    public static DiagnosticErrorInfo ExtractDiagnosticError(object? error)
    {
        if (error is not Exception exception)
        {
            return new DiagnosticErrorInfo("ThrownValue", FormatThrownValue(error), null, null);
        }
        // .NET 异常无 TS 的 code 惯例；保留扩展位（如 HttpRequestException.StatusCode 由调用方传 details）。
        return new DiagnosticErrorInfo(
            exception.GetType().Name,
            exception.Message.Length > 0 ? exception.Message : exception.GetType().Name,
            exception.StackTrace,
            null);
    }

    /// <summary>创建诊断条目。对应 TS <c>createAssistantMessageDiagnostic</c>。</summary>
    public static AssistantMessageDiagnostic CreateAssistantMessageDiagnostic(
        string type, object? error, JsonObject? details = null)
        => new(type, DateTimeOffset.Now.ToUnixTimeMilliseconds(), ExtractDiagnosticError(error), details);

    /// <summary>向助手消息追加诊断（不可变列表重建）。对应 TS <c>appendAssistantMessageDiagnostic</c>。</summary>
    public static void AppendAssistantMessageDiagnostic(AssistantMessage message, AssistantMessageDiagnostic diagnostic)
    {
        var list = message.Diagnostics is { Count: > 0 } existing
            ? new List<AssistantMessageDiagnostic>(existing) { diagnostic }
            : [diagnostic];
        message.Diagnostics = list;
    }
}
