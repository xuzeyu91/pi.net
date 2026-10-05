using System.Net;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>
/// provider HTTP 错误的共享规范化。对应 TS <c>error-body.ts</c>：网关/代理后面
/// 的端点可能返回非 2xx 响应，SDK 没把 body 折进 error.message。C# 侧对应
/// <see cref="ProviderHttpException"/>（Status/Headers/Body）与
/// <see cref="HttpRequestException"/>（StatusCode）。
/// </summary>
public sealed record NormalizedProviderError
{
    /// <summary>能提取到的 HTTP 状态码。</summary>
    public int? Status { get; init; }

    /// <summary>原始 HTTP body 原因（已修剪并截断到上限）。</summary>
    public string? Body { get; init; }

    /// <summary>error.Message；非 Error 抛出物为安全序列化结果。</summary>
    public required string Message { get; init; }

    /// <summary>message 已包含 body（无需再单独附加）。</summary>
    public required bool MessageCarriesBody { get; init; }
}

/// <summary>provider HTTP 请求失败异常（结构化携带 status/headers/body）。对应 TS 侧各 SDK 错误的字段形状。</summary>
public class ProviderHttpException : Exception
{
    public ProviderHttpException(int status, string body, string? message = null,
        IReadOnlyDictionary<string, string>? headers = null, Exception? inner = null)
        : base(message ?? $"provider returned {status}", inner)
    {
        Status = status;
        Headers = headers;
        Body = body;
    }

    public int Status { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }

    public string Body { get; }
}

public static class ProviderError
{
    /// <summary>错误 body 的最大字符数。对应 TS <c>MAX_PROVIDER_ERROR_BODY_CHARS</c>。</summary>
    public const int MaxProviderErrorBodyChars = 4000;

    /// <summary>规范化异常：提取 status 与 body。对应 TS <c>normalizeProviderError</c>。</summary>
    public static NormalizedProviderError Normalize(Exception error)
    {
        var status = ExtractStatus(error);
        var body = ExtractBody(error);
        var messageCarriesBody = body is null || error.Message.Contains(body, StringComparison.Ordinal);

        return new NormalizedProviderError
        {
            Status = status,
            Body = body,
            Message = error.Message,
            MessageCarriesBody = messageCarriesBody,
        };
    }

    /// <summary>
    /// 探测 HTTP 状态：ProviderHttpException.Status → HttpRequestException.StatusCode。
    /// 对应 TS 的 statusCode(Mistral) → status(openai/genai) → $metadata(Bedrock) 探测序。
    /// </summary>
    private static int? ExtractStatus(Exception error)
        => error switch
        {
            ProviderHttpException http => http.Status,
            HttpRequestException { StatusCode: { } statusCode } => (int)statusCode,
            _ => null,
        };

    /// <summary>探测原始 body：ProviderHttpException.Body，截断到上限。</summary>
    private static string? ExtractBody(Exception error)
    {
        var bodyText = error switch
        {
            ProviderHttpException http => http.Body,
            _ => null,
        };
        if (bodyText is null) return null;
        var trimmed = bodyText.Trim();
        if (trimmed.Length == 0) return null;
        return TruncateErrorText(trimmed, MaxProviderErrorBodyChars);
    }

    /// <summary>
    /// 从规范化错误合成展示串。message 已含 body 或无 body/status 时原样返回
    /// （带前缀且带 status 时附加前缀）；否则给出 status 与 body。
    /// </summary>
    public static string Format(NormalizedProviderError norm, string? prefix = null)
    {
        if (norm.MessageCarriesBody || norm.Status is null || norm.Body is null)
        {
            return prefix is not null && norm.Status is not null
                ? $"{prefix} ({norm.Status}): {norm.Message}"
                : norm.Message;
        }
        return prefix is not null
            ? $"{prefix} ({norm.Status}): {norm.Body}"
            : $"{norm.Status}: {norm.Body}";
    }

    public static string TruncateErrorText(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";
    }

    /// <summary>安全 JSON 序列化（失败退回 ToString）。对应 TS <c>safeJsonStringify</c>。</summary>
    public static string SafeJsonStringify(object? value)
    {
        try
        {
            return value switch
            {
                null => "null",
                string text => text,
                JsonNode node => node.ToJsonString(),
                _ => System.Text.Json.JsonSerializer.Serialize(value),
            };
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }
}
