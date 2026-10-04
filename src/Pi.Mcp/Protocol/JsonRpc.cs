using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Mcp.Protocol;

/// <summary>标准 JSON-RPC 2.0 错误码（MCP 使用子集）。对应 TS <c>JSON_RPC_ERROR_CODES</c>。</summary>
public static class JsonRpcErrorCodes
{
    public const long ParseError = -32700;
    public const long InvalidRequest = -32600;
    public const long MethodNotFound = -32601;
    public const long InvalidParams = -32602;
    public const long InternalError = -32603;
}

/// <summary>JSON-RPC 请求标识：字符串或有限数字。对应 TS <c>JsonRpcId = string | number</c>。</summary>
public readonly record struct JsonRpcId(object? Value)
{
    public static JsonRpcId FromString(string text) => new(text);
    public static JsonRpcId FromNumber(long number) => new(number);

    /// <summary>序列化为 JSON 节点。</summary>
    public JsonNode ToJson() => Value switch
    {
        string text => JsonValue.Create(text),
        long or int => JsonValue.Create(Convert.ToInt64(Value)),
        double d => JsonValue.Create(d),
        _ => throw new InvalidOperationException("Invalid JSON-RPC id"),
    };

    public override string ToString() => Value?.ToString() ?? "null";

    /// <summary>从 JSON 节点解析 id（string 或有限 number）。</summary>
    public static bool TryParse(JsonNode? node, out JsonRpcId id)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                id = FromString(text);
                return true;
            case JsonValue v when v.TryGetValue<long>(out var number):
                id = FromNumber(number);
                return true;
            default:
                id = default;
                return false;
        }
    }
}

/// <summary>JSON-RPC 消息判别基类。对应 TS <c>JsonRpcMessage</c> 联合。</summary>
public abstract record JsonRpcMessage
{
    private JsonRpcMessage() { }

    /// <summary>请求（携带 id，期望响应）。</summary>
    public sealed record Request(JsonRpcId Id, string Method, JsonNode? Params) : JsonRpcMessage;

    /// <summary>通知（无 id，不期望响应）。</summary>
    public sealed record Notification(string Method, JsonNode? Params) : JsonRpcMessage;

    /// <summary>成功响应。</summary>
    public sealed record SuccessResponse(JsonRpcId Id, JsonNode? Result) : JsonRpcMessage;

    /// <summary>错误响应。</summary>
    public sealed record ErrorResponse(JsonRpcId Id, JsonRpcErrorObject Error) : JsonRpcMessage;
}

/// <summary>JSON-RPC 错误对象。对应 TS <c>JsonRpcErrorObject</c>。</summary>
public sealed record JsonRpcErrorObject(long Code, string Message, JsonNode? Data = null);

/// <summary>MCP 基础异常。对应 TS <c>McpError</c>（携带 JSON-RPC 错误码）。</summary>
public class McpError(long code, string message, JsonNode? data = null) : Exception(message)
{
    public long Code { get; } = code;

    /// <summary>错误附加数据（JSON-RPC error.data）。</summary>
    public JsonNode? ErrorData { get; } = data;
}

/// <summary>连接关闭异常。对应 TS <c>McpConnectionClosedError</c>。</summary>
public sealed class McpConnectionClosedError(string message = "MCP connection closed") : Exception(message);

/// <summary>超时异常。对应 TS <c>McpTimeoutError</c>。</summary>
public sealed class McpTimeoutError(long timeoutMs)
    : Exception($"MCP request timed out after {timeoutMs}ms")
{
    public long TimeoutMs { get; } = timeoutMs;
}

/// <summary>中止异常。对应 TS <c>McpAbortError</c>。</summary>
public sealed class McpAbortError(string message = "MCP request aborted") : Exception(message);

/// <summary>
/// JSON-RPC 消息解析与判别。对应 TS <c>jsonrpc.ts</c> 的 isXxx / parseJsonRpcMessage。
/// </summary>
public static class JsonRpc
{
    /// <summary>是否为 JSON 对象（非数组）。</summary>
    public static bool IsObject(JsonNode? node) => node is JsonObject;

    /// <summary>是否为合法请求。</summary>
    public static bool IsRequest(JsonNode? node)
        => node is JsonObject obj
            && obj.TryGetPropertyValue("jsonrpc", out var version) && version is JsonValue { } v1
            && v1.TryGetValue<string>(out var v) && v == "2.0"
            && obj.TryGetPropertyValue("id", out var idNode)
            && JsonRpcId.TryParse(idNode, out _)
            && obj.TryGetPropertyValue("method", out var method) && method is JsonValue;

    /// <summary>是否为合法通知（无 id 字段）。</summary>
    public static bool IsNotification(JsonNode? node)
        => node is JsonObject obj
            && obj.TryGetPropertyValue("jsonrpc", out var version) && version is JsonValue { } v1
            && v1.TryGetValue<string>(out var v) && v == "2.0"
            && !obj.ContainsKey("id")
            && obj.TryGetPropertyValue("method", out var method) && method is JsonValue;

    /// <summary>是否为合法响应（result 与 error 互斥）。</summary>
    public static bool IsResponse(JsonNode? node)
    {
        if (node is not JsonObject obj
            || !obj.TryGetPropertyValue("jsonrpc", out var version) || version is not JsonValue { } v1
            || !v1.TryGetValue<string>(out var v) || v != "2.0"
            || !obj.TryGetPropertyValue("id", out var idNode)
            || !JsonRpcId.TryParse(idNode, out _))
            return false;
        if (obj.ContainsKey("result")) return !obj.ContainsKey("error");
        if (obj.TryGetPropertyValue("error", out var error) && error is JsonObject errObj)
            return errObj.TryGetPropertyValue("code", out var code) && code is JsonValue
                && errObj.TryGetPropertyValue("message", out var message) && message is JsonValue;
        return false;
    }

    /// <summary>解析并判别一条 JSON-RPC 消息；不合法时抛出 <see cref="McpError"/>（invalidRequest）。</summary>
    public static JsonRpcMessage Parse(JsonNode? node)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue("jsonrpc", out var version)
            && version is JsonValue { } versionValue
            && versionValue.TryGetValue<string>(out var versionText) && versionText == "2.0")
        {
            JsonRpcId id = default;
            var hasId = obj.TryGetPropertyValue("id", out var idNode)
                && idNode is not null
                && JsonRpcId.TryParse(idNode, out id);
            var method = obj.TryGetPropertyValue("method", out var methodNode) && methodNode is JsonValue { } mv
                ? mv.TryGetValue<string>(out var m) ? m : null
                : null;
            var hasParams = obj.TryGetPropertyValue("params", out var paramsNode);

            // 请求：有 id + method。
            if (hasId && method is not null)
                return new JsonRpcMessage.Request(id, method, hasParams ? paramsNode : null);

            // 通知：无 id + method。
            if (!obj.ContainsKey("id") && method is not null)
                return new JsonRpcMessage.Notification(method, hasParams ? paramsNode : null);

            // 响应：有 id + result 或 error（两者互斥，并存视为非法）。
            if (hasId)
            {
                var hasResult = obj.ContainsKey("result");
                var hasError = obj.ContainsKey("error");
                if (hasResult && !hasError)
                    return new JsonRpcMessage.SuccessResponse(id, obj["result"]);
                if (hasError && !hasResult
                    && obj.TryGetPropertyValue("error", out var errorNode) && errorNode is JsonObject errObj
                    && errObj.TryGetPropertyValue("code", out var codeNode) && codeNode is JsonValue { } codeValue
                    && codeValue.TryGetValue<long>(out var code)
                    && errObj.TryGetPropertyValue("message", out var messageNode) && messageNode is JsonValue { } msgValue
                    && msgValue.TryGetValue<string>(out var message))
                    return new JsonRpcMessage.ErrorResponse(
                        id, new JsonRpcErrorObject(code, message, errObj.ContainsKey("data") ? errObj["data"] : null));
            }
        }

        throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid JSON-RPC message");
    }

    // ---------- 序列化 ----------

    /// <summary>序列化请求。</summary>
    public static JsonObject Serialize(JsonRpcMessage.Request request)
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id.ToJson(), ["method"] = request.Method };
        if (request.Params is not null) obj["params"] = request.Params.DeepClone();
        return obj;
    }

    /// <summary>序列化通知。</summary>
    public static JsonObject Serialize(JsonRpcMessage.Notification notification)
    {
        var obj = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = notification.Method };
        if (notification.Params is not null) obj["params"] = notification.Params.DeepClone();
        return obj;
    }

    /// <summary>序列化成功响应。</summary>
    public static JsonObject Serialize(JsonRpcMessage.SuccessResponse response)
        => new() { ["jsonrpc"] = "2.0", ["id"] = response.Id.ToJson(), ["result"] = response.Result?.DeepClone() };

    /// <summary>序列化错误响应。</summary>
    public static JsonObject Serialize(JsonRpcMessage.ErrorResponse response)
    {
        var error = new JsonObject
        {
            ["code"] = response.Error.Code,
            ["message"] = response.Error.Message,
        };
        if (response.Error.Data is not null) error["data"] = response.Error.Data.DeepClone();
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = response.Id.ToJson(), ["error"] = error };
    }

    /// <summary>按消息类型分发序列化。</summary>
    public static JsonObject Serialize(JsonRpcMessage message) => message switch
    {
        JsonRpcMessage.Request request => Serialize(request),
        JsonRpcMessage.Notification notification => Serialize(notification),
        JsonRpcMessage.SuccessResponse success => Serialize(success),
        JsonRpcMessage.ErrorResponse error => Serialize(error),
        _ => throw new ArgumentException("Unknown JSON-RPC message", nameof(message)),
    };

    /// <summary>从 JSON 文本解析一条消息（解码失败抛 parseError）。</summary>
    public static JsonRpcMessage ParseText(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException error)
        {
            throw new McpError(JsonRpcErrorCodes.ParseError, $"JSON parse error: {error.Message}");
        }
        return Parse(node);
    }
}
